using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using YoutubeDownloaderWpf.Services;
using YoutubeDownloaderWpf.ViewModels;

namespace YoutubeDownloaderWpf
{
    public partial class App : Application
    {
        public static ServiceProvider ServiceProvider { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            var serviceCollection = new ServiceCollection();

            // 1. Đăng ký Service (Logic tải video)
            serviceCollection.AddSingleton<IYoutubeService, YoutubeService>();

            // 2. Đăng ký ViewModel (Logic giao diện)
            serviceCollection.AddSingleton<MainViewModel>();

            // 3. Đăng ký MainWindow (Giao diện chính)
            serviceCollection.AddSingleton<MainWindow>();

            ServiceProvider = serviceCollection.BuildServiceProvider();

            // 4. Lấy MainWindow từ ServiceProvider và hiển thị
            // Cách này giúp MainWindow tự động nhận MainViewModel vào trong nó
            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();

            base.OnStartup(e);
        }
    }
}