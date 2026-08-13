#define MyAppName "Pikura"
#define MyAppPublisher "mrplusultra"
#define MyAppURL "https://github.com/pikura-app/pikura"
#define MyAppExeName "Pikura.exe"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVerName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}/issues
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
AllowNoIcons=yes
OutputDir=..\..\publish
OutputBaseFilename=Pikura-{#MyAppVersion}-Setup
SetupIconFile=..\..\src\Pikura.Avalonia\Assets\pikura.ico
; Show the Pikura icon next to the entry in "Apps & features" / Programs and Features.
; Without this directive Windows displays a generic placeholder. Pointing at the
; installed exe (index 0) makes Windows extract the embedded ApplicationIcon from
; the binary itself, so we don't have to ship a separate .ico alongside the install.
UninstallDisplayIcon={app}\{#MyAppExeName},0
UninstallDisplayName={#MyAppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startupicon"; Description: "Start Pikura with Windows"; GroupDescription: "Startup"

[Files]
Source: "..\..\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; IconFilename: "{app}\{#MyAppExeName}"; AppUserModelID: "pikura-app.Pikura"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
  ValueType: string; ValueName: "{#MyAppName}"; \
  ValueData: """{app}\{#MyAppExeName}"""; \
  Flags: uninsdeletevalue; Tasks: startupicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
  Flags: nowait postinstall skipifsilent
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser; \
  Check: WizardSilent

[Code]
// ---------------------------------------------------------------------------
// One-time cleanup: detect and offer to remove the old "Pixora" installation.
//
// We can't rely on a fixed AppId GUID because the old installer may not be
// tracked here, so instead we scan every subkey under the two standard
// Uninstall hives (HKLM and HKCU) and match on DisplayName = "Pixora".
// If found we ask the user once; if they agree we run the recorded
// UninstallString silently with /SILENT so it disappears without showing
// its own wizard UI.
// ---------------------------------------------------------------------------

const
  UninstallBase = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';

function ScanHiveForPixora(RootKey: Integer; out UninstallStr: String): Boolean;
var
  SubkeyNames: TArrayOfString;
  Idx: Integer;
  DisplayName: String;
  UninstallString: String;
begin
  Result := False;
  if not RegGetSubkeyNames(RootKey, UninstallBase, SubkeyNames) then
    Exit;
  for Idx := 0 to GetArrayLength(SubkeyNames) - 1 do
  begin
    if RegQueryStringValue(RootKey,
         UninstallBase + '\' + SubkeyNames[Idx],
         'DisplayName', DisplayName) then
    begin
      if CompareText(Trim(DisplayName), 'Pixora') = 0 then
      begin
        if RegQueryStringValue(RootKey,
             UninstallBase + '\' + SubkeyNames[Idx],
             'UninstallString', UninstallString) then
        begin
          UninstallStr := UninstallString;
          Result := True;
          Exit;
        end;
      end;
    end;
  end;
end;

function FindPixoraUninstallString(out UninstallStr: String): Boolean;
begin
  // Check machine-wide installs first, then per-user installs
  Result := ScanHiveForPixora(HKEY_LOCAL_MACHINE, UninstallStr);
  if not Result then
    Result := ScanHiveForPixora(HKEY_CURRENT_USER, UninstallStr);
end;

function InitializeSetup(): Boolean;
var
  UninstallStr: String;
  ResultCode: Integer;
  ExeFile: String;
  Params: String;
  SepPos: Integer;
begin
  Result := True; // always continue with the Pikura install

  if not FindPixoraUninstallString(UninstallStr) then
    Exit; // no old Pixora found — nothing to do

  if MsgBox(
    'An older installation of Pixora was detected on this computer.' + Chr(13) + Chr(10) +
    Chr(13) + Chr(10) +
    'Pikura is the new name for the same application. The old entry will ' +
    'remain in "Apps & features" alongside Pikura unless it is removed.' + Chr(13) + Chr(10) +
    Chr(13) + Chr(10) +
    'Would you like to remove the old Pixora entry now?',
    mbConfirmation, MB_YESNO) <> IDYES then
    Exit; // user declined — leave the old entry alone

  // Parse the uninstall string. It may be:
  //   "C:\Program Files\Pixora\unins000.exe" /SILENT
  //   C:\Program Files\Pixora\unins000.exe
  // We strip surrounding quotes and append /SILENT /NORESTART ourselves.
  ExeFile := UninstallStr;
  Params  := '/SILENT /NORESTART';

  // If the string starts with a quote, split on the closing quote
  if (Length(ExeFile) > 0) and (ExeFile[1] = '"') then
  begin
    ExeFile := Copy(ExeFile, 2, Length(ExeFile));
    SepPos  := Pos('"', ExeFile);
    if SepPos > 0 then
      ExeFile := Copy(ExeFile, 1, SepPos - 1);
  end;

  // Run the old uninstaller and wait for it to finish
  if not Exec(ExeFile, Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    MsgBox(
      'Could not launch the Pixora uninstaller.' + Chr(13) + Chr(10) +
      'You can remove it manually via "Apps & features".',
      mbError, MB_OK);
end;

// ---------------------------------------------------------------------------
// Uninstall: ask whether to wipe all of Pikura's data (settings, saved login,
// download history/database, cached images, downloaded AI models, etc.) or
// keep it in place so a future reinstall picks up right where the user left
// off (download folder preferences, blocklists, saved login, etc.).
// ---------------------------------------------------------------------------

var
  RemoveAllUserData: Boolean;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  AppDataDir: String;
  LocalAppDataDir: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    RemoveAllUserData := (MsgBox(
      'Completely remove Pikura?' + Chr(13) + Chr(10) +
      Chr(13) + Chr(10) +
      'Choose "Yes" to also delete your settings, saved Pixiv login, download ' +
      'history/database, cached images, and any downloaded AI tagging models.' +
      Chr(13) + Chr(10) + Chr(13) + Chr(10) +
      'Choose "No" to keep your settings (such as your download folder and ' +
      'preferences) in case you reinstall Pikura later.',
      mbConfirmation, MB_YESNO) = IDYES);
  end;

  if CurUninstallStep = usPostUninstall then
  begin
    if RemoveAllUserData then
    begin
      AppDataDir := ExpandConstant('{userappdata}\Pikura');
      LocalAppDataDir := ExpandConstant('{localappdata}\Pikura');
      DelTree(AppDataDir, True, True, True);
      DelTree(LocalAppDataDir, True, True, True);
    end;
  end;
end;
