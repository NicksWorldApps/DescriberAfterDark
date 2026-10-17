using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImageDescriber.App;

/// <summary>An image ready for the model, plus facts about it for the status line and log.</summary>
internal sealed record PreparedImage(string Base64, int Width, int Height, string Format);

/// <summary>
/// Prepares any image for the model: shrinks it so its longest side is at most maxSide pixels
/// and converts it to JPEG. Works for very large images too, because big images are decoded
/// straight at the smaller size instead of being loaded in full first.
/// </summary>
internal static class ImagePrep
{
    public static PreparedImage Prepare(byte[] imageBytes, int maxSide)
    {
        var format = DetectFormat(imageBytes);

        // 1. Windows Imaging Component: reads JPEG, PNG, GIF, BMP, TIFF, and WebP/HEIC where Windows has
        //    those codecs, and can decode a huge image directly at the reduced size.
        try
        {
            return PrepareWithWic(imageBytes, maxSide, format);
        }
        catch
        {
            // Fall through to the classic decoder.
        }

        // 2. The classic Windows image library.
        try
        {
            using var input = new MemoryStream(imageBytes);
            using var source = Image.FromStream(input);
            return new PreparedImage(Convert.ToBase64String(EncodeJpeg(source, maxSide)), source.Width, source.Height, format);
        }
        catch
        {
            // Couldn't convert. Sending the original is better than nothing.
        }

        return new PreparedImage(Convert.ToBase64String(imageBytes), 0, 0, format + " (unconverted)");
    }

    public static PreparedImage Prepare(Image image, int maxSide) =>
        new(Convert.ToBase64String(EncodeJpeg(image, maxSide)), image.Width, image.Height, "copied image");

    private static PreparedImage PrepareWithWic(byte[] bytes, int maxSide, string format)
    {
        // Read just the size first, without decoding the pixels.
        int width, height;
        using (var probe = new MemoryStream(bytes))
        {
            var decoder = BitmapDecoder.Create(probe, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            width = decoder.Frames[0].PixelWidth;
            height = decoder.Frames[0].PixelHeight;
        }

        // Decode at the target size. For a huge picture this never holds the full-size pixels in memory.
        var image = new BitmapImage();
        using (var stream = new MemoryStream(bytes))
        {
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            if (Math.Max(width, height) > maxSide)
            {
                if (width >= height) image.DecodePixelWidth = maxSide;
                else image.DecodePixelHeight = maxSide;
            }
            image.EndInit();
        }
        image.Freeze();

        var flattened = FlattenOntoWhite(image);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(flattened));
        using var output = new MemoryStream();
        encoder.Save(output);
        return new PreparedImage(Convert.ToBase64String(output.ToArray()), width, height, format);
    }

    /// <summary>Turns transparent areas white (instead of black) and drops the alpha channel, ready for JPEG.</summary>
    private static BitmapSource FlattenOntoWhite(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);

        var rgb = new byte[w * h * 3];
        for (int i = 0, j = 0; i < pixels.Length; i += 4, j += 3)
        {
            int a = pixels[i + 3];
            rgb[j] = (byte)((pixels[i] * a + 255 * (255 - a)) / 255);
            rgb[j + 1] = (byte)((pixels[i + 1] * a + 255 * (255 - a)) / 255);
            rgb[j + 2] = (byte)((pixels[i + 2] * a + 255 * (255 - a)) / 255);
        }

        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr24, null, rgb, w * 3);
        result.Freeze();
        return result;
    }

    private static string DetectFormat(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8) return "JPEG";
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50) return "PNG";
        if (b.Length >= 4 && b[0] == 0x47 && b[1] == 0x49) return "GIF";
        if (b.Length >= 12 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "WebP";
        if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return "BMP";
        if (b.Length >= 4 && ((b[0] == 0x49 && b[1] == 0x49) || (b[0] == 0x4D && b[1] == 0x4D))) return "TIFF";
        return "unknown format";
    }

    private static byte[] EncodeJpeg(Image source, int maxSide)
    {
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(System.Drawing.Color.White); // transparent areas become white instead of black
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, 0, 0, width, height); // GIFs use their first frame
        }

        var jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);

        using var output = new MemoryStream();
        bitmap.Save(output, jpeg, parameters);
        return output.ToArray();
    }
}
