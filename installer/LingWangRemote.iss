; OpenRemoteDesk Windows 电脑端 安装脚本 (Inno Setup 7)
; 设计要点：管理员安装；文件落 C:\Program Files\LingWangRemote；
;           配置/日志落 C:\ProgramData\LingWangRemote；调用 PS 编排脚本完成服务/驱动/防火墙；
;           卸载默认保留驱动、证书、配对数据（安全优先）。
; 注意：本脚本引用的 ps 编排脚本在 ..\build\staging\ps 暂存区（含构建时注入的本机私密变量），
;       请用 build\make-installer.ps1 一键构建，不要直接用 Inno IDE 打开本文件编译。

#define MyAppName "OpenRemoteDesk"
#define MyAppVersion "0.1.4"
#define MyAppPublisher "LingWang"
#define MyAppExeName "LingWangTray.exe"
#define PayloadRoot "..\payload"

[Setup]
AppId={{8F2A6B71-9C4D-4E3A-B5A1-7D2E9F6C3B01}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\LingWangRemote
DefaultGroupName=OpenRemoteDesk
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=OpenRemoteDesk-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
WizardImageFile=assets\wizard_large.bmp
WizardSmallImageFile=assets\wizard_small.bmp
SetupIconFile=assets\app.ico
UninstallDisplayIcon={app}\tray\{#MyAppExeName}
SetupLogging=yes
ShowLanguageDialog=yes
LanguageDetectionMethod=uilanguage

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
; 临时编排脚本：stop 在装文件前调用；post 在装完后调用（dontcopy 不直接落地）
; 来自 build\make-installer.ps1 生成的暂存区（占位符已替换为真值）
Source: "..\build\staging\ps\stop-services.ps1"; DestDir: "{tmp}"; Flags: dontcopy
Source: "..\build\staging\ps\install-post.ps1"; DestDir: "{tmp}"; Flags: dontcopy
; 卸载脚本随程序落地，卸载时仍可用
Source: "..\build\staging\ps\uninstall.ps1"; DestDir: "{app}\installer"; Flags: ignoreversion

; 主体货舱
Source: "{#PayloadRoot}\sunshine\*"; DestDir: "{app}\sunshine"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#PayloadRoot}\easytier\*"; DestDir: "{app}\easytier"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#PayloadRoot}\nssm\*"; DestDir: "{app}\nssm"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#PayloadRoot}\streamsvc\*"; DestDir: "{app}\streamsvc"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#PayloadRoot}\tray\*"; DestDir: "{app}\tray"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#PayloadRoot}\cert\*"; DestDir: "{app}\cert"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#PayloadRoot}\watchdog\*"; DestDir: "{app}\watchdog"; Flags: recursesubdirs createallsubdirs ignoreversion
Source: "{#PayloadRoot}\config-template\*"; DestDir: "{app}\config-template"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\OpenRemoteDesk（托盘）"; Filename: "{app}\tray\{#MyAppExeName}"
Name: "{group}\卸载OpenRemoteDesk"; Filename: "{uninstallexe}"

[UninstallRun]
Filename: "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\installer\uninstall.ps1"" -AppDir ""{app}"""; \
  Flags: runhidden waituntilterminated; RunOnceId: "LingWangUninstallTeardown"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  ExtractTemporaryFile('stop-services.ps1');
  Exec('C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe',
       '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\stop-services.ps1') + '"',
       '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // 即使清理脚本返回非 0 也继续安装（后续步骤自带服务新建/改指向能力）
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
  Cmd: String;
begin
  if CurStep = ssPostInstall then begin
    ExtractTemporaryFile('install-post.ps1');
    Cmd := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\install-post.ps1') +
           '" -AppDir "' + ExpandConstant('{app}') + '"';
    Exec('C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe', Cmd,
         '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    if ResultCode <> 0 then
      MsgBox('安装后配置步骤返回错误码 ' + IntToStr(ResultCode) + #13#10 +
             '详情请看 C:\ProgramData\LingWangRemote\logs\installer.log',
             mbError, MB_OK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // usUninstall 时 {app} 仍在，卸载脚本可用；文件在此之后才被删除
end;
