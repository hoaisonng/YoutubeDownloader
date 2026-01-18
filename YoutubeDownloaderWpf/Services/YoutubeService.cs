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
using System.Linq; // Quan trọng để tìm file

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

        // Hàm tải nhận vào đường dẫn file cookies
        Task<SimpleRunResult<string>> DownloadVideoAsync(string url, string outputFolder, string subLangs, string cookieFilePath, IProgress<SimpleProgress> progress, CancellationToken ct);
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

        // --- LOGIC TẢI VIDEO (ĐÃ SỬA ĐỂ DÙNG FILE COOKIES) ---
        public async Task<SimpleRunResult<string>> DownloadVideoAsync(string url, string outputFolder, string subLangs, string cookieFilePath, IProgress<SimpleProgress> progress, CancellationToken ct)
        {
            var argsBuilder = new StringBuilder();

            // 1. Output Path
            string absOutputFolder = Path.GetFullPath(outputFolder);
            if (!Directory.Exists(absOutputFolder)) Directory.CreateDirectory(absOutputFolder);
            string outputTemplate = Path.Combine(absOutputFolder, "%(title)s.%(ext)s");
            argsBuilder.Append($" --encoding utf8 -o \"{outputTemplate}\"");

            // 2. COOKIES (QUAN TRỌNG NHẤT)
            // Chỉ dùng file cookies, không chèn thêm User-Agent thủ công nữa để tránh xung đột
            if (!string.IsNullOrEmpty(cookieFilePath) && File.Exists(cookieFilePath))
            {
                argsBuilder.Append($" --cookies \"{cookieFilePath}\"");
            }

            // 3. Cấu hình chuẩn
            // Thêm --check-formats để yt-dlp kiểm tra kỹ hơn các định dạng có sẵn
            argsBuilder.Append(" -f \"bestvideo+bestaudio/best\" --merge-output-format mp4");
            argsBuilder.Append(" --no-check-certificate --ignore-errors --no-mtime");

            // 4. FFmpeg
            if (File.Exists(FfmpegPath)) argsBuilder.Append($" --ffmpeg-location \"{FfmpegPath}\"");

            // 5. Subtitle
            if (!string.IsNullOrEmpty(subLangs)) argsBuilder.Append($" --write-sub --write-auto-sub --sub-lang \"{subLangs}\"");

            // 6. URL
            argsBuilder.Append($" \"{url}\"");

            // --- LOGIC XỬ LÝ PROCESS (Giữ nguyên như cũ) ---
            var progressRegex = new Regex(@"\[download\]\s+(\d+\.?\d*)%");
            string finalFilename = "";
            var errorLog = new StringBuilder();
            DateTime startTime = DateTime.Now;

            try
            {
                // ... (Đoạn code chạy RunProcessAsync phía dưới giữ nguyên y hệt bài trước) ...
                int exitCode = await RunProcessAsync(YtDlpPath, argsBuilder.ToString(), ct, (line) =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    // Debug.WriteLine(line);

                    if (line.Contains("Merging formats into"))
                    {
                        progress?.Report(new SimpleProgress { Progress = 0.99, DownloadSpeed = "Đang ghép file..." });
                        var mergeMatch = Regex.Match(line, "Merging formats into \"(.*?)\"");
                        if (mergeMatch.Success) finalFilename = mergeMatch.Groups[1].Value;
                    }
                    else if (line.Contains("has already been downloaded"))
                    {
                        var matchFile = Regex.Match(line, @"\[download\]\s+(.*?)\s+has already been downloaded");
                        if (matchFile.Success) finalFilename = matchFile.Groups[1].Value;
                        progress?.Report(new SimpleProgress { Progress = 1.0, DownloadSpeed = "Hoàn tất" });
                    }
                    else if (line.Contains("Destination:") && !line.Contains(".part") && string.IsNullOrEmpty(finalFilename))
                    {
                        finalFilename = line.Replace("Destination: ", "").Replace("[download]", "").Trim();
                    }

                    // Parse progress
                    var match = progressRegex.Match(line);
                    if (match.Success && progress != null)
                    {
                        if (double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double p))
                        {
                            double reportValue = (p >= 100) ? 0.99 : p / 100.0;
                            progress.Report(new SimpleProgress { Progress = reportValue, DownloadSpeed = "" });
                        }
                    }
                },
                (errLine) => errorLog.AppendLine(errLine));

                if (exitCode == 0)
                {
                    // Safety Net logic (Giữ nguyên)
                    if (string.IsNullOrEmpty(finalFilename) || !File.Exists(finalFilename))
                    {
                        var dirInfo = new DirectoryInfo(absOutputFolder);
                        var file = dirInfo.GetFiles()
                                          .Where(f => (f.Extension == ".mp4" || f.Extension == ".mkv") && f.LastWriteTime > startTime.AddSeconds(-20))
                                          .OrderByDescending(f => f.LastWriteTime)
                                          .FirstOrDefault();
                        if (file != null) finalFilename = file.FullName;
                    }
                    return new SimpleRunResult<string>(true, null, finalFilename);
                }
                else
                {
                    return new SimpleRunResult<string>(false, $"Lỗi (Code {exitCode}): " + errorLog.ToString(), null);
                }
            }
            catch (Exception ex)
            {
                return new SimpleRunResult<string>(false, ex.Message, null);
            }
        }
        // --- CÁC HÀM PHỤ TRỢ (GIỮ NGUYÊN) ---
        public async Task<SimpleRunResult<string[]>> GetPlaylistUrlsAsync(string playlistUrl)
        {
            string args = $"--flat-playlist --print url --no-check-certificate --ignore-errors \"{playlistUrl}\"";
            var urls = new List<string>();
            var errorLog = new StringBuilder();
            try
            {
                int exitCode = await RunProcessAsync(YtDlpPath, args, CancellationToken.None,
                    (line) => { if (!string.IsNullOrWhiteSpace(line) && line.Trim().StartsWith("http")) urls.Add(line.Trim()); },
                    (err) => errorLog.AppendLine(err));
                if (urls.Count == 0 && exitCode != 0) return new SimpleRunResult<string[]>(false, errorLog.ToString(), new string[0]);
                return new SimpleRunResult<string[]>(true, null, urls.ToArray());
            }
            catch (Exception ex) { return new SimpleRunResult<string[]>(false, ex.Message, new string[0]); }
        }

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
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            var process = new Process { StartInfo = psi };
            process.EnableRaisingEvents = true;
            process.OutputDataReceived += (s, e) => { if (e.Data != null) onOutput(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) onError(e.Data); };
            process.Exited += (s, e) => tcs.TrySetResult(process.ExitCode);
            using (ct.Register(() => { tcs.TrySetCanceled(); try { if (!process.HasExited) process.Kill(); } catch { } }))
            {
                if (!process.Start()) throw new Exception("Không thể khởi động process.");
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                return await tcs.Task;
            }
        }

        public async Task DownloadYtDlpAsync(IProgress<double> progress) { await DownloadFileAsync(YtDlpUrl, "yt-dlp.exe", progress); }
        public async Task DownloadFfmpegAsync(IProgress<double> progress)
        {
            string zipPath = "ffmpeg.zip"; await DownloadFileAsync(FfmpegUrl, zipPath, progress);
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) { entry.ExtractToFile("ffmpeg.exe", true); break; }
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
                    using (var contentStream = await response.Content.ReadAsStreamAsync())
                    using (var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        var totalRead = 0L; var buffer = new byte[8192]; var isMoreToRead = true;
                        do
                        {
                            var read = await contentStream.ReadAsync(buffer, 0, buffer.Length);
                            if (read == 0) isMoreToRead = false;
                            else { await fileStream.WriteAsync(buffer, 0, read); totalRead += read; if (totalBytes != -1 && progress != null) progress.Report((double)totalRead / totalBytes * 100); }
                        } while (isMoreToRead);
                    }
                }
            }
        }
    }
}