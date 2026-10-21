using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Study.Core.Config;
using Study.Native;
using Study.Services;
using WinForms = System.Windows.Forms;

namespace Study;

/// <summary>
/// Tray-resident like Picky: closing the window hides it, monitoring keeps running, and
/// Exit lives in the tray menu. Left-click on the tray icon opens the main window.
/// </summary>
public partial class App : Application
{
    private const string MutexName = @"Local\study.single-instance";

    private Mutex? _singleInstance;
    private WinForms.NotifyIcon _tray = null!;
    private WinForms.ToolStripMenuItem _pauseItem = null!;
    private WinForms.ToolStripMenuItem _ackItem = null!;
    private MainWindow? _main;
    private bool _trayShowsAlarm;

    internal static JsonStore Store { get; private set; } = null!;

    internal static StudySettings Settings { get; private set; } = new();

    internal static MonitorService Monitor { get; private set; } = null!;

    internal static new App Current => (App)Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Build-time helper (same as Picky): study.exe --emit-icon <path> writes the embedded app.ico.
        var emit = Array.IndexOf(e.Args, "--emit-icon");
        if (emit >= 0 && emit + 1 < e.Args.Length)
        {
            System.IO.File.WriteAllBytes(e.Args[emit + 1],
                AppIcon.BuildIco(ToDrawing(AccentTheme.Current), ToDrawing(AccentTheme.OnAccent)));
            Shutdown();
            return;
        }

        // --probe <folder>: headless self-check (CI + packaged exe), see Probe.cs.
        var probe = Array.IndexOf(e.Args, "--probe");
        if (probe >= 0 && probe + 1 < e.Args.Length)
        {
            Shutdown(Probe.Run(e.Args[probe + 1]));
            return;
        }

        // --snapshot only renders windows (no monitoring, no saving), so it may run beside a live instance.
        var snap = Array.IndexOf(e.Args, "--snapshot");
        var snapshotMode = snap >= 0 && snap + 1 < e.Args.Length;

