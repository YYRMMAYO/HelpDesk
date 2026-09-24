; HelpDesk 安装包脚本（Inno Setup 6）
;
; 编译（版本号由外部传入，避免脚本里再维护一份）：
;   "F:\YA\Inno Setup 6\ISCC.exe" /DAppVersion=1.1.0 installer\HelpDesk.iss
; 或直接跑封装好的脚本：tools\build-release.ps1
;
; 设计取舍：
;  1) PrivilegesRequired=lowest —— 默认「只为我安装」，装到 %LOCALAPPDATA%\Programs，
;     全程不弹 UAC。目标用户里不少人的账户没有管理员权限，装不上才是最大的问题。
;     需要给全机器装的人可以在向导里选「为所有用户安装」，那时才会要管理员权限。
;     这与程序本身的设计一致：主程序以 asInvoker 运行，只在卸载软件这类具体操作上临时提权。
;  2) AppId 固定不变 —— 升级时覆盖安装而不是并列两份。
;  3) 不写任何本机绝对路径 —— 相对路径基于本 .iss 所在目录，换机器照样能编译。
;  4) Inno 自带语言包里没有简体中文，所以以 Default.isl 打底，再用 [Messages] 覆盖
;     用户实际会看到的那些提示，这样不用额外下载社区翻译文件，行为完全可控。

#define AppName "HelpDesk 电脑助手"
#define AppShortName "HelpDesk"
#define AppPublisher "YYRMMAYO"
#define AppExeName "HelpDesk.exe"
#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif

[Setup]
AppId={{8F2B6E1A-3C4D-4E5F-9A7B-1C2D3E4F5A6B}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppShortName}
DefaultGroupName={#AppName}
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=HelpDesk-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

[Languages]
Name: "default"; MessagesFile: "compiler:Default.isl"

[Messages]
; —— 只覆盖用户一定看得见的文案，其余保持 Default.isl 原样 ——
SetupAppTitle=安装程序
SetupWindowTitle=安装 {#AppName}
WelcomeLabel1=欢迎安装 [name]
WelcomeLabel2=这会把 [name/ver] 安装到你的电脑上。%n%n安装完成后可以直接从开始菜单启动，之后需要什么功能（网速检测、软件卸载等）在「插件市场」里按需安装即可。%n%n点「下一步」继续。
SelectDirLabel3=安装程序会把 [name] 装到下面的文件夹。
SelectDirBrowseLabel=点「下一步」继续；想换位置就点「浏览」。
SelectTasksLabel2=选择要执行的其他任务，然后点「下一步」：
SelectStartMenuFolderLabel3=安装程序会在下面的文件夹里创建开始菜单快捷方式。
SelectStartMenuFolderBrowseLabel=点「下一步」继续；想换位置就点「浏览」。
ReadyLabel1=安装程序已经准备好，可以开始安装 [name] 了。
ReadyLabel2a=点「安装」开始，或者点「上一步」回去修改设置。
PreparingDesc=安装程序正在准备安装 [name]，稍等一下。
InstallingLabel=正在安装 [name]，请稍等。
FinishedLabel=安装完成，[name] 已经装好了。%n%n点「完成」关闭安装程序。
FinishedLabelNoIcons=[name] 已经装好了。%n%n点「完成」关闭安装程序。
ClickFinish=点「完成」关闭安装程序。
ButtonNext=下一步(&N) >
ButtonBack=< 上一步(&B)
ButtonInstall=安装(&I)
ButtonFinish=完成(&F)
ButtonCancel=取消
ButtonBrowse=浏览(&R)...
ButtonYes=是(&Y)
ButtonNo=否(&N)
ExitSetupTitle=退出安装
ExitSetupMessage=安装还没有完成。确定要退出吗？%n%n以后想继续安装，重新运行这个安装包即可。
ConfirmUninstall=确定要卸载 %1 吗？
UninstallStatusLabel=正在卸载 %1，请稍等。
UninstalledAll=%1 已经卸载完成。
ErrorExecutingProgram=无法运行：%1

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "其他任务："; Flags: unchecked

[Files]
; 自包含发布产物：用户不需要另外安装 .NET 运行时
Source: "..\artifacts\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "立即运行 {#AppName}"; Flags: nowait postinstall skipifsilent
