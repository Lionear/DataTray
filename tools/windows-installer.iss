; Windows installer for DataTray (Inno Setup 6).
;
; Per-user install by design: no admin prompt, and it matches where the app already writes — the Plugin
; Store installs into %APPDATA%\Lionear\DataTray\plugins, so an installer needing elevation would be
; the only part of the product that does. (SE-206 moved that path off the old product name.)
;
; Built by .github/workflows/build.yml, which passes the values that change per run:
;   ISCC.exe tools\windows-installer.iss /DAppVersion=0.1.0-nightly.20260717.42 /DArch=x64 /DSourceDir=... /DOutputDir=...
;
; The .zip stays the primary artifact; this is the convenience path (Start-menu entry + uninstaller).
; Unsigned, so first run shows a SmartScreen warning — a code-signing certificate is the only fix.

#define AppName "DataTray"
#define AppPublisher "Lionear"
#define AppUrl "https://lionear.dev"
#define ExeName "DataTray.Desktop.exe"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
; Stable AppId: upgrades replace the previous install instead of stacking a second copy. Never change it.
; It survived the SQL Explorer -> DataTray rename (SE-202) for exactly that reason. Consequence worth
; knowing: Inno resolves an upgrade's target from the AppId's registry entry, not from DefaultDirName, so
; a machine that already has SQL Explorer keeps installing into the old "SQL Explorer" directory and its
; old Start-menu group, now under the DataTray name. Only fresh installs land in a DataTray folder.
AppId={{8F3A6C21-4E7B-4D19-9A2E-6C5B1D0E7F84}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user: installs under %LOCALAPPDATA%\Programs, no UAC prompt.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
OutputDir={#OutputDir}
OutputBaseFilename=DataTray-{#AppVersion}-win-{#Arch}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
LicenseFile={#SourceDir}\LICENSE
UninstallDisplayIcon={app}\{#ExeName}
UninstallDisplayName={#AppName}
; In-app updater (SE-137): when the running app launches this installer silently to update itself, let
; Restart Manager close the running instance so its files can be replaced. We relaunch it ourselves in
; [Run] (see the silent entry), so Inno's own restart is off.
CloseApplications=yes
RestartApplications=no
; "x64" over the newer "x64compatible": the latter needs Inno Setup 6.3+, and this has to compile on
; whatever 6.x the runner happens to ship. 6.3+ treats x64 as an alias, so both work.
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "dutch"; MessagesFile: "compiler:Languages\Dutch.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Upgrading a SQL Explorer install (SE-202): every assembly was renamed SqlExplorer.* -> DataTray.*, and
; Inno only overwrites what the new payload contains — it never removes files a previous version left.
; Without this the folder keeps a full second set of binaries, ~90 MB of them, including a launchable
; SqlExplorer.Desktop.exe that still runs the OLD app against the pre-rename %APPDATA% folder. Someone
; starting it from a pinned taskbar button would silently be back on the old build with the old data.
; Runs before [Files], so it can never delete what we are about to install.
Type: files; Name: "{app}\SqlExplorer.*"

; Same problem one level down, and worse: the bundled plugin folders kept their ids but every assembly
; inside them was renamed, and sql-explorer-mcp became datatray-mcp. [Files] lays this tree down whole,
; and Store-installed plugins live in %APPDATA% rather than here (see [UninstallDelete]), so clearing it
; first is both safe and simpler than matching SqlExplorer.* inside each folder.
Type: filesandordirs; Name: "{app}\plugins"

; The pre-rename shortcuts. {group} resolves to the folder recorded at first install, so an upgraded
; machine keeps its "SQL Explorer" Start-menu folder and would otherwise show both names side by side.
Type: files; Name: "{group}\SQL Explorer.lnk"
Type: files; Name: "{autodesktop}\SQL Explorer.lnk"

[Files]
; The whole self-contained publish tree: single-file exe, the bundled plugins/ folder, LICENSE and
; THIRD-PARTY-NOTICES.md (attribution has to travel with the binaries — SE-127).
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#ExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#ExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#ExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Silent self-update path: no wizard to tick "launch", so relaunch the app automatically once files are in.
Filename: "{app}\{#ExeName}"; Flags: nowait postinstall; Check: WizardSilent

[UninstallDelete]
; Plugins the Store installed live in %APPDATA% and are deliberately left behind on uninstall — the same
; reasoning as connections.json: user data outlives the binaries. Only what we installed goes.
Type: filesandordirs; Name: "{app}\plugins"
