using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed class IndexSafetyDtotTests
{
    [Theory]
    [InlineData("DTOT({^2000-02-29 12:34:56})")]
    [InlineData("DTOT('')")]
    [InlineData("DTOT(1)")]
    [InlineData("DTOT(.F.)")]
    public void Dtot_InvalidTypeRaisesEleven(string expression)
    {
        using var session = new VfpSession();
        using var interpreter = new VfpInterpreter(session);
        var error = Assert.Throws<MicroVfpRuntimeException>(() => interpreter.Execute("result = " + expression));
        Assert.Equal(11, error.VfpErrorNumber);
    }

    [Theory]
    [InlineData("DTOT({^2000-02-29})", "2000-02-29")]
    [InlineData("DTOT({})", null)]
    public void Dtot_ReturnsDateTimeAtMidnight_InterpretedCompiledAndHosted(string expression, string? date)
    {
        var expected = VfpValue.DateTime(date is null ? default : DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture));
        var parsed = VfpExpression.Parse(expression);
        Assert.Equal(expected, parsed.Evaluate(TestRow.Empty));
        Assert.Equal(expected, parsed.Compile()(TestRow.Empty));
        Assert.Equal(VfpType.DateTime, parsed.InferType(new TestSchema()).Type);
        using var session = new VfpSession();
        using var interpreter = new VfpInterpreter(session);
        Assert.Equal(expected, interpreter.EvalExpression(expression));
    }

    [Fact]
    public void Dtot_NullPropagates()
    {
        var parsed = VfpExpression.Parse("DTOT(.NULL.)");
        Assert.True(parsed.Evaluate(TestRow.Empty).IsNull);
        Assert.True(parsed.Compile()(TestRow.Empty).IsNull);
        using var session = new VfpSession();
        using var interpreter = new VfpInterpreter(session);
        interpreter.Execute("result = DTOT(.NULL.)");
        Assert.True(interpreter.EvalExpression("result").IsNull);
    }
}
