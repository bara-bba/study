using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Study.Core.Expressions;
using Study.Core.Models;
using Study.Core.Monitoring;
using Study.Core.Plc;
using Study.Native;

namespace Study;

public partial class WatchEditorWindow : Window
{
    private static readonly TriggerMode[] Modes = Enum.GetValues<TriggerMode>();
    private static readonly TagDataType[] Types = Enum.GetValues<TagDataType>();

    private readonly WatchDefinition _original;
    private bool _loading = true;

    public WatchEditorWindow(WatchDefinition? existing)
    {
        InitializeComponent();
        DwmHelper.ApplyPowerToysChrome(this);
        Icon = App.CurrentIconSource();

        _original = existing ?? new WatchDefinition { PollMs = App.Settings.DefaultPollMs };
        HeaderText.Text = existing is null ? "New watch" : "Edit watch";

        PlcCombo.ItemsSource = App.Monitor.Plcs;
        TypeCombo.ItemsSource = Types.Select(t => t.ToString().ToUpperInvariant()).ToList();
        ModeCombo.ItemsSource = Modes.Select(m => new TriggerOption(m, m.Describe("x …"))).ToList();

        NameBox.Text = _original.Name;
        PlcCombo.SelectedItem = App.Monitor.Plcs.FirstOrDefault(p => p.Ip == _original.PlcIp)
                                ?? App.Monitor.Plcs.FirstOrDefault();
        TagBox.Text = _original.Tag;
        TypeCombo.SelectedIndex = Array.IndexOf(Types, _original.DataType);
        ModeCombo.SelectedIndex = Array.IndexOf(Modes, _original.Mode);
        ExpressionBox.Text = _original.Expression;
        StopWhenClearedCheck.IsChecked = _original.StopWhenCleared;
        PollBox.Text = _original.PollMs.ToString();
        EnabledCheck.IsChecked = _original.Enabled;

        _loading = false;
        RefreshModeUi();
        RefreshPlcHint();
        TagBox.Focus();
    }

    public WatchDefinition? Result { get; private set; }

    private TriggerMode SelectedMode => ModeCombo.SelectedIndex >= 0 ? Modes[ModeCombo.SelectedIndex] : TriggerMode.RisingEdge;

    private TagDataType SelectedType => TypeCombo.SelectedIndex >= 0 ? Types[TypeCombo.SelectedIndex] : TagDataType.Bool;

    private void Any_Changed(object sender, EventArgs e)
    {
        if (!_loading)
        {
            RefreshModeUi();
        }
    }

    private void RefreshModeUi()
    {
        var mode = SelectedMode;
        ExpressionPanel.Visibility = mode == TriggerMode.Expression ? Visibility.Visible : Visibility.Collapsed;
        StopWhenClearedCheck.Visibility = mode.IsEdge() ? Visibility.Collapsed : Visibility.Visible;
        ModeHint.Text = mode switch
        {
            TriggerMode.RisingEdge => "Alarms once each time the value goes FALSE → TRUE (0 → non-zero). The first read only sets the baseline.",
            TriggerMode.FallingEdge => "Alarms once each time the value goes TRUE → FALSE (non-zero → 0). The first read only sets the baseline.",
            TriggerMode.WhileTrue => "Alarms when the value becomes TRUE (also if it is already TRUE at start). After Acknowledge it stays silent until the value goes FALSE and back to TRUE.",
            TriggerMode.WhileFalse => "Alarms when the value becomes FALSE (also at start). After Acknowledge it stays silent until the value goes TRUE and back to FALSE.",
            _ => "Alarms when the expression becomes true. After Acknowledge it stays silent until the expression is false again.",
        };

        if (mode == TriggerMode.Expression)
        {
            try
            {
                ConditionExpression.Parse(ExpressionBox.Text);
                ExpressionStatus.Text = "✓ Expression is valid";
                ExpressionStatus.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Ok");
            }
            catch (ExpressionException ex)
            {
                ExpressionStatus.Text = "✗ " + ex.Message;
                ExpressionStatus.Foreground = (System.Windows.Media.Brush)FindResource("Brush.Bad");
            }
        }

        if (mode != TriggerMode.Expression && SelectedType == TagDataType.Real)
        {
            ModeHint.Text += "  Note: a REAL counts as TRUE when it is not exactly 0 — an Expression is usually what you want.";
        }
    }

    private void PlcCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPlcHint();

    private void RefreshPlcHint()
    {
        PlcHint.Text = PlcCombo.SelectedItem is PlcEntry p
            ? $"CPU slot {p.Slot} (path 1,{p.Slot}). Change it in the PLC list if the connection is refused."
            : App.Monitor.Plcs.Count == 0 ? "The PLC list is empty — add PLCs first." : "";
    }

    /// <summary>Builds the definition from the form; null + ErrorText when invalid.</summary>
    private WatchDefinition? BuildDefinition()
    {
        int.TryParse(PollBox.Text.Trim(), out var poll);
        var def = _original with
        {
            Name = NameBox.Text.Trim(),
            PlcIp = (PlcCombo.SelectedItem as PlcEntry)?.Ip ?? "",
            Tag = TagBox.Text.Trim(),
            DataType = SelectedType,
            Mode = SelectedMode,
            Expression = ExpressionBox.Text.Trim(),
            StopWhenCleared = StopWhenClearedCheck.IsChecked == true,
            PollMs = poll,
            Enabled = EnabledCheck.IsChecked == true,
        };

        var errors = def.Validate();
        ErrorText.Text = string.Join("\n", errors);
        return errors.Count == 0 ? def : null;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (PlcCombo.SelectedItem is not PlcEntry plc || string.IsNullOrWhiteSpace(TagBox.Text))
        {
            TestResult.Text = "Pick a PLC and enter a tag first.";
            return;
        }

        TestButton.IsEnabled = false;
        try
        {
            if (!App.Settings.SkipPing)
            {
                TestResult.Text = $"Pinging {plc.Ip}…";
                if (!await new IcmpPinger().PingAsync(plc.Ip, WatchRunner.PingTimeout, CancellationToken.None))
                {
                    TestResult.Text = $"✗ No ping reply from {plc.Ip}. Check the network / VLAN, or enable 'Skip ping' in Settings if ICMP is blocked.";
                    return;
                }
            }

            TestResult.Text = $"Reading {TagBox.Text.Trim()} on slot {plc.Slot}…";
            var address = TagAddress.Parse(TagBox.Text, SelectedType);
            using var reader = new LibPlcTagReader(plc, address, WatchRunner.ReadTimeout);
            await reader.InitializeAsync(CancellationToken.None);
            var value = await reader.ReadAsync(CancellationToken.None);
            TestResult.Text = $"✓ {TagBox.Text.Trim()} = {ValueFormat.Format(value, SelectedType)}";
        }
        catch (PlcException ex)
        {
            TestResult.Text = "✗ " + ex.Message;
        }
        finally
        {
            TestButton.IsEnabled = true;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (PlcCombo.SelectedItem is not PlcEntry plc)
        {
            ErrorText.Text = "Pick a PLC first.";
            return;
        }

        var browser = new TagBrowserWindow(plc) { Owner = this };
        if (browser.ShowDialog() == true && browser.Selected is { } tag)
        {
            TagBox.Text = tag.Name;
            if (tag.DataType is { } type)
            {
                TypeCombo.SelectedIndex = Array.IndexOf(Types, type);
            }
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (BuildDefinition() is { } def)
        {
            Result = def;
            DialogResult = true;
        }
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
