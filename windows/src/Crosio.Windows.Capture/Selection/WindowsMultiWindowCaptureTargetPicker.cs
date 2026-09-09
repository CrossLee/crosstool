using System.Drawing;

namespace Crosio.Windows.Capture.Selection;

/// <summary>
/// A compact native Windows picker backed by the same filtered top-level
/// window catalog used by single-window capture.
/// </summary>
public sealed class WindowsMultiWindowCaptureTargetPicker : IMultiWindowCaptureTargetPicker
{
    private readonly ICaptureTargetCatalog _catalog;

    public WindowsMultiWindowCaptureTargetPicker(ICaptureTargetCatalog catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public Task<IReadOnlyList<CaptureWindow>?> PickWindowsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var windows = _catalog
            .GetSnapshot(checked((uint)Environment.ProcessId))
            .Windows
            .ToArray();
        if (windows.Length == 0)
        {
            throw new ScreenshotCaptureException("当前没有可选择的可见顶层窗口。");
        }

        var completion = new TaskCompletionSource<IReadOnlyList<CaptureWindow>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunPicker(windows, cancellationToken, completion))
        {
            IsBackground = true,
            Name = "Crosio multi-window picker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RunPicker(
        IReadOnlyList<CaptureWindow> windows,
        CancellationToken cancellationToken,
        TaskCompletionSource<IReadOnlyList<CaptureWindow>?> completion)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var form = new MultiWindowPickerForm(windows);
            _ = form.Handle;
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!form.IsDisposed && form.IsHandleCreated)
                    {
                        form.BeginInvoke(form.CancelSelection);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The picker closed between the handle check and invoke.
                }
            });
            System.Windows.Forms.Application.Run(form);
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                completion.TrySetResult(form.SelectedWindows);
            }
        }
        catch (OperationCanceledException cancelled)
        {
            completion.TrySetCanceled(cancelled.CancellationToken);
        }
        catch (Exception error)
        {
            completion.TrySetException(new ScreenshotCaptureException(
                "多窗口选择器启动失败。",
                error));
        }
    }

    private sealed class MultiWindowPickerForm : Form
    {
        private static readonly Color Accent = Color.FromArgb(86, 92, 255);
        private readonly CheckedListBox _windowList;
        private readonly Label _status;
        private readonly IReadOnlyList<CaptureWindow> _windows;

        public MultiWindowPickerForm(IReadOnlyList<CaptureWindow> windows)
        {
            _windows = windows;
            Text = "Crosio · 多窗口截图";
            Crosio.Windows.Capture.Interop.WindowBranding.ApplyIcon(this);
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(620, 470);
            MinimumSize = new Size(480, 360);
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(247, 247, 250);

            var title = new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI", 17, FontStyle.Bold),
                ForeColor = Color.FromArgb(30, 30, 36),
                Location = new Point(24, 20),
                Text = "选择要合成的窗口",
            };
            var help = new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI", 10),
                ForeColor = Color.FromArgb(96, 96, 105),
                Location = new Point(26, 58),
                Text = "只会截取你在下面明确勾选的可见窗口；按 Esc 取消。",
            };
            _windowList = new CheckedListBox
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                BorderStyle = BorderStyle.FixedSingle,
                CheckOnClick = true,
                Font = new Font("Segoe UI", 10.5f),
                IntegralHeight = false,
                Location = new Point(24, 88),
                Size = new Size(572, 292),
            };
            foreach (var window in windows)
            {
                _windowList.Items.Add(new WindowEntry(window));
            }
            _windowList.ItemCheck += (_, _) => BeginInvoke(UpdateStatus);

            _status = new Label
            {
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = Color.FromArgb(96, 96, 105),
                Location = new Point(24, 401),
                Text = "尚未选择窗口",
            };
            var cancel = new Button
            {
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel,
                Font = new Font("Segoe UI", 10),
                Location = new Point(414, 398),
                Size = new Size(82, 36),
                Text = "取消",
            };
            cancel.Click += (_, _) => CancelSelection();
            var confirm = new Button
            {
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                BackColor = Accent,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10, FontStyle.Bold),
                ForeColor = Color.White,
                Location = new Point(506, 398),
                Size = new Size(90, 36),
                Text = "开始合成",
                UseVisualStyleBackColor = false,
            };
            confirm.FlatAppearance.BorderSize = 0;
            confirm.Click += (_, _) => ConfirmSelection();

            Controls.Add(title);
            Controls.Add(help);
            Controls.Add(_windowList);
            Controls.Add(_status);
            Controls.Add(cancel);
            Controls.Add(confirm);
            AcceptButton = confirm;
            CancelButton = cancel;
        }

        public IReadOnlyList<CaptureWindow>? SelectedWindows { get; private set; }

        public void CancelSelection()
        {
            SelectedWindows = null;
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs eventArgs)
        {
            if (eventArgs.KeyCode == Keys.Escape)
            {
                CancelSelection();
                eventArgs.Handled = true;
                return;
            }

            base.OnKeyDown(eventArgs);
        }

        private void ConfirmSelection()
        {
            var selected = _windowList.CheckedIndices
                .Cast<int>()
                .Select(index => _windows[index])
                .ToArray();
            if (selected.Length == 0)
            {
                _status.ForeColor = Color.FromArgb(190, 45, 50);
                _status.Text = "请至少勾选一个窗口，或按 Esc 取消。";
                return;
            }

            SelectedWindows = selected;
            Close();
        }

        private void UpdateStatus()
        {
            var count = _windowList.CheckedItems.Count;
            _status.ForeColor = Color.FromArgb(96, 96, 105);
            _status.Text = count == 0 ? "尚未选择窗口" : $"已选择 {count} 个窗口";
        }

        private sealed record WindowEntry(CaptureWindow Window)
        {
            public override string ToString()
            {
                var title = Window.Title.Length > 74
                    ? $"{Window.Title[..71]}…"
                    : Window.Title;
                return $"{title}    ({Window.Bounds.Width} × {Window.Bounds.Height})";
            }
        }
    }
}
