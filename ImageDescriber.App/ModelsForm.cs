using ImageDescriber.Core;
using ImageDescriber.Core.Speech;

namespace ImageDescriber.App;

/// <summary>
/// The Models window: a plain list of models (arrow through it; each item says its name, whether it's
/// downloaded or in use, its size, and what it's good for), plus buttons to use, download, or remove the
/// selected model, or add any other Ollama model by name. Progress is spoken through the screen reader.
/// </summary>
internal sealed class ModelsForm : Form
{
    private readonly OllamaClient _ollama;
    private readonly AppSettings _settings;
    private readonly ISpeechOutput _speech;
    private readonly Action<string> _log;

    private readonly ListBox _list = new();
    private readonly Button _useButton = new();
    private readonly Button _downloadButton = new();
    private readonly Button _removeButton = new();
    private readonly Button _addButton = new();
    private readonly Button _closeButton = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();

    private IReadOnlyList<ModelEntry> _entries = Array.Empty<ModelEntry>();
    private CancellationTokenSource? _download;
    private bool _working;

    /// <summary>True if the model in use was changed while the window was open.</summary>
    public bool ModelChanged { get; private set; }

    public ModelsForm(OllamaClient ollama, AppSettings settings, ISpeechOutput speech, Action<string> log)
    {
        _ollama = ollama;
        _settings = settings;
        _speech = speech;
        _log = log;
        BuildLayout();
        Shown += async (_, _) => await RefreshListAsync(selectName: _settings.Model, announce: true);
    }

