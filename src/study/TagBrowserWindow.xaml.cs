using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Study.Core.Models;
using Study.Core.Plc;
using Study.Native;

namespace Study;

/// <summary>Lists controller (and program) tags using the read-only @tags service.</summary>
public partial class TagBrowserWindow : Window
{
    private static readonly TimeSpan BrowseTimeout = TimeSpan.FromSeconds(10);

    private readonly PlcEntry _plc;
    private readonly ITagBrowser _browser = new LibPlcTagBrowser();
    private IReadOnlyList<BrowsedTag> _all = [];
    private string? _program;

    public TagBrowserWindow(PlcEntry plc)
    {
        InitializeComponent();
        DwmHelper.ApplyPowerToysChrome(this);
        Icon = App.CurrentIconSource();
        _plc = plc;
        Loaded += async (_, _) => await LoadAsync(null);
    }

    public BrowsedTag? Selected { get; private set; }

    private async Task LoadAsync(string? program)
    {
        _program = program;
        BackButton.Visibility = program is null ? Visibility.Collapsed : Visibility.Visible;
        ScopeText.Text = $"{_plc.DisplayName}  ·  slot {_plc.Slot}  ·  " + (program ?? "Controller scope");
        StatusText.Text = "Reading tag list…";
        TagList.ItemsSource = null;
        try
        {
            _all = await _browser.ListTagsAsync(_plc, program, BrowseTimeout, CancellationToken.None);
            // Hide system/module-defined internals (names starting with "__").
            _all = _all.Where(t => !t.Name.Contains("__", StringComparison.Ordinal)).ToList();
            ApplyFilter();
            StatusText.Text = $"{_all.Count} tag(s). Double-click a PROGRAM to see its tags; double-click a tag to pick it. "
                              + "Only atomic types (BOOL, SINT, INT, DINT, LINT, REAL) can be watched — for UDT members type the full name, e.g. Motor1.Fault.";
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
        PickButton.IsEnabled = TagList.SelectedItem is BrowsedTag { IsProgram: false };

    private async void TagList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TagList.SelectedItem is not BrowsedTag tag)
        {
            return;
        }

        if (tag.IsProgram)
        {
            FilterBox.Text = "";
            await LoadAsync(tag.Name);
        }
        else
        {
            Pick(tag);
        }
    }

    private void Pick_Click(object sender, RoutedEventArgs e)
    {
        if (TagList.SelectedItem is BrowsedTag tag)
        {
            Pick(tag);
        }
    }

    private void Pick(BrowsedTag tag)
    {
        Selected = tag;
        DialogResult = true;
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        FilterBox.Text = "";
        await LoadAsync(null);
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
