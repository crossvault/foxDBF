using System.Buffers.Binary;

namespace CrossVault.FoxDbf.Write;

/// <summary>Page-local maintenance of VFP's uncompressed IDX tree. Branch separators are the
/// last key of each child (without a record number); equal keys must therefore descend far
/// enough to compare the leaf's record number. Empty nodes are unlinked, not compacted.</summary>
internal sealed class IdxTreeEditor
{
    private const int PageSize = 512;
    private const uint NoPointer = uint.MaxValue;
    private readonly FileStream _stream;
    private readonly int _keyLength;
    private readonly int _capacity;
    private readonly uint _root;
    private uint _end;
    private readonly Dictionary<uint, Node> _nodes = new();
    private readonly HashSet<uint> _dirty = new();

    private readonly record struct Entry(byte[] Key, uint Pointer);
    private sealed class Node(uint offset, ushort attributes, uint left, uint right, List<Entry> entries)
    {
        internal uint Offset = offset;
        internal ushort Attributes = attributes;
        internal uint Left = left;
        internal uint Right = right;
        internal List<Entry> Entries = entries;
        internal bool IsLeaf => (Attributes & 2) != 0;
    }

    internal IdxTreeEditor(FileStream stream, int keyLength)
    {
        _stream = stream;
        _keyLength = keyLength;
        _capacity = (PageSize - 12) / (keyLength + 4);
        if (keyLength <= 0 || _capacity < 2) throw new InvalidDataException("Invalid IDX key length.");
        Span<byte> header = stackalloc byte[16];
        stream.Position = 0;
        stream.ReadExactly(header);
        _root = BinaryPrimitives.ReadUInt32LittleEndian(header);
        _end = checked((uint)stream.Length);
    }

    internal bool ContainsKey(byte[] key)
    {
        var node = Read(_root);
        for (int depth = 0; !node.IsLeaf; depth++)
        {
            if (depth >= 64 || node.Entries.Count == 0) throw new InvalidDataException("Invalid IDX tree.");
            int child = node.Entries.FindIndex(e => Compare(e.Key, key) >= 0);
            if (child < 0) return false;
            node = Read(node.Entries[child].Pointer);
        }
        return node.Entries.Any(e => Compare(e.Key, key) == 0);
    }

    internal void Insert(byte[] key, uint recordNumber)
        => Insert(Read(_root), new Entry(key, recordNumber), 0);

    private Node? Insert(Node node, Entry entry, int depth)
    {
        if (depth >= 64) throw new InvalidDataException("Cyclic IDX tree.");
        if (node.IsLeaf)
        {
            int at = node.Entries.FindIndex(e => Compare(e, entry) >= 0);
            if (at >= 0 && Compare(node.Entries[at], entry) == 0) return null;
            node.Entries.Insert(at < 0 ? node.Entries.Count : at, entry);
        }
        else
        {
            int at = node.Entries.Count - 1;
            for (int i = 0; i < node.Entries.Count - 1; i++)
            {
                int comparison = Compare(node.Entries[i].Key, entry.Key);
                if (comparison > 0 || comparison == 0 && Compare(Maximum(Read(node.Entries[i].Pointer)), entry) >= 0)
                {
                    at = i;
                    break;
                }
            }
            if (at < 0) throw new InvalidDataException("Empty IDX branch.");
            var child = Read(node.Entries[at].Pointer);
            var added = Insert(child, entry, depth + 1);
            node.Entries[at] = Separator(child);
            if (added is not null) node.Entries.Insert(at + 1, Separator(added));
        }
        _dirty.Add(node.Offset);
        if (node.Entries.Count <= _capacity) return null;

        // VFP grows the right edge asymmetrically; the root remains at its original offset.
        // Interior splits share the entries. Uncompressed pages need no compression budget.
        int cut = node.Right == NoPointer
            ? _capacity - (_keyLength + 4 < 50 && (node.Offset / PageSize & 1) != 0 ? 1 : 0)
            : (node.Entries.Count + 1) / 2;
        var right = Allocate((ushort)(node.Attributes & ~1), node.Offset, node.Right, node.Entries.GetRange(cut, node.Entries.Count - cut));
        node.Entries.RemoveRange(cut, node.Entries.Count - cut);
        if (node.Offset == _root)
        {
            var left = Allocate((ushort)(node.Attributes & ~1), NoPointer, right.Offset, node.Entries);
            right.Left = left.Offset;
            node.Attributes = 1;
            node.Left = node.Right = NoPointer;
            node.Entries = [Separator(left), Separator(right)];
            return null;
        }
        if (node.Right != NoPointer)
        {
            var sibling = Read(node.Right);
            sibling.Left = right.Offset;
            _dirty.Add(sibling.Offset);
        }
        node.Right = right.Offset;
        return right;
    }

    internal void Delete(byte[] key, uint recordNumber)
        => Delete(Read(_root), new Entry(key, recordNumber), 0);

