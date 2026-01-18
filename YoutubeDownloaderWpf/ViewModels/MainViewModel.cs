using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using YoutubeDownloaderWpf.Models;
using YoutubeDownloaderWpf.Services;

namespace YoutubeDownloaderWpf.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly IYoutubeService _youtubeService;
        private readonly SemaphoreSlim _concurrencyLimiter;

        [ObservableProperty] private string inputUrl;
        [ObservableProperty] private string outputFolderPath;

        // BIẾN QUAN TRỌNG: Lưu đường dẫn file cookies.txt
        [ObservableProperty] private string cookieFilePath;

        [ObservableProperty] private bool isSubVi = true;
        [ObservableProperty] private bool isSubEn = true;
        [ObservableProperty] private string logMessage;
        [ObservableProperty] private double totalProgressValue;
        [ObservableProperty] private string totalStatusInfo;

        // Tool status
        [ObservableProperty] private bool areToolsMissing;
        [ObservableProperty] private string toolStatusMessage;
        [ObservableProperty] private double toolDownloadProgress;
        [ObservableProperty] private bool isBusyWithTools;

        public ObservableCollection<DownloadItem> Downloads { get; } = new ObservableCollection<DownloadItem>();

        public MainViewModel()
        {
        }
        public MainViewModel(IYoutubeService youtubeService)
        {
            _youtubeService = youtubeService;
            _concurrencyLimiter = new SemaphoreSlim(4);
            OutputFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads");
            if (!Directory.Exists(OutputFolderPath)) Directory.CreateDirectory(OutputFolderPath);
            _ = InitializeApp();
        }

        private async Task InitializeApp()
        {
            await _youtubeService.InitializeAsync();
            CheckToolsStatus();
        }

        private void CheckToolsStatus()
        {
            bool ytdlOk = _youtubeService.IsYtDlpReady;
            bool ffmpegOk = _youtubeService.IsFfmpegReady;
            AreToolsMissing = !ytdlOk || !ffmpegOk;
            if (AreToolsMissing)
            {
                ToolStatusMessage = "Thiếu công cụ: " + (!ytdlOk ? "[yt-dlp] " : "") + (!ffmpegOk ? "[ffmpeg] " : "");
                LogMessage = "Cần tải công cụ trước.";
            }
            else
            {
                ToolStatusMessage = "Công cụ sẵn sàng.";
                LogMessage = "Sẵn sàng tải.";
            }
        }

        // --- LỆNH CHỌN FILE COOKIES ---
        [RelayCommand]
        private void SelectCookieFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                Title = "Chọn file Cookies.txt (Xuất từ Extension)"
            };

            if (dialog.ShowDialog() == true)
            {
                CookieFilePath = dialog.FileName;
                LogMessage = "Đã chọn cookies: " + Path.GetFileName(CookieFilePath);
            }
        }

        // --- LỆNH XÓA FILE COOKIES ---
        [RelayCommand]
        private void ClearCookies()
        {
            CookieFilePath = string.Empty;
            LogMessage = "Đã xóa cookies. Tải chế độ khách.";
        }

        [RelayCommand]
        private void ChangeOutputFolder()
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Chọn thư mục lưu", InitialDirectory = OutputFolderPath };
            if (dialog.ShowDialog() == true) OutputFolderPath = dialog.FolderName;
        }

        [RelayCommand]
        private void OpenOutputFolder()
        {
            if (!Directory.Exists(OutputFolderPath)) Directory.CreateDirectory(OutputFolderPath);
            System.Diagnostics.Process.Start("explorer.exe", OutputFolderPath);
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
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
            finally { IsBusyWithTools = false; }
        }

        [RelayCommand]
        private async Task UpdateTools()
        {
            try { await _youtubeService.UpdateToolsAsync(); LogMessage = "Đã cập nhật yt-dlp."; } catch (Exception ex) { LogMessage = ex.Message; }
        }

        [RelayCommand]
        private async Task AddDownload()
        {
            if (AreToolsMissing) { MessageBox.Show("Thiếu công cụ!"); return; }
            if (string.IsNullOrWhiteSpace(InputUrl)) return;
            string url = InputUrl; InputUrl = "";

            if (url.Contains("playlist?list="))
            {
                LogMessage = "Đang lấy playlist...";
                await Task.Run(async () => {
                    var result = await _youtubeService.GetPlaylistUrlsAsync(url);
                    Application.Current.Dispatcher.Invoke(() => {
                        if (result.Success) { foreach (var u in result.Data) QueueVideo(u); LogMessage = $"Thêm {result.Data.Length} video."; }
                        else LogMessage = result.ErrorOutput;
                        UpdateTotalStatus();
                    });
                });
            }
            else
            {
                QueueVideo(url);
                UpdateTotalStatus();
            }
        }

        private void QueueVideo(string url)
        {
            var item = new DownloadItem { Url = url, Title = "Waiting...", Status = "Pending" };
            Downloads.Add(item);
            _ = ProcessDownloadQueue(item);
        }

        private async Task ProcessDownloadQueue(DownloadItem item)
        {
            await _concurrencyLimiter.WaitAsync(item.Cts.Token);
            try
            {
                if (item.Cts.IsCancellationRequested) return;
                Application.Current.Dispatcher.Invoke(() => { item.Status = "Starting..."; item.IsDownloading = true; });

                var p = new Progress<SimpleProgress>(val => Application.Current.Dispatcher.Invoke(() => {
                    item.Progress = val.Progress * 100; item.Speed = val.DownloadSpeed; item.Status = $"Downloading {item.Progress:0}%";
                }));

                // TRUYỀN FILE COOKIES VÀO ĐÂY
                var result = await _youtubeService.DownloadVideoAsync(item.Url, OutputFolderPath, GetSubLanguages(), CookieFilePath, p, item.Cts.Token);

                Application.Current.Dispatcher.Invoke(() => {
                    if (result.Success)
                    {
                        item.Status = "Completed"; item.Progress = 100;
                        if (!string.IsNullOrEmpty(result.Data)) item.Title = Path.GetFileName(result.Data);
                    }
                    else
                    {
                        item.Status = item.Cts.IsCancellationRequested ? "Cancelled" : "Error";
                        if (item.Title == "Waiting...") item.Title = item.Url;
                        LogMessage = result.ErrorOutput;
                    }
                });
            }
            catch (Exception ex) { Application.Current.Dispatcher.Invoke(() => { item.Status = "Error"; LogMessage = ex.Message; }); }
            finally
            {
                Application.Current.Dispatcher.Invoke(() => item.IsDownloading = false);
                _concurrencyLimiter.Release();
                UpdateTotalStatus();
            }
        }

        private string GetSubLanguages()
        {
            var l = new System.Collections.Generic.List<string>();
            if (IsSubVi) l.Add("vi"); if (IsSubEn) l.Add("en");
            return l.Count > 0 ? string.Join(",", l) : null;
        }

        private void UpdateTotalStatus()
        {
            Application.Current.Dispatcher.Invoke(() => {
                var total = Downloads.Count;
                if (total == 0) { TotalProgressValue = 0; TotalStatusInfo = "Sẵn sàng"; return; }
                var done = Downloads.Count(x => x.Status == "Completed" || x.Status == "Error" || x.Status == "Cancelled");
                TotalProgressValue = (double)done / total * 100;
                TotalStatusInfo = $"Tiến độ: {done}/{total}";
            });
        }

        [RelayCommand] private void CancelItem(DownloadItem item) => item?.Cts.Cancel();
        [RelayCommand] private void CancelAll() { foreach (var i in Downloads) i.Cts.Cancel(); }
    }
}