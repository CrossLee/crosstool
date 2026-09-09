; -*- coding: utf-8 -*-
Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "${CROSIO_CONFIG}"

Name "Crosio"
Caption "Crosio 安装"
OutFile "${CROSIO_OUTPUT}"
RequestExecutionLevel admin
ManifestDPIAware true
ManifestSupportedOS all
SetCompressor zlib
CRCCheck force
SetDatablockOptimize on
ShowInstDetails nevershow
BrandingText "Crosio"
VIProductVersion "${CROSIO_VERSION}"
VIAddVersionKey /LANG=2052 "ProductName" "Crosio"
VIAddVersionKey /LANG=2052 "FileDescription" "Crosio 图形化安装器"
VIAddVersionKey /LANG=2052 "FileVersion" "${CROSIO_VERSION}"
VIAddVersionKey /LANG=2052 "ProductVersion" "${CROSIO_VERSION}"
VIAddVersionKey /LANG=2052 "LegalCopyright" "Crosio contributors"

Var NativePowerShell
Var EngineExitCode
Var EngineOutput
Var FailureMessage

!define MUI_ABORTWARNING
!define MUI_ICON "${CROSIO_ICON}"
!define MUI_CUSTOMFUNCTION_ABORT UserAbort
!define MUI_WELCOMEPAGE_TITLE "欢迎安装 Crosio"
!define MUI_WELCOMEPAGE_TEXT "即将安装 Crosio ${CROSIO_VERSION}${CROSIO_FLAVOR_TEXT}。$\r$\n$\r$\n安装器会自动选择 x64 或 ARM64 版本，无需输入命令。更新会保留已有设置。$\r$\n$\r$\n安装前请退出正在运行的 Crosio，然后点击“安装”。"
!define MUI_PAGE_CUSTOMFUNCTION_SHOW WelcomeShown
!insertmacro MUI_PAGE_WELCOME
!define MUI_INSTFILESPAGE_ABORTHEADER_TEXT "安装失败"
!define MUI_INSTFILESPAGE_ABORTHEADER_SUBTEXT "Crosio 未完成安装。请查看提示后重试。"
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_TITLE "Crosio 安装完成"
!define MUI_FINISHPAGE_TEXT "Crosio 已安装成功。$\r$\n$\r$\n请在开始菜单中搜索 Crosio。卸载时请使用 Windows 设置中的“已安装的应用”。"
!define MUI_FINISHPAGE_RUN
!define MUI_FINISHPAGE_RUN_TEXT "立即打开 Crosio"
!define MUI_FINISHPAGE_RUN_FUNCTION OpenCrosio
!define MUI_FINISHPAGE_RUN_NOTCHECKED
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_LANGUAGE "SimpChinese"

Function .onInit
  SetErrorLevel 2
  ${IfNot} ${RunningX64}
    MessageBox MB_OK|MB_ICONSTOP "Crosio 需要 Windows 11 的 x64 或 ARM64 版本。"
    Quit
  ${EndIf}
  ReadRegStr $0 HKLM "SOFTWARE\Microsoft\Windows NT\CurrentVersion" "CurrentBuildNumber"
  ${If} $0 < 22000
    MessageBox MB_OK|MB_ICONSTOP "Crosio 需要 Windows 11 或更新的桌面版系统。"
    Quit
  ${EndIf}
  StrCpy $NativePowerShell "$WINDIR\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
  IfFileExists "$NativePowerShell" native_ready
    MessageBox MB_OK|MB_ICONSTOP "无法找到系统安装服务，请检查 Windows 安装是否完整。"
    Quit
  native_ready:
  ; NSIS 3.12 creates an elevated plugin directory with an administrator-owned
  ; restricted DACL, before any payload is extracted (exehead CreateRestrictedDirectory).
  InitPluginsDir
FunctionEnd

Function WelcomeShown
  GetDlgItem $0 $HWNDPARENT 1
  SendMessage $0 0x000C 0 "STR:安装(&I)"
FunctionEnd

Section "安装 Crosio"
  SetErrorLevel 1
  SetOutPath "$PLUGINSDIR\Crosio"
  File /oname=Crosio.msixbundle "${CROSIO_BUNDLE}"
  File /oname=payload.json "${CROSIO_PAYLOAD_METADATA}"
  File /oname=Install-Payload.ps1 "${CROSIO_ENGINE}"
  File /oname=LICENSE.NSIS.txt "${CROSIO_NSIS_LICENSE}"
  DetailPrint "正在校验并安装 Crosio，请稍候…"
  ; ExecutionPolicy applies only to this short-lived process, never system policy.
  ; Arguments are fixed; no command shell or user-supplied command is evaluated.
  nsExec::ExecToStack /TIMEOUT=600000 '"$NativePowerShell" -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$PLUGINSDIR\Crosio\Install-Payload.ps1"'
  Pop $EngineExitCode
  Pop $EngineOutput
  ${If} $EngineExitCode != "0"
    ReadINIStr $FailureMessage "$PLUGINSDIR\Crosio\status.ini" "Result" "Message"
    ${If} $FailureMessage == ""
      StrCpy $FailureMessage "系统安装服务未能完成操作（$EngineExitCode）。请重试或重新下载安装包。"
    ${EndIf}
    SetDetailsView show
    DetailPrint "$FailureMessage"
    MessageBox MB_OK|MB_ICONSTOP "$FailureMessage"
    SetErrorLevel 1
    Abort "安装失败。"
  ${EndIf}
  ReadINIStr $0 "$PLUGINSDIR\Crosio\status.ini" "Result" "ApplicationId"
  ${If} $0 == ""
    SetDetailsView show
    DetailPrint "系统没有返回安装成功确认，请重试。"
    SetErrorLevel 1
    Abort "安装失败。"
  ${EndIf}
  DetailPrint "Crosio 安装成功。"
  SetErrorLevel 0
SectionEnd

Function .onInstFailed
  SetErrorLevel 1
FunctionEnd

Function UserAbort
  SetErrorLevel 2
FunctionEnd

Function OpenCrosio
  nsExec::ExecToStack /TIMEOUT=30000 '"$NativePowerShell" -NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "$PLUGINSDIR\Crosio\Install-Payload.ps1" -Mode Open'
  Pop $EngineExitCode
  Pop $EngineOutput
  ${If} $EngineExitCode != "0"
    MessageBox MB_OK|MB_ICONINFORMATION "Crosio 已安装。自动打开未完成，请在开始菜单中搜索 Crosio。"
  ${EndIf}
  SetErrorLevel 0
FunctionEnd
