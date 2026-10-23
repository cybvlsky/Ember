; Ember installer, built by .github/workflows/release.yml:
;   iscc /DVersion=1.2.3 /DSource=..\publish installer\Ember.iss
; Source is a self-contained `dotnet publish`, so nothing else has to be on the machine.

#ifndef Version
  #define Version "0.0.0"
#endif
#ifndef Source
  #define Source "..\publish"
#endif

[Setup]
AppId={{5C1B7F0E-3E5B-4F7A-9C1D-8E2A4B6D0F13}
AppName=Ember
AppVersion={#Version}
AppVerName=Ember {#Version}
AppPublisher=cybvlsky
AppPublisherURL=https://github.com/cybvlsky/Ember
AppSupportURL=https://github.com/cybvlsky/Ember/issues
AppUpdatesURL=https://github.com/cybvlsky/Ember/releases
VersionInfoVersion={#Version}
DefaultDirName={autopf}\Ember
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=Ember-Setup-{#Version}
SetupIconFile=..\app.ico
UninstallDisplayIcon={app}\Ember.exe
UninstallDisplayName=Ember
WizardStyle=modern
WizardImageFile=wizard.bmp,wizard-2x.bmp
WizardSmallImageFile=wizard-small.bmp,wizard-small-2x.bmp
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"

[CustomMessages]
en.PawnIo=Install the PawnIO driver (needed for CPU temperature)
pl.PawnIo=Zainstaluj sterownik PawnIO (potrzebny do temperatury procesora)
en.PawnIoStatus=Installing PawnIO...
pl.PawnIoStatus=Instaluję PawnIO...
en.Launch=Start Ember
pl.Launch=Uruchom Ember

[Tasks]
Name: pawnio; Description: "{cm:PawnIo}"; Check: not PawnIoInstalled

[Files]
Source: "{#Source}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Ember"; Filename: "{app}\Ember.exe"

[Run]
; Ember downloads PawnIO's official installer, checks its signature and runs it (elevated, like this setup).
Filename: "{app}\Ember.exe"; Parameters: "--install-pawnio"; StatusMsg: "{cm:PawnIoStatus}"; Tasks: pawnio; Flags: waituntilterminated
; Started elevated it registers its logon task, so later starts need no UAC prompt.
Filename: "{app}\Ember.exe"; Parameters: "--elevated --language {language}"; Description: "{cm:Launch}"; Flags: postinstall nowait skipifsilent runascurrentuser

[UninstallRun]
Filename: "{app}\Ember.exe"; Parameters: "--quit"; RunOnceId: "QuitEmber"; Flags: runhidden waituntilterminated
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN Ember /F"; RunOnceId: "DeleteTask"; Flags: runhidden waituntilterminated

[Code]
function PawnIoInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  // A running Ember keeps its files open; ask it to darken the screen and exit before they are replaced.
  if FileExists(ExpandConstant('{app}\Ember.exe')) then
    Exec(ExpandConstant('{app}\Ember.exe'), '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;
