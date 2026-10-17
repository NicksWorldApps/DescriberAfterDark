using System.Text.Json;

namespace ImageDescriber.Core;

/// <summary>
/// User settings, stored as JSON in %APPDATA%\ImageDescriber\settings.json.
/// Users can edit the file by hand; missing values fall back to defaults.
/// </summary>
public sealed class AppSettings
{
    public const string DefaultModel = "huihui_ai/qwen3-vl-abliterated:8b-instruct";

    /// <summary>Ollama model used for descriptions.</summary>
    public string Model { get; set; } = DefaultModel;

    /// <summary>Address of the local Ollama server.</summary>
    public string OllamaUrl { get; set; } = "http://localhost:11434/";

    /// <summary>Modifier keys for the global hotkey, joined with '+'. Allowed: Control, Alt, Shift, Win.</summary>
    public string HotkeyModifiers { get; set; } = "Control+Alt";

    /// <summary>Main key for the global hotkey, as a Windows Forms Keys name (for example D, F12).</summary>
    public string HotkeyKey { get; set; } = "D";

    /// <summary>"Sensual" (default) or "Plain". See <see cref="ImageDescriber.Core.DescriptionStyle"/>.</summary>
    public string DescriptionStyle { get; set; } = nameof(Core.DescriptionStyle.Sensual);

    [System.Text.Json.Serialization.JsonIgnore]
    public DescriptionStyle Style =>
        Enum.TryParse<DescriptionStyle>(DescriptionStyle, ignoreCase: true, out var s) ? s : Core.DescriptionStyle.Sensual;

    /// <summary>
    /// Fingerprint (SHA-256 hash) of the last image described, so the same-image warning still works after a restart.
    /// It's a one-way hash: the link or picture can't be recovered from it.
    /// </summary>
    public string LastDescribedHash { get; set; } = "";

    /// <summary>Set once the user has confirmed on first launch that they are 18 or older.</summary>
    public bool AgeConfirmed { get; set; }

    /// <summary>Speak descriptions through the screen reader as soon as they arrive.</summary>
    public bool SpeakDescriptions { get; set; } = true;

    /// <summary>Longest side, in pixels, images are scaled down to before being sent to the model.</summary>
    public int MaxImageSide { get; set; } = 1600;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static string SettingsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DescriberAfterDark");

    /// <summary>Where settings lived before the program was renamed; copied over once if found.</summary>
    private static string OldSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImageDescriber", "settings.json");

    public static string SettingsPath => Path.Combine(SettingsFolder, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath) && File.Exists(OldSettingsPath))
            {
                Directory.CreateDirectory(SettingsFolder);
                File.Copy(OldSettingsPath, SettingsPath);
            }

            if (File.Exists(SettingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions);
                if (loaded is not null)
                {
                    if (string.IsNullOrWhiteSpace(loaded.Model)) loaded.Model = DefaultModel;
                    if (loaded.MaxImageSide < 256) loaded.MaxImageSide = 1600;
                    return loaded;
                }
            }
        }
        catch
        {
            // A broken settings file shouldn't stop the app; fall back to defaults.
        }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsFolder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
    }
}
