[CmdletBinding()]
param(
    [string]$ApplicationPath,

    [ValidateRange(5, 30)]
    [int]$TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$smokeWatch = [System.Diagnostics.Stopwatch]::StartNew()
$cleanupReserve = [TimeSpan]::FromSeconds(4)
$windowsRoot = Split-Path -Parent $PSScriptRoot

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw "The Crosio startup smoke test can only run on Windows."
}

$osBuild = [Environment]::OSVersion.Version.Build
if ($osBuild -lt 19041) {
    throw "The unpackaged Crosio build targets Windows build 19041 or later; this runner is build $osBuild."
}

if ($osBuild -lt 22000) {
    Write-Warning (
        "Running the unpackaged launch smoke on Windows build $osBuild. " +
        "Windows App SDK 1.8 supports Windows Server 2022, but Crosio product acceptance still requires Windows 11 build 22000 or later."
    )
}

if ([string]::IsNullOrWhiteSpace($ApplicationPath)) {
    $ApplicationPath = Join-Path $windowsRoot "artifacts/publish/win-x64/Crosio.exe"
}

if (-not (Test-Path -LiteralPath $ApplicationPath -PathType Leaf)) {
    throw "The unpackaged Crosio executable was not found: $ApplicationPath"
}

$resolvedApplicationPath = (Resolve-Path -LiteralPath $ApplicationPath).Path
$workingDirectory = Split-Path -Parent $resolvedApplicationPath

