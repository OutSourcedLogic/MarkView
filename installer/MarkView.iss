; MarkView installer (Inno Setup 6.7+).
; Build with ..\build-installer.ps1, which publishes the app to %TEMP%\MarkView.Build\publish first.
;
; User experience: UAC prompt -> choose shortcuts -> progress -> Installation Complete
; (Close / Start MarkView). Installs to Program Files, registers with Windows so it shows in
; Settings > Apps, and can be fully uninstalled from there.
;
; File types: MarkView is added to "Open with" for Markdown (.md .markdown .mdown .mkd),
; JSON (.json .jsonc) and text (.txt) files, and listed under Settings > Default apps.
; It never takes over a default; the user chooses "Always" in Open with (or Default apps).

#define AppName "MarkView"
#define AppExe "MarkView.exe"
#ifndef PublishDir
  #define PublishDir GetEnv("TEMP") + "\MarkView.Build\publish"
#endif
; The file version has four parts (1.1.1.0); drop the last one for display (1.1.1).
#define AppVersion RemoveFileExt(GetVersionNumbersString(PublishDir + "\" + AppExe))
#define WebView2ClientKey "Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"

[Setup]
; AppId identifies the app across versions (upgrades, uninstall). Never change it.
AppId={{2F609E08-AD70-4521-B582-411DF135F019}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExe}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

