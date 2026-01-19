using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json; // Cần dùng thư viện này (có sẵn trong .NET)
using YoutubeDownloaderWpf.Models;

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

        Task<SimpleRunResult<string>> DownloadVideoAsync(string url, string outputFolder, string customFileName, string subLangs, string cookiePath, bool isAudioOnly, IProgress<SimpleProgress> progress, CancellationToken ct);
        Task<SimpleRunResult<string[]>> GetPlaylistUrlsAsync(string playlistUrl);
        Task<SimpleRunResult<DownloadItem>> GetVideoMetadataAsync(string url, string cookiePath);
        Task<string> TranslateToEnglishAsync(string text);
        // Thêm dòng này vào Interface
        Task<SimpleRunResult<List<Views.PlaylistVideoItem>>> GetPlaylistItemsAsync(string playlistUrl);
    }

    public class YoutubeService : IYoutubeService
    {
        // =================================================================================
        // [CẤU HÌNH AI] Dán API Key Gemini của bạn vào giữa 2 dấu ngoặc kép bên dưới
        // Lấy key tại: https://aistudio.google.com/app/apikey
        private const string GeminiApiKey = "AIzaSyBLe7bNfv-5Watv9DfOKAoofvyGgpqB0EE";
        // =================================================================================

        private const string YtDlpUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        private const string FfmpegUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        private const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";

        public string YtDlpPath { get; set; } = "yt-dlp.exe";
        public string FfmpegPath { get; set; } = "ffmpeg.exe";
        public string DenoPath { get; set; } = "deno.exe";

        public bool IsYtDlpReady => File.Exists(YtDlpPath);
        public bool IsFfmpegReady => File.Exists(FfmpegPath);
        public bool IsDenoReady => File.Exists(DenoPath);

        private readonly HttpClient _httpClient;

        public YoutubeService()
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/91.0.4472.124 Safari/537.36");
        }

        // --- HÀM ĐIỀU PHỐI DỊCH THUẬT ---
        public async Task<string> TranslateToEnglishAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
            text = text.Trim();

            // 1. Ưu tiên dùng AI (Gemini) nếu có Key
            if (!string.IsNullOrEmpty(GeminiApiKey))
            {
                string aiResult = await TranslateWithGeminiAI(text);
                if (!string.IsNullOrEmpty(aiResult)) return aiResult;
            }

            // 2. Nếu không có Key hoặc lỗi, dùng Google Translate "thông minh" (Có ngữ cảnh)
            return await TranslateWithGoogleContext(text);
        }

        // --- CÁCH 1: DÙNG AI GEMINI (CHUẨN NHẤT) ---
        private async Task<string> TranslateWithGeminiAI(string text)
        {
            try
            {
                string url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key={GeminiApiKey}";

                // Câu lệnh Prompt cực kỹ cho AI
                var prompt = $"You are a professional badminton translator. Translate the following video title to English. Use specific badminton terminology (e.g., 'hitting point' instead of 'RBI'). Only output the translation, no explanation. Text: {text}";

                var payload = new
                {
                    contents = new[]
                    {
                        new { parts = new[] { new { text = prompt } } }
                    }
                };

                string jsonContent = JsonSerializer.Serialize(payload);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(url, content);
                if (!response.IsSuccessStatusCode) return null;

                string responseString = await response.Content.ReadAsStringAsync();

                // Parse JSON trả về từ Gemini
                using (JsonDocument doc = JsonDocument.Parse(responseString))
                {
                    // Cấu trúc: candidates[0].content.parts[0].text
                    var root = doc.RootElement;
                    if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
                    {
                        var parts = candidates[0].GetProperty("content").GetProperty("parts");
                        if (parts.GetArrayLength() > 0)
                        {
                            return parts[0].GetProperty("text").GetString().Trim();
                        }
                    }
                }
                return null;
            }
            catch { return null; }
        }

        // --- CÁCH 2: DÙNG GOOGLE TRANSLATE VỚI NGỮ CẢNH (MIỄN PHÍ) ---
        private async Task<string> TranslateWithGoogleContext(string text)
        {
            try
            {
                // MẸO: Thêm chữ "Badminton: " vào trước để ép Google hiểu ngữ cảnh
                // Ví dụ: "Badminton: 낮은 타점 연습" -> Google sẽ dịch đúng là Hitting point thay vì RBI
                string query = "Badminton: " + text;

                string url = $"https://clients5.google.com/translate_a/t?client=dict-chrome-ex&sl=auto&tl=en&q={Uri.EscapeDataString(query)}";

                var response = await _httpClient.GetAsync(url);
                string json = await response.Content.ReadAsStringAsync();

                string translated = text;

                // Parse kết quả clients5
                if (!string.IsNullOrEmpty(json))
                {
                    if (json.StartsWith("[\"") && json.EndsWith("\"]"))
                        translated = Regex.Unescape(json.Substring(2, json.Length - 4));
                    else
                    {
                        var match = Regex.Match(json, "\"([^\"]+)\"");
                        if (match.Success) translated = Regex.Unescape(match.Groups[1].Value);
                    }
                }

                // Xóa bỏ chữ "Badminton: " hoặc "Badminton" ở đầu kết quả
                // Vd: "Badminton: Low hitting point practice" -> "Low hitting point practice"
                translated = Regex.Replace(translated, @"^Badminton[:\s-]*", "", RegexOptions.IgnoreCase).Trim();

                // Fix cứng một số lỗi nếu Google vẫn sai
                translated = translated.Replace("RBI", "hitting point", StringComparison.OrdinalIgnoreCase);

                return translated;
            }
            catch { return text; }
        }

        private string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "video";
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
            if (IsYtDlpReady) await RunProcessAsync(YtDlpPath, "--update-to nightly", CancellationToken.None, s => { }, e => { });
        }

        public async Task<SimpleRunResult<DownloadItem>> GetVideoMetadataAsync(string url, string cookiePath)
        {
            var args = $"--dump-json --no-playlist --ignore-errors \"{url}\"";
            if (IsDenoReady) args = $"--js-runtimes \"deno:{DenoPath}\" " + args;

            if (!string.IsNullOrEmpty(cookiePath) && File.Exists(cookiePath))
            {
                args += $" --cookies \"{cookiePath}\"";
                string uaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user-agent.txt");
                if (File.Exists(uaPath) && !string.IsNullOrEmpty(File.ReadAllText(uaPath)))
                    args += $" --user-agent \"{File.ReadAllText(uaPath).Trim()}\"";
            }

            var jsonOutput = new StringBuilder();
            try
            {
                await RunProcessAsync(YtDlpPath, args, CancellationToken.None, line => jsonOutput.AppendLine(line), _ => { });
                string json = jsonOutput.ToString();

                if (string.IsNullOrWhiteSpace(json))
                    return new SimpleRunResult<DownloadItem>(false, "Không lấy được thông tin video.", null);

                string title = Regex.Match(json, "\"title\":\\s*\"(.*?)\"").Groups[1].Value;
                try { title = Regex.Unescape(title); } catch { }

                string thumb = Regex.Match(json, "\"thumbnail\":\\s*\"(.*?)\"").Groups[1].Value;
                string durationStr = Regex.Match(json, "\"duration\":\\s*(\\d+)").Groups[1].Value;

                // Parse duration
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

        public async Task<SimpleRunResult<string>> DownloadVideoAsync(string url, string outputFolder, string customFileName, string subLangs, string cookiePath, bool isAudioOnly, IProgress<SimpleProgress> progress, CancellationToken ct)
        {
            var argsBuilder = new StringBuilder();
            argsBuilder.Append(" --rm-cache-dir");
            if (IsDenoReady) argsBuilder.Append($" --js-runtimes \"deno:{DenoPath}\"");

            string fileNameTemplate = string.IsNullOrEmpty(customFileName) ? "%(title)s" : SanitizeFileName(customFileName);
            string outputTemplate = Path.Combine(outputFolder, $"{fileNameTemplate}.%(ext)s");
            argsBuilder.Append($" --encoding utf8 --newline -o \"{outputTemplate}\"");

            string uaPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user-agent.txt");
            if (File.Exists(uaPath) && !string.IsNullOrEmpty(File.ReadAllText(uaPath)))
                argsBuilder.Append($" --user-agent \"{File.ReadAllText(uaPath).Trim()}\"");

            if (isAudioOnly)
                argsBuilder.Append(" -f \"bestaudio/best\" --extract-audio --audio-format mp3 --audio-quality 192K");
            else
                argsBuilder.Append(" -f \"bestvideo[height<=1080]+bestaudio/best[height<=1080]/best\" --merge-output-format mp4");

            argsBuilder.Append(" --embed-thumbnail --add-metadata --no-check-certificate --ignore-errors --no-mtime");

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
                        finalFilename = line.Replace("Destination: ", "").Trim();
                    else if (line.Contains("has already been downloaded"))
                    {
                        var matchFile = Regex.Match(line, @"\[download\]\s+(.*?)\s+has already been downloaded");
                        if (matchFile.Success) finalFilename = matchFile.Groups[1].Value;
                        progress?.Report(new SimpleProgress { Progress = 1.0, DownloadSpeed = "Đã xong" });
                    }

                    var match = progressRegex.Match(line);
                    if (match.Success && progress != null && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double p))
                        progress.Report(new SimpleProgress { Progress = p / 100.0, DownloadSpeed = "Downloading..." });
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
                foreach (ZipArchiveEntry entry in archive.Entries) { if (entry.FullName.EndsWith("deno.exe", StringComparison.OrdinalIgnoreCase)) { entry.ExtractToFile("deno.exe", true); break; } }
            }
            if (File.Exists(zipPath)) File.Delete(zipPath);
        }
        public async Task DownloadFfmpegAsync(IProgress<double> progress)
        {
            string zipPath = "ffmpeg.zip";
            await DownloadFileAsync(FfmpegUrl, zipPath, progress);
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries) { if (entry.FullName.EndsWith("ffmpeg.exe", StringComparison.OrdinalIgnoreCase)) { entry.ExtractToFile("ffmpeg.exe", true); break; } }
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
        // --- HÀM MỚI: QUÉT PLAYLIST LẤY CẢ ẢNH VÀ TÊN ---
        public async Task<SimpleRunResult<List<Views.PlaylistVideoItem>>> GetPlaylistItemsAsync(string playlistUrl)
        {
            // --flat-playlist: Quét nhanh (không check từng video)
            // --dump-json: Lấy thông tin chi tiết
            string args = $"--flat-playlist --dump-json --no-check-certificate --ignore-errors \"{playlistUrl}\"";

            var items = new List<Views.PlaylistVideoItem>();
            var errorLog = new StringBuilder();

            try
            {
                await RunProcessAsync(YtDlpPath, args, CancellationToken.None,
                    (line) =>
                    {
                        if (string.IsNullOrWhiteSpace(line)) return;
                        try
                        {
                            // Parse JSON đơn giản bằng Regex để lấy thông tin
                            // Lý do không dùng thư viện JSON: Để giữ code nhẹ và nhanh, yt-dlp trả về mỗi dòng là 1 JSON object
                            string id = Regex.Match(line, "\"id\":\\s*\"(.*?)\"").Groups[1].Value;
                            string title = Regex.Match(line, "\"title\":\\s*\"(.*?)\"").Groups[1].Value;
                            string url = Regex.Match(line, "\"url\":\\s*\"(.*?)\"").Groups[1].Value;
                            string duration = Regex.Match(line, "\"duration\":\\s*(\\d+)").Groups[1].Value;

                            // Giải mã unicode title
                            try { title = Regex.Unescape(title); } catch { }

                            // Nếu url rỗng (do flat-playlist chỉ trả về id), tự tạo url
                            if (string.IsNullOrEmpty(url) && !string.IsNullOrEmpty(id))
                            {
                                url = $"https://www.youtube.com/watch?v={id}";
                            }

                            if (!string.IsNullOrEmpty(url))
                            {
                                items.Add(new Views.PlaylistVideoItem
                                {
                                    IsSelected = true,
                                    Url = url,
                                    Title = string.IsNullOrEmpty(title) ? "Video không tên" : title,
                                    // Tự tạo link thumbnail từ ID (nhanh hơn chờ yt-dlp)
                                    ThumbnailUrl = !string.IsNullOrEmpty(id) ? $"https://i.ytimg.com/vi/{id}/mqdefault.jpg" : "",
                                    Duration = duration // Giây
                                });
                            }
                        }
                        catch { }
                    },
                    (err) => errorLog.AppendLine(err));

                if (items.Count == 0)
                    return new SimpleRunResult<List<Views.PlaylistVideoItem>>(false, "Không tìm thấy video hoặc playlist riêng tư.\n" + errorLog.ToString(), null);

                return new SimpleRunResult<List<Views.PlaylistVideoItem>>(true, null, items);
            }
            catch (Exception ex)
            {
                return new SimpleRunResult<List<Views.PlaylistVideoItem>>(false, ex.Message, null);
            }
        }
    }
}