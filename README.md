# Shine Yes Tool

A LAN TCP input tool. Server and Client are independent projects, both using .NET 9; Client uses Godot 4.7.2 Mono. Server is a pure Console application with no Avalonia, window, or tray menu. All configuration is in `config.json` located in the same directory as the executable.

## Build and Launch

Open PowerShell in the project root directory:

```powershell
dotnet build Server/Server.csproj
dotnet run --project Server/Server.csproj -- --list-windows
```

Default configuration `UseForegroundWindow=true` directly operates the focused input field in the current foreground window on the Server's computer, without needing to specify target path and title. Start the Server first, then switch to the target application and click the input field.

To specify a target, set `UseForegroundWindow` to `false` and fill `TargetProcessPath` and `TargetWindowTitle` in `Server/config.json` with the `ProcessPath` and `Title` from the window list output. Backslashes in JSON paths must be written as `\\`. Launch command:

```powershell
dotnet run --project Server/Server.csproj
```

Development runs read from `Server/bin/Debug/net9.0/config.json`; the first build copies from the source configuration, and subsequent builds default to keeping the newer output configuration. It is recommended to use the configuration in the publish directory for production use, and restart the Server after modifications.

```powershell
dotnet publish Server/Server.csproj -c Release --self-contained false -o publish/Server
./publish/Server/Server.exe --check-config
./publish/Server/Server.exe
```

Framework-dependent publishing requires the .NET 9 runtime. For self-contained publishing without runtime installation, use `-r win-x64 --self-contained true`, which may require downloading runtime packages from the internet.

Import `Client/project.godot` using Godot 4.7.2 Mono, build C#, and run the main scene. Enter the Server's LAN IP and port, then click connect.

Use the following script to build the Client from the command line. NuGet.Config points to the packages provided by the local Godot installation; modify this path when switching computers.

```powershell
./build-client.ps1
```

## config.json

| Configuration | Description |
| --- | --- |
| ListenAddress | Default `0.0.0.0`, listens on all IPv4 network cards; can be changed to a specific LAN IP |
| Port | Default 39827, range 1–65535 |
| UseForegroundWindow | `true`: uses the current foreground window, does not activate other windows, ignores path and title; `false`: finds and activates the specified target. New configurations default to true, old configurations missing this field maintain false |
| TargetProcessPath | Full path to the external application executable, required in specified target mode |
| TargetWindowTitle | Window title; exact match takes priority, title variation allowed when path corresponds to only one window |
| InputDelayMs | Wait time after activating the window, default 150, range 50–3000 milliseconds |
| MaxTextLength | Default 16000, allowed range 1–16000, calculated by UTF-16 length |
| LogLevel | `Debug` (default), `Info`, `Warning`, `Error`, logs this level and above |
| LogMaxSizeMB | Current log size limit, default 1024 MiB (1 GiB), minimum 1 MiB |

The Server does not launch the target application. Returns an error when multiple windows cannot be uniquely matched. Configuration changes require a restart. If Windows Firewall blocks connections, allow the Server's TCP port through the private network.

The current foreground window is determined when each command actually begins execution (not when received when multiple Clients are queued). If the window loses focus during execution, it stops and will not forward remaining text to a new window. The current window mode also requires the input field to have keyboard focus; it will not automatically find the input field. When testing on the same machine, the Client window will become the foreground window, so it is recommended to test this mode using a Client on another device.

## Input Behavior

- **Send and Enter**: Appends text at the current focus position in the target application, then presses Enter.
- **Type Only**: Appends text without pressing Enter at the end.
- **Press Enter**: Sends Enter.
- **Recall (Clear Target Input)**: Sends Ctrl+A, Backspace to clear text in the input field.
- Use Shift+Enter for line breaks within messages; specific applications may define different shortcuts.

The current implementation sends Chinese, emoji, and other text via Windows Unicode keyboard events without modifying the clipboard. The target application must support system keyboard input. Receiving success indicates that the system input command has been completed, not that the target application has sent the message to the remote end.

