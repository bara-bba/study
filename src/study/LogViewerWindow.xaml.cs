using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using Study.Core.Logging;
using Study.Native;

namespace Study;

/// <summary>
/// Browses the CSV logs in any folder (defaults to study's log folder). Select one or more
/// day files, filter by text / event; "Live" re-reads the selection every few seconds.
/// </summary>
public partial class LogViewerWindow : Window
{
    private const string AllEvents = "All events";

    private readonly DispatcherTimer _live = new() { Interval = TimeSpan.FromSeconds(3) };
    private string _folder;
    private List<LogEntry> _entries = [];

    public LogViewerWindow()
    {
        InitializeComponent();
        DwmHelper.ApplyPowerToysChrome(this);
        Icon = App.CurrentIconSource();
        _folder = App.Settings.LogFolder;
        EventCombo.ItemsSource = new[] { AllEvents }.Concat(LogEvents.All).ToList();
        EventCombo.SelectedIndex = 0;
        _live.Tick += (_, _) =>
        {
            if (AutoRefreshCheck.IsChecked == true && IsActive)
            {
                LoadSelected(keepScroll: true);
            }
        };
        _live.Start();
        Closed += (_, _) => _live.Stop();
        LoadFolder();
    }

    private sealed record FileRow(string Name, string Info, string Path);

    private void LoadFolder()
    {
        FolderText.Text = _folder;
        var files = LogReader.ListFiles(_folder)
            .Select(f => new FileRow(f.Name, $"{f.LastWriteTime:yyyy-MM-dd HH:mm}  ·  {f.Length / 1024.0:0.#} KB", f.FullName))
            .ToList();
        FileList.ItemsSource = files;
        if (files.Count > 0)
        {
            FileList.SelectedIndex = 0; // newest first
        }
        else
        {
            _entries = [];
            ApplyFilter();
            StatusText.Text = Directory.Exists(_folder)
                ? "No .csv files in this folder yet."
                : "Folder does not exist yet — it is created on the first logged event.";
        }
    }

    private void LoadSelected(bool keepScroll = false)
    {
        var rows = FileList.SelectedItems.Cast<FileRow>().ToList();
        var all = new List<LogEntry>();
        var problems = new List<string>();
        foreach (var r in rows)
        {
            try
            {
                all.AddRange(LogReader.Read(r.Path));
            }
            catch (IOException ex)
            {
                problems.Add($"{r.Name}: {ex.Message}");
            }
        }

        _entries = all.OrderByDescending(e => e.Timestamp).ToList();
        ApplyFilter(keepScroll);
        if (problems.Count > 0)
        {
            StatusText.Text += "   ✗ " + string.Join("; ", problems);
        }
    }

    private void ApplyFilter(bool keepScroll = false)
    {
        var text = SearchBox.Text.Trim();
        var evt = EventCombo.SelectedItem as string;
        IEnumerable<LogEntry> view = _entries;
        if (evt is not null && evt != AllEvents)
        {
            view = view.Where(e => e.Event == evt);
        }

        if (text.Length > 0)
        {
            view = view.Where(e =>
                Contains(e.Watch, text) || Contains(e.Plc, text) || Contains(e.Ip, text)
                || Contains(e.Tag, text) || Contains(e.Value, text) || Contains(e.Detail, text));
        }

        var list = view.ToList();
        var selected = EntryGrid.SelectedItem;
        EntryGrid.ItemsSource = list;
        if (keepScroll && selected is not null)
        {
            EntryGrid.SelectedItem = selected;
        }

        var triggers = list.Count(e => e.Event == LogEvents.Trigger);
        StatusText.Text = $"{list.Count} of {_entries.Count} entries  ·  {triggers} trigger(s)  ·  newest first";
    }

    private static bool Contains(string field, string text) => field.Contains(text, StringComparison.OrdinalIgnoreCase);

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadSelected();

    private void Filter_Changed(object sender, EventArgs e)
    {
        if (IsLoaded)
        {
            ApplyFilter();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => LoadFolder();

    private void ChangeFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Folder with study logs", InitialDirectory = Directory.Exists(_folder) ? _folder : "" };
        if (dlg.ShowDialog(this) == true)
        {
            _folder = dlg.FolderName;
            LoadFolder();
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(_folder))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_folder}\"") { UseShellExecute = true });
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
