using NexusDownloader.Core;
using NexusDownloader.Download;
using NexusDownloader.GraphQL;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace NexusDownloader.UI
{
    public partial class MainWindow : Window
    {
        private NexusSession _session;
        private NexusMediaService _media;
        private MediaDownloader _downloader;

        private int _totalImages;
        private int _activeDownloads;

        private readonly SemaphoreSlim _downloadLimiter = new SemaphoreSlim(60);
        private readonly SemaphoreSlim _gqlLimiter = new SemaphoreSlim(6);

        public MainWindow()
        {
            InitializeComponent();

            _session = new NexusSession(web);
            _media = new NexusMediaService(_gqlLimiter);
            _downloader = new MediaDownloader(_downloadLimiter);
        }

        private async void Open_Click(object sender, RoutedEventArgs e)
        {
            await _session.InitAsync();

            web.Source = new Uri(BuildMediaUrl(1));

            Log("Login if needed, then press ULTRA FAST.");
        }

        private async void UltraFast_Click(object sender, RoutedEventArgs e)
        {
            _totalImages = 0;
            _downloader.Downloaded = 0;

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

            string folder = PrepareFolder();
            var existing = GetExistingFiles(folder);

            await ProcessAllMedia(http, authorId, folder, existing);

            Log("need download = " + _totalImages);

            await WaitDownloadsFinish();

            sw.Stop();

            Log($"DONE {_downloader.Downloaded} in {sw.Elapsed:mm\\:ss\\.fff}");
            Log($"speed: {(_downloader.Downloaded / sw.Elapsed.TotalSeconds):F2} img/sec");
        }

        private async Task ProcessAllMedia(System.Net.Http.HttpClient http, string authorId, string folder, HashSet<string> existing)
        {
            int count = 20;
            int offset = 0;
            var known = new HashSet<string>();

            int emptyPages = 0;

            while (true)
            {
                var pages = await LoadPagesBatch(http, offset, count, authorId);

                if (pages.All(p => string.IsNullOrEmpty(p) || p.Length < 200))
                {
                    emptyPages++;
                    if (emptyPages >= 2)
                        break;
                }
                else
                {
                    emptyPages = 0;
                }

                foreach (var json in pages)
                    ExtractAndQueueDownloads(http, json, folder, known, existing);

                offset += count * 6;
            }
        }

        private async Task<string?[]> LoadPagesBatch(System.Net.Http.HttpClient http, int offset, int count, string authorId)
        {
            var batch = new List<Task<string?>>();
            string gameId = GameIdBox.Text?.Trim();

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

        private async Task DownloadWrapper(System.Net.Http.HttpClient http, string url, string file)
        {
            try
            {
                await _downloader.Download(http, url, file);

                if (_downloader.Downloaded % 50 == 0)
                    Log($"saved {_downloader.Downloaded}/{_totalImages}");
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

        private string BuildMediaUrl(int page)
        {
            string profileUrl = UrlBox.Text.Split('?')[0];

            if (!profileUrl.EndsWith("/media"))
                profileUrl += "/media";

            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(GameIdBox.Text))
                parts.Add("gameId=" + GameIdBox.Text.Trim());

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
    }
}