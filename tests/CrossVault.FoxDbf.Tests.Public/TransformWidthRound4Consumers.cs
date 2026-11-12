using System.Data;
using System.Globalization;
using System.Text;
using CrossVault.FoxDbf.Data;
using CrossVault.FoxDbf.Expressions;
using CrossVault.FoxDbf.MicroVfp;
using CrossVault.FoxDbf.Sql;

namespace CrossVault.FoxDbf.Tests;

// The same finite queries exercise materialized result rows and the actual ADO reader.
// SQL's host context supplies memvars, as it does for a SELECT executed by microVFP.
internal static class TransformWidthRound4Consumers
{
    internal static string Observe(VfpInterpreter vfp, FoxDbfConnection connection, string body, bool ado, Action<string>? rawObservation = null)
    {
        int start = body.IndexOf("\nSELECT ", body.IndexOf("x=1.2345", StringComparison.Ordinal), StringComparison.Ordinal) + 1;
        int end = body.IndexOf(" INTO CURSOR rr", start, StringComparison.Ordinal);
        vfp.Execute(body[..start]);
        var query = (SelectStatement)SqlParser.Parse(body[start..end]);
        var result = new SelectExecutor(vfp.Session, new MemoryRow(vfp)) { VfpBlankFields = true }.Run(query);
        var text = new StringBuilder();
        var raw = new StringBuilder();
        foreach (var c in result.Columns)
        {
            text.Append($"{c.VfpType}|{c.Length}|{c.Decimals};");
            raw.Append($"{c.VfpType}|{c.Length}|{c.Decimals}|{c.ClrType.FullName};");
        }
        void Cell(object? value, SqlColumn c)
        {
            raw.Append(value?.GetType().FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture) + ";");
            vfp.Memory.Set("consumer_value", VfpValue.FromField(value, c.VfpType, c.Decimals));
            text.Append(vfp.EvalExpression("STR(consumer_value,30,10)+'|'+TRANSFORM(consumer_value)+';'").AsString);
        }
        if (ado)
        {
            using var reader = new FoxDbfDataReader(result, connection, CommandBehavior.Default);
            var schema = reader.GetSchemaTable()!;
            for (int i = 0; i < result.Columns.Count; i++)
            {
                var c = result.Columns[i];
                Type expectedType = c.VfpType switch { 'B' or 'F' => typeof(double), 'I' => typeof(int), _ => typeof(decimal) };
                if (reader.GetFieldType(i) != expectedType || (Type)schema.Rows[i]["DataType"] != expectedType
                    || (int)schema.Rows[i]["NumericScale"] != c.Decimals
                    || (int)schema.Rows[i]["ColumnSize"] != c.Length
                    || (int)schema.Rows[i]["NumericPrecision"] != c.Length - c.Decimals)
                    throw new InvalidOperationException("ADO schema mismatch at " + i);
            }
            while (reader.Read())
                for (int i = 0; i < result.Columns.Count; i++)
                {
                    object value = reader.GetValue(i);
                    if (value is not DBNull && value.GetType() != reader.GetFieldType(i))
                        throw new InvalidOperationException("ADO value type mismatch at " + i);
                    Cell(value is DBNull ? null : value, result.Columns[i]);
                }
        }
        else
            foreach (var row in result.Rows)
                for (int i = 0; i < row.Length; i++) Cell(row[i], result.Columns[i]);
        rawObservation?.Invoke(raw.ToString());
        return text.ToString();
    }

    private sealed class MemoryRow(VfpInterpreter vfp) : IRowContext, IVfpValueRow, IVfpFunctionHost
    {
        public int RecNo => 1;
        public int RecCount => 1;
        public bool Deleted => false;
        public VfpValue GetValue(string name) => vfp.Memory.Get(name.StartsWith("m.", StringComparison.OrdinalIgnoreCase) ? name[2..] : name);
        public object? GetField(string name) => GetValue(name);
        public bool TryInvoke(string name, VfpValue[] args, EvaluationContext context, out VfpValue value)
        { value = default; return false; }
    }
}
