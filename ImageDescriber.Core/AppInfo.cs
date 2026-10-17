namespace ImageDescriber.Core;

public static class AppInfo
{
    /// <summary>Spoken at start-up and shown in the title bar, so it's easy to confirm a rebuild took effect.</summary>
    public const string Version = "1.0.1";

    /// <summary>Bumped whenever the model instructions change; written to the log with every description.</summary>
    public const int PromptVersion = 5;
}
