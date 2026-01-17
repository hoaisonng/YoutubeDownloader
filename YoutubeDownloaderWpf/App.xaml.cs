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

            // Đăng ký Services và ViewModels
            serviceCollection.AddSingleton<IYoutubeService, YoutubeService>();
            serviceCollection.AddSingleton<MainViewModel>();

            // Đăng ký MainWindow
            serviceCollection.AddSingleton<MainWindow>();

            ServiceProvider = serviceCollection.BuildServiceProvider();

            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();
            mainWindow.Show();

            base.OnStartup(e);
        }
    }
}