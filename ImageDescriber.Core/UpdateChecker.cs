using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImageDescriber.Core;

/// <summary>A newer version available on GitHub.</summary>
public sealed record UpdateInfo(Version Version, string Notes, string DownloadUrl, long Size);

/// <summary>
/// Looks at the latest release on GitHub and downloads it. A release counts as an update when its tag
/// (for example "v1.1.0") is a higher version than the running program and it has a "-win-x64.zip" download.
/// Publishing a release with publish-to-github.cmd is all it takes for everyone to be offered the update.
/// </summary>
public sealed partial class UpdateChecker : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _repository;

    public UpdateChecker(string repository, HttpMessageHandler? handler = null)
    {
        _repository = repository;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan; // downloads can be large; callers cancel instead
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DescriberAfterDark-Updater");
    }

    [GeneratedRegex(@"\d+(\.\d+){1,3}")]
    private static partial Regex VersionRegex();

    /// <summary>Reads a version out of a tag such as "v1.1.0" or "1.1". Returns null if there isn't one.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var m = VersionRegex().Match(tag);
        if (!m.Success || !Version.TryParse(m.Value, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>
    /// Turns GitHub's "latest release" answer into an update, or null when it isn't newer than
    /// <paramref name="current"/>, is a draft or pre-release, or has no Windows download.
    /// </summary>
    public static UpdateInfo? ParseRelease(string json, Version current)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;

        var version = ParseVersion(root.TryGetProperty("tag_name", out var t) ? t.GetString() : null);
        if (version is null || version <= current) return null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (url is null || !name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase)) continue;
            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            return new UpdateInfo(version, notes, url, size);
        }
        return null;
    }

    /// <summary>
    /// Release notes as plain sentences for a dialog or screen reader: no markdown symbols,
    /// and cut down to the "What's new" part when there is one.
    /// </summary>
    public static string PlainNotes(string markdown, int maxLength = 700)
    {
        var text = markdown.Replace("\r\n", "\n");
        var whatsNew = Regex.Match(text, @"(?ims)^#+\s*what'?s new[^\n]*\n(.*?)(?=^#+\s|\z)");
        if (whatsNew.Success) text = whatsNew.Groups[1].Value;

        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('#', '-', '*', ' ').Replace("**", "").Replace("`", "");
            if (line.Length > 0) lines.Add(line);
        }
        var plain = string.Join(Environment.NewLine, lines);
        return plain.Length <= maxLength ? plain : plain[..maxLength].TrimEnd() + "...";
    }

    /// <summary>Asks GitHub for the latest release. Returns null if there's no update or GitHub can't be reached.</summary>
    public async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{_repository}/releases/latest");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await _http.SendAsync(request, cts.Token);
            if (!response.IsSuccessStatusCode) return null;
            return ParseRelease(await response.Content.ReadAsStringAsync(cts.Token), current);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null; // offline, GitHub down, rate-limited: just try again later
        }
    }

    /// <summary>Downloads the update's zip to <paramref name="destination"/>, reporting percent done.</summary>
    public async Task DownloadAsync(UpdateInfo update, string destination, IProgress<int>? progress, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? update.Size;

        await using var input = await response.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(destination);
        var buffer = new byte[81920];
        long done = 0;
        int read, lastPercent = -1;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (total > 0)
            {
                var percent = (int)(done * 100 / total);
                if (percent != lastPercent) { lastPercent = percent; progress?.Report(percent); }
            }
        }
    }

    public void Dispose() => _http.Dispose();
}
