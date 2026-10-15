using Godot;
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public partial class Main : Control
{
    LineEdit host = null!;
    SpinBox port = null!;
    TextEdit text = null!;
    Label status = null!;
    Button connect = null!;
    Button[] actions = [];
    TcpClient? client;
    StreamReader? reader;
    CancellationTokenSource lifetime = new();
    bool busy;
    public override void _Ready()
    {
        host = GetNode<LineEdit>("Margin/Content/Connection/Host");
        port = GetNode<SpinBox>("Margin/Content/Connection/Port");
        connect = GetNode<Button>("Margin/Content/Connection/Connect");
        connect.FocusMode = FocusModeEnum.None;
        text = GetNode<TextEdit>("Margin/Content/Message");
        status = GetNode<Label>("Margin/Content/Status");
        connect.Pressed += () => _ = ConnectAsync();
        actions = [BindButton("Send", "send"), BindButton("Type", "type"), BindButton("Enter", "enter"), BindButton("Clear", "clear")];
        try {
            var file = ProjectSettings.GlobalizePath("user://client-settings.json");
            if (File.Exists(file)) { using var doc = JsonDocument.Parse(File.ReadAllText(file)); host.Text = doc.RootElement.GetProperty("host").GetString(); port.Value = doc.RootElement.GetProperty("port").GetInt32(); }
        } catch (Exception e) { status.Text = "设置读取失败：" + e.Message; }
        UpdateButtons();
    }
    Button BindButton(string nodeName, string action)
    {
        var button = GetNode<Button>("Margin/Content/Actions/" + nodeName);
        // Local foreground-mode testing injects Enter into this very window.
        // Action buttons must not keep keyboard focus and recursively send commands.
        button.FocusMode = FocusModeEnum.None;
        button.Pressed += () => _ = CommandAsync(action); return button;
    }
    void UpdateButtons()
    {
        connect.Disabled = busy; host.Editable = !busy && client is null; port.Editable = !busy && client is null;
        connect.Text = client is null ? "连接" : "断开";
        foreach (var button in actions) button.Disabled = busy || client is null;
    }
    async Task ConnectAsync()
    {
        if (busy) return;
        if (client is not null) { Disconnect(); status.Text = "已断开"; UpdateButtons(); return; }
        busy = true; UpdateButtons(); status.Text = "正在连接…";
        try {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(5000);
            client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host.Text.Trim(), (int)port.Value, timeout.Token);
            reader = new StreamReader(client.GetStream(), new UTF8Encoding(false, true), false, 1024, true);
            await RequestAsync("ping", "", timeout.Token);
            File.WriteAllText(ProjectSettings.GlobalizePath("user://client-settings.json"), JsonSerializer.Serialize(new { host = host.Text.Trim(), port = (int)port.Value }));
            status.Text = "已连接";
        } catch (Exception e) { Disconnect(); status.Text = "连接失败：" + e.Message; }
        finally { busy = false; UpdateButtons(); }
    }
    async Task CommandAsync(string action)
    {
        if (busy || client is null) return;
        if (action is "send" or "type" && string.IsNullOrEmpty(text.Text)) { status.Text = "请输入文字"; return; }
        if (text.Text.Length > 16000 && action is "send" or "type") { status.Text = "文字最多 16000 个 UTF-16 字符"; return; }
        busy = true; UpdateButtons();
        try {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(15000);
            status.Text = await RequestAsync(action, action is "send" or "type" ? text.Text : "", timeout.Token);
        } catch (Exception e) { Disconnect(); status.Text = "连接中断，指令可能已执行，请检查目标后重连。" + e.Message; }
        finally { busy = false; UpdateButtons(); }
    }
    async Task<string> RequestAsync(string action, string content, CancellationToken token)
    {
        string id = Guid.NewGuid().ToString("N");
        GD.Print($"TCP request id={id} action={action} textLength={content.Length}");
        await client!.GetStream().WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, action, text = content }) + "\n"), token);
        string line = await reader!.ReadLineAsync(token) ?? throw new IOException("Server 已断开");
        if (line.Length > 8192) throw new IOException("Server 响应过长");
        using var doc = JsonDocument.Parse(line);
        if (doc.RootElement.GetProperty("id").GetString() != id) throw new IOException("响应 ID 不匹配");
        return (doc.RootElement.GetProperty("ok").GetBoolean() ? "成功：" : "失败：") + doc.RootElement.GetProperty("message").GetString();
    }
    void Disconnect() { reader?.Dispose(); reader = null; client?.Dispose(); client = null; }
    public override void _ExitTree() { lifetime.Cancel(); Disconnect(); lifetime.Dispose(); }
}
