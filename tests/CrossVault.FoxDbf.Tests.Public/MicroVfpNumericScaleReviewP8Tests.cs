namespace CrossVault.FoxDbf.Tests;
public sealed class MicroVfpNumericScaleReviewP8Tests
{
    internal static readonly (string Name, string Script, string Expected)[] Evidence =
    [
        ("fixed_ON1.50", "SET DECIMALS TO 4\nSET FIXED ON\nx=1.50\nresult=TRANSFORM(x)+'|'+STR(x)+'|'+STR(x,12)+'|'+STR(x,12,2)+'|'+SET('FIXED')", "1.5000|         2|           2|        1.50|ON"),
        ("fixed_ON2", "SET DECIMALS TO 4\nSET FIXED ON\nx=2\nresult=TRANSFORM(x)+'|'+STR(x)+'|'+STR(x,12)+'|'+STR(x,12,2)+'|'+SET('FIXED')", "2.0000|         2|           2|        2.00|ON"),
        ("fixed_ON$1.5", "SET DECIMALS TO 4\nSET FIXED ON\nx=$1.5\nresult=TRANSFORM(x)+'|'+STR(x)+'|'+STR(x,12)+'|'+STR(x,12,2)+'|'+SET('FIXED')", "$1.5000|         2|           2|        1.50|ON"),
        ("fixed_ON1.123456", "SET DECIMALS TO 4\nSET FIXED ON\nx=1.123456\nresult=TRANSFORM(x)+'|'+STR(x)+'|'+STR(x,12)+'|'+STR(x,12,2)+'|'+SET('FIXED')", "1.1235|         1|           1|        1.12|ON"),
        ("fixed_ONSIN(1)", "SET DECIMALS TO 4\nSET FIXED ON\nx=SIN(1)\nresult=TRANSFORM(x)+'|'+STR(x)+'|'+STR(x,12)+'|'+STR(x,12,2)+'|'+SET('FIXED')", "0.8415|         1|           1|        0.84|ON"),
        ("fixed_OFF1.50", "SET DECIMALS TO 4\nSET FIXED OFF\nx=1.50\nresult=TRANSFORM(x)+'|'+STR(x)+'|'+STR(x,12)+'|'+STR(x,12,2)+'|'+SET('FIXED')", "1.50|         2|           2|        1.50|OFF"),
        ("fixed_OFF2", "SET DECIMALS TO 4\nSET FIXED OFF\nx=2\nresult=TRANSFORM(x)+'|'+STR(x)+'|'+STR(x,12)+'|'+STR(x,12,2)+'|'+SET('FIXED')", "2|         2|           2|        2.00|OFF"),
        ("fixed_OFF$1.5", "SET DECIMALS TO 4\nSET FIXED OFF\nx=$1.5\nresult=TRANSFORM(x)+'|'+STR(x)+'|'+STR(x,12)+'|'+STR(x,12,2)+'|'+SET('FIXED')", "$1.5000|         2|           2|        1.50|OFF"),
        ("fixed_OFF1.123456", "SET DECIMALS TO 4\nSET FIXED OFF\nx=1.123456\nresult=TRANSFORM(x)+'|'+STR(x)+'|'+STR(x,12)+'|'+STR(x,12,2)+'|'+SET('FIXED')", "1.123456|         1|           1|        1.12|OFF"),
        ("fixed_OFFSIN(1)", "SET DECIMALS TO 4\nSET FIXED OFF\nx=SIN(1)\nresult=TRANSFORM(x)+'|'+STR(x)+'|'+STR(x,12)+'|'+STR(x,12,2)+'|'+SET('FIXED')", "0.8415|         1|           1|        0.84|OFF"),
        ("output_ON1.5", "SET DECIMALS TO 4\nSET FIXED ON\nx=1.5\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? x\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n         1.5000\u001a"),
        ("output_ON2", "SET DECIMALS TO 4\nSET FIXED ON\nx=2\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? x\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n         2.0000\u001a"),
        ("output_ON-1.5", "SET DECIMALS TO 4\nSET FIXED ON\nx=-1.5\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? x\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n         -1.5000\u001a"),
        ("output_ON1.123456", "SET DECIMALS TO 4\nSET FIXED ON\nx=1.123456\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? x\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n           1.1235\u001a"),
        ("output_ON$1.5", "SET DECIMALS TO 4\nSET FIXED ON\nx=$1.5\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? x\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n               1.5000\u001a"),
        ("output_ONSIN(1)", "SET DECIMALS TO 4\nSET FIXED ON\nx=SIN(1)\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? x\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n         0.8415\u001a"),
        ("output_ON12345.67", "SET DECIMALS TO 4\nSET FIXED ON\nx=12345.67\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? x\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n     12345.6700\u001a"),
        ("output_OFF1.5", "SET DECIMALS TO 4\nSET FIXED OFF\nx=1.5\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? x\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n         1.5\u001a"),
        ("output_OFF2", "SET DECIMALS TO 4\nSET FIXED OFF\nx=2\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? x\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n         2\u001a"),
        ("output_OFF-1.5", "SET DECIMALS TO 4\nSET FIXED OFF\nx=-1.5\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? x\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n         -1.5\u001a"),
        ("output_OFF1.123456", "SET DECIMALS TO 4\nSET FIXED OFF\nx=1.123456\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? x\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n         1.123456\u001a"),
        ("output_OFF$1.5", "SET DECIMALS TO 4\nSET FIXED OFF\nx=$1.5\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? x\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n               1.5000\u001a"),
        ("output_OFFSIN(1)", "SET DECIMALS TO 4\nSET FIXED OFF\nx=SIN(1)\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? x\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n         0.8415\u001a"),
        ("output_OFF12345.67", "SET DECIMALS TO 4\nSET FIXED OFF\nx=12345.67\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? x\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n     12345.67\u001a"),
        ("print_ON", "SET DECIMALS TO 4\nSET FIXED ON\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? 1.5,2,$1.5,1.123456\n?? 'end'\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n 1.5000  2.0000                1.5000   1.1235end \u001a"),
        ("print_OFF", "SET DECIMALS TO 4\nSET FIXED OFF\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? 1.5,2,$1.5,1.123456\n?? 'end'\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n1.5 2                1.5000  1.123456end \u001a"),
        ("literal_1.1234", "SET DECIMALS TO 4\nSET FIXED OFF\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? 1.1234\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n 1.1234\u001a"),
        ("literal_1.12345", "SET DECIMALS TO 4\nSET FIXED OFF\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? 1.12345\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n 1.12345\u001a"),
        ("literal_1.123456", "SET DECIMALS TO 4\nSET FIXED OFF\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? 1.123456\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n 1.123456\u001a"),
        ("literal_12.34567", "SET DECIMALS TO 4\nSET FIXED OFF\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? 12.34567\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n 12.34567\u001a"),
        ("literal_-1.12345", "SET DECIMALS TO 4\nSET FIXED OFF\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? -1.12345\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n-1.12345\u001a"),
        ("literal_123456", "SET DECIMALS TO 4\nSET FIXED OFF\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? 123456\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n 123456\u001a"),
        ("literal_1.123", "SET DECIMALS TO 4\nSET FIXED OFF\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? 1.123\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n1.123\u001a"),
        ("literal_1.23", "SET DECIMALS TO 4\nSET FIXED OFF\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? 1.23\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n1.23\u001a"),
        ("moneyprint_2ON", "SET DECIMALS TO 2\nSET FIXED ON\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? $1.5\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n                 1.50\u001a"),
        ("moneyprint_2OFF", "SET DECIMALS TO 2\nSET FIXED OFF\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? $1.5\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n               1.5000\u001a"),
        ("moneyprint_6ON", "SET DECIMALS TO 6\nSET FIXED ON\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? $1.5\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n               1.5000\u001a"),
        ("moneyprint_6OFF", "SET DECIMALS TO 6\nSET FIXED OFF\nSET ALTERNATE TO printing.txt\nSET ALTERNATE ON\n? $1.5\nSET ALTERNATE OFF\nSET ALTERNATE TO\nresult=FILETOSTR('printing.txt')", "\r\n               1.5000\u001a"),
    ];
    public static IEnumerable<object[]> Cases => Evidence.Select(c => new object[] { c.Name, c.Script, c.Expected });
    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesVfp9(string name, string script, string expected)
    {
        string actual = MicroVfpNumericScaleReviewSupport.Run(script);
        Assert.True(expected == actual, $"{name}: expected [{expected}], actual [{actual}]");
    }
}
