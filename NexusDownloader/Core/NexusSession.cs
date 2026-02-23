using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace NexusDownloader.Core
{
    public class NexusSession
    {
        private readonly WebView2 _web;

        public NexusSession(WebView2 web)
        {
            _web = web;
        }

        public async Task InitAsync()
        {
            if (_web.CoreWebView2 is not null)
                return;

            var profile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NexusDownloaderWebView");

            var env = await CoreWebView2Environment.CreateAsync(null, profile);

            await _web.EnsureCoreWebView2Async(env);

            var core = _web.CoreWebView2
                ?? throw new InvalidOperationException("WebView2 init failed");

            core.NewWindowRequested += (s, ev) =>
            {
                ev.Handled = true;
                core.Navigate(ev.Uri);
            };
        }

        public async Task<HttpClient> CreateHttpClientAsync()
        {
            if (_web.CoreWebView2 == null)
                throw new InvalidOperationException("WebView2 is not initialized");

            var cookies = await _web.CoreWebView2.CookieManager.GetCookiesAsync(null);

            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxConnectionsPerServer = 200,
                UseCookies = true,
                CookieContainer = new CookieContainer()
            };

            foreach (var c in cookies)
            {
                try
                {
                    handler.CookieContainer.Add(new Cookie(c.Name, c.Value, c.Path, c.Domain));
                }
                catch { }
            }

            var http = new HttpClient(handler);
            http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
            http.DefaultRequestVersion = HttpVersion.Version20;

            return http;
        }

        public async Task WaitDomReady()
        {
            if (_web.CoreWebView2 == null)
                throw new InvalidOperationException("WebView2 not initialized");
            while (true)
            {
                var state = await _web.ExecuteScriptAsync("document.readyState");
                if (state.Contains("complete"))
                    return;

                await Task.Delay(120);
            }
        }

        public async Task WaitAvatar()
        {
            if (_web.CoreWebView2 == null)
                throw new InvalidOperationException("WebView2 not initialized");
            for (int i = 0; i < 120; i++)
            {
                var exists = await _web.ExecuteScriptAsync(
                    "document.querySelector('img[src*=\"avatars.nexusmods.com\"]') !== null");

                if (exists.Contains("true"))
                    return;

                await Task.Delay(150);
            }
        }

        public async Task<string?> DetectAuthorId()
        {
            if (_web.CoreWebView2 == null)
                throw new InvalidOperationException("WebView2 not initialized");
            var raw = await _web.ExecuteScriptAsync(@"
                Array.from(document.querySelectorAll('img[src*=""avatars.nexusmods.com""]'))
                .map(i => i.src).join('|')");

            raw = raw.Trim('"').Replace("\\/", "/");

            var matches = Regex.Matches(raw, @"avatars\.nexusmods\.com\/(\d+)\/");

            if (matches.Count == 0)
                return null;

            return matches
                .Cast<Match>()
                .GroupBy(m => m.Groups[1].Value)
                .OrderByDescending(g => g.Count())
                .First().Key;
        }
    }
}