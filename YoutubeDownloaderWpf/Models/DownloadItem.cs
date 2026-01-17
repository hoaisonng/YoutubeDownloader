using CommunityToolkit.Mvvm.ComponentModel;

namespace YoutubeDownloaderWpf.Models
{
    // Sử dụng ObservableObject để UI tự cập nhật khi dữ liệu đổi
    public partial class DownloadItem : ObservableObject
    {
        [ObservableProperty] private string title;
        [ObservableProperty] private string url;
        [ObservableProperty] private double progress; // 0 đến 100
        [ObservableProperty] private string status;   // "Pending", "Downloading", "Completed", "Error"
        [ObservableProperty] private string speed;
        // Thêm thuộc tính này để điều khiển hiển thị nút Stop
        [ObservableProperty] private bool isDownloading;
        public CancellationTokenSource Cts { get; set; } = new CancellationTokenSource();
    }
}