; ═══════════════════════════════════════════════════════════════════════════
;  VideoTurner 安装包脚本（Inno Setup 6.7.4）
;
;  构建方式（推荐）：
;      powershell -ExecutionPolicy Bypass -File tools\make-installer.ps1
;  手动编译：
;      "tools\InnoSetup6\ISCC.exe" "installer\VideoTurner.iss"
;
;  产物：dist\installer\VideoTurner-1.0.0-win-x64-setup.exe
;
;  说明：
;   * 安装包内是 dotnet publish 出来的**自包含**程序（约 151 MB），
;     目标机器不需要预装 .NET 8 桌面运行时。
;   * 按用户安装（PrivilegesRequired=lowest）：装到 %LOCALAPPDATA%\Programs\VideoTurner，
;     全程不弹 UAC。程序本身也不需要管理员（除非要接管以管理员运行的程序）。
;   * 「开机自启」不在安装时写注册表，而是由程序自己的设置页管理（写 HKCU\...\Run），
;     避免安装器与程序里的开关状态打架；卸载时会顺手删掉那个 Run 值。
;   * 本文件必须存成 UTF-8 **带 BOM**，否则编译器会按 ANSI 读，中文界面文案变乱码。
; ═══════════════════════════════════════════════════════════════════════════

#define MyAppName "VideoTurner"
#define MyAppDisplayName "VideoTurner — 多显示器音频路由"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "VideoTurner"
#define MyAppExeName "VideoTurner.exe"
#define MyPublishDir "..\dist\publish\win-x64"

[Setup]
; AppId 是「同一个程序」的身份，升级安装靠它识别；一旦发布就不要再改。
AppId={{A193211E-A089-4C22-9DD1-AD232096BD80}
AppName={#MyAppDisplayName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppComments=中文界面，托盘常驻；基于 MIT 许可的 ScreenSound 改写
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
; 按用户安装，不需要管理员
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist\installer
OutputBaseFilename={#MyAppName}-{#MyAppVersion}-win-x64-setup
SetupIconFile=..\src\VideoTurner\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppDisplayName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; MIT 许可要求随二进制保留许可证原文：安装时展示，并复制到 {app}
LicenseFile=..\LICENSE-ScreenSound-MIT.txt
; 安装/卸载前若程序还在跑（常驻托盘，通常都还在），先把它结束掉再替换文件
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
VersionInfoDescription={#MyAppDisplayName} 安装程序
VersionInfoCompany={#MyAppPublisher}
VersionInfoCopyright=Copyright (c) 2026 VideoTurner；包含 Copyright (c) 2026 Omar Omran 的 MIT 许可代码（ScreenSound）

[Languages]
Name: "chs"; MessagesFile: "ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 自包含发布产物（含 WPF / .NET 运行时）
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE-ScreenSound-MIT.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppDisplayName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "{#MyAppDisplayName}"; Tasks: desktopicon

[Registry]
; 程序自己的「开机自启」开关写的就是这个值。安装时不创建，只在卸载时清掉，
; 免得卸载后每次登录都弹一个「找不到文件」。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; \
    ValueName: "{#MyAppName}"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; \
    Flags: nowait postinstall skipifsilent

[Code]
var
  RemoveUserData: Boolean;

// taskkill 的返回码：0 = 已结束至少一个进程，128 = 没找到，1 = 权限不足。
function KillAppIfRunning(): Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec(ExpandConstant('{cmd}'),
                 '/c taskkill /IM {#MyAppExeName} /F >nul 2>nul',
                 '', SW_HIDE, ewWaitUntilTerminated, ResultCode)
            and (ResultCode = 0);
  if Result then
    Sleep(1200);  // 给系统一点时间释放 exe / dll
end;

// 安装前：本程序常驻托盘，绝大多数情况下它正在运行；不结束就没法覆盖文件。
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  KillAppIfRunning();
end;

function InitializeUninstall(): Boolean;
begin
  Result := True;
  KillAppIfRunning();

  // 设置与日志在 %APPDATA%\VideoTurner。
  //  * 交互式卸载：问一句，默认「否」——保留，重装还能沿用当前的映射与清单。
  //  * 静默卸载（/VERYSILENT）：一律保留。静默场景下没人能回答，绝不能悄悄删用户数据；
  //    真要清干净就显式传 /DELETEUSERDATA=1。
  if UninstallSilent() then
    RemoveUserData := ExpandConstant('{param:DELETEUSERDATA|0}') = '1'
  else
    RemoveUserData :=
      MsgBox('是否同时删除本程序的设置与日志？' + #13#10#13#10 +
             ExpandConstant('{userappdata}\{#MyAppName}') + #13#10#13#10 +
             '选择「否」会保留它们（以后重装可沿用当前的显示器映射与应用清单）。',
             mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;

  // 删掉可能残留的开机自启项（安装器不写它，程序自己会写）
  RegDeleteValue(HKEY_CURRENT_USER,
    'Software\Microsoft\Windows\CurrentVersion\Run', '{#MyAppName}');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if (CurUninstallStep = usPostUninstall) and RemoveUserData then
    DelTree(ExpandConstant('{userappdata}\{#MyAppName}'), True, True, True);
end;
