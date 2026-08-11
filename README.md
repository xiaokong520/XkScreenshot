# XkScreenshot

Windows 截图工具，基于 .NET 8 + WPF。除了框选、标注、贴图这些常规功能，还支持长截图、
文字识别和翻译。识别和翻译都可以离线运行，模型在程序内按需下载。

程序常驻托盘，默认 F1 截图，F3 把剪贴板里的内容钉到屏幕上。

## 功能

### 截图与标注

框选区域，或单击选中整个窗口，按 Tab 可切换到控件级检测（按界面元素取范围）。

标注工具有矩形、椭圆、箭头、画笔、文字、马赛克，支持撤销和重做。截图时光标处带放大镜和
取色器，按 C 复制颜色值，按 Shift 在 RGB 和 HEX 之间切换。

多屏是一屏一个覆盖层，各自在自己的 DPI 上下文里渲染。

### 长截图

选好区域后按 L 进入长截图。自动模式由程序发送滚轮消息、等画面稳定后抓帧；手动模式由用户
自己滚动，程序持续抓帧。帧之间通过图像匹配求出垂直位移后拼接，能识别吸顶和吸底栏。
最大高度可在设置里调整。

### 文字识别

离线引擎用 PP-OCRv5 mobile 模型，通过 RapidOcrNet 跑 ONNX Runtime。默认模型能识别汉字
（简繁）、日文假名、拉丁字母和数字。韩语、西里尔、阿拉伯、天城文、希腊、泰语、泰米尔、
泰卢固另有可选语言包，每个 7~13 MB。

不需要手动选择模型：默认模型先跑一遍，平均置信度不够时再用已安装的语言包各试一次，取最优
结果。也可以在设置里改成调用在线大模型，支持 OpenAI 和 Anthropic 两种协议。

### 翻译

离线引擎用 Bergamot（Firefox 的翻译引擎），覆盖 55 个语种与英语的互译，非英语语种之间通过
英语中转两跳。每个方向的模型 12~60 MB。

源语种按文字系统自动判定，目标语种在识别结果窗口里选择。同样支持切换到在线大模型。

### 贴图

把选区钉在屏幕上，可以拖动、滚轮缩放、Ctrl+滚轮调透明度、Del 关闭，右键菜单可复制和另存。

按 F3 钉的是剪贴板里的内容，支持图片、单个图片文件、以及文本（会排版成一张图）。剪贴板里
没有可贴的内容时，钉上一次截的那张图。

### 截屏历史

覆盖层里按 `,` 往更早翻、`.` 往更近翻，翻回来的是当时那一整屏画面，选区也一并恢复，可以
在上面重新框选和标注。数字键可以直接跳到第几条，0 回到实时画面。默认保留 30 条，
可在设置里修改，设为 0 表示关闭。

## 系统要求

