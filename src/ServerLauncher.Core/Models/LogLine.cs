namespace ServerLauncher.Core.Models;

/// <summary>A single captured console line, tagged with its origin and arrival time.</summary>
/// <param name="Timestamp">When the launcher received the line.</param>
/// <param name="Stream">Which stream produced it.</param>
/// <param name="Text">The line content, without a trailing newline.</param>
public readonly record struct LogLine(DateTimeOffset Timestamp, LogStream Stream, string Text)
{
    public static LogLine Output(string text) => new(DateTimeOffset.Now, LogStream.StandardOutput, text);

    public static LogLine Error(string text) => new(DateTimeOffset.Now, LogStream.StandardError, text);

    public static LogLine Launcher(string text) => new(DateTimeOffset.Now, LogStream.Launcher, text);

    public string ToLogFileLine() => $"{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Stream}] {Text}";
}