if ($null -eq ("Crosio.StartupSmoke.NativeWindow" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Crosio.StartupSmoke
{
    public sealed class WindowSnapshot
    {
        public WindowSnapshot(IntPtr handle, string title)
        {
            Handle = handle;
            Title = title;
        }

        public IntPtr Handle { get; private set; }

        public string Title { get; private set; }
    }

    public static class NativeWindow
    {
        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern int GetWindowTextLengthW(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern int GetWindowTextW(IntPtr window, StringBuilder text, int maximumCharacters);

        public static WindowSnapshot[] FindVisibleTopLevelWindows(int processId)
        {
            var windows = new List<WindowSnapshot>();
            var succeeded = EnumWindows((window, _) =>
            {
                uint ownerProcessId;
                GetWindowThreadProcessId(window, out ownerProcessId);
                if (ownerProcessId != (uint)processId || !IsWindowVisible(window))
                {
                    return true;
                }

                var titleLength = GetWindowTextLengthW(window);
                var title = new StringBuilder(Math.Max(1, titleLength + 1));
                GetWindowTextW(window, title, title.Capacity);
                windows.Add(new WindowSnapshot(window, title.ToString()));
                return true;
            }, IntPtr.Zero);

            if (!succeeded)
            {
                var error = Marshal.GetLastWin32Error();
                if (error != 0)
                {
                    throw new Win32Exception(error, "EnumWindows failed while locating the Crosio main window.");
                }
            }

            return windows.ToArray();
        }
    }
}
"@
}

function Get-StartedProcessExitCode {
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$StartedProcess
    )

    $StartedProcess.Refresh()
    if (-not $StartedProcess.HasExited) {
        return "<still running>"
    }

    try {
        return [string]$StartedProcess.ExitCode
    }
    catch {
        return "<unavailable>"
    }
}

function Find-CrosioMainWindow {
    param(
        [Parameter(Mandatory)]
        [int]$ProcessId
    )

    return [Crosio.StartupSmoke.NativeWindow]::FindVisibleTopLevelWindows($ProcessId) |
        Where-Object { [StringComparer]::Ordinal.Equals($_.Title, "Crosio") } |
        Select-Object -First 1
}

$startedProcess = $null
$startedProcessId = $null
$mainWindow = $null
$smokeException = $null
$cleanupException = $null

try {
    $startedProcess = Start-Process `
        -FilePath $resolvedApplicationPath `
        -WorkingDirectory $workingDirectory `
        -PassThru
    $startedProcessId = $startedProcess.Id

    # Force Process to retain a handle to exactly the process we launched. The
    # cleanup path never searches for, redirects to, or terminates another
    # Crosio instance.
    $null = $startedProcess.Handle
    $startupDeadline = [TimeSpan]::FromSeconds($TimeoutSeconds) - $cleanupReserve

    while ($smokeWatch.Elapsed -lt $startupDeadline) {
        $startedProcess.Refresh()
        if ($startedProcess.HasExited) {
            $exitCode = Get-StartedProcessExitCode -StartedProcess $startedProcess
            Write-Host "Crosio PID $($startedProcess.Id) exited during startup. ExitCode=$exitCode"
            throw "Crosio exited before its main window became visible. ExitCode=$exitCode"
        }

        $candidate = Find-CrosioMainWindow -ProcessId $startedProcess.Id
        if ($null -ne $candidate) {
            Start-Sleep -Milliseconds 400
            $startedProcess.Refresh()
            if ($startedProcess.HasExited) {
                $exitCode = Get-StartedProcessExitCode -StartedProcess $startedProcess
                Write-Host "Crosio PID $($startedProcess.Id) exited after showing a window. ExitCode=$exitCode"
                throw "Crosio exited immediately after its main window appeared. ExitCode=$exitCode"
            }

            $stableCandidate = Find-CrosioMainWindow -ProcessId $startedProcess.Id
            if ($null -ne $stableCandidate) {
                $mainWindow = $stableCandidate
                break
            }
        }

        Start-Sleep -Milliseconds 200
    }

    if ($null -eq $mainWindow) {
        $startedProcess.Refresh()
        $exitCode = Get-StartedProcessExitCode -StartedProcess $startedProcess
        $visibleWindows = [Crosio.StartupSmoke.NativeWindow]::FindVisibleTopLevelWindows($startedProcess.Id)
        $visibleTitles = @($visibleWindows | ForEach-Object { "'$($_.Title)'" }) -join ", "
        if ([string]::IsNullOrWhiteSpace($visibleTitles)) {
            $visibleTitles = "<none>"
        }

        throw (
            "Crosio did not expose a stable, visible top-level window titled 'Crosio' before the startup deadline. " +
            "PID=$($startedProcess.Id); ExitCode=$exitCode; VisibleTitles=$visibleTitles"
        )
    }
}
catch {
    $smokeException = $_.Exception
}
finally {
    if ($null -ne $startedProcess) {
        try {
            $startedProcess.Refresh()
            if ($startedProcess.HasExited) {
                $exitCode = Get-StartedProcessExitCode -StartedProcess $startedProcess
                Write-Host "Crosio PID $($startedProcess.Id) exited without smoke cleanup. ExitCode=$exitCode"
                if ($null -ne $mainWindow -and $null -eq $smokeException) {
                    $smokeException = [InvalidOperationException]::new(
                        "Crosio exited after its main window was verified. ExitCode=$exitCode"
                    )
                }
            }
            else {
                $startedProcess.Kill()
                $remaining = [TimeSpan]::FromSeconds($TimeoutSeconds) - $smokeWatch.Elapsed
                $waitMilliseconds = [Math]::Max(
                    0,
                    [Math]::Min(3000, [int][Math]::Floor($remaining.TotalMilliseconds))
                )
                $exited = $startedProcess.WaitForExit($waitMilliseconds)
                if (-not $exited) {
                    throw "The Crosio process started by this smoke test did not exit after Kill(). PID=$($startedProcess.Id)"
                }

                $exitCode = Get-StartedProcessExitCode -StartedProcess $startedProcess
                Write-Host "Smoke cleanup terminated started Crosio PID $($startedProcess.Id). ExitCode=$exitCode"
            }
        }
        catch {
            $cleanupException = $_.Exception
        }
        finally {
            $startedProcess.Dispose()
        }
    }
}

if ($null -ne $cleanupException) {
    if ($null -ne $smokeException) {
        throw "Startup smoke failed: $($smokeException.Message) Cleanup also failed: $($cleanupException.Message)"
    }

    throw $cleanupException
}

if ($null -ne $smokeException) {
    throw $smokeException
}

$windowHandle = "0x{0:X}" -f $mainWindow.Handle.ToInt64()
Write-Host (
    "Crosio unpackaged startup smoke passed. " +
    "PID=$startedProcessId; HWND=$windowHandle; Title='$($mainWindow.Title)'; OSBuild=$osBuild; Elapsed=$($smokeWatch.Elapsed)"
)
