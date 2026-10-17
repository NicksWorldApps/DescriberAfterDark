namespace ImageDescriber.Core;

/// <summary>One line in the Models window list.</summary>
public sealed record ModelEntry(string Name, string Text, bool Installed, bool InUse, bool? CanSeeImages, CatalogModel? Catalog);

/// <summary>Builds the Models window list: the catalog models first, then any other models already in Ollama.</summary>
public static class ModelList
{
    public static IReadOnlyList<ModelEntry> Build(
        IReadOnlyList<(string Name, long Size)> installed,
        string currentModel,
        IReadOnlyDictionary<string, bool?> canSeeImages)
    {
        static string Key(string n) => OllamaClient.NormalizeModelName(n).ToLowerInvariant();
        var installedSizes = installed.GroupBy(m => Key(m.Name)).ToDictionary(g => g.Key, g => g.First().Size);
        var vision = canSeeImages.ToDictionary(kv => Key(kv.Key), kv => kv.Value);
        var current = Key(currentModel);
        var entries = new List<ModelEntry>();

        foreach (var model in ModelCatalog.Models)
        {
            var key = Key(model.Name);
            var isInstalled = installedSizes.ContainsKey(key);
            var inUse = key == current;
            var status = inUse && isInstalled ? "In use" : inUse ? "Selected, not downloaded yet" : isInstalled ? "Downloaded" : "Not downloaded";
            var text = $"{model.Title}. {status}. {model.DownloadSize}. {model.Description}";
            entries.Add(new ModelEntry(model.Name, text, isInstalled, inUse, true, model));
        }

        foreach (var (name, size) in installed)
        {
            var key = Key(name);
            if (ModelCatalog.Find(name) is not null) continue;
            vision.TryGetValue(key, out var sees);
            var inUse = key == current;
            var note = sees == false
                ? "Text only: it can't see images, so it can't describe pictures."
                : "A model you added yourself.";
            var text = $"{name}. {(inUse ? "In use" : "Downloaded")}. {ModelCatalog.FormatSize(size)}. {note}";
            entries.Add(new ModelEntry(name, text, true, inUse, sees, null));
        }

        // A model chosen in settings.json that's neither in the catalog nor downloaded.
        if (!entries.Any(e => e.InUse))
            entries.Add(new ModelEntry(currentModel, $"{currentModel}. Selected, not downloaded yet. A model you added yourself.", false, true, null, null));

        return entries;
    }
}
