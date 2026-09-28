; ============================================================================

;  Fluxion - Inno Setup 6 installer script

;  Build:  ISCC.exe installer\Fluxion.iss   (or double-click make_installer.bat)

;  OUTPUT: <project>\installer_out\Fluxion-Setup-3.5.0.exe

;

;  NOTE 1 - FILE ENCODING: saved as UTF-8 **with BOM** so Inno Setup reads the

;  Chinese strings correctly. Keep the BOM when editing, otherwise every

;  Chinese literal turns into mojibake.

;

;  NOTE 2 - ELEVATION (why the "立即运行" step used to fail with code 740):

;  Fluxion.exe carries a requireAdministrator manifest. Inno runs

;  postinstall [Run] entries with the ORIGINAL (non-elevated) user token on

;  purpose - so apps it launches do not silently inherit admin rights. A plain

;  CreateProcess on a requireAdministrator exe from there fails with

;      "CreateProcess failed; code 740. 请求的操作需要提升."

;  The launch entry below therefore sets the `shellexec` flag, which routes

;  through ShellExecuteEx so the shell honours the manifest and raises the UAC

;  prompt.

;

;  NOTE 3 - AUTOSTART: HKCU\...\Run is deliberately NOT used anymore. Windows

;  launches Run-key entries through the shell, so a requireAdministrator app

;  there would raise a UAC prompt at EVERY logon. A scheduled task with

;  /RL HIGHEST starts elevated with no prompt, and it uses the SAME task name

;  that the app's own "开机自启" switch manages, so both stay in sync.

; ============================================================================



#define AppName      "Fluxion"

#define AppShort     "Fluxion"

#define AppVersion   "1.0.8"

#define AppPublisher "Seven John"

#define AppExe       "Fluxion.exe"

#define AppMutex     "Fluxion_SingleInstance"

#define AppTaskName  "Fluxion"

#define DataFolder   "Fluxion"



[Setup]

; AppId must stay constant across versions - it is how Windows identifies upgrades.

; v1.0.0 用全新 AppId：全套品牌改名（GameBoost-DLSSG → Fluxion）按全新产品对待，

; 与旧版并存而不是原地覆盖 —— 旧版需要手动卸载，数据由程序内的品牌迁移逻辑搬到

; %ProgramData%\Fluxion（搬完删除旧目录，不留两份）。

AppId={{CAEFA917-F5CB-40FE-A52D-8AC5FD8FB40C}

AppName={#AppName}

AppVersion={#AppVersion}

AppVerName={#AppName} {#AppVersion}

AppPublisher={#AppPublisher}

AppComments=游戏 / 系统优化工具：底层调优 + DLSS 帧生成（SM86 / SM75 内核补齐）

DefaultDirName={autopf}\{#AppName}

DefaultGroupName={#AppName}

DisableProgramGroupPage=yes

AllowNoIcons=yes

PrivilegesRequired=admin

ArchitecturesAllowed=x64compatible

ArchitecturesInstallIn64BitMode=x64compatible

MinVersion=10.0

OutputDir=..\installer_out

OutputBaseFilename={#AppName}-Setup-{#AppVersion}

Compression=lzma2/max

SolidCompression=yes

WizardStyle=modern

SetupIconFile=..\icon\icon.ico

UninstallDisplayIcon={app}\{#AppExe}

UninstallDisplayName={#AppName} {#AppVersion}

; The app itself holds this mutex, so Setup can detect a running instance and

; ask the user to close it before replacing files.

AppMutex={#AppMutex}

CloseApplications=yes

RestartApplications=no

VersionInfoVersion=1.0.8.0

VersionInfoDescription={#AppName} 安装程序

VersionInfoProductName={#AppName}

VersionInfoProductVersion={#AppVersion}

VersionInfoCompany={#AppPublisher}



[Languages]

Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

Name: "english"; MessagesFile: "compiler:Default.isl"



[Tasks]

Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

Name: "startup";     Description: "开机自动启动（提权计划任务，无 UAC 提示）"; GroupDescription: "附加任务："; Flags: unchecked



[Files]

; Program files -> Program Files (read-only location; user data lives in ProgramData)

Source: "..\{#AppExe}";        DestDir: "{app}";      Flags: ignoreversion

Source: "..\icon\icon.ico";    DestDir: "{app}\icon"; Flags: ignoreversion

Source: "..\README.md";        DestDir: "{app}";      Flags: ignoreversion

; Data folder: created on install so the app switches to it instead of writing

; into Program Files. Never overwritten, never removed by the uninstaller.

Source: "config.default.json"; DestDir: "{commonappdata}\{#DataFolder}"; Flags: onlyifdoesntexist uninsneveruninstall



[Icons]

Name: "{group}\{#AppShort}";                          Filename: "{app}\{#AppExe}"

Name: "{group}\{cm:UninstallProgram,{#AppShort}}";    Filename: "{uninstallexe}"

Name: "{autodesktop}\{#AppShort}";                    Filename: "{app}\{#AppExe}"; Tasks: desktopicon



[Run]

; Entry 1 has no `postinstall` flag, so it executes while Setup is still

; elevated - required, because schtasks needs admin to set /RL HIGHEST.

; Task name == the app's own name, so the in-app "开机自启" toggle recognises it.

Filename: "{sys}\schtasks.exe"; Parameters: "/Create /TN ""{#AppTaskName}"" /TR ""\""{app}\{#AppExe}\"" --minimized"" /SC ONLOGON /RL HIGHEST /F"; Flags: runhidden; Tasks: startup

; Entry 2 is the Finish-page checkbox. `shellexec` is REQUIRED here: postinstall

; entries run de-elevated while the app demands elevation (else: code 740).

Filename: "{app}\{#AppExe}"; Description: "立即运行 {#AppName}"; Flags: nowait postinstall skipifsilent shellexec



[UninstallRun]

Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#AppTaskName}"" /F"; Flags: runhidden; RunOnceId: "DelAutoStartTask"



; NOTE - there is deliberately NO [UninstallDelete] section.

; {app}\logs and {app}\backup used to be deleted on uninstall, which is

; dangerous: if Setup is ever pointed at (or over) a portable copy - that

; already happened once during testing - the uninstaller would wipe the user's

; real restore data. In installed mode all user data lives in

; {commonappdata}\Fluxion anyway, so there is nothing of ours left in

; {app}: the uninstaller removes the files it installed by itself.



[Code]

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);

var

  DataDir: String;

begin

  if CurUninstallStep = usPostUninstall then

  begin

    DataDir := ExpandConstant('{commonappdata}\{#DataFolder}');

    if DirExists(DataDir) then

      MsgBox('配置、日志与备份都已保留在：' + #13#10 + DataDir + #13#10 + #13#10 +

             '如需彻底清理，请手动删除该目录。', mbInformation, MB_OK);

  end;

end;

