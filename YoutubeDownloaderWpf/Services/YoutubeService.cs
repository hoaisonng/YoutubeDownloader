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

        // Đã thêm tham số isAudioOnly
        Task<SimpleRunResult<string>> DownloadVideoAsync(string url, string outputFolder, string subLangs, string cookiePath, bool isAudioOnly, IProgress<SimpleProgress> progress, CancellationToken ct);
        Task<SimpleRunResult<string[]>> GetPlaylistUrlsAsync(string playlistUrl);

        // Hàm mới: Lấy thông tin video
        Task<SimpleRunResult<DownloadItem>> GetVideoMetadataAsync(string url, string cookiePath);
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
            await Task.CompletedTask;
        }

        public async Task UpdateToolsAsync()
        {
            if (IsYtDlpReady) await RunProcessAsync(YtDlpPath, "-U", CancellationToken.None, s => { }, e => { });
        }

        // --- HÀM MỚI: LẤY THÔNG TIN VIDEO ---
        public async Task<SimpleRunResult<DownloadItem>> GetVideoMetadataAsync(string url, string cookiePath)
        {
            // --dump-json: Lấy dữ liệu dạng JSON thay vì tải về
            var args = $"--dump-json --no-playlist --ignore-errors \"{url}\"";
            if (!string.IsNullOrEmpty(cookiePath) && File.Exists(cookiePath))
            {
                args += $" --cookies \"{cookiePath}\"";
            }

            var jsonOutput = new StringBuilder();
            try
            {
                await RunProcessAsync(YtDlpPath, args, CancellationToken.None,
                    line => jsonOutput.AppendLine(line), _ => { });

                string json = jsonOutput.ToString();
                if (string.IsNullOrWhiteSpace(json))
                    return new SimpleRunResult<DownloadItem>(false, "Không lấy được thông tin video.", null);

                // Dùng Regex để tách thông tin từ chuỗi JSON (đơn giản, không cần thư viện JSON)
                string title = Regex.Match(json, "\"title\":\\s*\"(.*?)\"").Groups[1].Value;
                string thumb = Regex.Match(json, "\"thumbnail\":\\s*\"(.*?)\"").Groups[1].Value;
                string durationStr = Regex.Match(json, "\"duration\":\\s*(\\d+)").Groups[1].Value; // Giây

                // Chuyển giây sang format mm:ss
                string durationDisplay = "00:00";
                if (int.TryParse(durationStr, out int seconds))
                {
                    TimeSpan t = TimeSpan.FromSeconds(seconds);
                    durationDisplay = t.ToString(t.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
                }

                var item = new DownloadItem
                {
                    Url = url,
                    Title = string.IsNullOrEmpty(title) ? "Video không tên" : title,
                    ThumbnailUrl = thumb,
                    Duration = durationDisplay,
                    Progress = 0,
                    Status = "Ready"
                };

                return new SimpleRunResult<DownloadItem>(true, null, item);
            }
            catch (Exception ex)
            {
                return new SimpleRunResult<DownloadItem>(false, ex.Message, null);
            }
        }

        // --- HÀM TẢI VIDEO ---
        public async Task<SimpleRunResult<string>> DownloadVideoAsync(string url, string outputFolder, string subLangs, string cookiePath, bool isAudioOnly, IProgress<SimpleProgress> progress, CancellationToken ct)
        {
            var argsBuilder = new StringBuilder();

            // 1. Cấu hình Output
            string outputTemplate = Path.Combine(outputFolder, "%(title)s.%(ext)s");
            // --newline: Quan trọng để regex đọc từng dòng chính xác
            argsBuilder.Append($" --encoding utf8 --newline -o \"{outputTemplate}\"");

            // 2. Chọn Format (Video hay Audio)
            if (isAudioOnly)
            {
                // Tải audio và convert sang mp3
                argsBuilder.Append(" -f \"bestaudio/best\" --extract-audio --audio-format mp3 --audio-quality 192K");
            }
            else
            {
                // Tải video tốt nhất (tối đa 1080p để đảm bảo tốc độ và độ tương thích) + audio tốt nhất
                argsBuilder.Append(" -f \"bestvideo[height<=1080]+bestaudio/best[height<=1080]/best\" --merge-output-format mp4");
            }

            // 3. Metadata (Ảnh bìa, thông tin tác giả vào file)
            argsBuilder.Append(" --embed-thumbnail --add-metadata");

            // 4. Các cấu hình khác
            argsBuilder.Append(" --no-check-certificate --ignore-errors --no-mtime");

            if (File.Exists(FfmpegPath))
            {
                argsBuilder.Append($" --ffmpeg-location \"{FfmpegPath}\"");
            }

            if (!string.IsNullOrEmpty(subLangs))
            {
                argsBuilder.Append($" --write-sub --write-auto-sub --sub-lang \"{subLangs}\" --embed-subs");
            }

            if (!string.IsNullOrEmpty(cookiePath) && File.Exists(cookiePath))
            {
                argsBuilder.Append($" --cookies \"{cookiePath}\"");
            }

            argsBuilder.Append($" \"{url}\"");

            // --- XỬ LÝ LOG ---
            var progressRegex = new Regex(@"\[download\]\s+(\d+\.?\d*)%");
            string finalFilename = "";
            var errorLog = new StringBuilder();

            try
            {
                int exitCode = await RunProcessAsync(YtDlpPath, argsBuilder.ToString(), ct, (line) =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;

                    // Lấy tên file
                    if (line.Contains("Destination:") && !line.Contains(".part"))
                    {
                        finalFilename = line.Replace("Destination: ", "").Trim();
                    }
                    else if (line.Contains("has already been downloaded"))
                    {
                        var matchFile = Regex.Match(line, @"\[download\]\s+(.*?)\s+has already been downloaded");
                        if (matchFile.Success) finalFilename = matchFile.Groups[1].Value;
                        progress?.Report(new SimpleProgress { Progress = 1.0, DownloadSpeed = "Đã xong" });
                    }

                    // Lấy tiến độ
                    var match = progressRegex.Match(line);
                    if (match.Success && progress != null)
                    {
                        if (double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double p))
                        {
                            progress.Report(new SimpleProgress { Progress = p / 100.0, DownloadSpeed = "Downloading..." });
                        }
                    }
                },
                (errLine) => errorLog.AppendLine(errLine));

                if (exitCode == 0) return new SimpleRunResult<string>(true, null, finalFilename);
                else return new SimpleRunResult<string>(false, $"Code {exitCode}: " + errorLog.ToString(), null);
            }
            catch (Exception ex)
            {
                return new SimpleRunResult<string>(false, ex.Message, null);
            }
        }

        public async Task<SimpleRunResult<string[]>> GetPlaylistUrlsAsync(string playlistUrl)
        {
            // --flat-playlist: Lấy danh sách siêu nhanh, không kiểm tra từng video
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
                    return new SimpleRunResult<string[]>(false, errorLog.ToString(), new string[0]);

                return new SimpleRunResult<string[]>(true, null, urls.ToArray());
            }
            catch (Exception ex)
            {
                return new SimpleRunResult<string[]>(false, ex.Message, new string[0]);
            }
        }

        // Logic chạy process ngầm (giữ nguyên nhưng đảm bảo Encoding đúng)
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
                if (!process.Start()) throw new Exception("Không khởi động được tool.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                return await tcs.Task;
            }
        }

        // --- PHẦN TẢI TOOLS (Giữ nguyên logic cũ) ---
        public async Task DownloadYtDlpAsync(IProgress<double> progress) { await DownloadFileAsync(YtDlpUrl, "yt-dlp.exe", progress); }
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
            using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
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
                            if (totalBytes != -1 && progress != null) progress.Report((double)totalRead / totalBytes * 100);
                        }
                    } while (isMoreToRead);
                }
            }
        }
    }
}