#define Version "1.1.3"

[Setup]
AppId={{B3343EEF-2529-464D-9BDF-FA4FECDD18CE}
AppName=BrainSpace
AppVersion={#Version}
DefaultDirName={localappdata}\Programs\BrainSpace
DefaultGroupName=BrainSpace
UninstallDisplayIcon={app}\BrainSpace.exe
SetupIconFile=..\BrainSpace.ico
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\dist
OutputBaseFilename=BrainSpace-Setup-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "..\dist\portable\BrainSpace\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\BrainSpace"; Filename: "{app}\BrainSpace.exe"
