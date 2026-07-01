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
OutputBaseFilename={#AppName}-Setup-v{#AppVersion}-win-{#AppArch}
SetupIconFile=..\src\Resources\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "addtopath"; Description: "Add BrightSync to the PATH environment variable (allows command-line usage from anywhere)"; GroupDescription: "Additional tasks:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "*.pdb,Logs,Logs\*"
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  EnvironmentKey = 'System\CurrentControlSet\Control\Session Manager\Environment';
  UserEnvironmentKey = 'Environment';

function SendMessageTimeout(hWnd: Integer; Msg: Integer; wParam: Integer; lParam: string; fuFlags: Integer; uTimeout: Integer; var lpdwResult: Integer): Integer;
  external 'SendMessageTimeoutW@user32.dll stdcall';

const
  WM_SETTINGCHANGE = $001A;
  SMTO_ABORTIFHUNG = $0002;

procedure NotifyEnvironmentChange();
var
  dwResult: Integer;
begin
  SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, 0, 'Environment', SMTO_ABORTIFHUNG, 5000, dwResult);
end;

function NeedToAddPath(NewPath: string; CurrentPath: string): Boolean;
var
  Paths: TStringList;
  I: Integer;
  NormalizedNewPath: string;
  TempPath: string;
begin
  Result := True;
  NormalizedNewPath := LowerCase(NewPath);
  
  if (Length(NormalizedNewPath) > 0) and (NormalizedNewPath[Length(NormalizedNewPath)] = '\') then
    Delete(NormalizedNewPath, Length(NormalizedNewPath), 1);

  Paths := TStringList.Create;
  try
    Paths.Delimiter := ';';
    Paths.DelimitedText := CurrentPath;
    
    for I := 0 to Paths.Count - 1 do
    begin
      TempPath := LowerCase(Paths[I]);
      if (Length(TempPath) > 0) and (TempPath[Length(TempPath)] = '\') then
        Delete(TempPath, Length(TempPath), 1);
        
      if TempPath = NormalizedNewPath then
      begin
        Result := False;
        Exit;
      end;
    end;
  finally
    Paths.Free;
  end;
end;

procedure AddPath(NewPath: string);
var
  RootKey: Integer;
  Subkey: string;
  CurrentPath: string;
begin
  if IsAdminInstallMode then
  begin
    RootKey := HKEY_LOCAL_MACHINE;
    Subkey := EnvironmentKey;
  end
  else
  begin
    RootKey := HKEY_CURRENT_USER;
    Subkey := UserEnvironmentKey;
  end;

  if RegQueryStringValue(RootKey, Subkey, 'Path', CurrentPath) then
  begin
    if NeedToAddPath(NewPath, CurrentPath) then
    begin
      if (Length(CurrentPath) > 0) and (CurrentPath[Length(CurrentPath)] <> ';') then
        CurrentPath := CurrentPath + ';';
      CurrentPath := CurrentPath + NewPath;
      RegWriteExpandStringValue(RootKey, Subkey, 'Path', CurrentPath);
      NotifyEnvironmentChange();
      Log('Added path to environment: ' + NewPath);
    end;
  end;
end;

procedure RemovePath(OldPath: string);
var
  RootKey: Integer;
  Subkey: string;
  CurrentPath: string;
  Paths: TStringList;
  I: Integer;
  NewPathList: string;
  NormalizedOldPath: string;
  TempPath: string;
  Changed: Boolean;
begin
  if IsAdminInstallMode then
  begin
    RootKey := HKEY_LOCAL_MACHINE;
    Subkey := EnvironmentKey;
  end
  else
  begin
    RootKey := HKEY_CURRENT_USER;
    Subkey := UserEnvironmentKey;
  end;

  NormalizedOldPath := LowerCase(OldPath);
  if (Length(NormalizedOldPath) > 0) and (NormalizedOldPath[Length(NormalizedOldPath)] = '\') then
    Delete(NormalizedOldPath, Length(NormalizedOldPath), 1);

  if RegQueryStringValue(RootKey, Subkey, 'Path', CurrentPath) then
  begin
    Paths := TStringList.Create;
    try
      Paths.Delimiter := ';';
      Paths.DelimitedText := CurrentPath;
      
      Changed := False;
      NewPathList := '';
      
      for I := 0 to Paths.Count - 1 do
      begin
        TempPath := LowerCase(Paths[I]);
        if (Length(TempPath) > 0) and (TempPath[Length(TempPath)] = '\') then
          Delete(TempPath, Length(TempPath), 1);
          
        if TempPath <> NormalizedOldPath then
        begin
          if Length(NewPathList) > 0 then
            NewPathList := NewPathList + ';';
          NewPathList := NewPathList + Paths[I];
        end
        else
        begin
          Changed := True;
        end;
      end;
      
      if Changed then
      begin
        RegWriteExpandStringValue(RootKey, Subkey, 'Path', NewPathList);
        NotifyEnvironmentChange();
        Log('Removed path from environment: ' + OldPath);
      end;
    finally
      Paths.Free;
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    if WizardIsTaskSelected('addtopath') then
    begin
      AddPath(ExpandConstant('{app}'));
    end;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemovePath(ExpandConstant('{app}'));
  end;
end;
