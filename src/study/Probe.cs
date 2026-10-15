using System.IO;
using System.Text;
using Study.Core.Models;
using Study.Core.Plc;
using Study.Services;

namespace Study;

/// <summary>
/// <c>study.exe --probe &lt;folder&gt;</c>: headless self-check for CI and for the packaged exe.
/// Writes probe.txt and exits with 0 (ok) or 1 (problem). Proves the built-in sounds were found and
/// that the native libplctag library loads, by asking 127.0.0.1 for its tag list: with no PLC
/// there, a PLC error (timeout / refused) is the expected, healthy result. Read-only by construction —
/// the browser only uses the @tags read service.
/// </summary>
internal static class Probe
{
    public static int Run(string folder)
    {
        var report = new StringBuilder();
        var ok = true;
        report.AppendLine($"study {typeof(Probe).Assembly.GetName().Version}");
        report.AppendLine($"exe:      {Environment.ProcessPath}");
        report.AppendLine($"base dir: {AppContext.BaseDirectory}");

        var sounds = AlarmPlayer.BuiltInSounds();
        report.AppendLine($"sounds:   {sounds.Count} ({string.Join(", ", sounds)})");
        if (sounds.Count == 0)
        {
            ok = false;
            report.AppendLine("FAIL: no built-in sounds found next to the exe");
        }

        try
        {
            Task.Run(() => new LibPlcTagBrowser().ListTagsAsync(
                    new PlcEntry { Name = "probe", Ip = "127.0.0.1" }, null, TimeSpan.FromSeconds(2), CancellationToken.None))
                .GetAwaiter().GetResult();
            report.AppendLine("libplctag: loaded (unexpected answer from 127.0.0.1)");
        }
        catch (PlcException ex)
        {
            report.AppendLine($"libplctag: loaded OK (no PLC at 127.0.0.1, as expected: {ex.Message})");
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException
                                       or EntryPointNotFoundException)
        {
            ok = false;
            report.AppendLine($"FAIL: libplctag native library did not load: {ex.GetType().Name}: {ex.Message}");
        }

        report.AppendLine(ok ? "RESULT: OK" : "RESULT: FAIL");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "probe.txt"), report.ToString());
        return ok ? 0 : 1;
    }
}
