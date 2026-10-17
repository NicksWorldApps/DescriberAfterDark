namespace ImageDescriber.Core;

/// <summary>A model offered in the Models window.</summary>
/// <param name="Name">The exact Ollama name, used to download and run it.</param>
/// <param name="Title">Short spoken name.</param>
/// <param name="DownloadSize">Approximate download size, as spoken.</param>
/// <param name="Description">One or two sentences on when to pick it.</param>
public sealed record CatalogModel(string Name, string Title, string DownloadSize, string Description);

/// <summary>
/// The models offered in the Models window. All of them can see images and have had their refusals
/// removed ("abliterated"), so they describe explicit pictures. Names are checked against ollama.com.
/// Any other Ollama model can still be added by name in the Models window.
/// </summary>
public static class ModelCatalog
{
    public static readonly IReadOnlyList<CatalogModel> Models = new[]
    {
        new CatalogModel(AppSettings.DefaultModel, "Qwen3-VL 8B, uncensored", "6.1 GB",
            "The default and recommended model. Good detail and reliable naming of what's in the picture. " +
            "Fast on a graphics card with 8 GB of memory or more; works without one, just slower."),
        new CatalogModel("huihui_ai/qwen3-vl-abliterated:4b-instruct", "Qwen3-VL 4B, uncensored", "3.3 GB",
            "Smaller and faster, with a little less detail. A good choice for graphics cards with 4 to 6 GB of memory, or computers without one."),
        new CatalogModel("huihui_ai/qwen3-vl-abliterated:2b-instruct", "Qwen3-VL 2B, uncensored", "1.9 GB",
            "The smallest and fastest. Descriptions are shorter and simpler. For older or low-powered computers."),
        new CatalogModel("huihui_ai/qwen3-vl-abliterated:32b-instruct", "Qwen3-VL 32B, uncensored", "21 GB",
            "The most detailed and accurate, but very large. Needs a powerful graphics card with 24 GB of memory to run at a usable speed."),
        new CatalogModel("lukey03/qwen3.5-9b-abliterated-vision", "Qwen 3.5 9B vision, uncensored", "6.6 GB",
            "A newer community model, similar in size to the default. Worth trying if you'd like a different writing style."),
        new CatalogModel("huihui_ai/qwen2.5-vl-abliterated:7b-instruct", "Qwen2.5-VL 7B, uncensored", "6.0 GB",
            "The previous generation of the default model. An alternative if the default ever struggles with certain pictures."),
        new CatalogModel("huihui_ai/qwen2.5-vl-abliterated:3b-instruct", "Qwen2.5-VL 3B, uncensored", "3.2 GB",
            "A small previous-generation model, for low-powered computers."),
        new CatalogModel("huihui_ai/gemma3-abliterated:4b", "Gemma 3 4B, uncensored", "8.6 GB",
            "Google's Gemma with refusals removed. A different writing style from the Qwen models."),
    };

    public static CatalogModel? Find(string name)
    {
        var wanted = OllamaClient.NormalizeModelName(name);
        return Models.FirstOrDefault(m => string.Equals(OllamaClient.NormalizeModelName(m.Name), wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Bytes as people say them: "6.1 GB", "850 MB".</summary>
    public static string FormatSize(long bytes) =>
        bytes >= 1_000_000_000 ? $"{bytes / 1_000_000_000.0:0.#} GB" : $"{Math.Max(1, bytes / 1_000_000)} MB";
}
