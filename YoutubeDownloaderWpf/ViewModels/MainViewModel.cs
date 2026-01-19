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
        [ObservableProperty] private bool isSubVi = false;
        [ObservableProperty] private bool isSubEn = false;
        [ObservableProperty] private bool isAudioOnly;

        // MỚI: Biến bật tắt chế độ tự động dịch
        [ObservableProperty] private bool isAutoTranslate = false;

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
            _concurrencyLimiter = new SemaphoreSlim(3);
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

        private async Task InitializeApp() { await _youtubeService.InitializeAsync(); CheckToolsStatus(); }

        private void CheckToolsStatus()
        {
            bool ytdlOk = _youtubeService.IsYtDlpReady;
            bool ffmpegOk = _youtubeService.IsFfmpegReady;
            bool denoOk = _youtubeService.IsDenoReady;
            AreToolsMissing = !ytdlOk || !ffmpegOk || !denoOk;
            if (AreToolsMissing)
            {
                ToolStatusMessage = "Thiếu công cụ: " + (!ytdlOk ? "[yt-dlp] " : "") + (!ffmpegOk ? "[ffmpeg] " : "") + (!denoOk ? "[deno] " : "");
                LogMessage = "Cần tải đầy đủ công cụ.";
            }
            else { ToolStatusMessage = "Công cụ đã sẵn sàng."; LogMessage = "Sẵn sàng tải video."; }
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
            if (loginWin.ShowDialog() == true) { CookieFilePath = loginWin.SavedCookiePath; LogMessage = "Đã cập nhật Cookies!"; }
        }

        [RelayCommand]
        private void OpenPlaylist()
        {
            var playlistWin = new PlaylistWindow(_youtubeService);
            if (playlistWin.ShowDialog() == true)
            {
                foreach (var url in playlistWin.SelectedUrls) QueueVideo(url);
                LogMessage = $"Đã thêm {playlistWin.SelectedUrls.Count} video.";
            }
        }

        [RelayCommand]
        private async Task DownloadMissingTools()
        {
            IsBusyWithTools = true;
            try
            {
                var progress = new Progress<double>(p => ToolDownloadProgress = p);
                if (!_youtubeService.IsYtDlpReady) { LogMessage = "Tải yt-dlp..."; await _youtubeService.DownloadYtDlpAsync(progress); }
                if (!_youtubeService.IsDenoReady) { LogMessage = "Tải Deno..."; ToolDownloadProgress = 0; await _youtubeService.DownloadDenoAsync(progress); }
                if (!_youtubeService.IsFfmpegReady) { LogMessage = "Tải ffmpeg..."; ToolDownloadProgress = 0; await _youtubeService.DownloadFfmpegAsync(progress); }
                CheckToolsStatus(); LogMessage = "Tải công cụ thành công!";
            }
            catch (Exception ex) { LogMessage = $"Lỗi: {ex.Message}"; MessageBox.Show(ex.Message); }
            finally { IsBusyWithTools = false; ToolDownloadProgress = 0; }
        }

        [RelayCommand]
        private async Task UpdateTools()
        {
            IsBusyWithTools = true;
            LogMessage = "Đang cập nhật yt-dlp...";
            try { await _youtubeService.UpdateToolsAsync(); LogMessage = "Cập nhật xong."; MessageBox.Show("Đã cập nhật yt-dlp!"); }
            catch (Exception ex) { LogMessage = $"Lỗi update: {ex.Message}"; }
            finally { IsBusyWithTools = false; }
        }

        [RelayCommand] private void ClearCookies() { CookieFilePath = ""; LogMessage = "Đã xóa Cookies."; }

        [RelayCommand]
        private async Task AddDownload()
        {
            if (AreToolsMissing) { MessageBox.Show("Vui lòng tải tool trước."); return; }
            if (string.IsNullOrWhiteSpace(InputUrl)) return;
            string urlToAdd = InputUrl; InputUrl = "";
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
            else { QueueVideo(urlToAdd); UpdateTotalStatus(); }
        }

        // --- SỬA LOGIC: Lấy thông tin -> Dịch (nếu cần) -> Tải ---
        private async void QueueVideo(string url)
        {
            var item = new DownloadItem { Url = url, Title = "Đang lấy thông tin...", Status = "Checking..." };
            Downloads.Insert(0, item);

            await Task.Run(async () =>
            {
                var meta = await _youtubeService.GetVideoMetadataAsync(url, CookieFilePath);

                // Logic Dịch thuật
                if (meta.Success && meta.Data != null)
                {
                    string finalTitle = meta.Data.Title;

                    if (IsAutoTranslate)
                    {
                        // Gọi hàm dịch sang tiếng Anh
                        Application.Current.Dispatcher.Invoke(() => item.Status = "Translating...");
                        string translated = await _youtubeService.TranslateToEnglishAsync(finalTitle);
                        if (!string.IsNullOrEmpty(translated)) finalTitle = translated;
                    }

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        item.Title = finalTitle; // Cập nhật tên mới đã dịch
                        item.ThumbnailUrl = meta.Data.ThumbnailUrl;
                        item.Duration = meta.Data.Duration;
                        item.Status = "Pending";
                        _ = ProcessDownloadQueue(item);
                    });
                }
                else
                {
                    Application.Current.Dispatcher.Invoke(() => { item.Status = "Error"; item.Title = "Lỗi info"; LogMessage = meta.ErrorOutput; });
                }
            });
        }

        private async Task ProcessDownloadQueue(DownloadItem item)
        {
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
                        item.Speed = p.DownloadSpeed; // Đây là chỗ gán chữ "Downloading..."
                        item.Status = $"{(p.Progress * 100):0}%";
                    });
                });

                string nameForDownload = IsAutoTranslate ? item.Title : null;

                var result = await _youtubeService.DownloadVideoAsync(
                    item.Url,
                    OutputFolderPath,
                    nameForDownload,
                    GetSubLanguages(),
                    CookieFilePath,
                    IsAudioOnly,
                    progressIndicator,
                    item.Cts.Token);

                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (result.Success)
                    {
                        item.Status = "Completed";
                        item.Progress = 100;

                        // [FIX] Xóa chữ "Downloading..." ở dòng Speed đi
                        item.Speed = "";
                        // Hoặc bạn có thể để: item.Speed = "File saved";
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