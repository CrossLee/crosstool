using Crosio.Windows.Capture.Pinning;

namespace Crosio.Windows.Capture.Tests;

public sealed class PinnedScreenshotShortcutTests
{
    [Fact]
    public void FirstBareSInActiveNonTextEditorPins()
    {
        var decision = PinnedScreenshotShortcut.DecideEditorPin(
            PinnedScreenshotShortcut.VirtualKeyS,
            ScreenshotShortcutModifiers.None,
            isRepeat: false,
            isEditorActive: true,
            hasModalDialog: false,
            isEditingText: false);

        Assert.Equal(ScreenshotShortcutDecision.PerformPin, decision);
    }

    [Fact]
    public void RepeatedBareSIsConsumedWithoutCreatingAnotherPin()
    {
        var decision = PinnedScreenshotShortcut.DecideEditorPin(
            PinnedScreenshotShortcut.VirtualKeyS,
            ScreenshotShortcutModifiers.None,
            isRepeat: true,
            isEditorActive: true,
            hasModalDialog: false,
            isEditingText: false);

        Assert.Equal(ScreenshotShortcutDecision.Consume, decision);
    }

    [Theory]
    [InlineData(ScreenshotShortcutModifiers.Control)]
    [InlineData(ScreenshotShortcutModifiers.Alt)]
    [InlineData(ScreenshotShortcutModifiers.Shift)]
    [InlineData(ScreenshotShortcutModifiers.Windows)]
    public void ModifiedSIsLeftForNormalEditorCommands(ScreenshotShortcutModifiers modifiers)
    {
        var decision = PinnedScreenshotShortcut.DecideEditorPin(
            PinnedScreenshotShortcut.VirtualKeyS,
            modifiers,
            isRepeat: false,
            isEditorActive: true,
            hasModalDialog: false,
            isEditingText: false);

        Assert.Equal(ScreenshotShortcutDecision.Ignore, decision);
    }

    [Fact]
    public void TextEditingAndModalDialogsPreventBareSPin()
    {
        Assert.Equal(
            ScreenshotShortcutDecision.Ignore,
            PinnedScreenshotShortcut.DecideEditorPin(
                PinnedScreenshotShortcut.VirtualKeyS,
                ScreenshotShortcutModifiers.None,
                isRepeat: false,
                isEditorActive: true,
                hasModalDialog: false,
                isEditingText: true));
        Assert.Equal(
            ScreenshotShortcutDecision.Ignore,
            PinnedScreenshotShortcut.DecideEditorPin(
                PinnedScreenshotShortcut.VirtualKeyS,
                ScreenshotShortcutModifiers.None,
                isRepeat: false,
                isEditorActive: true,
                hasModalDialog: true,
                isEditingText: false));
    }

    [Fact]
    public void FirstBareEscapeClosesOnlyTheActivePin()
    {
        var first = PinnedScreenshotShortcut.DecidePinnedWindowClose(
            PinnedScreenshotShortcut.VirtualKeyEscape,
            ScreenshotShortcutModifiers.None,
            isRepeat: false,
            isPinnedWindowActive: true);
        var repeat = PinnedScreenshotShortcut.DecidePinnedWindowClose(
            PinnedScreenshotShortcut.VirtualKeyEscape,
            ScreenshotShortcutModifiers.None,
            isRepeat: true,
            isPinnedWindowActive: true);
        var inactive = PinnedScreenshotShortcut.DecidePinnedWindowClose(
            PinnedScreenshotShortcut.VirtualKeyEscape,
            ScreenshotShortcutModifiers.None,
            isRepeat: false,
            isPinnedWindowActive: false);

        Assert.Equal(ScreenshotShortcutDecision.ClosePin, first);
        Assert.Equal(ScreenshotShortcutDecision.Consume, repeat);
        Assert.Equal(ScreenshotShortcutDecision.Ignore, inactive);
    }

    [Fact]
    public void FirstBareEscapeClosesTheActiveEditorAndRepeatIsConsumed()
    {
        var first = PinnedScreenshotShortcut.DecideEditorCommand(
            PinnedScreenshotShortcut.VirtualKeyEscape,
            ScreenshotShortcutModifiers.None,
            isRepeat: false,
            isEditorActive: true,
            hasModalDialog: false,
            isEditingText: false);
        var repeat = PinnedScreenshotShortcut.DecideEditorCommand(
            PinnedScreenshotShortcut.VirtualKeyEscape,
            ScreenshotShortcutModifiers.None,
            isRepeat: true,
            isEditorActive: true,
            hasModalDialog: false,
            isEditingText: false);

        Assert.Equal(ScreenshotShortcutDecision.CloseEditor, first);
        Assert.Equal(ScreenshotShortcutDecision.Consume, repeat);
    }

    [Theory]
    [InlineData(false, false, ScreenshotShortcutModifiers.None)]
    [InlineData(true, true, ScreenshotShortcutModifiers.None)]
    [InlineData(true, false, ScreenshotShortcutModifiers.Control)]
    public void EditorEscapeDoesNotCloseOutsideItsUnmodifiedActiveContext(
        bool isEditorActive,
        bool hasModalDialog,
        ScreenshotShortcutModifiers modifiers)
    {
        var decision = PinnedScreenshotShortcut.DecideEditorCommand(
            PinnedScreenshotShortcut.VirtualKeyEscape,
            modifiers,
            isRepeat: false,
            isEditorActive,
            hasModalDialog,
            isEditingText: false);

        Assert.Equal(ScreenshotShortcutDecision.Ignore, decision);
    }
}