DefaultDirName={autopf}\{#AppName}
PrivilegesRequired=admin
#ifdef PerUserTest
; Test builds only (ISCC /DPerUserTest): allow "/CURRENTUSER" so Setup can run without UAC.
PrivilegesRequiredOverridesAllowed=commandline
#endif
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Tell Explorer to refresh "Open with" after install and uninstall.
ChangesAssociations=yes

; Pages: shortcuts -> installing -> finished.
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes

; Refuse to install/uninstall while the app is running (matches the app's MarkView.Running mutexes).
AppMutex=MarkView.Running,Global\MarkView.Running

WizardStyle=modern dynamic windows11
; Inno's base window size (its default is 120%).
WizardSizePercent=100
SetupIconFile=..\MarkView.ico
; One image per DPI setting, drawn unscaled (see make-wizard-images.ps1).
WizardImageStretch=no
#define WizardImages "assets\wizard-large-*.png"
#define WizardSmallImages "assets\wizard-small-*.png"
; Same images in light and dark mode (dark mode otherwise falls back to Inno's defaults).
WizardImageFile={#WizardImages}
WizardImageFileDynamicDark={#WizardImages}
WizardSmallImageFile={#WizardSmallImages}
WizardSmallImageFileDynamicDark={#WizardSmallImages}

OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}
Compression=lzma2/ultra64
SolidCompression=yes

[Messages]
WizardInstalling=Installing
InstallingLabel=Please wait while [name] is installed.
FinishedHeadingLabel=Installation Complete
ButtonFinish=&Close

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; GroupDescription: "{cm:AdditionalIcons}"
Name: startmenuicon; Description: "Create a &Start menu item"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "redist\MicrosoftEdgeWebview2Setup.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall; Check: NeedsWebView2

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: startmenuicon
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Every entry is removed on uninstall: whole keys MarkView owns (uninsdeletekey), and only
; MarkView's own value under keys shared with other apps (uninsdeletevalue).

; "App Paths" lets Win+R and other launchers start the app by name ("markview").
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExe}"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Microsoft\Windows\CurrentVersion\App Paths\{#AppExe}"; ValueType: string; ValueName: "Path"; ValueData: "{app}"

; File types (ProgIDs): what "Open with > MarkView" runs, and the icon those files get if
; the user makes MarkView their default.
Root: HKA; Subkey: "Software\Classes\MarkView.Markdown"; ValueType: string; ValueName: ""; ValueData: "Markdown Document"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\MarkView.Markdown\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\MarkView.Markdown\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\MarkView.Json"; ValueType: string; ValueName: ""; ValueData: "JSON File"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\MarkView.Json\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\MarkView.Json\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\MarkView.Text"; ValueType: string; ValueName: ""; ValueData: "Text Document"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\MarkView.Text\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\MarkView.Text\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""

; Extension keys MarkView may have created, removed on uninstall only if left empty. They come
; before their OpenWithProgids entries because the uninstaller works through entries in reverse.
Root: HKA; Subkey: "Software\Classes\.md"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\.markdown"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\.mdown"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\.mkd"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\.json"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\.jsonc"; Flags: uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\.txt"; Flags: uninsdeletekeyifempty

; "Open with" list for each extension.
Root: HKA; Subkey: "Software\Classes\.md\OpenWithProgids"; ValueType: string; ValueName: "MarkView.Markdown"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\.markdown\OpenWithProgids"; ValueType: string; ValueName: "MarkView.Markdown"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\.mdown\OpenWithProgids"; ValueType: string; ValueName: "MarkView.Markdown"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\.mkd\OpenWithProgids"; ValueType: string; ValueName: "MarkView.Markdown"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\.json\OpenWithProgids"; ValueType: string; ValueName: "MarkView.Json"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\.jsonc\OpenWithProgids"; ValueType: string; ValueName: "MarkView.Json"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty
Root: HKA; Subkey: "Software\Classes\.txt\OpenWithProgids"; ValueType: string; ValueName: "MarkView.Text"; ValueData: ""; Flags: uninsdeletevalue uninsdeletekeyifempty

; The application itself, as Explorer's "Choose another app" lists it.
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}"; ValueType: string; ValueName: "FriendlyAppName"; ValueData: "{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"" ""%1"""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".md"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".markdown"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mdown"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".mkd"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".json"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".jsonc"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\Applications\{#AppExe}\SupportedTypes"; ValueType: string; ValueName: ".txt"; ValueData: ""

; Settings > Apps > Default apps > MarkView.
Root: HKA; Subkey: "Software\{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationName"; ValueData: "{#AppName}"
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationDescription"; ValueData: "A Notepad-style viewer that shows Markdown and JSON formatted."
Root: HKA; Subkey: "Software\{#AppName}\Capabilities"; ValueType: string; ValueName: "ApplicationIcon"; ValueData: "{app}\{#AppExe},0"
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".md"; ValueData: "MarkView.Markdown"
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".markdown"; ValueData: "MarkView.Markdown"
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mdown"; ValueData: "MarkView.Markdown"
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".mkd"; ValueData: "MarkView.Markdown"
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".json"; ValueData: "MarkView.Json"
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".jsonc"; ValueData: "MarkView.Json"
Root: HKA; Subkey: "Software\{#AppName}\Capabilities\FileAssociations"; ValueType: string; ValueName: ".txt"; ValueData: "MarkView.Text"
Root: HKA; Subkey: "Software\RegisteredApplications"; ValueType: string; ValueName: "{#AppName}"; ValueData: "Software\{#AppName}\Capabilities"; Flags: uninsdeletevalue

[Run]
; The WebView2 Runtime ships with Windows 11 and most Windows 10 PCs; install it only if missing.
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing Microsoft Edge WebView2 Runtime..."; Check: NeedsWebView2

[Code]
var
  StartButton: TNewButton;

function HasWebView2(RootKey: Integer; SubKey: String): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, SubKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

// Per Microsoft's WebView2 distribution guidance: machine-wide or per-user install.
function NeedsWebView2: Boolean;
begin
  Result := not HasWebView2(HKLM32, 'SOFTWARE\{#WebView2ClientKey}')
        and not HasWebView2(HKCU, 'Software\{#WebView2ClientKey}');
end;

procedure StartButtonClick(Sender: TObject);
var
  ResultCode: Integer;
begin
  // Setup runs elevated; start the app as the signed-in user, not as administrator.
  ExecAsOriginalUser(ExpandConstant('{app}\{#AppExe}'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
  WizardForm.NextButton.OnClick(WizardForm.NextButton);
end;

procedure InitializeWizard;
begin
  // Shortcuts page: no intro sentence, just the checkboxes (moved up into its place).
  WizardForm.SelectTasksLabel.Visible := False;
  WizardForm.TasksList.Height := WizardForm.TasksList.Height + WizardForm.TasksList.Top - WizardForm.SelectTasksLabel.Top;
  WizardForm.TasksList.Top := WizardForm.SelectTasksLabel.Top;

  StartButton := TNewButton.Create(WizardForm);
  StartButton.Parent := WizardForm;
  StartButton.Caption := '&Start {#AppName}';
  StartButton.Width := WizardForm.CalculateButtonWidth(['&Start {#AppName}']);
  StartButton.Height := WizardForm.NextButton.Height;
  StartButton.Top := WizardForm.NextButton.Top;
  StartButton.Left := WizardForm.NextButton.Left - StartButton.Width - ScaleX(10);
  StartButton.Anchors := [akRight, akBottom];
  StartButton.OnClick := @StartButtonClick;
  StartButton.Visible := False;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // The shortcuts page is the last one before installing (the Ready page is disabled).
  if CurPageID = wpSelectTasks then
    WizardForm.NextButton.Caption := SetupMessage(msgButtonInstall)
  else if CurPageID = wpFinished then
  begin
    WizardForm.FinishedLabel.Caption :=
      '{#AppName} has been installed on your computer.' + #13#10 + #13#10 +
      'To open a file with it, right-click a Markdown, JSON or text file and choose Open with.';
    // Cancel is hidden on this page; line the buttons up against the right edge.
    WizardForm.NextButton.Left := WizardForm.CancelButton.Left;
    StartButton.Left := WizardForm.NextButton.Left - StartButton.Width - ScaleX(10);
    StartButton.Visible := True;
    StartButton.Default := True;
    WizardForm.NextButton.Default := False;
    WizardForm.ActiveControl := StartButton;
  end;
end;

// A desktop synced by OneDrive can hold a just-created shortcut open while it uploads, so the
// normal delete fails ("in use"). Keep trying for a few seconds.
procedure DeleteWithRetry(const FileName: String);
var
  Attempt: Integer;
begin
  for Attempt := 1 to 20 do
  begin
    if not FileExists(FileName) then Exit;
    if DeleteFile(FileName) then Exit;
    Sleep(500);
  end;
end;

// Complete removal: also delete the user's app data (settings and the formatted view's browser profile).
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DeleteWithRetry(ExpandConstant('{autodesktop}\{#AppName}.lnk'));
    DeleteWithRetry(ExpandConstant('{autoprograms}\{#AppName}.lnk'));
    DelTree(ExpandConstant('{localappdata}\{#AppName}'), True, True, True);
  end;
end;
