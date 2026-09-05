; Pulse Monitor — Inno Setup Installer Script
; Package: com.reforatech.pulse
; © 2025 Refora Technologies

[Setup]
AppId={{B8F3E2A4-9D1C-4F7E-A5B2-3C8D6E9F1A2B}
AppName=Pulse
AppVersion=1.2.0
AppVerName=Pulse v1.2.0
AppPublisher=Refora Technologies
AppPublisherURL=https://reforatech.com
AppSupportURL=https://reforatech.com
AppContact=reforatech@gmail.com
DefaultDirName={autopf}\Refora\Pulse
DefaultGroupName=Refora Technologies
OutputDir=installer
OutputBaseFilename=PulseSetup
SetupIconFile=Resources\Icons\pulse.ico
UninstallDisplayIcon={app}\Pulse.exe
LicenseFile=LICENSE
InfoBeforeFile=THIRD-PARTY-NOTICES.txt
Compression=lzma2/ultra64
SolidCompression=yes
PrivilegesRequired=admin
WizardStyle=modern
DisableProgramGroupPage=yes
UninstallDisplayName=Pulse — System Monitor by Refora Technologies
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

; Restart Manager only needs to care about Pulse itself. By default it inspects every exe
; and dll being replaced, which drags in the bundled PresentMon binary — and when it cannot
; get a clean answer about that file it stops with "unable to automatically close all
; applications" even though nothing of ours is running. The capture process is tied to
; Pulse's lifetime and killed outright before install, so Restart Manager has nothing useful
; to add there.
CloseApplicationsFilter=Pulse.exe

; Everything shipped here is x64 (Pulse, PresentMon, the PawnIO driver), so refuse to run
; anywhere it cannot work rather than installing and failing later.
;
; x64os, not x64compatible. They differ on exactly one machine: ARM64 Windows, which counts as
; x64-compatible because it can emulate x64 programs. Pulse itself would indeed run there, and
; PawnIO would not: it is a kernel driver, and a kernel does not emulate anything. The install
; would finish, the driver would fail, and every tile would read "--" with no explanation. Inno
; says the same thing in its own documentation, that driver installers want the OS identifiers.
ArchitecturesAllowed=x64os
MinVersion=10.0

; DisableDirPage defaults to "auto", which hides the folder page on an upgrade but shows it
; on a fresh install. Uninstalling clears the registry entry, so a later reinstall offered
; the page again and a different folder left the old install orphaned on disk with a working
; exe in it — which is how a user ended up with what looked like three copies of Pulse.
DisableDirPage=yes
UsePreviousAppDir=yes

; Deliberately no AppMutex. It is checked before the wizard even starts and can only refuse
; to continue, so it replaced the "Applications in use" page — which closes Pulse for the
; user — with a dead end telling them to go and close it themselves. CloseApplications above
; already detects a running instance through the Restart Manager and handles it gracefully.

; Acknowledged deliberately. The [UninstallDelete] entry below touches a per-user temp path,
; which in an elevated uninstall resolves to whichever account approved the UAC prompt. For
; the usual case — the signed-in user elevating themselves — that is the right folder, and
; when it is not, the entry simply matches nothing. Pulse also clears these on launch, but
; that stops happening once it is uninstalled, which is precisely when the cleanup matters.
UsedUserAreasWarning=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}";
Name: "startupentry"; Description: "Start Pulse when Windows starts"; GroupDescription: "System Integration:";

[Files]
Source: "publish\Pulse.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion
; The font is embedded in Pulse.exe, so its licence has to travel with the install.
Source: "Resources\Fonts\OFL.txt"; DestDir: "{app}"; Flags: ignoreversion
; Kept in {app} rather than {tmp}: the uninstaller needs it to offer driver removal, and a
; deleteafterinstall copy in {tmp} is long gone by then.
Source: "PawnIO_setup.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "Resources\PresentMon\PresentMon-2.5.1-x64.exe"; DestDir: "{app}\Resources\PresentMon"; Flags: ignoreversion

