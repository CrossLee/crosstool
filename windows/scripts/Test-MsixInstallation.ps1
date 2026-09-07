[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackageDirectory,

    [ValidateRange(5, 60)]
    [int]$LaunchTimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or
    [Environment]::OSVersion.Version.Build -lt 22000) {
    throw "Packaged Crosio acceptance requires Windows 11 build 22000 or later."
}
if ((Get-CimInstance -ClassName Win32_OperatingSystem -ErrorAction Stop).ProductType -ne 1) {
    throw "Packaged Crosio product acceptance requires Windows 11, not Windows Server."
}
$osArchitecture = [Environment]::GetEnvironmentVariable("PROCESSOR_ARCHITEW6432")
if ([string]::IsNullOrEmpty($osArchitecture)) {
    $osArchitecture = [Environment]::GetEnvironmentVariable("PROCESSOR_ARCHITECTURE")
}
if ($osArchitecture -ne "ARM64") {
    throw "This installation acceptance test requires an ARM64 Windows 11 host; found $osArchitecture."
}

$releaseDirectory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$installerPath = Join-Path $releaseDirectory "Install-Crosio.ps1"
$buildInfoPath = Join-Path $releaseDirectory "build-info.json"
foreach ($requiredPath in @($installerPath, $buildInfoPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Missing installation test input: $requiredPath"
    }
}
$bundles = @(Get-ChildItem -LiteralPath $releaseDirectory -Filter "*.msixbundle" -File)
if ($bundles.Count -ne 1) {
    throw "Expected exactly one MSIX bundle in $releaseDirectory; found $($bundles.Count)."
}
$buildInfo = Get-Content -LiteralPath $buildInfoPath -Raw | ConvertFrom-Json
$expectedVersion = [version]$buildInfo.version
if ($buildInfo.product -ne "Crosio" -or @($buildInfo.architectures) -notcontains "ARM64") {
    throw "The build information does not identify an ARM64 Crosio release."
}

# Read the bundle identity without extracting or running anything from it.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$bundleArchive = [System.IO.Compression.ZipFile]::OpenRead($bundles[0].FullName)
try {
    $entry = $bundleArchive.GetEntry("AppxMetadata/AppxBundleManifest.xml")
    if ($null -eq $entry) { throw "The bundle has no AppxBundleManifest.xml." }
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try { $bundleManifest = [xml]$reader.ReadToEnd() }
    finally { $reader.Dispose() }
}
finally { $bundleArchive.Dispose() }
$bundleIdentity = $bundleManifest.DocumentElement.SelectSingleNode("*[local-name()='Identity']")
$armPackages = @($bundleManifest.DocumentElement.SelectNodes("*[local-name()='Packages']/*[local-name()='Package'][@Architecture='arm64' and @Type='application']"))
if ($null -eq $bundleIdentity -or $bundleIdentity.GetAttribute("Name") -ne "Crosio.Windows" -or
    [version]$bundleIdentity.GetAttribute("Version") -ne $expectedVersion -or $armPackages.Count -ne 1 -or
    [version]$armPackages[0].GetAttribute("Version") -ne $expectedVersion) {
    throw "The bundle identity or ARM64 payload does not match build-info.json."
}
$expectedPublisher = $bundleIdentity.GetAttribute("Publisher")

# Do not replace or remove an app that existed before this isolated CI test.
$existingPackages = @(Get-AppxPackage -AllUsers -Name "Crosio.Windows" -ErrorAction Stop)
if ($existingPackages.Count -ne 0) {
    throw "Crosio is already installed or staged for a user. Refusing to modify it."
}
if (@(Get-Process -Name "Crosio" -ErrorAction SilentlyContinue).Count -ne 0) {
    throw "A Crosio process is already running. Refusing to redirect or terminate it."
}

