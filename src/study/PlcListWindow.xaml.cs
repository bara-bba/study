using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using Study.Core.Config;
using Study.Core.Models;
using Study.Core.Monitoring;
using Study.Core.Plc;
using Study.Native;
using Study.Services;
using Study.ViewModels;

namespace Study;

/// <summary>
/// Edits a working copy of the PLC list, shown as a folder tree (A→Z); nothing is persisted
/// until "Save list". Folders are just each PLC's Group path, so a folder exists while it holds PLCs.
/// </summary>
public partial class PlcListWindow : Window
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DetectTimeout = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, string> _ipRenames = new();
    private readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
    private List<PlcEntry> _plcs;
    private List<PlcTreeNode> _roots = [];
    private bool _dirty;
    private bool _filtering;

    public PlcListWindow()
    {
        InitializeComponent();
        DwmHelper.ApplyPowerToysChrome(this);
        Icon = App.CurrentIconSource();
        PlcTreeView.AddHandler(TreeViewItem.SelectedEvent,
            new RoutedEventHandler((_, e) => (e.OriginalSource as TreeViewItem)?.BringIntoView()));

        _plcs = PlcTree.Sort(App.Monitor.Plcs);
        _expanded.UnionWith(PlcTree.AllGroups(_plcs)); // start fully expanded
        RefreshTree();
        StatusText.Text = _plcs.Count == 0
            ? "Empty. Start with 'Import from RSLinx', then 'Import…' a CSV or JSON to add names and folders."
            : CountSummary();
    }

    private PlcEntry? SelectedPlc => (PlcTreeView.SelectedItem as PlcNode)?.Entry;

    /// <summary>Snapshot mode: show the window with one PLC selected.</summary>
    internal void Select(string ip) => RefreshTree(selectIp: ip);

    private FolderNode? SelectedFolder => PlcTreeView.SelectedItem as FolderNode;

    private string CountSummary() =>
        $"{_plcs.Count} PLC(s) in {PlcTree.AllGroups(_plcs).Count} folder(s).";

    // ---------------------------------------------------------------- tree

    private void RefreshTree(string? selectIp = null, string? selectFolder = null)
    {
        var f = FilterBox.Text.Trim();
        _filtering = f.Length > 0;
        var view = _filtering
            ? _plcs.Where(p => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                               || p.Ip.Contains(f, StringComparison.Ordinal)
                               || p.Group.Contains(f, StringComparison.OrdinalIgnoreCase)
                               || p.Notes.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList()
            : _plcs;

        // A filtered tree is shown fully open so every hit is visible; it does not touch _expanded.
        _roots = PlcTreeBuilder.Build(view, path => _filtering || _expanded.Contains(path));
        foreach (var folder in Folders(_roots))
        {
            folder.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlcTreeNode.IsExpanded) && !_filtering)
                {
                    if (folder.IsExpanded)
                    {
                        _expanded.Add(folder.Path);
                    }
                    else
                    {
                        _expanded.Remove(folder.Path);
                    }
                }
            };
        }

        PlcTreeView.ItemsSource = _roots;

        PlcTreeNode? target = selectIp is not null
            ? AllNodes(_roots).OfType<PlcNode>().FirstOrDefault(n => n.Entry.Ip == selectIp)
            : selectFolder is not null
                ? Folders(_roots).FirstOrDefault(n => string.Equals(n.Path, selectFolder, StringComparison.OrdinalIgnoreCase))
                : null;
        if (target is not null)
        {
            target.IsSelected = true;
        }
        else
        {
            ShowSelection();
        }
    }

    private static IEnumerable<PlcTreeNode> AllNodes(IEnumerable<PlcTreeNode> nodes)
    {
        foreach (var n in nodes)
        {
            yield return n;
            foreach (var c in AllNodes(n.Children))
            {
                yield return c;
            }
        }
    }

    private static IEnumerable<FolderNode> Folders(IEnumerable<PlcTreeNode> nodes) => AllNodes(nodes).OfType<FolderNode>();

    /// <summary>Opens every parent folder of <paramref name="group"/> so the item is visible after a rebuild.</summary>
    private void ExpandPath(string group)
    {
        var parts = PlcTree.NormalizeGroup(group).Split(PlcTree.Separator, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 1; i <= parts.Length; i++)
        {
            _expanded.Add(string.Join(PlcTree.Separator, parts.Take(i)));
        }
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => RefreshTree();

    private void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in Folders(_roots))
        {
            f.IsExpanded = true;
        }
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in Folders(_roots))
        {
            f.IsExpanded = false;
        }
    }

    private void PlcTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e) => ShowSelection();

    private void ShowSelection()
    {
        if (SelectedFolder is { } folder)
        {
            DetailPanel.Visibility = Visibility.Collapsed;
            FolderPanel.Visibility = Visibility.Visible;
            FolderPathBox.Text = folder.Path;
            FolderStatus.Text = $"{folder.CountText} in this folder and its sub-folders.";
            return;
        }

        var p = SelectedPlc;
        FolderPanel.Visibility = Visibility.Collapsed;
        DetailPanel.Visibility = Visibility.Visible;
        DetailPanel.IsEnabled = p is not null;
        NameBox.Text = p?.Name ?? "";
        IpBox.Text = p?.Ip ?? "";
        SlotBox.Text = p?.Slot.ToString() ?? "";
        GroupBox.Text = p?.Group ?? "";
        NotesBox.Text = p?.Notes ?? "";
        DetailStatus.Text = "";
    }

    private void ClearSelection()
    {
        foreach (var n in AllNodes(_roots).Where(n => n.IsSelected))
        {
            n.IsSelected = false;
        }

        ShowSelection();
    }

    // ---------------------------------------------------------------- import / export

    private void ApplyImport(ImportResult result, string what)
    {
        var known = PlcTree.AllGroups(_plcs).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _plcs = result.Plcs.ToList();
        _expanded.UnionWith(PlcTree.AllGroups(_plcs).Where(g => !known.Contains(g))); // show new folders open
        _dirty = true;
        RefreshTree();
        StatusText.Text = $"{what}: {result.Added} added, {result.Updated} updated"
                          + (result.Skipped.Count > 0 ? $", {result.Skipped.Count} skipped" : "")
                          + $". {_plcs.Count} PLC(s) — click 'Save list' to keep.";
        if (result.Skipped.Count > 0)
        {
            MessageBox.Show(this, "Skipped rows:\n\n" + string.Join("\n", result.Skipped.Take(30))
                                  + (result.Skipped.Count > 30 ? $"\n… and {result.Skipped.Count - 30} more" : ""),
                "study — import", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void ImportRsLinx_Click(object sender, RoutedEventArgs e)
    {
        List<(string Driver, string Value)> nodes;
        try
        {
            nodes = RsLinxImporter.ReadNodeTables();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            StatusText.Text = $"Could not read the RSLinx configuration: {ex.Message}. Use 'Import…' instead.";
            return;
        }

        if (nodes.Count == 0)
        {
            StatusText.Text = "No RSLinx Ethernet (AB_ETH) driver entries found on this PC. Use 'Import…' instead.";
            return;
        }

        var (plcs, skipped) = PlcList.FromRsLinx(nodes);
        ApplyImport(PlcList.Merge(_plcs, plcs, skipped), "RSLinx");
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "PLC list (*.json;*.csv)|*.json;*.csv|JSON tree (*.json)|*.json|CSV (*.csv)|*.csv|All files (*.*)|*.*",
            Title = "Import PLC list",
        };
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        var name = Path.GetFileName(dlg.FileName);
        try
        {
            if (IsJson(dlg.FileName))
            {
                ApplyImport(PlcList.Merge(_plcs, PlcTree.Parse(File.ReadAllText(dlg.FileName))), name);
            }
            else
            {
                var (plcs, skipped) = PlcList.ParseCsv(File.ReadAllLines(dlg.FileName));
                ApplyImport(PlcList.Merge(_plcs, plcs, skipped), name);
            }
        }
        catch (IOException ex)
        {
            StatusText.Text = $"Could not read {dlg.FileName}: {ex.Message}";
        }
        catch (JsonException ex)
        {
            StatusText.Text = $"{name} is not a valid PLC list: {ex.Message}";
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "JSON tree (*.json)|*.json|CSV (*.csv)|*.csv",
            FileName = "study-plcs.json",
            Title = "Export PLC list",
        };
        if (dlg.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            if (IsJson(dlg.FileName))
            {
                File.WriteAllText(dlg.FileName, PlcTree.Serialize(_plcs));
            }
            else
            {
                File.WriteAllLines(dlg.FileName, PlcList.ToCsv(_plcs));
            }

            StatusText.Text = $"Exported {_plcs.Count} PLC(s) to {dlg.FileName}";
        }
        catch (IOException ex)
        {
            StatusText.Text = $"Could not write {dlg.FileName}: {ex.Message}";
        }
    }

    private static bool IsJson(string path) => string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);

    private void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        var file = App.Store.PathFor(PlcList.FileName);
        var args = File.Exists(file) ? $"/select,\"{file}\"" : $"\"{App.Store.Folder}\"";
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
            StatusText.Text = "plcs.json can be edited by hand (restart study afterwards). Unsaved changes here are not in it yet.";
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            StatusText.Text = $"Could not open Explorer: {ex.Message}";
        }
    }

    // ---------------------------------------------------------------- edit

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var folder = SelectedFolder?.Path ?? SelectedPlc?.Group ?? "";
        ClearSelection();
        DetailPanel.IsEnabled = true;
        NameBox.Text = "";
        IpBox.Text = "";
        SlotBox.Text = "0";
        GroupBox.Text = folder;
        NotesBox.Text = "";
        DetailStatus.Text = "Fill in the fields and click Apply.";
        NameBox.Focus();
    }

    private void PickGroup_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = PickGroupButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var top = new MenuItem { Header = "(top level)" };
        top.Click += (_, _) => GroupBox.Text = "";
        menu.Items.Add(top);
        foreach (var g in PlcTree.AllGroups(_plcs))
        {
            var depth = g.Count(c => c == PlcTree.Separator);
            var item = new MenuItem { Header = new string(' ', depth * 4) + g[(g.LastIndexOf(PlcTree.Separator) + 1)..], Tag = g, ToolTip = g };
            item.Click += (_, _) => GroupBox.Text = (string)item.Tag;
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    /// <summary>Validates the detail form. Returns null and shows why when invalid.</summary>
    private PlcEntry? ReadForm()
    {
        var ip = IpBox.Text.Trim();
        if (!PlcList.IsValidIPv4(ip))
        {
            DetailStatus.Text = "✗ Enter a valid IPv4 address, e.g. 192.168.1.10";
            return null;
        }

        if (!int.TryParse(SlotBox.Text.Trim(), out var slot) || slot is < 0 or > SlotDetector.MaxSlot)
        {
            DetailStatus.Text = $"✗ Slot must be a number from 0 to {SlotDetector.MaxSlot}.";
            return null;
        }

        return new PlcEntry
        {
            Name = NameBox.Text.Trim(),
            Ip = ip,
            Slot = slot,
            Group = PlcTree.NormalizeGroup(GroupBox.Text),
            Notes = NotesBox.Text.Trim(),
            Source = SelectedPlc?.Source ?? "Manual",
        };
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is not { } entry)
        {
            return;
        }

        var current = SelectedPlc;
        if (_plcs.Any(p => p.Ip == entry.Ip && p.Ip != current?.Ip))
        {
            DetailStatus.Text = $"✗ {entry.Ip} is already in the list.";
            return;
        }

        if (current is null)
        {
            _plcs.Add(entry);
        }
        else
        {
            _plcs[_plcs.FindIndex(p => p.Ip == current.Ip)] = entry;
            if (current.Ip != entry.Ip)
            {
                // Watches follow the PLC to its new IP, applied on "Save list".
                _ipRenames[current.Ip] = entry.Ip;
            }
        }

        _plcs = PlcTree.Sort(_plcs);
        _dirty = true;
        ExpandPath(entry.Group);
        RefreshTree(selectIp: entry.Ip);
        DetailStatus.Text = "✓ Applied. Click 'Save list' to keep.";
    }

    private void RenameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFolder is not { } folder)
        {
            return;
        }

        var target = PlcTree.NormalizeGroup(FolderPathBox.Text);
        if (string.Equals(target, folder.Path, StringComparison.Ordinal))
        {
            FolderStatus.Text = "Same path — nothing to change.";
            return;
        }

        if (target.Length > 0 && PlcTree.IsInFolder(target, folder.Path)
            && !string.Equals(target, folder.Path, StringComparison.OrdinalIgnoreCase))
        {
            FolderStatus.Text = "✗ A folder cannot be moved inside itself.";
            return;
        }

        _plcs = PlcTree.RenameFolder(_plcs, folder.Path, target);
        _dirty = true;
        ExpandPath(target);
        RefreshTree(selectFolder: target.Length > 0 ? target : null);
        StatusText.Text = target.Length > 0
            ? $"Folder '{folder.Path}' → '{target}'. Click 'Save list' to keep."
            : $"PLCs of '{folder.Path}' moved to the top level. Click 'Save list' to keep.";
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlc is not { } p)
        {
            return;
        }

        var used = App.Monitor.Watches.Count(w => w.Definition.PlcIp == p.Ip);
        var msg = used > 0
            ? $"Remove {p.DisplayName}? {used} watch(es) use it and will show as unknown PLC until you edit them."
            : $"Remove {p.DisplayName}?";
        if (MessageBox.Show(this, msg, "study", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _plcs.RemoveAll(x => x.Ip == p.Ip);
            _dirty = true;
            RefreshTree(selectFolder: p.Group.Length > 0 ? p.Group : null);
            StatusText.Text = CountSummary();
        }
    }

    // ---------------------------------------------------------------- PLC checks (read-only)

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is not { } plc)
        {
            return;
        }

        TestButton.IsEnabled = false;
        try
        {
            DetailStatus.Text = $"Pinging {plc.Ip}…";
            var pingOk = await new IcmpPinger().PingAsync(plc.Ip, WatchRunner.PingTimeout, CancellationToken.None);
            if (!pingOk && !App.Settings.SkipPing)
            {
                DetailStatus.Text = $"✗ No ping reply from {plc.Ip}.";
                return;
            }

            DetailStatus.Text = $"Ping {(pingOk ? "OK" : "failed (ignored: Skip ping is on)")}. Reading tag list on slot {plc.Slot}…";
            var tags = await new LibPlcTagBrowser().ListTagsAsync(plc, null, TestTimeout, CancellationToken.None);
            DetailStatus.Text = $"✓ Connected on slot {plc.Slot}: {tags.Count} controller-scope tag(s).";
        }
        catch (PlcException ex)
        {
            DetailStatus.Text = "✗ " + ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private async void Detect_Click(object sender, RoutedEventArgs e)
    {
        if (ReadForm() is not { } plc)
        {
            return;
        }

        DetectButton.IsEnabled = false;
        try
        {
            var progress = new Progress<int>(s => DetailStatus.Text = $"Trying slot {s}…");
            var slot = await SlotDetector.DetectAsync(new LibPlcTagBrowser(), plc, DetectTimeout, progress, CancellationToken.None);
            if (slot is { } found)
            {
                SlotBox.Text = found.ToString();
                DetailStatus.Text = $"✓ CPU answered on slot {found}. Click Apply, then Save list.";
            }
            else
            {
                DetailStatus.Text = $"✗ No slot 0–{SlotDetector.MaxSlot} answered. Check the IP / network (Test).";
            }
        }
        finally
        {
            DetectButton.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------- save / close

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Monitor.SavePlcs(_plcs);
            foreach (var (oldIp, newIp) in _ipRenames)
            {
                foreach (var w in App.Monitor.Watches.Where(w => w.Definition.PlcIp == oldIp).ToList())
                {
                    App.Monitor.Upsert(w.Definition with { PlcIp = newIp });
                }
            }

            _dirty = false;
            DialogResult = true;
        }
        catch (IOException ex)
        {
            StatusText.Text = $"Could not save: {ex.Message}";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_dirty && MessageBox.Show(this, "Discard the changes to the PLC list?", "study", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        DialogResult = false;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not TextBox)
        {
            DragMove();
        }
    }
}
