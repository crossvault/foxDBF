using System.Text;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

public sealed partial class TransformWidthRound10Tests
{
    internal sealed record Probe(string Key, string Body, string? RawTable = null);
    internal const string Functions = "FUNCTION idf(p)\nRETURN p\nENDFUNC\n"
        + "FUNCTION one(p)\nIF p>2\nRETURN 100000\nENDIF\nRETURN 1\nENDFUNC\n"
        + "FUNCTION half(p)\nRETURN p/2\nENDFUNC\n";
    private const string Q = "CREATE TABLE q FREE (a N(18,4), c Y, i I, b B(4), n N(10,2))\n"
        + "INSERT INTO q VALUES (9999999999999.9999,92233720368547.5807,2147483647,1.5e15,100)\n"
        + "INSERT INTO q VALUES (9999999999999.9999,-92233720368547.5807,2147483647,1.5e15,-7)\n"
        + "INSERT INTO q VALUES (0.0001,0.0001,-2147483647,0.3333,0)\n"
        + "INSERT INTO q VALUES (1234567890123.5678,1234.5678,3,-2.25,1)\n"
        + "INSERT INTO q VALUES (0.1,0.1,7,0.1,12345.67)\nGO TOP\n";
    private const string W = "CREATE TABLE w FREE (k I,a N(20,4),b N(20,4),s N(3,0),y Y,f B(2))\n"
        + "INSERT INTO w VALUES (1,123456789012345.6789,0,5,1234.5678,0.1)\n"
        + "INSERT INTO w VALUES (2,-999999999999997.5,0,50,92233720368.5477,123456789012.345)\n"
        + "INSERT INTO w VALUES (3,99999999999.9999,0,999,0.0001,1e17)\n"
        + "INSERT INTO w VALUES (4,12345678901.2345,0,1,-5,-2.5)\n"
        + "INSERT INTO w VALUES (5,1,0,120,1,7)\nGO TOP\n";

    private static string Display(string expression, int scale = 4) =>
        $"TRANSFORM({expression})+'/'+ALLTRIM(STR({expression},40,{scale}))";

