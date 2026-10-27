using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Feige.App.Services;

public static class ImagePreview
{
    public static async Task<BitmapImage> LoadAsync(string path, int longestEdge)
    {
        using var stream = await (await StorageFile.GetFileFromPathAsync(path)).OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        double scale = Math.Min(1, longestEdge / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var bitmap = new BitmapImage
        {
            DecodePixelWidth = (int)Math.Max(1, decoder.PixelWidth * scale),
            DecodePixelHeight = (int)Math.Max(1, decoder.PixelHeight * scale)
        };
        stream.Seek(0);
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}
