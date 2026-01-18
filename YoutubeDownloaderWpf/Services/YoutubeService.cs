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
using GTranslate.Translators; // Mới: Thư viện dịch

namespace YoutubeDownloaderWpf.Services
{
    public interface IYoutubeService
    {
        string YtDlpPath { get; set; }
        string FfmpegPath { get; set; }
        string DenoPath { get; set; }
        bool IsYtDlpReady { get; }
        bool IsFfmpegReady { get; }
        bool IsDenoReady { get; }

        Task InitializeAsync();
        Task UpdateToolsAsync();
        Task DownloadYtDlpAsync(IProgress<double> progress);
        Task DownloadFfmpegAsync(IProgress<double> progress);
        Task DownloadDenoAsync(IProgress<double> progress);

        // SỬA: Thêm tham số customFileName
        Task<SimpleRunResult<string>> DownloadVideoAsync(string url, string outputFolder, string customFileName, string subLangs, string cookiePath, bool isAudioOnly, IProgress<SimpleProgress> progress, CancellationToken ct);

        Task<SimpleRunResult<string[]>> GetPlaylistUrlsAsync(string playlistUrl);
        Task<SimpleRunResult<DownloadItem>> GetVideoMetadataAsync(string url, string cookiePath);

        // MỚI: Hàm dịch
        Task<string> TranslateToEnglishAsync(string text);
    }

    public class YoutubeService : IYoutubeService
    {
        private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        private const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        private const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

        public string YtDlpPath { get; set; } = "yt-dlp.exe";
        public string FfmpegPath { get; set; } = "ffmpeg.exe";
        public string DenoPath { get; set; } = "deno.exe";

        public bool IsYtDlpReady => File.Exists(YtDlpPath);
        public bool IsFfmpegReady => File.Exists(FfmpegPath);
        public bool IsDenoReady => File.Exists(DenoPath);

        // --- MỚI: HÀM DỊCH THUẬT ---
        public async Task<string> TranslateToEnglishAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            try
            {
                // Sử dụng Google Translator thông qua thư viện GTranslate
                var translator = new GoogleTranslator();
                var result = await translator.TranslateAsync(text, "en");
                return result.Translation;
            }
            catch
            {
                // Nếu lỗi dịch (mất mạng...) thì trả về tên gốc
                return text;
            }
        }

