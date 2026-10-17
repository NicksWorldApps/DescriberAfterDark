using System.Diagnostics;
using System.Runtime.InteropServices;
using ImageDescriber.Core;
using ImageDescriber.Core.Speech;

namespace ImageDescriber.App;

/// <summary>
/// The main window. Kept deliberately simple for screen readers:
/// a status line, a read-only description box, a question box, and three buttons.
/// The app keeps running in the system tray when the window is closed.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly OllamaClient _ollama;
    private readonly ImageFetcher _fetcher = new();
    private readonly GlobalHotkey _hotkey = new();
    private ISpeechOutput _speech = new SilentSpeech();

    private readonly Label _status = new();
    private readonly TextBox _description = new();
    private readonly TextBox _question = new();
    private readonly Button _askButton = new();
    private readonly Button _describeButton = new();
    private readonly Button _hideButton = new();
    private readonly NotifyIcon _tray = new();

    private Conversation? _conversation;
    private CancellationTokenSource? _work;
    private bool _busy;
    private bool _ready;
    private bool _exiting;
    private readonly Button _helpButton = new();

    // Remembers the last image described, so pressing the shortcut again on the same clipboard is caught.
    private string? _lastDescribedKey;
    private DateTime _sameImageWarnedAt = DateTime.MinValue;

    // Whatever was on the clipboard when the program started. It's left over from before,
    // so pressing the shortcut on it gets a warning instead of an old picture.
    private string? _startupClipboardKey;

    public MainForm()
    {
        _ollama = new OllamaClient(_settings.OllamaUrl);
        try
        {
            _settings.Save(); // creates the file, and adds any new settings to an older one
        }
        catch
        {
            // Not being able to write settings isn't fatal.
        }
        BuildLayout();
        BuildTray();

        _hotkey.Pressed += async (_, _) => await DescribeClipboardAsync();
        Shown += async (_, _) => await StartUpAsync();
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        Text = $"Describer After Dark {AppInfo.Version}";
        AccessibleName = "Describer After Dark";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 520);
        MinimumSize = new Size(480, 360);
        KeyPreview = true;
        Icon = SystemIcons.Information;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(10),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // status
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // description label
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // description
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // question label
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // question
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // buttons

        _status.AutoSize = true;
        _status.Text = "Starting...";
        _status.AccessibleName = "Status";
        _status.Margin = new Padding(0, 0, 0, 8);

        var descriptionLabel = new Label { Text = "&Description:", AutoSize = true };
        _description.Multiline = true;
        _description.ReadOnly = true;
        _description.ScrollBars = ScrollBars.Vertical;
        _description.Dock = DockStyle.Fill;
        _description.AccessibleName = "Description";
        _description.BackColor = SystemColors.Window;
        _description.Font = new Font(Font.FontFamily, 11f);

        var questionLabel = new Label { Text = "Follow-up &question:", AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
        _question.Dock = DockStyle.Fill;
        _question.AccessibleName = "Follow-up question";
        _question.Enter += (_, _) => AcceptButton = _askButton;
        _question.Leave += (_, _) => AcceptButton = null;

        _askButton.Text = "&Ask";
        _askButton.AutoSize = true;
        _askButton.Click += async (_, _) => await AskAsync();

        _describeButton.Text = "Describe &clipboard";
        _describeButton.AutoSize = true;
        _describeButton.Click += async (_, _) => await DescribeClipboardAsync();

        _helpButton.Text = "How to &use (F1)";
        _helpButton.AutoSize = true;
        _helpButton.Click += (_, _) => ShowInstructions(speak: true);

        _hideButton.Text = "&Hide to tray";
        _hideButton.AutoSize = true;
        _hideButton.Click += (_, _) => HideToTray();

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange(new Control[] { _askButton, _describeButton, _helpButton, _hideButton });

        layout.Controls.Add(_status, 0, 0);
        layout.Controls.Add(descriptionLabel, 0, 1);
        layout.Controls.Add(_description, 0, 2);
        layout.Controls.Add(questionLabel, 0, 3);
        layout.Controls.Add(_question, 0, 4);
        layout.Controls.Add(buttons, 0, 5);
        Controls.Add(layout);

        // Tab order follows reading order; labels come right before their boxes so Alt shortcuts work.
        var order = 0;
        foreach (var c in new Control[] { _status, descriptionLabel, _description, questionLabel, _question, _askButton, _describeButton, _helpButton, _hideButton })
            c.TabIndex = order++;
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show window", null, (_, _) => ShowFromTray());
        menu.Items.Add("Describe clipboard", null, async (_, _) => await DescribeClipboardAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray.Icon = SystemIcons.Information;
        _tray.Text = "Describer After Dark";
        _tray.ContextMenuStrip = menu;
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    // ---------------------------------------------------------------- start-up

    private async Task StartUpAsync()
    {
        _speech = PrismSpeech.CreateBest(Log);

        if (!_settings.AgeConfirmed)
        {
            _speech.Say("Describer After Dark is for adults only. Please answer the age question.", interrupt: true);
            var answer = MessageBox.Show(this,
                "Describer After Dark describes sexually explicit images in explicit detail. It is for adults only.\n\n" +
                "Are you 18 years of age or older?",
                "Adults only", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                _speech.Say("Describer After Dark is only for adults. Closing.", interrupt: true);
                ExitApp();
                return;
            }
            _settings.AgeConfirmed = true;
            try { _settings.Save(); } catch { /* asked again next time if this fails */ }
        }

        if (!_hotkey.Register(_settings.HotkeyModifiers, _settings.HotkeyKey))
        {
            MessageBox.Show(this,
                $"The shortcut {_hotkey.Description} is already used by another program. " +
                $"You can still use the Describe clipboard button, or change the shortcut in {AppSettings.SettingsPath}.",
                "Describer After Dark", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        try
        {
            SetStatus("Checking Ollama...");
            if (!OllamaClient.IsInstalled() && !await _ollama.IsRunningAsync())
            {
                SetStatus("Ollama is not installed. It is needed to describe images.", speak: true);
                var open = MessageBox.Show(this,
                    "Describer After Dark needs the free Ollama program to run the description model on your computer.\n\n" +
                    "Open the Ollama download page now? After installing it, restart Describer After Dark.",
                    "Ollama needed", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (open == DialogResult.Yes) OpenUrl("https://ollama.com/download");
                return;
            }

            if (!await _ollama.EnsureRunningAsync())
            {
                SetStatus("Ollama is installed but wouldn't start. Try starting Ollama from the Start menu, then restart Describer After Dark.", speak: true);
                return;
            }

            if (!await _ollama.HasModelAsync(_settings.Model))
            {
                if (!await DownloadModelAsync()) return;
            }

            _lastDescribedKey = string.IsNullOrEmpty(_settings.LastDescribedHash) ? null : _settings.LastDescribedHash;
            _startupClipboardKey = CurrentClipboardKey();
            _ready = true;
            ShowInstructions(speak: false);
            SetStatus($"Describer After Dark version {AppInfo.Version} is ready.", speak: false);
            _speech.Say($"Describer After Dark version {AppInfo.Version} is ready. " +
                        $"Copy an image link, then press {_hotkey.Description} to describe it. " +
                        "In Luna for Reddit, open a post, press Shift F10, and choose Copy post image link. " +
                        "Full instructions are in the description box, or press F1.", interrupt: true);
            Log($"Started version {AppInfo.Version}, prompt v{AppInfo.PromptVersion}, model {_settings.Model}, style {_settings.Style}");
            _description.Focus();
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            SetStatus($"Start-up problem: {ex.Message}", speak: true);
        }
    }

    private async Task<bool> DownloadModelAsync()
    {
        var answer = MessageBox.Show(this,
            $"The description model ({_settings.Model}) needs to be downloaded once. It is about 6 gigabytes, " +
            "so it may take a while. Download it now?",
            "Download model", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes)
        {
            SetStatus("The model isn't downloaded. Restart Describer After Dark when you're ready to download it.", speak: true);
            return false;
        }

        _busy = true;
        _work = new CancellationTokenSource();
        SetStatus("Downloading model. Progress will be announced every 10 percent. Press Escape to stop.", speak: true);

        var lastSpoken = new Dictionary<string, int>();
        var progress = new Progress<PullProgress>(p =>
        {
            if (p.Percent < 0)
            {
                SetStatus($"Model download: {p.Status}");
                return;
            }
            SetStatus($"Downloading model: {p.Percent} percent");
            // Only announce the large file, in 10 percent steps, so speech isn't flooded.
            if (p.Total < 100_000_000) return;
            var step = p.Percent / 10 * 10;
            if (!lastSpoken.TryGetValue(p.Status, out var last) || step > last)
            {
                lastSpoken[p.Status] = step;
                if (step > 0) _speech.Say($"{step} percent", interrupt: true);
            }
        });

        try
        {
            await _ollama.PullModelAsync(_settings.Model, progress, _work.Token);
            _speech.Say("Model downloaded.", interrupt: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStatus("Model download stopped. It will continue where it left off next time.", speak: true);
            return false;
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            SetStatus($"Model download failed: {ex.Message} Restart Describer After Dark to try again.", speak: true);
            return false;
        }
        finally
        {
            _busy = false;
            _work?.Dispose();
            _work = null;
        }
    }

    // ---------------------------------------------------------------- describing

    private async Task DescribeClipboardAsync()
    {
        ShowFromTray();

        if (_busy)
        {
            _speech.Say("Still working. Press Escape to cancel.", interrupt: true);
            return;
        }
        if (!_ready)
        {
            _speech.Say(_status.Text, interrupt: true);
            return;
        }

        var clip = ReadClipboard();
        if (clip.Error is not null)
        {
            SetStatus(clip.Error, speak: true);
            return;
        }

        // Catch the easy mistake of pressing the shortcut without copying a new image,
        // including a link left on the clipboard from before the program was started.
        var key = KeyOf(clip);
        var stale = key == _lastDescribedKey ? "same" : key == _startupClipboardKey ? "old" : null;
        if (stale is not null && DateTime.Now - _sameImageWarnedAt > TimeSpan.FromSeconds(10))
        {
            _sameImageWarnedAt = DateTime.Now;
            var why = stale == "same"
                ? "That's the same image you described last time."
                : "That image was already on the clipboard before Describer After Dark started, so it's probably an old one.";
            SetStatus($"{why} Copy a new image link first. " +
                      $"To describe this one anyway, press {_hotkey.Description} again within 10 seconds.", speak: true);
            return;
        }
        _sameImageWarnedAt = DateTime.MinValue;

        _busy = true;
        _work = new CancellationTokenSource();
        var ct = _work.Token;
        try
        {
            PreparedImage image;
            string source;
            string where;
            if (clip.Url is not null)
            {
                SetStatus("Downloading image...", speak: true);
                var fetched = await _fetcher.FetchAsync(clip.Url, ct);
                image = await Task.Run(() => ImagePrep.Prepare(fetched.Bytes, _settings.MaxImageSide), ct);
                source = fetched.Source.AbsoluteUri;
                where = $"from {fetched.Source.Host}";
            }
            else
            {
                image = clip.Image!;
                source = clip.SourceName!;
                where = $"from the clipboard ({clip.SourceName})";
            }

            Log($"Describe: copied '{clip.Url ?? clip.SourceName}' -> image {source}, {image.Format}, {image.Width}x{image.Height}, " +
                $"model {_settings.Model}, style {_settings.Style}, prompt v{AppInfo.PromptVersion}");

            if (!await _ollama.EnsureRunningAsync(ct))
                throw new OllamaException("Ollama isn't running and couldn't be started.");

            var size = image.Width > 0 ? $", {image.Width} by {image.Height}" : "";
            SetStatus($"Describing image {where}{size}. This can take a little while.", speak: true);
            _conversation = new Conversation(_ollama, _settings.Model, image.Base64, source, _settings.Style);
            var text = await _conversation.DescribeAsync(ct);

            _lastDescribedKey = key;
            _startupClipboardKey = null; // a new image has been described, so the startup one no longer matters
            _settings.LastDescribedHash = key;
            try { _settings.Save(); } catch { /* not remembering this across restarts isn't fatal */ }
            _description.Text = text;
            _description.Focus();
            _description.SelectionStart = 0;
            _description.SelectionLength = 0;
            SetStatus("Description ready. Ask a follow-up question, or copy another image and press the shortcut.");
            if (_settings.SpeakDescriptions) _speech.Say(text, interrupt: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.", speak: true);
        }
        catch (Exception ex) when (ex is ImageFetchException or OllamaException)
        {
            SetStatus(ex.Message, speak: true);
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            SetStatus($"Something went wrong: {ex.Message}", speak: true);
        }
        finally
        {
            _busy = false;
            _work?.Dispose();
            _work = null;
        }
    }

    private async Task AskAsync()
    {
        var question = _question.Text.Trim();
        if (question.Length == 0)
        {
            _speech.Say("Type a question first.", interrupt: true);
            return;
        }
        if (_conversation is null)
        {
            _speech.Say("Describe an image first.", interrupt: true);
            return;
        }
        if (_busy)
        {
            _speech.Say("Still working. Press Escape to cancel.", interrupt: true);
            return;
        }

        _busy = true;
        _work = new CancellationTokenSource();
        try
        {
            SetStatus("Thinking...", speak: true);
            var answer = await _conversation.AskAsync(question, _work.Token);

            _description.AppendText($"{Environment.NewLine}{Environment.NewLine}You asked: {question}{Environment.NewLine}{answer}");
            _question.Clear();
            SetStatus("Answer ready. It was added to the end of the description box.");
            if (_settings.SpeakDescriptions) _speech.Say(answer, interrupt: true);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Cancelled.", speak: true);
        }
        catch (Exception ex) when (ex is OllamaException)
        {
            SetStatus(ex.Message, speak: true);
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
            SetStatus($"Something went wrong: {ex.Message}", speak: true);
        }
        finally
        {
            _busy = false;
            _work?.Dispose();
            _work = null;
        }
    }

    private sealed record ClipboardContent(string? Url, PreparedImage? Image, string? SourceName, string? Error);

    /// <summary>
    /// Reads whatever is on the clipboard: a copied image, a copied image file, or text containing a link.
    /// </summary>
    private ClipboardContent ReadClipboard()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Clipboard.ContainsImage())
                {
                    using var image = Clipboard.GetImage();
                    if (image is not null)
                        return new(null, ImagePrep.Prepare(image, _settings.MaxImageSide), "copied image", null);
                }

                if (Clipboard.ContainsFileDropList())
                {
                    var files = Clipboard.GetFileDropList();
                    if (files.Count > 0 && files[0] is { } path && File.Exists(path))
                    {
                        var bytes = File.ReadAllBytes(path);
                        if (!ImageFetcher.LooksLikeImage(bytes))
                            return new(null, null, null, "The copied file isn't an image I can read.");
                        return new(null, ImagePrep.Prepare(bytes, _settings.MaxImageSide), Path.GetFileName(path), null);
                    }
                }

                if (Clipboard.ContainsText())
                {
                    var url = ImageFetcher.FindUrl(Clipboard.GetText());
                    if (url is not null) return new(url, null, null, null);
                    return new(null, null, null, "The clipboard has text but no link. Copy an image link first.");
                }

                return new(null, null, null, "The clipboard is empty. Copy an image or an image link first.");
            }
            catch (ExternalException)
            {
                // Another program has the clipboard open; wait briefly and retry.
                Thread.Sleep(100);
            }
        }
        return new(null, null, null, "The clipboard is busy. Try again in a moment.");
    }

    // ---------------------------------------------------------------- window, keys, tray

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F1)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            ShowInstructions(speak: true);
            return;
        }
        if (e.KeyCode == Keys.Escape)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            if (_busy && _work is not null)
            {
                _work.Cancel();
                _speech.Say("Cancelling.", interrupt: true);
            }
            else
            {
                HideToTray();
            }
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        base.OnFormClosing(e);
    }

    private void HideToTray()
    {
        Hide();
        var hint = _ready ? $" Press {_hotkey.Description} to describe an image." : "";
        _speech.Say($"Describer After Dark is still running in the system tray.{hint}", interrupt: false);
    }

    private void ShowFromTray()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private void ExitApp()
    {
        _exiting = true;
        _work?.Cancel();
        _tray.Visible = false;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hotkey.Dispose();
            _tray.Dispose();
            _speech.Dispose();
            _fetcher.Dispose();
            _ollama.Dispose();
            _work?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---------------------------------------------------------------- helpers

    private string Instructions =>
        $"How to use Describer After Dark {AppInfo.Version}{Environment.NewLine}{Environment.NewLine}" +
        $"1. Copy a new image. The shortcut always describes whatever is on the clipboard right now, so copy a fresh link every time.{Environment.NewLine}" +
        $"   In Luna for Reddit: open a post with an image, press Shift F10, and choose Copy post image link.{Environment.NewLine}" +
        $"   In Reddit for Blind or a web browser: copy the image link or the post link.{Environment.NewLine}" +
        $"   You can also copy an image itself, or an image file in File Explorer.{Environment.NewLine}" +
        $"2. Press {_hotkey.Description} from anywhere. The image is downloaded and described, and your screen reader speaks the description.{Environment.NewLine}" +
        $"3. To ask about the image, press Alt Q, type a question such as what color is her hair, and press Enter.{Environment.NewLine}" +
        $"4. Press Escape to hide this window. Describer After Dark keeps running in the system tray. Pressing Escape while it is working cancels.{Environment.NewLine}{Environment.NewLine}" +
        $"If you press the shortcut without copying a new image, or on a link left over from before the program started, you'll be told so instead of hearing an old picture.{Environment.NewLine}" +
        $"Keys in this window: Alt D description, Alt Q question, Alt A ask, Alt C describe clipboard, F1 these instructions, Alt H hide.";

    private void ShowInstructions(bool speak)
    {
        _description.Text = Instructions;
        _description.SelectionStart = 0;
        _description.SelectionLength = 0;
        _description.Focus();
        if (speak) _speech.Say(Instructions.Replace(Environment.NewLine, " "), interrupt: true);
    }

    /// <summary>
    /// A fingerprint of the clipboard contents. Only this hash is ever saved, never the link or image itself.
    /// </summary>
    private static string KeyOf(ClipboardContent clip) => HashOf(clip.Url ?? clip.Image!.Base64);

    private string? CurrentClipboardKey()
    {
        try
        {
            var clip = ReadClipboard();
            return clip.Error is null ? KeyOf(clip) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string HashOf(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));

    private void SetStatus(string text, bool speak = false)
    {
        _status.Text = text;
        _tray.Text = text.Length > 63 ? text[..60] + "..." : text; // tray tooltips are limited to 63 characters
        if (speak) _speech.Say(text, interrupt: true);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* no browser available */ }
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsFolder);
            File.AppendAllText(Path.Combine(AppSettings.SettingsFolder, "log.txt"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
