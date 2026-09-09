[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64", "ARM64")]
    [string]$Architecture = "x64",

    [string]$Version = "0.1.0.0",

    [string]$CertificatePath,

    [AllowEmptyString()]
    [string]$CertificatePassword = "",

    [string]$TimestampUrl = "http://timestamp.digicert.com",

    [switch]$SkipSigning
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Packaging.Common.ps1")

if ($env:OS -ne "Windows_NT") {
    throw "MSIX packaging requires Windows 11, MSBuild, MSVC v143, and the Windows SDK."
}

Assert-CrosioMsixVersion $Version

$windowsRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $windowsRoot "src/Crosio.Windows.App/Crosio.Windows.App.csproj"
$manifestPath = Join-Path $windowsRoot "src/Crosio.Windows.App/Package.appxmanifest"
$artifactRoot = Join-Path $windowsRoot "artifacts"
$rawOutput = Join-Path $artifactRoot "app-packages/$($Architecture.ToLowerInvariant())"
$normalizedOutput = Join-Path $artifactRoot "packages/$($Architecture.ToLowerInvariant())"
$runtimeIdentifier = if ($Architecture -eq "ARM64") { "win-arm64" } else { "win-x64" }
$vcRuntimeDirectory = Resolve-CrosioVCRuntimeDirectory $Architecture
$vcRuntimeFiles = @(Get-CrosioRequiredVCRuntimeFiles -Architecture $Architecture)
$unsignedPublisher = "CN=Crosio, OID.2.25.311729368913984317654407730594956997722=1"

if ([string]::IsNullOrWhiteSpace($CertificatePath) -and -not [string]::IsNullOrEmpty($CertificatePassword)) {
    throw "CertificatePassword cannot be supplied without CertificatePath."
}

$publisher = if ([string]::IsNullOrWhiteSpace($CertificatePath)) {
    $unsignedPublisher
} else {
    Get-CrosioCertificateSubject $CertificatePath $CertificatePassword
}

foreach ($directory in @($rawOutput, $normalizedOutput)) {
    if (Test-Path -LiteralPath $directory) {
        $resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
        $resolvedDirectory = [System.IO.Path]::GetFullPath($directory)
        if (-not $resolvedDirectory.StartsWith($resolvedArtifactRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to clean a directory outside the Windows artifact root: $resolvedDirectory"
        }
        Remove-Item -LiteralPath $resolvedDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$originalManifest = [System.IO.File]::ReadAllBytes($manifestPath)
try {
    $manifest = [System.Xml.XmlDocument]::new()
    $manifest.PreserveWhitespace = $true
    $manifest.Load($manifestPath)
    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaceManager.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
    $identity = $manifest.SelectSingleNode("/f:Package/f:Identity", $namespaceManager)
    if ($null -eq $identity) {
        throw "Package.appxmanifest does not contain an Identity element."
    }
    $identity.SetAttribute("Publisher", $publisher)
    $identity.SetAttribute("Version", $Version)

    $writerSettings = [System.Xml.XmlWriterSettings]::new()
    $writerSettings.Encoding = [System.Text.UTF8Encoding]::new($false)
    $writerSettings.Indent = $true
    $writer = [System.Xml.XmlWriter]::Create($manifestPath, $writerSettings)
    try {
        $manifest.Save($writer)
    } finally {
        $writer.Dispose()
    }

    $msbuildArguments = @(
        $appProject,
        "/restore",
        "/m",
        "/p:Configuration=$Configuration",
        "/p:Platform=$Architecture",
        "/p:RuntimeIdentifier=$runtimeIdentifier",
        "/p:CrosioVCRuntimeDirectory=$vcRuntimeDirectory",
        "/p:CrosioPackageMode=MSIX",
        "/p:GenerateAppxPackageOnBuild=true",
        "/p:AppxBundle=Never",
        "/p:UapAppxPackageBuildMode=SideloadOnly",
        "/p:AppxPackageSigningEnabled=false",
        "/p:SelfContained=true",
        "/p:PublishReadyToRun=false",
        "/p:AppxPackageDir=$rawOutput\"
    )
    Invoke-CrosioChecked -Command "msbuild" -Arguments $msbuildArguments
} finally {
    [System.IO.File]::WriteAllBytes($manifestPath, $originalManifest)
}

$candidates = @(
    Get-ChildItem -LiteralPath $rawOutput -Filter "*.msix" -File -Recurse |
        Where-Object {
            $_.FullName -notmatch "[\\/]Dependencies[\\/]" -and
            $_.FullName -notmatch "[\\/]Upload[\\/]"
        }
)
if ($candidates.Count -ne 1) {
    $found = if ($candidates.Count -eq 0) { "none" } else { ($candidates.FullName -join "; ") }
    throw "Expected one primary MSIX for $Architecture, found: $found"
}

$packagePath = Join-Path $normalizedOutput "Crosio-Windows-$($Architecture.ToLowerInvariant()).msix"
Copy-Item -LiteralPath $candidates[0].FullName -Destination $packagePath -Force

$inspectionDirectory = Join-Path $artifactRoot "inspection/$($Architecture.ToLowerInvariant())"
if (Test-Path -LiteralPath $inspectionDirectory) {
    Remove-Item -LiteralPath $inspectionDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $inspectionDirectory -Force | Out-Null
$makeAppx = Resolve-CrosioWindowsSdkTool "makeappx.exe"
Invoke-CrosioChecked -Command $makeAppx -Arguments @("unpack", "/p", $packagePath, "/d", $inspectionDirectory, "/o")
& (Join-Path $PSScriptRoot "Test-IconAssets.ps1") -PackageDirectory $inspectionDirectory

$packagedShellDll = Join-Path $inspectionDirectory "ShellExtensions/Crosio.Windows.ShellExtension.dll"
$requiredPackageFiles = @(
    "Crosio.exe",
    "Crosio.dll",
    "Crosio.deps.json",
    "Crosio.runtimeconfig.json",
    "coreclr.dll",
    "hostfxr.dll",
    "hostpolicy.dll",
    "System.Private.CoreLib.dll",
    "Microsoft.WindowsAppRuntime.dll",
    "Microsoft.ui.xaml.dll",
    "Crosio.Windows.Accessibility.dll",
    "Crosio.Windows.Capture.dll",
    "Crosio.Windows.Core.dll",
    "Crosio.Windows.Hotkeys.dll",
    "Crosio.Windows.Intelligence.dll",
    "Crosio.Windows.LongCapture.dll",
    "Crosio.Windows.Translation.dll",
    "Crosio.Windows.Ocr.dll",
    "Crosio.Windows.Platform.dll",
    "Crosio.Windows.Sharing.dll",
    "Microsoft.ML.OnnxRuntime.dll",
    "Microsoft.ML.Tokenizers.dll",
    "onnxruntime.dll",
    "onnxruntime_providers_shared.dll",
    "Crosio.Windows.Media.dll",
    "ScreenRecorderLib.dll",
    "THIRD_PARTY_NOTICES.md",
    "Licenses/Microsoft.WindowsAppSDK-LICENSE.txt",
    "Licenses/Microsoft.WindowsAppSDK-NOTICE.txt",
    "Licenses/Microsoft.ML.OnnxRuntime-LICENSE.txt",
    "Licenses/Microsoft.ML.OnnxRuntime-ThirdPartyNotices.txt",
    "Licenses/Microsoft.ML.Tokenizers-LICENSE.txt",
    "Licenses/Microsoft.ML.Tokenizers-ThirdPartyNotices.txt",
    "Licenses/ScreenRecorderLib-LICENSE.txt",
    "ShellExtensions/Crosio.Windows.ShellExtension.dll"
) + $vcRuntimeFiles
foreach ($relativePackageFile in $requiredPackageFiles) {
    $requiredPackageFile = Join-Path $inspectionDirectory $relativePackageFile
    if (-not (Test-Path -LiteralPath $requiredPackageFile -PathType Leaf)) {
        throw "The MSIX is missing required self-contained payload: $relativePackageFile"
    }
}

foreach ($nativePackageFile in (@(
    "Crosio.exe",
    "coreclr.dll",
    "hostfxr.dll",
    "hostpolicy.dll",
    "Microsoft.WindowsAppRuntime.dll",
    "onnxruntime.dll",
    "onnxruntime_providers_shared.dll",
    "ScreenRecorderLib.dll"
) + $vcRuntimeFiles)) {
    Assert-CrosioPeArchitecture (Join-Path $inspectionDirectory $nativePackageFile) $Architecture
}
if ($Architecture -eq "ARM64" -and (Test-Path -LiteralPath (Join-Path $inspectionDirectory "vcruntime140_1.dll"))) {
    throw "The pure ARM64 MSIX must not contain the x64/ARM64EC vcruntime140_1.dll helper."
}
Assert-CrosioPeArchitecture $packagedShellDll $Architecture

$generatedManifestPath = Join-Path $inspectionDirectory "AppxManifest.xml"
$generatedManifest = [xml](Get-Content -Encoding UTF8 -LiteralPath $generatedManifestPath -Raw)
$generatedNamespaces = [System.Xml.XmlNamespaceManager]::new($generatedManifest.NameTable)
$generatedNamespaces.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
$generatedNamespaces.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")
$generatedNamespaces.AddNamespace("desktop", "http://schemas.microsoft.com/appx/manifest/desktop/windows10")
$generatedNamespaces.AddNamespace("desktop5", "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5")
$generatedNamespaces.AddNamespace("com", "http://schemas.microsoft.com/appx/manifest/com/windows10")
$generatedIdentity = $generatedManifest.SelectSingleNode("/f:Package/f:Identity", $generatedNamespaces)
$expectedPackageArchitecture = $Architecture.ToLowerInvariant()
if ($null -eq $generatedIdentity -or $generatedIdentity.GetAttribute("ProcessorArchitecture") -ne $expectedPackageArchitecture) {
    throw "The generated MSIX identity does not target $expectedPackageArchitecture."
}
if ($generatedIdentity.GetAttribute("Publisher") -ne $publisher -or $generatedIdentity.GetAttribute("Version") -ne $Version) {
    throw "The generated MSIX publisher or version does not match the requested package identity."
}
if ($generatedIdentity.GetAttribute("Name") -ne "Crosio.Windows" -or
    $generatedManifest.SelectSingleNode("/f:Package/f:Properties/f:DisplayName", $generatedNamespaces).InnerText -ne "一爪" -or
    $generatedManifest.SelectSingleNode("//uap:VisualElements", $generatedNamespaces).GetAttribute("DisplayName") -ne "一爪") {
    throw "The generated MSIX lost the current display brand or its compatible package identity."
}
$appVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $inspectionDirectory "Crosio.exe"))
if ($appVersionInfo.ProductName -ne "一爪" -or $appVersionInfo.FileDescription -ne "一爪") {
    throw "The packaged executable does not expose the current product display name."
}

$generatedComClass = $generatedManifest.SelectSingleNode("//com:Class", $generatedNamespaces)
if (
    $null -eq $generatedComClass -or
    $generatedComClass.GetAttribute("Id").ToUpperInvariant() -ne "6EA97827-5B42-4E82-8ABC-4FC22D3BE129" -or
    $generatedComClass.GetAttribute("Path") -ne "ShellExtensions\Crosio.Windows.ShellExtension.dll"
) {
    throw "The generated MSIX dropped or changed the Explorer COM registration."
}
$generatedVerbs = @($generatedManifest.SelectNodes("//desktop5:Verb", $generatedNamespaces))
if (
    $generatedVerbs.Count -ne 2 -or
    @($generatedVerbs | Where-Object { $_.GetAttribute("Clsid").ToUpperInvariant() -eq "6EA97827-5B42-4E82-8ABC-4FC22D3BE129" }).Count -ne 2
) {
    throw "The generated MSIX must register the Explorer command for both files and directories."
}
if ($null -eq $generatedManifest.SelectSingleNode("//desktop:StartupTask[@TaskId='CrosioStartupTask']", $generatedNamespaces)) {
    throw "The generated MSIX dropped the opt-in startup task."
}
$generatedImageTypes = @($generatedManifest.SelectNodes("//uap:FileTypeAssociation[@Name='crosio.images']/uap:SupportedFileTypes/uap:FileType", $generatedNamespaces))
if ($generatedImageTypes.Count -ne 9) {
    throw "The generated MSIX dropped one or more image file associations."
}
Remove-Item -LiteralPath $inspectionDirectory -Recurse -Force

if (-not [string]::IsNullOrWhiteSpace($CertificatePath) -and -not $SkipSigning) {
    Invoke-CrosioSign -Path $packagePath -CertificatePath $CertificatePath -CertificatePassword $CertificatePassword -TimestampUrl $TimestampUrl
}

$shellDll = Join-Path $artifactRoot "shell/$Architecture/$Configuration/Crosio.Windows.ShellExtension.dll"
if (-not (Test-Path -LiteralPath $shellDll -PathType Leaf)) {
    throw "The native Explorer command was not built: $shellDll"
}

$hash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText(
    "$packagePath.sha256",
    "$hash  $([System.IO.Path]::GetFileName($packagePath))`n",
    [System.Text.UTF8Encoding]::new($false))

Write-Host "MSIX created: $packagePath"
Write-Output $packagePath
