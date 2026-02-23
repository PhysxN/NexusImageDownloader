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
        private readonly string _tempFolder;
        private readonly SemaphoreSlim _convertLimiter = new SemaphoreSlim(4);

        public int Downloaded;

        public MediaDownloader(SemaphoreSlim limiter)
        {
            _limiter = limiter;

            _tempFolder = Path.Combine(Environment.CurrentDirectory, "Temp");

            try
            {
                if (Directory.Exists(_tempFolder))
                    Directory.Delete(_tempFolder, true);
            }
            catch
            {
                // если файлы заняты — просто игнорируем
            }

            Directory.CreateDirectory(_tempFolder);
        }



        public async Task Download(HttpClient http, string url, string file)
        {
            await _limiter.WaitAsync();

            string? tempPath = null;
            string? finalTempFile = null;

            try
            {
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    try
                    {
                        using var resp = await http.GetAsync(
                            url,
                            HttpCompletionOption.ResponseHeadersRead);

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

                        // ✔ освобождаем download limiter раньше
                        _limiter.Release();

                        // ✔ конвертация отдельно
                        _ = ProcessAfterDownload(finalTempFile, file, ext);

                        Interlocked.Increment(ref Downloaded);
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
                if (_limiter.CurrentCount == 0)
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
                        string jpgTemp = Path.ChangeExtension(tempFile, ".jpg");

                        await ImageConverter.ConvertWebpToJpg(tempFile);

                        string finalPath = Path.ChangeExtension(targetBase, ".jpg");
                        File.Move(jpgTemp, finalPath, true);
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
                // если что-то пошло не так — temp просто останется
            }
        }

    }
}