; Focus installer script for Inno Setup 6 (free, from https://jrsoftware.org).
; Don't run this directly: run build-installer.ps1, which builds the app first and then this.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif

[Setup]
; Keep this AppId the same forever, so new versions install over old ones.
AppId={{84A32758-BE40-4BC9-B0BE-DACDB0094843}
AppName=Focus
AppVersion={#MyAppVersion}
AppVerName=Focus {#MyAppVersion}
AppPublisher=Focus
VersionInfoVersion={#MyAppVersion}
; Installs just for you, so no administrator password is needed.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\Focus
DisableDirPage=yes
DisableProgramGroupPage=yes
OutputDir=installer
OutputBaseFilename=FocusSetup
SetupIconFile=app.ico
UninstallDisplayIcon={app}\FocusApp.exe
UninstallDisplayName=Focus
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; Closes Focus if it's running, so it can be updated.
CloseApplications=force
RestartApplications=no

[Tasks]
Name: "desktopicon"; Description: "Add a shortcut to the desktop"; Flags: unchecked

[Files]
Source: "bin\Release\net10.0-windows\win-x64\publish\FocusApp.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Focus"; Filename: "{app}\FocusApp.exe"; Comment: "Plan your day and block distractions"
Name: "{userdesktop}\Focus"; Filename: "{app}\FocusApp.exe"; Tasks: desktopicon

[Registry]
; Start Focus quietly in the tray when you sign in to Windows.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Focus"; ValueData: """{app}\FocusApp.exe"" --startup"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\FocusApp.exe"; Description: "Open Focus now"; Flags: nowait postinstall skipifsilent
; After an automatic update (a silent install), reopen Focus.
Filename: "{app}\FocusApp.exe"; Flags: nowait skipifnotsilent

[UninstallRun]
; Make sure Focus (and its helper) are closed before removing it.
Filename: "{sys}\taskkill.exe"; Parameters: "/f /t /im FocusApp.exe"; Flags: runhidden; RunOnceId: "CloseFocus"

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nFocus helps you plan your day, block distracting apps during focus sessions, and keep track of your focus time. It runs entirely on this PC.
FinishedLabel=Focus is installed. It starts when you sign in to Windows and waits in the system tray, by the clock.
