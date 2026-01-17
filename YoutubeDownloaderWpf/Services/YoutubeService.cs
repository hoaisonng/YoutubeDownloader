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

            // 1. Cấu hình Output
            string outputTemplate = Path.Combine(outputFolder, "%(title)s.%(ext)s");
            argsBuilder.Append($" --encoding utf8 -o \"{outputTemplate}\"");

            // 2. Format và Merge
            argsBuilder.Append(" -f \"bestvideo+bestaudio/best\" --merge-output-format mp4");
            argsBuilder.Append(" --no-check-certificate --ignore-errors --no-mtime");

            // ---> FIX QUAN TRỌNG: CHỈ ĐỊNH RÕ ĐƯỜNG DẪN FFMPEG <---
            // Nếu không có dòng này, yt-dlp sẽ không tìm thấy ffmpeg để ghép file
            if (File.Exists(FfmpegPath))
            {
                argsBuilder.Append($" --ffmpeg-location \"{FfmpegPath}\"");
            }

            // 3. Subtitle
            if (!string.IsNullOrEmpty(subLangs))
            {
                argsBuilder.Append($" --write-sub --write-auto-sub --sub-lang \"{subLangs}\"");
            }

            // 4. Cookies
            if (!string.IsNullOrEmpty(cookiePath) && File.Exists(cookiePath))
            {
                argsBuilder.Append($" --cookies \"{cookiePath}\"");
            }

            // 5. URL
            argsBuilder.Append($" \"{url}\"");

            // --- XỬ LÝ PROCESS ---
            var progressRegex = new Regex(@"\[download\]\s+(\d+\.?\d*)%");
            string finalFilename = "";
            var errorLog = new StringBuilder();
            bool isMerging = false; // Cờ đánh dấu đang ghép file

            try
            {
                int exitCode = await RunProcessAsync(YtDlpPath, argsBuilder.ToString(), ct, (line) =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;

                    // Debug: Xem log thực tế yt-dlp trả về
                    Debug.WriteLine(line);

                    // Bắt sự kiện bắt đầu ghép file
                    if (line.Contains("[Merger]") || line.Contains("Merging formats"))
                    {
                        isMerging = true;
                        // Hack nhẹ: Báo progress đặc biệt để UI biết đang xử lý
                        progress?.Report(new SimpleProgress { Progress = 0.99, DownloadSpeed = "Đang ghép file..." });
                    }

                    // Lấy tên file kết quả
                    if (line.Contains("Destination:") && !line.Contains(".part"))
                    {
                        // Logic cũ có thể trượt, dùng logic đơn giản hơn:
                        // Lấy phần text sau "Destination: "
                        finalFilename = line.Replace("Destination: ", "").Trim();
                    }
                    // Trường hợp file đã tồn tại và yt-dlp bỏ qua tải
                    else if (line.Contains("has already been downloaded"))
                    {
                        // Cố gắng parse tên file từ dòng thông báo
                        // Log mẫu: [download] Downloads\VideoName.mp4 has already been downloaded
                        var matchFile = Regex.Match(line, @"\[download\]\s+(.*?)\s+has already been downloaded");
                        if (matchFile.Success) finalFilename = matchFile.Groups[1].Value;
                        progress?.Report(new SimpleProgress { Progress = 1.0, DownloadSpeed = "Hoàn tất" });
                    }

                    // Parse tiến độ tải (Chỉ parse khi CHƯA ghép file)
                    if (!isMerging)
                    {
                        var match = progressRegex.Match(line);
                        if (match.Success && progress != null)
                        {
                            if (double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double p))
                            {
                                // Nếu p = 100% nhưng chưa xong hẳn (còn merge), ta chỉ để 99% thôi
                                double reportValue = (p >= 100) ? 0.99 : p / 100.0;
                                progress.Report(new SimpleProgress { Progress = reportValue, DownloadSpeed = "" });
                            }
                        }
                    }
                },
                (errLine) => errorLog.AppendLine(errLine));

                // Kiểm tra kết quả
                // Nếu exitCode = 0 (Thành công)
                if (exitCode == 0)
                {
                    // Nếu không bắt được tên file từ Log (do yt-dlp thay đổi format log), 
                    // ta thử tìm file mới nhất trong thư mục downloads khớp với title (Optional logic)
                    // Nhưng tốt nhất là trả về true.
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