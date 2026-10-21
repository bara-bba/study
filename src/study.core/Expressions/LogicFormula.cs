namespace Study.Core.Expressions;

/// <summary>How an extra condition combines with everything above it in "row by row" mode.</summary>
public enum LogicJoin
{
    And,
    Or,
    Xor,
}

/// <summary>
/// A boolean formula over condition letters: <c>A</c> is the watch's main trigger, <c>B</c>,
/// <c>C</c>, … are the extra conditions in order. Operators (case-insensitive):
/// <c>not</c> / <c>!</c>, <c>and</c> / <c>&amp;&amp;</c> / <c>&amp;</c>, <c>xor</c> / <c>^</c>,
/// <c>or</c> / <c>||</c> / <c>|</c>, and parentheses. Precedence, tightest first:
/// NOT, AND, XOR, OR (the same order as C and Structured Text).
/// </summary>
public sealed class LogicFormula
{
    private readonly Node _root;

    private LogicFormula(Node root) => _root = root;

    /// <summary>Highest letter index referenced (A = 0), or -1 for none.</summary>
    public int MaxLetter => _root.MaxLetter;

    /// <summary>Letters the formula uses (A = 0).</summary>
    public IReadOnlySet<int> Letters
    {
        get
        {
            var set = new HashSet<int>();
            _root.Collect(set);
            return set;
        }
    }

    /// <summary>Canonical text with only the parentheses that change meaning, e.g. <c>A AND (B OR C)</c>.</summary>
    public override string ToString() => _root.Show(0);

    public bool Evaluate(IReadOnlyList<bool> values) => _root.Eval(values);

    /// <summary>
    /// True when <paramref name="letter"/> can only push the result towards TRUE: it never sits
    /// under an odd number of NOTs and never inside an XOR. An edge watch needs this for A, because
    /// A is FALSE on almost every read; <c>NOT A</c> or <c>A XOR B</c> would then be TRUE between
    /// edges and fire again on the read after every edge.
    /// </summary>
    public bool UsesLetterOnlyPositively(int letter) => _root.OnlyPositive(letter, negated: false);

    /// <summary>
    /// Builds the "row by row" formula: each join applies to everything above it, so
    /// [Or, And] gives <c>(A OR B) AND C</c>. <paramref name="negate"/>[i] turns row i into
    /// NOT that letter, e.g. "AND NOT" gives <c>A AND NOT B</c>.
    /// </summary>
    public static LogicFormula Chain(IReadOnlyList<LogicJoin> joins, IReadOnlyList<bool>? negate = null)
    {
        Node node = new Letter(0);
        for (var i = 0; i < joins.Count; i++)
        {
            Node letter = new Letter(i + 1);
            if (negate is not null && i < negate.Count && negate[i])
            {
                letter = new Not(letter);
            }

            node = new Binary(joins[i], node, letter);
        }

        return new LogicFormula(node);
    }