if ($null -eq ("Crosio.MsixAcceptance.Native" -as [type])) {
    # Interface signatures and GUIDs are from Microsoft's shobjidl_core.h.
    # CLSCTX_LOCAL_SERVER keeps activation arguments alive in the COM surrogate.
    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Crosio.MsixAcceptance
{
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager
    {
        [PreserveSig] int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
        [PreserveSig] int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appId,
            IntPtr items, [MarshalAs(UnmanagedType.LPWStr)] string verb, out uint processId);
        [PreserveSig] int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appId,
            IntPtr items, out uint processId);
    }

    public sealed class ActivationOperation
    {
        private readonly object gate = new object();
        private readonly ManualResetEventSlim done = new ManualResetEventSlim(false);
        private bool stopRequested;
        private Process process;
        private Exception error;

        internal ActivationOperation(string appId, string packageName)
        {
            var thread = new Thread(() => Run(appId, packageName));
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public bool Wait(int milliseconds) { return done.Wait(milliseconds); }
        public Exception Error { get { return error; } }
        public Process Process { get { lock (gate) { return process; } } }

        private void Run(string appId, string packageName)
        {
            IApplicationActivationManager manager = null;
            bool initialized = false;
            Process candidate = null;
            try
            {
                int result = Native.CoInitializeEx(IntPtr.Zero, 2);
                Marshal.ThrowExceptionForHR(result);
                initialized = true;
                var clsid = new Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c");
                var iid = typeof(IApplicationActivationManager).GUID;
                result = Native.CoCreateInstance(ref clsid, IntPtr.Zero, 4, ref iid, out manager);
                Marshal.ThrowExceptionForHR(result);
                DateTime activationStart = DateTime.UtcNow;
                uint processId;
                result = manager.ActivateApplication(appId, null, 2, out processId);
                Marshal.ThrowExceptionForHR(result);
                if (processId == 0) { throw new InvalidOperationException("Activation returned no process ID."); }
                candidate = System.Diagnostics.Process.GetProcessById(checked((int)processId));
                IntPtr handle = candidate.Handle; // Retain exactly this process, protecting against PID reuse.
                if (candidate.StartTime.ToUniversalTime() < activationStart.AddSeconds(-1) ||
                    !String.Equals(Native.PackageFullName(handle), packageName, StringComparison.Ordinal))
                {
                    candidate.Dispose();
                    candidate = null;
                    throw new InvalidOperationException("Activation did not create a new process with the expected package identity.");
                }
                lock (gate)
                {
                    if (stopRequested) { StopProcess(candidate); }
                    else { process = candidate; }
                    candidate = null;
                }
            }
            catch (Exception failure) { error = failure; }
            finally
            {
                // An unverified candidate is never terminated.
                if (candidate != null) { candidate.Dispose(); }
                try
                {
                    if (manager != null) { Marshal.FinalReleaseComObject(manager); }
                }
                catch (Exception releaseFailure) { if (error == null) { error = releaseFailure; } }
                finally
                {
                    if (initialized) { Native.CoUninitialize(); }
                    done.Set();
                }
            }
        }

        private static void StopProcess(Process ownedProcess)
        {
            try
            {
                if (!ownedProcess.HasExited)
                {
                    ownedProcess.Kill();
                    if (!ownedProcess.WaitForExit(3000))
                    {
                        throw new InvalidOperationException("The activated test process did not stop.");
                    }
                }
            }
            finally { ownedProcess.Dispose(); }
        }

        public void Stop()
        {
            lock (gate)
            {
                stopRequested = true;
                if (process != null)
                {
                    Process ownedProcess = process;
                    process = null;
                    StopProcess(ownedProcess);
                }
            }
        }
    }

    public static class Native
    {
        internal static ActivationOperation StartCore(string appId, string packageName)
        { return new ActivationOperation(appId, packageName); }
        public static ActivationOperation Start(string appId, string packageName)
        { return StartCore(appId, packageName); }

        [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint flags);
        [DllImport("ole32.dll")] internal static extern void CoUninitialize();
        [DllImport("ole32.dll")] internal static extern int CoCreateInstance(ref Guid clsid,
            IntPtr outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IApplicationActivationManager manager);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int GetPackageFullName(
            IntPtr process, ref uint length, StringBuilder name);
        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetWindowTextW(IntPtr window, StringBuilder title, int capacity);

        internal static string PackageFullName(IntPtr handle)
        {
            uint length = 0;
            int result = GetPackageFullName(handle, ref length, null);
            if (result != 122) { throw new Win32Exception(result, "Activated process has no readable package identity."); }
            var name = new StringBuilder(checked((int)length));
            result = GetPackageFullName(handle, ref length, name);
            if (result != 0) { throw new Win32Exception(result); }
            return name.ToString();
        }

        public static IntPtr FindMainWindow(int processId)
        {
            IntPtr found = IntPtr.Zero;
            bool succeeded = EnumWindows((window, parameter) =>
            {
                uint owner;
                GetWindowThreadProcessId(window, out owner);
                if (owner == (uint)processId && IsWindowVisible(window))
                {
                    var title = new StringBuilder(256);
                    GetWindowTextW(window, title, title.Capacity);
                    if (String.Equals(title.ToString(), "Crosio", StringComparison.Ordinal)) { found = window; }
                }
                return true;
            }, IntPtr.Zero);
            if (!succeeded) { throw new Win32Exception(Marshal.GetLastWin32Error(), "EnumWindows failed."); }
            return found;
        }
    }
}
"@
}