    internal static Probe[] Probes(int decimals)
    {
        var probes = new List<Probe>();
        void Add(string key, string body, string? raw = null) => probes.Add(new(
            $"{decimals}/{key}", $"CLOSE TABLES ALL\nSET FIXED OFF\nSET DECIMALS TO {decimals}\nresult=''\n" + body, raw));
        foreach (string field in new[] { "i20", "n20" })
        {
            int scale = field == "i20" ? 0 : 4;
            string literal = field == "i20" ? "123456789012345678" : "123456789012345.6789";
            string source = "USE seed\nGO TOP\n";
            Add($"S1/{field}/read-memvar", source + $"m={field}\nresult={Display(field, scale)}+'|'+{Display("m", scale)}");
            foreach (string destination in new[] { "CURSOR", "TABLE", "ARRAY" })
            {
                string value = destination == "ARRAY" ? "aa[1]" : "val";
                Add($"S1/{field}/select-{destination}", source
                    + $"SELECT {field} AS val FROM seed INTO {destination} " + (destination == "ARRAY" ? "aa" : "outval")
                    + (destination == "TABLE" ? "\nUSE outval" : "")
                    + $"\nresult={Display(value, scale)}", destination == "TABLE" ? "outval" : null);
            }
            foreach (string path in new[] { "insert-memvar", "insert-literal", "replace" })
                Add($"S1/{field}/{path}", source + $"m={field}\nCREATE TABLE outval FREE (val N(20,{scale}))\n"
                    + (path == "replace" ? $"APPEND BLANK\nREPLACE val WITH m\n" : $"INSERT INTO outval VALUES ({(path == "insert-memvar" ? "m" : literal)})\n")
                    + $"result={Display("val", scale)}", "outval");
            Add($"S1/{field}/copy", source + $"COPY TO outval\nUSE outval\nresult={Display(field, scale)}", "outval");
            Add($"S1/{field}/seek", source + $"COPY TO outval\nUSE outval\nINDEX ON {field} TAG keyval\nm={field}\n"
                + $"result=TRANSFORM(SEEK(m))+'|'+TRANSFORM(SEEK({literal}))");
            Add($"S1/{field}/bits", source + $"bits=BINTOC({field},'B')\nFOR j=1 TO 8\nresult=result+TRANSFORM(ASC(SUBSTR(bits,j,1)))+','\nENDFOR");
        }
        foreach (string literal in new[] { "92233720368547.5807", "12345678901234.5678", "9999999999999.9999" })
        foreach (string sign in new[] { "", "-" })
        {
            string number = sign + literal;
            Add($"S2/{number}", "CREATE TABLE outval FREE (c Y)\n"
                + $"INSERT INTO outval VALUES ({number})\nm={number}\nINSERT INTO outval VALUES (m)\n"
                + $"APPEND BLANK\nREPLACE c WITH {number}\nGO TOP\nresult={Display("m")}+'|'\n"
                + $"SCAN\nv=c\nresult=result+{Display("c")}+';'+{Display("v")}+'|'\nENDSCAN", "outval");
        }
        foreach (string aggregate in new[] { "SUM(c)", "AVG(c)" })
        {
            Add("S2/sql-" + aggregate, Q + $"SELECT {aggregate} AS val FROM q INTO TABLE outval\nUSE outval\nresult={Display("val")}", "outval");
            Add("S2/calculate-" + aggregate, Q + $"CALCULATE {aggregate} TO m\nresult={Display("m")}");
        }
        Add("S2/sum-command", Q + $"SUM c TO m\nresult={Display("m")}");
        Add("S2/array-cancellation", Q + "SELECT c FROM q INTO ARRAY aa\nresult=" + Display("aa[1]+aa[2]"));
        Add("S3/transform-projection", Q + "SELECT TRANSFORM(a) AS val FROM q INTO CURSOR rr\n"
            + "=AFIELDS(ff)\nresult=TRANSFORM(ff[1,3])+'|'\nSCAN\nresult=result+val+';'\nENDSCAN");
        Add("S4/direct-scan", Q + "SCAN\nm=a\nresult=result+TRANSFORM(a)+'|'+TRANSFORM(m)+';'\nENDSCAN");
        Add("S4/sql-memvar", Q + "m=a\nSELECT TRANSFORM(m) AS val FROM q INTO CURSOR rr\nresult=val+'|'+TRANSFORM(m)");
        Add("S4/where", Q + "SELECT COUNT(*) AS val FROM q WHERE TRANSFORM(a)=='*************.****' INTO ARRAY aa\nresult=TRANSFORM(aa[1])");
        Add("S5/udf", W + "SELECT idf(a) AS u4,idf(f) AS u6 FROM w INTO CURSOR rr\nSCAN\nresult=result+TRANSFORM(u4)+'|'+TRANSFORM(u6)+';'\nENDSCAN");
        Add("S5/group-max", W + "SELECT k%2 AS g,MAX(idf(a)) AS ma FROM w GROUP BY 1 INTO CURSOR rr\nSCAN\nresult=result+TRANSFORM(ma)+';'\nENDSCAN");
        Add("S5/reviewer00", W + "SELECT k,one(k) AS u1,idf(s) AS u2,half(k) AS u3,idf(a) AS u4,EVALUATE('s*2') AS e1,EVALUATE('1/3') AS e2,EVALUATE('k') AS e3,idf(y) AS u5,idf(f) AS u6 FROM w INTO CURSOR rr\nSCAN\nresult=result+TRANSFORM(u4)+'|'+TRANSFORM(u6)+';'\nENDSCAN");
        Add("S5/reviewer07", W + "SELECT k%2 AS g,SUM(one(k)) AS su,MAX(idf(a)) AS ma FROM w GROUP BY 1 INTO CURSOR rr\nSCAN\nresult=result+TRANSFORM(ma)+';'\nENDSCAN");
        Add("S5/schema", W + "SELECT idf(a) AS u4,idf(f) AS u6 FROM w INTO CURSOR rr\n=AFIELDS(ff)\nFOR j=1 TO 2\nresult=result+ff[j,2]+':'+TRANSFORM(ff[j,3])+','+TRANSFORM(ff[j,4])+';'\nENDFOR\nSCAN\nresult=result+STR(u6,30,4)+';'\nENDSCAN");
        Add("S5/group-schema", W + "SELECT k%2 AS g,MAX(idf(a)) AS ma FROM w GROUP BY 1 INTO CURSOR rr\n=AFIELDS(ff)\nresult=ff[2,2]+':'+TRANSFORM(ff[2,3])+','+TRANSFORM(ff[2,4])");
        Add("S5/udf-table", W + "SELECT idf(f) AS val FROM w INTO TABLE outval\nUSE outval\nSCAN\nresult=result+TRANSFORM(val)+'/'+STR(val,30,4)+';'\nENDSCAN", "outval");
        Add("S5/str-scientific", "result=STR(1e17,13,2)+'|'+STR(1e17,10,0)+'|'+STR(1e17,3,0)");
        Add("S5/int32-short-picture", string.Join("\n", new[] { "99999999", "100000000", "999999999", "1000000000", "2147483647", "2147483648", "-1000000000", "1000000000.1" }
            .SelectMany(number => new[] { 6, 7, 8, 9, 10 }.Select(width => $"result=result+'{number}/{width}='+STR({number},{width},0)+';'"))));
        foreach (string number in new[] { "9999999", "1e9", "1e10", "1e12", "1e17" })
        foreach (int width in new[] { 6, 8, 10, 13 })
            Add($"S5/scientific-zero-scale/{number}/{width}", $"CREATE TABLE outval FREE (n N({width},0))\nAPPEND BLANK\nTRY\nREPLACE n WITH {number}\nresult='ok'\nCATCH TO failure\nresult='error='+TRANSFORM(failure.ErrorNo)\nENDTRY\nresult=result+'|'+TRANSFORM(n)+'/'+ALLTRIM(STR(n,30,0))+'|literal='+STR({number},{width},0)", "outval");
        foreach (string number in new[] { "1e9", "1e10", "1e12", "1e15", "1e16", "1e17", "-1e17", "1e100", "9999999", "99999999" })
            Add("S5/scientific/" + number, string.Join("\n", new[] { 3, 6, 7, 8, 10, 13 }
                .Select(width => $"result=result+STR({number},{width},2)+'|'")));
        foreach (string number in new[] { "9999999", "1e9", "1e10", "1e12", "1e17" })
        foreach (int width in new[] { 6, 8, 10, 13 })
            Add($"S5/scientific-write/{number}/{width}", $"CREATE TABLE outval FREE (n N({width},2))\nAPPEND BLANK\nTRY\nREPLACE n WITH {number}\nresult='ok'\nCATCH TO failure\nresult='error='+TRANSFORM(failure.ErrorNo)\nENDTRY\nresult=result+'|'+TRANSFORM(n)", "outval");
        foreach (string aggregate in new[] { "SUM(a/3)", "SUM(b/3)", "AVG(b)", "SUM(b)", "AVG(a)" })
        {
            Add("S6/sql-" + aggregate, Q + $"SELECT {aggregate} AS val FROM q INTO CURSOR rr\nresult={Display("val", 10)}");
            Add("S6/calculate-" + aggregate, Q + $"CALCULATE {aggregate} TO m\nresult={Display("m", 10)}");
        }
        Add("S6/b-multiply", W + "GO 3\nz=f\nresult=TRANSFORM(z)+'|'+TRANSFORM(z*1)");
        Add("S6/seek-literal", W + "INDEX ON a TAG keyval\nresult=TRANSFORM(SEEK(123456789012345.6789))");
        Add("S6/nvl-overflow", "CREATE TABLE t FREE (n N(3,0))\nINSERT INTO t VALUES (1000)\nresult=TRANSFORM(NVL(n/3,0)-1)");
        Add("S6/nvl-column", "CREATE TABLE t FREE (n N(3,0))\nINSERT INTO t VALUES (1000)\nSELECT NVL(n/3,0)-1 AS val FROM t INTO CURSOR rr\nresult=TRANSFORM(val)");
        Add("S7/str-field", W + "GO 3\nresult=STR(a,30,6)");
        // S8: raw N(18,4)/N(19,5)/F(20,4) text above the round10 1e12 boundary (Prepare's seed2).
        // SCATTER/GATHER must read the field like m=field; the SELECT copies store their own text.
        var large = new[] { (Name: "a", Scale: 4), (Name: "b", Scale: 5), (Name: "fb", Scale: 4) };
        string Each(Func<string, int, int, string> line) => string.Concat(large.Select((f, i) => "\nresult=result+" + line(f.Name, f.Scale, i + 1) + "+';'"));
        Add("S8/read", "USE seed2\nGO 1\nx1=a\nx2=b\nx3=fb\nSCATTER MEMVAR\nSCATTER TO arr\nSCATTER NAME oo" + Each((f, s, i) =>
            $"{Display(f, s)}+'|'+{Display("x" + i, s)}+'|'+{Display("m." + f, s)}+'|'+{Display($"arr[{i}]", s)}+'|'+{Display("oo." + f, s)}"
            + $"+'|'+TRANSFORM(x{i}={f})+TRANSFORM(m.{f}={f})+TRANSFORM(arr[{i}]={f})+TRANSFORM(oo.{f}={f})"));
        Add("S8/gather-memvar", "USE gm\nSCATTER MEMVAR\nGATHER MEMVAR\nresult=''" + Each((f, s, i) => Display(f, s)), "gm");
        Add("S8/gather-array", "USE ga\nSCATTER TO arr\nGATHER FROM arr\nresult=''" + Each((f, s, i) => Display(f, s)), "ga");
        Add("S8/replace-self", "USE rp\nREPLACE a WITH a, b WITH b, fb WITH fb\nresult=''" + Each((f, s, i) => Display(f, s)), "rp");
        Add("S8/insert-scatter", "USE seed2\nSCATTER MEMVAR\nCREATE TABLE outval FREE (a N(18,4), b N(19,5), fb F(20,4))\nINSERT INTO outval VALUES (m.a, m.b, m.fb)\nresult=''"
            + Each((f, s, i) => Display(f, s)), "outval");
        Add("S8/select-star-table", "SELECT * FROM seed2 INTO TABLE outval\nUSE outval\nresult=''" + Each((f, s, i) => Display(f, s)), "outval");
        // fb AS fb is still open (F alias columns are CLR double; VFP9 copies the text .5679).
        Add("S8/select-as-table", "SELECT a AS a, b AS b FROM seed2 INTO TABLE outval\nUSE outval\nresult=''"
            + string.Concat(large.Take(2).Select(f => "\nresult=result+" + Display(f.Name, f.Scale) + "+';'")), "outval");
        Add("S8/select-star-cursor", "SELECT * FROM seed2 INTO CURSOR rr\nresult=''" + Each((f, s, i) => Display(f, s)));
        Add("S8/scan", "USE seed2\nSCAN\nresult=''" + Each((f, s, i) => Display(f, s)) + "\nENDSCAN");
        // S9: ordinary N/F and B/Y fields (Prepare's seed3, three rows) through every SCATTER form.
        string[] small = { "n1", "n2", "f1", "f2", "b1", "b2", "y" };
        string Cell(string e, string field) => $"TRANSFORM({e})+'/'+STR({e},20,6)+'/'+TRANSFORM({e}{(field == "y" ? "+0" : "*3")})+'/'+VARTYPE({e})+'/'+TRANSFORM({e}={field})";
        var read = new StringBuilder("USE seed3\nSCAN\n");
        for (int i = 0; i < small.Length; i++) read.Append($"x{i + 1}={small[i]}\n");
        read.Append("SCATTER MEMVAR\nSCATTER TO arr\nSCATTER NAME oo\n");
        for (int i = 0; i < small.Length; i++)
            foreach (string e in new[] { small[i], "x" + (i + 1), "m." + small[i], $"arr[{i + 1}]", "oo." + small[i] })
                read.Append("result=result+").Append(Cell(e, small[i])).Append("+'|'\n");
        Add("S9/read", read.Append("result=result+';'\nENDSCAN").ToString());
        Add("S9/gather-memvar", "USE g3m\nSCAN\nSCATTER MEMVAR\nGATHER MEMVAR\nENDSCAN\nresult='ok'", "g3m");
        Add("S9/gather-array", "USE g3a\nSCAN\nSCATTER TO arr\nGATHER FROM arr\nENDSCAN\nresult='ok'", "g3a");
        Add("S9/gather-name", "USE g3o\nSCAN\nSCATTER NAME oo\nGATHER NAME oo\nENDSCAN\nresult='ok'", "g3o");
        // S10: SQL copies of raw F(20,4) text (Prepare's fd above 1e12: .5678 and .5679 are one double,
        // .5671 and .5674 two that share sixteen digits; fs: sixteen digits below 1e12).
        const string Rows = "\nSCAN\nresult=result+TRANSFORM(fb)+'/'+STR(fb,25,4)+';'\nENDSCAN";
        Add("S10/distinct-cursor", "SELECT DISTINCT fb FROM fd INTO CURSOR rr\nresult=TRANSFORM(RECCOUNT())+'|'" + Rows);
        Add("S10/distinct-table", "SELECT DISTINCT fb FROM fd INTO TABLE outval\nUSE outval\nresult=TRANSFORM(RECCOUNT())+'|'" + Rows, "outval");
        foreach (var (left, right) in new[] { (1, 2), (2, 3), (3, 4) })
            Add($"S10/union-{left}{right}", $"SELECT fb FROM fd WHERE k={left} UNION SELECT fb FROM fd WHERE k={right} INTO TABLE outval\nUSE outval\nresult=TRANSFORM(RECCOUNT())+'|'" + Rows, "outval");
        Add("S10/group", "SELECT fb, COUNT(*) AS n FROM fd GROUP BY fb INTO TABLE outval\nUSE outval\nresult=TRANSFORM(RECCOUNT())+'|'" + Rows, "outval");
        Add("S10/order", "SELECT k, fb FROM fd ORDER BY fb DESC, k INTO TABLE outval\nUSE outval\nresult=''" + Rows, "outval");
        foreach (string table in new[] { "fd", "fs" })
        {
            Add($"S10/{table}/read", $"USE {table}\nSCAN\nm=fb\nresult=result+TRANSFORM(fb)+'/'+STR(fb,25,4)+'/'+TRANSFORM(m)+'/'+STR(m,25,4)+';'\nENDSCAN");
            Add($"S10/{table}/star-table", $"SELECT * FROM {table} INTO TABLE outval\nUSE outval\nresult=''" + Rows, "outval");
            Add($"S10/{table}/field-table", $"SELECT fb FROM {table} INTO TABLE outval\nUSE outval\nresult=''" + Rows, "outval");
            Add($"S10/{table}/field-cursor", $"SELECT fb FROM {table} INTO CURSOR rr\nresult=''" + Rows);
            Add($"S10/{table}/array", $"USE {table}\nSELECT fb FROM {table} INTO ARRAY ab\nSELECT {table}\nFOR j=1 TO ALEN(ab)\nGO j\n"
                + "result=result+TRANSFORM(ab[j])+'/'+STR(ab[j],25,4)+'/'+TRANSFORM(ab[j]=fb)+';'\nENDFOR\nresult=result+TRANSFORM(ab[1]=ab[2])+TRANSFORM(ab[2]=ab[3])");
        }
        // S10: currency display of raw Y payloads (Prepare's yc).
        Add("S10/currency", "USE yc\nSCAN\nm=y\nresult=result+TRANSFORM(y)+'/'+STR(y,25,4)+'/'+STR(y,25,2)+'/'+STR(y,25,0)+'/'+TRANSFORM(m)+'/'+ALLTRIM(STR(m,40,4))+'/'+TRANSFORM(y*1)+';'\nENDSCAN");
        // S10: SCATTER ... BLANK value types (Prepare's sb: y Y, b B(2), n N(10,2), f F(10,3), i I).
        string Blank(Func<string, int, string> target) => string.Concat(new[] { "y", "b", "n", "f", "i" }.Select((f, i) =>
            $"\nresult=result+VARTYPE({target(f, i + 1)})+':'+TRANSFORM({target(f, i + 1)})+'/'+STR({target(f, i + 1)},20,4)+'/'+TRANSFORM({target(f, i + 1)}+1)+';'"));
        Add("S10/scatter-blank-memvar", "USE sb\nSCATTER MEMVAR BLANK" + Blank((f, i) => "m." + f));
        Add("S10/scatter-blank-array", "USE sb\nSCATTER TO arr BLANK" + Blank((f, i) => $"arr[{i}]"));
        Add("S10/scatter-blank-name", "USE sb\nSCATTER NAME oo BLANK" + Blank((f, i) => "oo." + f));
        Add("S10/scatter-memvar", "USE sb\nSCATTER MEMVAR" + Blank((f, i) => "m." + f));
        return probes.ToArray();
    }

