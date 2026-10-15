using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Study.Core.Logging;
using Study.Core.Models;
using Study.Core.Monitoring;
using Study.Core.Triggers;
using Study.ViewModels;

namespace Study;

/// <summary>
/// <c>study.exe --snapshot &lt;folder&gt; [--scale 2]</c>: renders every window to PNG from inside the
/// app (screen grabs of WPF windows are unreliable), then exits. Uses ONLY made-up demo data —
/// PLCs, watches, log entries and displayed paths — so the images are safe for a public README.
/// Never starts monitoring and never saves: the real config is loaded but not shown or touched.
/// </summary>
internal static class Snapshot
{
    private const string DemoConfigPath = @"%APPDATA%\study";
    private const string DemoLogPath = @"%USERPROFILE%\Documents\study\logs";

    private static readonly PlcEntry[] DemoPlcs =
    [
        new() { Name = "Merge_01", Ip = "192.168.10.11", Group = "Line 1/Conveyors", Source = "RSLinx", Notes = "5069-L330ERS2" },
        new() { Name = "Merge_02", Ip = "192.168.10.12", Group = "Line 1/Conveyors", Source = "RSLinx", Notes = "5069-L330ERS2" },
        new() { Name = "Sorter_01", Ip = "192.168.10.21", Group = "Line 1/Sortation", Source = "RSLinx", Notes = "5069-L350ERS2" },
        new() { Name = "Induct_01", Ip = "192.168.10.22", Group = "Line 1/Sortation", Source = "CSV", Notes = "5069-L330ERS2" },
        new() { Name = "Induct_02", Ip = "192.168.10.23", Group = "Line 1/Sortation", Source = "CSV", Notes = "5069-L330ERS2" },
        new() { Name = "Palletizer_01", Ip = "192.168.20.11", Group = "Line 2/Palletizing", Source = "RSLinx", Notes = "1769-L36ERMS" },
        new() { Name = "Palletizer_02", Ip = "192.168.20.12", Group = "Line 2/Palletizing", Source = "RSLinx", Notes = "1769-L36ERMS" },
        new() { Name = "Stacker_1", Ip = "192.168.20.21", Group = "Line 2/Stackers", Source = "RSLinx", Notes = "5069-L306ERS2" },
        new() { Name = "Stacker_2", Ip = "192.168.20.22", Group = "Line 2/Stackers", Source = "RSLinx", Notes = "5069-L306ERS2" },
        new() { Name = "Stacker_10", Ip = "192.168.20.30", Group = "Line 2/Stackers", Source = "RSLinx", Notes = "5069-L306ERS2" },
        new() { Name = "Wrapper", Ip = "192.168.30.5", Group = "Utilities", Source = "Manual", Notes = "1769-L33ER" },
        new() { Name = "Compressor_PLC", Ip = "192.168.30.6", Group = "Utilities", Source = "Manual", Notes = "1769-L33ER" },
    ];

    private static double _scale = 1;

    public static void Run(string folder, double scale)
    {
        _scale = scale <= 0 ? 1 : scale;
        Directory.CreateDirectory(folder);
        App.Monitor.UseInMemoryPlcs(DemoPlcs);
        App.Monitor.Watches.Clear(); // in memory only: watches are saved by Upsert/Delete, never here
        AddDemoWatches();

        var main = new MainWindow();
        App.Monitor.Watches.First(w => w.IsRinging).Alarm = AlarmState.Ringing;
        main.RefreshAlarmBanner();
        Render(main, Path.Combine(folder, "1-main.png"));

        var editor = new WatchEditorWindow(App.Monitor.Watches[1].Definition);
        Render(editor, Path.Combine(folder, "2-watch-editor.png"));

        var plcs = new PlcListWindow();
        plcs.Select("192.168.10.21");
        Render(plcs, Path.Combine(folder, "3-plc-list.png"));

        var settings = new SettingsWindow();
        settings.ConfigPathText.Text = $"Saved in {DemoConfigPath}";
        settings.LogFolderText.Text = DemoLogPath;
        Render(settings, Path.Combine(folder, "4-settings.png"));

        var logs = Path.Combine(Path.GetTempPath(), $"study-demo-logs-{Environment.ProcessId}");
        try
        {
            WriteDemoLog(logs);
            var viewer = new LogViewerWindow(logs);
            viewer.FolderText.Text = DemoLogPath;
            Render(viewer, Path.Combine(folder, "5-log-viewer.png"));
        }
        finally
        {
            Directory.Delete(logs, recursive: true);
        }
    }

