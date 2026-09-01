; What a tester installs. Per user, start to finish: the app registers its sync root under the
; signed-in profile, seals its token with that user's DPAPI key and puts its URI scheme in HKCU,
; so an installer that asked for administrator rights would be asking for rights it has no use for
; — and every elevation prompt is a tester who stops to ask whether they should have clicked it.
;
; Compiled by package.ps1, which passes the three values below; the defaults are here so the script
; can also be opened and compiled straight from the Inno Setup IDE.

#define AppName "Helmsley Drive"
#define AppExe "HelmsleyDrive.App.exe"

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef PublishDir
  #define PublishDir "..\App\bin\publish\win-" + Arch
#endif

[Setup]
; Fixed, and shared by both architectures: it is what tells Windows that the next build is this
; build again rather than a second app beside it, so an update replaces the install and an ARM
; tester who started on the emulated x64 build moves across without uninstalling first.
AppId={{BCA3C827-60C8-4D1C-998E-787F870063F1}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Helmsley
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
; Under PrivilegesRequired=lowest, {autopf} is %LOCALAPPDATA%\Programs — a location the user owns,
; which is the whole reason no elevation is needed. Neither page has a decision worth making.
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
#if Arch == "arm64"
ArchitecturesAllowed=arm64
#else
; x64compatible rather than x64: an ARM machine runs this build under emulation, and a tester who
; downloaded the wrong one should get a working drive rather than a refusal.
ArchitecturesAllowed=x64compatible
#endif
; The Cloud Filter API arrived in 1709 and the app targets 1809; below that there is no drive.
MinVersion=10.0.17763
OutputDir=..\..\dist
OutputBaseFilename=HelmsleyDrive-{#AppVersion}-{#Arch}
SetupIconFile=..\App\HelmsleyDrive.ico
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; The drive is the running process, so an update or an uninstall has to close it first — otherwise
; the file is locked and the sync root outlives the binary that answers for it. Not restarted
; afterwards: the Startup shortcut is what brings it back, and a half-installed relaunch is worse
; than a deliberate one.
CloseApplications=yes
RestartApplications=no

[Files]
; Whatever the publish produced, minus its symbols: one self-contained executable today, plus the
; .ico that SyncRoot.Icon hands the shell as a path — the registration draws the nav-pane entry
; from that file rather than from anything inside the process, so it has to exist beside the exe.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
; There is no extension process on Windows: the drive answers Explorer only while the app is
; running, so without this a reboot leaves a registered sync root full of placeholders that never
; hydrate — which reads to a tester as the drive having broken overnight.
;
; --background, because a login start belongs in the notification area and not in front of whatever
; the person sat down to do. It mounts either way; only the window is withheld.
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExe}"; Parameters: "--background"

[Registry]
; The app rewrites these itself on every sign-in, pointing them at wherever its executable now
; lives (SignIn.RegisterUriScheme). Writing them here too is what makes the scheme work before the
; first sign-in has happened — and, more to the point, what gets them removed on uninstall instead
; of leaving a protocol handler aimed at a deleted file.
Root: HKCU; Subkey: "Software\Classes\helmsley-drive"; ValueType: string; ValueName: ""; ValueData: "URL:Helmsley Drive"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\helmsley-drive"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKCU; Subkey: "Software\Classes\helmsley-drive\shell\open\command"; ValueType: string; ValueData: """{app}\{#AppExe}"" ""%1"""

[Run]
Filename: "{app}\{#AppExe}"; Description: "Start {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Before the files go, because this is the executable being asked to do it — and it exits quietly
; when there is nothing registered, which is the ordinary case for a tester who signed out first.
;
; It is not a no-op on the folder, whatever the folder's own permanence suggests: Windows drops the
; placeholders it can no longer fetch for, so a tree nobody had opened comes back empty. That is
; the right trade anyway. The alternative is leaving a registration behind that names a deleted
; executable, and every placeholder it governs is a file the portal still holds.
;
; What is left: %USERPROFILE%\Helmsley Drive itself, and %LOCALAPPDATA%\Helmsley Drive, whose
; app.log is usually the reason the app is being removed in the first place.
Filename: "{app}\{#AppExe}"; Parameters: "--unregister"; RunOnceId: "UnregisterSyncRoot"; Flags: runhidden waituntilterminated
