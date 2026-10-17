using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace ImageDescriber.Core;

public sealed record FetchedImage(byte[] Bytes, Uri Source);

public sealed class ImageFetchException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Downloads an image from a link without using the Reddit API.
/// Handles direct links (i.redd.it, preview.redd.it, i.imgur.com, any image URL),
/// Reddit's media viewer links (reddit.com/media?url=...), and pages that wrap
/// an image, by reading the page's og:image tag.
/// </summary>
public sealed partial class ImageFetcher : IDisposable
{
    private const long MaxBytes = 40L * 1024 * 1024;

    private readonly HttpClient _http;

    public ImageFetcher(HttpMessageHandler? handler = null)
    {
        handler ??= new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DescriberAfterDark/1.0 (accessibility tool for blind users)");
    }

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"<meta[^>]+(?:property|name)\s*=\s*[""'](?:og:image|twitter:image)(?::url)?[""'][^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex OgImageTagRegex();

    [GeneratedRegex(@"content\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ContentAttrRegex();

    /// <summary>Finds the first web link in a piece of text, such as clipboard contents.</summary>
    public static string? FindUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = UrlRegex().Match(text);
        return match.Success ? match.Value.TrimEnd('.', ',', ')', ']', ';') : null;
    }

    /// <summary>
    /// Turns Reddit media viewer links (https://www.reddit.com/media?url=https%3A%2F%2Fi.redd.it%2Fabc.jpg)
    /// into the direct image link they point to. Other links come back unchanged.
    /// </summary>
    public static string Unwrap(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return url;
        var host = uri.Host.ToLowerInvariant();
        if ((host == "reddit.com" || host.EndsWith(".reddit.com")) &&
            uri.AbsolutePath.TrimEnd('/').Equals("/media", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq > 0 && part[..eq].Equals("url", StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(part[(eq + 1)..]);
            }
        }
        return url;
    }

    /// <summary>True if the bytes start like a JPEG, PNG, GIF, WebP, or BMP file.</summary>
    public static bool LooksLikeImage(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return true;           // JPEG
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true; // PNG
        if (b.Length >= 4 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x38) return true; // GIF
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
            b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return true;           // WebP
        if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return true;                             // BMP
        return false;
    }

    /// <summary>Pulls the image address out of an HTML page that wraps an image.</summary>
    public static string? FindImageInHtml(string html, Uri pageUri)
    {
        foreach (Match tag in OgImageTagRegex().Matches(html))
        {
            var content = ContentAttrRegex().Match(tag.Value);
            if (content.Success && TryMakeAbsolute(content.Groups[1].Value, pageUri, out var abs)) return abs;
        }

        return null;
    }

    private static bool TryMakeAbsolute(string raw, Uri baseUri, out string result)
    {
        var decoded = WebUtility.HtmlDecode(raw.Trim());
        if (Uri.TryCreate(baseUri, decoded, out var abs) && (abs.Scheme == "https" || abs.Scheme == "http"))
        {
            result = abs.AbsoluteUri;
            return true;
        }
        result = "";
        return false;
    }

    public async Task<FetchedImage> FetchAsync(string url, CancellationToken ct = default)
    {
        url = Unwrap(url.Trim());
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            throw new ImageFetchException("That doesn't look like a web link.");

        if (IsRedditPostLink(uri))
        {
            var postImage = await GetRedditPostImageAsync(uri, ct);
            var (postBytes, postFinal) = await DownloadAsync(new Uri(postImage), ct);
            if (LooksLikeImage(postBytes)) return new FetchedImage(postBytes, postFinal);
            throw new ImageFetchException("I found the post, but its image wouldn't download.");
        }

        var (bytes, finalUri) = await DownloadAsync(uri, ct);
        if (LooksLikeImage(bytes)) return new FetchedImage(bytes, finalUri);

        // Not an image: probably a page wrapped around one (Reddit sometimes does this).
        var html = Encoding.UTF8.GetString(bytes);
        var inner = FindImageInHtml(html, finalUri);
        if (inner is not null && Uri.TryCreate(inner, UriKind.Absolute, out var innerUri) && innerUri != finalUri)
        {
            var (innerBytes, innerFinal) = await DownloadAsync(innerUri, ct);
            if (LooksLikeImage(innerBytes)) return new FetchedImage(innerBytes, innerFinal);
        }

        throw new ImageFetchException(
            "That link didn't lead to an image. It may be a post link, a video, or a page that needs signing in. Try copying the image link instead.");
    }

    /// <summary>True for links to a Reddit post (including share links and redd.it short links), not to an image.</summary>
    public static bool IsRedditPostLink(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (host == "redd.it") return true;
        if (host != "reddit.com" && !host.EndsWith(".reddit.com")) return false;
        var path = uri.AbsolutePath;
        return path.Contains("/comments/", StringComparison.OrdinalIgnoreCase) ||
               System.Text.RegularExpressions.Regex.IsMatch(path, @"^/r/[^/]+/s/", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// Picks the image out of a Reddit post's public JSON (the same data the post page is built from).
    /// Returns the first image for galleries. Returns null if the post has no image.
    /// </summary>
    public static string? FindImageInRedditJson(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        var listing = root.ValueKind == System.Text.Json.JsonValueKind.Array ? root[0] : root;
        if (!listing.TryGetProperty("data", out var data) || !data.TryGetProperty("children", out var children) ||
            children.GetArrayLength() == 0) return null;
        var post = children[0].GetProperty("data");
        if (post.TryGetProperty("crosspost_parent_list", out var xp) && xp.ValueKind == System.Text.Json.JsonValueKind.Array &&
            xp.GetArrayLength() > 0) post = xp[0];

        static string? Str(System.Text.Json.JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;

        // Gallery: first item, full size.
        if (post.TryGetProperty("gallery_data", out var gallery) && gallery.ValueKind == System.Text.Json.JsonValueKind.Object &&
            post.TryGetProperty("media_metadata", out var meta) && meta.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var item in gallery.GetProperty("items").EnumerateArray())
            {
                var id = Str(item, "media_id");
                if (id is not null && meta.TryGetProperty(id, out var m) && m.TryGetProperty("s", out var src))
                {
                    var u = Str(src, "u") ?? Str(src, "gif");
                    if (u is not null) return WebUtility.HtmlDecode(u);
                }
            }
        }

        // Single image post.
        var link = Str(post, "url_overridden_by_dest") ?? Str(post, "url");
        if (link is not null && System.Text.RegularExpressions.Regex.IsMatch(link,
                @"(i\.redd\.it|i\.imgur\.com)/|\.(jpe?g|png|gif|webp|bmp)(\?|$)", RegexOptions.IgnoreCase))
            return WebUtility.HtmlDecode(link);

        // Video or link post: its preview image.
        if (post.TryGetProperty("preview", out var preview) && preview.TryGetProperty("images", out var imgs) &&
            imgs.GetArrayLength() > 0 && imgs[0].TryGetProperty("source", out var source))
        {
            var u = Str(source, "url");
            if (u is not null) return WebUtility.HtmlDecode(u);
        }
        return null;
    }

    private async Task<string> GetRedditPostImageAsync(Uri postUri, CancellationToken ct)
    {
        // Share links and short links redirect to the real post; follow them first.
        var resolved = postUri;
        if (!postUri.AbsolutePath.Contains("/comments/", StringComparison.OrdinalIgnoreCase))
        {
            using var head = new HttpRequestMessage(HttpMethod.Get, postUri);
            using var r = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
            resolved = r.RequestMessage?.RequestUri ?? postUri;
        }

        var jsonUrl = $"https://www.reddit.com{resolved.AbsolutePath.TrimEnd('/')}.json?raw_json=1";
        using var request = new HttpRequestMessage(HttpMethod.Get, jsonUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new ImageFetchException($"Reddit wouldn't give me that post (error {(int)response.StatusCode}). Try copying the image link instead.");

        string? image;
        try
        {
            image = FindImageInRedditJson(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new ImageFetchException("Reddit sent something I couldn't read for that post. Try copying the image link instead.", ex);
        }
        return image ?? throw new ImageFetchException("That post doesn't seem to have an image. It may be a text post or a video.");
    }

    private async Task<(byte[] Bytes, Uri FinalUri)> DownloadAsync(Uri uri, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        // Ask for image formats first so hosts like i.redd.it return the file, not a viewer page.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/jpeg"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/png"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*", 0.9));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.5));

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ImageFetchException("I couldn't download that image. Check your internet connection.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ImageFetchException("Downloading the image took too long.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var why = code switch
                {
                    403 => "the site refused access",
                    404 => "the image no longer exists",
                    _ => $"the site returned error {code}",
                };
                throw new ImageFetchException($"I couldn't download that image: {why}.");
            }

            if (response.Content.Headers.ContentLength > MaxBytes)
                throw new ImageFetchException("That image is too large to describe.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBytes) throw new ImageFetchException("That image is too large to describe.");
            }

            var finalUri = response.RequestMessage?.RequestUri ?? uri;
            return (buffer.ToArray(), finalUri);
        }
    }

    public void Dispose() => _http.Dispose();
}
