#define AppName "BrightSync"
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppPublisher "BrightSync"
#define AppURL "https://github.com/bberka/BrightSync"
#define AppExeName "BrightSync.exe"

#ifndef PublishDir
  #define PublishDir "..\src\bin\Release\net10.0-windows\win-x64\publish"
#endif

#ifndef AppArch
  #define AppArch "x64"
#endif

[Setup]
; Unique App Id
AppId={{0E12368B-B2B0-4A94-9D9B-F5BC332D6DE0}}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}
AppUpdatesURL={#AppURL}
AppMutex=BrightSync-SingleInstance-Mutex-Guid-9b3d-098c86e194a9
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
; Use appropriate install path based on architecture
#if AppArch == "arm64"
ArchitecturesInstallIn64BitMode=arm64
ArchitecturesAllowed=arm64
#else
ArchitecturesInstallIn64BitMode=x64compatible
ArchitecturesAllowed=x64compatible
#endif
; Output configuration
OutputDir=.
OutputBaseFilename={#AppName}-Setup-v{#AppVersion}-{#AppArch}
SetupIconFile=..\src\Resources\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,Logs,Logs\*"
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
