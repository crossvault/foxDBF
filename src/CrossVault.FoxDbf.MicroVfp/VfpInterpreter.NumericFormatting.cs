using System.Globalization;
using System.IO;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.MicroVfp;

public sealed partial class VfpInterpreter
{
    /// <summary>Receives ? and ?? text using fixed character cells (VFP9 Courier New). The default discards screen output.</summary>
    public TextWriter Output { get; set; } = TextWriter.Null;
    private StreamWriter? _alternateWriter;
    private bool _alternateOn;

    private string FixedNumber(VfpValue value) => Math.Round(value.AsDisplayNumber,
        _ctx.NumericDecimals, MidpointRounding.AwayFromZero).ToString("F" + _ctx.NumericDecimals, CultureInfo.InvariantCulture);

    private void SetAlternate(string argument)
    {
        if (!argument.StartsWith("TO", StringComparison.OrdinalIgnoreCase))
        {
            _alternateOn = OnOff(argument);
            return;
        }
        CloseAlternate();
        string name = argument[2..].Trim();
        bool additive = name.EndsWith(" ADDITIVE", StringComparison.OrdinalIgnoreCase);
        if (additive) name = name[..^9].TrimEnd();
        name = name.Trim('\'', '"', '[', ']');
        if (name.Length == 0)
        {
            _alternateOn = false;
            return;
        }
        string path = ResolveDataPath(Path.HasExtension(name) ? name : name + ".txt");
        var stream = new FileStream(path, additive ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        if (additive && stream.Length > 0)
        {
            stream.Position = stream.Length - 1;
            if (stream.ReadByte() == 0x1a) stream.SetLength(stream.Length - 1);
        }
        stream.Position = stream.Length;
        _alternateWriter = new StreamWriter(stream, LlEncoding);
    }

    private void CloseAlternate()
    {
        if (_alternateWriter is null) return;
        _alternateWriter.Write('\u001a');
        _alternateWriter.Dispose();
        _alternateWriter = null;
    }

    private void ExecPrint(PrintStmt statement)
    {
        if (statement.NewLine) WritePrint("\r\n");
        for (int i = 0; i < statement.Expressions.Count; i++)
        {
            if (i > 0) WritePrint(" ");
            var expression = statement.Expressions[i];
            var value = Eval(expression);
            WritePrint(IsNumeric(value) ? PrintNumber(value, expression) : TransformNoFormat(value));
        }
    }

    private void WritePrint(string text)
    {
        Output.Write(text);
        if (_alternateOn) _alternateWriter?.Write(text);
    }

    private string PrintNumber(VfpValue value, PrgExpr expression)
    {
        if (!double.IsFinite(value.AsDouble)) return TransformNoFormat(value);
        int originalScale = Math.Max(0, value.DisplayDecimals);
        int scale = _ctx.FixedNumeric ? _ctx.NumericDecimals : originalScale;
        int width;
        if (value.Type == VfpType.Currency)
        {
            scale = _ctx.FixedNumeric ? Math.Min(4, _ctx.NumericDecimals) : 4;
            width = 21;
        }
        else if (double.TryParse(expression.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            width = expression.Text.Trim().Length;
            if (_ctx.FixedNumeric)
            {
                int digits = decimal.Truncate(value.AsDisplayNumber).ToString(CultureInfo.InvariantCulture).Length;
                width = Math.Max(width, digits + scale + (scale > 0 ? 1 : 0));
            }
        }
        else if (PrintFieldWidth(expression) is int fieldWidth)
        {
            width = fieldWidth;
            if (_ctx.FixedNumeric && scale > originalScale)
                width += scale - originalScale + (originalScale == 0 && scale > 0 ? 1 : 0);
        }
        else
        {
            // Numeric variables reserve ten integer positions; FIXED never shortens their original width.
            int widthScale = _ctx.FixedNumeric ? Math.Max(originalScale, scale) : scale;
            width = 10 + (widthScale > 0 ? widthScale + 1 : 0) + (value.AsDouble < 0 ? 1 : 0);
        }
        string text = value.RoundForDisplay(scale)
            .ToString("F" + scale, CultureInfo.InvariantCulture);
        return text.PadLeft(width);
    }
    private int? PrintFieldWidth(PrgExpr expression)
    {
        if (expression.Parsed is not { } parsed) return null;
        var schema = new PrintSchema(this);
        if (!parsed.ReferencedFields().Any(name => schema.TryGetColumn(name, out _, out _, out _))) return null;
        var info = parsed.InferSqlType(schema);
        return info.Type == VfpType.Integer ? 11 : info.SqlStorageType == 'B' ? 21 : info.Length;
    }

    private sealed class PrintSchema(VfpInterpreter owner) : ISchema
    {
        public bool TryGetColumn(string name, out char type, out int length, out int decimals)
        {
            type = '\0'; length = 0; decimals = 0;
            int dot = name.IndexOf('.');
            string? alias = dot < 0 ? null : name[..dot];
            if (string.Equals(alias, "m", StringComparison.OrdinalIgnoreCase)) return false;
            var area = alias is null ? owner.Session.AreaAt(owner.Session.CurrentArea)
                : owner.Session.OpenAreas.FirstOrDefault(a => a.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase));
            string field = dot < 0 ? name : name[(dot + 1)..];
            var column = area?.Table.Columns.FirstOrDefault(c => c.Name.Equals(field, StringComparison.OrdinalIgnoreCase));
            if (column is null) return false;
            type = column.Type; length = column.Length; decimals = column.Decimal;
            return true;
        }
    }

}
