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
        private readonly SemaphoreSlim _concurrencyLimiter; // Giới hạn số lượng tải cùng lúc

        [ObservableProperty] private string inputUrl;
        [ObservableProperty] private string cookieFilePath;
        //[ObservableProperty] private bool isDownloadingSubtitles = true;
        // Thay thế biến isDownloadingSubtitles cũ bằng 2 biến này
        [ObservableProperty] private bool isSubVi = true;
        [ObservableProperty] private bool isSubEn = true;
        [ObservableProperty] private string logMessage;
        // Trạng thái Tools
        [ObservableProperty] private bool areToolsMissing;
        [ObservableProperty] private string toolStatusMessage;
        [ObservableProperty] private double toolDownloadProgress;
        [ObservableProperty] private bool isBusyWithTools;

        // --- CÁC BIẾN MỚI CHO TIẾN ĐỘ TỔNG ---
        [ObservableProperty] private double totalProgressValue;
        [ObservableProperty] private string totalStatusInfo;
        // THÊM: Biến lưu thư mục đầu ra
        [ObservableProperty] private string outputFolderPath;
        // Danh sách hiển thị lên UI
        public ObservableCollection<DownloadItem> Downloads { get; } = new ObservableCollection<DownloadItem>();

        public MainViewModel(IYoutubeService youtubeService)
        {
            _youtubeService = youtubeService;
            _concurrencyLimiter = new SemaphoreSlim(4);

            // Mặc định lưu vào folder Downloads cạnh file exe
            OutputFolderPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads");
            if (!Directory.Exists(OutputFolderPath)) Directory.CreateDirectory(OutputFolderPath);

            _ = InitializeApp();
        }
        // Thêm hàm lấy chuỗi subLang
        private string GetSubLanguages()
        {
            var langs = new System.Collections.Generic.List<string>();
            if (IsSubVi) langs.Add("vi");
            if (IsSubEn) langs.Add("en");

            if (langs.Count == 0) return null;
            return string.Join(",", langs);
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
                ToolStatusMessage = "Thiếu công cụ hỗ trợ: " +
                                    (!ytdlOk ? "[yt-dlp] " : "") +
                                    (!ffmpegOk ? "[ffmpeg] " : "");
                LogMessage = "Vui lòng tải tool hoặc chọn file có sẵn.";
            }
            else
            {
                ToolStatusMessage = "Công cụ đã sẵn sàng.";
                LogMessage = "Sẵn sàng tải video.";
            }
        }
        // THÊM: Lệnh thay đổi thư mục lưu
        [RelayCommand]
        private void ChangeOutputFolder()
        {
            // Trong .NET 6/8 WPF, dùng OpenFolderDialog là chuẩn nhất
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Chọn thư mục lưu video",
                InitialDirectory = OutputFolderPath
            };

            if (dialog.ShowDialog() == true)
            {
                OutputFolderPath = dialog.FolderName;
                LogMessage = $"Đã đổi thư mục lưu sang: {OutputFolderPath}";
            }
        }
        // Lệnh mở thư mục chứa file tải về
        [RelayCommand]
        private void OpenOutputFolder()
        {
            if (!Directory.Exists(OutputFolderPath)) Directory.CreateDirectory(OutputFolderPath);
            System.Diagnostics.Process.Start("explorer.exe", OutputFolderPath);
        }

        // Hàm tính toán lại tiến độ tổng
        private void UpdateTotalStatus()
        {
            // Chạy trên UI Thread để tránh lỗi cập nhật giao diện
            Application.Current.Dispatcher.Invoke(() =>
            {
                var total = Downloads.Count;
                if (total == 0)
                {
                    TotalProgressValue = 0;
                    TotalStatusInfo = "Sẵn sàng";
                    return;
                }

                // Đếm số lượng đã xong (Completed, Error hoặc Cancelled đều tính là xong lượt)
                var done = Downloads.Count(x => x.Status == "Completed" || x.Status == "Error" || x.Status == "Cancelled");

                TotalProgressValue = (double)done / total * 100;
                TotalStatusInfo = $"Tổng tiến độ: {done}/{total} video";
            });
        }

        // Command Mở cửa sổ Đăng nhập
        [RelayCommand]
        private void OpenLogin()
        {
            var loginWin = new LoginWindow();
            if (loginWin.ShowDialog() == true)
            {
                CookieFilePath = loginWin.SavedCookiePath;
                LogMessage = "Đã cập nhật Cookies từ phiên đăng nhập.";
            }
        }

        // Command Mở cửa sổ Playlist
        [RelayCommand]
        private void OpenPlaylist()
        {
            var playlistWin = new PlaylistWindow(_youtubeService);
            if (playlistWin.ShowDialog() == true)
            {
                foreach (var url in playlistWin.SelectedUrls)
                {
                    QueueVideo(url);
                }
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

                if (!_youtubeService.IsYtDlpReady)
                {
                    LogMessage = "Đang tải yt-dlp...";
                    await _youtubeService.DownloadYtDlpAsync(progress);
                }

                if (!_youtubeService.IsFfmpegReady)
                {
                    LogMessage = "Đang tải ffmpeg (khá nặng)...";
                    ToolDownloadProgress = 0;
                    await _youtubeService.DownloadFfmpegAsync(progress);
                }

                CheckToolsStatus();
                LogMessage = "Tải công cụ thành công!";
            }
            catch (Exception ex)
            {
                LogMessage = $"Lỗi tải tool: {ex.Message}";
                MessageBox.Show(ex.Message, "Lỗi Tải Tool");
            }
            finally
            {
                IsBusyWithTools = false;
                ToolDownloadProgress = 0;
            }
        }

        [RelayCommand]
        private void LocateYtDlp()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Executable|yt-dlp.exe|All Files|*.*" };
            if (dialog.ShowDialog() == true)
            {
                _youtubeService.YtDlpPath = dialog.FileName;
                CheckToolsStatus();
            }
        }

        [RelayCommand]
        private void LocateFfmpeg()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Executable|ffmpeg.exe|All Files|*.*" };
            if (dialog.ShowDialog() == true)
            {
                _youtubeService.FfmpegPath = dialog.FileName;
                CheckToolsStatus();
            }
        }
        //[RelayCommand]
        //private void SelectCookieFile()
        //{
        //    var dialog = new Microsoft.Win32.OpenFileDialog();
        //    if (dialog.ShowDialog() == true) CookieFilePath = dialog.FileName;
        //}
        [RelayCommand]
        private void SelectCookieFile()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                Title = "Chọn file Cookies (Netscape format)"
            };

            if (dialog.ShowDialog() == true)
            {
                CookieFilePath = dialog.FileName;
                LogMessage = "Đã chọn file Cookies: " + System.IO.Path.GetFileName(CookieFilePath);
            }
        }

        // Lệnh 2: Xóa/Bỏ chọn Cookies
        [RelayCommand]
        private void ClearCookies()
        {
            if (string.IsNullOrEmpty(CookieFilePath)) return;

            CookieFilePath = string.Empty;
            LogMessage = "Đã xóa lựa chọn Cookies. Tải video sẽ ở chế độ Khách (Guest).";
        }
        [RelayCommand]
        private async Task UpdateTools()
        {
            LogMessage = "Đang cập nhật yt-dlp...";
            try { await _youtubeService.UpdateToolsAsync(); LogMessage = "Cập nhật xong."; }
            catch (Exception ex) { LogMessage = $"Lỗi update: {ex.Message}"; }
        }

        // Sửa hàm AddDownload
        [RelayCommand]
        private async Task AddDownload()
        {
            if (AreToolsMissing)
            {
                MessageBox.Show("Vui lòng tải hoặc chọn công cụ (yt-dlp/ffmpeg) trước.", "Thiếu Tool");
                return;
            }
            if (string.IsNullOrWhiteSpace(InputUrl)) return;

            string urlToAdd = InputUrl;
            InputUrl = "";

            if (urlToAdd.Contains("playlist?list="))
            {
                LogMessage = "Đang lấy danh sách playlist (vui lòng đợi)...";
                // Chạy task lấy playlist ở background để không đơ UI lúc chờ
                await Task.Run(async () =>
                {
                    var result = await _youtubeService.GetPlaylistUrlsAsync(urlToAdd);

                    // Cập nhật UI phải qua Dispatcher
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (result.Success)
                        {
                            foreach (var u in result.Data) QueueVideo(u);
                            LogMessage = $"Đã thêm {result.Data.Length} video.";
                        }
                        else
                        {
                            LogMessage = result.ErrorOutput;
                        }
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

        private void QueueVideo(string url)
        {
            var item = new DownloadItem { Url = url, Title = "Đang chờ...", Status = "Pending" };
            Downloads.Add(item);
            _ = ProcessDownloadQueue(item);
        }

        // Sửa hàm ProcessDownloadQueue
        private async Task ProcessDownloadQueue(DownloadItem item)
        {
            await _concurrencyLimiter.WaitAsync(item.Cts.Token);

            try
            {
                if (item.Cts.IsCancellationRequested) return;

                // BẬT cờ đang tải -> Nút Stop hiện lên
                Application.Current.Dispatcher.Invoke(() =>
                {
                    item.Status = "Starting...";
                    item.IsDownloading = true;
                });

                var progressIndicator = new Progress<SimpleProgress>(p =>
                {
                    // Đảm bảo update UI mượt mà
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        item.Progress = p.Progress * 100;
                        item.Speed = p.DownloadSpeed;
                        item.Status = $"Downloading {item.Progress:0}%";
                    });
                });

                // TRUYỀN OutputFolderPath VÀO HÀM TẢI
                var result = await _youtubeService.DownloadVideoAsync(
                    item.Url,
                    OutputFolderPath, // <--- Đổi "Downloads" thành biến này
                    GetSubLanguages(),
                    CookieFilePath,
                    progressIndicator,
                    item.Cts.Token
                );

                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (result.Success)
                    {
                        item.Status = "Completed";
                        item.Progress = 100;
                        if (!string.IsNullOrEmpty(result.Data))
                            item.Title = Path.GetFileName(result.Data);
                    }
                    else
                    {
                        item.Status = item.Cts.IsCancellationRequested ? "Cancelled" : "Error";
                        LogMessage = "Lỗi: " + result.ErrorOutput;
                    }
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    item.Status = "Error";
                    LogMessage = ex.Message;
                });
            }
            finally
            {
                // TẮT cờ đang tải -> Nút Stop ẩn đi
                Application.Current.Dispatcher.Invoke(() => item.IsDownloading = false);

                _concurrencyLimiter.Release();
                UpdateTotalStatus();
            }
        }
        [RelayCommand]
        private void CancelItem(DownloadItem item)
        {
                item?.Cts.Cancel();
        }
        [RelayCommand]
        private void CancelAll()
        {
            foreach (var i in Downloads)
            {
                CancelItem(i);
            }
            UpdateTotalStatus();
        }        
    }
}