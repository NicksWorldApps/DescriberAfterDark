using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ImageDescriber.App;

/// <summary>An image ready for the model, plus facts about it for the status line and log.</summary>
internal sealed record PreparedImage(string Base64, int Width, int Height, string Format);

/// <summary>Prepares images for the model: scales big images down and converts everything to JPEG.</summary>
internal static class ImagePrep
{
    public static PreparedImage Prepare(byte[] imageBytes, int maxSide)
    {
        var format = DetectFormat(imageBytes);

        // 1. Windows' classic image library: JPEG, PNG, GIF, BMP.
        try
        {
            using var input = new MemoryStream(imageBytes);
            using var source = Image.FromStream(input);
            return new PreparedImage(Convert.ToBase64String(EncodeJpeg(source, maxSide)), source.Width, source.Height, format);
        }
        catch
        {
            // Fall through to the newer decoder.
        }

        // 2. Windows Imaging Component, which also reads WebP and HEIC when Windows has those codecs (Windows 10 and 11 do).
        try
        {
            using var input = new MemoryStream(imageBytes);
            var decoder = System.Windows.Media.Imaging.BitmapDecoder.Create(input,
                System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
            using var png = new MemoryStream();
            encoder.Save(png);
            png.Position = 0;
            using var source = Image.FromStream(png);
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

    private static string DetectFormat(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8) return "JPEG";
        if (b.Length >= 4 && b[0] == 0x89 && b[1] == 0x50) return "PNG";
        if (b.Length >= 4 && b[0] == 0x47 && b[1] == 0x49) return "GIF";
        if (b.Length >= 12 && b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return "WebP";
        if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return "BMP";
        return "unknown format";
    }

    private static byte[] EncodeJpeg(Image source, int maxSide)
    {
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));

        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White); // transparent areas become white instead of black
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
