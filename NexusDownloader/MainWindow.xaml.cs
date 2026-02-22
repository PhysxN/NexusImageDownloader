using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace NexusDownloader
{
    public partial class MainWindow : Window
    {      
        
        private int downloaded = 0;
        private int totalImages = 0;
        private int activeDownloads = 0;
        private SemaphoreSlim limiter = new SemaphoreSlim(120);
        private SemaphoreSlim gqlLimiter = new SemaphoreSlim(16);

        public MainWindow()
        {
            InitializeComponent();            
        }

        private async void Open_Click(object sender, RoutedEventArgs e)
        {
            // создаём persistent профиль (логин сохранится)
            var profile = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "NexusDownloaderWebView");

            var env = await CoreWebView2Environment.CreateAsync(null, profile);
            await web.EnsureCoreWebView2Async(env);

            // разрешаем popup логина
            web.CoreWebView2.NewWindowRequested += (s, ev) =>
            {
                ev.Handled = true;
                web.CoreWebView2.Navigate(ev.Uri);
            };

            web.Source = new Uri(BuildMediaUrl(1));

            Log("Login if needed, then press Download ALL.");
        }

        private async Task<HttpClient> CreateHttpFromWebView()
        {
            var cookies = await web.CoreWebView2.CookieManager.GetCookiesAsync(null);

            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                MaxConnectionsPerServer = 200,
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(20),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };

            foreach (var c in cookies)
            {
                try
                {
                    handler.CookieContainer.Add(new System.Net.Cookie(
                        c.Name, c.Value, c.Path, c.Domain));
                }
                catch { }
            }

            var http = new HttpClient(handler);

            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
            http.DefaultRequestVersion = HttpVersion.Version20;
            http.Timeout = TimeSpan.FromMinutes(10);

            return http;
        }

        private async Task WaitProfileAvatar()
        {
            for (int i = 0; i < 120; i++)
            {
                var exists = await web.ExecuteScriptAsync(@"
            document.querySelector('img[src*=""avatars.nexusmods.com""]') !== null
        ");

                if (exists.Contains("true"))
                    return;

                await Task.Delay(150);
            }

            Log("avatar wait timeout");
        }

        private async Task WaitDomReady()
        {
            while (true)
            {
                var state = await web.ExecuteScriptAsync("document.readyState");
                if (state.Contains("complete"))
                    return;

                await Task.Delay(120);
            }
        }

        private async void UltraFast_Click(object sender, RoutedEventArgs e)
        {
            totalImages = 0;
            downloaded = 0;

            var sw = Stopwatch.StartNew();

            var http = await CreateHttpFromWebView();

            await WaitProfileAvatar();
            await WaitDomReady();
            var authorId = await DetectAuthorId();
            

            if (string.IsNullOrEmpty(authorId))
            {
                Log("author detect fail");
                return;
            }

            Log("author = " + authorId);

            string folder = PrepareFolder();
            var existing = GetExistingFiles(folder);

            await ProcessAllMedia(http, authorId, folder, existing);

            Log("need download = " + totalImages);

            await WaitDownloadsFinish();

            sw.Stop();

            Log($"DONE {downloaded} in {sw.Elapsed:mm\\:ss\\.fff}");
            Log($"speed: {(downloaded / sw.Elapsed.TotalSeconds):F2} img/sec");
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

        private async Task ProcessAllMedia(HttpClient http, string authorId, string folder, HashSet<string> existing)
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

        private async Task<string?[]> LoadPagesBatch(HttpClient http, int offset, int count, string authorId)
        {
            var batch = new List<Task<string?>>();

            for (int i = 0; i < 6; i++)
                batch.Add(GetMediaPage(http, offset + i * count, count, authorId));

            var pages = await Task.WhenAll(batch);

            Log("pages received: " + pages.Length);
            Log("sample json len: " + pages.FirstOrDefault()?.Length);

            return pages;
        }

        private void ExtractAndQueueDownloads(
    HttpClient http,
    string? json,
    string folder,
    HashSet<string> known,
    HashSet<string> existing)
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

                Interlocked.Increment(ref totalImages);
                existing.Add(id);

                Interlocked.Increment(ref activeDownloads);
                _ = DownloadImage(http, url, Path.Combine(folder, id));
            }
        }

        private async Task WaitDownloadsFinish()
        {
            while (Volatile.Read(ref activeDownloads) > 0)
                await Task.Delay(120);
        }

        private async Task<string?> DetectAuthorId()
        {
            var raw = await web.ExecuteScriptAsync(@"
        Array.from(document.querySelectorAll('img[src*=""avatars.nexusmods.com""]'))
            .map(i => i.src)
            .join('|')
    ");

            raw = raw.Trim('"').Replace("\\/", "/");

            if (string.IsNullOrWhiteSpace(raw))
                return null;

            var matches = Regex.Matches(raw, @"avatars\.nexusmods\.com\/(\d+)\/");

            if (matches.Count == 0)
                return null;

            return matches
                .Cast<Match>()
                .GroupBy(m => m.Groups[1].Value)
                .OrderByDescending(g => g.Count())
                .First()
                .Key;
        }

        private async Task DownloadImage(HttpClient http, string url, string file)
        {
            await limiter.WaitAsync();

            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

                using var resp = await http.GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cts.Token);

                if (!resp.IsSuccessStatusCode)
                    return;

                var type = resp.Content.Headers.ContentType?.MediaType;

                string ext = type switch
                {
                    "image/webp" => ".webp",
                    "image/png" => ".png",
                    "image/jpeg" => ".jpg",
                    _ => ".jpg"
                };

                string filePath = Path.ChangeExtension(file, ext);

                // Добавляем таймаут и для чтения стрима
                using var stream = await resp.Content.ReadAsStreamAsync();
                using var fs = File.Create(filePath);

                // Копируем с таймаутом через отдельный CTS
                using var copyCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await stream.CopyToAsync(fs, 256 * 1024, copyCts.Token);

                int d = Interlocked.Increment(ref downloaded);

                if (d % 20 == 0)
                    Log($"saved {d}/{totalImages}");
            }
            catch (OperationCanceledException)
            {
                
            }
            catch (Exception ex)
            {
                // Логируем другие ошибки
                Log($"Error downloading {url}: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref activeDownloads);
                limiter.Release();
            }
        }

        private async Task<string?> GetMediaPage(HttpClient http, int offset, int count, string authorId)
        {
            await gqlLimiter.WaitAsync();

            try
            {
                string facets = string.IsNullOrWhiteSpace(GameIdBox.Text)
                    ? "null"
                    : @"{""gameId"":[""" + GameIdBox.Text.Trim() + @"""]}";

                string body = @"{
  ""operationName"":""ProfileMedia"",
  ""variables"":{
    ""count"":" + count + @",
    ""facets"":" + facets + @",
    ""filter"":{
        ""mediaStatus"":[{""op"":""EQUALS"",""value"":""published""}],
        ""owner"":[{""op"":""EQUALS"",""value"":""" + authorId + @"""}]
    },
    ""offset"":" + offset + @",
    ""sort"":{""createdAt"":{""direction"":""DESC""}}
  },
  ""query"":""query ProfileMedia($count:Int,$facets:MediaFacet,$filter:MediaSearchFilter,$offset:Int,$sort:[MediaSearchSort!]){media(count:$count,facets:$facets,filter:$filter,offset:$offset,sort:$sort,viewUserBlockedContent:true){nodes{... on Image{thumbnailUrl} ... on SupporterImage{thumbnailUrl}}}}""
}";

                using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

                var resp = await http.PostAsync(
                    "https://api-router.nexusmods.com/graphql",
                    content);

                return await resp.Content.ReadAsStringAsync();
            }
            catch
            {
                return null;
            }
            finally
            {
                gqlLimiter.Release();
            }
        }



        private string BuildMediaUrl(int page)
        {
            string profileUrl = UrlBox.Text.Split('?')[0];

            if (!profileUrl.EndsWith("/media"))
                profileUrl += "/media";

            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(GameIdBox.Text))
                parts.Add("gameId=" + GameIdBox.Text.Trim());

            // ВСЕГДА только изображения
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