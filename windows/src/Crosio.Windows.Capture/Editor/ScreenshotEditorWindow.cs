using System.Drawing;
using Crosio.Windows.Capture.Clipboard;
using Crosio.Windows.Capture.Interop;
using Crosio.Windows.Capture.Pinning;

namespace Crosio.Windows.Capture.Editor;

internal sealed class ScreenshotEditorWindow : Form
{
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const long PreviousKeyStateMask = 1L << 30;

    private readonly CapturedImage _originalImage;
    private readonly IImageClipboard _clipboard;
    private readonly IPinnedScreenshotService _pins;
    private readonly IScreenshotEditorShareSink? _shareSink;
    private readonly IScreenshotEditorOcrService? _ocrService;
    private readonly ScreenshotAnnotationDocument _document;
    private readonly ScreenshotAnnotationCanvas _canvas;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Label _statusLabel;
    private readonly Button _copyButton;
    private readonly Button _saveButton;
    private readonly Button _shareButton;
    private readonly Button _pinButton;
    private readonly Button _closeButton;
    private readonly Button _undoButton;
    private readonly Button _redoButton;
    private readonly Button _resetButton;
    private readonly Button _colorButton;
    private readonly ContextMenuStrip _colorMenu;
    private readonly ComboBox _toolPicker;
    private readonly NumericUpDown _lineWidthPicker;
    private readonly NumericUpDown _mosaicBrushPicker;
    private readonly NumericUpDown _mosaicBlockPicker;
    private readonly TextBox _ocrText;
    private readonly Label _ocrStatus;
    private readonly Button _copyTextButton;
    private readonly Button _retryOcrButton;
    private bool _operationInFlight;
    private bool _ocrInFlight;

    internal ScreenshotEditorWindow(
        CapturedImage image,
        IImageClipboard clipboard,
        IPinnedScreenshotService pins,
        IScreenshotEditorShareSink? shareSink,
        IScreenshotEditorOcrService? ocrService,
        string? initialStatus)
    {
        _originalImage = image;
        _clipboard = clipboard;
        _pins = pins;
        _shareSink = shareSink;
        _ocrService = ocrService;
        _document = new ScreenshotAnnotationDocument(image);
        _canvas = new ScreenshotAnnotationCanvas(_document);
        _canvas.DocumentChanged += OnDocumentChanged;

        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(244, 244, 246);
        KeyPreview = true;
        MaximizeBox = true;
        MinimizeBox = true;
        MinimumSize = new Size(760, 480);
        Name = "CrosioScreenshotEditor";
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        Text = "截图 — Crosio";
        AccessibleName = "Crosio 截图编辑器";
        AccessibleDescription = "标注或复制截图，按 S 贴到屏幕，按 Esc 关闭";

        _toolPicker = new ComboBox
        {
            AccessibleName = "标注工具",
            DropDownStyle = ComboBoxStyle.DropDownList,
            Name = "AnnotationTool",
            Width = 104,
        };
        _toolPicker.Items.AddRange(["画笔", "马赛克", "矩形", "箭头"]);
        _toolPicker.SelectedIndex = 0;
        _toolPicker.SelectedIndexChanged += OnToolChanged;

        _colorButton = CreateButton("颜色", "AnnotationColor", 70);
        _colorButton.BackColor = _canvas.AnnotationColor;
        _colorButton.UseVisualStyleBackColor = false;
        _colorMenu = CreateColorMenu();
        _colorButton.Click += OnColorClicked;

        _lineWidthPicker = new NumericUpDown
        {
            AccessibleName = "线宽",
            Minimum = 1,
            Maximum = 48,
            Name = "AnnotationLineWidth",
            Value = 6,
            Width = 58,
        };
        _lineWidthPicker.ValueChanged += OnLineWidthChanged;

        _mosaicBrushPicker = new NumericUpDown
        {
            AccessibleName = "马赛克笔刷大小",
            Minimum = 8,
            Maximum = 240,
            Name = "MosaicBrushDiameter",
            Value = 40,
            Width = 58,
        };
        _mosaicBrushPicker.ValueChanged += OnMosaicBrushChanged;

        _mosaicBlockPicker = new NumericUpDown
        {
            AccessibleName = "马赛克颗粒大小",
            Minimum = 3,
            Maximum = 64,
            Name = "MosaicBlockSize",
            Value = 10,
            Width = 58,
        };
        _mosaicBlockPicker.ValueChanged += OnMosaicBlockChanged;

        _undoButton = CreateButton("撤销", "UndoAnnotation", 70);
        _redoButton = CreateButton("重做", "RedoAnnotation", 70);
        _resetButton = CreateButton("全部重置", "ResetAnnotations", 86);
        _undoButton.Click += OnUndoClicked;
        _redoButton.Click += OnRedoClicked;
        _resetButton.Click += OnResetClicked;

        var toolbar = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.White,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Name = "ScreenshotAnnotationToolbar",
            Padding = new Padding(12, 8, 12, 8),
            WrapContents = true,
        };
        toolbar.Controls.Add(CreateLabel("工具"));
        toolbar.Controls.Add(_toolPicker);
        toolbar.Controls.Add(_colorButton);
        toolbar.Controls.Add(CreateLabel("线宽"));
        toolbar.Controls.Add(_lineWidthPicker);
        toolbar.Controls.Add(CreateLabel("笔刷"));
        toolbar.Controls.Add(_mosaicBrushPicker);
        toolbar.Controls.Add(CreateLabel("颗粒"));
        toolbar.Controls.Add(_mosaicBlockPicker);
        toolbar.Controls.Add(_undoButton);
        toolbar.Controls.Add(_redoButton);
        toolbar.Controls.Add(_resetButton);

