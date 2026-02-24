using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using NexusDownloader.Imaging;

namespace NexusDownloader.Download
{
    public class MediaDownloader
    {
        private readonly SemaphoreSlim _limiter;
        public int Downloaded;
        private readonly string _tempFolder;
        private readonly SemaphoreSlim _convertLimiter = new SemaphoreSlim(4);
        private int _delayMs = 0;
        private int _maxDelayMs = 0;
        public int MaxDelay => _maxDelayMs;
        private int _slowResponses = 0;
        private int _fastResponses = 0;
        private readonly object _adaptiveLock = new object();
        public int CurrentDelay => _delayMs;

        public MediaDownloader(SemaphoreSlim limiter, string game, string author)
        {
            _limiter = limiter;

            _tempFolder = Path.Combine(AppContext.BaseDirectory, "Temp", game, author);

            try
            {
                if (Directory.Exists(_tempFolder))
                    Directory.Delete(_tempFolder, true);
            }
            catch { }

            Directory.CreateDirectory(_tempFolder);
        }



        public async Task Download(HttpClient http, string url, string file)
        {
            await _limiter.WaitAsync();
            bool limiterReleased = false;

            string? tempPath = null;
            string? finalTempFile = null;

            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        int delay = CurrentDelay;
                        if (delay > 0)
                            await Task.Delay(delay);

                        var sw = System.Diagnostics.Stopwatch.StartNew();

                        using var resp = await http.GetAsync(
                            url,
                            HttpCompletionOption.ResponseHeadersRead);

                        sw.Stop();

                        lock (_adaptiveLock)
                        {
                            if (sw.ElapsedMilliseconds > 1200)
                            {
                                _slowResponses++;
                                _fastResponses = 0;
                            }
                            else
                            {
                                _fastResponses++;
                                _slowResponses = 0;
                            }

                            if (_slowResponses >= 4)
                            {
                                _delayMs = Math.Min(_delayMs + 25, 250);
                                _maxDelayMs = Math.Max(_maxDelayMs, _delayMs);
                                _slowResponses = 0;
                            }

                            if (_fastResponses >= 6)
                            {
                                _delayMs = Math.Max(_delayMs - 10, 0);
                                _fastResponses = 0;
                            }
                        }

                        if (!resp.IsSuccessStatusCode)
                            continue;

                        var type = resp.Content.Headers.ContentType?.MediaType;
                        if (type == null || !type.StartsWith("image"))
                            continue;

                        string ext = type switch
                        {
                            "image/webp" => ".webp",
                            "image/png" => ".png",
                            "image/jpeg" => ".jpg",
                            _ => ".jpg"
                        };

                        finalTempFile = Path.Combine(_tempFolder,
                            Path.GetFileNameWithoutExtension(file) + ext);

                        tempPath = finalTempFile + ".tmp";

                        using var stream = await resp.Content.ReadAsStreamAsync();
                        using (var fs = File.Create(tempPath))
                            await stream.CopyToAsync(fs);

                        if (new FileInfo(tempPath).Length < 4000)
                        {
                            File.Delete(tempPath);
                            continue;
                        }

                        File.Move(tempPath, finalTempFile, true);

                        // освобождаем limiter сразу
                        _limiter.Release();
                        limiterReleased = true;

                        Forget(ProcessAfterDownload(finalTempFile, file, ext));

                        int d = Interlocked.Increment(ref Downloaded);

                        if (d % 100 == 0)
                            System.Diagnostics.Debug.WriteLine($"adaptive delay = {_delayMs} ms");

                        return;
                    }
                    catch
                    {
                        if (tempPath != null && File.Exists(tempPath))
                            File.Delete(tempPath);
                    }

                    await Task.Delay(200 + Random.Shared.Next(150));
                }
            }
            finally
            {
                if (!limiterReleased)
                    _limiter.Release();
            }
        }

        private async Task ProcessAfterDownload(string tempFile, string targetBase, string ext)
        {
            try
            {
                if (ext == ".webp")
                {
                    await _convertLimiter.WaitAsync();

                    try
                    {
                        await ImageConverter.ConvertWebpToJpg(tempFile);

                        string finalPath = Path.ChangeExtension(targetBase, ".jpg");
                        File.Move(Path.ChangeExtension(tempFile, ".jpg"), finalPath, true);

                        var jpg = Path.ChangeExtension(tempFile, ".jpg");

                        if (File.Exists(jpg))
                            File.Move(jpg, finalPath, true);
                    }
                    finally
                    {
                        _convertLimiter.Release();
                    }
                }
                else
                {
                    string finalPath = Path.ChangeExtension(targetBase, ext);
                    File.Move(tempFile, finalPath, true);
                }
            }
            catch
            {
                // temp просто останется
            }
        }

        private static void Forget(Task task)
        {
            _ = task.ContinueWith(
                t => { var _ = t.Exception; },
                TaskContinuationOptions.OnlyOnFaulted);
        }

    }
}