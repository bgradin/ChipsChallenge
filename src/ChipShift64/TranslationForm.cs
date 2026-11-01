using Translator.Delivery;

namespace ChipShift64;

internal sealed class TranslationForm : Form
{
    private readonly TextBox _source = new()
    {
        ReadOnly = true,
        Dock = DockStyle.Fill,
        AccessibleName = "Original CHIPS.EXE"
    };
    private readonly TextBox _destination = new()
    {
        ReadOnly = true,
        Dock = DockStyle.Fill,
        AccessibleName = "Output executable"
    };
    private readonly Button _browse = new()
    {
        Text = "Choose CHIPS.EXE…",
        AutoSize = true
    };
    private readonly Button _chooseOutput = new()
    {
        Text = "Change output…",
        AutoSize = true,
        Enabled = false
    };
    private readonly Button _translate = new()
    {
        Text = "Translate",
        AutoSize = true,
        Enabled = false
    };
    private readonly Button _cancel = new()
    {
        Text = "Cancel",
        AutoSize = true,
        Enabled = false
    };
    private readonly Button _openFolder = new()
    {
        Text = "Open output folder",
        AutoSize = true,
        Enabled = false
    };
    private readonly Label _status = new()
    {
        Text = "Choose your original CHIPS.EXE to begin.",
        AutoSize = true,
        MaximumSize = new(650, 0),
        AccessibleName = "Translation status"
    };
    private readonly ProgressBar _progress = new()
    {
        Dock = DockStyle.Fill,
        Style = ProgressBarStyle.Marquee,
        Visible = false
    };
    private CancellationTokenSource? _cancellation;
    private bool _closeWhenDone;

    public TranslationForm()
    {
        Text = "ChipShift64";
        Name = "TranslationForm";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(720, 250);
        MinimumSize = new(640, 289);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new(12),
            ColumnCount = 2,
            RowCount = 8
        };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new(SizeType.AutoSize));
        for (var row = 0; row < 7; row++)
        {
            layout.RowStyles.Add(new(SizeType.AutoSize));
        }

        layout.RowStyles.Add(new(SizeType.Percent, 100));
        var intro = new Label
        {
            Text = "Translate your original Chip’s Challenge into a native Windows executable.",
            AutoSize = true,
            Margin = new(3, 0, 3, 10)
        };
        layout.Controls.Add(intro, 0, 0);
        layout.SetColumnSpan(intro, 2);
        layout.Controls.Add(new Label { Text = "Original application", AutoSize = true }, 0, 1);
        layout.Controls.Add(_source, 0, 2);
        layout.Controls.Add(_browse, 1, 2);
        layout.Controls.Add(new Label { Text = "Output (existing files are never replaced)", AutoSize = true }, 0, 3);
        layout.Controls.Add(_destination, 0, 4);
        layout.Controls.Add(_chooseOutput, 1, 4);
        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new(0, 6, 0, 6)
        };
        buttons.Controls.AddRange([_translate, _cancel, _openFolder]);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 2);
        layout.Controls.Add(_progress, 0, 6);
        layout.SetColumnSpan(_progress, 2);
        layout.Controls.Add(_status, 0, 7);
        layout.SetColumnSpan(_status, 2);
        layout.SizeChanged += (_, _) =>
        {
            var width = Math.Max(1, layout.ClientSize.Width - layout.Padding.Horizontal - _status.Margin.Horizontal);
            intro.MaximumSize = _status.MaximumSize = new(width, 0);
        };
        Controls.Add(layout);
        _browse.Click += (_, _) => SelectSource();
        _chooseOutput.Click += (_, _) => SelectOutput();
        _translate.Click += async (_, _) => await Translate();
        _cancel.Click += (_, _) =>
        {
            _cancellation?.Cancel();
            _cancel.Enabled = false;
            _status.Text = "Cancelling; staged files will be removed…";
        };
        _openFolder.Click += (_, _) =>
        {
            var start = new System.Diagnostics.ProcessStartInfo("explorer.exe")
            {
                UseShellExecute = true
            };
            start.ArgumentList.Add(Path.GetDirectoryName(_destination.Text)!);
            System.Diagnostics.Process.Start(start);
        };
        Shown += (_, _) => SelectSource();
        FormClosing += (_, e) =>
        {
            if (_cancellation is null)
            {
                return;
            }

            e.Cancel = true;
            _closeWhenDone = true;
            _cancellation.Cancel();
            _status.Text = "Cancelling before closing…";
        };
    }

    private void SelectSource()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Choose your original CHIPS.EXE",
            Filter = "Chip's Challenge (CHIPS.EXE)|CHIPS.EXE|Executables (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _source.Text = dialog.FileName;
        _destination.Text = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, "CHIPS64.EXE");
        _chooseOutput.Enabled = _translate.Enabled = true;
        _openFolder.Enabled = false;
        _status.Text = "Ready. Original data, saves and existing output files will be preserved.";
    }

    private void SelectOutput()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Choose a new output filename",
            Filter = "Windows executable (*.exe)|*.exe",
            FileName = _destination.Text,
            AddExtension = true,
            DefaultExt = "exe",
            OverwritePrompt = false,
            CheckPathExists = true
        };
        // No overwrite prompt: the service explicitly refuses every existing destination.
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _destination.Text = dialog.FileName;
            _openFolder.Enabled = false;
        }
    }

    private async Task Translate()
    {
        _cancellation = new();
        _browse.Enabled = _chooseOutput.Enabled = _translate.Enabled = _openFolder.Enabled = false;
        _cancel.Enabled = true;
        _progress.Visible = true;
        try
        {
            var current = _cancellation;
            var reporter = new Progress<TranslationProgress>(p =>
            {
                if (_cancellation == current && !current.IsCancellationRequested)
                {
                    _status.Text = p.Message;
                }
            });
            var result = await new TranslationService().TranslateAsync(new(_source.Text, _destination.Text), reporter, _cancellation.Token);
            _status.Text = $"Created {result.Output}. Launch it directly.";
            _openFolder.Enabled = true;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Cancelled. Existing files were preserved.";
        }
        catch (Exception e)
        {
            _status.Text = "Translation failed: " + e.Message;
            if (!_closeWhenDone)
            {
                MessageBox.Show(this, e.Message, "Translation could not complete", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            _progress.Visible = false;
            _cancel.Enabled = false;
            _browse.Enabled = _chooseOutput.Enabled = _translate.Enabled = true;
            if (_closeWhenDone)
            {
                Close();
            }
        }
    }
}
