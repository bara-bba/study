using System.Globalization;

namespace Study.Core.Expressions;

public sealed class ExpressionException : Exception
{
    public ExpressionException(string message) : base(message)
    {
    }
}

/// <summary>
/// A small, safe condition language evaluated against the tag value. No reflection, no code
/// execution — just arithmetic, comparisons and logic on two variables:
/// <list type="bullet">
/// <item><c>x</c> — the value just read (BOOL reads as 1 / 0)</item>
/// <item><c>prev</c> — the previous value (equals <c>x</c> on the first read)</item>
/// </list>
/// Operators: <c>+ - * / %</c>, <c>== != &lt; &lt;= &gt; &gt;=</c>, <c>&amp;&amp; || !</c>
/// (or <c>and or not</c>), parentheses, <c>true</c>/<c>false</c>.
/// Functions: <c>abs(a)</c>, <c>min(a,b)</c>, <c>max(a,b)</c>, <c>bit(a,n)</c> (bit n of a).
/// A result is "true" when it is non-zero.
/// </summary>
public sealed class ConditionExpression
{
    private readonly Node _root;

    private ConditionExpression(string source, Node root)
    {
        Source = source;
        _root = root;
    }

    public string Source { get; }

    /// <summary>Parses and validates; throws <see cref="ExpressionException"/> with a readable message.</summary>
    public static ConditionExpression Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ExpressionException("Expression is empty.");
        }

        var parser = new Parser(Tokenizer.Tokenize(source));
        var root = parser.ParseAll();
        return new ConditionExpression(source.Trim(), root);
    }

    public double Evaluate(double x, double prev) => _root.Eval(x, prev);

    public bool IsTrue(double x, double prev) => Evaluate(x, prev) != 0;

    // ------------------------------------------------------------------ AST

    private abstract class Node
    {
        public abstract double Eval(double x, double prev);
    }

    private sealed class Number(double value) : Node
    {
        public override double Eval(double x, double prev) => value;
    }

    private sealed class Variable(bool isPrev) : Node
    {
        public override double Eval(double x, double prev) => isPrev ? prev : x;
    }

    private sealed class Unary(string op, Node operand) : Node
    {
        public override double Eval(double x, double prev)
        {
            var v = operand.Eval(x, prev);
            return op == "!" ? B(v == 0) : -v;
        }
    }

    private sealed class Binary(string op, Node left, Node right) : Node
    {
        public override double Eval(double x, double prev)
        {
            // Short-circuit logic so e.g. "prev != 0 && x / prev > 2" is safe.
            if (op == "&&")
            {
                return B(left.Eval(x, prev) != 0 && right.Eval(x, prev) != 0);
            }

            if (op == "||")
            {
                return B(left.Eval(x, prev) != 0 || right.Eval(x, prev) != 0);
            }

            var a = left.Eval(x, prev);
            var b = right.Eval(x, prev);
            return op switch
            {
                "+" => a + b,
                "-" => a - b,
                "*" => a * b,
                // Division by zero yields 0 instead of Infinity/NaN so a condition stays well-defined.
                "/" => b == 0 ? 0 : a / b,
                "%" => b == 0 ? 0 : a % b,
                "==" => B(a == b),
                "!=" => B(a != b),
                "<" => B(a < b),
                "<=" => B(a <= b),
                ">" => B(a > b),
                ">=" => B(a >= b),
                _ => throw new InvalidOperationException(op),
            };
        }
    }

    private sealed class Call(string name, Node[] args) : Node
    {
        public override double Eval(double x, double prev)
        {
            var v = args.Select(a => a.Eval(x, prev)).ToArray();
            return name switch
            {
                "abs" => Math.Abs(v[0]),
                "min" => Math.Min(v[0], v[1]),
                "max" => Math.Max(v[0], v[1]),
                "bit" => BitOf(v[0], v[1]),
                _ => throw new InvalidOperationException(name),
            };
        }

        private static double BitOf(double value, double index)
        {
            var n = (int)index;
            if (n is < 0 or > 63)
            {
                return 0;
            }

            var raw = unchecked((long)value);
            return (raw >> n) & 1;
        }
    }

    private static double B(bool b) => b ? 1 : 0;

    private static readonly Dictionary<string, int> FunctionArity = new()
    {
        ["abs"] = 1,
        ["min"] = 2,
        ["max"] = 2,
        ["bit"] = 2,
    };

    // ------------------------------------------------------------ tokenizer

    private enum TokenKind
    {
        Number,
        Ident,
        Op,
        LParen,
        RParen,
        Comma,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Pos);

    private static class Tokenizer
    {
        private static readonly string[] TwoCharOps = ["&&", "||", "==", "!=", "<=", ">="];

        public static List<Token> Tokenize(string s)
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

                if (char.IsDigit(c) || (c == '.' && i + 1 < s.Length && char.IsDigit(s[i + 1])))
                {
                    var start = i;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.'))
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenKind.Number, s[start..i], start));
                    continue;
                }

                if (char.IsLetter(c) || c == '_')
                {
                    var start = i;
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_'))
                    {
                        i++;
                    }

                    tokens.Add(new Token(TokenKind.Ident, s[start..i].ToLowerInvariant(), start));
                    continue;
                }

                if (i + 1 < s.Length && TwoCharOps.Contains(s.Substring(i, 2)))
                {
                    tokens.Add(new Token(TokenKind.Op, s.Substring(i, 2), i));
                    i += 2;
                    continue;
                }

                switch (c)
                {
                    case '+' or '-' or '*' or '/' or '%' or '<' or '>' or '!':
                        tokens.Add(new Token(TokenKind.Op, c.ToString(), i));
                        break;
                    case '(':
                        tokens.Add(new Token(TokenKind.LParen, "(", i));
                        break;
                    case ')':
                        tokens.Add(new Token(TokenKind.RParen, ")", i));
                        break;
                    case ',':
                        tokens.Add(new Token(TokenKind.Comma, ",", i));
                        break;
                    case '=':
                        throw new ExpressionException($"Single '=' at position {i + 1}: use '==' to compare.");
                    case '&':
                        throw new ExpressionException($"Single '&' at position {i + 1}: use '&&' (or 'and').");
                    case '|':
                        throw new ExpressionException($"Single '|' at position {i + 1}: use '||' (or 'or').");
                    default:
                        throw new ExpressionException($"Unexpected character '{c}' at position {i + 1}.");
                }

                i++;
            }

            tokens.Add(new Token(TokenKind.End, "", s.Length));
            return tokens;
        }
    }

    // --------------------------------------------------------------- parser

    private sealed class Parser(List<Token> tokens)
    {
        private int _pos;

        private Token Peek => tokens[_pos];

        public Node ParseAll()
        {
            var node = ParseOr();
            if (Peek.Kind != TokenKind.End)
            {
                throw new ExpressionException($"Unexpected '{Peek.Text}' at position {Peek.Pos + 1}.");
            }

            return node;
        }

        private Node ParseOr()
        {
            var left = ParseAnd();
            while (IsOp("||") || IsWord("or"))
            {
                _pos++;
                left = new Binary("||", left, ParseAnd());
            }

            return left;
        }

        private Node ParseAnd()
        {
            var left = ParseComparison();
            while (IsOp("&&") || IsWord("and"))
            {
                _pos++;
                left = new Binary("&&", left, ParseComparison());
            }

            return left;
        }

        private Node ParseComparison()
        {
            var left = ParseAdditive();
            if (Peek.Kind == TokenKind.Op && Peek.Text is "==" or "!=" or "<" or "<=" or ">" or ">=")
            {
                var op = tokens[_pos++].Text;
                left = new Binary(op, left, ParseAdditive());
            }

            return left;
        }

        private Node ParseAdditive()
        {
            var left = ParseMultiplicative();
            while (IsOp("+") || IsOp("-"))
            {
                var op = tokens[_pos++].Text;
                left = new Binary(op, left, ParseMultiplicative());
            }

            return left;
        }

        private Node ParseMultiplicative()
        {
            var left = ParseUnary();
            while (IsOp("*") || IsOp("/") || IsOp("%"))
            {
                var op = tokens[_pos++].Text;
                left = new Binary(op, left, ParseUnary());
            }

            return left;
        }

        private Node ParseUnary()
        {
            if (IsOp("!") || IsWord("not"))
            {
                _pos++;
                return new Unary("!", ParseUnary());
            }

            if (IsOp("-"))
            {
                _pos++;
                return new Unary("-", ParseUnary());
            }

            return ParsePrimary();
        }

        private Node ParsePrimary()
        {
            var t = Peek;
            switch (t.Kind)
            {
                case TokenKind.Number:
                    _pos++;
                    if (!double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                    {
                        throw new ExpressionException($"'{t.Text}' is not a valid number.");
                    }

                    return new Number(n);

                case TokenKind.LParen:
                    _pos++;
                    var inner = ParseOr();
                    Expect(TokenKind.RParen, "')'");
                    return inner;

                case TokenKind.Ident:
                    _pos++;
                    return t.Text switch
                    {
                        "x" => new Variable(isPrev: false),
                        "prev" => new Variable(isPrev: true),
                        "true" => new Number(1),
                        "false" => new Number(0),
                        _ when FunctionArity.ContainsKey(t.Text) => ParseCall(t),
                        _ => throw new ExpressionException(
                            $"Unknown name '{t.Text}'. Use x (current value) or prev (previous value)."),
                    };

                case TokenKind.End:
                    throw new ExpressionException("Expression ends too early.");

                default:
                    throw new ExpressionException($"Unexpected '{t.Text}' at position {t.Pos + 1}.");
            }
        }

        private Node ParseCall(Token name)
        {
            Expect(TokenKind.LParen, $"'(' after {name.Text}");
            var args = new List<Node>();
            if (Peek.Kind != TokenKind.RParen)
            {
                args.Add(ParseOr());
                while (Peek.Kind == TokenKind.Comma)
                {
                    _pos++;
                    args.Add(ParseOr());
                }
            }

            Expect(TokenKind.RParen, "')'");
            var arity = FunctionArity[name.Text];
            if (args.Count != arity)
            {
                throw new ExpressionException($"{name.Text}() takes {arity} argument(s), got {args.Count}.");
            }

            return new Call(name.Text, args.ToArray());
        }

        private void Expect(TokenKind kind, string what)
        {
            if (Peek.Kind != kind)
            {
                throw new ExpressionException($"Expected {what} at position {Peek.Pos + 1}.");
            }

            _pos++;
        }

        private bool IsOp(string op) => Peek.Kind == TokenKind.Op && Peek.Text == op;

        private bool IsWord(string word) => Peek.Kind == TokenKind.Ident && Peek.Text == word;
    }
}
