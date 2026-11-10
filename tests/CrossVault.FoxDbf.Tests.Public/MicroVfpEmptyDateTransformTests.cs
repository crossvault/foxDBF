namespace CrossVault.FoxDbf.Tests;

public sealed class MicroVfpEmptyDateTransformTests
{
    public static IEnumerable<object[]> Cases => from format in new[] { "AMERICAN", "GERMAN", "ANSI" }
        from century in new[] { "ON", "OFF" }
        from expression in new[] { "{}", "{//::}", "d", "t", "md", "mt", "fd", "ft" }
        select new object[] { format, century, expression };

    internal static string EmptyDate(string format, string century) => (format, century) switch
    {
        ("AMERICAN", "ON") => "  /  /    ",
        ("AMERICAN", "OFF") => "  /  /  ",
        ("GERMAN", "ON") => "  .  .    ",
        ("ANSI", "ON") => "    .  .  ",
        _ => "  .  .  "
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void TransformEmptyDates_PreservesSeparatorsAndCentury(string format, string century, string expression)
    {
        bool timestamp = expression is "{//::}" or "t" or "mt" or "ft";
        string expected = EmptyDate(format, century) + (timestamp ? "   :  :   AM" : "");
        string scenario = $"USE t\nGO TOP\nSET DATE {format}\nSET CENTURY {century}\nmd={{}}\nmt=CTOT('')\nfd=d\nft=t\nresult=TRANSFORM({expression})";
        Assert.Equal(expected, MicroVfpXbaseUpdateTests.Run("CREATE TABLE t (d D,t T)\nAPPEND BLANK", scenario));
    }
}
