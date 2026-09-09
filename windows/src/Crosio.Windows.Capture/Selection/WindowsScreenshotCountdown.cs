using System.Diagnostics;
using System.Drawing;

namespace Crosio.Windows.Capture.Selection;

public interface IScreenshotCountdown
{
    Task<bool> RunAsync(
        PixelRect displayBounds,
        int seconds,
        CancellationToken cancellationToken = default);
}

public static class ScreenshotCountdownSchedule
{
    public static int RemainingSeconds(int totalSeconds, TimeSpan elapsed)
    {
        if (totalSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalSeconds));
        }

        return Math.Max(
            0,
            (int)Math.Ceiling(totalSeconds - Math.Max(0, elapsed.TotalSeconds)));
    }
}

/// <summary>
/// A brand-colored, per-display countdown. The form closes before the task
/// completes, ensuring it cannot leak into the subsequent screen capture.
/// </summary>
public sealed class WindowsScreenshotCountdown : IScreenshotCountdown
{
    public Task<bool> RunAsync(
        PixelRect displayBounds,
        int seconds,
        CancellationToken cancellationToken = default)
    {
        if (seconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => RunCountdown(
            displayBounds,
            seconds,
            cancellationToken,
            completion))
        {
            IsBackground = true,
            Name = "Crosio screenshot countdown",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void RunCountdown(
        PixelRect displayBounds,
        int seconds,
        CancellationToken cancellationToken,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var form = new CountdownForm(displayBounds, seconds);
            _ = form.Handle;
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!form.IsDisposed && form.IsHandleCreated)
                    {
                        form.BeginInvoke(form.CancelCountdown);
                    }
                }
                catch (InvalidOperationException)
                {
                    // The countdown completed between check and invoke.
                }
            });
            System.Windows.Forms.Application.Run(form);
            if (cancellationToken.IsCancellationRequested)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                completion.TrySetResult(form.WasCompleted);
            }
        }
        catch (OperationCanceledException cancelled)
        {
            completion.TrySetCanceled(cancelled.CancellationToken);
        }
        catch (Exception error)
        {
            completion.TrySetException(new ScreenshotCaptureException(
                "截图倒计时启动失败。",
                error));
        }
    }

    private sealed class CountdownForm : Form
    {
        private static readonly Color Accent = Color.FromArgb(86, 92, 255);
        private readonly Label _number;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly Stopwatch _stopwatch = new();
        private readonly int _seconds;

        public CountdownForm(PixelRect displayBounds, int seconds)
        {
            _seconds = seconds;
            FormBorderStyle = FormBorderStyle.None;
            Bounds = new Rectangle(
                displayBounds.Left,
                displayBounds.Top,
                displayBounds.Width,
                displayBounds.Height);
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            ShowInTaskbar = false;
            KeyPreview = true;
            BackColor = Color.FromArgb(17, 18, 25);
            Opacity = 0.82;
            AutoScaleMode = AutoScaleMode.Dpi;

            var panelSize = Math.Clamp(Math.Min(Width, Height) / 3, 220, 360);
            var panel = new Panel
            {
                BackColor = Color.FromArgb(30, 31, 43),
                Size = new Size(panelSize, panelSize),
                Location = new Point((Width - panelSize) / 2, (Height - panelSize) / 2),
            };
            _number = new Label
            {
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", Math.Clamp(panelSize / 3.2f, 64, 112), FontStyle.Bold),
                ForeColor = Accent,
                Text = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
                TextAlign = ContentAlignment.MiddleCenter,
            };
            var help = new Label
            {
                BackColor = Color.Transparent,
                Dock = DockStyle.Bottom,
                Font = new Font("Segoe UI", 11),
                ForeColor = Color.White,
                Height = 52,
                Text = "准备带壳截图 · Esc 取消",
                TextAlign = ContentAlignment.MiddleCenter,
            };
            panel.Controls.Add(_number);
            panel.Controls.Add(help);
            Controls.Add(panel);

            _timer = new System.Windows.Forms.Timer { Interval = 50 };
            _timer.Tick += OnTick;
            Shown += (_, _) =>
            {
                Activate();
                _stopwatch.Start();
                _timer.Start();
            };
            MouseDown += (_, eventArgs) =>
            {
                if (eventArgs.Button == MouseButtons.Right)
                {
                    CancelCountdown();
                }
            };
        }

        public bool WasCompleted { get; private set; }

        public void CancelCountdown()
        {
            WasCompleted = false;
            Close();
        }

        protected override void OnKeyDown(KeyEventArgs eventArgs)
        {
            if (eventArgs.KeyCode == Keys.Escape)
            {
                CancelCountdown();
                eventArgs.Handled = true;
                return;
            }

            base.OnKeyDown(eventArgs);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
            }
            base.Dispose(disposing);
        }

        private void OnTick(object? sender, EventArgs eventArgs)
        {
            var remaining = ScreenshotCountdownSchedule.RemainingSeconds(
                _seconds,
                _stopwatch.Elapsed);
            if (remaining <= 0)
            {
                _timer.Stop();
                WasCompleted = true;
                Close();
                return;
            }

            _number.Text = remaining.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
