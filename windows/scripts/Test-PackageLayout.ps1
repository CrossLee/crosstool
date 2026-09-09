[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot "Packaging.Common.ps1")

$windowsRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = Split-Path -Parent $windowsRoot
$manifestPath = Join-Path $windowsRoot "src/Crosio.Windows.App/Package.appxmanifest"
$appProjectPath = Join-Path $windowsRoot "src/Crosio.Windows.App/Crosio.Windows.App.csproj"
$shellProjectPath = Join-Path $windowsRoot "shell/Crosio.Windows.ShellExtension/Crosio.Windows.ShellExtension.vcxproj"
$shellSourcePath = Join-Path $windowsRoot "shell/Crosio.Windows.ShellExtension/CopyPathExplorerCommand.cpp"
$shellFragmentPath = Join-Path $windowsRoot "shell/Crosio.Windows.ShellExtension/PackageManifest.fragment.xml"
$platformFragmentPath = Join-Path $windowsRoot "src/Crosio.Windows.Platform/Packaging/AppExtensions.fragment.xml"
$mediaProjectPath = Join-Path $windowsRoot "src/Crosio.Windows.Media/Crosio.Windows.Media.csproj"
$translationProjectPath = Join-Path $windowsRoot "src/Crosio.Windows.Translation/Crosio.Windows.Translation.csproj"
$exportsPath = Join-Path $windowsRoot "shell/Crosio.Windows.ShellExtension/exports.def"
$iconPath = Join-Path $repositoryRoot "Resources/Brand/OnePaw-AppIcon.png"
$thirdPartyNoticesPath = Join-Path $windowsRoot "THIRD_PARTY_NOTICES.md"
$buildMsixPath = Join-Path $windowsRoot "scripts/build-msix.ps1"
$buildBundlePath = Join-Path $windowsRoot "scripts/build-msixbundle.ps1"
$verifyWindowsPath = Join-Path $windowsRoot "scripts/verify-windows.ps1"

