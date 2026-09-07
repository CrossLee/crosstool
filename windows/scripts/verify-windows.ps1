[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [ValidateSet("x64", "ARM64")]
    [string]$Platform = "x64"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$windowsRoot = Split-Path -Parent $PSScriptRoot
$artifactRoot = Join-Path $windowsRoot "artifacts"
$runtimeIdentifier = if ($Platform -eq "ARM64") { "win-arm64" } else { "win-x64" }
$architecture = $Platform.ToLowerInvariant()

. (Join-Path $PSScriptRoot "Packaging.Common.ps1")

$verificationFailures = [System.Collections.Generic.List[string]]::new()

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$Command,

        [Parameter(ValueFromRemainingArguments)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code ${LASTEXITCODE}: $Command $($Arguments -join ' ')"
    }
}

function Invoke-VerificationStage {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [scriptblock]$Action
    )

    Write-Host "Starting Windows verification stage: $Name"
    try {
        & $Action
    }
    catch {
        $failure = "${Name}: $($_.Exception.Message)"
        $verificationFailures.Add($failure)
        Write-Warning "Windows verification stage failed: $failure"
    }
}

Invoke-VerificationStage -Name "Package layout" -Action {
    & (Join-Path $PSScriptRoot "Test-PackageLayout.ps1")
}

$testProjects = @(
    "tests/Crosio.Windows.Core.Tests/Crosio.Windows.Core.Tests.csproj",
    "tests/Crosio.Windows.Intelligence.Tests/Crosio.Windows.Intelligence.Tests.csproj",
    "tests/Crosio.Windows.Translation.Tests/Crosio.Windows.Translation.Tests.csproj",
    "tests/Crosio.Windows.Sharing.Tests/Crosio.Windows.Sharing.Tests.csproj",
    "tests/Crosio.Windows.Hotkeys.Tests/Crosio.Windows.Hotkeys.Tests.csproj",
    "tests/Crosio.Windows.LongCapture.Tests/Crosio.Windows.LongCapture.Tests.csproj",
    "tests/Crosio.Windows.Media.Tests/Crosio.Windows.Media.Tests.csproj",
    "tests/Crosio.Windows.Capture.Tests/Crosio.Windows.Capture.Tests.csproj",
    "tests/Crosio.Windows.Platform.Tests/Crosio.Windows.Platform.Tests.csproj"
)

foreach ($relativeProject in $testProjects) {
    $project = Join-Path $windowsRoot $relativeProject
    Invoke-VerificationStage -Name "Test $relativeProject" -Action {
        Invoke-Checked -Command "dotnet" -Arguments @("test", $project, "--configuration", $Configuration, "--arch", $architecture)
    }
}

$windowsProjects = @(
    "src/Crosio.Windows.Accessibility/Crosio.Windows.Accessibility.csproj",
    "src/Crosio.Windows.Ocr/Crosio.Windows.Ocr.csproj",
    "src/Crosio.Windows.LongCapture/Crosio.Windows.LongCapture.csproj",
    "src/Crosio.Windows.Media/Crosio.Windows.Media.csproj",
    "src/Crosio.Windows.Capture/Crosio.Windows.Capture.csproj",
    "src/Crosio.Windows.Platform/Crosio.Windows.Platform.csproj"
)

foreach ($relativeProject in $windowsProjects) {
    $project = Join-Path $windowsRoot $relativeProject
    Invoke-VerificationStage -Name "Build $relativeProject" -Action {
        Invoke-Checked -Command "dotnet" -Arguments @("build", $project, "--configuration", $Configuration, "-p:Platform=$Platform")
    }
}

$hostArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
$canRunShellSmoke = ($Platform -eq "x64") -or (($Platform -eq "ARM64") -and ($hostArchitecture -eq "Arm64"))
Invoke-VerificationStage -Name "Native shell build and clipboard smoke test" -Action {
    $shellProject = Join-Path $windowsRoot "shell/Crosio.Windows.ShellExtension/Crosio.Windows.ShellExtension.vcxproj"
    Invoke-Checked -Command "msbuild" -Arguments @($shellProject, "/restore", "/m", "/p:Configuration=$Configuration", "/p:Platform=$Platform")

    $shellSmokeProject = Join-Path $windowsRoot "shell/Crosio.Windows.ShellExtension.SmokeTests/Crosio.Windows.ShellExtension.SmokeTests.vcxproj"
    Invoke-Checked -Command "msbuild" -Arguments @($shellSmokeProject, "/restore", "/m", "/p:Configuration=$Configuration", "/p:Platform=$Platform")

    if ($canRunShellSmoke) {
        $shellExtension = Join-Path $artifactRoot "shell/$Platform/$Configuration/Crosio.Windows.ShellExtension.dll"
        $shellSmokeExecutable = Join-Path $artifactRoot "shell-tests/$Platform/$Configuration/Crosio.Windows.ShellExtension.SmokeTests.exe"
        Invoke-Checked -Command $shellSmokeExecutable -Arguments @($shellExtension)
    }
    else {
        Write-Warning "Built the $Platform native shell smoke harness, but cannot execute it on a $hostArchitecture host."
    }
}

$publishDirectory = Join-Path $artifactRoot "publish/$runtimeIdentifier"
Invoke-VerificationStage -Name "WinUI app build, publish, payload, and startup" -Action {
    $vcRuntimeDirectory = Resolve-CrosioVCRuntimeDirectory $Platform
    $appProject = Join-Path $windowsRoot "src/Crosio.Windows.App/Crosio.Windows.App.csproj"
    Invoke-Checked -Command "dotnet" -Arguments @(
        "build", $appProject,
        "--configuration", $Configuration,
        "-p:Platform=$Platform",
        "-p:CrosioVCRuntimeDirectory=$vcRuntimeDirectory"
    )

    Invoke-Checked -Command "dotnet" -Arguments @(
        "publish", $appProject,
        "--configuration", $Configuration,
        "--runtime", $runtimeIdentifier,
        "--self-contained", "true",
        "--output", $publishDirectory,
        "-p:Platform=$Platform",
        "-p:CrosioVCRuntimeDirectory=$vcRuntimeDirectory",
        "-p:PublishSingleFile=false",
        "-p:EnableMsixTooling=true"
    )

    $requiredPublishFiles = @(
        "Crosio.exe",
        "onnxruntime.dll",
        "onnxruntime_providers_shared.dll",
        "ScreenRecorderLib.dll",
        "concrt140.dll",
        "msvcp140.dll",
        "vcruntime140.dll",
        "vcruntime140_1.dll",
        "THIRD_PARTY_NOTICES.md",
        "Licenses/Microsoft.WindowsAppSDK-LICENSE.txt",
        "Licenses/Microsoft.WindowsAppSDK-NOTICE.txt",
        "Licenses/Microsoft.ML.OnnxRuntime-LICENSE.txt",
        "Licenses/Microsoft.ML.OnnxRuntime-ThirdPartyNotices.txt",
        "Licenses/Microsoft.ML.Tokenizers-LICENSE.txt",
        "Licenses/Microsoft.ML.Tokenizers-ThirdPartyNotices.txt",
        "Licenses/ScreenRecorderLib-LICENSE.txt"
    )
    foreach ($relativePublishFile in $requiredPublishFiles) {
        $publishFile = Join-Path $publishDirectory $relativePublishFile
        if (-not (Test-Path -LiteralPath $publishFile -PathType Leaf)) {
            throw "The unpackaged build is missing a required self-contained payload file: $relativePublishFile"
        }
    }

    foreach ($nativePublishFile in @(
        "Crosio.exe",
        "onnxruntime.dll",
        "onnxruntime_providers_shared.dll",
        "ScreenRecorderLib.dll",
        "concrt140.dll",
        "msvcp140.dll",
        "vcruntime140.dll",
        "vcruntime140_1.dll"
    )) {
        Assert-CrosioPeArchitecture (Join-Path $publishDirectory $nativePublishFile) $Platform
    }

    if ($canRunShellSmoke) {
        & (Join-Path $PSScriptRoot "Test-AppStartup.ps1") -ApplicationPath (Join-Path $publishDirectory "Crosio.exe")
    }
    else {
        Write-Warning "The $Platform unpackaged application was built but cannot be launched on a $hostArchitecture host."
    }
}

if ($verificationFailures.Count -gt 0) {
    $failureSummary = ($verificationFailures | ForEach-Object { " - $_" }) -join "`n"
    throw "Windows verification failed in $($verificationFailures.Count) stage(s):`n$failureSummary"
}

Write-Host "Windows verification passed. Self-contained test build: $publishDirectory"
