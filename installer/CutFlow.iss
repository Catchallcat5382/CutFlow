#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\\dist\\app"
#endif

#define AppName "CutFlow"
#define AppPublisher "Catchallcat5382"
#define AppExeName "CutFlow.exe"

[Setup]
AppId={{8F4A77C7-2C0C-4C3E-86D0-A9B67E8F7D24}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\CutFlow
DefaultGroupName=CutFlow
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist\installer
OutputBaseFilename=CutFlow-Setup-v{#AppVersion}
SetupIconFile=..\Assets\CutFlow.ico
UninstallDisplayIcon={app}\CutFlow.exe
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
ChangesAssociations=no
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName=CutFlow
VersionInfoDescription=CutFlow Installer

[Files]
Source: "{#SourceDir}\CutFlow.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Icons]
Name: "{group}\CutFlow"; Filename: "{app}\CutFlow.exe"; WorkingDir: "{app}"
Name: "{group}\Uninstall CutFlow"; Filename: "{uninstallexe}"
Name: "{autodesktop}\CutFlow"; Filename: "{app}\CutFlow.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\CutFlow.exe"; Description: "Launch CutFlow"; Flags: nowait postinstall skipifsilent
