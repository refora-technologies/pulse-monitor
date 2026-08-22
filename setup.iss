; Pulse Monitor — Inno Setup Installer Script
; Package: com.reforatech.pulse
; © 2025 Refora Technologies

[Setup]
AppId={{B8F3E2A4-9D1C-4F7E-A5B2-3C8D6E9F1A2B}
AppName=Pulse
AppVersion=1.1.0
AppVerName=Pulse v1.1.0
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
ArchitecturesAllowed=x64compatible
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
Filename: "{app}\PawnIO_setup.exe"; Parameters: "-install -silent"; Flags: runhidden waituntilterminated; StatusMsg: "Installing sensor driver..."
Filename: "schtasks.exe"; Parameters: "/Create /TN ""PulseMonitor"" /TR ""\""{app}\Pulse.exe\"" --startup"" /SC ONLOGON /RL HIGHEST /F"; Flags: runhidden; Tasks: startupentry
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
  AppPath := Uppercase(ExpandConstant('{app}'));
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
procedure CloseOrphanedCapture();
var
  ResultCode: Integer;
begin
  { Filtered to this user's own processes rather than by image name alone, which would
    end any similarly named process anywhere on the machine, including another user's. }
  Exec(ExpandConstant('{sys}\taskkill.exe'),
       '/F /FI "IMAGENAME eq PresentMon-2.5.1-x64.exe" /FI "USERNAME eq %USERNAME%"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(400);
end;

procedure CloseRunningPulse();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'),
       '/FI "IMAGENAME eq Pulse.exe" /FI "USERNAME eq %USERNAME%"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);

  Exec(ExpandConstant('{sys}\taskkill.exe'),
       '/F /FI "IMAGENAME eq Pulse.exe" /FI "USERNAME eq %USERNAME%"',
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

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
  RemoveDriver: Boolean;
begin
  if CurUninstallStep = usUninstall then
  begin
    CloseRunningPulse();

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
