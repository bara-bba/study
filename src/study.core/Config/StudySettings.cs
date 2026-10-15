namespace Study.Core.Config;

/// <summary>User preferences, persisted to <c>settings.json</c>.</summary>
public sealed record StudySettings
{
    public const string FileName = "settings.json";

    /// <summary>Built-in sound name (file name without .mp3) or <see cref="CustomSound"/>.</summary>
    public string Sound { get; init; } = "Klaxon";

    public const string CustomSound = "Custom";

    /// <summary>Full path to the user's own .mp3/.wav, used when <see cref="Sound"/> is Custom.</summary>
    public string CustomSoundPath { get; init; } = "";

    /// <summary>0–100.</summary>
    public int Volume { get; init; } = 80;

    /// <summary>Stop the sound automatically after this many seconds; 0 = until acknowledged.</summary>
    public int AutoStopSeconds { get; init; }

    public string LogFolder { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "study", "logs");

    /// <summary>Polling time pre-filled for new watches.</summary>
    public int DefaultPollMs { get; init; } = 1000;

    /// <summary>Connect even when ICMP ping fails (some VLANs block ping but allow EtherNet/IP).</summary>
    public bool SkipPing { get; init; }

    /// <summary>Sound the alarm when a watched PLC becomes unreachable or a read fails.</summary>
    public bool AlarmOnConnectionLost { get; init; }

    public string AccentColor { get; init; } = "#0078D4";

    public bool StartWithWindows { get; init; }

    /// <summary>Show a Windows balloon notification when an alarm fires.</summary>
    public bool TrayNotifications { get; init; } = true;
}
