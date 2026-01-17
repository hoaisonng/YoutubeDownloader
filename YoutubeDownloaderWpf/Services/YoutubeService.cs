using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using YoutubeDownloaderWpf.Models;

namespace YoutubeDownloaderWpf.Services
{
    public interface IYoutubeService
    {
        string YtDlpPath { get; set; }
        string FfmpegPath { get; set; }
        bool IsYtDlpReady { get; }
        bool IsFfmpegReady { get; }

        Task InitializeAsync();
        Task UpdateToolsAsync();
        Task DownloadYtDlpAsync(IProgress<double> progress);
        Task DownloadFfmpegAsync(IProgress<double> progress);

        Task<SimpleRunResult<string>> DownloadVideoAsync(string url, string outputFolder, string subLangs, string cookiePath, IProgress<SimpleProgress> progress, CancellationToken ct);
        Task<SimpleRunResult<string[]>> GetPlaylistUrlsAsync(string playlistUrl);
    }

    public class YoutubeService : IYoutubeService
    {
        private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        private const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

        public string YtDlpPath { get; set; } = "yt-dlp.exe";
        public string FfmpegPath { get; set; } = "ffmpeg.exe";

        public bool IsYtDlpReady => File.Exists(YtDlpPath);
        public bool IsFfmpegReady => File.Exists(FfmpegPath);

        public async Task InitializeAsync()
        {
            if (!Directory.Exists("Downloads")) Directory.CreateDirectory("Downloads");
        }

        public async Task UpdateToolsAsync()
        {
            if (IsYtDlpReady) await RunProcessAsync(YtDlpPath, "-U", CancellationToken.None, s => { }, e => { });
        }

        // --- DOWNLOAD VIDEO ---
        public async Task<SimpleRunResult<string>> DownloadVideoAsync(string url, string outputFolder, string subLangs, string cookiePath, IProgress<SimpleProgress> progress, CancellationToken ct)
        {
            var argsBuilder = new StringBuilder();

            // Cấu hình output và format
            string outputTemplate = Path.Combine(outputFolder, "%(title)s.%(ext)s");
            // Thêm encoding utf8 để tránh lỗi hiển thị tên file tiếng Việt
            argsBuilder.Append($" --encoding utf8 -o \"{outputTemplate}\"");
            argsBuilder.Append(" -f \"bestvideo+bestaudio/best\" --merge-output-format mp4");
            argsBuilder.Append(" --no-check-certificate --ignore-errors --no-mtime");

            // Subtitle
            if (!string.IsNullOrEmpty(subLangs))
            {
                argsBuilder.Append($" --write-sub --write-auto-sub --sub-lang \"{subLangs}\"");
            }

            // Cookies
            if (!string.IsNullOrEmpty(cookiePath) && File.Exists(cookiePath))
            {
                argsBuilder.Append($" --cookies \"{cookiePath}\"");
            }

            // URL
            argsBuilder.Append($" \"{url}\"");

            // Regex bắt tiến độ
            var progressRegex = new Regex(@"\[download\]\s+(\d+\.?\d*)%");
            var speedRegex = new Regex(@"at\s+(\S+)");

            string finalFilename = "";
            var errorLog = new StringBuilder();

            try
            {
                int exitCode = await RunProcessAsync(YtDlpPath, argsBuilder.ToString(), ct, (line) =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;

                    // Log dòng raw để debug (nếu cần xem nó đang chạy gì)
                    Debug.WriteLine($"[YTDLP]: {line}");

                    // Lấy tên file
                    if (line.Contains("[Merger] Merging formats into") || line.Contains("Destination:"))
                    {
                        var parts = line.Split('"');
                        if (parts.Length > 1) finalFilename = parts[1];
                        else if (line.Contains("Destination: ")) finalFilename = line.Replace("Destination: ", "").Trim();
                    }

                    // Parse tiến độ
                    var match = progressRegex.Match(line);
                    if (match.Success && progress != null)
                    {
                        if (double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double p))
                        {
                            string speed = "N/A";
                            var speedMatch = speedRegex.Match(line);
                            if (speedMatch.Success) speed = speedMatch.Groups[1].Value;

                            // Report về UI
                            progress.Report(new SimpleProgress { Progress = p / 100.0, DownloadSpeed = speed });
                        }
                    }
                },
                (errLine) =>
                {
                    Debug.WriteLine($"[ERROR]: {errLine}");
                    errorLog.AppendLine(errLine);
                });

