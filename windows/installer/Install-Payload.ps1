[CmdletBinding()]
param(
    [ValidateSet("Install", "Open")]
    [string]$Mode = "Install"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
Set-StrictMode -Version Latest
$statusPath = Join-Path $PSScriptRoot "status.ini"

function Write-SetupStatus {
    param([string]$Message, [string]$ApplicationId = "")
    # A Unicode INI is readable by NSIS without console/code-page conversion.
    $safeMessage = ($Message -replace '[\r\n]+', ' ').Trim()
    if ($safeMessage.Length -gt 700) { $safeMessage = $safeMessage.Substring(0, 700) }
    [System.IO.File]::WriteAllText($statusPath,
        "[Result]`r`nMessage=$safeMessage`r`nApplicationId=$ApplicationId`r`n",
        [System.Text.Encoding]::Unicode)
}

try {
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT -or
        [Environment]::OSVersion.Version.Build -lt 22000 -or
        (Get-CimInstance -ClassName Win32_OperatingSystem).ProductType -ne 1) {
        throw "Crosio 需要 Windows 11 或更新的桌面版系统。"
    }
    if (-not [Environment]::Is64BitProcess) {
        throw "无法启动系统原生安装服务，请重新下载完整安装器。"
    }
    # Do not trust inherited PROCESSOR_ARCHITECTURE from the x86 bootstrapper.
    $processor = Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1
    $architecture = switch ([int]$processor.Architecture) { 9 { "AMD64" } 12 { "ARM64" } default { "Unsupported" } }
    if ($architecture -eq "Unsupported") {
        throw "当前系统架构不受支持。Crosio 支持 x64 和 ARM64。"
    }
    # MSIX registers for the executing account. Over-the-shoulder UAC must not
    # silently install into a different administrator's account.
    Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
namespace Crosio.Setup
{
    public static class SessionIdentity
    {
        [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WTSQuerySessionInformationW(IntPtr server,
            int sessionId, int infoClass, out IntPtr buffer, out uint bytesReturned);
        [DllImport("wtsapi32.dll")]
        private static extern void WTSFreeMemory(IntPtr memory);
        private static string Query(int infoClass)
        {
            IntPtr buffer = IntPtr.Zero;
            uint bytes;
            if (!WTSQuerySessionInformationW(IntPtr.Zero, Process.GetCurrentProcess().SessionId,
                infoClass, out buffer, out bytes))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try { return Marshal.PtrToStringUni(buffer) ?? String.Empty; }
            finally { WTSFreeMemory(buffer); }
        }
        public static bool MatchesDesktopUser()
        {
            string user = Query(5); // WTSUserName
            string domain = Query(7); // WTSDomainName
            if (String.IsNullOrWhiteSpace(user)) return false;
            NTAccount account = String.IsNullOrWhiteSpace(domain) ? new NTAccount(user) : new NTAccount(domain, user);
            SecurityIdentifier desktopSid = (SecurityIdentifier)account.Translate(typeof(SecurityIdentifier));
            using (WindowsIdentity current = WindowsIdentity.GetCurrent())
                return current.User != null && desktopSid.Equals(current.User);
        }
    }
}
"@
    if (-not [Crosio.Setup.SessionIdentity]::MatchesDesktopUser()) {
        throw "授权使用了其他管理员账户，无法为当前桌面用户安装。请使用当前登录账户的管理员权限重试，或联系电脑管理员。"
    }
    $metadata = Get-Content -LiteralPath (Join-Path $PSScriptRoot "payload.json") -Raw | ConvertFrom-Json
    $expectedVersion = [version]$metadata.version
    if ($metadata.product -ne "Crosio" -or $metadata.packageName -ne "Crosio.Windows" -or
        $metadata.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
        $metadata.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or
        $metadata.allowUnsignedTestPackage -isnot [bool]) {
        throw "安装器信息不完整，请重新下载。"
    }

    $installed = @(Get-AppxPackage -Name "Crosio.Windows" -ErrorAction Stop)
    foreach ($existing in $installed) {
        if ($existing.Publisher -ne $metadata.publisher) {
            throw "已安装的 Crosio 来自不同发布者。为保护现有数据，本次安装已停止。"
        }
        if ([version]$existing.Version -gt $expectedVersion) {
            throw "电脑上已有更新版本的 Crosio，不能安装较旧版本。现有版本和设置未被更改。"
        }
    }

    if ($Mode -eq "Install") {
        $bundlePath = Join-Path $PSScriptRoot "Crosio.msixbundle"
        if ((Get-FileHash -LiteralPath $bundlePath -Algorithm SHA256).Hash -ne $metadata.sha256) {
            throw "安装包校验失败，文件可能已损坏。请重新下载安装器。"
        }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [System.IO.Compression.ZipFile]::OpenRead($bundlePath)
        try {
            $entry = $archive.GetEntry("AppxMetadata/AppxBundleManifest.xml")
            if ($null -eq $entry) { throw "安装包缺少版本信息。" }
            $reader = [System.IO.StreamReader]::new($entry.Open())
            try { $manifest = [xml]$reader.ReadToEnd() }
            finally { $reader.Dispose() }
        }
        finally { $archive.Dispose() }
        $identity = $manifest.DocumentElement.SelectSingleNode("*[local-name()='Identity']")
        if ($null -eq $identity -or $identity.GetAttribute("Name") -ne "Crosio.Windows" -or
            $identity.GetAttribute("Publisher") -ne $metadata.publisher -or
            [version]$identity.GetAttribute("Version") -ne $expectedVersion) {
            throw "安装包的身份或版本与安装器不一致。"
        }
        foreach ($requiredArchitecture in @("x64", "arm64")) {
            $packages = @($manifest.DocumentElement.SelectNodes("*[local-name()='Packages']/*[local-name()='Package'][@Type='application' and @Architecture='$requiredArchitecture']"))
            if ($packages.Count -ne 1 -or [version]$packages[0].GetAttribute("Version") -ne $expectedVersion) {
                throw "安装包缺少匹配版本的 $requiredArchitecture 应用。"
            }
        }

        $signature = Get-AuthenticodeSignature -FilePath $bundlePath
        if ($signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid) {
            # Add-AppxPackage performs an in-place update and retains app data.
            # No Remove-AppxPackage, forced shutdown, or downgrade override.
            Add-AppxPackage -Path $bundlePath -ErrorAction Stop
        }
        elseif ($signature.Status -eq [System.Management.Automation.SignatureStatus]::NotSigned -and
            $metadata.allowUnsignedTestPackage) {
            # Only an explicitly built preview can opt into Windows 11's
            # unsigned-package deployment. Never use this for an invalid signature.
            $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
            if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
                throw "测试版安装需要在系统授权窗口中允许管理员权限。"
            }
            Add-AppxPackage -Path $bundlePath -AllowUnsigned -ErrorAction Stop
        }
        else {
            throw "安装包签名无效或不受信任（$($signature.Status)），已停止安装。请使用官方安装包。"
        }
    }

    $installed = @(Get-AppxPackage -Name "Crosio.Windows" -ErrorAction Stop |
        Where-Object { $_.Publisher -eq $metadata.publisher -and [version]$_.Version -eq $expectedVersion })
    $expectedArchitecture = if ($architecture -eq "ARM64") { "Arm64" } else { "X64" }
    if ($installed.Count -ne 1 -or $installed[0].Architecture.ToString() -ne $expectedArchitecture -or
        $installed[0].Status.ToString() -ne "Ok") {
        throw "Windows 未能确认 Crosio 已正确安装。请关闭正在运行的 Crosio 后重试。"
    }
    $appManifest = Get-AppxPackageManifest -Package $installed[0].PackageFullName
    $application = $appManifest.Package.Applications.Application |
        Where-Object { $_.Id -eq "App" } | Select-Object -First 1
    if ($null -eq $application) { throw "已安装的应用缺少启动信息。" }
    $applicationId = "$($installed[0].PackageFamilyName)!App"
    if ($Mode -eq "Open") {
        Start-Process -FilePath (Join-Path $env:WINDIR "explorer.exe") -ArgumentList "shell:AppsFolder\$applicationId" -ErrorAction Stop
    }
    Write-SetupStatus -Message "Crosio 安装成功。请在开始菜单中搜索 Crosio。" -ApplicationId $applicationId
    exit 0
}
catch {
    $message = "安装未完成：$($_.Exception.Message)"
    try { Write-SetupStatus -Message $message } catch { }
    [Console]::Error.WriteLine($message)
    exit 1
}
