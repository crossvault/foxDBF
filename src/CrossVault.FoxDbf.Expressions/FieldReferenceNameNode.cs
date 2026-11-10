using System.Collections.Generic;
using System.Linq.Expressions;

namespace CrossVault.FoxDbf.Expressions;

// A bound field-name argument still depends on that field, even though the host receives its name.
// Keeping the dependency lets index/schema validation see the same fields as the source expression.
internal sealed class FieldReferenceNameNode : AstNode
{
    private readonly string _name;
    private readonly VfpValue _value;

    internal FieldReferenceNameNode(string name)
    {
        _name = name;
        _value = VfpValue.Character(name);
    }

    public override VfpValue Eval(IRowContext row, EvaluationContext ctx) => _value;
    public override Expression Build(BuildContext b) => Expression.Constant(_value);
    public override VfpTypeInfo Infer(ISchema schema) => new(VfpType.Character, _name.Length);
    public override void CollectFields(ICollection<string> into, System.Func<string, int, bool>? skipFunction = null)
        => into.Add(_name);
}
