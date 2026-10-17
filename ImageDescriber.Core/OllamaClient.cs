using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImageDescriber.Core;

public sealed record ChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("images")] IReadOnlyList<string>? Images = null);

public sealed record PullProgress(string Status, long Completed, long Total)
{
    /// <summary>Percent complete, or -1 when the current step has no size (for example "verifying").</summary>
    public int Percent => Total > 0 ? (int)(Completed * 100 / Total) : -1;
}

public sealed class OllamaException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Talks to the local Ollama server over its HTTP API.</summary>
public sealed class OllamaClient : IDisposable
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public OllamaClient(string baseUrl)
    {
        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        // No overall timeout: descriptions on a CPU-only machine can take minutes.
        // Callers cancel with a CancellationToken instead.
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<bool> IsRunningAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await _http.GetAsync("api/version", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Starts Ollama if it isn't running and waits up to 30 seconds for it.</summary>
    public async Task<bool> EnsureRunningAsync(CancellationToken ct = default)
    {
        if (await IsRunningAsync(ct)) return true;
        if (!TryStartOllama()) return false;

        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(1000, ct);
            if (await IsRunningAsync(ct)) return true;
        }
        return false;
    }

    /// <summary>True if the Ollama program appears to be installed on this computer.</summary>
    public static bool IsInstalled() => FindOllamaApp() is not null || FindOnPath("ollama.exe") is not null;

    private static bool TryStartOllama()
    {
        try
        {
            // Prefer the tray app, which keeps the server running in the background.
            var app = FindOllamaApp();
            if (app is not null)
            {
                Process.Start(new ProcessStartInfo(app) { UseShellExecute = true });
                return true;
            }

            var exe = FindOnPath("ollama.exe") ?? FindOnPath("ollama");
            if (exe is not null)
            {
                Process.Start(new ProcessStartInfo(exe, "serve")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                return true;
            }
        }
        catch
        {
            // Fall through: report "couldn't start" to the caller.
        }
        return false;
    }

    private static string? FindOllamaApp()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidate = Path.Combine(local, "Programs", "Ollama", "ollama app.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(full)) return full;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }
        return null;
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("api/tags", ct);
        var names = new List<string>();
        if (doc.RootElement.TryGetProperty("models", out var models))
        {
            foreach (var m in models.EnumerateArray())
            {
                if (m.TryGetProperty("name", out var n) && n.GetString() is { } name) names.Add(name);
            }
        }
        return names;
    }

    /// <summary>Installed models with their size on disk, in bytes.</summary>
    public async Task<IReadOnlyList<(string Name, long Size)>> ListModelsWithSizesAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("api/tags", ct);
        var result = new List<(string, long)>();
        if (doc.RootElement.TryGetProperty("models", out var models))
        {
            foreach (var m in models.EnumerateArray())
            {
                if (!m.TryGetProperty("name", out var n) || n.GetString() is not { } name) continue;
                var size = m.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                result.Add((name, size));
            }
        }
        return result;
    }

    /// <summary>
    /// True if the installed model can look at images, false if it's text-only,
    /// null if Ollama doesn't say (older Ollama versions).
    /// </summary>
    public async Task<bool?> CanSeeImagesAsync(string model, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/show")
        {
            Content = JsonContent.Create(new { model }),
        };
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!doc.RootElement.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Array) return null;
        return caps.EnumerateArray().Any(c => string.Equals(c.GetString(), "vision", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Deletes a downloaded model's files, freeing the disk space.</summary>
    public async Task DeleteModelAsync(string model, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, "api/delete")
        {
            Content = JsonContent.Create(new { model }),
        };
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureOkAsync(response, ct);
    }

    public async Task<bool> HasModelAsync(string model, CancellationToken ct = default)
    {
        var wanted = NormalizeModelName(model);
        var installed = await ListModelsAsync(ct);
        return installed.Any(m => string.Equals(NormalizeModelName(m), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Ollama treats a name without a tag as ":latest".</summary>
    public static string NormalizeModelName(string name)
    {
        name = name.Trim();
        var lastSlash = name.LastIndexOf('/');
        return name.IndexOf(':', lastSlash + 1) >= 0 ? name : name + ":latest";
    }

    /// <summary>Downloads a model, reporting progress as it goes. Safe to call again to resume.</summary>
    public async Task PullModelAsync(string model, IProgress<PullProgress>? progress, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/pull")
        {
            Content = JsonContent.Create(new { model, stream = true }),
        };
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureOkAsync(response, ct);

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.TryGetProperty("error", out var err))
                throw new OllamaException($"Downloading the model failed: {err.GetString()}");

            var status = root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            var total = root.TryGetProperty("total", out var t) && t.TryGetInt64(out var tv) ? tv : 0;
            var done = root.TryGetProperty("completed", out var c) && c.TryGetInt64(out var cv) ? cv : 0;
            progress?.Report(new PullProgress(status, done, total));
        }
    }

    /// <summary>Sends a whole conversation and returns the model's reply.</summary>
    public async Task<string> ChatAsync(string model, IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var body = new
        {
            model,
            messages,
            stream = false,
            keep_alive = "15m", // keep the model loaded so the next image is fast
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureOkAsync(response, ct, model);

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "";
        return CleanReply(content);
    }

    /// <summary>Removes any "thinking" section and markdown symbols that would be read aloud.</summary>
    public static string CleanReply(string text)
    {
        var end = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (end >= 0) text = text[(end + "</think>".Length)..];

        var sb = new StringBuilder(text.Length);
        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            line = line.TrimStart('#', '*', '-', '•', ' ');
            line = line.Replace("**", "").Replace("__", "");
            sb.AppendLine(line);
        }
        // Collapse runs of blank lines.
        var cleaned = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"(\r?\n){3,}", Environment.NewLine + Environment.NewLine);
        return cleaned.Trim();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, CancellationToken ct)
    {
        try
        {
            return await _http.SendAsync(request, option, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new OllamaException("I couldn't reach Ollama. Make sure it is installed and running.", ex);
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
        await EnsureOkAsync(response, ct);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    private static async Task EnsureOkAsync(HttpResponseMessage response, CancellationToken ct, string? model = null)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await response.Content.ReadAsStringAsync(ct);
        string detail = body;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e)) detail = e.GetString() ?? body;
        }
        catch (JsonException)
        {
            // Not JSON; use the raw text.
        }

        if (model is not null && detail.Contains("not found", StringComparison.OrdinalIgnoreCase))
            throw new OllamaException($"The model {model} isn't downloaded yet.");

        throw new OllamaException($"Ollama reported an error: {detail}");
    }

    public void Dispose() => _http.Dispose();
}
