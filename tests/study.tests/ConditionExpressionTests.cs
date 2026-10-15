using Study.Core.Expressions;

namespace Study.Tests;

public class ConditionExpressionTests
{
    [Theory]
    [InlineData("x > 100", 101, 0, true)]
    [InlineData("x > 100", 100, 0, false)]
    [InlineData("x >= 10 && x <= 20", 15, 0, true)]
    [InlineData("x >= 10 and x <= 20", 25, 0, false)]
    [InlineData("x < 0 || x > 50", -1, 0, true)]
    [InlineData("x < 0 or x > 50", 10, 0, false)]
    [InlineData("!x", 0, 0, true)]
    [InlineData("not x", 1, 0, false)]
    [InlineData("x == true", 1, 0, true)]
    [InlineData("x != false", 0, 0, false)]
    [InlineData("x % 2 == 0", 4, 0, true)]
    [InlineData("(x + 2) * 3 == 15", 3, 0, true)]
    [InlineData("x - prev > 5", 20, 10, true)]
    [InlineData("prev < 100 && x >= 100", 100, 99, true)]
    [InlineData("abs(x - prev) > 3", 1, 5, true)]
    [InlineData("max(x, prev) == 9", 2, 9, true)]
    [InlineData("min(x, prev) == 2", 2, 9, true)]
    [InlineData("bit(x, 3)", 8, 0, true)]
    [InlineData("bit(x, 3)", 7, 0, false)]
    [InlineData("-x > 0", -5, 0, true)]
    [InlineData("x / 0 == 0", 5, 0, true)]
    [InlineData("X > 1", 2, 0, true)]
    [InlineData("x > 1.5", 1.6, 0, true)]
    public void Evaluates(string expr, double x, double prev, bool expected)
    {
        Assert.Equal(expected, ConditionExpression.Parse(expr).IsTrue(x, prev));
    }

    [Fact]
    public void Precedence_MultiplicationBeforeAddition()
    {
        Assert.Equal(7, ConditionExpression.Parse("1 + 2 * 3").Evaluate(0, 0));
    }

    [Fact]
    public void Precedence_AndBeforeOr()
    {
        // true || (false && false) = true
        Assert.True(ConditionExpression.Parse("1 || 0 && 0").IsTrue(0, 0));
    }

    [Fact]
    public void ShortCircuit_AvoidsRightSide()
    {
        Assert.False(ConditionExpression.Parse("prev != 0 && x / prev > 2").IsTrue(10, 0));
    }

    [Theory]
    [InlineData("x = 1", "==")]
    [InlineData("x & 1", "&&")]
    [InlineData("x | 1", "||")]
    [InlineData("y > 1", "Unknown name 'y'")]
    [InlineData("x >", "ends too early")]
    [InlineData("(x > 1", "')'")]
    [InlineData("x > 1)", "Unexpected")]
    [InlineData("abs(x, 1)", "takes 1 argument")]
    [InlineData("x $ 1", "Unexpected character")]
    [InlineData("1.2.3 > x", "not a valid number")]
    [InlineData("", "empty")]
    public void RejectsWithHelpfulMessage(string expr, string fragment)
    {
        var ex = Assert.Throws<ExpressionException>(() => ConditionExpression.Parse(expr));
        Assert.Contains(fragment, ex.Message);
    }
}
