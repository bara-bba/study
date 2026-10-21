using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Study.Core.Expressions;
using Study.Core.Models;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;

namespace Study;

/// <summary>
/// One extra condition in the watch editor's Advanced tab:
/// [letter] tag · Browse… · data type · ✕ on the first line,
/// [AND / OR / XOR] test (Is TRUE / Is FALSE / Expression) and its expression on the second.
/// </summary>
internal sealed class ConditionRow : Border
{
    private static readonly TagDataType[] Types = Enum.GetValues<TagDataType>();
    private static readonly AndTest[] Tests = Enum.GetValues<AndTest>();
    /// <summary>The picker's entries: every join, plain and with NOT on this row.</summary>
    private static readonly (LogicJoin Join, bool Not)[] Joins = Enum.GetValues<LogicJoin>()
        .SelectMany(j => new[] { (j, false), (j, true) }).ToArray();

    // Layout (DIP). The second line is indented by the badge plus its margin so it lines up under the tag box.
    private const double FieldHeight = 32;
    private const double BadgeIndent = 26 + 10;
    private const double TypeWidth = 96;
    private const double JoinWidth = 112;
    private const double TestWidth = 150;

    private readonly TextBlock _letter;
    private readonly TextBox _tag;
    private readonly ComboBox _type;
    private readonly ComboBox _join;
    private readonly TextBlock _joinLabel;
    private readonly ComboBox _test;
    private readonly TextBox _expression;
    private readonly TextBlock _status;

    public ConditionRow(AndCondition condition, Action<ConditionRow> browse, Action<ConditionRow> remove, Action changed)
    {
        Style = Res<Style>("Card");
        Margin = new Thickness(0, 0, 0, 8);
        Padding = new Thickness(12, 10, 10, 10);

        _letter = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var badge = new Border { Style = Res<Style>("LetterBadge"), Child = _letter, Margin = new Thickness(0, 0, 10, 0) };

        _tag = new TextBox
        {
            Style = Res<Style>("DarkTextBox"),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Text = condition.Tag,
            ToolTip = "Tag on the same PLC, e.g. Motor1.Feedback",
        };
        var browseButton = new Button
        {
            Content = "Browse…",
            Style = Res<Style>("SubtleButton"),
            Height = FieldHeight,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(8, 0, 8, 0),
            ToolTip = "List the tags in the PLC",
        };
        browseButton.Click += (_, _) => browse(this);
        _type = new ComboBox
        {
            Style = Res<Style>("DarkCombo"),
            ItemsSource = Types.Select(t => t.ToString().ToUpperInvariant()).ToList(),
            SelectedIndex = Array.IndexOf(Types, condition.DataType),
        };
        var removeButton = new Button
        {
            Content = "✕",
            Style = Res<Style>("IconButton"),
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = "Remove this condition (the letters after it move up)",
        };
        removeButton.Click += (_, _) => remove(this);

        _join = new ComboBox
        {
            Style = Res<Style>("DarkCombo"),
            ItemsSource = Joins.Select(j => j.Join.ToString().ToUpperInvariant() + (j.Not ? " NOT" : "")).ToList(),
            SelectedIndex = Array.IndexOf(Joins, (condition.Join, condition.Not)),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "How this row combines with everything above it:\n"
                      + "AND = both must hold · OR = either one · XOR = exactly one of the two\n"
                      + "… NOT = use the opposite of this row (it holds when the test below does NOT)",
        };
        _joinLabel = new TextBlock
        {
            Style = Res<Style>("Hint"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 8, 0),
            Visibility = Visibility.Collapsed,
        };
        _test = new ComboBox
        {
            Style = Res<Style>("DarkCombo"),
            ItemsSource = Tests.Select(Describe).ToList(),
            SelectedIndex = Array.IndexOf(Tests, condition.Test),
        };
        _expression = new TextBox
        {
            Style = Res<Style>("DarkTextBox"),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            Text = condition.Expression,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "x = this tag's value, prev = its previous value. E.g. x > 100, bit(x, 3)",
        };
        _status = new TextBlock { Style = Res<Style>("Hint") };

        _test.SelectionChanged += (_, _) => { Refresh(); changed(); };
        _expression.TextChanged += (_, _) => Refresh();
        _join.SelectionChanged += (_, _) => changed();
        _tag.TextChanged += (_, _) => changed();

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TypeWidth) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Place(top, badge, 0);
        Place(top, _tag, 1);
        Place(top, browseButton, 2);
        Place(top, _type, 3);
        Place(top, removeButton, 4);

