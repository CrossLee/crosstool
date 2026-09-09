namespace Crosio.Windows.Capture.Selection;

public interface IMultiWindowCaptureTargetPicker
{
    /// <summary>
    /// Returns only windows explicitly checked by the user. Null means the
    /// picker was cancelled and must never be widened to a screen capture.
    /// </summary>
    Task<IReadOnlyList<CaptureWindow>?> PickWindowsAsync(
        CancellationToken cancellationToken = default);
}
