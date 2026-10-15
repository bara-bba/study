using System.Globalization;
using Study.Core.Config;

namespace Study.Core.Logging;

/// <summary>Kinds of events written to the CSV log.</summary>
public static class LogEvents
{
    public const string Started = "STARTED";
    public const string Stopped = "STOPPED";
    public const string Connected = "CONNECTED";
    public const string Unreachable = "UNREACHABLE";
    public const string ReadError = "READ_ERROR";
    public const string Trigger = "TRIGGER";
    public const string Ack = "ACK";
    public const string Cleared = "CLEARED";
    public const string AutoStop = "AUTO_STOP";

    public static readonly string[] All =
        [Started, Stopped, Connected, Unreachable, ReadError, Trigger, Ack, Cleared, AutoStop];
}

public sealed record LogEntry(
    DateTime Timestamp,
    string Event,
    string Watch,
    string Plc,
    string Ip,
    string Tag,
    string Value,
    string Detail)
{
    public const string Header = "timestamp,event,watch,plc,ip,tag,value,detail";

    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    public string ToCsvLine() => string.Join(',',
        Timestamp.ToString(TimeFormat, CultureInfo.InvariantCulture),
        Event,
        Csv.Quote(Watch),
        Csv.Quote(Plc),
        Ip,
        Csv.Quote(Tag),
        Csv.Quote(Value),
        Csv.Quote(Detail));

    public static bool TryParse(string line, out LogEntry entry)
    {
        entry = null!;
        var c = Csv.SplitLine(line);
        if (c.Count < 8
            || !DateTime.TryParseExact(c[0], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var ts))
        {
            return false;
        }

        entry = new LogEntry(ts, c[1], c[2], c[3], c[4], c[5], c[6], c[7]);
        return true;
    }
}

/// <summary>
/// Appends events to one CSV per day (<c>study-YYYY-MM-DD.csv</c>) in the log folder.
/// Thread-safe; each write opens/appends/closes so the file can be opened in Excel while study runs.
/// </summary>
public sealed class EventLogger
{
    private readonly object _gate = new();
    private readonly Func<DateTime> _clock;

    public EventLogger(string folder, Func<DateTime>? clock = null)
    {
        Folder = folder;
        _clock = clock ?? (() => DateTime.Now);
    }

    public string Folder { get; private set; }

    /// <summary>Raised after every successful write (on the writer's thread).</summary>
    public event Action<LogEntry>? Written;

    /// <summary>Raised when the log file cannot be written (e.g. locked by Excel).</summary>
    public event Action<string>? WriteFailed;

    public void ChangeFolder(string folder)
    {
        lock (_gate)
        {
            Folder = folder;
        }
    }

    public static string FileNameFor(DateTime day) => $"study-{day:yyyy-MM-dd}.csv";

    public LogEntry Log(string evt, string watch = "", string plc = "", string ip = "", string tag = "",
        string value = "", string detail = "")
    {
        var entry = new LogEntry(_clock(), evt, watch, plc, ip, tag, value, detail);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var path = Path.Combine(Folder, FileNameFor(entry.Timestamp));
                var isNew = !File.Exists(path);
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream);
                if (isNew)
                {
                    writer.WriteLine(LogEntry.Header);
                }

                writer.WriteLine(entry.ToCsvLine());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WriteFailed?.Invoke($"Could not write log in {Folder}: {ex.Message}");
                return entry;
            }
        }

        Written?.Invoke(entry);
        return entry;
    }
}

/// <summary>Reads study CSV logs for the log viewer.</summary>
public static class LogReader
{
    public static IReadOnlyList<FileInfo> ListFiles(string folder) =>
        Directory.Exists(folder)
            ? new DirectoryInfo(folder).GetFiles("*.csv").OrderByDescending(f => f.Name).ToList()
            : [];

    /// <summary>Reads entries; shares the file so a log being written right now can still be read.</summary>
    public static List<LogEntry> Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var entries = new List<LogEntry>();
        while (reader.ReadLine() is { } line)
        {
            if (LogEntry.TryParse(line, out var e))
            {
                entries.Add(e);
            }
        }

        return entries;
    }
}