        var bottom = new Grid { Margin = new Thickness(BadgeIndent, 8, 0, 0) };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TestWidth) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _join.Width = JoinWidth;
        var joinCell = new Grid();
        joinCell.Children.Add(_join);
        joinCell.Children.Add(_joinLabel);
        Place(bottom, joinCell, 0);
        Place(bottom, _test, 1);
        Place(bottom, _expression, 2);

        var stack = new StackPanel();
        stack.Children.Add(top);
        stack.Children.Add(bottom);
        stack.Children.Add(_status);
        _status.Margin = new Thickness(BadgeIndent, 4, 0, 0);
        Child = stack;
        Refresh();
    }

    public string TagText
    {
        get => _tag.Text.Trim();
        set => _tag.Text = value;
    }

    public TagDataType DataType
    {
        get => _type.SelectedIndex >= 0 ? Types[_type.SelectedIndex] : TagDataType.Bool;
        set => _type.SelectedIndex = Array.IndexOf(Types, value);
    }

    public LogicJoin Join => _join.SelectedIndex >= 0 ? Joins[_join.SelectedIndex].Join : LogicJoin.And;

    public bool Not => _join.SelectedIndex >= 0 && Joins[_join.SelectedIndex].Not;

    public AndCondition ToCondition() => new()
    {
        Tag = TagText,
        DataType = DataType,
        Test = _test.SelectedIndex >= 0 ? Tests[_test.SelectedIndex] : AndTest.IsTrue,
        Expression = _expression.Text.Trim(),
        Join = Join,
        Not = Not,
    };

    /// <summary>
    /// Sets this row's letter (1 = B) and whether its AND/OR/XOR picker applies: with a custom
    /// formula the picker is replaced by a note, because the formula decides.
    /// </summary>
    public void SetPosition(int letterIndex, bool customFormula)
    {
        var letter = LogicFormula.LetterFor(letterIndex);
        _letter.Text = letter;
        _join.Visibility = customFormula ? Visibility.Collapsed : Visibility.Visible;
        _joinLabel.Visibility = Visibility.Collapsed; // the badge already shows the letter
        _joinLabel.Text = $"used as {letter}";
        _joinLabel.Width = 82;
    }

    public void FocusTag() => _tag.Focus();

    /// <summary>Shows a test-read result under the row (✓ / ✗ text).</summary>
    public void ShowResult(string text) => SetStatus(text, "Brush.TextSecondary");

    private void Refresh()
    {
        var isExpression = _test.SelectedIndex >= 0 && Tests[_test.SelectedIndex] == AndTest.Expression;
        _expression.Visibility = isExpression ? Visibility.Visible : Visibility.Hidden;
        if (!isExpression)
        {
            SetStatus("", "Brush.TextSecondary");
            return;
        }

        try
        {
            ConditionExpression.Parse(_expression.Text);
            SetStatus("✓ Expression is valid", "Brush.Ok");
        }
        catch (ExpressionException ex)
        {
            SetStatus("✗ " + ex.Message, "Brush.Bad");
        }
    }

    /// <summary>The line under the row only takes space when it has something to say.</summary>
    private void SetStatus(string text, string brushKey)
    {
        _status.Text = text;
        _status.Foreground = Res<Brush>(brushKey);
        _status.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static string Describe(AndTest test) => test switch
    {
        AndTest.IsTrue => "Is TRUE (1)",
        AndTest.IsFalse => "Is FALSE (0)",
        _ => "Expression {x}",
    };

    private static void Place(Grid grid, UIElement element, int column)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);
}
