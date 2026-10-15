namespace Shine.Server;
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        FileLogger? logger = null;
        ConsoleDashboard? dashboard = null;
        try {
            IInputController input = OperatingSystem.IsWindows() ? new WindowsInputController() : new UnsupportedInputController();
            if (args.Contains("--list-windows"))
            {
                foreach (var window in input.GetWindows()) Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { window.Title, window.ProcessPath }));
                return 0;
            }
            var config = Configuration.Load();
            if (!File.Exists(Configuration.FilePath)) config.Save();
            if (args.Contains("--check-config")) { Console.WriteLine("配置有效：" + Configuration.FilePath); return 0; }
            logger = new FileLogger(config, message => { if (dashboard is not null) dashboard.Error(message); else Console.Error.WriteLine(message); });
            if (input is WindowsInputController windows) windows.Logger = logger;
            using var exit = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.Cancel(); };
            await using var server = new TcpCommandServer(config, input, logger);
            server.Start();
            dashboard = new ConsoleDashboard(config);
            logger.Write("Info", "Server 已启动", new { config.Port, config.ListenAddress, config.UseForegroundWindow, config.TargetProcessPath, config.TargetWindowTitle, processId = Environment.ProcessId });
            if (!OperatingSystem.IsWindows()) logger.Write("Error", "当前系统输入适配尚未实现；TCP 可运行，但控制指令将返回不支持。");
            try { await Task.Delay(Timeout.Infinite, exit.Token); } catch (OperationCanceledException) { }
            return 0;
        } catch (Exception e) { if (logger is not null) logger.Write("Error", "Server 异常退出", exception: e); else Console.Error.WriteLine(e); return 1; }
    }
}
