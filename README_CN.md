# Shine Yes Tool

局域网 TCP 输入工具。Server 和 Client 是独立项目，均使用 .NET 9；Client 使用本机 Godot 4.7.2 Mono。Server 是纯 Console，无 Avalonia、窗口或托盘菜单，全部配置在 exe 同目录的 `config.json`。

## 构建和启动

在项目根目录打开 PowerShell：

```powershell
dotnet build Server/Server.csproj
dotnet run --project Server/Server.csproj -- --list-windows
```

默认配置 `UseForegroundWindow=true`，直接操作 Server 所在电脑当前前台窗口中已聚焦的输入框，不需要填写目标路径和标题。先启动 Server，再切到目标应用并点击输入框。

需要指定目标时，将 `UseForegroundWindow` 设为 `false`，并将窗口列表输出中的 `ProcessPath` 和 `Title` 填入 `Server/config.json` 的 `TargetProcessPath` 与 `TargetWindowTitle`。JSON 路径中的反斜杠需写成 `\\`。启动命令：

```powershell
dotnet run --project Server/Server.csproj
```

开发运行读取 `Server/bin/Debug/net9.0/config.json`；首次构建从源配置复制，后续默认保留较新的输出配置。建议正式使用发布目录中的配置，修改后重启 Server。

```powershell
dotnet publish Server/Server.csproj -c Release --self-contained false -o publish/Server
./publish/Server/Server.exe --check-config
./publish/Server/Server.exe
```

框架依赖发布需要 .NET 9 运行时。若需要不安装运行时的发布，使用 `-r win-x64 --self-contained true`，该操作可能需要联网下载运行时包。

使用 Godot 4.7.2 Mono 导入 `Client/project.godot`，构建 C# 后运行主场景。输入 Server 的局域网 IP 和端口，点击连接。

命令行构建 Client 可使用下面的脚本。NuGet.Config 指向本机 Godot 自带的包；换电脑后修改该路径。

```powershell
./build-client.ps1
```

## config.json

| 配置 | 含义 |
| --- | --- |
| ListenAddress | 默认 `0.0.0.0`，监听全部 IPv4 网卡；可改成具体局域网 IP |
| Port | 默认 39827，范围 1–65535 |
| UseForegroundWindow | `true`：使用当前前台窗口，不激活其他窗口，忽略路径和标题；`false`：查找并激活指定目标。新配置默认 true，旧配置缺少此字段时维持 false |
| TargetProcessPath | 指定目标模式下必填的外部应用可执行文件完整路径 |
| TargetWindowTitle | 窗口标题；优先精确匹配，路径仅对应一个窗口时允许标题变化 |
| InputDelayMs | 激活窗口后的等待时间，默认 150，范围 50–3000 毫秒 |
| MaxTextLength | 默认 16000，允许范围 1–16000，按 UTF-16 长度计算 |
| LogLevel | `Debug`（默认）、`Info`、`Warning`、`Error`，记录该级别及以上日志 |
| LogMaxSizeMB | 当前日志上限，默认 1024 MiB（1 GiB），最小 1 MiB |

Server 不启动目标应用。多窗口无法唯一匹配时返回错误。配置修改需要重启。Windows 防火墙如拦截连接，需要允许该 Server 的 TCP 端口通过专用网络。

当前前台窗口在每条指令实际开始执行时确定（多个 Client 排队时不是接收时确定），执行过程中窗口失焦则停止，不会把剩余文字转发到新窗口。当前窗口模式也要求输入框已获得键盘焦点；不会自动寻找输入框。同机测试时 Client 窗口会成为前台，建议使用另一台设备上的 Client 测试此模式。

## 输入行为

- **发送并回车**：在目标应用现有焦点位置追加文字，最后按 Enter。
- **只输入文字**：追加文字，不按最后的 Enter。
- **按回车**：发送 Enter。
- **撤回（清空目标输入框）**：发送 Ctrl+A、Backspace，清空尚在输入框中的文字。
- 消息内部换行使用 Shift+Enter；具体应用可能定义不同的快捷键。

当前实现通过 Windows Unicode 键盘事件发送中文、emoji 等文字，不修改剪贴板。目标应用必须支持系统键盘输入。收到成功表示系统输入指令已完成，不表示目标应用已向远端发送消息。

