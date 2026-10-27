; Setup.exe wrapper for the Notepads (Sublime features) MSIX package.
; Installs the test certificate, then the MSIX with its dependencies.

[Setup]
AppId={{6B1E5D2A-7C3F-4E8B-9A21-5D0C8F3E7A11}
AppName=Notepads Sublime
#ifndef MyVersion
  #define MyVersion "1.5.6.0"
#endif
AppVersion={#MyVersion}
VersionInfoVersion={#MyVersion}
RestartIfNeededByRun=no
CloseApplications=no
AppPublisher=wooshaq
DefaultDirName={autopf}\Notepads Sublime
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
OutputDir=..\..\Artifacts
OutputBaseFilename=Notepads-Sublime-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayName=Notepads Sublime

[Files]
Source: "..\..\pkg\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Run]
Filename: "{sys}\certutil.exe"; Parameters: "-addstore TrustedPeople ""{app}\Notepads-Sublime.cer"""; StatusMsg: "Dodawanie certyfikatu..."; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""$d = Get-ChildItem -Path '{app}\Dependencies' -Filter *.appx -ErrorAction SilentlyContinue | ForEach-Object FullName; Add-AppxPackage -Path '{app}\Notepads.msix' -DependencyPath $d -ForceApplicationShutdown"""; StatusMsg: "Instalowanie Notepads..."; Flags: runhidden waituntilterminated runasoriginaluser

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -Command ""Get-AppxPackage -Name 'wooshaq.Notepads-Sublime' | Remove-AppxPackage"""; Flags: runhidden waituntilterminated; RunOnceId: "RemoveNotepadsSublime"
