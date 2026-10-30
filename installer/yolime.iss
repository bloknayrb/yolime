; yolime-setup.exe: a wrapper around tools\install.ps1, which does all the work (see
; docs\superpowers\plans\2026-10-29-setup-exe.md). Inno owns only its own folder, "Yolime Setup" (the uninstaller and a
; copy of the scripts), and the Apps & Features entry; install.ps1 owns "Yolime" (the program), the shortcut and the
; Run value, exactly as when it runs on its own, so either can upgrade an install made by the other.
;
; Build it with tools\build-setup.ps1, which passes:
;   /DPublishDir=<checked Release publish folder> /DAppVersion=0.1.0+abc1234 /DFileVersion=0.1.0 [/DSandboxTest|/DSandboxE2E]
; SandboxTest builds the only variant that may run elevated (Windows Sandbox's one account is an administrator);
; SandboxE2E also installs an E2E payload, for the in-app update tests. Neither is ever shipped.
;
; Command line, besides Inno's own: /STARTUP=on|off (silent only), /NOLAUNCH, and, from yolime's own updater,
; /UPDATE /RESULT="<file>": install without launching, write the outcome word to that file, and start yolime again
; in DeinitializeSetup, the one launch point, whatever the outcome.
; Pure ASCII.

#if Ver < EncodeVer(6, 3, 0)
  #error Inno Setup 6.3 or later is needed (x64compatible, ExecAndLogOutput)
#endif
#ifndef PublishDir
  #error Pass /DPublishDir=<publish folder> (tools\build-setup.ps1 does)
#endif
#ifndef AppVersion
  #error Pass /DAppVersion=<version> (tools\build-setup.ps1 does)
#endif
#ifndef FileVersion
  #error Pass /DFileVersion=<n.n.n> (tools\build-setup.ps1 does)
#endif
#ifdef SandboxE2E
  #define Suffix "-sandboxe2e"
#else
  #ifdef SandboxTest
    #define Suffix "-sandboxtest"
  #else
    #define Suffix ""
  #endif
#endif

[Setup]
AppId={{A601F07C-0247-4CB7-ACCC-3B0F33A6BE52}
AppName=yolime
AppVersion={#AppVersion}
AppVerName=yolime {#AppVersion}
AppPublisher=Bryan Kolb
VersionInfoVersion={#FileVersion}
UninstallDisplayName=yolime
UninstallDisplayIcon={localappdata}\Programs\Yolime\Yolime.App.exe
; Fixed, and checked again in PrepareToInstall: /DIR= must not move it.
DefaultDirName={localappdata}\Programs\Yolime Setup
DisableDirPage=yes
UsePreviousAppDir=no
DisableProgramGroupPage=yes
DisableWelcomePage=yes
DisableReadyPage=yes
; The start-with-Windows box mirrors the Run value, not what an earlier setup chose.
UsePreviousTasks=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; Restart Manager's close would skip yolime's clean exit; install.ps1 asks it to exit the way its tray's Exit does.
CloseApplications=no
SetupMutex=yolime-setup-A601F07C
SetupLogging=yes
UninstallLogging=yes
WizardStyle=modern
OutputBaseFilename=yolime-setup-{#AppVersion}{#Suffix}
Compression=lzma2/normal
SolidCompression=yes

[Files]
; First, so extracting them in PrepareToInstall doesn't decompress the rest of the solid stream for nothing.
Source: "{#PublishDir}\*"; DestDir: "{tmp}\pkg"; Flags: dontcopy recursesubdirs createallsubdirs ignoreversion
Source: "..\tools\install.ps1"; DestDir: "{tmp}\scripts"; Flags: dontcopy ignoreversion
Source: "..\tools\check-publish.ps1"; DestDir: "{tmp}\scripts"; Flags: dontcopy ignoreversion
; Kept for uninstalling.
Source: "..\tools\install.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\tools\check-publish.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Tasks]
Name: "startup"; Description: "Start yolime when I sign in"

[Messages]
ConfirmUninstall=Remove %1? Your settings, history, voice recordings and saved keys are kept unless you choose to delete them next.
UninstalledAll=%1 was removed.%n%nIf you kept your data, it is in AppData\Roaming\Yolime and AppData\Local\Yolime in your user folder, and your saved keys are in Credential Manager (yolime/...). Installing yolime again picks them up.

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ApprovedKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';
  RunningExit = 2;
  DataKeptExit = 3;
#ifdef SandboxE2E
  ElevatedArg = ' -AllowElevated -AllowE2EPayload';
#else
  #ifdef SandboxTest
  ElevatedArg = ' -AllowElevated';
  #else
  ElevatedArg = '';
  #endif
#endif

var
  Progress: TOutputMarqueeProgressWizardPage;
  LastLine: String;
  Summary: String;
  TasksPreset: Boolean;
  RefusedElevated: Boolean;

function HasParam(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), Name) = 0 then Result := True;
end;

function ElevatedMessage(): String;
begin
  Result := 'yolime-setup is running as administrator. Start it from Explorer, without "Run as administrator": ' +
    'yolime must not run elevated, and an elevated setup may install into another account.';
end;

function RefuseElevated(Silent: Boolean): Boolean;
begin
  Result := False;
  if IsAdmin and (ElevatedArg = '') then
  begin
    Log('Refused: running elevated.');
    if not Silent then MsgBox(ElevatedMessage(), mbError, MB_OK);
    RefusedElevated := True;
    Result := True;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := not RefuseElevated(WizardSilent);
end;

function ProgramDir(): String;
begin
  Result := ExpandConstant('{localappdata}\Programs\Yolime');
end;

function PowerShell(): String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

// Each line the script prints: shown under the progress bar, and kept so a failure can quote its last words.
procedure ScriptLine(const S: String; const Error, FirstLine: Boolean);
begin
  // Given a callback, ExecAndLogOutput leaves the logging to it.
  Log('  ' + S);
  if Trim(S) <> '' then LastLine := Trim(S);
  if Progress <> nil then
  begin
    Progress.SetText('Installing yolime...', LastLine);
    Progress.Animate;
  end;
end;

function RunScript(const Script, Args: String; var Code: Integer): Boolean;
var
  Params: String;
begin
  Params := '-NoProfile -ExecutionPolicy Bypass -File ' + AddQuotes(Script) + ' ' + Args + ElevatedArg;
  Log('Running install.ps1 ' + Args + ElevatedArg);
  LastLine := '';
  Result := ExecAndLogOutput(PowerShell(), Params, ExpandConstant('{sys}'), SW_HIDE, ewWaitUntilTerminated, Code, @ScriptLine);
  if Result then Log('install.ps1 exited with ' + IntToStr(Code)) else Log('Could not start PowerShell: ' + SysErrorMessage(Code));
end;

// Upgrade means "the program folder exists", whoever installed it; then the box shows what's on now.
procedure CurPageChanged(CurPageID: Integer);
var
  Value: String;
  Approved: AnsiString;
  On: Boolean;
begin
  if (CurPageID = wpSelectTasks) and not TasksPreset then
  begin
    TasksPreset := True;
    On := True;
    if DirExists(ProgramDir()) then
    begin
      On := RegQueryStringValue(HKCU, RunKey, 'yolime', Value) and (Trim(Value) <> '');
      if On and RegQueryBinaryValue(HKCU, ApprovedKey, 'yolime', Approved) and (Length(Approved) > 0) then
        On := (Ord(Approved[1]) and 1) = 0;
    end;
    if On then WizardSelectTasks('startup') else WizardSelectTasks('!startup');
  end;
  if CurPageID = wpFinished then
  begin
    WizardForm.FinishedLabel.Caption := Summary;
    WizardForm.FinishedLabel.AdjustHeight;
  end;
end;

function StartupArg(): String;
var
  Value: String;
begin
  Result := '';
  if WizardSilent then
  begin
    Value := ExpandConstant('{param:STARTUP|}');
    if (CompareText(Value, 'on') = 0) or (CompareText(Value, 'off') = 0) then Result := ' -StartWithWindows ' + LowerCase(Value);
  end
  else if WizardIsTaskSelected('startup') then Result := ' -StartWithWindows on'
  else Result := ' -StartWithWindows off';
end;

function LoadSummary(const FileName: String): String;
var
  Lines: TArrayOfString;
  I: Integer;
  Line: String;
begin
  Result := '';
  if not LoadStringsFromFile(FileName, Lines) then Exit;
  for I := 0 to GetArrayLength(Lines) - 1 do
  begin
    Line := Lines[I];
    // The script aligns its columns with spaces, which look ragged in the wizard's font.
    while Pos('  ', Line) > 0 do StringChangeEx(Line, '  ', ' ', True);
    Result := Result + Line + #13#10;
  end;
end;

// The updater's switches: /UPDATE and /NOLAUNCH leave launching to DeinitializeSetup (or to nobody), /RESULT names
// the file the script writes its outcome to.
function UpdateArgs(): String;
var
  ResultPath: String;
begin
  Result := '';
  if HasParam('/UPDATE') or HasParam('/NOLAUNCH') then Result := ' -NoLaunch';
  ResultPath := ExpandConstant('{param:RESULT|}');
  if ResultPath <> '' then Result := Result + ' -ResultFile ' + AddQuotes(ResultPath);
end;

// Under /UPDATE, the one place yolime is started again, on every outcome: after a good install it starts the new copy,
// after a failure the old one (which the script may already have closed). Setup that refused to run elevated starts
// nothing, so it can never start yolime elevated. A crash or kill of setup skips this (Inno can't run it then).
procedure DeinitializeSetup();
var
  ResultPath, Exe: String;
  Code: Integer;
begin
  if not HasParam('/UPDATE') then Exit;
  // Inno decided this outcome itself (refused, /DIR, unpacking, PowerShell) if the script never wrote one.
  ResultPath := ExpandConstant('{param:RESULT|}');
  if (ResultPath <> '') and not FileExists(ResultPath) then SaveStringToFile(ResultPath, 'setup-failed', False);
  if RefusedElevated then Exit;
  // Already running (the copy never stopped, or another copy holds the single instance): starting one would only
  // hand over to it and show "already running".
  if FindWindowByClassName('yolime-tray') <> 0 then
  begin
    Log('yolime is already running; not starting it.');
    Exit;
  end;
  Exe := ProgramDir() + '\Yolime.App.exe';
  if not FileExists(Exe) then Exit;
  if Exec(Exe, '', ProgramDir(), SW_SHOWNORMAL, ewNoWait, Code) then Log('Started yolime.')
  else Log('Could not start yolime: ' + SysErrorMessage(Code));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Script, Args: String;
  Code: Integer;
begin
  Result := '';
  if CompareText(ExpandConstant('{app}'), ExpandConstant('{localappdata}\Programs\Yolime Setup')) <> 0 then
  begin
    Result := 'yolime-setup installs only to ' + ExpandConstant('{localappdata}\Programs') + '; /DIR= is not supported.';
    Log('Refused: ' + Result);
    Exit;
  end;

  Progress := CreateOutputMarqueeProgressPage('Installing yolime', 'This takes up to a minute.');
  Progress.Show;
  WizardForm.CancelButton.Enabled := False;
  try
    Progress.SetText('Unpacking...', '');
    ExtractTemporaryFiles('{tmp}\pkg\*');
    ExtractTemporaryFiles('{tmp}\scripts\*');
    if not FileExists(ExpandConstant('{tmp}\pkg\Yolime.App.exe')) then
    begin
      Result := 'Setup could not unpack yolime.';
      Log(Result);
      Exit;
    end;

    Script := ExpandConstant('{tmp}\scripts\install.ps1');
    Args := '-From ' + AddQuotes(ExpandConstant('{tmp}\pkg')) + ' -Move -SummaryFile ' +
      AddQuotes(ExpandConstant('{tmp}\summary.txt')) + StartupArg() + UpdateArgs();
    if not RunScript(Script, Args, Code) then
    begin
      Result := 'Setup could not start Windows PowerShell (' + PowerShell() + ').';
      Exit;
    end;

    if Code = RunningExit then
    begin
      // The script's own line here tells a script user to add -Force; setup asks instead.
      Progress.SetText('yolime is running.', '');
      if not WizardSilent then
        if MsgBox('yolime is running. Setup will close it the way Exit in its tray menu does, and start the new ' +
          'version afterwards.' + #13#10#13#10 + 'A dictation in progress won''t be pasted; it will be in History to ' +
          'retry, unless history is paused.', mbConfirmation, MB_OKCANCEL) <> IDOK then
        begin
          Result := 'Exit yolime from its tray (right-click the yolime icon > Exit), then click Back and Install again.';
          Exit;
        end;
      // The first run wrote "running"; if the rerun never gets to write its own, that must not be what's reported.
      if ExpandConstant('{param:RESULT|}') <> '' then DeleteFile(ExpandConstant('{param:RESULT|}'));
      if not RunScript(Script, Args + ' -Force', Code) then
      begin
        Result := 'Setup could not start Windows PowerShell (' + PowerShell() + ').';
        Exit;
      end;
    end;

    if Code <> 0 then
    begin
      if LastLine <> '' then Result := LastLine else Result := 'Setup failed (install.ps1 exit ' + IntToStr(Code) + '); see the setup log.';
      Log('Failed: ' + Result);
      Exit;
    end;
    Summary := LoadSummary(ExpandConstant('{tmp}\summary.txt'));
  finally
    WizardForm.CancelButton.Enabled := True;
    Progress.Hide;
  end;
end;

function InitializeUninstall(): Boolean;
begin
  Result := not RefuseElevated(UninstallSilent);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Script, Args, Message: String;
  Code: Integer;
begin
  if CurUninstallStep <> usUninstall then Exit;
  Args := '-Uninstall -Force';
  if HasParam('/REMOVEDATA') then Args := Args + ' -RemoveData'
  else if not UninstallSilent then
    if MsgBox('Also delete your settings, history, voice recordings and saved keys?' + #13#10#13#10 +
      'Choose No to keep them for a later install.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      Args := Args + ' -RemoveData';

  Script := ExpandConstant('{app}\install.ps1');
  if not FileExists(Script) or not RunScript(Script, Args, Code) then
  begin
    // Without the script nothing can remove the program, but the Apps entry shouldn't be stuck forever.
    Log('The uninstall script is missing or PowerShell would not start.');
    if UninstallSilent then Exit;
    if MsgBox('yolime''s uninstall files are missing, so the program in ' + ProgramDir() + ' can''t be removed ' +
      'from here.' + #13#10#13#10 + 'Remove the Apps entry anyway?', mbConfirmation, MB_YESNO) = IDYES then Exit;
    Abort;
  end;
  if Code = DataKeptExit then
  begin
    // The program is gone; only the data deletion was refused or partial. Say so, then finish normally.
    Log('Data kept: ' + LastLine);
    if not UninstallSilent then MsgBox('yolime was removed, but not all of your data was deleted.' + #13#10#13#10 +
      LastLine, mbInformation, MB_OK);
    Exit;
  end;
  if Code <> 0 then
  begin
    Message := LastLine;
    if Message = '' then Message := 'install.ps1 -Uninstall failed with exit ' + IntToStr(Code) + '.';
    Log('Failed: ' + Message);
    if not UninstallSilent then MsgBox('yolime was not removed: ' + Message, mbError, MB_OK);
    // Stops the uninstaller before it removes its own files and the Apps entry, so it can be run again. Abort, not
    // RaiseException: that one adds a "Runtime error" box after the message above.
    Abort;
  end;
end;
