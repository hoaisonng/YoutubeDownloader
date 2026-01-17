using System.Collections.Generic;
using System.Linq;
using System.Windows;
using YoutubeDownloaderWpf.Services;

namespace YoutubeDownloaderWpf.Views
{
    // Class đơn giản để bind lên DataGrid
    public class PlaylistVideoItem
    {
        public bool IsSelected { get; set; } = true;
        public string Url { get; set; }
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

            lblStatus.Text = "Đang quét danh sách...";
            var result = await _service.GetPlaylistUrlsAsync(url);

            if (result.Success)
            {
                var items = result.Data.Select(u => new PlaylistVideoItem { Url = u }).ToList();
                dgList.ItemsSource = items;
                lblStatus.Text = $"Tìm thấy {items.Count} video.";
            }
            else
            {
                lblStatus.Text = "Lỗi: " + result.ErrorOutput;
            }
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
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