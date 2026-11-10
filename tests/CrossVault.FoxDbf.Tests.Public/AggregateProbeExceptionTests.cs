using CrossVault.FoxDbf.Expressions;
namespace CrossVault.FoxDbf.Tests;

public sealed class AggregateProbeExceptionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataProbe_DoesNotSuppressHostFailure(bool cancellation)
    {
        Exception error = cancellation ? new OperationCanceledException() : new InvalidOperationException("Host failed");
        var expression = VfpExpression.Parse("IIF(.T.,1,broken)");
        var caught = Record.Exception(() => expression.ProbeAggregate<CrossVault.FoxDbf.MicroVfp.MicroVfpRuntimeException>(new TestSchema(), new FailingRow(error), EvaluationContext.Default));
        Assert.Same(error, caught);
    }

    private sealed class FailingRow(Exception error) : IRowContext
    {
        public object? GetField(string name) => throw error;
        public int RecNo => 1;
        public bool Deleted => false;
        public int RecCount => 1;
    }
}
