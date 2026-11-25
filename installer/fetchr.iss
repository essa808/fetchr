; Fetchr installer (Inno Setup 6). Built by installer\build.ps1, which passes MyVersion and PublishDir.
; Per-user install: no administrator prompt, installs to %LOCALAPPDATA%\Programs\Fetchr.

#ifndef MyVersion
  #define MyVersion "0.3.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\out\publish"
#endif

[Setup]
AppId={{7D3C2E5A-6B1F-4F7E-9A2C-0F8B1D4E6A31}
AppName=Fetchr
AppVersion={#MyVersion}
AppVerName=Fetchr {#MyVersion}
AppPublisher=Fetchr
VersionInfoVersion={#MyVersion}
DefaultDirName={autopf}\Fetchr
DefaultGroupName=Fetchr
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputBaseFilename=Fetchr-Setup-{#MyVersion}
SetupIconFile=fetchr.ico
UninstallDisplayIcon={app}\Fetchr.App.exe
UninstallDisplayName=Fetchr
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "arabic"; MessagesFile: "compiler:Languages\Arabic.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
arabic.TaskBrowsers=ربط Fetchr بمتصفحي Edge وChrome (لالتقاط التحميلات)
english.TaskBrowsers=Connect Fetchr to Edge and Chrome (to catch downloads)
arabic.ExtensionFolder=مجلد إضافة المتصفح
english.ExtensionFolder=Browser extension folder
arabic.AfterInstall=لالتقاط التحميلات من المتصفح: افتح edge://extensions، فعّل وضع المطوّر، اختر "تحميل إضافة غير مضغوطة"، وحدد المجلد:%n%1%n%nيمكنك الوصول إليه لاحقاً من Fetchr ← المتصفح ← فتح المجلد.
english.AfterInstall=To catch downloads from your browser: open edge://extensions, turn on Developer mode, choose "Load unpacked" and pick:%n%1%n%nYou can find it later in Fetchr → Browser → Open folder.

[Tasks]
Name: "browsers"; Description: "{cm:TaskBrowsers}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Fetchr"; Filename: "{app}\Fetchr.App.exe"
Name: "{autoprograms}\Fetchr - {cm:ExtensionFolder}"; Filename: "{app}\extension"
Name: "{autodesktop}\Fetchr"; Filename: "{app}\Fetchr.App.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Fetchr.App.exe"; Parameters: "--register-browsers"; Flags: runhidden waituntilterminated; Tasks: browsers
Filename: "{app}\Fetchr.App.exe"; Description: "{cm:LaunchProgram,Fetchr}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Removes only the browser registration Fetchr made (HKCU keys + its manifest). Downloads and settings stay.
Filename: "{app}\Fetchr.App.exe"; Parameters: "--unregister-browsers"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterBrowsers"

[Code]
procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpFinished) and WizardIsTaskSelected('browsers') then
    WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
      FmtMessage(CustomMessage('AfterInstall'), [ExpandConstant('{app}\extension')]);
end;
