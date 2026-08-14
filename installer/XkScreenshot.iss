; XkScreenshot 安装脚本（Inno Setup 6）
;
; 编译：ISCC.exe installer\XkScreenshot.iss
; 输入：..\publish\   —— 先跑 dotnet publish -c Release -r win-x64 --self-contained false -o publish
; 产物：installer\out\XkScreenshot-1.0.0-setup.exe
;
; 下面这几个 define 都可以在命令行上用 /D 覆盖，CI 就是这么一份脚本编出两个安装包的：
;
;   ISCC /DAppVersion=1.2.3 /DSourceDir=..\publish\fd installer\XkScreenshot.iss
;   ISCC /DAppVersion=1.2.3 /DSourceDir=..\publish\sc /DSelfContained /DOutputSuffix=-selfcontained ...
;
; 覆盖得靠 #ifndef 兜着 —— 直接写 #define 的话，命令行给的值会被脚本里这行原地盖掉。

#define AppName      "XkScreenshot"

; 和根目录 Directory.Build.props 里的 <Version> 保持一致：程序「关于」页显示的是那一份
#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

; 写进 exe 版本资源的那一份，只能是纯数字。AppVersion 带了 -beta 这类后缀时，
; CI 会把后缀去掉再从这里传进来。
#ifndef AppVersionNumeric
  #define AppVersionNumeric AppVersion
#endif

; 输出文件名的尾巴，用来区分自包含和框架依赖两个包
#ifndef OutputSuffix
  #define OutputSuffix ""
#endif

#define AppPublisher "小空"
#define AppExeName   "XkScreenshot.exe"

#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
; 自包含和框架依赖两个包共用这一个 AppId：它们是同一个程序，只是随包带不带运行时。
; 分开写的话，两个包会在「应用和功能」里各占一条，来回换一次就装出两份。
AppId={{7C4B9E52-3A61-4E8D-9F2C-1D5A8B0E6F34}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersionNumeric}

; 单用户安装，装到 %LocalAppData%\Programs\XkScreenshot。
;
; 两个原因，缺一不可：一是不弹 UAC；二是装完的目录用户自己能写 —— OCR 和翻译的
; 模型是运行期现下到程序目录下 models\ 里的（见 AppSettings.DefaultModelsDirectory），
; 截屏历史默认也写在程序目录下的 history\，而本程序以 asInvoker 运行，
; 装进 Program Files 的话这两样都会因为没权限全部失败。
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; 这是个常驻托盘的程序，装/卸之前得先让它退出，否则文件占用会让安装半途失败。
; 名字取自 App.xaml.cs 的 SingleInstanceMutexName（那边带的 Local\ 前缀就是默认命名空间）。
AppMutex=XkScreenshot.SingleInstance

OutputDir=out
OutputBaseFilename={#AppName}-{#AppVersion}-setup{#OutputSuffix}
SetupIconFile=..\src\XkScreenshot.App\Assets\XkScreenshot.ico
UninstallDisplayIcon={app}\{#AppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "chs"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 用户后来下载的模型不在安装清单里，卸载时不会自己走。留着就是几百兆的孤儿目录，
; 而且它们随时能重新下回来。
Type: filesandordirs; Name: "{app}\models"

; 截屏历史同理。这里存的是最近几十次截图的整屏画面，是为了「再截一次同一块地方」
; 攒的，不是用户存下来的图 —— 那些在他自己选的保存目录里，不在这儿。
Type: filesandordirs; Name: "{app}\history"

; 下面整段只在框架依赖的包里编进去。自包含的包把运行时一起带上了，
; 再去查机器上装没装 .NET 8 纯属误导 —— 没装也照样能跑。
#ifndef SelfContained

[Code]

const
  DownloadPage = 'https://dotnet.microsoft.com/download/dotnet/8.0';

{ 装没装 .NET 8 桌面运行时。

  按目录判断而不是查注册表：注册表那条路在 32/64 位重定向上容易看走眼，而
  dotnet\shared\ 下面摆的就是实打实能用的运行时。

  只认 8.x。更高的大版本不算数 —— 框架依赖程序默认的前滚策略只在同一个大版本
  里往上找，光装了 .NET 9 是跑不起来 net8.0 程序的。 }
function DesktopRuntime8Installed(): Boolean;
var
  Root: String;
  Rec: TFindRec;
begin
  Result := False;

  Root := ExpandConstant('{commonpf64}') + '\dotnet\shared\Microsoft.WindowsDesktop.App';
  if not DirExists(Root) then
    Root := ExpandConstant('{commonpf}') + '\dotnet\shared\Microsoft.WindowsDesktop.App';
  if not DirExists(Root) then
    Exit;

  if FindFirst(Root + '\*', Rec) then
  try
    repeat
      if ((Rec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Copy(Rec.Name, 1, 2) = '8.') then
      begin
        Result := True;
        Break;
      end;
    until not FindNext(Rec);
  finally
    FindClose(Rec);
  end;
end;

{ 缺运行时不拦着装完，只是提醒并把下载页打开。

  拦下来的话用户手里既没程序也没快捷方式，还得重跑一遍安装；装完再补运行时，
  桌面图标已经在那儿了，补完直接能用。 }
function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if DesktopRuntime8Installed() then
    Exit;

  if MsgBox(
      '没有检测到 .NET 8 桌面运行时（.NET Desktop Runtime 8.0，x64）。' + #13#10#13#10 +
      '{#AppName} 需要它才能启动。安装会照常进行，但装完之后要先补上运行时，程序才打得开。'
      + #13#10#13#10 + '现在打开下载页吗？',
      mbConfirmation, MB_YESNO) = IDYES then
  begin
    ShellExec('open', DownloadPage, '', '', SW_SHOW, ewNoWait, ErrorCode);
  end;
end;

#endif
