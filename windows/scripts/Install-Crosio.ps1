[CmdletBinding()]
param(
    [string]$PackagePath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if ($env:OS -ne "Windows_NT") {
    throw "一爪 for Windows can only be installed on Windows 11."
}

if ([Environment]::OSVersion.Version.Build -lt 22000) {
    throw "一爪 requires Windows 11 (build 22000) or later."
}

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $bundle = Get-ChildItem -LiteralPath $PSScriptRoot -Filter "*.msixbundle" -File | Select-Object -First 1
    $singlePackage = Get-ChildItem -LiteralPath $PSScriptRoot -Filter "*.msix" -File | Select-Object -First 1
    $candidate = if ($null -ne $bundle) { $bundle } else { $singlePackage }
    if ($null -eq $candidate) {
        throw "No .msixbundle or .msix package was found beside this installer script."
    }
    $PackagePath = $candidate.FullName
}

$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$signature = Get-AuthenticodeSignature -FilePath $resolvedPackage

if ($signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid) {
    Add-AppxPackage -Path $resolvedPackage
    Write-Host "一爪 was installed successfully."
    return
}

if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::NotSigned) {
    throw "The package has an untrusted or invalid signature ($($signature.Status)). Trust the publisher certificate or use an official signed package."
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator) {
    throw "This is an unsigned CI test package. Re-run PowerShell as administrator, then run this installer again."
}

Add-AppxPackage -Path $resolvedPackage -AllowUnsigned
Write-Host "The unsigned 一爪 CI test package was installed successfully. Do not redistribute it as a production release."
