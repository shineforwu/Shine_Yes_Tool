using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Shine.Server;
public record TargetWindow(nint Handle, string Title, string ProcessPath)
{
    public override string ToString() => $"{Title} ({Path.GetFileName(ProcessPath)})";
}
public interface IInputController
{
    IReadOnlyList<TargetWindow> GetWindows();
    Task ExecuteAsync(Configuration configuration, string action, string text, CancellationToken token);
}
public sealed class WindowsInputController : IInputController
{
    public FileLogger? Logger { get; set; }
    private nint pinnedWindow;
    public void Select(TargetWindow window) => pinnedWindow = window.Handle;
    public IReadOnlyList<TargetWindow> GetWindows()
    {
        var result = new List<TargetWindow>();
        EnumWindows((handle, _) => {
            if (!IsWindowVisible(handle)) return true;
            var title = new StringBuilder(1024); GetWindowText(handle, title, title.Capacity);
            GetWindowThreadProcessId(handle, out var pid);
            if (pid == Environment.ProcessId || title.Length == 0) return true;
            try {
                using var process = Process.GetProcessById((int)pid);
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrEmpty(path)) result.Add(new(handle, title.ToString(), path));
            } catch { /* Protected or exited processes cannot be selected. */ }
            return true;
        }, 0);
        return result;
    }
    public async Task ExecuteAsync(Configuration c, string action, string text, CancellationToken token)
    {
        nint targetHandle;
        if (c.UseForegroundWindow)
        {
            targetHandle = GetForegroundWindow();
            if (targetHandle == 0) throw new InvalidOperationException("当前没有可用的前台窗口");
            GetWindowThreadProcessId(targetHandle, out var foregroundPid);
            if (foregroundPid == Environment.ProcessId) throw new InvalidOperationException("当前前台窗口属于 Server，请先聚焦目标应用的输入框");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(c.TargetProcessPath)) throw new InvalidOperationException("UseForegroundWindow=false 时必须在 config.json 填写 TargetProcessPath");
            var candidates = GetWindows().Where(w => string.Equals(w.ProcessPath, c.TargetProcessPath, StringComparison.OrdinalIgnoreCase)).ToList();
            var target = candidates.FirstOrDefault(w => w.Handle == pinnedWindow);
            target ??= candidates.SingleOrDefault(w => w.Title == c.TargetWindowTitle);
            if (target is null && candidates.Count == 1) target = candidates[0];
            if (target is null) throw new InvalidOperationException("目标窗口未找到或存在多个匹配窗口，请重新选择");
            if (IsIconic(target.Handle)) ShowWindow(target.Handle, 9);
            var current = GetForegroundWindow();
            var currentThread = GetWindowThreadProcessId(current, out _);
            var thread = GetCurrentThreadId();
            var attached = currentThread != thread && AttachThreadInput(thread, currentThread, true);
            try { SetForegroundWindow(target.Handle); }
            finally { if (attached) AttachThreadInput(thread, currentThread, false); }
            targetHandle = target.Handle;
        }
        await Task.Delay(c.InputDelayMs, token);
        EnsureForeground(targetHandle);
        GetWindowThreadProcessId(targetHandle, out var targetPid);
        var targetTitle = new StringBuilder(1024); GetWindowText(targetHandle, targetTitle, targetTitle.Capacity);
        Logger?.Write("Debug", "输入目标已确认", new { action, windowHandle = targetHandle.ToInt64(), processId = targetPid, title = targetTitle.ToString(), c.UseForegroundWindow });
        for (var key = 0x10; key <= 0x12; key++)
            if ((GetAsyncKeyState(key) & 0x8000) != 0) throw new InvalidOperationException("目标电脑正在按住 Shift/Ctrl/Alt，请松开后重试");
        if (action == "clear")
        {
            Send([Key(0x11), Key(0x41), Key(0x41, 2), Key(0x11, 2), Key(0x08), Key(0x08, 2)]);
            return;
        }
        if (action is "send" or "type")
        {
            // UTF-16 Unicode events preserve Chinese and surrogate pairs without modifying the clipboard.
            for (int offset = 0; offset < text.Length; offset += 128)
            {
                EnsureForeground(targetHandle);
                token.ThrowIfCancellationRequested();
                var batch = new List<INPUT>();
                foreach (var ch in text.Skip(offset).Take(128))
                {
                    if (ch == '\r') continue;
                    if (ch == '\n') batch.AddRange([Key(0x10), Key(0x0D), Key(0x0D, 2), Key(0x10, 2)]);
                    else batch.AddRange([Unicode(ch), Unicode(ch, true)]);
                }
                if (batch.Count > 0) Send(batch.ToArray());
                await Task.Delay(5, token);
            }
        }
        if (action is "send" or "enter") { EnsureForeground(targetHandle); Send([Key(0x0D), Key(0x0D, 2)]); }
    }
    static void EnsureForeground(nint target)
    {
        if (GetForegroundWindow() != target) throw new InvalidOperationException("目标窗口未获得焦点或焦点已改变，输入已停止");
    }
    static INPUT Key(ushort key, uint flags = 0) => new() { type = 1, data = new INPUTUNION { keyboard = new KEYBDINPUT { virtualKey = key, flags = flags } } };
    static INPUT Unicode(char ch, bool up = false) => new() { type = 1, data = new INPUTUNION { keyboard = new KEYBDINPUT { scan = ch, flags = up ? 6u : 4u } } };
    static void Send(INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new InvalidOperationException("系统拒绝输入，目标应用可能以管理员权限运行；指令可能部分执行，请检查后再试");
    }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION data; }
    [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT keyboard; [FieldOffset(0)] public MOUSEINPUT mouse; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort virtualKey, scan; public uint flags, time; public nuint extra; }
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int x, y; public uint mouseData, flags, time; public nuint extra; }
    delegate bool EnumProc(nint handle, nint data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc callback, nint data);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(nint handle, StringBuilder text, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint handle, out uint pid);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool IsIconic(nint handle);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
public sealed class UnsupportedInputController : IInputController
{
    public IReadOnlyList<TargetWindow> GetWindows() => [];
    public Task ExecuteAsync(Configuration c, string a, string text, CancellationToken token) => throw new PlatformNotSupportedException("此版本仅实现 Windows 输入适配；macOS/Linux 尚未实现");
}
