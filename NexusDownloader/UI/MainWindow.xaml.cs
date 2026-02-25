using NexusDownloader.Core;
using NexusDownloader.Download;
using NexusDownloader.GraphQL;
using NexusDownloader.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NexusDownloader.UI
{
    public partial class MainWindow : Window
    {
        private readonly NexusSession _session;
        private NexusMediaService _media;
        private MediaDownloader? _downloader;

        private int _totalImages;
        private int _activeDownloads;


        private readonly AdaptiveLimiter _adaptiveLimiter = new AdaptiveLimiter(32, 8, 48);
        private readonly SemaphoreSlim _gqlLimiter = new SemaphoreSlim(6);
        private NexusGamesService _games = new NexusGamesService();
        private NexusCookieSession _cookieSession;
        private bool _loginHandled;
        private HttpClient? _http;
        private DateTime _lastClientReset = DateTime.MinValue;
        private int _burstGuard;
        private int _latencyGuard;
        private int _clientResetGuard;
        private int _burstCounter;

        public MainWindow()
        {
            InitializeComponent();

            _session = new NexusSession(web);
            _media = new NexusMediaService(_gqlLimiter);
            _cookieSession = new NexusCookieSession(web);

            web.NavigationCompleted += Web_NavigationCompleted;
        }

        private async void Open_Click(object sender, RoutedEventArgs e)
        {
            _loginHandled = false;

            await _session.InitAsync();

            if (!await _cookieSession.IsLoggedAsync())
            {
                Log("Login required — please login in the embedded browser.");

                web.Source = new Uri("https://users.nexusmods.com/auth/sign_in");
                return;
            }

            Log("Logged in.");

            web.Source = new Uri(BuildMediaUrl(1));

            Log("Loading profile...");
            await Task.Delay(500);
            
            await _session.WaitDomReady();

            var authorId = await _session.DetectAuthorId();

            if (string.IsNullOrEmpty(authorId))
            {
                Log("author detect fail");
                return;
            }

            Log("author = " + authorId);

            _http ??= await _cookieSession.CreateHttpClientAsync();
            var http = _http!;

            try
            {
                await _games.LoadGameNames(http);
                var list = await _games.LoadGames(http, authorId);

                GameBox.Items.Clear();

                GameBox.Items.Add(new GameFacet
                {
                    Id = null,
                    Name = $"All games ({list.Sum(x => x.Count)})"
                });

                foreach (var g in list)
                    GameBox.Items.Add(g);

                GameBox.SelectedIndex = 0;

                Log("games detected: " + list.Count);
            }
            catch
            {
                Log("games load failed");
            }

            UrlBox.Text = NormalizeProfileInput(UrlBox.Text);
            Log("Select game, then press ULTRA FAST.");
        }

        private string SafeFolder(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return "_Unknown";

            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            return name.Trim();
        }

        private async void UltraFast_Click(object sender, RoutedEventArgs e)
        {
            await _session.InitAsync();            

            if (!await _cookieSession.IsLoggedAsync())
            {
                Log("Login required.");
                return;
            }

            SetUiEnabled(false);

            try
            {
                _totalImages = 0;

                var sw = Stopwatch.StartNew();

                _http ??= await _cookieSession.CreateHttpClientAsync();
                
                await Task.Delay(500);
                
                await _session.WaitDomReady();

                var authorId = await _session.DetectAuthorId();

                if (string.IsNullOrEmpty(authorId))
                {
                    Log("author detect fail");
                    return;
                }

                Log("author = " + authorId);

                string nick = SafeFolder(NormalizeProfileInput(UrlBox.Text));

                var selected = GameBox.SelectedItem as GameFacet;
                string game = SafeFolder(selected?.Name ?? "_AllGames");
                ClearTemp(game, nick);

                _downloader = new MediaDownloader(_adaptiveLimiter.Semaphore, game, nick);
                _downloader.Downloaded = 0;

                string folder = Path.Combine(AppContext.BaseDirectory, "Images", game, nick);
                Directory.CreateDirectory(folder);

                var existing = GetExistingFiles(folder);

                await ProcessAllMedia(authorId, folder, existing);

                Log("need download = " + _totalImages);

                await WaitDownloadsFinish();

                sw.Stop();

                Log($"DONE {_downloader?.Downloaded ?? 0} in {sw.Elapsed:mm\\:ss\\.fff}");
                Log($"speed: {((_downloader?.Downloaded ?? 0) / sw.Elapsed.TotalSeconds):F2} img/sec");
                if (_downloader != null)
                    Log($"adaptive delay peak = {_downloader.MaxDelay} ms");
            }
            finally
            {
                SetUiEnabled(true);
            }
        }

        private async Task ProcessAllMedia(string authorId, string folder, HashSet<string> existing)
        {
            int count = 20;
            int offset = 0;
            var known = new HashSet<string>();

            
            while (true)
            {
                var pages = await LoadPagesBatch(offset, count, authorId);

                if (pages.All(p => p == null || !p.Contains("thumbnailUrl")))
                    break;

                foreach (var json in pages)
                    ExtractAndQueueDownloads(json, folder, known, existing);

                offset += count * 6;
            }
        }

        private async Task<string?[]> LoadPagesBatch(int offset, int count, string authorId)
        {
            if (_http == null)
                return Array.Empty<string?>();

            var batch = new List<Task<string?>>();
            string? gameId = (GameBox.SelectedItem as GameFacet)?.Id;

            for (int i = 0; i < 6; i++)
                batch.Add(_media.GetMediaPage(_http, offset + i * count, count, authorId, gameId));

            var pages = await Task.WhenAll(batch);

            Log("pages received: " + pages.Length);
            Log("sample json len: " + pages.FirstOrDefault()?.Length);

            return pages;
        }

        private void ExtractAndQueueDownloads(string? json, string folder,
    HashSet<string> known, HashSet<string> existing)
        {
            if (string.IsNullOrEmpty(json))
                return;

            int idx = 0;

            while (true)
            {
                idx = json.IndexOf("\"thumbnailUrl\":\"", idx, StringComparison.Ordinal);
                if (idx == -1)
                    break;

                idx += 16;

                int end = json.IndexOf('"', idx);
                if (end == -1)
                    break;

                string thumb = json.Substring(idx, end - idx).Replace("\\/", "/");
                idx = end;

                string url = thumb.Contains("/thumbnails/")
                    ? thumb.Replace("/thumbnails/", "/")
                    : thumb;

                if (!known.Add(url))
                    continue;

                string id = Path.GetFileNameWithoutExtension(
                    Path.GetFileName(url).Split('?')[0]);

                if (existing.Contains(id))
                    continue;

                Interlocked.Increment(ref _totalImages);
                existing.Add(id);

                Interlocked.Increment(ref _activeDownloads);

                _ = DownloadWrapper(url, Path.Combine(folder, id));
            }
        }

        private async Task DownloadWrapper(string url, string file)
        {
            if (_http == null)
                return;

            var downloader = _downloader;
            if (downloader == null)
                return;

            try
            {
                await Task.Delay(Random.Shared.Next(8, 35));
                await downloader.Download(_http!, url, file);

                if (downloader.Downloaded % 50 == 0)
                    Log($"saved {downloader.Downloaded}/{_totalImages}  delay={downloader.CurrentDelay}");
                int burst = GetDynamicBurst(downloader.CurrentDelay);
                int c = Interlocked.Increment(ref _burstCounter);

                if (c >= burst && Interlocked.Exchange(ref _burstGuard, 1) == 0)
                {
                    Interlocked.Exchange(ref _burstCounter, 0);

                    int pause =
                        downloader.CurrentDelay < 80 ? 1200 :
                        downloader.CurrentDelay < 150 ? 2200 :
                        downloader.CurrentDelay < 210 ? 3200 :
                        4500;

                    Log($"Burst pause {pause} ms (delay={downloader.CurrentDelay})");

                    await Task.Delay(pause + Random.Shared.Next(400));

                    _adaptiveLimiter.Update(downloader.CurrentDelay);
                    Interlocked.Exchange(ref _burstGuard, 0);
                }
                if (downloader.CurrentDelay >= 240)
                    await Task.Delay(1200 + Random.Shared.Next(400));
                if (downloader.IsLatencyStalled && Interlocked.Exchange(ref _latencyGuard, 1) == 0)
                {
                    Log($"Latency stall {downloader.LastLatency} ms → cooldown");
                    await Task.Delay(3500 + Random.Shared.Next(1500));
                    Interlocked.Exchange(ref _latencyGuard, 0);
                }

                // глобальный анти-stall cooldown + защита от частых reset
                if (downloader.CurrentDelay >= 240 &&
    Volatile.Read(ref _activeDownloads) <= 2 &&
    Interlocked.Exchange(ref _clientResetGuard, 1) == 0)
                {
                    try
                    {
                        Log("CDN cooldown...");
                        await Task.Delay(5000);

                        await RecreateClient(_http!);
                        _lastClientReset = DateTime.UtcNow;
                    }
                    finally
                    {
                        Interlocked.Exchange(ref _clientResetGuard, 0);
                    }
                }

                _adaptiveLimiter.Update(downloader.CurrentDelay);
            }
            finally
            {
                Interlocked.Decrement(ref _activeDownloads);
            }
        }

        private async Task WaitDownloadsFinish()
        {
            while (Volatile.Read(ref _activeDownloads) > 0)
                await Task.Delay(120);
        }

        private string PrepareFolder()
        {
            string folder = Path.Combine(Environment.CurrentDirectory, "Images");
            Directory.CreateDirectory(folder);
            return folder;
        }

        private async Task<HttpClient> RecreateClient(HttpClient old)
        {
            await Task.Delay(300);
            _ = Task.Run(async () =>
            {
                await Task.Delay(15000);
                try { old.Dispose(); } catch { }
            });

            Log("Recreating HttpClient...");

            _http = await _cookieSession.CreateHttpClientAsync();
            return _http;
        }

        private HashSet<string> GetExistingFiles(string folder)
        {
            return new HashSet<string>(
                Directory.GetFiles(folder)
                    .Select(f => Path.GetFileNameWithoutExtension(f)),
                StringComparer.OrdinalIgnoreCase);
        }

        private string NormalizeProfileInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return "";

            input = input.Trim();

            // если вставили полный URL
            var m = System.Text.RegularExpressions.Regex.Match(
                input,
                @"profile\/([^\/\?\#]+)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (m.Success)
                return m.Groups[1].Value;

            // если вставили URL без protocol
            if (input.Contains("nexusmods.com"))
            {
                var parts = input.Split('/');
                return parts.LastOrDefault() ?? input;
            }

            // иначе считаем, что это ник
            return input;
        }

        private int GetDynamicBurst(int delay)
        {
            if (delay < 80) return 200;
            if (delay < 150) return 140;
            if (delay < 210) return 80;
            return 45;
        }

        private string BuildMediaUrl(int page)
        {
            string nick = NormalizeProfileInput(UrlBox.Text);

            if (string.IsNullOrWhiteSpace(nick))
                return "";

            string profileUrl = $"https://www.nexusmods.com/profile/{nick}/media";

            var parts = new List<string>();

            var selected = GameBox.SelectedItem as GameFacet;
            if (!string.IsNullOrWhiteSpace(selected?.Id))
                parts.Add("gameId=" + selected.Id);

            parts.Add("mediaType=image");
            parts.Add("page=" + page);

            return profileUrl + "?" + string.Join("&", parts);
        }

        private void Log(string text)
        {
            Dispatcher.BeginInvoke(() =>
            {
                LogBox.AppendText(text + Environment.NewLine);
                LogBox.ScrollToEnd();
            });
        }

        private void SetUiEnabled(bool enabled)
        {
            Dispatcher.Invoke(() =>
            {
                OpenButton.IsEnabled = enabled;
                UltraFastButton.IsEnabled = enabled;
                UrlBox.IsEnabled = enabled;
                GameBox.IsEnabled = enabled;
            });
        }

        private async void Web_NavigationCompleted(object? sender, EventArgs e)
        {
            if (_loginHandled || web.Source == null)
                return;

            if (!await _cookieSession.IsLoggedAsync())
                return;

            _loginHandled = true;

            Log("Login detected.");
            web.Source = new Uri(BuildMediaUrl(1));
        }

        private void ClearTemp(string game, string author)
        {
            try
            {
                string temp = Path.Combine(AppContext.BaseDirectory, "Temp", game, author);

                if (Directory.Exists(temp))
                    Directory.Delete(temp, true);
            }
            catch { }
        }

    }
}