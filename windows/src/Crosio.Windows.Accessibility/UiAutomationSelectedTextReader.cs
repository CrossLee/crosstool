using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace Crosio.Windows.Accessibility;

public sealed class UiAutomationSelectedTextReader : ISelectedTextReader
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);
    public const int MaximumSelectedTextCharacters = 32 * 1024;
    private readonly object _gate = new();
    private readonly TimeSpan _timeout;
    private Task<SelectedTextResult>? _inFlight;

    public UiAutomationSelectedTextReader(TimeSpan? timeout = null)
    {
        _timeout = timeout ?? DefaultTimeout;
        if (_timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }
    }

    public async Task<SelectedTextResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        Task<SelectedTextResult> read;
        lock (_gate)
        {
            // UI Automation providers are out-of-process and a broken provider
            // can block a COM call indefinitely. Never create another worker
            // while one is still running; repeated hotkeys fail closed instead
            // of leaking threads or consuming a stale selection result.
            if (_inFlight is not null && !_inFlight.IsCompleted)
            {
                // Never let a later hotkey consume a selection result that was
                // captured for an earlier foreground application.
                return SelectedTextResult.Failure(
                    SelectedTextIssue.TimedOut,
                    "上一次读取仍未结束，请稍后在目标软件中重新选择文字");
            }
            _inFlight = StartAutomationRead();
            read = _inFlight;
        }

        try
        {
            return await read.WaitAsync(_timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return SelectedTextResult.Failure(
                SelectedTextIssue.TimedOut,
                "读取选中文字超时，请在目标软件中重新选择后再试");
        }
    }

    private static Task<SelectedTextResult> StartAutomationRead()
    {
        var completion = new TaskCompletionSource<SelectedTextResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SelectedTextResult result;
            try
            {
                result = ReadOnAutomationThread();
            }
            catch
            {
                // No exception from a third-party UI Automation provider may
                // escape a background thread and terminate Crosio.
                result = SelectedTextResult.Failure(
                    SelectedTextIssue.Failed,
                    "无法读取当前选中文字，请重新选择后再试");
            }
            completion.TrySetResult(result);
        })
        {
            IsBackground = true,
            Name = "Crosio selected-text reader",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return completion.Task;
    }

    private static SelectedTextResult ReadOnAutomationThread()
    {
        try
        {
            var focused = AutomationElement.FocusedElement;
            if (focused is null)
            {
                return SelectedTextResult.Failure(
                    SelectedTextIssue.TargetClosed,
                    "没有找到当前正在输入的窗口");
            }

            if (!focused.TryGetCurrentPattern(TextPattern.Pattern, out var patternObject) ||
                patternObject is not TextPattern pattern)
            {
                return SelectedTextResult.Failure(
                    SelectedTextIssue.UnsupportedControl,
                    "当前软件没有向 Windows 提供文字选区");
            }

            var ranges = pattern.GetSelection();
            if (ranges is null || ranges.Length == 0)
            {
                return SelectedTextResult.Failure(
                    SelectedTextIssue.EmptySelection,
                    "请先选中需要翻译的文字");
            }

            var selectedBuilder = new StringBuilder();
            foreach (var range in ranges)
            {
                var separatorLength = selectedBuilder.Length == 0
                    ? 0
                    : Environment.NewLine.Length;
                var remaining = MaximumSelectedTextCharacters - selectedBuilder.Length - separatorLength;
                if (remaining <= 0)
                {
                    break;
                }

                var text = range.GetText(remaining).Trim();
                if (text.Length == 0)
                {
                    continue;
                }
                if (selectedBuilder.Length > 0)
                {
                    selectedBuilder.AppendLine();
                }
                selectedBuilder.Append(text.AsSpan(0, Math.Min(text.Length, remaining)));
            }
            var selected = selectedBuilder.ToString().Trim();
            return selected.Length == 0
                ? SelectedTextResult.Failure(
                    SelectedTextIssue.EmptySelection,
                    "请先选中需要翻译的文字")
                : SelectedTextResult.Success(selected);
        }
        catch (ElementNotAvailableException)
        {
            return SelectedTextResult.Failure(
                SelectedTextIssue.TargetClosed,
                "目标窗口已经关闭，请重新选择文字");
        }
        catch (UnauthorizedAccessException)
        {
            return SelectedTextResult.Failure(
                SelectedTextIssue.AccessDenied,
                "无法读取管理员权限窗口中的选中文字");
        }
        catch (COMException error) when (error.HResult is unchecked((int)0x80070005))
        {
            return SelectedTextResult.Failure(
                SelectedTextIssue.AccessDenied,
                "Windows 阻止了对当前窗口文字选区的访问");
        }
        catch (Exception error) when (error is COMException or InvalidOperationException)
        {
            return SelectedTextResult.Failure(
                SelectedTextIssue.Failed,
                "无法读取当前选中文字，请重新选择后再试");
        }
    }
}
