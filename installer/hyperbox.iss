; HyperBox Windows installer (Inno Setup 6). Built by .github/workflows/build.yml:
;   iscc /DAppVersion=0.3.1 /DSourceExe=path\to\HyperBox.exe /DOutDir=dist installer\hyperbox.iss
; Installs for the current user by default (no admin prompt); the first page offers
; "Install for all users" for people who want it in Program Files.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "HyperBox.exe"
#endif
#ifndef OutDir
  #define OutDir "dist"
#endif

[Setup]
; Keep this AppId forever: it is how Windows knows a new version updates the old one.
AppId={{7C0F2B9E-4D7A-4E2B-9A61-2F3C8B5D9E14}
AppName=HyperBox
AppVersion={#AppVersion}
AppVerName=HyperBox {#AppVersion}
AppPublisher=HyperBox
AppPublisherURL=https://hyperboxstudio.com
AppSupportURL=https://hyperboxstudio.com
AppUpdatesURL=https://hyperboxstudio.com
DefaultDirName={autopf}\HyperBox
DefaultGroupName=HyperBox
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutDir}
OutputBaseFilename=HyperBox-Setup
UninstallDisplayIcon={app}\HyperBox.exe
UninstallDisplayName=HyperBox
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "HyperBox.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\HyperBox"; Filename: "{app}\HyperBox.exe"
Name: "{autodesktop}\HyperBox"; Filename: "{app}\HyperBox.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\HyperBox.exe"; Description: "Open HyperBox"; Flags: nowait postinstall skipifsilent
