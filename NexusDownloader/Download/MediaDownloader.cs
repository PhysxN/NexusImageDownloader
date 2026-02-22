using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NexusDownloader.Download
{
    public class MediaDownloader
    {
        private readonly SemaphoreSlim _limiter;

        public int Downloaded;

        public MediaDownloader(SemaphoreSlim limiter)
        {
            _limiter = limiter;
        }

        public async Task Download(HttpClient http, string url, string file)
        {
            await _limiter.WaitAsync();

            try
            {
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);

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

                using var stream = await resp.Content.ReadAsStreamAsync();
                using var fs = File.Create(Path.ChangeExtension(file, ext));
                await stream.CopyToAsync(fs);

                Interlocked.Increment(ref Downloaded);
            }
            finally
            {
                _limiter.Release();
            }
        }
    }
}