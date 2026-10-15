using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Shine.Server;
public sealed class ConsoleDashboard
{
    readonly object gate = new();
    readonly List<string> header;
    readonly Queue<string> errors = new();
    bool redirected = Console.IsOutputRedirected;
    public ConsoleDashboard(Configuration config)
    {
        var addresses = new List<(string Kind, string Name, IPAddress Address, int Order)>();
        var listenAddress = IPAddress.Parse(config.ListenAddress);
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            var (kind, order) = adapter.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => ("Wi-Fi", 0),
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT => ("有线", 1),
                _ => ("其他网卡", 2)
            };
            foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                if (address.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address.Address))
                    addresses.Add((kind, adapter.Name, address.Address, order));
        }
        header = ["Shine TCP Server | Ctrl+C 退出", "Client 连接地址（选择与 Client 同一局域网的 IP）："];
        foreach (var address in addresses.OrderBy(a => a.Order).ThenBy(a => a.Name).ThenBy(a => a.Address.ToString()))
        {
            bool listening = listenAddress.Equals(IPAddress.Any) || listenAddress.Equals(address.Address);
            header.Add($"  {address.Kind}: {address.Address}  端口: {config.Port}{(listening ? "" : " [未监听]")}  ({address.Name})");
        }
        if (!listenAddress.Equals(IPAddress.Any)) header.Add($"监听地址：{config.ListenAddress}:{config.Port}");
        if (addresses.Count == 0) header.Add("未找到可用局域网 IPv4 地址，请检查网络。");
        if (config.ListenAddress == "0.0.0.0" || config.ListenAddress == "127.0.0.1") header.Add($"本机测试：127.0.0.1:{config.Port}");
        header.Add("下方仅显示 Error；完整日志：server.log");
        lock (gate) Render();
    }
    public void Error(string message)
    {
        lock (gate)
        {
            if (redirected) { Console.Error.WriteLine(message); return; }
            errors.Enqueue(message.Replace('\r', ' ').Replace('\n', ' '));
            while (errors.Count > 100) errors.Dequeue();
            Render();
        }
    }
    void Render()
    {
        if (redirected) { foreach (var line in header) Console.WriteLine(line); return; }
        try
        {
            int height = Console.WindowHeight;
            int width = Console.WindowWidth;
            Console.Clear();
            var fixedLines = header.Take(Math.Max(1, height - 3)).ToList();
            foreach (var line in fixedLines) Console.WriteLine(Clip(line, width - 1));
            int available = Math.Max(0, height - fixedLines.Count - 2);
            foreach (var line in errors.TakeLast(available)) Console.WriteLine(Clip(line, width - 1));
        }
        catch (Exception e) when (e is IOException or PlatformNotSupportedException or ArgumentOutOfRangeException)
        {
            redirected = true;
            foreach (var line in header) Console.WriteLine(line);
            if (errors.TryPeek(out var last)) Console.Error.WriteLine(last);
        }
    }
    static string Clip(string value, int columns)
    {
        var result = new System.Text.StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            int size = rune.Value < 128 ? 1 : 2;
            if (columns < size) break;
            result.Append(rune); columns -= size;
        }
        return result.ToString();
    }
}