$installationAttempted = $false
$installedPackageFullName = $null
$activation = $null
$failure = $null
$cleanupFailures = [System.Collections.Generic.List[string]]::new()
$activatedProcessId = $null
$mainWindow = [IntPtr]::Zero
try {
    $installationAttempted = $true
    & $installerPath -PackagePath $bundles[0].FullName
    $packages = @(Get-AppxPackage -Name "Crosio.Windows" -ErrorAction Stop)
    if ($packages.Count -ne 1) { throw "Installation did not register exactly one Crosio package." }
    $package = $packages[0]
    if ($package.Publisher -ne $expectedPublisher -or [version]$package.Version -ne $expectedVersion) {
        throw "Installed Crosio identity does not match the supplied bundle."
    }
    $installedPackageFullName = $package.PackageFullName
    if ($package.Architecture.ToString() -ne "Arm64") {
        throw "The ARM64 host installed $($package.Architecture) instead of ARM64."
    }
    $manifest = Get-AppxPackageManifest -Package $package.PackageFullName -ErrorAction Stop
    $ns = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $ns.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
    $ns.AddNamespace("desktop5", "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5")
    $ns.AddNamespace("desktop", "http://schemas.microsoft.com/appx/manifest/desktop/windows10")
    $ns.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")
    $verbs = @($manifest.SelectNodes("//desktop5:Verb", $ns))
    $itemTypes = @($manifest.SelectNodes("//desktop5:ItemType", $ns) | ForEach-Object { $_.GetAttribute("Type") })
    if ($verbs.Count -ne 2 -or $itemTypes -notcontains "*" -or $itemTypes -notcontains "Directory" -or
        @($verbs | Where-Object { $_.GetAttribute("Clsid") -ne "6EA97827-5B42-4E82-8ABC-4FC22D3BE129" }).Count -ne 0) {
        throw "Installed package is missing its file/folder Copy Path registrations."
    }
    $startup = $manifest.SelectSingleNode("//desktop:StartupTask[@TaskId='CrosioStartupTask']", $ns)
    if ($null -eq $startup -or $startup.GetAttribute("Enabled") -ne "false") {
        throw "Installed package is missing its opt-in startup task."
    }
    $actualTypes = @($manifest.SelectNodes("//uap:FileTypeAssociation[@Name='crosio.images']/uap:SupportedFileTypes/uap:FileType", $ns) | ForEach-Object { $_.InnerText })
    $expectedTypes = @(".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".heic", ".heif", ".tif", ".tiff")
    $actualTypesKey = ($actualTypes | Sort-Object) -join "|"
    $expectedTypesKey = ($expectedTypes | Sort-Object) -join "|"
    if ($actualTypesKey -ne $expectedTypesKey) {
        throw "Installed package does not register the expected nine image types."
    }
    $application = $manifest.SelectSingleNode("/f:Package/f:Applications/f:Application[@Id='App']", $ns)
    if ($null -eq $application) { throw "Installed package has no App activation entry." }
    $aumid = "$($package.PackageFamilyName)!$($application.GetAttribute('Id'))"
    Write-Host "Installed $installedPackageFullName. Activating registered AUMID $aumid."

    $launchWatch = [System.Diagnostics.Stopwatch]::StartNew()
    $activation = [Crosio.MsixAcceptance.Native]::Start($aumid, $installedPackageFullName)
    if (-not $activation.Wait($LaunchTimeoutSeconds * 1000)) {
        throw "Registered package activation exceeded $LaunchTimeoutSeconds seconds."
    }
    if ($null -ne $activation.Error) { throw $activation.Error }
    $startedProcess = $activation.Process
    if ($null -eq $startedProcess) { throw "Registered activation yielded no owned process." }
    $activatedProcessId = $startedProcess.Id
    $visibleSince = $null
    while ($launchWatch.Elapsed.TotalSeconds -lt $LaunchTimeoutSeconds) {
        $startedProcess.Refresh()
        if ($startedProcess.HasExited) { throw "Installed Crosio exited during startup. ExitCode=$($startedProcess.ExitCode)" }
        $candidate = [Crosio.MsixAcceptance.Native]::FindMainWindow($activatedProcessId)
        if ($candidate -ne [IntPtr]::Zero) {
            if ($null -eq $visibleSince) { $visibleSince = $launchWatch.Elapsed }
            if (($launchWatch.Elapsed - $visibleSince).TotalSeconds -ge 1) { $mainWindow = $candidate; break }
        }
        else { $visibleSince = $null }
        Start-Sleep -Milliseconds 150
    }
    if ($mainWindow -eq [IntPtr]::Zero) { throw "Installed Crosio did not show a stable visible main window within $LaunchTimeoutSeconds seconds." }
}
catch { $failure = $_.Exception }
finally {
    if ($null -ne $activation) {
        try { $activation.Stop() }
        catch { $cleanupFailures.Add("Process cleanup: $($_.Exception.Message)") }
    }
    if ($installationAttempted) {
        try {
            # Also recover a registration created before the installer threw.
            # Preflight proved no existing Crosio package; match this bundle's identity.
            $installedByTest = @(Get-AppxPackage -Name "Crosio.Windows" -ErrorAction Stop |
                Where-Object { $_.Publisher -eq $expectedPublisher -and [version]$_.Version -eq $expectedVersion })
            foreach ($testPackage in $installedByTest) {
                Remove-AppxPackage -Package $testPackage.PackageFullName -ErrorAction Stop
            }
            if (@(Get-AppxPackage -Name "Crosio.Windows" -ErrorAction Stop |
                Where-Object { $_.Publisher -eq $expectedPublisher -and [version]$_.Version -eq $expectedVersion }).Count -ne 0) {
                throw "The package installed by this acceptance test remains registered."
            }
        }
        catch { $cleanupFailures.Add("Package cleanup: $($_.Exception.Message)") }
    }
}

if ($null -ne $failure -or $cleanupFailures.Count -gt 0) {
    $messages = @()
    if ($null -ne $failure) { $messages += $failure.Message }
    $messages += @($cleanupFailures)
    throw "Windows 11 ARM64 MSIX acceptance failed: $($messages -join ' | ')"
}
Write-Host "Windows 11 ARM64 MSIX acceptance passed: Version=$expectedVersion; Package=$installedPackageFullName; PID=$activatedProcessId; HWND=$mainWindow. Test process stopped and test package removed."
