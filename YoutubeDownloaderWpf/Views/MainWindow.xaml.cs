using System.Windows;
using YoutubeDownloaderWpf.ViewModels;

namespace YoutubeDownloaderWpf
{
    public partial class MainWindow : Window
    {
        // Constructor injection
        public MainWindow(MainViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }
    }
}