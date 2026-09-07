# Crosio Windows platform services

This project contains the Windows-only implementations used by the WinUI host:

- on-device JPEG, PNG, HEIF/HEIC, and TIFF compression through
  `Windows.Graphics.Imaging` (WIC-backed codecs);
- silent file activation, collision-safe output naming, and Explorer selection
  of completed output files;
- direct clipboard path copying without constructing the main window;
- packaged `StartupTask` registration with an HKCU Run fallback for unpackaged
  development builds;
- notification-area registration plus a message-only tray host with **Open**
  and **Exit** commands and Explorer-restart recovery.

Compression preserves the source extension and verifies that the encoded codec
matches that extension. It always writes a sibling `-crosio` copy, uses numbered
suffixes for collisions, and never overwrites the source. HEIF/HEIC availability
depends on codecs installed on the Windows machine; a missing decoder or encoder
is reported instead of silently changing formats.

`Packaging/AppExtensions.fragment.xml` is the focused integration reference.
The declarations are merged into
`windows/src/Crosio.Windows.App/Package.appxmanifest`; the package build also
compiles and copies the architecture-matching native Explorer DLL. The
registration becomes active only after installing the resulting MSIX/MSIXBundle,
not when running an unpackaged developer build.

Run the managed tests with:

```powershell
dotnet test windows/tests/Crosio.Windows.Platform.Tests/Crosio.Windows.Platform.Tests.csproj -c Release
```

Final acceptance must run on Windows 11 x64 and ARM64. In particular, validate
installed codec behavior, `SHOpenFolderAndSelectItems`, startup enable/disable,
tray recovery after restarting Explorer, and both x64 and ARM64 native builds.