The target input field must be selected by the user first; this version preserves the application's own input focus, **automatic input field positioning for specific applications has not yet been implemented**. After switching to other controls in the target application, please click the input field again. Clearing depends on the input field's handling of Ctrl+A. When the target runs as administrator, a normal permission Server may not be able to input; both need consistent permissions. If the window loses focus during execution, subsequent input will stop, but previous input may have already taken effect.

By default, there is no secret key or encryption. Any device on the LAN that can access the port can operate the target. The Client does not automatically resend when the connection is disconnected or times out to avoid duplicate messages; check the target application before retrying. Multi-client commands are executed serially.

The Console permanently displays the IPv4 addresses and TCP ports of available network cards. When there are multiple network cards, select the LAN address reachable by the Client; `0.0.0.0` is the listening address and cannot be used as the remote IP. The console only shows Errors, and redraws the fixed address area when errors increase. Earlier errors are moved out of the display area, with complete records kept in files. Fixed layout is not used when standard output is redirected to a file.

File logs are fixed as `server.log` and `server.history.log` in the same directory as the executable, without creating new files by date. Before each write, the UTF-8 byte size is checked: when exceeding `LogMaxSizeMB`, the current log is copied to the history file, overwriting the original history, then the current file is cleared and writing continues; if copying fails, the current file is retained and an error is reported. The default single file limit is 1 GiB, with both files totaling approximately 2 GiB. Historical files in the `logs` directory from older versions are retained but no longer written to; `LogDirectory` and `LogRetentionDays` are deprecated.

Debug includes connection number, client address, request ID, command, text length/hash, queuing and execution time, target window handle/process ID/title, and response sending; Error includes exception stack traces. Message bodies are not recorded. The same text hash with different request IDs indicates separate requests initiated repeatedly, not that the Server automatically retries. The Client's Godot output also prints request IDs.

When testing the current window mode on the same machine, Enter generated by the Server may fall into the Client; operation buttons are prohibited from receiving keyboard focus to prevent Enter from reactivating "Send" and creating a loop. The target of this mode is still the current foreground window and will not automatically switch to another application; for actual remote use, please focus the target input field on the Server computer.

## Background Running

Pure Console without tray. On Windows, it can be launched in the current logged-in user's interactive session via Task Scheduler, or in PowerShell using `Start-Process -FilePath <full path to Server.exe> -WindowStyle Hidden`. Do not run as a Session 0 system service, as system services cannot control the input fields on the current desktop. Stop by ending the process after hidden launch; use Ctrl+C for Console foreground running. This project does not automatically establish startup tasks.

## Cross-Platform Boundaries

The Server's TCP and configuration use cross-platform .NET 9, and the Godot Client does not use Windows-exclusive APIs. The first version only completes Windows external application input; macOS/Linux input interfaces have not yet been implemented, and control requests will explicitly return unsupported. In the future, macOS accessibility permissions and Linux X11/Wayland adaptations can be added under `IInputController`. Other platforms have not been tested on real machines; Godot export requires corresponding platform export templates.

## Verification

The TCP protocol is UTF-8 JSON, with each message separated by a newline, and a maximum frame size of 128 KiB. For example:

```json
{"id":"1","action":"send","text":"Hello"}
{"id":"2","action":"clear"}
{"id":"3","action":"enter"}
```

`action` supports `send`, `type`, `enter`, `clear`, `ping`. Response is `{"id":"1","ok":true,"message":"Command executed"}`; on failure, `ok` is `false` with a reason. `id` is required and must not exceed 64 characters; responses are in order per connection. No automatic timeout resend or request deduplication is provided.

```powershell
dotnet run --project Tests/Tests.csproj
```

Tests use simulated input and do not operate the desktop, covering TCP packet fragmentation/sticky packets, Unicode, multi-client serialization, error requests, text/frame length, input failure, encoding, and closure. Manual acceptance: click the input field in a test window such as Notepad, configure path and title, use the Client to verify Chinese, emoji, multi-line, append, Enter, clear, and error feedback for target closure and multiple windows. Specific target application positioning and behavior require separate adaptation and acceptance after receiving application information.