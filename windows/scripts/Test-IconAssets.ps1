[CmdletBinding()]
param([string]$PackageDirectory)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$windowsRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = Split-Path -Parent $windowsRoot
$sourcePath = Join-Path $repositoryRoot "Resources/Brand/OnePaw-AppIcon.png"
$assetsPath = Join-Path $windowsRoot "src/Crosio.Windows.App/Assets"
$metadata = Get-Content -LiteralPath (Join-Path $assetsPath "IconAssets.json") -Raw | ConvertFrom-Json
$expectedSourceHash = "632a1d63d4b47f0062dc8893853f2d832f1da27948c6d27cb44b37e26dd775dd"
if ($metadata.sourceSha256 -cne $expectedSourceHash -or
    (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedSourceHash) {
    throw "The icon source does not match the user-approved OnePaw kitten."
}
if ($PackageDirectory) { $assetsPath = Join-Path $PackageDirectory "Assets" }

$expectedNames = [System.Collections.Generic.List[string]]::new()
foreach ($logo in @("Square44x44Logo", "Square150x150Logo", "StoreLogo")) {
    foreach ($scale in @(100, 125, 150, 200, 400)) { $expectedNames.Add("$logo.scale-$scale.png") }
}
foreach ($pixels in @(16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256)) {
    foreach ($suffix in @("", "_altform-unplated", "_altform-lightunplated")) {
        $expectedNames.Add("Square44x44Logo.targetsize-$pixels$suffix.png")
    }
}
$expectedNames.Add("OnePaw.ico")
$actualNames = @($metadata.assets | ForEach-Object { $_.name })
if ((($expectedNames | Sort-Object) -join "|") -cne (($actualNames | Sort-Object) -join "|")) {
    throw "The icon export manifest is missing required scale, target-size, theme, or ICO assets."
}
foreach ($asset in $metadata.assets) {
    $path = Join-Path $assetsPath $asset.name
    $bytes = [System.IO.File]::ReadAllBytes($path)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset.sha256) {
        throw "Icon asset differs from its approved export: $path"
    }
    if ($asset.format -eq "png") {
        if ($bytes.Length -lt 24 -or [BitConverter]::ToString($bytes, 0, 8) -ne "89-50-4E-47-0D-0A-1A-0A") {
            throw "Invalid PNG icon: $path"
        }
        $width = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 16))
        $height = [System.Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 20))
        $expectedPixels = if ($asset.name -match '^([A-Za-z0-9]+)\.scale-(\d+)\.png$') {
            $base = switch ($Matches[1]) { "Square44x44Logo" { 44 }; "Square150x150Logo" { 150 }; "StoreLogo" { 50 } }
            [int][Math]::Ceiling($base * [int]$Matches[2] / 100)
        } elseif ($asset.name -match '\.targetsize-(\d+)') { [int]$Matches[1] } else { throw "Unknown icon asset name: $path" }
        if ($width -ne $expectedPixels -or $height -ne $expectedPixels -or $asset.pixels -ne $expectedPixels) {
            throw "Incorrect icon dimensions: $path ($width x $height), expected $expectedPixels square."
        }
    } elseif ($asset.format -eq "ico") {
        $sizes = @(16, 24, 32, 48, 256)
        if ($bytes.Length -lt 86 -or [BitConverter]::ToUInt16($bytes, 0) -ne 0 -or
            [BitConverter]::ToUInt16($bytes, 2) -ne 1 -or [BitConverter]::ToUInt16($bytes, 4) -ne $sizes.Count) {
            throw "Invalid multi-size ICO directory: $path"
        }
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $entry = 6 + 16 * $index
            $width = if ($bytes[$entry] -eq 0) { 256 } else { [int]$bytes[$entry] }
            $height = if ($bytes[$entry + 1] -eq 0) { 256 } else { [int]$bytes[$entry + 1] }
            $length = [BitConverter]::ToUInt32($bytes, $entry + 8)
            $offset = [BitConverter]::ToUInt32($bytes, $entry + 12)
            if ($width -ne $sizes[$index] -or $height -ne $width -or $length -eq 0 -or
                $offset -lt 86 -or [long]$offset + $length -gt $bytes.Length) {
                throw "Invalid ICO frame at index $index in $path"
            }
        }
    } else { throw "Unknown icon export format: $($asset.format)" }
}
Write-Host "OnePaw source and 58 icon exports verified: $assetsPath"
