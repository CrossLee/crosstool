Set-StrictMode -Version Latest

function Invoke-CrosioChecked {
    param(
        [Parameter(Mandatory)]
        [string]$Command,

        [Parameter(ValueFromRemainingArguments)]
        [string[]]$Arguments
    )

    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) {
        # Do not echo arguments here: SignTool arguments can contain a PFX password.
        throw "Command failed with exit code ${LASTEXITCODE}: $Command"
    }
}

function Assert-CrosioPeArchitecture {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [ValidateSet("x64", "ARM64")]
        [string]$ExpectedArchitecture
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required native payload is missing: $Path"
    }

    $peBytes = [System.IO.File]::ReadAllBytes($Path)
    if ($peBytes.Length -lt 64) {
        throw "The native payload is not a valid PE file: $Path"
    }

    $peHeaderOffset = [BitConverter]::ToInt32($peBytes, 60)
    if ($peHeaderOffset -lt 0 -or $peHeaderOffset + 6 -gt $peBytes.Length) {
        throw "The native payload has an invalid PE header: $Path"
    }

    $peMachine = [BitConverter]::ToUInt16($peBytes, $peHeaderOffset + 4)
    $expectedMachine = if ($ExpectedArchitecture -eq "ARM64") { 0xAA64 } else { 0x8664 }
    if ($peMachine -ne $expectedMachine) {
        throw "The native payload does not match $ExpectedArchitecture (PE machine 0x$($peMachine.ToString('X4'))): $Path"
    }
}

function Resolve-CrosioWindowsSdkTool {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("makeappx.exe", "signtool.exe")]
        [string]$Name
    )

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} "Windows Kits/10/bin"
    if (-not (Test-Path -LiteralPath $kitsRoot -PathType Container)) {
        throw "Windows SDK tools directory was not found: $kitsRoot"
    }

    $candidates = @(
        Get-ChildItem -LiteralPath $kitsRoot -Filter $Name -File -Recurse |
            Where-Object { $_.Directory.Name -eq "x64" } |
            Sort-Object { try { [version]$_.Directory.Parent.Name } catch { [version]"0.0" } } -Descending
    )
    if ($candidates.Count -eq 0) {
        throw "$Name was not found in an x64 Windows SDK tools directory."
    }

    return $candidates[0].FullName
}

function Resolve-CrosioVCRuntimeDirectory {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("x64", "ARM64")]
        [string]$Architecture
    )

    if ($env:OS -ne "Windows_NT") {
        throw "The Visual C++ runtime can only be resolved on a Windows build host."
    }

    $architectureDirectory = $Architecture.ToLowerInvariant()
    $requiredFiles = @(
        "concrt140.dll",
        "msvcp140.dll",
        "vcruntime140.dll",
        "vcruntime140_1.dll"
    )
    $redistRoots = @()

    if (-not [string]::IsNullOrWhiteSpace($env:VCToolsRedistDir)) {
        $redistRoots += $env:VCToolsRedistDir
    }

    $programFilesX86 = ${env:ProgramFiles(x86)}
    if (-not [string]::IsNullOrWhiteSpace($programFilesX86)) {
        $vswhere = Join-Path $programFilesX86 "Microsoft Visual Studio/Installer/vswhere.exe"
        if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
            $installations = @(& $vswhere -all -products * -property installationPath)
            if ($LASTEXITCODE -ne 0) {
                throw "vswhere failed while locating the Visual C++ redistributable files."
            }
            foreach ($installation in $installations) {
                if (-not [string]::IsNullOrWhiteSpace($installation)) {
                    $redistRoots += Join-Path $installation "VC/Redist/MSVC"
                }
            }
        }
    }

    $candidateDirectories = @()
    foreach ($redistRoot in @($redistRoots | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $redistRoot -PathType Container)) {
            continue
        }

        $candidateDirectories += Join-Path $redistRoot "$architectureDirectory/Microsoft.VC143.CRT"
        $versionDirectories = @(
            Get-ChildItem -LiteralPath $redistRoot -Directory -ErrorAction SilentlyContinue |
                Sort-Object { try { [version]$_.Name } catch { [version]"0.0" } } -Descending
        )
        foreach ($versionDirectory in $versionDirectories) {
            $candidateDirectories += Join-Path $versionDirectory.FullName "$architectureDirectory/Microsoft.VC143.CRT"
        }
    }

    foreach ($candidateDirectory in @($candidateDirectories | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidateDirectory -PathType Container)) {
            continue
        }

        $missingFile = $false
        foreach ($requiredFile in $requiredFiles) {
            if (-not (Test-Path -LiteralPath (Join-Path $candidateDirectory $requiredFile) -PathType Leaf)) {
                $missingFile = $true
                break
            }
        }
        if (-not $missingFile) {
            return (Resolve-Path -LiteralPath $candidateDirectory).Path
        }
    }

    throw "The $Architecture Microsoft.VC143.CRT redistributable directory was not found. Install the matching MSVC v143 build tools and redistributable files."
}

function Get-CrosioCertificateSubject {
    param(
        [Parameter(Mandatory)]
        [string]$CertificatePath,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$CertificatePassword
    )

    if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
        throw "Signing certificate does not exist: $CertificatePath"
    }

    $securePassword = if ([string]::IsNullOrEmpty($CertificatePassword)) {
        [System.Security.SecureString]::new()
    } else {
        ConvertTo-SecureString -String $CertificatePassword -AsPlainText -Force
    }
    $pfxData = Get-PfxData -FilePath $CertificatePath -Password $securePassword
    $certificates = @($pfxData.EndEntityCertificates)
    if ($certificates.Count -ne 1) {
        throw "The PFX must contain exactly one end-entity signing certificate."
    }

    return $certificates[0].Subject
}

function Invoke-CrosioSign {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$CertificatePath,

        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$CertificatePassword,

        [string]$TimestampUrl
    )

    $signTool = Resolve-CrosioWindowsSdkTool "signtool.exe"
    $arguments = @(
        "sign",
        "/fd", "SHA256",
        "/f", $CertificatePath
    )
    if (-not [string]::IsNullOrEmpty($CertificatePassword)) {
        $arguments += @("/p", $CertificatePassword)
    }
    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $arguments += @("/tr", $TimestampUrl, "/td", "SHA256")
    }
    $arguments += $Path

    Invoke-CrosioChecked $signTool @arguments
}

function Assert-CrosioMsixVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Version
    )

    $parts = $Version.Split('.')
    if ($parts.Count -ne 4) {
        throw "MSIX version must contain four numeric fields: $Version"
    }

    foreach ($part in $parts) {
        $value = 0
        if (-not [int]::TryParse($part, [ref]$value) -or $value -lt 0 -or $value -gt 65535) {
            throw "Each MSIX version field must be between 0 and 65535: $Version"
        }
    }
}
