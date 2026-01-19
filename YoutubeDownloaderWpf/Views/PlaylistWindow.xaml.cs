using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using YoutubeDownloaderWpf.Services;

namespace YoutubeDownloaderWpf.Views
{
    // Cập nhật Model để chứa ảnh và tiêu đề
    public class PlaylistVideoItem
    {
        public bool IsSelected { get; set; } = true;
        public string Url { get; set; }
        public string Title { get; set; }
        public string ThumbnailUrl { get; set; }
        public string Duration { get; set; }
    }

    public partial class PlaylistWindow : Window
    {
        private readonly IYoutubeService _service;
        public List<string> SelectedUrls { get; private set; } = new List<string>();

        public PlaylistWindow(IYoutubeService service)
        {
            InitializeComponent();
            _service = service;
        }

        private async void GetList_Click(object sender, RoutedEventArgs e)
        {
            string url = txtPlaylistUrl.Text;
            if (string.IsNullOrWhiteSpace(url)) return;

            btnScan.IsEnabled = false;
            lblStatus.Text = "Đang quét danh sách (sẽ mất vài giây)...";

            // Gọi hàm mới GetPlaylistItemsAsync (sẽ tạo ở bước 2)
            var result = await _service.GetPlaylistItemsAsync(url);

            if (result.Success)
            {
                dgList.ItemsSource = result.Data; // Data bây giờ là List<PlaylistVideoItem>
                lblStatus.Text = $"Tìm thấy {result.Data.Count} video.";
            }
            else
            {
                lblStatus.Text = "Lỗi: " + result.ErrorOutput;
            }
            btnScan.IsEnabled = true;
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            // Lấy danh sách từ DataGrid
            var items = dgList.ItemsSource as List<PlaylistVideoItem>;
            if (items != null)
            {
                SelectedUrls = items.Where(x => x.IsSelected).Select(x => x.Url).ToList();
                this.DialogResult = true;
                this.Close();
            }
        }
    }
}