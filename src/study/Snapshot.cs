using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Study.Core.Models;
using Study.Core.Monitoring;
using Study.Core.Triggers;
using Study.ViewModels;

namespace Study;

/// <summary>
/// <c>study.exe --snapshot &lt;folder&gt;</c>: renders every window to PNG from inside the app
/// (screen grabs of WPF windows are unreliable) with a few demo watches, then exits.
/// Never starts monitoring, never logs, never saves — safe to run next to a live instance's config.
/// </summary>
internal static class Snapshot
{
    public static void Run(string folder)
    {
        Directory.CreateDirectory(folder);
        AddDemoWatches();

        var main = new MainWindow();
        Render(main, Path.Combine(folder, "1-main.png"));
        App.Monitor.Watches.First(w => w.IsRinging).Alarm = AlarmState.Ringing;
        main.RefreshAlarmBanner();
        Render(main, Path.Combine(folder, "1-main.png"));

        var demo = App.Monitor.Watches.Last().Definition;
        Render(new WatchEditorWindow(demo), Path.Combine(folder, "2-watch-editor.png"));
        Render(new PlcListWindow(), Path.Combine(folder, "3-plc-list.png"));
        Render(new SettingsWindow(), Path.Combine(folder, "4-settings.png"));
        Render(new LogViewerWindow(), Path.Combine(folder, "5-log-viewer.png"));
    }

    private static void AddDemoWatches()
    {
        var ip = App.Monitor.Plcs.FirstOrDefault()?.Ip ?? "10.0.0.1";
        var name = App.Monitor.Plcs.FirstOrDefault()?.Name ?? "PLC";

        var a = new WatchItem(new WatchDefinition { Name = "E-stop pressed", PlcIp = ip, Tag = "Estop_Active" }, name)
        {
            Status = WatchStatus.Connected, Value = 1, LastRead = DateTime.Now, Message = "OK", Alarm = AlarmState.Ringing,
        };
        var b = new WatchItem(new WatchDefinition
        {
            Name = "Jam counter high", PlcIp = ip, Tag = "Program:Main.JamCount", DataType = TagDataType.Dint,
            Mode = TriggerMode.Expression, Expression = "x > 100 && prev <= 100", PollMs = 500,
        }, name)
        {
            Status = WatchStatus.Connected, Value = 42, LastRead = DateTime.Now, Message = "OK",
        };
        var c = new WatchItem(new WatchDefinition { Name = "Merge fault", PlcIp = "10.137.16.99", Tag = "StatusWord.5", Mode = TriggerMode.WhileTrue }, "10.137.16.99")
        {
            Status = WatchStatus.Unreachable, Message = "No ping reply from 10.137.16.99",
        };
        App.Monitor.Watches.Add(a);
        App.Monitor.Watches.Add(b);
        App.Monitor.Watches.Add(c);
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
        var width = (int)Math.Ceiling(w.ActualWidth);
        var height = (int)Math.Ceiling(w.ActualHeight);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
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
