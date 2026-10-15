using Shine.Server;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// Dependency-free integration tests. Never inject input into the real desktop.
var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start();
int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
var input = new FakeInput();
string logDirectory = Path.Combine(Path.GetTempPath(), "ShineTests", Guid.NewGuid().ToString("N"));
var consoleErrors = new System.Collections.Concurrent.ConcurrentQueue<string>();
var logger = new FileLogger(new Configuration { LogLevel = "Debug" }, consoleErrors.Enqueue, logDirectory);
await using var server = new TcpCommandServer(new Configuration { ListenAddress = "127.0.0.1", Port = port }, input, logger);
server.Start();
using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, port);
var stream = client.GetStream();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
async Task<JsonDocument> Request(string line)
{
    await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), timeout.Token);
    return JsonDocument.Parse((await TcpCommandServer.ReadLineAsync(stream, timeout.Token))!);
}
void Check(bool result, string name) { if (!result) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
Check(JsonSerializer.Deserialize<Configuration>("{\"UseForegroundWindow\":true}")!.UseForegroundWindow, "配置开启当前窗口模式");
Check(!JsonSerializer.Deserialize<Configuration>("{}")!.UseForegroundWindow, "旧配置保留指定目标模式");
using (var reply = await Request("{\"id\":\"1\",\"action\":\"ping\"}")) Check(reply.RootElement.GetProperty("ok").GetBoolean() && input.Count == 0, "ping 不执行输入");
var payload = JsonSerializer.Serialize(new { id = "2", action = "send", text = "中文😀\nsecond line" });
byte[] bytes = Encoding.UTF8.GetBytes(payload + "\n");
await stream.WriteAsync(bytes.AsMemory(0, 7), timeout.Token);
await stream.WriteAsync(bytes.AsMemory(7), timeout.Token);
using (var reply = JsonDocument.Parse((await TcpCommandServer.ReadLineAsync(stream, timeout.Token))!))
    Check(reply.RootElement.GetProperty("ok").GetBoolean() && input.LastText == "中文😀\nsecond line", "拆包、Unicode 和换行");
await Task.Delay(250, timeout.Token);
Check(input.Count == 1, "一条 send 仅执行一次");
await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"id\":\"3\",\"action\":\"clear\"}\n{\"id\":\"4\",\"action\":\"enter\"}\n"), timeout.Token);
using (var reply = JsonDocument.Parse((await TcpCommandServer.ReadLineAsync(stream, timeout.Token))!)) Check(reply.RootElement.GetProperty("id").GetString() == "3", "粘包第一条");
using (var reply = JsonDocument.Parse((await TcpCommandServer.ReadLineAsync(stream, timeout.Token))!)) Check(reply.RootElement.GetProperty("id").GetString() == "4", "粘包第二条");
using (var reply = await Request("bad json")) Check(!reply.RootElement.GetProperty("ok").GetBoolean(), "无效 JSON 返回错误");
using (var reply = await Request("{\"id\":\"5\",\"action\":\"unknown\"}")) Check(!reply.RootElement.GetProperty("ok").GetBoolean(), "拒绝未知指令");
using (var reply = await Request(JsonSerializer.Serialize(new { id = "6", action = "send", text = new string('a', 16001) }))) Check(!reply.RootElement.GetProperty("ok").GetBoolean(), "拒绝超长文字");
input.Fail = true;
using (var reply = await Request("{\"id\":\"7\",\"action\":\"clear\"}")) Check(!reply.RootElement.GetProperty("ok").GetBoolean(), "输入失败返回错误");
input.Fail = false;
using var other = new TcpClient(); await other.ConnectAsync(IPAddress.Loopback, port);
await Task.WhenAll(stream.WriteAsync(Encoding.UTF8.GetBytes("{\"id\":\"8\",\"action\":\"clear\"}\n"), timeout.Token).AsTask(), other.GetStream().WriteAsync(Encoding.UTF8.GetBytes("{\"id\":\"9\",\"action\":\"enter\"}\n"), timeout.Token).AsTask());
await Task.WhenAll(TcpCommandServer.ReadLineAsync(stream, timeout.Token), TcpCommandServer.ReadLineAsync(other.GetStream(), timeout.Token));
Check(input.MaxConcurrency == 1, "多客户端输入串行执行");
using var overlong = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 131073)));
try { await TcpCommandServer.ReadLineAsync(overlong, timeout.Token); throw new Exception("长度检查未生效"); }
catch (InvalidDataException) { Check(true, "未终止请求长度上限"); }
using var invalidUtf8 = new MemoryStream([0xFF, 10]);
try { await TcpCommandServer.ReadLineAsync(invalidUtf8, timeout.Token); throw new Exception("编码检查未生效"); }
catch (DecoderFallbackException) { Check(true, "拒绝无效 UTF-8"); }
var logEntries = File.ReadAllLines(Path.Combine(logDirectory, "server.log")).Select(line => JsonDocument.Parse(line)).ToList();
try
{
    Check(logEntries.Any(entry => entry.RootElement.GetProperty("level").GetString() == "Debug" && entry.RootElement.GetProperty("message").GetString() == "收到请求帧"), "Debug 日志写入文件");
    Check(logEntries.Any(entry => entry.RootElement.GetProperty("level").GetString() == "Error" && entry.RootElement.GetProperty("exception").GetString()!.Contains("模拟失败")), "Error 日志包含异常堆栈");
    Check(logEntries.Count(entry => entry.RootElement.GetProperty("message").GetString() == "指令已执行" && entry.RootElement.GetProperty("data").GetProperty("requestId").GetString() == "2") == 1, "请求 ID 对应一次执行日志");
}
finally { foreach (var entry in logEntries) entry.Dispose(); }
Check(consoleErrors.Count > 0 && consoleErrors.All(line => line.Contains("ERROR")), "控制台只输出 Error");
int beforeInfo = consoleErrors.Count;
logger.Write("Info", "不应输出到控制台"); logger.Write("Warning", "也不应输出到控制台");
Check(consoleErrors.Count == beforeInfo, "Info 和 Warning 仅写文件");
string rotationDirectory = Path.Combine(logDirectory, "rotation");
var rotating = new FileLogger(new Configuration { LogMaxSizeMB = 1 }, _ => { }, rotationDirectory);
string currentLog = Path.Combine(rotationDirectory, "server.log");
string historyLog = Path.Combine(rotationDirectory, "server.history.log");
File.WriteAllBytes(currentLog, Enumerable.Repeat((byte)'a', 1024 * 1024).ToArray());
rotating.Write("Debug", "第一次轮转");
Check(new FileInfo(historyLog).Length == 1024 * 1024 && File.ReadAllBytes(historyLog)[0] == 'a' && new FileInfo(currentLog).Length < 1024 * 1024, "达到上限备份并重建当前日志");
File.WriteAllBytes(currentLog, Enumerable.Repeat((byte)'b', 1024 * 1024).ToArray());
rotating.Write("Debug", "第二次轮转");
Check(Directory.GetFiles(rotationDirectory).Length == 2 && File.ReadAllBytes(historyLog)[0] == 'b', "第二次轮转覆盖历史，始终最多两个日志文件");
Console.WriteLine("全部通过；退出时同时验证带活动连接的 Server 关闭。");

sealed class FakeInput : IInputController
{
    public int Count; public string LastText = ""; public bool Fail; public int MaxConcurrency; int active;
    public IReadOnlyList<TargetWindow> GetWindows() => [];
    public async Task ExecuteAsync(Configuration c, string action, string text, CancellationToken token)
    {
        if (Fail) throw new InvalidOperationException("模拟失败");
        int count = Interlocked.Increment(ref active); MaxConcurrency = Math.Max(MaxConcurrency, count);
        try { await Task.Delay(15, token); Count++; LastText = text; } finally { Interlocked.Decrement(ref active); }
    }
}