    // Synthetic legacy bytes, deliberately not written through the numeric expression engine.
    // Both engines read exactly the same 18-digit integer and four-place fraction.
    internal static void Prepare(string directory)
    {
        File.Delete(Path.Combine(directory, "seed.dbf"));
        using (var session = new VfpSession())
        {
            session.OpenDirectory(directory);
            using var vfp = new VfpInterpreter(session);
            vfp.Execute("CREATE TABLE seed FREE (i20 N(20,0), n20 N(20,4))\nINSERT INTO seed VALUES (0,0)\nCLOSE TABLES ALL");
        }
        string path = Path.Combine(directory, "seed.dbf");
        byte[] bytes = File.ReadAllBytes(path);
        int header = BitConverter.ToUInt16(bytes, 8);
        Encoding.ASCII.GetBytes("  123456789012345678123456789012345.6789").CopyTo(bytes, header + 1);
        File.WriteAllBytes(path, bytes);

        // seed2 plus one pristine copy per S8 probe that writes its table back.
        string[] large = { "seed2", "gm", "ga", "rp" };
        foreach (string name in large) File.Delete(Path.Combine(directory, name + ".dbf"));
        using (var session = new VfpSession())
        {
            session.OpenDirectory(directory);
            using var vfp = new VfpInterpreter(session);
            vfp.Execute("CREATE TABLE seed2 FREE (a N(18,4), b N(19,5), fb F(20,4))\nINSERT INTO seed2 VALUES (0,0,0)\nCLOSE TABLES ALL");
        }
        path = Path.Combine(directory, "seed2.dbf");
        bytes = File.ReadAllBytes(path);
        header = BitConverter.ToUInt16(bytes, 8);
        Encoding.ASCII.GetBytes("1234567890123.56781234567890123.56789  1234567890123.5679").CopyTo(bytes, header + 1);
        foreach (string name in large) File.WriteAllBytes(Path.Combine(directory, name + ".dbf"), bytes);

        // seed3: small N/F text and exact B/Y binary payloads, plus one copy per S9 GATHER probe.
        string[] small = { "seed3", "g3m", "g3a", "g3o" };
        foreach (string name in small) File.Delete(Path.Combine(directory, name + ".dbf"));
        using (var session = new VfpSession())
        {
            session.OpenDirectory(directory);
            using var vfp = new VfpInterpreter(session);
            vfp.Execute("CREATE TABLE seed3 FREE (n1 N(10,2), n2 N(5,0), f1 F(10,3), f2 F(20,10), b1 B(2), b2 B(4), y Y)\n"
                + "INSERT INTO seed3 VALUES (0,0,0,0,0,0,0)\nINSERT INTO seed3 VALUES (0,0,0,0,0,0,0)\nINSERT INTO seed3 VALUES (0,0,0,0,0,0,0)\nCLOSE TABLES ALL");
        }
        path = Path.Combine(directory, "seed3.dbf");
        bytes = File.ReadAllBytes(path);
        header = BitConverter.ToUInt16(bytes, 8);
        int length = BitConverter.ToUInt16(bytes, 10);
        var rows = new (string Text, double B1, double B2, long Y)[]
        {
            ("      1.50   42     1.125        0.3333333333", 1.5, 123456.1234, 12345),
            ("     -0.25   42     1.125        0.3333333333", 1.5e15, 123456.1234, -922337203685475800),
            ("      0.00    0     0.000        0.0000000000", 0, 0, 0),
        };
        for (int row = 0; row < rows.Length; row++)
        {
            int offset = header + row * length + 1;
            Encoding.ASCII.GetBytes(rows[row].Text).CopyTo(bytes, offset);
            offset += rows[row].Text.Length;
            BitConverter.GetBytes(rows[row].B1).CopyTo(bytes, offset);
            BitConverter.GetBytes(rows[row].B2).CopyTo(bytes, offset + 8);
            BitConverter.GetBytes(rows[row].Y).CopyTo(bytes, offset + 16);
        }
        foreach (string name in small) File.WriteAllBytes(Path.Combine(directory, name + ".dbf"), bytes);

        // S10: fd/fs hold raw k N(1,0) + fb F(20,4) text, yc raw k N(1,0) + Y payloads, sb plain values.
        foreach (string name in new[] { "fd", "fs", "yc", "sb" }) File.Delete(Path.Combine(directory, name + ".dbf"));
        using (var session = new VfpSession())
        {
            session.OpenDirectory(directory);
            using var vfp = new VfpInterpreter(session);
            vfp.Execute("CREATE TABLE fd FREE (k N(1,0), fb F(20,4))\nCREATE TABLE fs FREE (k N(1,0), fb F(20,4))\nCREATE TABLE yc FREE (k N(1,0), y Y)\n"
                + "CREATE TABLE sb FREE (y Y, b B(2), n N(10,2), f F(10,3), i I)\nINSERT INTO sb VALUES (12.5, 1.25, 3.5, 2.125, 7)\nCLOSE TABLES ALL");
        }
        static void Rows(string path, IReadOnlyList<byte[]> rows)
        {
            byte[] bytes = File.ReadAllBytes(path);
            int header = BitConverter.ToUInt16(bytes, 8), length = BitConverter.ToUInt16(bytes, 10);
            var table = new byte[header + rows.Count * length + 1];
            bytes.AsSpan(0, header).CopyTo(table);
            BitConverter.GetBytes(rows.Count).CopyTo(table, 4);
            for (int row = 0; row < rows.Count; row++)
            {
                table[header + row * length] = (byte)' ';
                rows[row].CopyTo(table, header + row * length + 1);
            }
            table[^1] = 0x1A;
            File.WriteAllBytes(path, table);
        }
        byte[] Text(string text) => Encoding.ASCII.GetBytes(text);
        Rows(Path.Combine(directory, "fd.dbf"), new[] { Text("1  1234567890123.5678"), Text("2  1234567890123.5679"), Text("3  1234567890123.5671"), Text("4  1234567890123.5674") });
        Rows(Path.Combine(directory, "fs.dbf"), new[] { Text("1   123456789012.3456"), Text("2    99999999999.9999"), Text("3            123.4567") });
        Rows(Path.Combine(directory, "yc.dbf"), new long[] { 922337203685475807, -922337203685475807, 666666666666666666, 922337203685475800, 922337203685475840 }
            .Select((payload, row) => Text((row + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)).Concat(BitConverter.GetBytes(payload)).ToArray()).ToArray());
    }