        _singleInstance = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew && !snapshotMode)
        {
            MessageBox.Show("study is already running — look for its icon in the system tray.", "study",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandledException;

        Store = new JsonStore(JsonStore.DefaultFolder);
        string? loadProblem = null;
        try
        {
            Settings = Store.Load(StudySettings.FileName, () => new StudySettings());
        }
        catch (ConfigException ex)
        {
            loadProblem = ex.Message;
        }

        if (AccentTheme.TryParse(Settings.AccentColor, out var accent))
        {
            AccentTheme.Apply(accent);
        }

        Monitor = new MonitorService(Store, Settings, Dispatcher);
        loadProblem = string.Join("\n", new[] { loadProblem, Monitor.Load() }.Where(p => p is not null));
        Monitor.AlarmsChanged += OnAlarmsChanged;
        Monitor.AlarmFired += OnAlarmFired;
        Monitor.Player.Failed += msg => _tray.ShowBalloonTip(5000, "study", msg, WinForms.ToolTipIcon.Warning);
        Monitor.Logger.WriteFailed += msg => Dispatcher.BeginInvoke(() =>
            _tray.ShowBalloonTip(5000, "study — log", msg, WinForms.ToolTipIcon.Warning));

        _tray = CreateTrayIcon();

        if (snapshotMode)
        {
            var scaleAt = Array.IndexOf(e.Args, "--scale");
            var scale = scaleAt >= 0 && scaleAt + 1 < e.Args.Length
                        && double.TryParse(e.Args[scaleAt + 1], System.Globalization.CultureInfo.InvariantCulture, out var s)
                ? s
                : 1;
            Snapshot.Run(e.Args[snap + 1], scale);
            _tray.Dispose();
            Shutdown();
            return;
        }

        ShowMain();
        if (!string.IsNullOrEmpty(loadProblem))
        {
            MessageBox.Show(
                $"Some configuration could not be read and was ignored:\n\n{loadProblem}\n\nFix or delete the file, then restart study.",
                "study", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Monitor.StartAll();
    }

    internal void SaveSettings(StudySettings settings)
    {
        Settings = settings;
        Store.Save(StudySettings.FileName, settings);
        Monitor.ApplySettings(settings);
        RefreshTrayIcon(force: true);
        _main?.RefreshAppIcon();
    }

    /// <summary>The accent-coloured .ico (all sizes), used for the taskbar button.</summary>
    internal static byte[] CurrentIcoBytes() =>
        AppIcon.BuildIco(ToDrawing(AccentTheme.Current), ToDrawing(AccentTheme.OnAccent));

    internal void ShowMain()
    {
        _main ??= new MainWindow();
        if (!_main.IsVisible)
        {
            _main.Show();
        }

        if (_main.WindowState == WindowState.Minimized)
        {
            _main.WindowState = WindowState.Normal;
        }

        _main.Activate();
    }

    internal static BitmapSource CurrentIconSource() =>
        AppIcon.CreateImageSource(ToDrawing(AccentTheme.Current), ToDrawing(AccentTheme.OnAccent));

    internal static System.Windows.Media.ImageSource CurrentVectorIcon() =>
        AppIcon.CreateVectorImage(ToDrawing(AccentTheme.Current), ToDrawing(AccentTheme.OnAccent));

    internal async void ExitApp()
    {
        _tray.Visible = false;
        await Monitor.ShutdownAsync();
        _tray.Dispose();
        Shutdown();
    }

    internal async void TogglePause()
    {
        if (Monitor.Paused)
        {
            Monitor.StartAll();
        }
        else
        {
            await Monitor.PauseAsync();
        }

        _pauseItem.Text = Monitor.Paused ? "Resume monitoring" : "Pause monitoring";
        _main?.RefreshPauseState();
    }

    private WinForms.NotifyIcon CreateTrayIcon()
    {
        var menu = TrayIcon.CreateDarkMenu();
        menu.Items.Add("Open study", null, (_, _) => ShowMain());
        _ackItem = new WinForms.ToolStripMenuItem("Acknowledge all alarms", null, (_, _) => Monitor.AcknowledgeAll())
        {
            Enabled = false,
        };
        menu.Items.Add(_ackItem);
        _pauseItem = new WinForms.ToolStripMenuItem("Pause monitoring", null, (_, _) => TogglePause());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Log viewer", null, (_, _) => new LogViewerWindow().Show());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        var icon = new WinForms.NotifyIcon
        {
            Icon = BuildTrayIcon(alarm: false),
            Text = "study — PLC tag monitor",
            Visible = true,
            ContextMenuStrip = menu,
        };
        icon.MouseClick += (_, args) =>
        {
            if (args.Button == WinForms.MouseButtons.Left)
            {
                ShowMain();
            }
        };
        icon.BalloonTipClicked += (_, _) => ShowMain();
        return icon;
    }

    private void OnAlarmsChanged()
    {
        _ackItem.Enabled = Monitor.Ringing.Any();
        RefreshTrayIcon(force: false);
        _main?.RefreshAlarmBanner();
    }

    private void OnAlarmFired(string title, string message)
    {
        if (Settings.TrayNotifications)
        {
            _tray.ShowBalloonTip(8000, title, message, WinForms.ToolTipIcon.Warning);
        }
    }

    /// <summary>Tray icon turns red while anything is alarming.</summary>
    private void RefreshTrayIcon(bool force)
    {
        var alarm = Monitor.Ringing.Any();
        if (!force && alarm == _trayShowsAlarm)
        {
            return;
        }

        _trayShowsAlarm = alarm;
        var old = _tray.Icon;
        _tray.Icon = BuildTrayIcon(alarm);
        _tray.Text = alarm ? "study — ALARM" : "study — PLC tag monitor";
        old?.Dispose();
    }

    private static System.Drawing.Icon BuildTrayIcon(bool alarm) =>
        AppIcon.CreateIcon(alarm ? AppIcon.AlarmRed : ToDrawing(AccentTheme.Current),
            alarm ? System.Drawing.Color.White : ToDrawing(AccentTheme.OnAccent));

    private static System.Drawing.Color ToDrawing(System.Windows.Media.Color c) =>
        System.Drawing.Color.FromArgb(c.R, c.G, c.B);

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep monitoring alive: report, don't crash the tray app.
        Monitor?.Logger.Log("APP_ERROR", detail: e.Exception.ToString());
        MessageBox.Show($"Unexpected error:\n\n{e.Exception.Message}\n\nDetails were written to the log.", "study",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
