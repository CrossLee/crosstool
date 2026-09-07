# Crosio Windows Explorer command

This native in-process COM component implements the Windows 11
`IExplorerCommand` named **复制路径** for selected files and folders.

The command copies the selected filesystem paths directly to `CF_UNICODETEXT`,
one path per CRLF-delimited line. It deliberately does not launch
`Crosio.Windows.App.exe`; invoking the command therefore cannot display or
activate Crosio's main window.

Build and packaging notes:

- Visual Studio 2022 Desktop C++ workload and Windows 10/11 SDK.
- Build the DLL for the architecture of File Explorer (`x64` and `ARM64` are
  declared in the project).
- The application project builds the architecture-matching DLL and packages it
  as `ShellExtensions\Crosio.Windows.ShellExtension.dll`.
- `Package.appxmanifest` already contains the COM server and the file/folder
  `windows.fileExplorerContextMenus` registrations. `PackageManifest.fragment.xml`
  is retained as the focused registration reference; keep all three CLSID values
  synchronized with `CopyPathExplorerCommand.cpp`.
- Install the signed MSIX, then restart File Explorer (or sign out and in) when
  verifying an extension update.

The package registration is required for the modern Windows 11 context menu.
Legacy registry verbs are intentionally omitted because they normally appear
only under **Show more options** and would create a duplicate command.
