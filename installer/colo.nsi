; Instalador de Colo: por usuario (sin pedir administrador), en %LOCALAPPDATA%\Programs\Colo.
; Accesos en Inicio y Escritorio, arranque con Windows, y aparece en «Aplicaciones instaladas» para desinstalar.
; Instalar encima de una versión anterior la actualiza (cierra el Colo abierto antes de copiar).
; Compilar: makensis -DVERSION=1.2.3 -DEXE=ruta/Colo.exe -DOUT=ruta/ColoSetup.exe installer/colo.nsi

Unicode true
SetCompressor /SOLID lzma

!ifndef VERSION
  !define VERSION "0.0.0"
!endif
!ifndef EXE
  !define EXE "..\out\colo\Colo.exe"
!endif
!ifndef OUT
  !define OUT "..\out\ColoSetup.exe"
!endif

!define UNINST "Software\Microsoft\Windows\CurrentVersion\Uninstall\Colo"

Name "Colo"
OutFile "${OUT}"
InstallDir "$LOCALAPPDATA\Programs\Colo"
InstallDirRegKey HKCU "${UNINST}" "InstallLocation"
RequestExecutionLevel user
BrandingText "Colo ${VERSION}"
VIProductVersion "${VERSION}.0"
VIAddVersionKey "ProductName" "Colo"
VIAddVersionKey "FileDescription" "Instalador de Colo"
VIAddVersionKey "FileVersion" "${VERSION}"
VIAddVersionKey "ProductVersion" "${VERSION}"
VIAddVersionKey "LegalCopyright" "Miguel Villamediana"

!include "MUI2.nsh"
!define MUI_ICON "..\src\Colo\colo.ico"
!define MUI_UNICON "..\src\Colo\colo.ico"
!define MUI_ABORTWARNING
!define MUI_FINISHPAGE_RUN "$INSTDIR\Colo.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Abrir Colo"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "Spanish"

; Cierra Colo si está abierto (por las buenas, guarda lo que estuviera grabando; si no contesta, a la fuerza).
!macro CloseColo
  nsExec::Exec 'taskkill /IM Colo.exe'
  Sleep 2500
  nsExec::Exec 'taskkill /F /IM Colo.exe'
  Sleep 500
!macroend

Section "Colo"
  !insertmacro CloseColo
  SetOutPath "$INSTDIR"
  File "${EXE}"
  WriteUninstaller "$INSTDIR\Desinstalar Colo.exe"

  CreateShortcut "$SMPROGRAMS\Colo.lnk" "$INSTDIR\Colo.exe"
  CreateShortcut "$DESKTOP\Colo.lnk" "$INSTDIR\Colo.exe"

  ; Arranca con Windows desde aquí (Colo lo mantiene igual si se apaga en Ajustes).
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Colo" '"$INSTDIR\Colo.exe"'

  WriteRegStr HKCU "${UNINST}" "DisplayName" "Colo"
  WriteRegStr HKCU "${UNINST}" "DisplayVersion" "${VERSION}"
  WriteRegStr HKCU "${UNINST}" "Publisher" "Miguel Villamediana"
  WriteRegStr HKCU "${UNINST}" "DisplayIcon" "$INSTDIR\Colo.exe"
  WriteRegStr HKCU "${UNINST}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST}" "UninstallString" '"$INSTDIR\Desinstalar Colo.exe"'
  WriteRegStr HKCU "${UNINST}" "QuietUninstallString" '"$INSTDIR\Desinstalar Colo.exe" /S'
  WriteRegDWORD HKCU "${UNINST}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST}" "NoRepair" 1
  WriteRegDWORD HKCU "${UNINST}" "EstimatedSize" 72000
SectionEnd

; Desinstalar quita el programa y sus accesos; los ajustes, la memoria, el cofre y las reuniones se quedan.
Section "Uninstall"
  !insertmacro CloseColo
  Delete "$INSTDIR\Colo.exe"
  Delete "$INSTDIR\Desinstalar Colo.exe"
  ; Lo que Colo saca junto al .exe al arrancar (librerías de Whisper).
  RMDir /r "$INSTDIR\runtimes"
  RMDir /r "$INSTDIR\models"
  RMDir "$INSTDIR"
  Delete "$SMPROGRAMS\Colo.lnk"
  Delete "$DESKTOP\Colo.lnk"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "Colo"
  DeleteRegKey HKCU "${UNINST}"
SectionEnd
