namespace Crosio.Windows.Capture.Pinning;

[Flags]
public enum ScreenshotShortcutModifiers
{
    None = 0,
    Control = 1 << 0,
    Alt = 1 << 1,
    Shift = 1 << 2,
    Windows = 1 << 3,
}

public enum ScreenshotShortcutDecision
{
    Ignore,
    Consume,
    PerformPin,
    ClosePin,
    CloseEditor,
}

public static class PinnedScreenshotShortcut
{
    public const int VirtualKeyS = 0x53;
    public const int VirtualKeyEscape = 0x1B;

    public static ScreenshotShortcutDecision DecideEditorPin(
        int virtualKey,
        ScreenshotShortcutModifiers modifiers,
        bool isRepeat,
        bool isEditorActive,
        bool hasModalDialog,
        bool isEditingText)
    {
        if (!isEditorActive
            || hasModalDialog
            || isEditingText
            || virtualKey != VirtualKeyS
            || modifiers != ScreenshotShortcutModifiers.None)
        {
            return ScreenshotShortcutDecision.Ignore;
        }

        return isRepeat
            ? ScreenshotShortcutDecision.Consume
            : ScreenshotShortcutDecision.PerformPin;
    }

    /// <summary>
    /// Resolves the two editor-local shortcuts without installing a global
    /// keyboard hook. Bare S creates one pin and bare Escape closes the
    /// editor; repeats are consumed so key repeat cannot duplicate work.
    /// </summary>
    public static ScreenshotShortcutDecision DecideEditorCommand(
        int virtualKey,
        ScreenshotShortcutModifiers modifiers,
        bool isRepeat,
        bool isEditorActive,
        bool hasModalDialog,
        bool isEditingText)
    {
        if (!isEditorActive || hasModalDialog || modifiers != ScreenshotShortcutModifiers.None)
        {
            return ScreenshotShortcutDecision.Ignore;
        }

        if (virtualKey == VirtualKeyEscape)
        {
            return isRepeat
                ? ScreenshotShortcutDecision.Consume
                : ScreenshotShortcutDecision.CloseEditor;
        }

        return DecideEditorPin(
            virtualKey,
            modifiers,
            isRepeat,
            isEditorActive,
            hasModalDialog,
            isEditingText);
    }

    public static ScreenshotShortcutDecision DecidePinnedWindowClose(
        int virtualKey,
        ScreenshotShortcutModifiers modifiers,
        bool isRepeat,
        bool isPinnedWindowActive,
        bool hasModalDialog = false)
    {
        if (!isPinnedWindowActive
            || hasModalDialog
            || virtualKey != VirtualKeyEscape
            || modifiers != ScreenshotShortcutModifiers.None)
        {
            return ScreenshotShortcutDecision.Ignore;
        }

        return isRepeat
            ? ScreenshotShortcutDecision.Consume
            : ScreenshotShortcutDecision.ClosePin;
    }
}
