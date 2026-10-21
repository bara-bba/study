using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Study.Native;

/// <summary>
/// Makes the taskbar button use the accent-coloured icon.
///
/// Windows draws a running app's taskbar button from the EXE's embedded icon (always the default
/// blue), not from the window icon. Giving the window its own AppUserModel identity with a
/// <c>RelaunchIconResource</c> pointing at an accent-coloured .ico is the documented way to
/// override that; the same properties make a pinned study start the same exe.
/// </summary>
internal static class TaskbarIdentity
{
    private const string AppId = "study.plcmonitor";

    private static readonly Guid AppUserModelFmtid = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");
    private const uint PidRelaunchCommand = 2;
    private const uint PidRelaunchIconResource = 3;
    private const uint PidRelaunchDisplayName = 4;
    private const uint PidId = 5;

    /// <summary>Applies (or refreshes after an accent change) the identity on <paramref name="window"/>.</summary>
    public static void Apply(Window window, byte[] icoBytes)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var exe = Environment.ProcessPath;
        if (hwnd == IntPtr.Zero || string.IsNullOrEmpty(exe))
        {
            return;
        }

        try
        {
            var icoPath = WriteIcon(icoBytes);
            var iid = typeof(IPropertyStore).GUID;
            Marshal.ThrowExceptionForHR(SHGetPropertyStoreForWindow(hwnd, ref iid, out var store));
            try
            {
                Set(store, PidId, AppId);
                Set(store, PidRelaunchCommand, $"\"{exe}\"");
                Set(store, PidRelaunchDisplayName, "study");
                Set(store, PidRelaunchIconResource, $"{icoPath},0");
                Marshal.ThrowExceptionForHR(store.Commit());
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
        {
            // Cosmetic only: the button keeps the exe's blue icon.
            System.Diagnostics.Debug.WriteLine($"Taskbar icon not applied: {ex.Message}");
        }
    }

    /// <summary>
    /// The icon file lives next to the config. Its name carries a hash of the bytes because the
    /// taskbar caches icons per path, so a new accent must be a new file.
    /// </summary>
    private static string WriteIcon(byte[] icoBytes)
    {
        var folder = Study.Core.Config.JsonStore.DefaultFolder;
        Directory.CreateDirectory(folder);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(icoBytes))[..12].ToLowerInvariant();
        var path = Path.Combine(folder, $"taskbar-{hash}.ico");
        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, icoBytes);
        }

        foreach (var old in Directory.EnumerateFiles(folder, "taskbar-*.ico"))
        {
            if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(old);
            }
        }

        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Set(IPropertyStore store, uint pid, string value)
    {
        var key = new PropertyKey(AppUserModelFmtid, pid);
        var pv = new PropVariant { vt = 31 /* VT_LPWSTR */, pointer = Marshal.StringToCoTaskMemUni(value) };
        try
        {
            Marshal.ThrowExceptionForHR(store.SetValue(ref key, ref pv));
        }
        finally
        {
            Marshal.FreeCoTaskMem(pv.pointer);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private readonly struct PropertyKey(Guid fmtid, uint pid)
    {
        public readonly Guid Fmtid = fmtid;
        public readonly uint Pid = pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)]
        public ushort vt;

        [FieldOffset(8)]
        public IntPtr pointer;
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint count);

        int GetAt(uint index, out PropertyKey key);

        int GetValue(ref PropertyKey key, out PropVariant value);

        int SetValue(ref PropertyKey key, ref PropVariant value);

        int Commit();
    }
}