目标输入框需要用户先点选；此版本保留应用自身的输入焦点，**尚未实现特定应用的自动输入框定位**。在目标应用中切换到其他控件后，请重新点击输入框。清空依赖该输入框对 Ctrl+A 的处理方式。目标以管理员身份运行时，普通权限 Server 可能无法输入，需要两者权限一致。执行中窗口失焦会停止后续输入，但此前输入可能已生效。

默认无密钥、无加密，局域网内能访问端口的设备均可操作目标。连接断开或超时时 Client 不自动重发，以避免重复发送消息；先检查目标应用再重试。多客户端指令串行执行。

Console 固定显示可用网卡的 IPv4 地址和 TCP 端口，多网卡时选择 Client 可达的局域网地址；`0.0.0.0` 是监听地址，不能填作远端 IP。控制台只显示 Error，并在错误增加时重绘固定地址区，较早错误从显示区移出，完整记录留在文件。标准输出重定向到文件时不使用固定布局。

文件日志固定为 exe 同目录的 `server.log` 和 `server.history.log`，不按日期新增文件。每次写入前检查 UTF-8 字节大小：将超过 `LogMaxSizeMB` 时，把当前日志复制到历史文件并覆盖原有历史，再清空当前文件继续写入；复制失败保留当前文件并报告错误。默认单文件上限 1 GiB，两个文件合计约 2 GiB。旧版本 `logs` 目录中的历史文件保留，不再写入；`LogDirectory`、`LogRetentionDays` 已废弃。

Debug 包含连接编号、客户端地址、请求 ID、指令、文字长度/哈希、排队与执行耗时、目标窗口句柄/进程 ID/标题及响应发送；Error 包含异常堆栈。不记录消息正文。相同文字哈希配合不同请求 ID 表示重复发起的独立请求，不代表 Server 自动重试。Client 的 Godot 输出也会打印请求 ID。

同机当前窗口模式测试时，Server 生成的 Enter 可能落在 Client 内；操作按钮已禁止获得键盘焦点，避免回车再次激活"发送"产生循环。此模式的目标仍然是当前前台窗口，不会自动转向另一应用；实际远程使用请在 Server 电脑聚焦目标输入框。

## 后台运行

纯 Console 无托盘。Windows 可通过任务计划程序在当前已登录用户的交互式会话中启动，或在 PowerShell 使用 `Start-Process -FilePath <Server.exe完整路径> -WindowStyle Hidden`。不要作为 Session 0 系统服务运行，系统服务无法控制当前桌面的输入框。隐藏启动后通过结束进程停止；Console 前台运行使用 Ctrl+C。该项目不会自动建立开机启动任务。

## 跨平台边界

Server 的 TCP 和配置使用跨平台 .NET 9，Godot Client 未使用 Windows 专属 API。第一版仅完成 Windows 外部应用输入；macOS/Linux 的输入接口尚未实现，控制请求会明确返回不支持。后续可在 `IInputController` 下增加 macOS 辅助功能权限和 Linux X11/Wayland 适配。其他平台尚未实机测试，Godot 导出需要对应平台导出模板。

## 验证

TCP 协议为 UTF-8 JSON，每条消息以换行分隔，最大帧 128 KiB。例如：

```json
{"id":"1","action":"send","text":"你好"}
{"id":"2","action":"clear"}
{"id":"3","action":"enter"}
```

`action` 支持 `send`、`type`、`enter`、`clear`、`ping`。响应为 `{"id":"1","ok":true,"message":"指令已执行"}`；失败时 `ok` 为 `false` 并附带原因。`id` 必填、长度不超过 64；同连接按顺序响应。不提供超时自动重发或请求去重。

```powershell
dotnet run --project Tests/Tests.csproj
```

测试使用模拟输入，不会操作桌面，覆盖 TCP 拆包/粘包、Unicode、多客户端串行、错误请求、文字/帧长度、输入失败、编码和关闭。实机验收：在记事本等测试窗口点选输入框，配置路径和标题，使用 Client 验证中文、emoji、多行、追加、Enter、清空，以及目标关闭和多个窗口的错误反馈。具体目标应用的定位和行为需收到应用信息后另行适配、验收。