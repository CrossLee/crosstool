# Shared, fail-closed main-window acceptance used by both unpackaged and MSIX
# launch tests. Keep this file ASCII-only because the packaged acceptance runs
# under Windows PowerShell 5.1.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

if ($null -eq ("Crosio.WindowAcceptance.NativeWindow" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Crosio.WindowAcceptance
{
    public sealed class NativeWindowState
    {
        public bool Visible { get; internal set; }
        public bool Minimized { get; internal set; }
        public bool Cloaked { get; internal set; }
        public bool PositiveSize { get; internal set; }
        public bool IntersectsMonitorWorkArea { get; internal set; }
        public uint DisplayAffinity { get; internal set; }
        public int Left { get; internal set; }
        public int Top { get; internal set; }
        public int Right { get; internal set; }
        public int Bottom { get; internal set; }
    }

    public static class NativeWindow
    {
        private const int DwmWindowAttributeCloaked = 14;
        private const int ShowWindowHide = 0;
        private const uint WindowDisplayAffinityNone = 0;

        public static uint DisplayAffinityNone { get { return WindowDisplayAffinityNone; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MonitorInfo
        {
            internal int Size;
            internal NativeRect Monitor;
            internal NativeRect Work;
            internal uint Flags;
        }

        private delegate bool MonitorEnumerationCallback(
            IntPtr monitor,
            IntPtr deviceContext,
            ref NativeRect monitorRectangle,
            IntPtr data);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr window, out NativeRect rectangle);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplayMonitors(
            IntPtr deviceContext,
            IntPtr clipRectangle,
            MonitorEnumerationCallback callback,
            IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo information);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(
            IntPtr window,
            int attribute,
            out int value,
            int valueSize);

        public static NativeWindowState Inspect(IntPtr window)
        {
            if (window == IntPtr.Zero)
            {
                throw new ArgumentException("A non-zero window handle is required.", "window");
            }

            NativeRect rectangle;
            if (!GetWindowRect(window, out rectangle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetWindowRect failed.");
            }

            int cloakedValue;
            int cloakedResult = DwmGetWindowAttribute(
                window,
                DwmWindowAttributeCloaked,
                out cloakedValue,
                Marshal.SizeOf(typeof(int)));
            if (cloakedResult < 0)
            {
                Marshal.ThrowExceptionForHR(cloakedResult);
            }

            uint displayAffinity;
            if (!GetWindowDisplayAffinity(window, out displayAffinity))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetWindowDisplayAffinity failed.");
            }

            bool intersectsMonitorWorkArea = false;
            int monitorInformationError = 0;
            bool monitorEnumerationSucceeded = EnumDisplayMonitors(
                IntPtr.Zero,
                IntPtr.Zero,
                delegate(IntPtr monitor, IntPtr deviceContext, ref NativeRect monitorRectangle, IntPtr data)
                {
                    MonitorInfo information = new MonitorInfo();
                    information.Size = Marshal.SizeOf(typeof(MonitorInfo));
                    if (!GetMonitorInfo(monitor, ref information))
                    {
                        monitorInformationError = Marshal.GetLastWin32Error();
                        return false;
                    }

                    int intersectionLeft = Math.Max(rectangle.Left, information.Work.Left);
                    int intersectionTop = Math.Max(rectangle.Top, information.Work.Top);
                    int intersectionRight = Math.Min(rectangle.Right, information.Work.Right);
                    int intersectionBottom = Math.Min(rectangle.Bottom, information.Work.Bottom);
                    if (intersectionRight > intersectionLeft && intersectionBottom > intersectionTop)
                    {
                        intersectsMonitorWorkArea = true;
                    }

                    return true;
                },
                IntPtr.Zero);
            if (monitorInformationError != 0)
            {
                throw new Win32Exception(monitorInformationError, "GetMonitorInfo failed.");
            }
            if (!monitorEnumerationSucceeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumDisplayMonitors failed.");
            }

            return new NativeWindowState
            {
                Visible = IsWindowVisible(window),
                Minimized = IsIconic(window),
                Cloaked = cloakedValue != 0,
                PositiveSize = rectangle.Right > rectangle.Left && rectangle.Bottom > rectangle.Top,
                IntersectsMonitorWorkArea = intersectsMonitorWorkArea,
                DisplayAffinity = displayAffinity,
                Left = rectangle.Left,
                Top = rectangle.Top,
                Right = rectangle.Right,
                Bottom = rectangle.Bottom,
            };
        }

        public static bool HideWindow(IntPtr window)
        {
            if (window == IntPtr.Zero)
            {
                throw new ArgumentException("A non-zero window handle is required.", "window");
            }

            return ShowWindow(window, ShowWindowHide);
        }
    }
}
"@
}

function Get-OnePawInteractiveWindowAssessment {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [IntPtr]$WindowHandle,

        [Parameter(Mandatory)]
        [string]$ExpectedHomeName
    )

    $problems = [System.Collections.Generic.List[string]]::new()
    $nativeState = $null
    $homeControlType = "<none>"

    try {
        $nativeState = [Crosio.WindowAcceptance.NativeWindow]::Inspect($WindowHandle)
        if (-not $nativeState.Visible) { $problems.Add("WS_VISIBLE is not set.") }
        if ($nativeState.Minimized) { $problems.Add("The window is minimized.") }
        if ($nativeState.Cloaked) { $problems.Add("DWM reports the window as cloaked.") }
        if ($nativeState.DisplayAffinity -ne [Crosio.WindowAcceptance.NativeWindow]::DisplayAffinityNone) {
            $problems.Add("GetWindowDisplayAffinity is not WDA_NONE (0).")
        }
        if (-not $nativeState.PositiveSize) { $problems.Add("GetWindowRect returned an empty rectangle.") }
        if (-not $nativeState.IntersectsMonitorWorkArea) {
            $problems.Add("The window does not intersect any active monitor work area.")
        }
    }
    catch {
        $problems.Add("Native window inspection failed: $($_.Exception.Message)")
    }

    try {
        $root = [System.Windows.Automation.AutomationElement]::FromHandle($WindowHandle)
        if ($null -eq $root) {
            $problems.Add("UI Automation returned no root element.")
        }
        else {
            $rootCurrent = $root.Current
            if (-not $rootCurrent.IsEnabled) { $problems.Add("The UI Automation root is disabled.") }
            if ($rootCurrent.IsOffscreen) { $problems.Add("The UI Automation root is offscreen.") }

            $windowPatternObject = $null
            if (-not $root.TryGetCurrentPattern(
                [System.Windows.Automation.WindowPattern]::Pattern,
                [ref]$windowPatternObject)) {
                $problems.Add("The UI Automation root exposes no WindowPattern.")
            }
            elseif ($windowPatternObject.Current.WindowVisualState -eq
                [System.Windows.Automation.WindowVisualState]::Minimized) {
                $problems.Add("UI Automation reports a minimized window.")
            }

            $homeCondition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty,
                $ExpectedHomeName)
            $homeMatches = $root.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $homeCondition)
            $acceptedHomeElement = $null
            foreach ($homeElement in $homeMatches) {
                try {
                    $homeCurrent = $homeElement.Current
                    $homeBounds = $homeCurrent.BoundingRectangle
                    $supportedPatternIds = @(
                        $homeElement.GetSupportedPatterns() | ForEach-Object { $_.Id }
                    )
                    $hasInteractivePattern =
                        $supportedPatternIds -contains [System.Windows.Automation.InvokePattern]::Pattern.Id -or
                        $supportedPatternIds -contains [System.Windows.Automation.SelectionItemPattern]::Pattern.Id
                    $isTextElement = $homeCurrent.ControlType -eq
                        [System.Windows.Automation.ControlType]::Text
                    if ($homeCurrent.IsEnabled -and
                        -not $homeCurrent.IsOffscreen -and
                        $homeBounds.Width -gt 0 -and
                        $homeBounds.Height -gt 0 -and
                        ($hasInteractivePattern -or $isTextElement)) {
                        $acceptedHomeElement = $homeElement
                        $homeControlType = $homeCurrent.ControlType.ProgrammaticName
                        break
                    }
                }
                catch [System.Windows.Automation.ElementNotAvailableException] {
                    # The tree can change while WinUI finishes its first layout.
                }
            }

            if ($null -eq $acceptedHomeElement) {
                $problems.Add("UI Automation found no visible, enabled home navigation text or control.")
            }
        }
    }
    catch [System.Windows.Automation.ElementNotAvailableException] {
        $problems.Add("The UI Automation root disappeared during inspection.")
    }
    catch {
        $problems.Add("UI Automation inspection failed: $($_.Exception.Message)")
    }

    $rectangleText = if ($null -eq $nativeState) {
        "<unavailable>"
    }
    else {
        "[$($nativeState.Left),$($nativeState.Top),$($nativeState.Right),$($nativeState.Bottom)]"
    }
    $nativeText = if ($null -eq $nativeState) {
        "<unavailable>"
    }
    else {
        "Visible=$($nativeState.Visible); Minimized=$($nativeState.Minimized); " +
        "Cloaked=$($nativeState.Cloaked); DisplayAffinity=$($nativeState.DisplayAffinity); Rect=$rectangleText; " +
        "OnMonitor=$($nativeState.IntersectsMonitorWorkArea)"
    }
    $problemText = if ($problems.Count -eq 0) { "none" } else { $problems -join " " }

    return [pscustomobject]@{
        Accepted = $problems.Count -eq 0
        NativeState = $nativeState
        HomeControlType = $homeControlType
        Summary = "$nativeText; HomeControl=$homeControlType; Problems=$problemText"
    }
}
