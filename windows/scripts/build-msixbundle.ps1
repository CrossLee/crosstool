[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$Version = "0.1.0.0",

    [string]$CertificatePath,

    [AllowEmptyString()]
    [string]$CertificatePassword = "",

    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot "Packaging.Common.ps1")

if ($env:OS -ne "Windows_NT") {
    throw "MSIX bundle packaging requires Windows 11 and the Windows SDK."
}

Assert-CrosioMsixVersion $Version

$windowsRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $windowsRoot "artifacts"
$releaseOutput = Join-Path $artifactRoot "release"
$bundleInput = Join-Path $artifactRoot "bundle-input"
$buildScript = Join-Path $PSScriptRoot "build-msix.ps1"
$thirdPartyNotices = Join-Path $windowsRoot "THIRD_PARTY_NOTICES.md"
$packageFlavor = if ([string]::IsNullOrWhiteSpace($CertificatePath)) { "unsigned-test" } else { "signed" }

if ([string]::IsNullOrWhiteSpace($CertificatePath) -and -not [string]::IsNullOrEmpty($CertificatePassword)) {
    throw "CertificatePassword cannot be supplied without CertificatePath."
}

foreach ($directory in @($releaseOutput, $bundleInput)) {
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

$packages = @()
foreach ($architecture in @("x64", "ARM64")) {
    $arguments = @{
        Configuration = $Configuration
        Architecture = $architecture
        Version = $Version
        TimestampUrl = $TimestampUrl
        SkipSigning = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
        $arguments["CertificatePath"] = $CertificatePath
        $arguments["CertificatePassword"] = $CertificatePassword
    }

    & $buildScript @arguments
    $packagePath = Join-Path $artifactRoot "packages/$($architecture.ToLowerInvariant())/Crosio-Windows-$($architecture.ToLowerInvariant()).msix"
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
        throw "The $architecture MSIX build did not create its normalized package."
    }
    $packages += $packagePath
}

$releasePackages = @()
foreach ($package in $packages) {
    Copy-Item -LiteralPath $package -Destination (Join-Path $bundleInput ([System.IO.Path]::GetFileName($package))) -Force
    $architectureName = [System.IO.Path]::GetFileNameWithoutExtension($package).Replace("Crosio-Windows-", "")
    $releasePackage = Join-Path $releaseOutput "Crosio-Windows-$Version-$architectureName-$packageFlavor.msix"
    Copy-Item -LiteralPath $package -Destination $releasePackage -Force
    $releasePackages += $releasePackage
}

$makeAppx = Resolve-CrosioWindowsSdkTool "makeappx.exe"
$bundlePath = Join-Path $releaseOutput "Crosio-Windows-$Version-$packageFlavor.msixbundle"
Invoke-CrosioChecked $makeAppx "bundle" "/d" $bundleInput "/p" $bundlePath "/o"

$releaseNotices = Join-Path $releaseOutput "THIRD_PARTY_NOTICES.md"
Copy-Item -LiteralPath $thirdPartyNotices -Destination $releaseNotices -Force

$releaseFiles = @($releasePackages) + @($bundlePath) + @($releaseNotices)
if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
    foreach ($package in $releaseFiles | Where-Object { $_.EndsWith(".msix", [System.StringComparison]::OrdinalIgnoreCase) }) {
        Invoke-CrosioSign $package $CertificatePath $CertificatePassword $TimestampUrl
    }
    Invoke-CrosioSign $bundlePath $CertificatePath $CertificatePassword $TimestampUrl
}

$checksumLines = foreach ($file in $releaseFiles) {
    $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([System.IO.Path]::GetFileName($file))"
}
[System.IO.File]::WriteAllLines(
    (Join-Path $releaseOutput "SHA256SUMS.txt"),
    $checksumLines,
    [System.Text.UTF8Encoding]::new($false))

Copy-Item -LiteralPath (Join-Path $PSScriptRoot "Install-Crosio.ps1") -Destination $releaseOutput -Force

$buildInfo = [ordered]@{
    product = "Crosio"
    version = $Version
    architectures = @("x64", "ARM64")
    selfContained = $true
    vcRuntimeDeployment = "app-local"
    screenRecorderLib = "7.0.0"
    signed = -not [string]::IsNullOrWhiteSpace($CertificatePath)
    artifactKind = $packageFlavor
    generatedAtUtc = [DateTime]::UtcNow.ToString("O")
}
[System.IO.File]::WriteAllText(
    (Join-Path $releaseOutput "build-info.json"),
    (($buildInfo | ConvertTo-Json) + "`n"),
    [System.Text.UTF8Encoding]::new($false))

Write-Host "MSIX bundle created: $bundlePath"
Write-Output $bundlePath
