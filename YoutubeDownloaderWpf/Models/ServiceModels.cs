using System;

namespace YoutubeDownloaderWpf.Models
{
    // Class thay thế cho RunResult của thư viện cũ
    public class SimpleRunResult<T>
    {
        public bool Success { get; set; }
        public string ErrorOutput { get; set; }
        public T Data { get; set; }

        public SimpleRunResult(bool success, string error, T data)
        {
            Success = success;
            ErrorOutput = error;
            Data = data;
        }
    }

    // Class thay thế cho DownloadProgress
    public class SimpleProgress
    {
        public double Progress { get; set; } // 0.0 đến 1.0
        public string DownloadSpeed { get; set; }
    }
}