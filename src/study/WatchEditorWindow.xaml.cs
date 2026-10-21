using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Study.Core.Expressions;
using Study.Core.Models;
using Study.Core.Monitoring;
using Study.Core.Plc;
using Study.Native;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;

namespace Study;

public partial class WatchEditorWindow : Window
{
    private static readonly TriggerMode[] Modes = Enum.GetValues<TriggerMode>();
    private static readonly TagDataType[] Types = Enum.GetValues<TagDataType>();

    /// <summary>The truth table is drawn while the formula uses at most this many letters (2^4 = 16 columns).</summary>
    private const int MaxTruthLetters = 4;

    // Truth table layout (DIP).
    private const double TruthLabelMaxWidth = 200;
    private const double TruthColumnWidth = 26;
    private const double TruthRowHeight = 22;
    private const int TruthTagChars = 22;

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
        CombineCombo.ItemsSource = new[]
        {
            "Row by row — each row's AND / OR / XOR applies to everything above it",
            "Custom formula — write it with the letters, e.g. A and (B or C)",
        };

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
        CombineCombo.SelectedIndex = _original.HasCustomLogic ? 1 : 0;
        FormulaBox.Text = _original.Logic;
        foreach (var c in _original.AndConditions)
        {
            AddRow(c);
        }

        _loading = false;
        RefreshModeUi();
        RefreshPlcHint();
        TagBox.Focus();
    }

    public WatchDefinition? Result { get; private set; }

    /// <summary>Opens the editor on the Advanced tab (used by the README snapshots).</summary>
    public void ShowAdvanced() => AdvancedTab.IsChecked = true;

    private TriggerMode SelectedMode => ModeCombo.SelectedIndex >= 0 ? Modes[ModeCombo.SelectedIndex] : TriggerMode.RisingEdge;

    private TagDataType SelectedType => TypeCombo.SelectedIndex >= 0 ? Types[TypeCombo.SelectedIndex] : TagDataType.Bool;

    private bool CustomFormula => CombineCombo.SelectedIndex == 1;

    private IEnumerable<ConditionRow> Rows => ConditionPanel.Children.OfType<ConditionRow>();

    // ------------------------------------------------------------------ tabs

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        // Fires once from XAML (IsChecked="True") before the panels exist.
        if (BasicPanel is null || AdvancedPanel is null)
        {
            return;
        }

        var advanced = AdvancedTab.IsChecked == true;
        BasicPanel.Visibility = advanced ? Visibility.Collapsed : Visibility.Visible;
        AdvancedPanel.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
    }

    private void GoAdvanced_Click(object sender, RoutedEventArgs e) => AdvancedTab.IsChecked = true;

    private void GoBasic_Click(object sender, RoutedEventArgs e)
    {
        BasicTab.IsChecked = true;
        TagBox.Focus();
    }

    // ------------------------------------------------------------- main trigger

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
                SetHint(ExpressionStatus, "✓ Expression is valid", "Brush.Ok");
            }
            catch (ExpressionException ex)
            {
                SetHint(ExpressionStatus, "✗ " + ex.Message, "Brush.Bad");
            }
        }

        if (mode != TriggerMode.Expression && SelectedType == TagDataType.Real)
        {
            ModeHint.Text += "  Note: a REAL counts as TRUE when it is not exactly 0 — an Expression is usually what you want.";
        }

        StopWhenClearedCheck.IsEnabled = !mode.IsEdge();
        BehaviourHint.Text = mode.IsEdge()
            ? "Not available with an edge trigger: the alarm rings until you press Acknowledge."
            : "Off: the alarm keeps sounding until you press Acknowledge, even if the condition has cleared.";

        RefreshLogic();
    }

    // --------------------------------------------------------------- conditions

    private void AddCondition_Click(object sender, RoutedEventArgs e) => AddRow(new AndCondition()).FocusTag();

    private ConditionRow AddRow(AndCondition condition)
    {
        var row = new ConditionRow(condition, BrowseFor, r =>
        {
            ConditionPanel.Children.Remove(r);
            RefreshLogic();
        }, RefreshLogic);
        ConditionPanel.Children.Add(row);
        RefreshLogic();
        return row;
    }

    private void BrowseFor(ConditionRow row)
    {
        if (PickTag(row.TagText) is { } tag)
        {
            row.TagText = tag.Name;
            if (tag.DataType is { } type)
            {
                row.DataType = type;
            }
        }
    }

    private void Combine_Changed(object sender, SelectionChangedEventArgs e)
    {
        // Switching to a custom formula starts from what the rows currently say.
        if (!_loading && CustomFormula && string.IsNullOrWhiteSpace(FormulaBox.Text))
        {
            FormulaBox.Text = RowChain(Rows.ToList()).ToString();
        }

        RefreshLogic();
    }

    private void Formula_Changed(object sender, TextChangedEventArgs e) => RefreshLogic();

    private static LogicFormula RowChain(IReadOnlyList<ConditionRow> rows) =>
        LogicFormula.Chain(rows.Select(r => r.Join).ToList(), rows.Select(r => r.Not).ToList());

    /// <summary>Current formula, or null with the reason when the custom one does not parse.</summary>
    private LogicFormula? CurrentLogic(out string? problem)
    {
        problem = null;
        var rows = Rows.ToList();
        if (!CustomFormula)
        {
            return RowChain(rows);
        }

        try
        {
            return LogicFormula.Parse(FormulaBox.Text, rows.Count + 1);
        }
        catch (ExpressionException ex)
        {
            problem = ex.Message;
            return null;
        }
    }

    /// <summary>Refreshes letters, the main-tag card, the formula check, the result and the Basic summary.</summary>
    private void RefreshLogic()
    {
        if (_loading)
        {
            return;
        }

        var rows = Rows.ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            rows[i].SetPosition(i + 1, CustomFormula);
        }

        AddConditionButtonState(rows.Count);

        var mode = SelectedMode;
        MainTagText.Text = string.IsNullOrWhiteSpace(TagBox.Text) ? "(no tag yet)" : TagBox.Text.Trim();
        MainModeIcon.Source = TriggerIcons.For(mode);
        MainModeText.Text = mode.Describe(ExpressionBox.Text.Trim());

        FormulaPanel.Visibility = CustomFormula ? Visibility.Visible : Visibility.Collapsed;
        var logic = CurrentLogic(out var problem);
        if (CustomFormula)
        {
            if (logic is null)
            {
                SetHint(FormulaStatus, "✗ " + problem, "Brush.Bad");
            }
            else
            {
                var unused = Enumerable.Range(0, rows.Count + 1).Where(i => !logic.Letters.Contains(i))
                    .Select(LogicFormula.LetterFor).ToList();
                SetHint(FormulaStatus,
                    unused.Count == 0 ? "✓ Formula is valid"
                        : $"✓ Formula is valid · not used: {string.Join(", ", unused)} (still read, but they cannot trigger anything)",
                    unused.Count == 0 ? "Brush.Ok" : "Brush.Warn");
            }
        }

        ResultFormula.Text = logic?.ToString() ?? "—";
        ResultWords.Text = DescribeLetters(rows, logic);
        RenderTruthTable(logic);

        SummaryText.Text = rows.Count == 0
            ? "None: the trigger above decides alone. Open Advanced to combine it with other tags using AND / OR / XOR."
            : $"{rows.Count} extra tag(s) · alarm when {logic?.ToString() ?? "(the formula has an error)"}";
    }

    private void AddConditionButtonState(int count)
    {
        AddConditionButton.IsEnabled = count < LogicFormula.MaxLetters - 1;
        AddConditionButton.ToolTip = AddConditionButton.IsEnabled
            ? "Add another tag on the same PLC (TRUE / FALSE / expression)"
            : "All letters B to Z are in use.";
    }

    /// <summary>"A = Motor.Run rising edge (only on that read) · B = Motor.Fb is FALSE …" plus edge notes.</summary>
    private string DescribeLetters(IReadOnlyList<ConditionRow> rows, LogicFormula? logic)
    {
        var mode = SelectedMode;
        var tag = string.IsNullOrWhiteSpace(TagBox.Text) ? "main tag" : TagBox.Text.Trim();
        var lines = new List<string>
        {
            "A = " + (mode switch
            {
                TriggerMode.RisingEdge => $"{tag} goes FALSE → TRUE (true only on the read where it happens)",
                TriggerMode.FallingEdge => $"{tag} goes TRUE → FALSE (true only on the read where it happens)",
                TriggerMode.WhileTrue => $"{tag} is TRUE",
                TriggerMode.WhileFalse => $"{tag} is FALSE",
                _ => $"{tag}: {ExpressionBox.Text.Trim()}",
            }),
        };
        for (var i = 0; i < rows.Count; i++)
        {
            var c = rows[i].ToCondition();
            var text = string.IsNullOrWhiteSpace(c.Tag) ? "(no tag yet)" : c.Describe();
            lines.Add($"{LogicFormula.LetterFor(i + 1)} = {text}");
        }

        if (rows.Count > 0 && logic is not null)
        {
            lines.Add(mode.IsEdge()
                ? "The alarm fires once each time the result turns true; Acknowledge stops it."
                : "The alarm fires when the result turns true and clears when it turns false.");
            if (mode.IsEdge() && !logic.UsesLetterOnlyPositively(0))
            {
                lines.Add("⚠ Can't be saved: with an edge trigger, NOT A or A XOR … would fire on every read after an edge.");
            }
        }

        return string.Join("\n", lines);
    }

    /// <summary>Columns = every combination of the letters used, last row = whether it alarms.</summary>
    private void RenderTruthTable(LogicFormula? logic)
    {
        TruthGrid.Children.Clear();
        TruthGrid.RowDefinitions.Clear();
        TruthGrid.ColumnDefinitions.Clear();

        var letters = logic?.Letters.OrderBy(i => i).ToList() ?? [];
        var show = logic is not null && Rows.Any() && letters.Count is > 0 and <= MaxTruthLetters;
        TruthLabel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        TruthGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        var combos = 1 << letters.Count;
        TruthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MaxWidth = TruthLabelMaxWidth });
        for (var c = 0; c < combos; c++)
        {
            TruthGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TruthColumnWidth) });
        }

        for (var r = 0; r <= letters.Count; r++)
        {
            TruthGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TruthRowHeight) });
        }

        // Shade every other combination column so a column reads top to bottom at a glance.
        var shade = (Brush)FindResource("Brush.Subtle");
        for (var c = 1; c < combos; c += 2)
        {
            var band = new Border { Background = shade, CornerRadius = new CornerRadius(3) };
            Grid.SetColumn(band, c + 1);
            Grid.SetRowSpan(band, letters.Count + 1);
            TruthGrid.Children.Add(band);
        }

        // Divider between the inputs and the result row.
        var divider = new Border
        {
            BorderBrush = (Brush)FindResource("Brush.Stroke"),
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        Grid.SetRow(divider, letters.Count);
        Grid.SetColumnSpan(divider, combos + 1);
        TruthGrid.Children.Add(divider);

        var primary = (Brush)FindResource("Brush.TextPrimary");
        var secondary = (Brush)FindResource("Brush.TextSecondary");
        var tertiary = (Brush)FindResource("Brush.TextTertiary");
        var rows = Rows.ToList();
        for (var r = 0; r < letters.Count; r++)
        {
            var label = LetterLabel(letters[r], rows);
            Cell(label, r, 0, secondary, FontWeights.SemiBold, HorizontalAlignment.Left).ToolTip = label;
        }

        Cell("Alarm", letters.Count, 0, primary, FontWeights.Bold, HorizontalAlignment.Left);

        var values = new bool[letters.Max() + 1];
        for (var c = 0; c < combos; c++)
        {
            for (var r = 0; r < letters.Count; r++)
            {
                // Most significant letter first, so columns read 000, 001, 010, …
                var bit = (c >> (letters.Count - 1 - r)) & 1;
                values[letters[r]] = bit == 1;
                Cell(bit.ToString(), r, c + 1, bit == 1 ? secondary : tertiary, FontWeights.Normal, HorizontalAlignment.Center);
            }

            var alarm = logic!.Evaluate(values);
            Cell(alarm ? "✓" : "·", letters.Count, c + 1,
                alarm ? (Brush)FindResource("Brush.Accent") : tertiary, FontWeights.Bold, HorizontalAlignment.Center);
        }
    }

    private TextBlock Cell(string text, int row, int column, Brush brush, FontWeight weight, HorizontalAlignment align)
    {
        var t = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontWeight = weight,
            FontSize = 12,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = column == 0 ? new Thickness(0, 0, 12, 0) : new Thickness(0),
        };
        Grid.SetRow(t, row);
        Grid.SetColumn(t, column);
        TruthGrid.Children.Add(t);
        return t;
    }

    /// <summary>"A  Motor.Run": the letter plus the tag it stands for (short tail of the tag name).</summary>
    private string LetterLabel(int letter, IReadOnlyList<ConditionRow> rows)
    {
        var tag = letter == 0 ? TagBox.Text.Trim() : letter - 1 < rows.Count ? rows[letter - 1].TagText : "";
        if (tag.Length > TruthTagChars)
        {
            tag = "…" + tag[^(TruthTagChars - 1)..];
        }

        return tag.Length == 0 ? LogicFormula.LetterFor(letter) : $"{LogicFormula.LetterFor(letter)}  {tag}";
    }

    private void SetHint(TextBlock target, string text, string brushKey)
    {
        target.Text = text;
        target.Foreground = (Brush)FindResource(brushKey);
    }

    // ------------------------------------------------------------------ PLC filter

    private void PlcFilter_Changed(object sender, TextChangedEventArgs e)
    {
        var f = PlcFilterBox.Text.Trim();
        var selected = PlcCombo.SelectedItem as PlcEntry;
        var list = string.IsNullOrEmpty(f)
            ? App.Monitor.Plcs
            : App.Monitor.Plcs.Where(p => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                                          || p.Ip.Contains(f, StringComparison.OrdinalIgnoreCase)
                                          || p.Group.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        PlcCombo.ItemsSource = list;
        PlcCombo.SelectedItem = selected is not null && list.Contains(selected) ? selected : list.FirstOrDefault();
        if (PlcCombo.SelectedItem is null)
        {
            PlcHint.Text = $"No PLC matches '{f}'.";
        }
    }

    private void PlcCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshPlcHint();

    private void RefreshPlcHint()
    {
        PlcHint.Text = PlcCombo.SelectedItem is PlcEntry p
            ? $"CPU slot {p.Slot} (path 1,{p.Slot}). Change it in the PLC list if the connection is refused."
            : App.Monitor.Plcs.Count == 0 ? "The PLC list is empty — add PLCs first." : "";
    }

    // ------------------------------------------------------------- save / test

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
            AndConditions = Rows.Select(r => r.ToCondition()).ToList(),
            Logic = CustomFormula ? FormulaBox.Text.Trim() : "",
        };

        var errors = def.Validate().ToList();
        if (CustomFormula && !def.HasCustomLogic)
        {
            errors.Add("Formula: enter a formula, or choose 'Row by row'.");
        }

        ErrorText.Text = string.Join("\n", errors);

        // Point the user at the tab that holds the problem.
        if (errors.Count > 0 && errors.All(e => e.StartsWith("Condition ") || e.StartsWith("Formula")
                                                || e.StartsWith("At most") || e.StartsWith("With an edge")))
        {
            AdvancedTab.IsChecked = true;
        }

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
        var tagText = TagBox.Text.Trim();
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

            TestResult.Text = $"Reading {tagText} on slot {plc.Slot}…";
            var value = await ReadOnceAsync(plc, tagText, SelectedType);
            TestResult.Text = $"✓ A  {tagText} = {ValueFormat.Format(value, SelectedType)}";

            // Extra rows: each gets its own result line; one bad tag does not hide the others.
            var failures = 0;
            var met = new List<bool?>();
            foreach (var row in Rows)
            {
                var c = row.ToCondition();
                if (string.IsNullOrWhiteSpace(c.Tag))
                {
                    row.ShowResult("Enter a tag first.");
                    met.Add(null);
                    continue;
                }

                try
                {
                    var v = await ReadOnceAsync(plc, c.Tag, c.DataType);
                    var holds = c.Validate(0).Any() ? (bool?)null : new AndConditionEvaluator(c).IsMet(v);
                    met.Add(holds);
                    row.ShowResult($"✓ {c.Tag} = {ValueFormat.Format(v, c.DataType)}"
                                   + (holds is null ? "" : holds.Value ? "  ·  holds now" : "  ·  does not hold now"));
                }
                catch (PlcException ex)
                {
                    failures++;
                    met.Add(null);
                    row.ShowResult("✗ " + ex.Message);
                }
            }

            if (failures > 0)
            {
                TestResult.Text += $"   ·   {failures} extra tag(s) failed, see the Advanced tab.";
            }
            else if (met.Count > 0 && SelectedMode.IsEdge())
            {
                TestResult.Text += "   ·   An edge needs two reads, so one test read can't say whether it would alarm.";
            }
            else if (met.Count > 0 && met.All(m => m is not null) && MainLevel(value) is { } a
                     && CurrentLogic(out _) is { } logic)
            {
                var result = logic.Evaluate([a, .. met.Select(m => m!.Value)]);
                TestResult.Text += $"   ·   {logic} is {(result ? "TRUE now: it would alarm" : "FALSE now")}";
            }
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

    /// <summary>A's value for a single read; null for edges (one read cannot show an edge) or a bad expression.</summary>
    private bool? MainLevel(double value)
    {
        switch (SelectedMode)
        {
            case TriggerMode.WhileTrue:
                return value != 0;
            case TriggerMode.WhileFalse:
                return value == 0;
            case TriggerMode.Expression:
                try
                {
                    return ConditionExpression.Parse(ExpressionBox.Text).IsTrue(value, value);
                }
                catch (ExpressionException)
                {
                    return null;
                }

            default:
                return null;
        }
    }

    private static async Task<double> ReadOnceAsync(PlcEntry plc, string tag, TagDataType type)
    {
        using var reader = new LibPlcTagReader(plc, TagAddress.Parse(tag, type), WatchRunner.ReadTimeout);
        await reader.InitializeAsync(CancellationToken.None);
        return await reader.ReadAsync(CancellationToken.None);
    }

    private BrowsedTag? PickTag(string startAt)
    {
        if (PlcCombo.SelectedItem is not PlcEntry plc)
        {
            ErrorText.Text = "Pick a PLC first.";
            return null;
        }

        var browser = new TagBrowserWindow(plc, startAt) { Owner = this };
        return browser.ShowDialog() == true ? browser.Selected : null;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (PickTag(TagBox.Text) is { } tag)
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