                if (exitCode != 0 && string.IsNullOrEmpty(finalFilename))
                {
                    return new SimpleRunResult<string>(false, $"Lỗi (Code {exitCode}): " + errorLog.ToString(), null);
                }

                return new SimpleRunResult<string>(true, null, finalFilename);
            }
            catch (Exception ex)
            {
                return new SimpleRunResult<string>(false, ex.Message, null);
            }
        }

        // --- GET PLAYLIST ---
        public async Task<SimpleRunResult<string[]>> GetPlaylistUrlsAsync(string playlistUrl)
        {
            string args = $"--flat-playlist --print url --no-check-certificate --ignore-errors \"{playlistUrl}\"";
            var urls = new List<string>();
            var errorLog = new StringBuilder();

            try
            {
                int exitCode = await RunProcessAsync(YtDlpPath, args, CancellationToken.None,
                    (line) =>
                    {
                        if (!string.IsNullOrWhiteSpace(line) && line.Trim().StartsWith("http"))
                            urls.Add(line.Trim());
                    },
                    (err) => errorLog.AppendLine(err));

                if (urls.Count == 0 && exitCode != 0)
                {
                    return new SimpleRunResult<string[]>(false, "Không lấy được video nào.\n" + errorLog.ToString(), new string[0]);
                }

                return new SimpleRunResult<string[]>(true, null, urls.ToArray());
            }
            catch (Exception ex)
            {
                return new SimpleRunResult<string[]>(false, ex.Message, new string[0]);
            }
        }

        // --- HÀM CHẠY PROCESS CHUẨN (FIX TREO UI) ---
        private async Task<int> RunProcessAsync(string fileName, string arguments, CancellationToken ct, Action<string> onOutput, Action<string> onError)
        {
            var tcs = new TaskCompletionSource<int>();

            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, // Đọc tiếng Việt
                StandardErrorEncoding = Encoding.UTF8
            };

            var process = new Process { StartInfo = psi };
            process.EnableRaisingEvents = true;

            // Xử lý sự kiện Output (Không chặn luồng)
            process.OutputDataReceived += (s, e) => { if (e.Data != null) onOutput(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) onError(e.Data); };

            process.Exited += (s, e) => tcs.TrySetResult(process.ExitCode);

            using (ct.Register(() => {
                tcs.TrySetCanceled();
                try { if (!process.HasExited) process.Kill(); } catch { }
            }))
            {
                if (!process.Start()) throw new Exception("Không thể khởi động process.");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                return await tcs.Task;
            }
        }

        // --- DOWNLOAD TOOLS (Giữ nguyên) ---
        public async Task DownloadYtDlpAsync(IProgress<double> progress)
        {
            await DownloadFileAsync(YtDlpUrl, "yt-dlp.exe", progress);
        }

        public async Task DownloadFfmpegAsync(IProgress<double> progress)
        {
            string zipPath = "ffmpeg.zip";
            await DownloadFileAsync(FfmpegUrl, zipPath, progress);
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        entry.ExtractToFile("ffmpeg.exe", true);
                        break;
                    }
                }
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
        }

        private async Task DownloadFileAsync(string url, string destination, IProgress<double> progress)
        {
            using (HttpClient client = new HttpClient())
            {
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    var canReportProgress = totalBytes != -1 && progress != null;

                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var totalRead = 0L;
                        var buffer = new byte[8192];
                        var isMoreToRead = true;
                        do
                        {
                            var read = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                            if (read == 0) isMoreToRead = false;
                            else
                            {
                                await fileStream.WriteAsync(buffer, 0, read);
                                totalRead += read;
                                if (canReportProgress) progress.Report((double)totalRead / totalBytes * 100);
                            }
                        } while (isMoreToRead);
                    }
                }
            }
        }
    }
}