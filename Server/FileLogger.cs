using System.Text.Json;

namespace Shine.Server;
public sealed class FileLogger
{
    static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All) };
    readonly object gate = new();
    readonly string currentPath;
    readonly string historyPath;
    readonly int minimum;
    readonly long maxBytes;
    readonly Action<string> errorOutput;
    public FileLogger(Configuration configuration, Action<string>? errorOutput = null, string? baseDirectory = null)
    {
        string directory = baseDirectory ?? AppContext.BaseDirectory;
        currentPath = Path.Combine(directory, "server.log");
        historyPath = Path.Combine(directory, "server.history.log");
        minimum = Level(configuration.LogLevel);
        maxBytes = checked((long)configuration.LogMaxSizeMB * 1024 * 1024);
        this.errorOutput = errorOutput ?? Console.Error.WriteLine;
        Directory.CreateDirectory(directory);
    }
    public static int Level(string name) => name.ToUpperInvariant() switch
    {
        "DEBUG" => 0, "INFO" => 1, "WARNING" => 2, "ERROR" => 3,
        _ => throw new InvalidDataException("LogLevel 必须为 Debug/Info/Warning/Error")
    };
    public void Write(string level, string message, object? data = null, Exception? exception = null)
    {
        if (Level(level) < minimum) return;
        lock (gate)
        {
            string entry = JsonSerializer.Serialize(new { time = DateTimeOffset.Now, level, message, data, exception = exception?.ToString() }, JsonOptions);
            if (Level(level) == 3) errorOutput($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} ERROR {message}: {exception?.Message}");
            try
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(entry + Environment.NewLine);
                if (File.Exists(currentPath) && new FileInfo(currentPath).Length + bytes.Length > maxBytes)
                {
                    // Overwrite the one previous backup before clearing the current log.
                    // If copying fails the original log is preserved.
                    File.Copy(currentPath, historyPath, overwrite: true);
                    File.WriteAllBytes(currentPath, []);
                }
                using var stream = new FileStream(currentPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { errorOutput("ERROR 写入日志失败：" + e.Message); }
        }
    }
}