function Assert-True {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,

        [Parameter(Mandatory)]
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

foreach ($requiredPath in @(
    $manifestPath,
    $appProjectPath,
    $shellProjectPath,
    $shellSourcePath,
    $shellFragmentPath,
    $platformFragmentPath,
    $mediaProjectPath,
    $translationProjectPath,
    $exportsPath,
    $iconPath,
    $thirdPartyNoticesPath,
    $buildMsixPath,
    $buildBundlePath,
    $verifyWindowsPath
)) {
    Assert-True (Test-Path -LiteralPath $requiredPath -PathType Leaf) "Missing package input: $requiredPath"
}

$manifest = [xml](Get-Content -LiteralPath $manifestPath -Raw)
$namespaces = [System.Xml.XmlNamespaceManager]::new($manifest.NameTable)
$namespaces.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
$namespaces.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")
$namespaces.AddNamespace("desktop", "http://schemas.microsoft.com/appx/manifest/desktop/windows10")
$namespaces.AddNamespace("desktop4", "http://schemas.microsoft.com/appx/manifest/desktop/windows10/4")
$namespaces.AddNamespace("desktop5", "http://schemas.microsoft.com/appx/manifest/desktop/windows10/5")
$namespaces.AddNamespace("com", "http://schemas.microsoft.com/appx/manifest/com/windows10")

$ignorableNamespaces = @(
    $manifest.Package.GetAttribute("IgnorableNamespaces").Split(
        [char[]]@(" ", "`t", "`r", "`n"),
        [System.StringSplitOptions]::RemoveEmptyEntries
    )
)
foreach ($requiredNamespace in @("com", "desktop4", "desktop5")) {
    Assert-True ($ignorableNamespaces -contains $requiredNamespace) "Package.appxmanifest must list '$requiredNamespace' in IgnorableNamespaces."
}

$identity = $manifest.SelectSingleNode("/f:Package/f:Identity", $namespaces)
Assert-True ($null -ne $identity) "Package identity is missing."
Assert-True ($identity.GetAttribute("Name") -eq "Crosio.Windows") "Unexpected package identity name."
Assert-True ($identity.GetAttribute("Publisher") -match "^CN=") "Package publisher must be a certificate-style subject."
Assert-True ($identity.GetAttribute("Publisher").EndsWith("OID.2.25.311729368913984317654407730594956997722=1", [System.StringComparison]::Ordinal)) "The checked-in publisher must end with Microsoft's exact Windows 11 unsigned-package marker."
Assert-True ($identity.GetAttribute("Version") -match "^\d+\.\d+\.\d+\.\d+$") "Package version must have four numeric fields."

$targetFamily = $manifest.SelectSingleNode("/f:Package/f:Dependencies/f:TargetDeviceFamily", $namespaces)
Assert-True ($null -ne $targetFamily) "Windows.Desktop target family is missing."
Assert-True ($targetFamily.GetAttribute("Name") -eq "Windows.Desktop") "The package must target Windows.Desktop."
Assert-True ([version]($targetFamily.GetAttribute("MinVersion")) -ge [version]"10.0.22000.0") "The package must require Windows 11 or later."

$expectedExtensions = @(".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".heic", ".heif", ".tif", ".tiff")
$actualExtensions = @(
    $manifest.SelectNodes("//uap:FileTypeAssociation[@Name='crosio.images']/uap:SupportedFileTypes/uap:FileType", $namespaces) |
        ForEach-Object { $_.InnerText }
)
Assert-True (($expectedExtensions -join "|") -eq ($actualExtensions -join "|")) "Image file association list is incomplete or reordered."

$startupExtension = $manifest.SelectSingleNode("//desktop:Extension[@Category='windows.startupTask']", $namespaces)
Assert-True ($null -ne $startupExtension) "The windows.startupTask extension is missing."
Assert-True ($startupExtension.GetAttribute("Executable") -eq "Crosio.exe") "The startup task must launch the packaged Crosio executable."
Assert-True ($startupExtension.GetAttribute("EntryPoint") -eq "Windows.FullTrustApplication") "The startup task must use full-trust desktop activation."
$startupTask = $startupExtension.SelectSingleNode("desktop:StartupTask[@TaskId='CrosioStartupTask']", $namespaces)
Assert-True ($null -ne $startupTask) "The Crosio startup task is missing."
Assert-True ($startupTask.GetAttribute("Enabled") -eq "false") "Startup must remain opt-in."

$comExtension = $manifest.SelectSingleNode("//com:Extension[@Category='windows.comServer']", $namespaces)
Assert-True ($null -ne $comExtension) "The windows.comServer extension is missing."
$outOfProcessAttributes = @($comExtension.SelectNodes(".//@Executable | .//@Arguments", $namespaces))
Assert-True ($outOfProcessAttributes.Count -eq 0) "The DLL server must not be registered as an executable COM server."
$surrogateServer = $comExtension.SelectSingleNode("com:ComServer/com:SurrogateServer", $namespaces)
Assert-True ($null -ne $surrogateServer) "The COM class must be registered under com:ComServer/com:SurrogateServer."
foreach ($unsupportedAttribute in @("Executable", "Arguments")) {
    Assert-True (-not $surrogateServer.HasAttribute($unsupportedAttribute)) "com:SurrogateServer must not declare unsupported '$unsupportedAttribute'."
}
Assert-True (-not $surrogateServer.HasAttribute("CustomSurrogateExecutable")) "The Explorer command must use the default system COM surrogate."
Assert-True (-not $surrogateServer.HasAttribute("SystemSurrogate")) "The Explorer command must use the default system COM surrogate."
$surrogateAppId = $surrogateServer.GetAttribute("AppId")
if (-not [string]::IsNullOrWhiteSpace($surrogateAppId)) {
    $parsedAppId = [guid]::Empty
    Assert-True ([guid]::TryParse($surrogateAppId, [ref]$parsedAppId)) "com:SurrogateServer AppId must be a GUID when supplied."
}
$comClass = $surrogateServer.SelectSingleNode("com:Class", $namespaces)
Assert-True ($null -ne $comClass) "The packaged COM server is missing."
Assert-True ($comClass.GetAttribute("Path") -eq "ShellExtensions\Crosio.Windows.ShellExtension.dll") "The COM DLL package path is inconsistent."
Assert-True ($comClass.GetAttribute("ThreadingModel") -eq "STA") "The Explorer command COM class must use the STA threading model."
$manifestClsid = $comClass.GetAttribute("Id").ToUpperInvariant()

$verbs = @($manifest.SelectNodes("//desktop5:Verb", $namespaces))
Assert-True ($verbs.Count -eq 2) "Copy path must be registered once for files and once for directories."
foreach ($verb in $verbs) {
    Assert-True ($verb.GetAttribute("Clsid").ToUpperInvariant() -eq $manifestClsid) "A context-menu verb uses the wrong CLSID."
    Assert-True ($verb.GetAttribute("Id") -match '^[A-Za-z0-9]+$') "A context-menu verb Id must contain only letters and digits, as required by the desktop5 schema."
}

$itemTypes = @(
    $manifest.SelectNodes("//desktop5:ItemType", $namespaces) |
        ForEach-Object { $_.GetAttribute("Type") }
)
Assert-True (($itemTypes -contains "*") -and ($itemTypes -contains "Directory")) "Copy path must support both files and selected folders."

$shellSource = Get-Content -LiteralPath $shellSourcePath -Raw
$hexParts = [regex]::Matches($shellSource, "0x[0-9a-fA-F]+") | Select-Object -First 11
Assert-True ($hexParts.Count -eq 11) "Could not read the native command CLSID."
$nativeClsid = "{0:X8}-{1:X4}-{2:X4}-{3:X2}{4:X2}-{5:X2}{6:X2}{7:X2}{8:X2}{9:X2}{10:X2}" -f @(
    [Convert]::ToUInt32($hexParts[0].Value.Substring(2), 16),
    [Convert]::ToUInt16($hexParts[1].Value.Substring(2), 16),
    [Convert]::ToUInt16($hexParts[2].Value.Substring(2), 16),
    [Convert]::ToByte($hexParts[3].Value.Substring(2), 16),
    [Convert]::ToByte($hexParts[4].Value.Substring(2), 16),
    [Convert]::ToByte($hexParts[5].Value.Substring(2), 16),
    [Convert]::ToByte($hexParts[6].Value.Substring(2), 16),
    [Convert]::ToByte($hexParts[7].Value.Substring(2), 16),
    [Convert]::ToByte($hexParts[8].Value.Substring(2), 16),
    [Convert]::ToByte($hexParts[9].Value.Substring(2), 16),
    [Convert]::ToByte($hexParts[10].Value.Substring(2), 16)
)
Assert-True ($nativeClsid -eq $manifestClsid) "The native IExplorerCommand CLSID does not match Package.appxmanifest."

$shellFragment = [xml](Get-Content -LiteralPath $shellFragmentPath -Raw)
$fragmentNamespaces = [System.Xml.XmlNamespaceManager]::new($shellFragment.NameTable)
$fragmentNamespaces.AddNamespace("com", "http://schemas.microsoft.com/appx/manifest/com/windows10")
$fragmentClass = $shellFragment.SelectSingleNode("//com:Class", $fragmentNamespaces)
Assert-True ($null -ne $fragmentClass) "The Explorer command manifest reference is incomplete."
Assert-True ($fragmentClass.GetAttribute("Id").ToUpperInvariant() -eq $manifestClsid) "The Explorer command manifest reference uses a different CLSID."

$platformFragment = [xml](Get-Content -LiteralPath $platformFragmentPath -Raw)
$platformNamespaces = [System.Xml.XmlNamespaceManager]::new($platformFragment.NameTable)
$platformNamespaces.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")
$platformNamespaces.AddNamespace("desktop", "http://schemas.microsoft.com/appx/manifest/desktop/windows10")
$fragmentStartupTask = $platformFragment.SelectSingleNode("//desktop:StartupTask", $platformNamespaces)
Assert-True ($null -ne $fragmentStartupTask) "The startup-task manifest reference is incomplete."
Assert-True ($fragmentStartupTask.GetAttribute("TaskId") -eq $startupTask.GetAttribute("TaskId")) "The startup-task reference does not match the package manifest."
$fragmentExtensions = @(
    $platformFragment.SelectNodes("//uap:FileType", $platformNamespaces) |
        ForEach-Object { $_.InnerText }
)
Assert-True (($fragmentExtensions -join "|") -eq ($actualExtensions -join "|")) "The file-association reference does not match the package manifest."

$exports = Get-Content -LiteralPath $exportsPath -Raw
Assert-True ($exports -match "(?m)^\s*DllGetClassObject\s") "DllGetClassObject is not exported."
Assert-True ($exports -match "(?m)^\s*DllCanUnloadNow\s") "DllCanUnloadNow is not exported."

$appProject = Get-Content -LiteralPath $appProjectPath -Raw
Assert-True ($appProject -match "EnableMsixTooling") "Single-project MSIX tooling is not enabled."
Assert-True ($appProject -match "<RuntimeIdentifiers>win-x64;win-arm64</RuntimeIdentifiers>") "The app must publish both win-x64 and win-arm64 runtimes."
Assert-True ($appProject -match "<Platforms>x64;ARM64</Platforms>") "The app must expose both x64 and ARM64 package platforms."
Assert-True ($appProject -match "<SelfContained Condition=.*WindowsPackageType.*MSIX.*>true</SelfContained>") "MSIX builds must include the .NET runtime."
Assert-True ($appProject -match "Crosio\.Windows\.ShellExtension\.vcxproj") "The app does not build the native Explorer command."
Assert-True ($appProject -match "ShellExtensions\\Crosio\.Windows\.ShellExtension\.dll") "The native Explorer command is not included in the package payload."
Assert-True ($appProject.Contains('<ApplicationIcon>Assets\OnePaw.ico</ApplicationIcon>')) "The app EXE must embed the approved OnePaw icon."
Assert-True ($appProject.Contains('<Content Include="Assets\*.png;Assets\OnePaw.ico">')) "The approved icon exports must be included in packaged and unpackaged builds."
Assert-True ($appProject -match "\.\./\.\./THIRD_PARTY_NOTICES\.md") "The third-party notices file is not included in app outputs."
Assert-True ($appProject -match "Microsoft\.WindowsAppSDK-LICENSE\.txt") "The Windows App SDK license is not included in app outputs."
Assert-True ($appProject -match '\$\(CrosioVCRuntimeDirectory\)\\\*\.dll') "The matching app-local Visual C++ runtime is not included in app outputs."
foreach ($requiredProjectReference in @(
    "Crosio.Windows.Hotkeys",
    "Crosio.Windows.LongCapture",
    "Crosio.Windows.Media",
    "Crosio.Windows.Translation"
)) {
    $escapedProjectReference = [regex]::Escape("../$requiredProjectReference/$requiredProjectReference.csproj")
    Assert-True ($appProject -match $escapedProjectReference) "The app is missing its $requiredProjectReference project reference."
}

$mediaProject = Get-Content -LiteralPath $mediaProjectPath -Raw
Assert-True ($mediaProject -match '<PackageReference Include="ScreenRecorderLib" Version="7\.0\.0"[^>]*/>') "The production recording backend must pin ScreenRecorderLib 7.0.0."
# ScreenRecorderLib's NuGet target injects the copy-local assembly reference.
# A second Content/None entry duplicates that DLL during transitive publish.
# The real publish/MSIX checks below verify presence and PE architecture.
Assert-True ($mediaProject -notmatch '<(?:None|Content)\s+Include="[^"]*ScreenRecorderLib\.dll"') "ScreenRecorderLib must not be duplicated as an explicit content payload."
Assert-True ($mediaProject -match 'ScreenRecorderLib-LICENSE\.txt') "The ScreenRecorderLib license is not copied into app outputs."
Assert-True ($mediaProject -match '<Platforms>x64;ARM64</Platforms>') "The recording backend must expose the two package architectures."

$translationProject = Get-Content -LiteralPath $translationProjectPath -Raw
foreach ($requiredTranslationNotice in @(
    "Microsoft.ML.OnnxRuntime-LICENSE.txt",
    "Microsoft.ML.OnnxRuntime-ThirdPartyNotices.txt",
    "Microsoft.ML.Tokenizers-LICENSE.txt",
    "Microsoft.ML.Tokenizers-ThirdPartyNotices.txt"
)) {
    Assert-True ($translationProject.Contains($requiredTranslationNotice)) "Translation output is missing $requiredTranslationNotice."
}

$thirdPartyNotices = Get-Content -LiteralPath $thirdPartyNoticesPath -Raw
Assert-True ($thirdPartyNotices -match 'ScreenRecorderLib 7\.0\.0') "The ScreenRecorderLib license notice is missing."

$buildMsix = Get-Content -LiteralPath $buildMsixPath -Raw
Assert-True ($buildMsix -match 'Resolve-CrosioVCRuntimeDirectory') "MSIX builds do not resolve the matching app-local VC++ runtime."
$requiredLicensePayloads = @(
    "Licenses/Microsoft.ML.OnnxRuntime-LICENSE.txt",
    "Licenses/Microsoft.ML.OnnxRuntime-ThirdPartyNotices.txt",
    "Licenses/Microsoft.ML.Tokenizers-LICENSE.txt",
    "Licenses/Microsoft.ML.Tokenizers-ThirdPartyNotices.txt",
    "Licenses/ScreenRecorderLib-LICENSE.txt"
)
foreach ($requiredLicensePayload in $requiredLicensePayloads) {
    Assert-True ($buildMsix.Contains($requiredLicensePayload)) "MSIX inspection does not require $requiredLicensePayload."
}
foreach ($requiredRecordingPayload in @(
    "ScreenRecorderLib.dll",
    "THIRD_PARTY_NOTICES.md"
)) {
    Assert-True ($buildMsix.Contains($requiredRecordingPayload)) "MSIX inspection does not require $requiredRecordingPayload."
}

$x64RuntimeFiles = @(Get-CrosioRequiredVCRuntimeFiles -Architecture "x64")
$arm64RuntimeFiles = @(Get-CrosioRequiredVCRuntimeFiles -Architecture "ARM64")
foreach ($runtimeFile in @("concrt140.dll", "msvcp140.dll", "msvcp140_1.dll", "vcruntime140.dll")) {
    Assert-True ($x64RuntimeFiles -contains $runtimeFile) "The x64 CRT payload does not require $runtimeFile."
    Assert-True ($arm64RuntimeFiles -contains $runtimeFile) "The ARM64 CRT payload does not require $runtimeFile."
}
Assert-True ($x64RuntimeFiles -contains "vcruntime140_1.dll") "The x64 exception runtime is missing from its required payload."
Assert-True ($arm64RuntimeFiles -notcontains "vcruntime140_1.dll") "The ARM64 payload must not require the x64/ARM64EC exception helper."
Assert-True ($buildMsix -match 'Get-CrosioRequiredVCRuntimeFiles') "MSIX inspection does not use the architecture-specific CRT payload."
Assert-True ($appProject -match 'Content Remove="\$\(CrosioVCRuntimeDirectory\)\\vcruntime140_1\.dll"') "App staging does not exclude the incompatible ARM64 redist helper."

$verifyWindows = Get-Content -LiteralPath $verifyWindowsPath -Raw
Assert-True ($verifyWindows -match 'Resolve-CrosioVCRuntimeDirectory') "Unpackaged publishing does not resolve the matching app-local VC++ runtime."
Assert-True ($verifyWindows -match 'Get-CrosioRequiredVCRuntimeFiles') "Unpackaged inspection does not use the architecture-specific CRT payload."
Assert-True ($verifyWindows -match '-p:Platform=\$Platform') "Windows verification must pass Platform explicitly to native-aware builds."
foreach ($requiredLicensePayload in $requiredLicensePayloads) {
    Assert-True ($verifyWindows.Contains($requiredLicensePayload)) "Unpackaged verification does not require $requiredLicensePayload."
}

$buildBundle = Get-Content -LiteralPath $buildBundlePath -Raw
Assert-True ($buildBundle -match 'THIRD_PARTY_NOTICES\.md') "The release directory does not carry third-party notices."
Assert-True ($buildBundle -match 'Crosio-Windows-\$Version-\$architectureName-\$packageFlavor\.msix') "Architecture MSIX release names do not identify their signing state."
Assert-True ($buildBundle -match 'Crosio-Windows-\$Version-\$packageFlavor\.msixbundle') "MSIXBundle release names do not identify their signing state."

$shellProjectXml = [xml](Get-Content -LiteralPath $shellProjectPath -Raw)
$msbuildNamespaces = [System.Xml.XmlNamespaceManager]::new($shellProjectXml.NameTable)
$msbuildNamespaces.AddNamespace("msb", "http://schemas.microsoft.com/developer/msbuild/2003")
$shellConfigurations = @(
    $shellProjectXml.SelectNodes("//msb:ProjectConfiguration", $msbuildNamespaces) |
        ForEach-Object { $_.GetAttribute("Include") }
)
foreach ($requiredConfiguration in @("Debug|x64", "Release|x64", "Debug|ARM64", "Release|ARM64")) {
    Assert-True ($shellConfigurations -contains $requiredConfiguration) "The native Explorer command is missing $requiredConfiguration."
}
$shellProjectText = Get-Content -LiteralPath $shellProjectPath -Raw
Assert-True ($shellProjectText -like '*artifacts\shell\$(Platform)\$(Configuration)\*') "The native Explorer command output is not routed to the package staging path."
Assert-True ($shellProjectText -match "/utf-8") "The native Explorer command must compile its Chinese title as UTF-8."

& (Join-Path $PSScriptRoot "Test-IconAssets.ps1")
$visualElements = $manifest.SelectSingleNode("//uap:VisualElements", $namespaces)
Assert-True ($visualElements.GetAttribute("Square44x44Logo") -eq 'Assets\Square44x44Logo.png') "The app-list icon must use its correctly sized asset family."
Assert-True ($visualElements.GetAttribute("Square150x150Logo") -eq 'Assets\Square150x150Logo.png') "The tile icon must use its correctly sized asset family."
Assert-True ($manifest.Package.Properties.Logo -eq 'Assets\StoreLogo.png') "The package logo must use its separate 50px asset family."
$featureServices = Get-Content -LiteralPath (Join-Path $windowsRoot "src/Crosio.Windows.App/FeatureServices.cs") -Raw
Assert-True ($featureServices.Contains('_trayImage.Value.Handle') -and -not $featureServices.Contains('SystemIcons.Application')) "The tray must use the owned OnePaw icon, not the generic Windows icon."
$mainWindow = Get-Content -LiteralPath (Join-Path $windowsRoot "src/Crosio.Windows.App/MainWindow.xaml.cs") -Raw
Assert-True ($mainWindow.Contains('AppWindow.SetIcon(ApplicationBranding.IconPath)')) "The main window must use the same OnePaw icon."
$editorWindow = Get-Content -LiteralPath (Join-Path $windowsRoot "src/Crosio.Windows.Capture/Editor/ScreenshotEditorWindow.cs") -Raw
$pickerWindow = Get-Content -LiteralPath (Join-Path $windowsRoot "src/Crosio.Windows.Capture/Selection/WindowsMultiWindowCaptureTargetPicker.cs") -Raw
Assert-True ($editorWindow.Contains('WindowBranding.ApplyIcon(this)') -and $pickerWindow.Contains('WindowBranding.ApplyIcon(this)')) "Capture tool windows must not retain generic Windows icons."
$setupScript = Get-Content -LiteralPath (Join-Path $windowsRoot "scripts/build-setup.ps1") -Raw
$setupDefinition = Get-Content -LiteralPath (Join-Path $windowsRoot "installer/CrosioSetup.nsi") -Raw
Assert-True ($setupScript.Contains('Assets/OnePaw.ico') -and $setupDefinition.Contains('!define MUI_ICON "${CROSIO_ICON}"')) "The Setup.exe icon must match the application icon."

Write-Host "Crosio package manifest, Explorer command registration, and payload layout are consistent."
