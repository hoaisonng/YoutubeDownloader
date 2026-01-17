using System;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace YoutubeDownloaderWpf.Views
{
    public partial class LoginWindow : Window
    {
        public string SavedCookiePath { get; private set; }

        public LoginWindow()
        {
            InitializeComponent();
            InitializeAsync();
        }

        async void InitializeAsync()
        {
            await webView.EnsureCoreWebView2Async(null);
        }

        private async void SaveCookies_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cookieManager = webView.CoreWebView2.CookieManager;
                var cookies = await cookieManager.GetCookiesAsync("https://www.youtube.com");

                if (cookies.Count == 0)
                {
                    MessageBox.Show("Chưa tìm thấy cookies. Hãy chắc chắn bạn đã đăng nhập!", "Thông báo");
                    return;
                }

                // Chuyển đổi cookie sang format Netscape mà yt-dlp hiểu
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Netscape HTTP Cookie File");

                foreach (var cookie in cookies)
                {
                    // Format: domain flag path secure expiration name value
                    string domain = cookie.Domain.StartsWith(".") ? cookie.Domain : "." + cookie.Domain;
                    string flag = "TRUE";
                    string path = cookie.Path;
                    string secure = cookie.IsSecure ? "TRUE" : "FALSE";

                    long expires;

                    // WebView2: cookie.Expires là double.
                    if (cookie.IsSession || cookie.Expires == System.DateTime.MinValue)
                    {
                        // Nếu là cookie phiên hoặc ngày hết hạn không hợp lệ -> Gán hạn 1 năm
                        expires = System.DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
                    }
                    else
                    {
                        // Nếu có ngày hết hạn cụ thể:
                        // Phải chuyển đổi từ DateTime sang Unix Timestamp (số giây)
                        try
                        {
                            // Chuyển DateTime sang DateTimeOffset rồi lấy số giây
                            expires = new System.DateTimeOffset(cookie.Expires).ToUnixTimeSeconds();
                        }
                        catch
                        {
                            // Phòng trường hợp lỗi chuyển đổi, gán mặc định 1 năm
                            expires = System.DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
                        }
                    }

                    sb.AppendLine($"{domain}\t{flag}\t{path}\t{secure}\t{expires}\t{cookie.Name}\t{cookie.Value}");
                }

                // Lưu vào file cookies.txt trong thư mục app
                string pathFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cookies.txt");
                File.WriteAllText(pathFile, sb.ToString());

                SavedCookiePath = pathFile;
                MessageBox.Show("Đã lưu cookies thành công! Bạn có thể tải video Member ngay.", "Thành công");
                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi lưu cookie: {ex.Message}");
            }
        }
    }
}