    private void BuildLayout()
    {
        Text = "Models - Describer After Dark";
        AccessibleName = "Models";
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(760, 480);
        MinimumSize = new Size(520, 360);
        KeyPreview = true;
        ShowInTaskbar = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var listLabel = new Label { Text = "&Models (arrow through the list; each one says whether it's downloaded, its size, and what it's good for):", AutoSize = true };
        _list.Dock = DockStyle.Fill;
        _list.AccessibleName = "Models";
        _list.HorizontalScrollbar = true;
        _list.IntegralHeight = false;
        _list.Font = new Font(Font.FontFamily, 10.5f);
        _list.SelectedIndexChanged += (_, _) => UpdateButtons();
        _list.DoubleClick += async (_, _) => await UseSelectedAsync();

        _useButton.Text = "&Use this model";
        _downloadButton.Text = "&Download";
        _removeButton.Text = "&Remove";
        _addButton.Text = "&Add a model by name...";
        _closeButton.Text = "&Close";
        foreach (var b in new[] { _useButton, _downloadButton, _removeButton, _addButton, _closeButton }) b.AutoSize = true;
        _useButton.Click += async (_, _) => await UseSelectedAsync();
        _downloadButton.Click += async (_, _) =>
        {
            if (_download is not null) StopDownload();
            else if (Selected is { } e) await DownloadAsync(e.Name, e.Catalog?.DownloadSize, useAfterwards: false);
        };
        _removeButton.Click += async (_, _) => await RemoveSelectedAsync();
        _addButton.Click += async (_, _) => await AddByNameAsync();
        _closeButton.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 8, 0, 0) };
        buttons.Controls.AddRange(new Control[] { _useButton, _downloadButton, _removeButton, _addButton, _closeButton });

        _status.AutoSize = true;
        _status.AccessibleName = "Status";
        _status.Margin = new Padding(0, 8, 0, 0);
        _progress.Dock = DockStyle.Fill;
        _progress.AccessibleName = "Download progress";
        _progress.Visible = false;

        layout.Controls.Add(listLabel, 0, 0);
        layout.Controls.Add(_list, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        layout.Controls.Add(_status, 0, 3);
        layout.Controls.Add(_progress, 0, 4);
        Controls.Add(layout);

        var order = 0;
        foreach (var c in new Control[] { listLabel, _list, _useButton, _downloadButton, _removeButton, _addButton, _closeButton, _status, _progress })
            c.TabIndex = order++;

        AcceptButton = _useButton;
        CancelButton = _closeButton;
    }

    private ModelEntry? Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _entries.Count ? _entries[_list.SelectedIndex] : null;

    private void UpdateButtons()
    {
        var e = Selected;
        var downloading = _download is not null;
        _useButton.Enabled = !_working && e is not null && !(e.InUse && e.Installed) && e.CanSeeImages != false;
        _downloadButton.Enabled = downloading || (!_working && e is not null && !e.Installed);
        _downloadButton.Text = downloading ? "Stop &download" : "&Download";
        _removeButton.Enabled = !_working && e is not null && e.Installed && !e.InUse;
        _addButton.Enabled = !_working;
    }

    private void SetStatus(string text, bool speak = true)
    {
        if (!IsDisposed) _status.Text = text;
        if (speak) _speech.Say(text, interrupt: true);
    }

    // ---------------------------------------------------------------- list

    private async Task RefreshListAsync(string? selectName, bool announce)
    {
        _working = true;
        UpdateButtons();
        try
        {
            if (!await _ollama.EnsureRunningAsync())
            {
                SetStatus("Ollama isn't running and couldn't be started, so models can't be listed right now.");
                return;
            }

            var installed = await _ollama.ListModelsWithSizesAsync();
            var vision = new Dictionary<string, bool?>();
            foreach (var (name, _) in installed)
            {
                if (ModelCatalog.Find(name) is null) vision[name] = await _ollama.CanSeeImagesAsync(name);
            }

            _entries = ModelList.Build(installed, _settings.Model, vision);
            _list.BeginUpdate();
            _list.Items.Clear();
            foreach (var e in _entries) _list.Items.Add(e.Text);
            _list.EndUpdate();

            var wanted = selectName is null ? null : OllamaClient.NormalizeModelName(selectName);
            var index = _entries.ToList().FindIndex(e => wanted is not null &&
                string.Equals(OllamaClient.NormalizeModelName(e.Name), wanted, StringComparison.OrdinalIgnoreCase));
            _list.SelectedIndex = index >= 0 ? index : (_entries.Count > 0 ? 0 : -1);
            _list.Focus();

            var current = _entries.FirstOrDefault(e => e.InUse);
            var currentName = current?.Catalog?.Title ?? current?.Name ?? _settings.Model;
            if (announce)
                SetStatus($"{_entries.Count} models. The one in use is {currentName}. Arrow through the list, then press Enter to use a model, or Alt D to download it.");
        }
        catch (Exception ex)
        {
            _log("Models window: " + ex);
            SetStatus($"Couldn't list the models: {ex.Message}");
        }
        finally
        {
            _working = false;
            UpdateButtons();
        }
    }

    // ---------------------------------------------------------------- use

    private async Task UseSelectedAsync()
    {
        if (_working || Selected is not { } e) return;
        if (e.CanSeeImages == false)
        {
            SetStatus($"{e.Name} can't see images, so it can't describe pictures. Choose a different model.");
            return;
        }
        if (e.InUse && e.Installed)
        {
            SetStatus($"{e.Catalog?.Title ?? e.Name} is already in use.");
            return;
        }
        if (!e.Installed)
        {
            var size = e.Catalog?.DownloadSize is { } s ? $" ({s})" : "";
            var answer = MessageBox.Show(this,
                $"{e.Catalog?.Title ?? e.Name} isn't downloaded yet{size}. Download it now and then use it?",
                "Download model", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes) return;
            await DownloadAsync(e.Name, e.Catalog?.DownloadSize, useAfterwards: true);
            return;
        }
        Use(e.Name);
        await RefreshListAsync(e.Name, announce: false);
    }

    private void Use(string name)
    {
        _settings.Model = name;
        try { _settings.Save(); } catch (Exception ex) { _log("Saving settings: " + ex); }
        ModelChanged = true;
        _log($"Model changed to {name}");
        var title = ModelCatalog.Find(name)?.Title ?? name;
        SetStatus($"Now using {title}. The next picture you describe will use it.");
    }

    // ---------------------------------------------------------------- download

    private async Task DownloadAsync(string name, string? sizeText, bool useAfterwards)
    {
        if (_working) return;
        _working = true;
        _download = new CancellationTokenSource();
        _progress.Value = 0;
        _progress.Visible = true;
        UpdateButtons();

        var title = ModelCatalog.Find(name)?.Title ?? name;
        var size = sizeText is null ? "" : $" It's about {sizeText}, so this can take a while.";
        SetStatus($"Downloading {title}.{size} Progress is announced every 10 percent. Press the Stop download button to stop; it continues where it left off next time.");

        var lastSpoken = new Dictionary<string, int>();
        var progress = new Progress<PullProgress>(p =>
        {
            if (p.Percent < 0)
            {
                SetStatus($"Download: {p.Status}", speak: false);
                return;
            }
            if (!IsDisposed) _progress.Value = Math.Clamp(p.Percent, 0, 100);
            SetStatus($"Downloading {title}: {p.Percent} percent", speak: false);
            if (p.Total < 100_000_000) return; // only announce the big file, not the small pieces
            var step = p.Percent / 10 * 10;
            if (step > 0 && (!lastSpoken.TryGetValue(p.Status, out var last) || step > last))
            {
                lastSpoken[p.Status] = step;
                _speech.Say($"{step} percent", interrupt: true);
            }
        });

        var ok = false;
        try
        {
            if (!await _ollama.EnsureRunningAsync(_download.Token))
                throw new OllamaException("Ollama isn't running and couldn't be started.");
            await _ollama.PullModelAsync(name, progress, _download.Token);
            ok = true;
            _log($"Downloaded model {name}");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"Stopped downloading {title}. Choose Download again later and it continues where it left off.");
        }
        catch (Exception ex)
        {
            _log($"Downloading {name}: {ex}");
            SetStatus($"Downloading {title} failed: {ex.Message}");
        }
        finally
        {
            _download.Dispose();
            _download = null;
            _working = false;
            if (!IsDisposed)
            {
                _progress.Visible = false;
                UpdateButtons();
            }
        }

        if (!ok || IsDisposed) return;
        if (useAfterwards)
        {
            Use(name);
        }
        else
        {
            SetStatus($"{title} is downloaded. Press Enter on it in the list to start using it.");
        }
        await RefreshListAsync(name, announce: false);
    }

    private void StopDownload()
    {
        _download?.Cancel();
    }

    // ---------------------------------------------------------------- remove

    private async Task RemoveSelectedAsync()
    {
        if (_working || Selected is not { } e || !e.Installed) return;
        if (e.InUse)
        {
            SetStatus("That model is in use. Choose a different model first, then remove this one.");
            return;
        }
        var title = e.Catalog?.Title ?? e.Name;
        var answer = MessageBox.Show(this,
            $"Remove {title}? Its downloaded files are deleted to free up disk space. You can download it again later.",
            "Remove model", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) return;

        _working = true;
        UpdateButtons();
        try
        {
            await _ollama.DeleteModelAsync(e.Name);
            _log($"Removed model {e.Name}");
            SetStatus($"{title} was removed.");
        }
        catch (Exception ex)
        {
            _log($"Removing {e.Name}: {ex}");
            SetStatus($"Removing {title} failed: {ex.Message}");
        }
        finally
        {
            _working = false;
        }
        await RefreshListAsync(e.Name, announce: false);
    }

    // ---------------------------------------------------------------- add by name

    private async Task AddByNameAsync()
    {
        if (_working) return;
        var name = AskForModelName();
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();

        var existing = _entries.FirstOrDefault(e =>
            string.Equals(OllamaClient.NormalizeModelName(e.Name), OllamaClient.NormalizeModelName(name), StringComparison.OrdinalIgnoreCase));
        if (existing is { Installed: true })
        {
            SetStatus($"{name} is already downloaded. It's in the list.");
            await RefreshListAsync(existing.Name, announce: false);
            return;
        }

        await DownloadAsync(name, null, useAfterwards: false);

        // Warn straight away if it turns out to be a text-only model.
        try
        {
            if (await _ollama.HasModelAsync(name) && await _ollama.CanSeeImagesAsync(name) == false)
                SetStatus($"{name} is downloaded, but it's text only and can't see images, so it can't describe pictures. You can remove it from the list.");
        }
        catch
        {
            // Not being able to check isn't a problem; the list says what it can.
        }
    }

    private string? AskForModelName()
    {
        using var dialog = new Form
        {
            Text = "Add a model by name",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ClientSize = new Size(560, 170),
        };
        var label = new Label
        {
            Text = "&Model name, exactly as shown on ollama.com, for example llava:13b or huihui_ai/qwen3-vl-abliterated:8b-instruct. " +
                   "It must be a vision model to describe pictures.",
            Location = new Point(12, 12),
            Size = new Size(536, 60),
        };
        var box = new TextBox { Location = new Point(12, 78), Width = 536, AccessibleName = "Model name" };
        var ok = new Button { Text = "&Download", DialogResult = DialogResult.OK, Location = new Point(372, 120), AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(464, 120), AutoSize = true };
        dialog.Controls.AddRange(new Control[] { label, box, ok, cancel });
        label.TabIndex = 0; box.TabIndex = 1; ok.TabIndex = 2; cancel.TabIndex = 3;
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK ? box.Text : null;
    }

    // ---------------------------------------------------------------- closing

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_download is not null)
        {
            var answer = MessageBox.Show(this, "A model is still downloading. Stop the download and close? It continues where it left off next time.",
                "Download in progress", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            StopDownload();
        }
        base.OnFormClosing(e);
    }
}