- Windows 10 1809 及以上，x64
- [.NET 桌面运行时 8.0（x64）](https://dotnet.microsoft.com/download/dotnet/8.0)

安装包会检测运行时，缺少时会提示并打开下载页。

## 安装

运行 `installer\XkScreenshot.iss` 编译出的安装包。默认装到
`%LocalAppData%\Programs\XkScreenshot`，单用户安装，不需要管理员权限。

安装位置选在用户目录下，是因为模型是运行期下载到程序目录的 `models\` 里的，装进
Program Files 会导致下载失败。

也可以直接使用 `dotnet publish` 的输出目录，解压即用，没有额外的注册项。

## 快捷键

全局热键有两个：

| 键 | 作用 |
|---|---|
| F1 | 开始截图 |
| F3 | 把剪贴板内容钉到屏幕上 |

两个热键都可以在设置里改。在设置的热键输入框里按 Delete 可以清除该项，清除后这个功能就
没有全局热键了。

截图覆盖层内的快捷键（按 H 可以收起屏幕上的提示面板）：

| 键 | 作用 |
|---|---|
| 拖拽 | 框选区域 |
| 单击 | 选中整个窗口 |
| Tab | 切换整窗 / 控件级检测 |
| W A S D | 光标移动 1 像素 |
| ← ↑ ↓ → | 移动选区 / 选中的标注 |
| L | 长截图 |
| 滚轮 | 调粗细 / 字号 / 马赛克粒度 |
| Delete | 删除选中的标注 |
| Ctrl + A | 选中整屏 / 整个桌面 |
| `,` `.` | 回溯截屏历史：画面 + 选区 |
| 数字键 | 跳到第几条历史，0 回到当前 |
| C | 复制颜色值 |
| Shift | 切换 RGB / HEX |
| Enter | 确认截图，双击选区亦可 |
| Esc | 逐级返回：标注 → 工具 → 重选 |
| H | 隐藏提示面板 |

## 构建

需要 .NET 8 SDK。用 Visual Studio 打开需要「.NET 桌面开发」工作负载，命令行构建则不需要。

```powershell
dotnet build XkScreenshot.sln -c Debug
dotnet run --project src\XkScreenshot.App
```

发布（框架依赖，输出约 50 MB）：

```powershell
dotnet publish src\XkScreenshot.App\XkScreenshot.App.csproj -c Release -r win-x64 --self-contained false -o publish
```

打安装包需要 [Inno Setup 6](https://jrsoftware.org/isdl.php)，先执行上面的 publish：

```powershell
ISCC.exe installer\XkScreenshot.iss
```

产物在 `installer\out\`。Inno Setup 自带的语言文件里没有中文，脚本引用的
`ChineseSimplified.isl` 需要从 [issrc 仓库的 Unofficial 目录](https://github.com/jrsoftware/issrc/tree/main/Files/Languages/Unofficial)
下载后放进 Inno Setup 的 `Languages\` 目录。

仓库里的 `NuGet.config` 不能删，它在仓库内声明了 nuget.org 源，机器上全局 NuGet 配置为空时
也能正常还原。网络慢的话可以把里面的地址换成镜像。

## 项目结构

| 项目 | 职责 |
|---|---|
| `XkScreenshot.Core` | Win32 互操作、显示器/窗口枚举、几何类型、全局热键、LLM 客户端 |
| `XkScreenshot.Capture` | 抓屏后端与冻屏快照 |
| `XkScreenshot.Annotate` | 标注文档模型、各类形状、撤销/重做、马赛克 |
| `XkScreenshot.Scroll` | 滚动驱动、帧比对、拼接、预览条 |
| `XkScreenshot.Ocr` | `IOcrEngine` 抽象、离线 PaddleOCR 引擎、在线 LLM 引擎、语言包 |
| `XkScreenshot.Translate` | `ITranslator` 抽象、Bergamot 离线引擎、在线 LLM 引擎、缓存 |
| `XkScreenshot.Pin` | 贴图窗口与生命周期管理 |
| `XkScreenshot.App` | WPF 宿主、托盘、覆盖层交互、设置界面、输出（剪贴板/文件） |

有三条约定贯穿全部代码：

1. 坐标系只有一个：虚拟屏幕物理像素。几何运算一律用 `PixelRect` / `PixelPoint`，原点是虚拟
   桌面左上角（可能为负）。DIP 换算只出现在 `OverlayWindow.ToLocalDip` 一处。
2. DPI 感知在 `app.manifest` 里声明，不在代码里设置。WPF 在启动早期就读走了进程的 DPI 感知
   模式，运行期再调 API 已经晚了。
3. 多屏是多个窗口，不是一个大窗口。横跨整个虚拟桌面的单一窗口在混合 DPI 下会出错。

## 数据存放位置

```
%APPDATA%\XkScreenshot\settings.json     设置
%APPDATA%\XkScreenshot\history\          截屏历史（index.json + 整屏 PNG，目录可改）
<程序目录>\models\                        离线模型（目录可改）
    paddleocr\                           识别模型与语言包
    bergamot\{源}-{目标}\                 翻译模型
```

开机自启是个例外，它的实际状态存在 `HKCU\...\Run` 里，程序启动时以注册表为准回填设置。

## 已知限制

- 抓屏只有 GDI 后端，受 DRM 保护的窗口会截出纯黑，目前不会提示用户。
- 控件级检测依赖目标进程的 UIA 提供者。管理员权限的窗口（除非本程序也提权）、以及尚未启用
  无障碍树的 Chromium/Electron 应用，都会退回整窗选取。
- 混合 DPI（如主屏 150% + 副屏 100%）的换算路径尚未在真机上验证。
- 翻译没有中日这类直连，非英语语种之间走英语两跳。

## 第三方组件

| 组件 | 用途 |
|---|---|
| [RapidOcrNet](https://github.com/BobLd/RapidOcrNet)（Apache-2.0）、[ONNX Runtime](https://onnxruntime.ai)（MIT） | 离线文字识别 |
| [PP-OCRv5](https://huggingface.co/PaddlePaddle) 模型 | 识别模型与语言包，取自 PaddlePaddle 官方组织 |
| [BergamotTranslatorSharp](https://github.com/Freeesia/BergamotTranslatorSharp)（MPL-2.0） | 离线翻译引擎 |
| [Bergamot / Firefox Translations](https://github.com/mozilla/translations) 模型 | 翻译模型 |
| [Lucide](https://lucide.dev)（MIT） | 界面图标，以矢量路径内嵌在 `Ui/Icons.cs` |
| [Inno Setup](https://jrsoftware.org/isinfo.php) | 打安装包 |

