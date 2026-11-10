using System.Globalization;
using System.IO;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.MicroVfp;

public sealed partial class VfpInterpreter
{
    /// <summary>Receives ? and ?? output. The default discards screen output.</summary>
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
        string name = argument[2..].Trim().Trim('\'', '"', '[', ']');
        if (name.Length == 0) return;
        string path = ResolveDataPath(Path.HasExtension(name) ? name : name + ".txt");
        _alternateWriter = new StreamWriter(path, false, LlEncoding);
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
            WritePrint(IsNumeric(value) ? PrintNumber(value, expression.Text) : TransformNoFormat(value));
            if (i == statement.Expressions.Count - 1 && value.Type == VfpType.Character) WritePrint(" ");
        }
    }

    private void WritePrint(string text)
    {
        Output.Write(text);
        if (_alternateOn) _alternateWriter?.Write(text);
    }

    private string PrintNumber(VfpValue value, string expression)
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
        else if (double.TryParse(expression.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            width = expression.Trim().Length;
            if (!_ctx.FixedNumeric && width > 5 && value.AsDouble >= 0) width++;
            if (_ctx.FixedNumeric)
            {
                int digits = decimal.Truncate(value.AsDisplayNumber).ToString(CultureInfo.InvariantCulture).Length;
                width = Math.Max(width, digits + scale + 2);
            }
        }
        else
        {
            // Numeric variables reserve ten integer positions; FIXED never shortens their original width.
            int widthScale = _ctx.FixedNumeric ? Math.Max(originalScale, scale) : scale;
            width = 10 + (widthScale > 0 ? widthScale + 1 : 0) + (value.AsDouble < 0 ? 1 : 0);
        }
        string text = Math.Round(value.AsDisplayNumber, scale, MidpointRounding.AwayFromZero)
            .ToString("F" + scale, CultureInfo.InvariantCulture);
        return text.PadLeft(width);
    }
}