[Icons]
Name: "{group}\Pulse"; Filename: "{app}\Pulse.exe"; IconFilename: "{app}\Pulse.exe"
Name: "{group}\Uninstall Pulse"; Filename: "{uninstallexe}"
Name: "{autodesktop}\Pulse"; Filename: "{app}\Pulse.exe"; IconFilename: "{app}\Pulse.exe"; Tasks: desktopicon

[Run]
; The sensor driver and the startup task are not here any more. [Run] ignores what a program
; exits with: it reports only a failure to start one at all, so a driver installer that ran and
; refused looked exactly like one that worked, and setup finished saying everything was fine
; while every reading in Pulse would show "--". Both are run from [Code] instead, where the
; exit code can be looked at and said out loud. See InstallStep below.
Filename: "{app}\Pulse.exe"; Description: "Launch Pulse"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
; Only remove the startup task if it actually points at *this* install. Every version shares
; the one task name, so deleting it unconditionally meant uninstalling an old copy silently
; broke "start with Windows" for the copy the user kept.
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""PulseMonitor"" /F"; Flags: runhidden; RunOnceId: "DelPulseTask"; Check: TaskTargetsThisInstall

[UninstallDelete]
; Pulse is a single-file app, so .NET unpacks its native libraries here, into a differently
; named folder for every build. Left behind they accumulate and look like stray installs.
Type: filesandordirs; Name: "{localappdata}\Temp\.net\Pulse"

; Update downloads are deliberately locked down to Administrators and SYSTEM, so nothing
; running as the ordinary user can ever clear them out. Pulse tidies them on launch, but
; once it is uninstalled that stops happening — the uninstaller is the last chance, and it
; is elevated, so it is the only thing that can.
Type: filesandordirs; Name: "{localappdata}\Temp\Pulse-update-*"

[Code]
{ True when the PulseMonitor scheduled task runs an exe from the folder being uninstalled.

  /V /FO LIST is used rather than /XML on purpose: /XML emits UTF-16, which does not survive
  LoadStringFromFile, whereas the list format comes back in the console encoding. }
function TaskTargetsThisInstall(): Boolean;
var
  TempFile, AppPath: String;
  Content: AnsiString;
  ResultCode: Integer;
