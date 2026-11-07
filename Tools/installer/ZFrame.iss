; ZFrame Windows 安装包脚本（Inno Setup 6）
; 由 Tools/build_installer.ps1 注入 MyAppVersion，不要直接编译本文件

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0.0"
#endif

#define MyAppName "ZFrame"
#define MyAppPublisher "ZFrame"
#define MyAppExeName "ZFrame.exe"

[Setup]
AppId={{8F3C2A11-4D7E-4A6B-9C1D-7E2F5B8A3C60}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=..\..\Build\Installer
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#MyAppExeName}
; 默认装到用户目录（{autopf} 在 lowest 下 = %LOCALAPPDATA%\Programs），不需要 UAC；
; 用户仍可在向导里选择「为所有人安装」（会提权到 Program Files）
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
MinVersion=10.0

[Languages]
; 注：Inno Setup 6.7.3 未自带简体中文语言包（Languages 目录无 ChineseSimplified.isl）。
; 如需中文安装界面，把官方 ChineseSimplified.isl 放进 Inno 的 Languages 目录后取消下一行注释。
Name: "en"; MessagesFile: "compiler:Default.isl"
; Name: "zh"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; 排除 Unity 的 Burst 调试信息目录（不随包分发）
; 由 build_installer.ps1 先把 Windows64 复制到 staging 并物理排除 Burst 调试目录，
; 这里只对 staging 做无排除的全量收录（Inno 的 Excludes 对递归子目录不可靠）
Source: "..\..\Build\Installer\staging\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent
