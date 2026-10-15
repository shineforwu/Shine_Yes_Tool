using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;

namespace Shine.Server;
public sealed class TcpCommandServer(Configuration config, IInputController input, FileLogger? logger = null) : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly SemaphoreSlim execution = new(1, 1);
    readonly ConcurrentDictionary<int, Task> clients = new();
    TcpListener? listener;
    Task? acceptTask;
    int nextClient;
    public event Action<string>? Status;
    public void Start()
    {
        listener = new TcpListener(IPAddress.Parse(config.ListenAddress), config.Port);
        listener.Start(16);
        acceptTask = AcceptAsync();
    }
    async Task AcceptAsync()
    {
        try {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener!.AcceptTcpClientAsync(stop.Token);
                if (clients.Count >= 16) { logger?.Write("Warning", "连接上限，拒绝连接", new { remote = client.Client.RemoteEndPoint?.ToString() }); client.Dispose(); continue; }
                int id = Interlocked.Increment(ref nextClient);
                var task = HandleAsync(client, id);
                clients[id] = task;
                _ = task.ContinueWith(t => { clients.TryRemove(id, out _); }, TaskScheduler.Default);
            }
        } catch (OperationCanceledException) { } catch (SocketException) when (stop.IsCancellationRequested) { }
        catch (Exception e) { logger?.Write("Error", "监听失败", exception: e); Status?.Invoke("监听失败：" + e.Message); }
    }
    async Task HandleAsync(TcpClient client, int connectionId)
    {
        using (client)
        {
            client.NoDelay = true;
            string? remote = client.Client.RemoteEndPoint?.ToString();
            logger?.Write("Info", "客户端已连接", new { connectionId, remote });
            Status?.Invoke("客户端已连接：" + client.Client.RemoteEndPoint);
            try {
                var stream = client.GetStream();
                while (!stop.IsCancellationRequested)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    timeout.CancelAfter(TimeSpan.FromMinutes(5));
                    var line = await ReadLineAsync(stream, timeout.Token);
                    if (line is null) break;
                    string? id = null;
                    string? action = null;
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    logger?.Write("Debug", "收到请求帧", new { connectionId, remote, frameLength = line.Length });
                    object reply;
                    try {
                        using var json = JsonDocument.Parse(line);
                        var root = json.RootElement;
                        id = root.GetProperty("id").GetString();
                        if (string.IsNullOrEmpty(id) || id.Length > 64) throw new InvalidDataException("无效请求 ID");
                        action = root.GetProperty("action").GetString();
                        if (action is not ("send" or "type" or "enter" or "clear" or "ping")) throw new InvalidDataException("未知指令");
                        string text = root.TryGetProperty("text", out var value) ? value.GetString() ?? "" : "";
                        if (text.Length > config.MaxTextLength || text.Contains('\0')) throw new InvalidDataException("文字过长或包含空字符");
                        if (action is "send" or "type" && text.Length == 0) throw new InvalidDataException("文字不能为空");
                        logger?.Write("Debug", "请求已验证，等待执行", new { connectionId, remote, requestId = id, action, textLength = text.Length, textHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))) });
                        if (action != "ping")
                        {
                            await execution.WaitAsync(timeout.Token);
                            try {
                                logger?.Write("Debug", "开始执行", new { connectionId, requestId = id, action, queueWaitMs = timer.ElapsedMilliseconds });
                                await input.ExecuteAsync(config, action!, text, timeout.Token);
                            }
                            finally { execution.Release(); }
                        }
                        reply = new { id, ok = true, message = "指令已执行" };
                        logger?.Write("Info", "指令已执行", new { connectionId, remote, requestId = id, action, elapsedMs = timer.ElapsedMilliseconds });
                        Status?.Invoke($"已执行：{action}");
                    } catch (OperationCanceledException) { throw; }
                    catch (Exception e) { logger?.Write("Error", "指令失败", new { connectionId, remote, requestId = id, action, elapsedMs = timer.ElapsedMilliseconds }, e); reply = new { id, ok = false, message = e.Message }; Status?.Invoke("指令失败：" + e.Message); }
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply) + "\n"), timeout.Token);
                    logger?.Write("Debug", "响应已发送", new { connectionId, requestId = id, action });
                }
            } catch (OperationCanceledException e) { logger?.Write(stop.IsCancellationRequested ? "Info" : "Warning", "连接已取消或超时", new { connectionId, remote }, stop.IsCancellationRequested ? null : e); }
            catch (Exception e) { logger?.Write("Error", "连接处理异常", new { connectionId, remote }, e); Status?.Invoke("客户端断开：" + e.Message); }
            finally { logger?.Write("Info", "客户端已断开", new { connectionId, remote }); }
        }
    }
    // Bounded byte framing prevents unbounded allocations from unterminated requests.
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        byte[] one = new byte[1];
        while (await stream.ReadAsync(one, token) != 0)
        {
            if (one[0] == 10) return new UTF8Encoding(false, true).GetString(buffer.ToArray());
            if (buffer.Length >= 131072) throw new InvalidDataException("请求超过 128 KiB");
            buffer.WriteByte(one[0]);
        }
        if (buffer.Length != 0) throw new InvalidDataException("不完整请求");
        return null;
    }
    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync(); listener?.Stop();
        if (acceptTask is not null) await acceptTask;
        await Task.WhenAll(clients.Values);
    }
}
