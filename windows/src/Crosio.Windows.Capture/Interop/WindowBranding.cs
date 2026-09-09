namespace Crosio.Windows.Capture.Interop;

internal static class WindowBranding
{
    public static void ApplyIcon(Form window)
    {
        var icon = new System.Drawing.Icon(
            Path.Combine(AppContext.BaseDirectory, "Assets", "OnePaw.ico"));
        window.Icon = icon;
        window.Disposed += (_, _) => icon.Dispose();
    }
}
