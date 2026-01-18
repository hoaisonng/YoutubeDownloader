using CommunityToolkit.Mvvm.ComponentModel;
using System.Threading;
using System.Windows.Media.Imaging; // Dùng để xử lý ảnh nếu cần, ở đây dùng string URL cho đơn giản

namespace YoutubeDownloaderWpf.Models
{
    // Class đại diện cho một video đang tải hoặc chờ tải
    public partial class DownloadItem : ObservableObject
    {
        [ObservableProperty] private string title;
        [ObservableProperty] private string url;
        [ObservableProperty] private double progress; // 0 đến 100
        [ObservableProperty] private string status;   // Trạng thái hiển thị
        [ObservableProperty] private string speed;    // Tốc độ tải

        // Mới: URL ảnh thumbnail của video
        [ObservableProperty] private string thumbnailUrl;

        // Mới: Thời lượng video (ví dụ "10:05")
        [ObservableProperty] private string duration;

        // Biến điều khiển hiển thị nút Stop/Hủy
        [ObservableProperty] private bool isDownloading;

        // Token để hủy lệnh tải khi bấm nút Stop
        public CancellationTokenSource Cts { get; set; } = new CancellationTokenSource();
    }
}