    private static void AddDemoWatches()
    {
        WatchItem Item(WatchDefinition d, string plc) => new(d, plc);
        var now = DateTime.Now;

        var a = Item(new WatchDefinition { Name = "E-stop pressed", PlcIp = "192.168.10.21", Tag = "Estop_Active" }, "Sorter_01");
        a.Status = WatchStatus.Connected; a.Value = 1; a.LastRead = now; a.Message = "OK"; a.Alarm = AlarmState.Ringing;

        var b = Item(new WatchDefinition
        {
            Name = "Jam counter high", PlcIp = "192.168.10.11", Tag = "Program:Main.JamCount", DataType = TagDataType.Dint,
            Mode = TriggerMode.Expression, Expression = "x > 100 && prev <= 100", PollMs = 500,
        }, "Merge_01");
        b.Status = WatchStatus.Connected; b.Value = 42; b.LastRead = now; b.Message = "OK";

        var c = Item(new WatchDefinition
        {
            Name = "Pallet released", PlcIp = "192.168.20.11", Tag = "Release_PB", Mode = TriggerMode.FallingEdge, PollMs = 250,
        }, "Palletizer_01");
        c.Status = WatchStatus.Connected; c.Value = 1; c.LastRead = now; c.Message = "OK";

        var d = Item(new WatchDefinition
        {
            Name = "Wrapper film low", PlcIp = "192.168.30.5", Tag = "Status.5", Mode = TriggerMode.WhileTrue,
        }, "Wrapper");
        d.Status = WatchStatus.Unreachable; d.Message = "No ping reply from 192.168.30.5";

        foreach (var w in new[] { a, b, c, d })
        {
            App.Monitor.Watches.Add(w);
        }
    }

    private static void WriteDemoLog(string folder)
    {
        var t = DateTime.Today.AddHours(6);
        var log = new EventLogger(folder, () => t);
        void At(int minutes, string evt, string watch = "", string plc = "", string ip = "", string tag = "",
            string value = "", string detail = "")
        {
            t = DateTime.Today.AddHours(6).AddMinutes(minutes).AddMilliseconds(minutes * 37 % 1000);
            log.Log(evt, watch, plc, ip, tag, value, detail);
        }

        At(0, LogEvents.Started, detail: "4 watch(es)");
        At(1, LogEvents.Connected, "E-stop pressed", "Sorter_01", "192.168.10.21", "Estop_Active", "FALSE", "slot 0");
        At(1, LogEvents.Connected, "Jam counter high", "Merge_01", "192.168.10.11", "Program:Main.JamCount", "12", "slot 0");
        At(2, LogEvents.Connected, "Pallet released", "Palletizer_01", "192.168.20.11", "Release_PB", "TRUE", "slot 0");
        At(2, LogEvents.Unreachable, "Wrapper film low", "Wrapper", "192.168.30.5", "Status.5", "", "No ping reply from 192.168.30.5");
        At(47, LogEvents.Trigger, "Jam counter high", "Merge_01", "192.168.10.11", "Program:Main.JamCount", "101", "Expression: x > 100 && prev <= 100");
        At(48, LogEvents.Ack, "Jam counter high", "Merge_01", "192.168.10.11", "Program:Main.JamCount", "103", "Acknowledged; condition still active");
        At(95, LogEvents.Trigger, "Pallet released", "Palletizer_01", "192.168.20.11", "Release_PB", "FALSE", "Falling edge (true → false)");
        At(96, LogEvents.Ack, "Pallet released", "Palletizer_01", "192.168.20.11", "Release_PB", "TRUE", "Acknowledged");
        At(180, LogEvents.Trigger, "E-stop pressed", "Sorter_01", "192.168.10.21", "Estop_Active", "TRUE", "Rising edge (false → true)");
    }

    private static void Render(Window w, string path)
    {
        w.ShowInTaskbar = false;
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -20000; // off-screen: rendering only
        w.Top = -20000;
        w.Show();
        DoEvents();

        var content = (FrameworkElement)w.Content;
        var width = Math.Ceiling(w.ActualWidth);
        var height = Math.Ceiling(w.ActualHeight);
        // Rendering at a higher DPI rasterises the vector UI sharper (2 = crisp on HiDPI / GitHub).
        var rtb = new RenderTargetBitmap((int)(width * _scale), (int)(height * _scale), 96 * _scale, 96 * _scale,
            PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, width, height));
        }

        rtb.Render(bg);
        rtb.Render(content);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(path))
        {
            enc.Save(fs);
        }

        if (w is not MainWindow)
        {
            w.Close();
        }
    }

    private static void DoEvents()
    {
        for (var i = 0; i < 5; i++)
        {
            Application.Current.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, () => { });
            Thread.Sleep(60);
        }
    }
}
