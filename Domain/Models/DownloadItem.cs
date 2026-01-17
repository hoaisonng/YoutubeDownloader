namespace Domain.Models
{
    public enum DownloadStatus
    {
        Pending,
        Downloading,
        Completed,
        Failed,
        Canceled
    }

    public class DownloadItem
    {
        public string Url { get; set; } = "";
        public string Title { get; set; } = "";
        public double Progress { get; set; }
        public DownloadStatus Status { get; set; }
    }

}
