; ============================================================================
;  RitschyMirror - Inno Setup Skript (v1.0)
;  Screen Mirror Tool for capture cards (HDR -> SDR tonemapping).
;
;  Per-User-Installation (KEIN Administrator / UAC noetig) - installiert nach
;  %LOCALAPPDATA%\Programs\RitschyMirror. Exe + Config liegen zusammen in einem
;  beschreibbaren Ordner (die App schreibt mirror_config.json / app_settings.json
;  / mirror.log daneben). Deinstallation entfernt den Ordner restlos.
;
;  Build:  "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" RitschyMirror.iss
; ============================================================================

#define MyAppName "RitschyMirror"
#define MyAppVersion "1.0.1"
#define MyAppPublisher "RitschyRigz"
#define MyAppURL "https://github.com/RitschyRigz/ritschy-mirror"
#define MyAppExeName "RitschyMirror.exe"
; Projekt-Root relativ zur .iss (liegt in installer\) — portabel, kein absoluter Build-Pfad.
#define SrcDir SourcePath + ".."

[Setup]
; Eindeutige App-ID (fest - NICHT aendern, sonst erkennt ein Update die alte Installation nicht).
AppId={{8F2C6A14-9B3D-4E7A-AC51-1D9E2F6B0C77}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup

DefaultDirName={localappdata}\Programs\{#MyAppName}
DisableProgramGroupPage=yes
DefaultGroupName={#MyAppName}
AllowNoIcons=yes

; Per-User: keine Admin-Rechte, keine UAC-Abfrage.
PrivilegesRequired=lowest

; 64-bit only (self-contained win-x64 Build).
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Laufende App vor (De-)Installation per Restart Manager schliessen.
CloseApplications=yes
RestartApplications=no

OutputDir={#SrcDir}\installer\out
OutputBaseFilename=RitschyMirror-Setup-{#MyAppVersion}
SetupIconFile={#SrcDir}\assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}

WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutoStartProgram,{#MyAppName}}"; GroupDescription: "{cm:AutoStartProgramGroupDescription}"

[Files]
Source: "{#SrcDir}\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
; Standard-Config nur anlegen, falls noch keine da ist (Update ueberschreibt User-Einstellungen NICHT).
Source: "{#SrcDir}\installer\default_config\mirror_config.json"; DestDir: "{app}"; Flags: onlyifdoesntexist

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Screen Mirror Tool"
Name: "{group}\{#MyAppName} Einstellungen"; Filename: "{app}\{#MyAppExeName}"; Parameters: "--settings"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Vom Programm erzeugte Dateien (Config/Settings/Log) + Ordner restlos entfernen.
Type: filesandordirs; Name: "{app}"
