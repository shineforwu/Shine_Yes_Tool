using System.Text.Json;

namespace Shine.Server;
public sealed class Configuration
{
    public string ListenAddress { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 39827;
    public bool UseForegroundWindow { get; set; }
    public string TargetProcessPath { get; set; } = "";
    public string TargetWindowTitle { get; set; } = "";
    public int InputDelayMs { get; set; } = 150;
    public int MaxTextLength { get; set; } = 16000;
    public string LogLevel { get; set; } = "Debug";
    public int LogMaxSizeMB { get; set; } = 1024;
    public static string FilePath => Path.Combine(AppContext.BaseDirectory, "config.json");
    public static Configuration Load()
    {
        var config = File.Exists(FilePath) ? JsonSerializer.Deserialize<Configuration>(File.ReadAllText(FilePath)) ?? throw new InvalidDataException("配置为空") : new Configuration();
        if (config.Port is < 1 or > 65535 || config.InputDelayMs is < 50 or > 3000 || config.MaxTextLength is < 1 or > 16000)
            throw new InvalidDataException("配置中的端口、输入延迟或文字长度无效");
        System.Net.IPAddress.Parse(config.ListenAddress);
        FileLogger.Level(config.LogLevel);
        if (config.LogMaxSizeMB is < 1 or > 1048576)
            throw new InvalidDataException("LogMaxSizeMB 范围为 1–1048576，默认 1024（1 GiB）");
        return config;
    }
    public void Save() => File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}
