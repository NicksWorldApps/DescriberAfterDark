namespace ImageDescriber.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Only one copy may run, since two copies can't share the hotkey.
        using var mutex = new Mutex(initiallyOwned: true, @"Local\DescriberAfterDark.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Describer After Dark is already running. Look for it in the system tray.",
                "Describer After Dark", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
