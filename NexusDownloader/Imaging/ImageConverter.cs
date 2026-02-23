using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using System.IO;
using System.Threading.Tasks;
namespace NexusDownloader.Imaging
{
    public static class ImageConverter
    {
        public static async Task ConvertWebpToJpg(string webpPath)
        {
            string jpgPath = Path.ChangeExtension(webpPath, ".jpg");

            using (var image = await Image.LoadAsync(webpPath))
            {
                var encoder = new JpegEncoder
                {
                    Quality = 92
                };

                await image.SaveAsJpegAsync(jpgPath, encoder);
            }

            File.Delete(webpPath);
        }
    }
}