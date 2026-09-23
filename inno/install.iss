#define MyAppName "智能键鼠"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "cat"
#define MyAppURL "https://www.example.com/"
#define MyAppExeName "智能键鼠.exe"

[Setup]
; 注意：不同软件务必不要复用相同AppId
AppId={{696AE626-5808-427A-A150-9E3316F2415F}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\inkm
UninstallDisplayIcon={app}\{#MyAppExeName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=D:d\cat\inno
OutputBaseFilename=inkm_version1.0.0
SetupIconFile=D:d\cat\inkm\desktop\mouse.ico
SolidCompression=yes
WizardStyle=modern dark

[Languages]
Name: "chinese"; MessagesFile: "compiler:Languages\Chinese.isl"
Name: "english"; MessagesFile: "compiler:Languages\English.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: checkablealone

[Files]
Source: "D:d\cat\inkm\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "D:d\cat\inkm\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ExecResult: Integer;
begin
  // usUninstall = 卸载流程正式开始前执行
  if CurUninstallStep = usUninstall then
  begin
    // =====================配置区=====================
    // 修改这里为你需要运行的exe名称
    Exec(ExpandConstant('{app}\quit.exe'),
         '',
         '',
         SW_HIDE,        // SW_HIDE = 静默后台运行，无窗口
         ewWaitUntilTerminated, // 等待程序执行完毕再继续卸载
         ExecResult);
    // 如果不想等待程序执行完毕，把上面 ewWaitUntilTerminated 改为 ewNoWait
  end;
end;
