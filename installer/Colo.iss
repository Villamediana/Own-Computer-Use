; Instalador de Colo (Inno Setup 6). Instala solo para el usuario: no pide administrador.
; Compilar: ISCC.exe /DAppVersion=1.3.0 /DSourceDir=..\out installer\Colo.iss
;   SourceDir debe tener Colo.exe y models\whisper\ggml-base-q5_1.bin

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out"
#endif

[Setup]
AppId={{6C0F7A3E-2B1D-4E58-9C67-0B5A3D2E9F41}
AppName=Colo
AppVersion={#AppVersion}
AppVerName=Colo {#AppVersion}
AppPublisher=Miguel Villamediana
DefaultDirName={autopf}\Colo
DefaultGroupName=Colo
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#SourceDir}
OutputBaseFilename=ColoSetup-{#AppVersion}
SetupIconFile=..\src\Colo\colo.ico
UninstallDisplayIcon={app}\Colo.exe
UninstallDisplayName=Colo
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Si Colo está abierto, el instalador lo detecta por su mutex y pide cerrarlo.
AppMutex=Colo.SingleInstance
CloseApplications=yes

[Languages]
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "pt"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\Colo.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\models\whisper\ggml-base-q5_1.bin"; DestDir: "{app}\models\whisper"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Colo"; Filename: "{app}\Colo.exe"
Name: "{autodesktop}\Colo"; Filename: "{app}\Colo.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Colo.exe"; Description: "{cm:LaunchProgram,Colo}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Quita el arranque automático con Windows.
Filename: "{sys}\reg.exe"; Parameters: "delete HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v Colo /f"; Flags: runhidden; RunOnceId: "QuitarAutoarranque"