        _ocrText = new TextBox
        {
            AcceptsReturn = true,
            AccessibleName = "OCR 识别文字",
            BackColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle,
            Dock = DockStyle.Fill,
            Multiline = true,
            Name = "OcrRecognizedText",
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
        };
        _ocrStatus = new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(84, 84, 88),
            Name = "OcrStatus",
            Text = _ocrService is null ? "未连接本地 OCR 服务。" : "等待识别…",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _copyTextButton = CreateButton("复制文字", "CopyOcrText", 90);
        _retryOcrButton = CreateButton("重试", "RetryOcr", 70);
        _copyTextButton.Enabled = false;
        _retryOcrButton.Enabled = _ocrService is not null;
        _copyTextButton.Click += OnCopyTextClicked;
        _retryOcrButton.Click += OnRetryOcrClicked;

        var ocrButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = Padding.Empty,
            WrapContents = false,
        };
        ocrButtons.Controls.Add(_retryOcrButton);
        ocrButtons.Controls.Add(_copyTextButton);

        var ocrPanel = new TableLayoutPanel
        {
            BackColor = Color.FromArgb(249, 249, 250),
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Name = "ScreenshotOcrPanel",
            Padding = new Padding(12),
            RowCount = 4,
        };
        ocrPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        ocrPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        ocrPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        ocrPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        ocrPanel.Controls.Add(new Label
        {
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", 10, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 8),
            Text = "图片文字识别",
        }, 0, 0);
        ocrPanel.Controls.Add(_ocrText, 0, 1);
        ocrPanel.Controls.Add(_ocrStatus, 0, 2);
        ocrPanel.Controls.Add(ocrButtons, 0, 3);

        var workspace = new TableLayoutPanel
        {
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Name = "ScreenshotWorkspace",
            RowCount = 1,
        };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 280));
        workspace.Controls.Add(_canvas, 0, 0);
        workspace.Controls.Add(ocrPanel, 1, 0);

        _statusLabel = new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            Dock = DockStyle.Fill,
            ForeColor = Color.FromArgb(84, 84, 88),
            Name = "ScreenshotStatus",
            Text = string.IsNullOrWhiteSpace(initialStatus)
                ? "按 S 可贴到屏幕，按 Esc 关闭。"
                : initialStatus,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _copyButton = CreateButton("复制图片", "CopyScreenshot", 96);
        _saveButton = CreateButton("另存 PNG", "SaveScreenshot", 96);
        _shareButton = CreateButton("加入共享", "ShareScreenshot", 96);
        _pinButton = CreateButton("贴到屏幕（S）", "PinScreenshot", 116);
        _closeButton = CreateButton("关闭", "CloseScreenshotEditor", 76);
        _shareButton.Enabled = _shareSink is not null;
        _copyButton.Click += OnCopyClicked;
        _saveButton.Click += OnSaveClicked;
        _shareButton.Click += OnShareClicked;
        _pinButton.Click += OnPinClicked;
        _closeButton.Click += OnCloseClicked;

        var outputButtons = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Margin = Padding.Empty,
            Name = "ScreenshotActions",
            WrapContents = false,
        };
        outputButtons.Controls.Add(_closeButton);
        outputButtons.Controls.Add(_pinButton);
        outputButtons.Controls.Add(_shareButton);
        outputButtons.Controls.Add(_saveButton);
        outputButtons.Controls.Add(_copyButton);

        var footer = new TableLayoutPanel
        {
            AutoSize = true,
            BackColor = Color.White,
            ColumnCount = 2,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Name = "ScreenshotFooter",
            Padding = new Padding(14, 10, 14, 10),
            RowCount = 1,
        };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(_statusLabel, 0, 0);
        footer.Controls.Add(outputButtons, 1, 0);

        var root = new TableLayoutPanel
        {
            ColumnCount = 1,
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Name = "ScreenshotEditorRoot",
            RowCount = 3,
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(toolbar, 0, 0);
        root.Controls.Add(workspace, 0, 1);
        root.Controls.Add(footer, 0, 2);
        Controls.Add(root);

        OnToolChanged(this, EventArgs.Empty);
        UpdateHistoryButtons();
        ApplyInitialBounds(_document.SourcePixelSize);
        Shown += OnShownStartOcr;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Shown -= OnShownStartOcr;
            _canvas.DocumentChanged -= OnDocumentChanged;
            _toolPicker.SelectedIndexChanged -= OnToolChanged;
            _colorButton.Click -= OnColorClicked;
            _lineWidthPicker.ValueChanged -= OnLineWidthChanged;
            _mosaicBrushPicker.ValueChanged -= OnMosaicBrushChanged;
            _mosaicBlockPicker.ValueChanged -= OnMosaicBlockChanged;
            _undoButton.Click -= OnUndoClicked;
            _redoButton.Click -= OnRedoClicked;
            _resetButton.Click -= OnResetClicked;
            _copyButton.Click -= OnCopyClicked;
            _saveButton.Click -= OnSaveClicked;
            _shareButton.Click -= OnShareClicked;
            _pinButton.Click -= OnPinClicked;
            _closeButton.Click -= OnCloseClicked;
            _copyTextButton.Click -= OnCopyTextClicked;
            _retryOcrButton.Click -= OnRetryOcrClicked;
            _lifetime.Cancel();
            _lifetime.Dispose();
            _colorMenu.Dispose();
            _document.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Z))
        {
            Undo();
            return true;
        }

        if (keyData == (Keys.Control | Keys.Y))
        {
            Redo();
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override bool ProcessKeyPreview(ref Message message)
    {
        if (message.Msg != WmKeyDown && message.Msg != WmSysKeyDown)
        {
            return base.ProcessKeyPreview(ref message);
        }

        var virtualKey = message.WParam.ToInt32();
        if (virtualKey != PinnedScreenshotShortcut.VirtualKeyS
            && virtualKey != PinnedScreenshotShortcut.VirtualKeyEscape)
        {
            return base.ProcessKeyPreview(ref message);
        }

        var isRepeat = (message.LParam.ToInt64() & PreviousKeyStateMask) != 0;
        var decision = PinnedScreenshotShortcut.DecideEditorCommand(
            virtualKey,
            CurrentModifiers(),
            isRepeat,
            isEditorActive: ReferenceEquals(ActiveForm, this) || ContainsFocus,
            hasModalDialog: OwnedForms.Any(form => form.Modal),
            isEditingText: ActiveControl is TextBoxBase
                || ActiveControl is ComboBox { DropDownStyle: not ComboBoxStyle.DropDownList });
        switch (decision)
        {
            case ScreenshotShortcutDecision.PerformPin:
                _ = PinAndCloseAsync();
                return true;
            case ScreenshotShortcutDecision.CloseEditor:
                BeginInvoke(Close);
                return true;
            case ScreenshotShortcutDecision.Consume:
                return true;
            default:
                return base.ProcessKeyPreview(ref message);
        }
    }

    private static Button CreateButton(string text, string name, int width) => new()
    {
        AutoSize = true,
        MinimumSize = new Size(width, 34),
        Name = name,
        Padding = new Padding(8, 3, 8, 3),
        Text = text,
        UseVisualStyleBackColor = true,
    };

    private static Label CreateLabel(string text) => new()
    {
        AutoSize = true,
        Margin = new Padding(8, 8, 4, 0),
        Text = text,
    };

    private ContextMenuStrip CreateColorMenu()
    {
        var menu = new ContextMenuStrip();
        AddPresetColor(menu, "红色", Color.FromArgb(255, 255, 62, 70));
        AddPresetColor(menu, "黄色", Color.FromArgb(255, 255, 184, 0));
        AddPresetColor(menu, "绿色", Color.FromArgb(255, 41, 184, 92));
        AddPresetColor(menu, "蓝色", Color.FromArgb(255, 55, 116, 255));
        AddPresetColor(menu, "黑色", Color.FromArgb(255, 25, 25, 28));
        menu.Items.Add(new ToolStripSeparator());
        var custom = new ToolStripMenuItem("自定义…");
        custom.Click += OnCustomColorClicked;
        menu.Items.Add(custom);
        return menu;
    }

    private void AddPresetColor(ContextMenuStrip menu, string name, Color color)
    {
        var item = new ToolStripMenuItem(name)
        {
            BackColor = color,
            ForeColor = color.GetBrightness() < 0.45 ? Color.White : Color.Black,
            Tag = color,
        };
        item.Click += OnPresetColorClicked;
        menu.Items.Add(item);
    }

    private void OnShownStartOcr(object? sender, EventArgs eventArgs)
    {
        if (_ocrService is not null)
        {
            _ = RecognizeTextAsync();
        }
    }

    private void OnToolChanged(object? sender, EventArgs eventArgs)
    {
        _canvas.Tool = (ScreenshotAnnotationTool)Math.Max(0, _toolPicker.SelectedIndex);
        _colorButton.Enabled = _canvas.Tool != ScreenshotAnnotationTool.Mosaic;
        _lineWidthPicker.Enabled = _canvas.Tool != ScreenshotAnnotationTool.Mosaic;
        _mosaicBrushPicker.Enabled = _canvas.Tool == ScreenshotAnnotationTool.Mosaic;
        _mosaicBlockPicker.Enabled = _canvas.Tool == ScreenshotAnnotationTool.Mosaic;
    }

    private void OnColorClicked(object? sender, EventArgs eventArgs)
    {
        _colorMenu.Show(_colorButton, new Point(0, _colorButton.Height));
    }

    private void OnPresetColorClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is ToolStripMenuItem { Tag: Color color })
        {
            SetAnnotationColor(color);
        }
    }

    private void OnCustomColorClicked(object? sender, EventArgs eventArgs)
    {
        using var dialog = new ColorDialog
        {
            AllowFullOpen = true,
            AnyColor = true,
            Color = _canvas.AnnotationColor,
            FullOpen = true,
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            SetAnnotationColor(dialog.Color);
        }
    }

    private void SetAnnotationColor(Color color)
    {
        _canvas.AnnotationColor = color;
        _colorButton.BackColor = color;
        _colorButton.ForeColor = color.GetBrightness() < 0.45 ? Color.White : Color.Black;
    }

    private void OnLineWidthChanged(object? sender, EventArgs eventArgs) =>
        _canvas.LineWidth = (float)_lineWidthPicker.Value;

    private void OnMosaicBrushChanged(object? sender, EventArgs eventArgs) =>
        _canvas.MosaicBrushDiameter = (float)_mosaicBrushPicker.Value;

    private void OnMosaicBlockChanged(object? sender, EventArgs eventArgs)
    {
        _canvas.MosaicBlockSize = (int)_mosaicBlockPicker.Value;
    }

    private void OnDocumentChanged(object? sender, EventArgs eventArgs) => UpdateHistoryButtons();

    private void OnUndoClicked(object? sender, EventArgs eventArgs) => Undo();

    private void OnRedoClicked(object? sender, EventArgs eventArgs) => Redo();

    private void OnResetClicked(object? sender, EventArgs eventArgs)
    {
        if (_document.Reset())
        {
            _canvas.RefreshFromDocument();
            SetStatus("已重置全部标注，可撤销。", isError: false);
        }
    }

    private void OnCopyClicked(object? sender, EventArgs eventArgs) => _ = CopyImageAsync();

    private void OnSaveClicked(object? sender, EventArgs eventArgs) => _ = SaveAsync();

    private void OnShareClicked(object? sender, EventArgs eventArgs) => _ = ShareAsync();

    private void OnPinClicked(object? sender, EventArgs eventArgs) => _ = PinAndCloseAsync();

    private void OnCloseClicked(object? sender, EventArgs eventArgs) => Close();

    private void OnCopyTextClicked(object? sender, EventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(_ocrText.Text))
        {
            return;
        }

        try
        {
            System.Windows.Forms.Clipboard.SetText(_ocrText.Text);
            _ocrStatus.Text = "已复制识别文字。";
        }
        catch (Exception error)
        {
            _ocrStatus.Text = $"复制文字失败：{error.Message}";
        }
    }

    private void OnRetryOcrClicked(object? sender, EventArgs eventArgs) => _ = RecognizeTextAsync();

    private void Undo()
    {
        if (_document.Undo())
        {
            _canvas.RefreshFromDocument();
            SetStatus("已撤销。", isError: false);
        }
    }

    private void Redo()
    {
        if (_document.Redo())
        {
            _canvas.RefreshFromDocument();
            SetStatus("已重做。", isError: false);
        }
    }

    private async Task CopyImageAsync()
    {
        if (!TryBeginOperation("正在复制标注图片…"))
        {
            return;
        }

        try
        {
            var snapshot = _document.CreateOutputSnapshot();
            await _clipboard.WritePngAsync(snapshot.Image, _lifetime.Token);
            SetStatus("已复制当前标注图片。", isError: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            SetStatus($"复制失败：{error.Message}", isError: true);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task SaveAsync()
    {
        using var dialog = new SaveFileDialog
        {
            AddExtension = true,
            DefaultExt = "png",
            FileName = $"Crosio-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            Filter = "PNG 图片 (*.png)|*.png",
            OverwritePrompt = true,
            Title = "另存截图为 PNG",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK
            || !TryBeginOperation("正在保存 PNG…"))
        {
            return;
        }

        try
        {
            var snapshot = _document.CreateOutputSnapshot();
            await File.WriteAllBytesAsync(
                dialog.FileName,
                snapshot.Image.CopyPngBytes(),
                _lifetime.Token);
            SetStatus("已保存 PNG。", isError: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            SetStatus($"保存失败：{error.Message}", isError: true);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task ShareAsync()
    {
        if (_shareSink is null || !TryBeginOperation("正在加入课堂共享区…"))
        {
            return;
        }

        try
        {
            var snapshot = _document.CreateOutputSnapshot();
            await _shareSink.ShareAsync(snapshot.Image, _lifetime.Token);
            SetStatus("已加入课堂共享区。", isError: false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            SetStatus($"共享失败：{error.Message}", isError: true);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task PinAndCloseAsync()
    {
        if (!TryBeginOperation("正在贴到屏幕…"))
        {
            return;
        }

        try
        {
            var snapshot = _document.CreateOutputSnapshot();
            _ = await _pins.PinAsync(
                snapshot.Image,
                new PinnedScreenshotOptions(SelectForKeyboard: true),
                _lifetime.Token);
            if (!IsDisposed && _document.Version == snapshot.Version)
            {
                Close();
            }
            else
            {
                SetStatus("贴图已创建；标注随后有变化，编辑器继续保留。", isError: false);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            SetStatus($"贴图失败：{error.Message}", isError: true);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task RecognizeTextAsync()
    {
        if (_ocrService is null || _ocrInFlight || IsDisposed)
        {
            return;
        }

        _ocrInFlight = true;
        _retryOcrButton.Enabled = false;
        _copyTextButton.Enabled = false;
        _ocrStatus.Text = "识别中…";
        try
        {
            var text = await _ocrService.RecognizeAsync(_originalImage, _lifetime.Token);
            if (IsDisposed)
            {
                return;
            }

            _ocrText.Text = text;
            _copyTextButton.Enabled = !string.IsNullOrWhiteSpace(text);
            _ocrStatus.Text = string.IsNullOrWhiteSpace(text)
                ? "没有识别到文字。"
                : "识别完成；图片剪贴板未被改动。";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            if (!IsDisposed)
            {
                _ocrStatus.Text = $"识别失败：{error.Message}";
            }
        }
        finally
        {
            _ocrInFlight = false;
            if (!IsDisposed)
            {
                _retryOcrButton.Enabled = true;
            }
        }
    }

    private bool TryBeginOperation(string status)
    {
        if (_operationInFlight || IsDisposed)
        {
            return false;
        }

        _operationInFlight = true;
        SetOutputButtonsEnabled(false);
        SetStatus(status, isError: false);
        return true;
    }

    private void EndOperation()
    {
        if (IsDisposed)
        {
            return;
        }

        _operationInFlight = false;
        SetOutputButtonsEnabled(true);
    }

    private void SetOutputButtonsEnabled(bool enabled)
    {
        _copyButton.Enabled = enabled;
        _saveButton.Enabled = enabled;
        _shareButton.Enabled = enabled && _shareSink is not null;
        _pinButton.Enabled = enabled;
    }

    private void UpdateHistoryButtons()
    {
        _undoButton.Enabled = _document.CanUndo;
        _redoButton.Enabled = _document.CanRedo;
        _resetButton.Enabled = _document.HasAnnotations;
    }

    private void SetStatus(string message, bool isError)
    {
        if (IsDisposed)
        {
            return;
        }

        _statusLabel.ForeColor = isError
            ? Color.FromArgb(184, 40, 40)
            : Color.FromArgb(84, 84, 88);
        _statusLabel.Text = message;
    }

    private void ApplyInitialBounds(Size sourcePixelSize)
    {
        var screen = Screen.FromPoint(Cursor.Position);
        var workingArea = screen.WorkingArea;
        var maximumWidth = Math.Max(760, (int)Math.Floor(workingArea.Width * 0.9));
        var maximumHeight = Math.Max(480, (int)Math.Floor(workingArea.Height * 0.86));
        const int chromeWidth = 280;
        const int chromeHeight = 124;
        var scale = Math.Min(
            1,
            Math.Min(
                Math.Max(1, maximumWidth - chromeWidth) / (double)sourcePixelSize.Width,
                Math.Max(1, maximumHeight - chromeHeight) / (double)sourcePixelSize.Height));
        var width = Math.Clamp(
            (int)Math.Round(sourcePixelSize.Width * scale) + chromeWidth,
            760,
            maximumWidth);
        var height = Math.Clamp(
            (int)Math.Round(sourcePixelSize.Height * scale) + chromeHeight,
            480,
            maximumHeight);
        Bounds = new Rectangle(
            workingArea.Left + ((workingArea.Width - width) / 2),
            workingArea.Top + ((workingArea.Height - height) / 2),
            width,
            height);
    }

    private static ScreenshotShortcutModifiers CurrentModifiers()
    {
        var keys = ModifierKeys;
        var result = ScreenshotShortcutModifiers.None;
        if ((keys & Keys.Control) != 0)
        {
            result |= ScreenshotShortcutModifiers.Control;
        }

        if ((keys & Keys.Alt) != 0)
        {
            result |= ScreenshotShortcutModifiers.Alt;
        }

        if ((keys & Keys.Shift) != 0)
        {
            result |= ScreenshotShortcutModifiers.Shift;
        }

        if ((NativeMethods.GetAsyncKeyState(NativeMethods.VkLeftWindows) & 0x8000) != 0
            || (NativeMethods.GetAsyncKeyState(NativeMethods.VkRightWindows) & 0x8000) != 0)
        {
            result |= ScreenshotShortcutModifiers.Windows;
        }

        return result;
    }
}
