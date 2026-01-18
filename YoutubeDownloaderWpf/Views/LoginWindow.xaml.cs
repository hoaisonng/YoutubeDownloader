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
            // Xóa cookie cũ
            webView.CoreWebView2.CookieManager.DeleteAllCookies();
            webView.Source = new Uri("https://www.youtube.com");
        }

        private async void SaveCookies_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var cookieManager = webView.CoreWebView2.CookieManager;
                var cookies = await cookieManager.GetCookiesAsync("https://www.youtube.com");

                if (cookies.Count == 0)
                {
                    MessageBox.Show("Chưa thấy cookies. Hãy đăng nhập trước!", "Thông báo");
                    return;
                }

                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Netscape HTTP Cookie File");

                foreach (var cookie in cookies)
                {
                    string domain = cookie.Domain;
                    // Fix domain
                    if (!domain.StartsWith(".") && !System.Text.RegularExpressions.Regex.IsMatch(domain, @"^\d"))
                        domain = "." + domain;

                    string flag = domain.StartsWith(".") ? "TRUE" : "FALSE";
                    string path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path;
                    string secure = cookie.IsSecure ? "TRUE" : "FALSE";

                    // --- SỬA PHẦN NÀY ĐỂ FIX LỖI CS0019/CS0030 ---
                    // Chuyển đổi linh hoạt bất kể cookie.Expires là double hay DateTime
                    long expires = 4102444800; // Mặc định năm 2099

                    try
                    {
                        // Cách xử lý an toàn nhất: Ép sang DateTime rồi lấy Timestamp
                        // Lưu ý: Nếu máy bạn báo lỗi ở dòng 'cookie.Expires', hãy thử chuột phải vào chữ Expires -> Go to Definition xem nó là kiểu gì.
                        // Code dưới đây giả định nó đang bị hiểu là DateTime như lỗi bạn báo.

                        dynamic rawExpires = cookie.Expires; // Dùng dynamic để tránh lỗi biên dịch kiểu dữ liệu

                        // Kiểm tra nếu là DateTime
                        if (rawExpires is DateTime dt)
                        {
                            if (dt != DateTime.MinValue)
                                expires = new DateTimeOffset(dt).ToUnixTimeSeconds();
                        }
                        // Kiểm tra nếu là Double (số giây)
                        else if (rawExpires is double d)
                        {
                            if (d > 0) expires = (long)d;
                        }
                    }
                    catch
                    {
                        // Nếu lỗi convert thì giữ nguyên mặc định 2099
                    }
                    // ---------------------------------------------

                    sb.AppendLine($"{domain}\t{flag}\t{path}\t{secure}\t{expires}\t{cookie.Name}\t{cookie.Value}");
                }

                string pathFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cookies.txt");
                File.WriteAllText(pathFile, sb.ToString());

                SavedCookiePath = pathFile;
                MessageBox.Show("Đã lưu Cookies! Giờ bạn có thể tải video.", "Thành công");
                this.DialogResult = true;
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Lỗi: {ex.Message}");
            }
        }
    }
}