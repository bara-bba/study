using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Study.Core.Models;
using Study.Core.Plc;
using Study.Native;

namespace Study;

/// <summary>
/// Lists controller / program tags (read-only <c>@tags</c>) and opens structures (UDTs, AOIs,
/// timers…) into their members (read-only <c>@udt/&lt;id&gt;</c>), so a member like
/// <c>Motor1.Fault</c> or <c>Conv_TMR.DN</c> can be picked instead of typed.
/// </summary>
public partial class TagBrowserWindow : Window
{
    private static readonly TimeSpan BrowseTimeout = TimeSpan.FromSeconds(10);

    private readonly PlcEntry _plc;
    private readonly ITagBrowser _browser = new LibPlcTagBrowser();

    /// <summary>Where we are: the controller (bottom), then programs / structures opened from it.</summary>
    private readonly Stack<Level> _levels = new();
    private IReadOnlyList<BrowsedTag> _all = [];

    private sealed record Level(string Title, Func<Task<IReadOnlyList<BrowsedTag>>> Load);

    public TagBrowserWindow(PlcEntry plc, string? startAt = null)
    {
        InitializeComponent();
        DwmHelper.ApplyPowerToysChrome(this);
        Icon = App.CurrentIconSource();
        _plc = plc;
        Loaded += async (_, _) =>
        {
            await OpenAsync(new Level("Controller scope", () =>
                _browser.ListTagsAsync(_plc, null, BrowseTimeout, CancellationToken.None)));
            await GoToAsync(startAt);
        };
    }

    /// <summary>
    /// Starts where the Tag box points: "Motor1" or "Motor1." opens that structure,
    /// "Motor1.Timer.DN" opens Motor1 → Timer and selects DN; anything else becomes the filter.
    /// </summary>
    private async Task GoToAsync(string? startAt)
    {
        var text = (startAt ?? "").Trim();
        if (text.Length == 0 || text.StartsWith("Program:", StringComparison.OrdinalIgnoreCase))
        {
            FilterBox.Text = text;
            return;
        }

        var parts = text.TrimEnd('.').Split('.');
        var path = "";
        foreach (var part in parts)
        {
            path = path.Length == 0 ? part : $"{path}.{part}";
            var match = _all.FirstOrDefault(t => string.Equals(t.Name, path, StringComparison.OrdinalIgnoreCase));
            if (match is { IsStruct: true })
            {
                await ActivateAsync(match);
                continue;
            }

            if (match is not null)
            {
                TagList.SelectedItem = match;
                TagList.ScrollIntoView(match);
            }
            else if (_levels.Count == 1)
            {
                FilterBox.Text = text; // not a known struct: just filter the controller list
            }

            return;
        }
    }

    public BrowsedTag? Selected { get; private set; }

    private async Task OpenAsync(Level level)
    {
        _levels.Push(level);
        FilterBox.Text = "";
        await ShowCurrentAsync();
    }

    private async Task ShowCurrentAsync()
    {
        var level = _levels.Peek();
        BackButton.Visibility = _levels.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        ScopeText.Text = $"{_plc.DisplayName}  ·  slot {_plc.Slot}  ·  {level.Title}";
        StatusText.Text = "Reading…";
        TagList.ItemsSource = null;
        PickButton.IsEnabled = false;
        try
        {
            // Hide system/module-defined internals (names containing "__").
            _all = (await level.Load()).Where(t => !t.Name.Contains("__", StringComparison.Ordinal)).ToList();
            ApplyFilter();
            StatusText.Text = $"{_all.Count} item(s). Double-click a PROGRAM or a UDT/struct ( › ) to open it; "
                              + "double-click a BOOL, SINT, INT, DINT, LINT or REAL to pick it.";
        }
        catch (PlcException ex)
        {
            StatusText.Text = "✗ " + ex.Message;
        }
    }

    private void ApplyFilter()
    {
        var f = FilterBox.Text.Trim();
        TagList.ItemsSource = string.IsNullOrEmpty(f)
            ? _all
            : _all.Where(t => t.Name.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void TagList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        PickButton.IsEnabled = TagList.SelectedItem is BrowsedTag { IsWatchable: true };

    private async void TagList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TagList.SelectedItem is BrowsedTag tag)
        {
            await ActivateAsync(tag);
        }
    }

    private async void TagList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && TagList.SelectedItem is BrowsedTag tag)
        {
            e.Handled = true;
            await ActivateAsync(tag);
        }
        else if (e.Key == Key.Back && _levels.Count > 1)
        {
            e.Handled = true;
            await BackAsync();
        }
    }

    private async Task ActivateAsync(BrowsedTag tag)
    {
        if (tag.IsProgram)
        {
            await OpenAsync(new Level(tag.Name, () =>
                _browser.ListTagsAsync(_plc, tag.Name, BrowseTimeout, CancellationToken.None)));
        }
        else if (tag.IsStruct)
        {
            await OpenAsync(new Level(tag.Name, () =>
                _browser.ListMembersAsync(_plc, tag.Name, tag.UdtId!.Value, BrowseTimeout, CancellationToken.None)));
        }
        else if (tag.IsWatchable)
        {
            Pick(tag);
        }
        else
        {
            StatusText.Text = $"'{tag.Name}' is {tag.TypeLabel}: arrays and this type can't be watched whole. "
                              + "Type one element in the Tag box instead, e.g. MyArray[3].";
        }
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        if (TagList.SelectedItem is BrowsedTag { IsWatchable: true } tag)
        {
            Pick(tag);
        }
    }

    private void Pick(BrowsedTag tag)
    {
        Selected = tag;
        DialogResult = true;
    }

    private async void Back_Click(object sender, RoutedEventArgs e) => await BackAsync();

    private async Task BackAsync()
    {
        if (_levels.Count <= 1)
        {
            return;
        }

        _levels.Pop();
        FilterBox.Text = "";
        await ShowCurrentAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not TextBox)
        {
            DragMove();
        }
    }
}