    /// <summary>Parses a custom formula; <paramref name="letterCount"/> is how many conditions exist (A included).</summary>
    public static LogicFormula Parse(string source, int letterCount)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ExpressionException("Formula is empty.");
        }

        var parser = new Parser(Tokenize(source), letterCount);
        return new LogicFormula(parser.ParseAll());
    }

    public static string LetterFor(int index) => ((char)('A' + index)).ToString();

    /// <summary>Most letters supported: A (main) + 25 extra conditions.</summary>
    public const int MaxLetters = 26;

    // ------------------------------------------------------------------ AST

    private abstract class Node
    {
        public abstract int Precedence { get; }

        public abstract int MaxLetter { get; }

        public abstract bool Eval(IReadOnlyList<bool> v);

        public abstract string Show(int parentPrecedence);

        public abstract void Collect(HashSet<int> set);

        public abstract bool OnlyPositive(int letter, bool negated);

        public bool Uses(int letter)
        {
            var set = new HashSet<int>();
            Collect(set);
            return set.Contains(letter);
        }
    }

    private sealed class Letter(int index) : Node
    {
        public override int Precedence => 9;

        public override int MaxLetter => index;

        public override bool Eval(IReadOnlyList<bool> v) => index < v.Count && v[index];

        public override string Show(int parentPrecedence) => LetterFor(index);

        public override void Collect(HashSet<int> set) => set.Add(index);

        public override bool OnlyPositive(int letter, bool negated) => index != letter || !negated;
    }

    private sealed class Not(Node operand) : Node
    {
        public override int Precedence => 4;

        public override int MaxLetter => operand.MaxLetter;

        public override bool Eval(IReadOnlyList<bool> v) => !operand.Eval(v);

        public override string Show(int parentPrecedence) => "NOT " + operand.Show(Precedence);

        public override void Collect(HashSet<int> set) => operand.Collect(set);

        public override bool OnlyPositive(int letter, bool negated) => operand.OnlyPositive(letter, !negated);
    }

    private sealed class Binary(LogicJoin op, Node left, Node right) : Node
    {
        public override int Precedence => op switch
        {
            LogicJoin.And => 3,
            LogicJoin.Xor => 2,
            _ => 1,
        };

        public override int MaxLetter => Math.Max(left.MaxLetter, right.MaxLetter);

        public override bool Eval(IReadOnlyList<bool> v) => op switch
        {
            LogicJoin.And => left.Eval(v) && right.Eval(v),
            LogicJoin.Or => left.Eval(v) || right.Eval(v),
            _ => left.Eval(v) ^ right.Eval(v),
        };

        public override string Show(int parentPrecedence)
        {
            // Left-associative: the right child needs brackets at equal precedence too.
            var text = $"{left.Show(Precedence)} {op.ToString().ToUpperInvariant()} {right.Show(Precedence + 1)}";
            return Precedence < parentPrecedence ? $"({text})" : text;
        }

        public override void Collect(HashSet<int> set)
        {
            left.Collect(set);
            right.Collect(set);
        }

        // XOR flips its result when either side changes, so a letter inside it is never "only positive".
        public override bool OnlyPositive(int letter, bool negated) => op == LogicJoin.Xor
            ? !Uses(letter)
            : left.OnlyPositive(letter, negated) && right.OnlyPositive(letter, negated);
    }

    // ------------------------------------------------------------ tokenizer

    private readonly record struct Token(string Text, int Pos);

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (char.IsLetter(c))
            {
                var start = i;
                while (i < s.Length && char.IsLetterOrDigit(s[i]))
                {
                    i++;
                }

                tokens.Add(new Token(s[start..i].ToUpperInvariant(), start));
                continue;
            }

            if (i + 1 < s.Length && s.Substring(i, 2) is "&&" or "||")
            {
                tokens.Add(new Token(s.Substring(i, 2) == "&&" ? "AND" : "OR", i));
                i += 2;
                continue;
            }

            var mapped = c switch
            {
                '&' => "AND",
                '|' => "OR",
                '^' => "XOR",
                '!' => "NOT",
                '(' => "(",
                ')' => ")",
                _ => throw new ExpressionException($"Unexpected character '{c}' at position {i + 1}."),
            };
            tokens.Add(new Token(mapped, i));
            i++;
        }

        tokens.Add(new Token("", s.Length));
        return tokens;
    }

    // --------------------------------------------------------------- parser

    private sealed class Parser(List<Token> tokens, int letterCount)
    {
        private int _pos;

        private Token Peek => tokens[_pos];

        public Node ParseAll()
        {
            var node = ParseOr();
            if (Peek.Text.Length > 0)
            {
                throw new ExpressionException($"Unexpected '{Peek.Text}' at position {Peek.Pos + 1}.");
            }

            return node;
        }

        private Node ParseOr() => ParseLevel(LogicJoin.Or, "OR", ParseXor);

        private Node ParseXor() => ParseLevel(LogicJoin.Xor, "XOR", ParseAnd);

        private Node ParseAnd() => ParseLevel(LogicJoin.And, "AND", ParseUnary);

        private Node ParseLevel(LogicJoin op, string word, Func<Node> next)
        {
            var left = next();
            while (Peek.Text == word)
            {
                _pos++;
                left = new Binary(op, left, next());
            }

            return left;
        }

        private Node ParseUnary()
        {
            if (Peek.Text == "NOT")
            {
                _pos++;
                return new Not(ParseUnary());
            }

            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            var t = Peek;
            if (t.Text.Length == 0)
            {
                throw new ExpressionException("Formula ends too early.");
            }

            if (t.Text == "(")
            {
                _pos++;
                var inner = ParseOr();
                if (Peek.Text != ")")
                {
                    throw new ExpressionException($"Expected ')' at position {Peek.Pos + 1}.");
                }

                _pos++;
                return inner;
            }

            if (t.Text.Length == 1 && t.Text[0] is >= 'A' and <= 'Z')
            {
                var index = t.Text[0] - 'A';
                if (index >= letterCount)
                {
                    var last = LetterFor(Math.Max(0, letterCount - 1));
                    throw new ExpressionException(letterCount <= 1
                        ? $"'{t.Text}' does not exist: only A (the main tag) is defined. Add a condition first."
                        : $"'{t.Text}' does not exist: conditions go from A to {last}.");
                }

                _pos++;
                return new Letter(index);
            }

            throw new ExpressionException($"Unexpected '{t.Text}' at position {t.Pos + 1}. Use letters A, B, C… and AND / OR / XOR / NOT.");
        }
    }
}
