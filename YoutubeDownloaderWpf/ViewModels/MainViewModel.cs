using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using YoutubeDownloaderWpf.Models;
using YoutubeDownloaderWpf.Services;
using YoutubeDownloaderWpf.Views;

namespace YoutubeDownloaderWpf.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IYoutubeService _youtubeService;
        private readonly SemaphoreSlim _concurrencyLimiter;

        [ObservableProperty] private string inputUrl;
        [ObservableProperty] private string cookieFilePath;
        [ObservableProperty] private bool isSubVi = false; // Mặc định tắt
        [ObservableProperty] private bool isSubEn = false;

        // Mới: Chế độ chỉ tải Audio
        [ObservableProperty] private bool isAudioOnly;

        [ObservableProperty] private string logMessage;
        [ObservableProperty] private bool areToolsMissing;
        [ObservableProperty] private string toolStatusMessage;
        [ObservableProperty] private double toolDownloadProgress;
        [ObservableProperty] private bool isBusyWithTools;
        [ObservableProperty] private double totalProgressValue;
        [ObservableProperty] private string totalStatusInfo;
        [ObservableProperty] private string outputFolderPath;

        public ObservableCollection<DownloadItem> Downloads { get; } = new ObservableCollection<DownloadItem>();

        public MainViewModel(IYoutubeService youtubeService)
        {
            _youtubeService = youtubeService;
            _concurrencyLimiter = new SemaphoreSlim(3); // Tải tối đa 3 video cùng lúc

            OutputFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads");
            if (!Directory.Exists(OutputFolderPath)) Directory.CreateDirectory(OutputFolderPath);

            _ = InitializeApp();
        }

        private string GetSubLanguages()
        {
            var langs = new System.Collections.Generic.List<string>();
            if (IsSubVi) langs.Add("vi");
            if (IsSubEn) langs.Add("en");
            return langs.Count == 0 ? null : string.Join(",", langs);
        }

        private async Task InitializeApp()
        {
            await _youtubeService.InitializeAsync();
            CheckToolsStatus();
        }

        private void CheckToolsStatus()
        {
            AreToolsMissing = !_youtubeService.IsYtDlpReady || !_youtubeService.IsFfmpegReady;
            ToolStatusMessage = AreToolsMissing ? "Thiếu công cụ (yt-dlp/ffmpeg)" : "Công cụ đã sẵn sàng";
            LogMessage = AreToolsMissing ? "Cần tải công cụ trước." : "Sẵn sàng tải.";
        }

        [RelayCommand]
        private void ChangeOutputFolder()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = OutputFolderPath };
            if (dialog.ShowDialog() == true) OutputFolderPath = dialog.FolderName;
        }

        [RelayCommand]
        private void OpenOutputFolder()
        {
            if (!Directory.Exists(OutputFolderPath)) Directory.CreateDirectory(OutputFolderPath);
            System.Diagnostics.Process.Start("explorer.exe", OutputFolderPath);
        }

        private void UpdateTotalStatus()
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var total = Downloads.Count;
                if (total == 0) { TotalProgressValue = 0; TotalStatusInfo = "Chờ lệnh..."; return; }
                var done = Downloads.Count(x => x.Status == "Completed" || x.Status == "Error" || x.Status == "Cancelled");
                TotalProgressValue = (double)done / total * 100;
                TotalStatusInfo = $"Hoàn thành: {done}/{total}";
            });
        }

        [RelayCommand]
        private void OpenLogin()
        {
            var loginWin = new LoginWindow();
            if (loginWin.ShowDialog() == true)
            {
                CookieFilePath = loginWin.SavedCookiePath;
                LogMessage = "Đã cập nhật Cookies!";
            }
        }

        [RelayCommand]
        private void OpenPlaylist()
        {
            var playlistWin = new PlaylistWindow(_youtubeService);
            if (playlistWin.ShowDialog() == true)
            {
                foreach (var url in playlistWin.SelectedUrls) QueueVideo(url);
                LogMessage = $"Đã thêm {playlistWin.SelectedUrls.Count} video từ playlist.";
            }
        }

        [RelayCommand]
        private async Task DownloadMissingTools()
        {
            IsBusyWithTools = true;
            try
            {
                var progress = new Progress<double>(p => ToolDownloadProgress = p);
                if (!_youtubeService.IsYtDlpReady) await _youtubeService.DownloadYtDlpAsync(progress);
                if (!_youtubeService.IsFfmpegReady) await _youtubeService.DownloadFfmpegAsync(progress);
                CheckToolsStatus();
                LogMessage = "Tải công cụ thành công!";
            }
            catch (Exception ex) { LogMessage = $"Lỗi tải tool: {ex.Message}"; }
            finally { IsBusyWithTools = false; ToolDownloadProgress = 0; }
        }

        [RelayCommand] private void ClearCookies() { CookieFilePath = ""; LogMessage = "Đã xóa Cookies (Chế độ Guest)."; }

        [RelayCommand]
        private async Task AddDownload()
        {
            if (AreToolsMissing) { MessageBox.Show("Vui lòng tải tool trước."); return; }
            if (string.IsNullOrWhiteSpace(InputUrl)) return;

            string urlToAdd = InputUrl;
            InputUrl = ""; // Xóa ô nhập liệu ngay

            if (urlToAdd.Contains("playlist?list="))
            {
                LogMessage = "Đang quét playlist...";
                await Task.Run(async () =>
                {
                    var result = await _youtubeService.GetPlaylistUrlsAsync(urlToAdd);
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (result.Success) foreach (var u in result.Data) QueueVideo(u);
                        else LogMessage = "Lỗi Playlist: " + result.ErrorOutput;
                        UpdateTotalStatus();
                    });
                });
            }
            else
            {
                QueueVideo(urlToAdd);
                UpdateTotalStatus();
            }
        }

        // --- Logic chính: Thêm vào hàng đợi ---
        private async void QueueVideo(string url)
        {
            // Bước 1: Tạo item tạm
            var item = new DownloadItem { Url = url, Title = "Đang lấy thông tin...", Status = "Checking..." };
            Downloads.Insert(0, item); // Thêm vào đầu danh sách cho dễ thấy

            // Bước 2: Lấy metadata ở background
            await Task.Run(async () =>
            {
                var meta = await _youtubeService.GetVideoMetadataAsync(url, CookieFilePath);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (meta.Success && meta.Data != null)
                    {
                        item.Title = meta.Data.Title;
                        item.ThumbnailUrl = meta.Data.ThumbnailUrl;
                        item.Duration = meta.Data.Duration;
                        item.Status = "Pending";
                        // Bắt đầu tải sau khi có thông tin
                        _ = ProcessDownloadQueue(item);
                    }
                    else
                    {
                        item.Status = "Error";
                        item.Title = "Lỗi lấy thông tin";
                        LogMessage = meta.ErrorOutput;
                    }
                });
            });
        }

        private async Task ProcessDownloadQueue(DownloadItem item)
        {
            // Chờ đến lượt tải (Semaphore)
            await _concurrencyLimiter.WaitAsync(item.Cts.Token);

            try
            {
                if (item.Cts.IsCancellationRequested) return;

                Application.Current.Dispatcher.Invoke(() => { item.Status = "Starting..."; item.IsDownloading = true; });

                var progressIndicator = new Progress<SimpleProgress>(p =>
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        item.Progress = p.Progress * 100;
                        item.Speed = p.DownloadSpeed;
                        item.Status = $"{(p.Progress * 100):0}%";
                    });
                });

                // Gọi service tải thật
                var result = await _youtubeService.DownloadVideoAsync(
                    item.Url,
                    OutputFolderPath,
                    GetSubLanguages(),
                    CookieFilePath,
                    IsAudioOnly, // Truyền tham số MP3
                    progressIndicator,
                    item.Cts.Token);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (result.Success)
                    {
                        item.Status = "Completed";
                        item.Progress = 100;
                        if (!string.IsNullOrEmpty(result.Data)) item.Title = Path.GetFileName(result.Data);
                    }
                    else
                    {
                        item.Status = item.Cts.IsCancellationRequested ? "Cancelled" : "Error";
                        if (!item.Cts.IsCancellationRequested) LogMessage = "Lỗi: " + result.ErrorOutput;
                    }
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() => { item.Status = "Error"; LogMessage = ex.Message; });
            }
            finally
            {
                Application.Current.Dispatcher.Invoke(() => item.IsDownloading = false);
                _concurrencyLimiter.Release();
                UpdateTotalStatus();
            }
        }

        [RelayCommand] private void CancelItem(DownloadItem item) { item?.Cts.Cancel(); }
        [RelayCommand] private void CancelAll() { foreach (var i in Downloads) CancelItem(i); }
    }
}