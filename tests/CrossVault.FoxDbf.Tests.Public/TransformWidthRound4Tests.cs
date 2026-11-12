using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed partial class TransformWidthRound4Tests
{
    internal sealed record Probe(string Name, string Body);
    internal const string Setup = "CREATE TABLE q FREE (n N(10,2), s N(5,0), h N(12,4), w N(18,4), ii I, bb B, yy Y, ff F(12,4))\nINSERT INTO q VALUES (100.25,7,1.2345,123456.7891,5,2.5,3.1256,1.2345)\n";
    internal static Probe[] Probes(int setting, bool finite)
    {
        var probes = new List<Probe>();
        void Add(string name, string body) => probes.Add(new(name,
            $"IF USED('rr')\nUSE IN rr\nENDIF\nIF USED('outtab')\nUSE IN outtab\nENDIF\nSELECT q\nGO TOP\nSET DECIMALS TO {setting}\nmx=5\nmy=2.5\nmz=1/3\nx=1.2345\n" + body));
        void Select(string name, string expression, string setup = "", bool variants = false)
        {
            foreach (string variant in variants ? new[] { "single", "union", "group" } : new[] { "single" })
            foreach (string destination in new[] { "cursor", "array", "table" })
            {
                string branch = $"SELECT {expression} FROM q" + (name == "memvar-exact" ? "" : " WHERE n>0");
                string query = branch;
                if (variant == "union") query += " UNION ALL " + branch;
                if (variant == "group") query += " GROUP BY " +
                    (name.StartsWith("field/") || name.StartsWith("function/") ? name.Split('/')[1] : "n");
                string body = setup + "\n" + query;
                if (destination == "array")
                    body += " INTO ARRAY aa\nresult=''\nFOR j=1 TO ALEN(aa,1)\nFOR k=1 TO ALEN(aa,2)\nresult=result+VARTYPE(aa[j,k])+'|'+STR(aa[j,k],30,10)+'|'+TRANSFORM(aa[j,k])+';'\nENDFOR\nENDFOR";
                else
                {
                    string alias = destination == "cursor" ? "rr" : "outtab";
                    body += destination == "cursor" ? " INTO CURSOR rr" : " INTO TABLE outtab\nIF !USED('outtab')\nUSE outtab IN 0\nENDIF\nSELECT outtab";
                    body += $"\nDIMENSION ac[1]\n=AFIELDS(ac,'{alias}')\nresult=''\nFOR k=1 TO ALEN(ac,1)\nresult=result+ac[k,2]+'|'+TRANSFORM(ac[k,3])+'|'+TRANSFORM(ac[k,4])+';'\nENDFOR\nSELECT {alias}\nSCAN\nFOR k=1 TO ALEN(ac,1)\nv=EVALUATE(ac[k,1])\nresult=result+STR(v,30,10)+'|'+TRANSFORM(v)+';'\nENDFOR\nENDSCAN\nUSE IN {alias}";
                }
                Add(name + "/" + variant + "/" + destination, body);
            }
        }
        if (finite)
        {
            Select("memvar-exact", "mx AS a,my AS b,mz AS f,n*mx AS c,n+my AS d,mx/3 AS e,n/mx AS g,mx+my AS h", variants: true);
            foreach (string value in new[] { "5", "1.2", "1.23", "1.234", "1.2345", "1.23456", "1.234567", "1000000000000", "1000000000000000", "-1.234567", "-1000000000000", "-1000000000000000" })
                Select("memvar/" + value, "x AS a,n*x AS b,n+x AS c,x/3 AS d,n/x AS e,x+my AS f", "x=" + value, true);
            foreach (string field in new[] { "n", "s", "h", "w", "ii", "bb", "yy", "ff" })
            {
                Select("field/" + field, $"{field} AS a,{field}+2 AS b,{field}-2 AS c,{field}*2 AS d,{field}/3 AS e,{field}%2 AS f", variants: true);
                Select("function/" + field, $"INT({field}/3) AS a,ROUND({field}/3,2) AS b,ABS({field}/3) AS c,SQRT({field}) AS d,IIF(.T.,{field}/3,0) AS e,NVL(.NULL.,{field}/3) AS f", variants: true);
                Select("aggregate/" + field, $"SUM({field}) AS a,AVG({field}) AS b,MIN({field}) AS c,MAX({field}) AS d,COUNT({field}) AS e", variants: true);
                foreach (string expression in new[] { field, field + "/3", "INT(" + field + "/3)+1", "ROUND(" + field + "/3,2)+1" })
                foreach (string command in new[] { "SUM " + expression, "AVERAGE " + expression, "CALCULATE SUM(" + expression + ")", "CALCULATE AVG(" + expression + ")", "CALCULATE MIN(" + expression + ")", "CALCULATE MAX(" + expression + ")", "CALCULATE CNT(" + expression + ")" })
                    Add("command/" + command, command + " TO v\nresult=STR(v,30,10)+'|'+TRANSFORM(v)");
            }
            Select("quotient-functions", "LOG(n/3+10) AS a,EXP(n/30000) AS b,NVL(.NULL.,n/3) AS c,EVL(0,n/3) AS d", variants: true);
        }
        else
        {
            foreach (string operand in new[] { "1/0", "n/0", "SQRT(1.234567)/(n-n)" })
            foreach (string wrapper in new[] { "INT({0})", "ROUND({0},0)", "SQRT({0})", "MAX({0},1)", "NVL(.NULL.,{0})", "INT(NVL(.NULL.,{0}))" })
            foreach (string tail in new[] { "", "+1", "/2" })
            {
                string expression = string.Format(wrapper, operand) + tail;
                Select(expression, expression + " AS val", variants: true);
                Add(expression + "/sum", "SUM " + expression + " TO v\nresult=TRANSFORM(v)");
                Add(expression + "/calculate", "CALCULATE SUM(" + expression + ") TO v\nresult=TRANSFORM(v)");
            }
        }
        return probes.ToArray();
    }

    [Theory]
    [InlineData(0, true)] [InlineData(2, true)] [InlineData(4, true)]
    [InlineData(0, false)] [InlineData(2, false)] [InlineData(4, false)]
    public void Round4Cases_MatchExpectedVfp(int setting, bool finite)
    {
        using var dir = new MicroVfpTestSupport.TempDir("twidthround4");
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(Setup);
        var failures = new List<string>();
        foreach (var p in Probes(setting, finite))
        {
            string key = setting + "/" + finite + "/" + p.Name;
            string actual = TransformWidthRound3Tests.Observe(vfp, p.Body);
            if (!ExpectedVfp.TryGetValue(key, out var expected) || actual != expected)
                failures.Add(key + ": expected=" + expected + "; actual=" + actual);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(4)]
    public void FiniteResultAndReader_MatchExpectedVfp(int setting)
    {
        using var dir = new MicroVfpTestSupport.TempDir("twidthconsumers");
        using var connection = new FoxDbfConnection($"Data Source={dir.Path}");
        connection.Open();
        using var vfp = new VfpInterpreter(connection.Session);
        vfp.Execute(Setup);
        var failures = new List<string>();
        foreach (var p in Probes(setting, true).Where(p => p.Name.EndsWith("/cursor")))
        foreach (bool ado in new[] { false, true })
        {
            string key = setting + "/True/" + p.Name;
            string actual = TransformWidthRound4Consumers.Observe(vfp, connection, p.Body, ado);
            if (ExpectedVfp[key] != actual) failures.Add(key + "/" + ado + ": " + actual);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Theory]
    [InlineData("SUM(ii)", typeof(int))]
    [InlineData("n/0", typeof(decimal))]
    public void NonFiniteTypedGetters_ThrowOverflow(string expression, Type schemaType)
    {
        using var dir = new MicroVfpTestSupport.TempDir("twidthgetters");
        using (var session = new VfpSession())
        {
            session.OpenDirectory(dir.Path);
            session.Execute("CREATE TABLE q (ii I,n N(10,2))");
            session.Execute("INSERT INTO q VALUES (2000000000,1)");
            session.Execute("INSERT INTO q VALUES (2000000000,1)");
        }
        using var connection = new FoxDbfConnection($"Data Source={dir.Path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + expression + " FROM q";
        using var reader = command.ExecuteReader();
        Assert.Equal(schemaType, reader.GetFieldType(0));
        Assert.Equal(schemaType, reader.GetSchemaTable()!.Rows[0]["DataType"]);
        Assert.True(reader.Read());
        Assert.False(double.IsFinite(Assert.IsType<double>(reader.GetValue(0))));
        Assert.False(double.IsFinite(reader.GetDouble(0)));
        Assert.Throws<OverflowException>(() => reader.GetInt16(0));
        Assert.Throws<OverflowException>(() => reader.GetInt32(0));
        Assert.Throws<OverflowException>(() => reader.GetInt64(0));
        Assert.Throws<OverflowException>(() => reader.GetDecimal(0));
        Assert.Throws<OverflowException>(() => reader.GetByte(0));
        Assert.Throws<OverflowException>(() => reader.GetFloat(0));
        Assert.Throws<OverflowException>(() => reader.GetBoolean(0));
        Assert.Throws<OverflowException>(() => reader.GetString(0));
        Assert.Throws<OverflowException>(() => reader.GetChar(0));
        Assert.Throws<OverflowException>(() => reader.GetDateTime(0));
        Assert.Throws<OverflowException>(() => reader.GetGuid(0));
        Assert.Throws<OverflowException>(() => reader.GetBytes(0, 0, null, 0, 0));
        Assert.Throws<OverflowException>(() => reader.GetChars(0, 0, null, 0, 0));
        Assert.Throws<OverflowException>(() => reader.GetFieldValue<int>(0));
        Assert.Throws<OverflowException>(() => reader.GetFieldValue<decimal>(0));
    }
}
