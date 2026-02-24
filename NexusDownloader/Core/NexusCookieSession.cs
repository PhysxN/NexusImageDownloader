using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Linq;

namespace NexusDownloader.Core
{
    public class NexusCookieSession
    {
        private readonly WebView2 _web;
        private bool _initialized;

        public NexusCookieSession(WebView2 web)
        {
            _web = web;
        }        

        public async Task<HttpClient> CreateHttpClientAsync()
        {
            if (_web.CoreWebView2 == null)
                throw new InvalidOperationException("WebView not initialized");

            var cookies = await _web.CoreWebView2.CookieManager  .GetCookiesAsync("https://www.nexusmods.com");

            var handler = new SocketsHttpHandler
            {
                UseCookies = true,
                CookieContainer = new CookieContainer(),
                AutomaticDecompression = DecompressionMethods.None
            };

            foreach (var c in cookies)
            {
                try
                {
                    var domain = c.Domain.StartsWith(".") ? c.Domain : "." + c.Domain;

                    handler.CookieContainer.Add(
                        new Cookie(c.Name, c.Value, c.Path, domain));
                }
                catch { }
            }

            var http = new HttpClient(handler);
            var ua = await _web.ExecuteScriptAsync("navigator.userAgent");
            ua = ua.Trim('"');

            http.DefaultRequestHeaders.UserAgent.ParseAdd(ua);

            http.DefaultRequestHeaders.Referrer = new Uri("https://www.nexusmods.com/");
            http.DefaultRequestVersion = HttpVersion.Version20;

            return http;
        }

        public async Task<bool> IsLoggedAsync()
        {
            if (_web.CoreWebView2 == null)
                return false;

            var cookies = await _web.CoreWebView2.CookieManager    .GetCookiesAsync("https://www.nexusmods.com");

            bool hasSession = cookies.Any(c =>
                                    c.Name == "nexusmods_session" ||
                                    c.Name == "cf_clearance");

            if (!hasSession)
                return false;            

            try
            {
                return cookies.Any(c =>
                                c.Name == "nexusmods_session" ||
                                c.Name == "cf_clearance");
            }
            catch
            {
                return false;
            }
        }

    }
}