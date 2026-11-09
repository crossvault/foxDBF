namespace CrossVault.FoxDbf.Write;

public sealed partial class DbfWriter
{
    private string IndexFailurePath => StructuralCdxPath() + ".needs-reindex";

    private IndexMutation? BeginIndexMutation(long offset, bool append, byte[]? before = null)
        => _cdxMaintEditor is not null ? new IndexMutation(this, offset, append, before) : null;

    /// <summary>Undo one DBF mutation and its structural index edits while the mutation lock is held.</summary>
    private sealed class IndexMutation : IDisposable
    {
        private readonly DbfWriter _owner;
        private readonly long _offset;
        private readonly long _length;
        private readonly int _count;
        private readonly bool _append;
        private readonly byte[] _header;
        private readonly byte[] _data;
        private readonly CdxTreeEditor? _editor;
        private bool _committed;

        internal IndexMutation(DbfWriter owner, long offset, bool append, byte[]? before)
        {
            _owner = owner;
            _offset = offset;
            _length = append ? owner._stream.Length : 0;   // only an append changes file length
            _count = owner._recordCount;
            _append = append;
            // Memo allocations are append-only and may be shared. Restore the DBF's old pointers,
            // but do not rewind the FPT allocator over blocks another writer could have reserved.
            // Updates restore only the date: another shared writer may have advanced the row count.
            _header = ReadRaw(owner._handle, append ? 0 : 1, append ? owner._headerLength : 3);
            // UpdateCore already read this row under the same record lock for KeepValue/key derivation.
            _data = before ?? ReadRaw(owner._handle, offset, append ? checked((int)Math.Max(0, _length - offset)) : owner._recordLength);
            _editor = owner._cdxMaintEditor!;
            _editor.BeginEdit();
        }

        internal void Commit()
        {
            _editor?.CommitEdit();
            _committed = true;
        }

        public void Dispose()
        {
            if (_committed) return;
            try
            {
                _editor?.RollbackEdit();
                _owner._stream.Position = _offset;
                _owner._stream.Write(_data);
                if (_append) _owner._stream.SetLength(_length);
                _owner._stream.Position = _append ? 0 : 1;
                _owner._stream.Write(_header);
                _owner._stream.Flush();
                _owner._recordCount = _count;
            }
            catch
            {
                // Preserve definitions even if storage refuses the undo. A later Open must not
                // mistake this tree for a healthy one; only a successful rebuild clears the marker.
                File.WriteAllText(_owner.IndexFailurePath, "Index maintenance rollback failed. Reindex required.");
                throw;
            }
            finally
            {
                _owner.ReindexNeeded = true;
                _owner.CloseCdxMaint(flush: false);
            }
        }
    }
}
