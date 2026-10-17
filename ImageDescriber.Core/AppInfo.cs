using System.Reflection;

namespace ImageDescriber.Core;

public static class AppInfo
{
    /// <summary>
    /// The program's version, taken from the &lt;Version&gt; in ImageDescriber.App.csproj, which is the
    /// one and only place it is set. The release scripts read it from the built program, so the GitHub
    /// release and the program always agree, which the automatic updater depends on.
    /// </summary>
    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>Spoken at start-up and shown in the title bar, for example "1.1.0".</summary>
    public static string Version => CurrentVersion.ToString(3);

    /// <summary>Bumped whenever the model instructions change; written to the log with every description.</summary>
    public const int PromptVersion = 5;

    /// <summary>The GitHub repository that updates are published to, as owner/name.</summary>
    public const string UpdateRepository = "NicksWorldApps/DescriberAfterDark";
}