begin
  Result  := False;

  { The whole program, not the folder it sits in.

    This used to look for the folder name anywhere in the listing, which says yes to more than
    it means. An install at C:\Program Files\Pulse would match a task running
    C:\Program Files\Pulse-old\Pulse.exe, because the second contains the first, so uninstalling
    one copy could delete the startup task belonging to another. Matching the full path to the
    executable removes the family of near misses in one go. }
  AppPath := Uppercase(ExpandConstant('{app}\Pulse.exe'));
  TempFile := ExpandConstant('{tmp}\pulse_task_query.txt');

  if Exec(ExpandConstant('{cmd}'),
          '/C schtasks /Query /TN "PulseMonitor" /V /FO LIST > "' + TempFile + '" 2>&1',
          '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if (ResultCode = 0) and LoadStringFromFile(TempFile, Content) then
      Result := Pos(AppPath, Uppercase(String(Content))) > 0;
  end;

  DeleteFile(TempFile);
end;

{ Closes a running Pulse before anything is removed.

  CloseApplications handles this during installation through the Restart Manager, but not
  during uninstallation — so uninstalling while Pulse was running left its files locked
  ("some elements could not be removed") and, worse, left the sensor driver in use, which
  made removing PawnIO fail silently even when the user had asked for it.

  Politely first, so Pulse can finish writing settings and release the driver handle;
  forcefully only if it is still there. Settings are written as they change, so nothing is
  lost either way. }
{ Kills a frame capture process left behind by an older Pulse.

  From 1.1.0 the capture is tied to Pulse's lifetime and cannot outlive it, but a version
  being upgraded or removed may predate that. Its capture process holds
  Resources\PresentMon\PresentMon-2.5.1-x64.exe open, which is what made installs stall on a
  "the file is in use, try again" prompt and left the Resources folder behind afterwards. }
{ Builds a taskkill filter for one image name, owned by the account running this installer.

  The account name has to be substituted here rather than written as %USERNAME%. Exec calls
  CreateProcess directly, with no shell involved, and ExpandConstant is only applied to the
  program path above, not to the parameters. A literal %USERNAME% therefore reached taskkill
  unexpanded and matched no process at all, so every one of these kills silently did nothing.
  That is how the change which added this filter, to stop cleanup reaching other users, ended
  up stopping it reaching anyone.

  Falls back to matching on the image name alone if the account cannot be read, since killing
  slightly too much is better than the uninstaller failing to release its own files. }
function OwnProcessFilter(const ImageName: String): String;
var
  User: String;
begin
  Result := '/FI "IMAGENAME eq ' + ImageName + '"';

  User := GetEnv('USERNAME');
  if User <> '' then
    Result := Result + ' /FI "USERNAME eq ' + User + '"';
end;

procedure CloseOrphanedCapture();
var
  ResultCode: Integer;
begin
  { Scoped to this user's own processes rather than by image name alone, which would end any
    similarly named process anywhere on the machine, including another user's. }
  Exec(ExpandConstant('{sys}\taskkill.exe'),
       '/F ' + OwnProcessFilter('PresentMon-2.5.1-x64.exe'),
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(400);
end;

procedure CloseRunningPulse();
var
  ResultCode: Integer;
begin
  { Politely first. Note this only asks: Pulse minimises to the tray when its window is
    closed, so the request is expected to be declined and the forced kill below is what
    actually ends it. It is still worth sending, because a Pulse that does decide to exit
    here closes its sensor library cleanly and releases the driver handle, which is what
    lets PawnIO be removed afterwards. }
  Exec(ExpandConstant('{sys}\taskkill.exe'),
       OwnProcessFilter('Pulse.exe'),
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);

  { The sensor host is a second process running the same executable, so one image name
    covers both. It is also tied to Pulse's lifetime by a job object and would go on its
    own, but not before the uninstaller has already tried to delete the file it is running. }
  Exec(ExpandConstant('{sys}\taskkill.exe'),
       '/F ' + OwnProcessFilter('Pulse.exe'),
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(800);

  CloseOrphanedCapture();
end;

{ Runs after the wizard and Restart Manager have done their work but before any file is
  written, which is the only point where clearing the orphan actually helps. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseOrphanedCapture();
  Result := '';
end;

{ Burn bootstrappers report 3010 when the removal succeeded but wants a reboot. }
function SucceededOrNeedsReboot(Code: Integer): Boolean;
begin
  Result := (Code = 0) or (Code = 3010);
end;

var
  DriverFailed:  Boolean;
  StartupFailed: Boolean;
  RebootWanted:  Boolean;

{ Runs one installation step and returns what it exited with, or -1 if it never ran.

  The Run section cannot do this. It checks whether a program could be started and then discards
  the exit code entirely, so a driver installer that started and refused was indistinguishable
  from one that succeeded. That is the worst thing to be quiet about here: without the driver
  there are no temperatures and no power readings, which is what most people install Pulse for,
  and the first they would know of it is every tile reading "--". }
function InstallStep(const Exe, Params, Message: String): Integer;
var
  Code: Integer;
begin
  WizardForm.StatusLabel.Caption := Message;

  if Exec(Exe, Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := Code
  else
    Result := -1;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if CurStep <> ssPostInstall then
    exit;

  Code := InstallStep(ExpandConstant('{app}\PawnIO_setup.exe'), '-install -silent',
                      'Installing sensor driver...');

  if Code = 3010 then
    RebootWanted := True
  else if Code <> 0 then
    DriverFailed := True;

  { Registered through Pulse rather than schtasks so there is one definition of this task.
    The bare schtasks command line cannot express three settings that matter here, and it
    silently defaults all three the wrong way for an app meant to run all day: the task will
    not start on battery, Windows terminates it when the machine is unplugged, and Windows
    terminates it again after 72 hours of uptime. See Services\StartupTask.cs. }
  if WizardIsTaskSelected('startupentry') then
  begin
    Code := InstallStep(ExpandConstant('{app}\Pulse.exe'), '--install-startup-task',
                        'Setting up startup...');
    if Code <> 0 then
      StartupFailed := True;
  end;

  { A MsgBox from [Code] appears even under /SILENT, so a quiet install would stop and wait for
    somebody who is not there. The failure still reaches Pulse's own log either way. }
  if WizardSilent() then
    exit;

  if DriverFailed then
    MsgBox('Pulse is installed, but the sensor driver did not install correctly.' + #13#10 + #13#10 +
           'Temperatures, power and fan readings will be unavailable until it does. You can try ' +
           'again by running PawnIO_setup.exe from the Pulse folder.',
           mbError, MB_OK);

  if StartupFailed then
    MsgBox('Pulse is installed, but starting with Windows could not be set up.' + #13#10 + #13#10 +
           'You can switch it on at any time from Pulse''s settings.',
           mbInformation, MB_OK);
end;

{ Asked by Inno at the end. The driver package says 3010 when it is in place but wants a
  restart before it will load, and silently ignoring that leaves somebody with no sensor
  readings and no idea that a reboot is all it needs. }
function NeedRestart(): Boolean;
begin
  Result := RebootWanted;
end;

{ Stops the frame capture trace session, which outlives every process that touched it.

  Pulse names its session PulseMonitor and reuses that one name, so a killed PresentMon leaves
  at most one behind and the next launch takes it over. After an uninstall there is no next
  launch, so it sits registered until the machine reboots, holding one of the few dozen slots
  Windows allows. Filling those is what stopped frame capture working machine wide, including
  for other tools, so leaving one behind on the way out is not a tidy way to go.

  Failure is ignored on purpose: there is usually no session to stop, and logman says so with a
  non zero exit code. }
procedure StopTraceSession();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\logman.exe'), 'stop PulseMonitor -ets',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  RemoveDriver: Boolean;
begin
  if CurUninstallStep = usUninstall then
  begin
    CloseRunningPulse();
    StopTraceSession();

    { A MsgBox created from [Code] is shown even under /SILENT and /SUPPRESSMSGBOXES, so an
      unattended uninstall would sit waiting for an answer nobody is there to give. Silent
      runs therefore skip the question and keep the driver, which is the safe default and
      matches what the visible dialog defaults to. }
    if UninstallSilent() then
      RemoveDriver := False
    else
      RemoveDriver :=
        MsgBox('Also remove the PawnIO sensor driver?' + #13#10 + #13#10 +
               'Other hardware monitoring applications may use it, and removing it could stop ' +
               'them reading your sensors. Choose No if you are not sure.',
               mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;

    { Run here rather than from [UninstallRun] so the result can actually be checked. As an
      UninstallRun entry a failure was invisible, and the driver quietly stayed behind. }
    if RemoveDriver then
    begin
      if not Exec(ExpandConstant('{app}\PawnIO_setup.exe'), '-uninstall -silent',
                  '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
         or not SucceededOrNeedsReboot(ResultCode) then
      begin
        if not UninstallSilent() then
          MsgBox('The PawnIO sensor driver could not be removed automatically.' + #13#10 + #13#10 +
                 'You can remove it yourself from Installed apps in Windows Settings. Pulse ' +
                 'itself will still be uninstalled.',
                 mbInformation, MB_OK);
      end;
    end;
  end;
end;
