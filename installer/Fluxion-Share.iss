; ============================================================================
;  Fluxion 分享版 - Inno Setup 6 installer script
;  Build:  ISCC.exe installer\Fluxion-Share.iss
;  OUTPUT: <project>\installer_out\Fluxion-Share-Setup-1.0.0.exe
;
;  NOTE 1 - ENCODING: UTF-8 **with BOM** (Chinese literals break without it).
;  NOTE 2 - Packages the /define:SHARE build (dist_share\Fluxion.exe).
;           The share build runs ITS OWN version line (1.0.0) - it does not
;           follow the author's private 3.x numbering.
;  NOTE 3 - Plugins are bundled: the frame-generation runtimes that the app
;           would otherwise download. They are placed in the DATA directory
;           ({commonappdata}\Fluxion), because the app switches to that
;           folder once installed under Program Files (read-only).
;           Bundled: dlssg\source, dlssg030-pack, xess-pack\zzz
;           Not bundled: xess-pack\wuwa (~330 MB) and every _removed/_backup copy.
;  NOTE 4 - Own AppId, so it coexists with the author's full build.
;  NOTE 5 - No scheduled task / autostart: the trimmed UI has no switch to turn
;           one off again, so the installer must not create one.
;  NOTE 6 - No config.json / config.default.json: the app writes its own
;           defaults; a friend's machine must never inherit the author's config.
; ============================================================================

#define AppName      "Fluxion 分享版"
#define AppShort     "Fluxion 分享版"
#define AppVersion   "1.0.0"
#define AppPublisher "Fluxion 分享版"
#define AppExe       "Fluxion.exe"
#define AppMutex     "Fluxion_SingleInstance"
#define DataFolder   "Fluxion"

[Setup]
AppId={{8B4D2F75-3E6C-4A81-B5D2-9C7E4A1B03F6}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppComments=游戏帧生成助手（分享版）：DX12 启动 + 运行模式检测 + DLSS/FSR 帧生成，插件已内置
DefaultDirName={autopf}\Fluxion-Share
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\installer_out
OutputBaseFilename=Fluxion-Share-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\icon\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} {#AppVersion}
AppMutex={#AppMutex}
CloseApplications=yes
RestartApplications=no
VersionInfoVersion=1.0.0.0
VersionInfoDescription={#AppName} 安装程序
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}

[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加任务："

[Files]
Source: "..\dist_share\{#AppExe}";  DestDir: "{app}";      Flags: ignoreversion
Source: "..\dist_share\README.txt"; DestDir: "{app}";      Flags: ignoreversion
Source: "..\icon\icon.ico";         DestDir: "{app}\icon"; Flags: ignoreversion

; ---- 内置插件（帧生成运行时）----
; 装到数据目录而不是 {app}：安装后数据目录会切到 %ProgramData%（{app} 只读）。
Source: "..\share_out\stage\Fluxion\dlssg\source\*";        DestDir: "{commonappdata}\{#DataFolder}\dlssg\source";  Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\share_out\stage\Fluxion\dlssg030-pack\common\*"; DestDir: "{commonappdata}\{#DataFolder}\dlssg030-pack\common"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\share_out\stage\Fluxion\dlssg030-pack\alts\*";   DestDir: "{commonappdata}\{#DataFolder}\dlssg030-pack\alts";   Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\share_out\stage\Fluxion\xess-pack\zzz\*";        DestDir: "{commonappdata}\{#DataFolder}\xess-pack\zzz";        Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppShort}";                       Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppShort}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppShort}";                 Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; `shellexec` is REQUIRED: the exe carries a requireAdministrator manifest, and
; postinstall [Run] entries execute de-elevated (otherwise: error 740).
Filename: "{app}\{#AppExe}"; Description: "立即运行 {#AppName}"; Flags: nowait postinstall skipifsilent shellexec

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{commonappdata}\{#DataFolder}');
    if DirExists(DataDir) then
      MsgBox('程序已卸载。配置与日志仍保留在：' + #13#10 + DataDir + #13#10 + #13#10 +
             '如需彻底清理，请手动删除该目录。', mbInformation, MB_OK);
  end;
end;
