using System;
using System.Linq;
using CrossVault.FoxDbf.Expressions;

namespace CrossVault.FoxDbf.MicroVfp;

public sealed partial class VfpInterpreter
{
    private int _xbaseUpdateUnbufferedArea;

    private void ExecXbaseUpdate(XbaseUpdateStmt statement)
    {
        int target = Session.CurrentArea;
        FlushPendingAppends();
        var wa = Session.AreaAt(target)
            ?? throw new MicroVfpRuntimeException("No table is open in the current work area.", 52);
        var source = Session.FindAreaByAlias(NameOf(statement.Source))
            ?? throw new MicroVfpRuntimeException("Alias is not found.", 13);
        if (statement.Random)
        {
            using var order = OpenMasterOrder(target);
            if (order is null) throw new MicroVfpRuntimeException("Table has no index order set.", 26);
        }
        if (Session.ReadOnly || wa.NoUpdate)
            throw new MicroVfpRuntimeException("Cannot update the cursor.", 111);

        bool previousRequireNames = _requireDmlNames;
        _requireDmlNames = true;
        var meta = Meta(target);
        int buffering = meta.Buffering;
        int previousInternalArea = _xbaseUpdateUnbufferedArea;
        bool started = false;
        int tally = 0;
        try
        {
            // Name errors precede navigation and preserve the previous _TALLY.
            string keyText = PrgScan.FoldArrowTarget(statement.Key.Text).Trim();
            if (keyText.Any(c => !char.IsLetterOrDigit(c) && c is not ('_' or '.')))
                throw new MicroVfpRuntimeException("Command contains unrecognized phrase/keyword.", 36);
            string keyField = TargetField(keyText);
            var keyExpression = PrgExpr.Parse(keyField);
            int keyColumn = ColumnIndex(wa.Table, keyField);
            int sourceColumn = ColumnIndex(source.Table, keyField);
            if (keyColumn < 0 || sourceColumn < 0)
                throw new MicroVfpRuntimeException($"Variable '{keyField}' is not found.", 12);
            if (wa.Table.Columns[keyColumn].Type != source.Table.Columns[sourceColumn].Type)
                throw new MicroVfpRuntimeException("Data type mismatch.", 9);
            if (wa.Table.Columns[keyColumn].Type is 'C' or 'V' or 'Q'
                && wa.Table.Columns[keyColumn].Length != source.Table.Columns[sourceColumn].Length)
                throw new MicroVfpRuntimeException("Data type mismatch.", 9);
            if (wa.Table.Columns[keyColumn].Type is not ('C' or 'V' or 'Q' or 'N' or 'F' or 'D' or 'T'))
                throw new MicroVfpRuntimeException("Invalid key field type.", 1145);
            foreach (var clause in statement.Replacement.Clauses)
                if (ColumnIndex(wa.Table, TargetField(NameOf(clause.Field))) < 0)
                    throw new MicroVfpRuntimeException($"Variable '{NameOf(clause.Field)}' is not found.", 12);
            foreach (var clause in statement.Replacement.Clauses)
                if (clause.Value.Parsed is { } expression)
                    foreach (string name in expression.ReferencedFields()) ResolveName(name);

            // VFP holds all updates to the same target record until it leaves that record, even
            // without user buffering. The REPLACE buffer supplies sequential expressions and
            // immediate field rules; leaving the record supplies record rules and update triggers.
            if (buffering == 1)
            {
                _xbaseUpdateUnbufferedArea = target;
                meta.Buffering = 3;
                _anyBuffering = true;
            }
            started = true;
            if (!statement.Random) GoTop(target);
            GoTop(source.Area);
            while (!Meta(source.Area).Eof)
            {
                Session.SelectArea(source.Area);
                var key = Eval(keyExpression);
                Session.SelectArea(target);
                bool match;
                if (statement.Random)
                {
                    // A failed RANDOM lookup does not leave the current target record. Nor does
                    // another hit on that record flush its pending changes (including duplicates).
                    var current = Meta(target);
                    var position = (current.RecNo, current.Eof, current.Bof, current.OrderPos, current.Cached, current.CachedRec, current.Found);
                    match = DoSeekCore(key, target, null);
                    int hit = current.RecNo;
                    (current.RecNo, current.Eof, current.Bof, current.OrderPos, current.Cached, current.CachedRec, current.Found) = position;
                    if (match && hit != current.RecNo)
                    {
                        FinishRow();
                        GoRecord(target, hit);
                    }
                }
                else
                {
                    // This is a forward merge, not a search from the top for every source row.
                    // VFP accepts physically ordered tables and does not diagnose unsorted inputs.
                    // VFP's merge predicate propagates NULL: a NULL source is skipped, while
                    // a NULL target stops this forward scan. Non-NULL type errors still propagate.
                    while (!Meta(target).Eof && VfpRuntime.Binary(BinOp.Lt, Eval(keyExpression), key, _ctx).AsLogical)
                    {
                        FinishRow();
                        Skip(target, 1);
                    }
                    match = !Meta(target).Eof && VfpRuntime.Binary(BinOp.ExactEq, Eval(keyExpression), key, _ctx).AsLogical;
                }
                if (match)
                {
                    try { BufferReplace(statement.Replacement, target, Session.AreaAt(target)!, Meta(target), normalize: true); }
                    catch (MicroVfpRuntimeException error) when (buffering == 1 && error.VfpErrorNumber == 9 && !error.FromFieldErrorExpression)
                    {
                        // A later type mismatch retains the preceding assignments on this row.
                        FinishRow(leavingRow: false, pendingTypeError: error);
                        throw;
                    }
                    tally++;
                }
                Skip(source.Area, 1);
            }
            FinishRow(leavingRow: false);
        }
        finally
        {
            Session.SelectArea(target);
            _requireDmlNames = previousRequireNames;
            _xbaseUpdateUnbufferedArea = previousInternalArea;
            if (buffering == 1)
            {
                meta = Meta(target);
                meta.Buffering = buffering;
                meta.Buf = null;
                _anyBuffering = _meta.Values.Any(m => m.Buffering > 1);
            }
            if (started) Memory.Set("_TALLY", VfpValue.Integer(tally));
        }

        string TargetField(string text)
        {
            text = PrgScan.FoldArrowTarget(text);
            int dot = text.IndexOf('.');
            if (dot >= 0)
            {
                var qualified = Session.FindAreaByAlias(text[..dot])
                    ?? throw new MicroVfpRuntimeException($"Alias '{text[..dot]}' is not found.", 13);
                if (qualified.Area != target)
                    throw new MicroVfpRuntimeException($"Variable '{text}' is not found.", 12);
            }
            return dot < 0 ? text : text[(dot + 1)..];
        }

        void FinishRow(bool leavingRow = true, MicroVfpRuntimeException? pendingTypeError = null)
        {
            var current = Meta(target);
            if (current.Buf?.Rows.TryGetValue(current.RecNo, out var edit) == true)
            {
                // A CHECK evaluation error already completed the field assignment in VFP.
                // Do not evaluate that field again while publishing the accepted prefix.
                if (pendingTypeError is not null) edit.ValidatedFields.UnionWith(edit.Fields.Keys);
                try
                {
                    if (buffering <= 3 || leavingRow) ValidateBufferedRow(Session.AreaAt(target)!, current.RecNo, edit);
                }
                catch when (pendingTypeError is not null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(pendingTypeError).Throw();
                    throw;
                }
                if (buffering == 1)
                {
                    // Publish through the normal REPLACE writer so candidate uniqueness and RI
                    // rollback use the same pre-image as an ordinary unbuffered field change.
                    var table = Session.AreaAt(target)!.Table;
                    var clauses = edit.Fields.Select(field => new ReplaceClause(NameRef.OfName(table.Columns[field.Key].Name),
                        PrgExpr.Constant(VfpValue.FromClr(field.Value)), false)).ToArray();
                    current.Buf = null;
                    current.Buffering = 1;
                    try { ExecReplace(new ReplaceStmt(clauses, null, null, null, null), rulesValidated: true); }
                    finally { Meta(target).Buffering = 3; }
                    return;
                }
            }
            MaybeAutoCommitRow(target);
        }
    }
}
