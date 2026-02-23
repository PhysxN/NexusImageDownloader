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
        private NexusSession _session;
        private NexusMediaService _media;
        private MediaDownloader? _downloader;

        private int _totalImages;
        private int _activeDownloads;
        

        private readonly SemaphoreSlim _downloadLimiter = new SemaphoreSlim(32);
        private readonly SemaphoreSlim _gqlLimiter = new SemaphoreSlim(6);
        private NexusGamesService _games = new NexusGamesService();

        public MainWindow()
        {
            InitializeComponent();

            _session = new NexusSession(web);
            _media = new NexusMediaService(_gqlLimiter);
        }

        private async void Open_Click(object sender, RoutedEventArgs e)
        {
            await _session.InitAsync();

            web.Source = new Uri(BuildMediaUrl(1));

            Log("Login if needed...");

            await _session.WaitAvatar();
            await _session.WaitDomReady();

            var authorId = await _session.DetectAuthorId();

            if (string.IsNullOrEmpty(authorId))
            {
                Log("author detect fail");
                return;
            }

            Log("author = " + authorId);

            var http = await _session.CreateHttpClientAsync();

            try
            {
                await _games.LoadGameNames(http);

                var list = await _games.LoadGames(http, authorId);

                GameBox.Items.Clear();

                GameBox.Items.Add(new Models.GameFacet
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
            SetUiEnabled(false);

            try
            {
                _totalImages = 0;

                var sw = Stopwatch.StartNew();

                var http = await _session.CreateHttpClientAsync();

                await _session.WaitAvatar();
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

                _downloader = new MediaDownloader(_downloadLimiter, game, nick);
                _downloader.Downloaded = 0;

                string folder = Path.Combine(AppContext.BaseDirectory, "Images", game, nick);
                Directory.CreateDirectory(folder);

                var existing = GetExistingFiles(folder);

                await ProcessAllMedia(http, authorId, folder, existing);

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

        private async Task ProcessAllMedia(System.Net.Http.HttpClient http, string authorId, string folder, HashSet<string> existing)
        {
            int count = 20;
            int offset = 0;
            var known = new HashSet<string>();

            
            while (true)
            {
                var pages = await LoadPagesBatch(http, offset, count, authorId);

                if (pages.All(p => p == null || !p.Contains("thumbnailUrl")))
                    break;

                foreach (var json in pages)
                    ExtractAndQueueDownloads(http, json, folder, known, existing);

                offset += count * 6;
            }
        }

        private async Task<string?[]> LoadPagesBatch(HttpClient http, int offset, int count, string authorId)
        {
            var batch = new List<Task<string?>>();

            string? gameId = (GameBox.SelectedItem as GameFacet)?.Id;

            for (int i = 0; i < 6; i++)
                batch.Add(_media.GetMediaPage(http, offset + i * count, count, authorId, gameId));

            var pages = await Task.WhenAll(batch);

            Log("pages received: " + pages.Length);
            Log("sample json len: " + pages.FirstOrDefault()?.Length);

            return pages;
        }

        private void ExtractAndQueueDownloads(System.Net.Http.HttpClient http, string? json, string folder,
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

                _ = DownloadWrapper(http, url, Path.Combine(folder, id));
            }
        }

        private async Task DownloadWrapper(HttpClient http, string url, string file)
        {
            var downloader = _downloader;

            if (downloader == null)
                return;

            try
            {
                await downloader.Download(http, url, file);

                if (downloader.Downloaded % 50 == 0)
                    Log($"saved {downloader.Downloaded}/{_totalImages}");
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

    }
}