    internal static string Raw(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int header = BitConverter.ToUInt16(bytes, 8), length = BitConverter.ToUInt16(bytes, 10);
        var result = new StringBuilder("|raw:");
        for (int row = 0; row < BitConverter.ToInt32(bytes, 4); row++)
        {
            int offset = header + row * length + 1;
            for (int field = 32; bytes[field] != 13; field += 32)
            {
                int width = bytes[field + 16];
                if (bytes[field + 11] == '0') { offset += width; continue; }
                result.Append(bytes[field + 11] == 'Y' ? BitConverter.ToInt64(bytes, offset).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : bytes[field + 11] == 'I' ? BitConverter.ToInt32(bytes, offset).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : bytes[field + 11] == 'B' ? BitConverter.ToDouble(bytes, offset).ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    : Encoding.ASCII.GetString(bytes, offset, width).Trim()).Append(';');
                offset += width;
            }
        }
        return result.ToString();
    }

    internal static string Observe(Probe probe)
    {
        using var dir = new MicroVfpTestSupport.TempDir("twidthround10");
        Prepare(dir.Path);
        using var session = new VfpSession();
        session.OpenDirectory(dir.Path);
        using var vfp = new VfpInterpreter(session);
        vfp.Execute(Functions);
        string actual = TransformWidthRound3Tests.Observe(vfp, probe.Body);
        vfp.Execute("CLOSE TABLES ALL");
        return actual + (probe.RawTable is null ? "" : Raw(Path.Combine(dir.Path, probe.RawTable + ".dbf")));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void AdjudicationRound10_MatchesExpectedVfp(int decimals)
    {
        var failures = new List<string>();
        foreach (var probe in Probes(decimals))
        {
            string actual = Observe(probe);
            if (!ExpectedVfp.TryGetValue(probe.Key, out string? expected) || actual != expected)
                failures.Add(probe.Key + ": VFP9=" + expected + "; actual=" + actual);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