    private bool Delete(Node node, Entry entry, int depth)
    {
        if (depth >= 64) throw new InvalidDataException("Cyclic IDX tree.");
        if (node.IsLeaf)
        {
            int at = node.Entries.FindIndex(e => Compare(e, entry) == 0);
            if (at < 0) return false;
            node.Entries.RemoveAt(at);
        }
        else
        {
            bool found = false;
            for (int i = 0; i < node.Entries.Count; i++)
            {
                int comparison = Compare(node.Entries[i].Key, entry.Key);
                if (comparison < 0) continue;
                var child = Read(node.Entries[i].Pointer);
                if (comparison == 0 && Compare(Maximum(child), entry) < 0) continue;
                if (Delete(child, entry, depth + 1))
                {
                    if (child.Entries.Count == 0) node.Entries.RemoveAt(i);
                    else node.Entries[i] = Separator(child);
                    found = true;
                    break;
                }
                if (comparison > 0) break;
            }
            if (!found) return false;
        }
        _dirty.Add(node.Offset);
        if (node.Entries.Count == 0)
        {
            if (node.Offset == _root)
            {
                node.Attributes = 3;
                node.Left = node.Right = NoPointer;
            }
            else
            {
                if (node.Left != NoPointer)
                {
                    var left = Read(node.Left);
                    left.Right = node.Right;
                    _dirty.Add(left.Offset);
                }
                if (node.Right != NoPointer)
                {
                    var right = Read(node.Right);
                    right.Left = node.Left;
                    _dirty.Add(right.Offset);
                }
            }
        }
        return true;
    }

    private Entry Maximum(Node node)
    {
        for (int depth = 0; !node.IsLeaf; depth++)
        {
            if (depth >= 64 || node.Entries.Count == 0) throw new InvalidDataException("Invalid IDX branch.");
            node = Read(node.Entries[^1].Pointer);
        }
        if (node.Entries.Count == 0) throw new InvalidDataException("Empty IDX child.");
        return node.Entries[^1];
    }

    private static Entry Separator(Node node) => new(node.Entries[^1].Key, node.Offset);
    private static int Compare(byte[] left, byte[] right) => left.AsSpan().SequenceCompareTo(right);
    private static int Compare(Entry left, Entry right)
    {
        int comparison = Compare(left.Key, right.Key);
        return comparison != 0 ? comparison : left.Pointer.CompareTo(right.Pointer);
    }

    private Node Allocate(ushort attributes, uint left, uint right, List<Entry> entries)
    {
        var node = new Node(_end, attributes, left, right, entries);
        _end = checked(_end + PageSize);
        _nodes.Add(node.Offset, node);
        _dirty.Add(node.Offset);
        return node;
    }

    private Node Read(uint offset)
    {
        if (_nodes.TryGetValue(offset, out var node)) return node;
        if (offset < PageSize || offset % PageSize != 0 || offset + (long)PageSize > _stream.Length)
            throw new InvalidDataException("Invalid IDX page pointer.");
        Span<byte> page = stackalloc byte[PageSize];
        _stream.Position = offset;
        _stream.ReadExactly(page);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(page[2..]);
        if (count > _capacity) throw new InvalidDataException("Invalid IDX entry count.");
        var entries = new List<Entry>(count);
        for (int i = 0; i < count; i++)
        {
            int start = 12 + i * (_keyLength + 4);
            entries.Add(new Entry(page.Slice(start, _keyLength).ToArray(), BinaryPrimitives.ReadUInt32BigEndian(page[(start + _keyLength)..])));
        }
        node = new Node(offset, BinaryPrimitives.ReadUInt16LittleEndian(page),
            BinaryPrimitives.ReadUInt32LittleEndian(page[4..]), BinaryPrimitives.ReadUInt32LittleEndian(page[8..]), entries);
        _nodes.Add(offset, node);
        return node;
    }

    internal void Flush()
    {
        Span<byte> page = stackalloc byte[PageSize];
        foreach (uint offset in _dirty)
        {
            var node = _nodes[offset];
            page.Clear();
            BinaryPrimitives.WriteUInt16LittleEndian(page, node.Attributes);
            BinaryPrimitives.WriteUInt16LittleEndian(page[2..], (ushort)node.Entries.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(page[4..], node.Left);
            BinaryPrimitives.WriteUInt32LittleEndian(page[8..], node.Right);
            for (int i = 0; i < node.Entries.Count; i++)
            {
                int start = 12 + i * (_keyLength + 4);
                node.Entries[i].Key.CopyTo(page[start..]);
                BinaryPrimitives.WriteUInt32BigEndian(page[(start + _keyLength)..], node.Entries[i].Pointer);
            }
            _stream.Position = offset;
            _stream.Write(page);
        }
        if (_end > _stream.Length) _stream.SetLength(_end);
        if (_dirty.Count > 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(page, _end);
            _stream.Position = 8;
            _stream.Write(page[..4]);
        }
        _stream.Flush();
        _dirty.Clear();
    }
}
