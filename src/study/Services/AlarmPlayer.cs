using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using Study.Core.Config;

namespace Study.Services;

/// <summary>
/// Plays the one global alarm sound on a loop until <see cref="Stop"/>. Uses the WPF
/// <see cref="MediaPlayer"/> (MP3 + WAV, no extra dependency). Must be used on the UI thread.
/// </summary>
internal sealed class AlarmPlayer
{
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _autoStop = new();
    private bool _looping;

    public AlarmPlayer()
    {
        _player.MediaEnded += (_, _) =>
        {
            if (_looping)
            {
                _player.Position = TimeSpan.Zero;
                _player.Play();
            }
        };
        _player.MediaFailed += (_, e) => Failed?.Invoke($"Could not play alarm sound: {e.ErrorException.Message}");
        _autoStop.Tick += (_, _) =>
        {
            Stop();
            AutoStopped?.Invoke();
        };
    }

    public bool IsPlaying { get; private set; }

    /// <summary>Raised when the auto-stop timeout silenced the sound.</summary>
    public event Action? AutoStopped;

    public event Action<string>? Failed;

    public static string SoundsFolder => Path.Combine(AppContext.BaseDirectory, "Sounds");

    /// <summary>Names of the built-in sounds (the .mp3 files shipped in Sounds\).</summary>
    public static IReadOnlyList<string> BuiltInSounds() =>
        Directory.Exists(SoundsFolder)
            ? Directory.GetFiles(SoundsFolder, "*.mp3").Select(Path.GetFileNameWithoutExtension).OfType<string>().Order().ToList()
            : [];

    /// <summary>Resolves the configured sound to a file, falling back to the first built-in one.</summary>
    public static string? ResolvePath(StudySettings s)
    {
        if (s.Sound == StudySettings.CustomSound && File.Exists(s.CustomSoundPath))
        {
            return s.CustomSoundPath;
        }

        var builtIn = Path.Combine(SoundsFolder, s.Sound + ".mp3");
        if (File.Exists(builtIn))
        {
            return builtIn;
        }

        return BuiltInSounds().Select(n => Path.Combine(SoundsFolder, n + ".mp3")).FirstOrDefault();
    }

    /// <summary>Starts looping (no-op if already playing). <paramref name="autoStopSeconds"/> 0 = never.</summary>
    public void Start(StudySettings settings)
    {
        if (IsPlaying)
        {
            return;
        }

        Play(settings, loop: true);
        if (settings.AutoStopSeconds > 0)
        {
            _autoStop.Interval = TimeSpan.FromSeconds(settings.AutoStopSeconds);
            _autoStop.Start();
        }
    }

    /// <summary>One-shot playback for the Settings "Test" button.</summary>
    public void Preview(StudySettings settings)
    {
        Stop();
        Play(settings, loop: false);
    }

    public void Stop()
    {
        _autoStop.Stop();
        _looping = false;
        IsPlaying = false;
        _player.Stop();
    }

    private void Play(StudySettings settings, bool loop)
    {
        var path = ResolvePath(settings);
        if (path is null)
        {
            Failed?.Invoke("No alarm sound found. Pick one in Settings.");
            return;
        }

        _looping = loop;
        IsPlaying = loop;
        _player.Open(new Uri(path, UriKind.Absolute));
        _player.Volume = Math.Clamp(settings.Volume, 0, 100) / 100.0;
        _player.Play();
    }
}