        // --- MỚI: HÀM LÀM SẠCH TÊN FILE (Để tránh lỗi khi lưu file) ---
        private string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "video";
            // Thay thế các ký tự cấm trong Windows \ / : * ? " < > | bằng dấu gạch ngang
            string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
            string invalidRegStr = string.Format(@"([{0}]*\.+$)|([{0}]+)", invalidChars);
            return Regex.Replace(name, invalidRegStr, "-");
        }

        public async Task InitializeAsync()
        {
            if (!Directory.Exists("Downloads")) Directory.CreateDirectory("Downloads");
            await Task.CompletedTask;
        }

        public async Task UpdateToolsAsync()
        {
            if (IsYtDlpReady)
                await RunProcessAsync(YtDlpPath, "--update-to nightly", CancellationToken.None, s => { }, e => { });
        }

        // --- CẬP NHẬT HÀM NÀY TRONG YoutubeService.cs ---
        public async Task<SimpleRunResult<DownloadItem>> GetVideoMetadataAsync(string url, string cookiePath)
        {
            var args = $"--dump-json --no-playlist --ignore-errors \"{url}\"";
            if (IsDenoReady) args = $"--js-runtimes \"deno:{DenoPath}\" " + args;

            if (!string.IsNullOrEmpty(cookiePath) && File.Exists(cookiePath))
            {
                args += $" --cookies \"{cookiePath}\"";
                string uaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user-agent.txt");
                if (File.Exists(uaPath))
                {
                    string ua = File.ReadAllText(uaPath).Trim();
                    if (!string.IsNullOrEmpty(ua)) args += $" --user-agent \"{ua}\"";
                }
            }

            var jsonOutput = new StringBuilder();
            try
            {
                // Chạy lệnh lấy thông tin
                await RunProcessAsync(YtDlpPath, args, CancellationToken.None, line => jsonOutput.AppendLine(line), _ => { });
                string json = jsonOutput.ToString();

                if (string.IsNullOrWhiteSpace(json))
                    return new SimpleRunResult<DownloadItem>(false, "Không lấy được thông tin video.", null);

                // Lấy Title
                string title = Regex.Match(json, "\"title\":\\s*\"(.*?)\"").Groups[1].Value;

                // --- [FIX QUAN TRỌNG] ---
                // Dòng này sẽ biến mã lỗi (-uXXXX hoặc \uXXXX) thành tiếng Hàn/Trung/Nhật chuẩn
                try
                {
                    title = Regex.Unescape(title);
                }
                catch { }
                // ------------------------

                string thumb = Regex.Match(json, "\"thumbnail\":\\s*\"(.*?)\"").Groups[1].Value;
                string durationStr = Regex.Match(json, "\"duration\":\\s*(\\d+)").Groups[1].Value;

                string durationDisplay = "00:00";
                if (int.TryParse(durationStr, out int seconds))
                {
                    TimeSpan t = TimeSpan.FromSeconds(seconds);
                    durationDisplay = t.ToString(t.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss");
                }

                var item = new DownloadItem
                {
                    Url = url,
                    Title = string.IsNullOrEmpty(title) ? "Video" : title,
                    ThumbnailUrl = thumb,
                    Duration = durationDisplay,
                    Status = "Ready"
                };
                return new SimpleRunResult<DownloadItem>(true, null, item);
            }
            catch (Exception ex) { return new SimpleRunResult<DownloadItem>(false, ex.Message, null); }
        }
        // --- SỬA: Thêm tham số customFileName ---
        public async Task<SimpleRunResult<string>> DownloadVideoAsync(string url, string outputFolder, string customFileName, string subLangs, string cookiePath, bool isAudioOnly, IProgress<SimpleProgress> progress, CancellationToken ct)
        {
            var argsBuilder = new StringBuilder();
            argsBuilder.Append(" --rm-cache-dir");
            if (IsDenoReady) argsBuilder.Append($" --js-runtimes \"deno:{DenoPath}\"");

            // --- XỬ LÝ TÊN FILE ---
            // Nếu có tên tùy chỉnh (đã dịch) -> Dùng tên đó
            // Nếu không -> Dùng %(title)s để yt-dlp tự lấy tên gốc
            string fileNameTemplate = string.IsNullOrEmpty(customFileName) ? "%(title)s" : SanitizeFileName(customFileName);

            // Format: Folder\TenFile.Ext
            string outputTemplate = Path.Combine(outputFolder, $"{fileNameTemplate}.%(ext)s");

            argsBuilder.Append($" --encoding utf8 --newline -o \"{outputTemplate}\"");

            string uaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user-agent.txt");
            if (File.Exists(uaPath))
            {
                string ua = File.ReadAllText(uaPath).Trim();
                if (!string.IsNullOrEmpty(ua)) argsBuilder.Append($" --user-agent \"{ua}\"");
            }

            if (isAudioOnly)
                argsBuilder.Append(" -f \"bestaudio/best\" --extract-audio --audio-format mp3 --audio-quality 192K");
            else
                argsBuilder.Append(" -f \"bestvideo[height<=1080]+bestaudio/best[height<=1080]/best\" --merge-output-format mp4");

            argsBuilder.Append(" --embed-thumbnail --add-metadata");
            argsBuilder.Append(" --no-check-certificate --ignore-errors --no-mtime");

            if (File.Exists(FfmpegPath)) argsBuilder.Append($" --ffmpeg-location \"{FfmpegPath}\"");
            if (!string.IsNullOrEmpty(subLangs)) argsBuilder.Append($" --write-sub --write-auto-sub --sub-lang \"{subLangs}\" --embed-subs");
            if (!string.IsNullOrEmpty(cookiePath) && File.Exists(cookiePath)) argsBuilder.Append($" --cookies \"{cookiePath}\"");

            argsBuilder.Append($" \"{url}\"");

            var progressRegex = new Regex(@"\[download\]\s+(\d+\.?\d*)%");
            string finalFilename = "";
            var errorLog = new StringBuilder();

            try
            {
                int exitCode = await RunProcessAsync(YtDlpPath, argsBuilder.ToString(), ct, (line) =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
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

                    var match = progressRegex.Match(line);
                    if (match.Success && progress != null)
                    {
                        if (double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double p))
                            progress.Report(new SimpleProgress { Progress = p / 100.0, DownloadSpeed = "Downloading..." });
                    }
                }, (errLine) => errorLog.AppendLine(errLine));

                if (exitCode != 0 || (string.IsNullOrEmpty(finalFilename) && errorLog.Length > 0))
                    return new SimpleRunResult<string>(false, $"Lỗi (Code {exitCode}): " + errorLog.ToString(), null);

                return new SimpleRunResult<string>(true, null, finalFilename);
            }
            catch (Exception ex) { return new SimpleRunResult<string>(false, ex.Message, null); }
        }

        public async Task<SimpleRunResult<string[]>> GetPlaylistUrlsAsync(string playlistUrl)
        {
            string args = $"--flat-playlist --print url --no-check-certificate --ignore-errors \"{playlistUrl}\"";
            var urls = new List<string>();
            var errorLog = new StringBuilder();
            try
            {
                await RunProcessAsync(YtDlpPath, args, CancellationToken.None,
                    (line) => { if (!string.IsNullOrWhiteSpace(line) && line.Trim().StartsWith("http")) urls.Add(line.Trim()); },
                    (err) => errorLog.AppendLine(err));
                if (urls.Count == 0) return new SimpleRunResult<string[]>(false, errorLog.ToString(), new string[0]);
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
                if (!process.Start()) throw new Exception("Không khởi động được tool.");
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                return await tcs.Task;
            }
        }

        public async Task DownloadYtDlpAsync(IProgress<double> progress) { await DownloadFileAsync(YtDlpUrl, "yt-dlp.exe", progress); }
        public async Task DownloadDenoAsync(IProgress<double> progress)
        {
            string zipPath = "deno.zip";
            await DownloadFileAsync(DenoUrl, zipPath, progress);
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (entry.FullName.EndsWith("deno.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        entry.ExtractToFile("deno.exe", true);
                        break;
                    }
                }
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
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