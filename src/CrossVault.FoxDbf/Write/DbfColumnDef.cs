using System.Text;

namespace CrossVault.FoxDbf.Write;

/// <summary>
/// The AutoIncrement seed for a CREATE-time column definition (plan §D4): the next value
/// to assign (descriptor bytes 19–22, u32 LE) and the per-append step (descriptor byte 23).
/// Only meaningful on an Integer (<c>I</c>) column; presence forces the table version to
/// <c>0x31</c> when no Varchar/Varbinary column raises it to <c>0x32</c>.
/// </summary>
public sealed record DbfAutoIncrement
{
    /// <summary>The next value to assign on the first append (descriptor <c>Next</c>, bytes 19–22).</summary>
    public uint NextValue { get; init; }

    /// <summary>The per-append increment (descriptor <c>Step</c>, byte 23). Defaults to 1.</summary>
    public byte Step { get; init; } = 1;

    /// <summary>Create an AutoIncrement seed with the given <paramref name="nextValue"/> and <paramref name="step"/>.</summary>
    public DbfAutoIncrement(uint nextValue, byte step = 1)
    {
        NextValue = nextValue;
        Step = step;
    }
}

/// <summary>
/// A single column definition for <see cref="DbfWriter.Create(string, System.Collections.Generic.IEnumerable{DbfColumnDef}, DbfCreateOptions)"/>
/// (plan §D4). Describes the on-disk field BEFORE the table exists: its <see cref="Name"/>,
/// type char (<see cref="Type"/>), on-disk <see cref="Length"/> in bytes, <see cref="Decimal"/>
/// places, and the §A5b flags — <see cref="Nullable"/> (descriptor flag <c>0x02</c>, consumes a
/// NULL bit in <c>_NullFlags</c>) and <see cref="Binary"/> (flag <c>0x04</c>, NOCPTRANS).
/// An optional <see cref="AutoIncrement"/> seed adds flag <c>0x08</c> and the Next/Step descriptor bytes.
/// </summary>
public sealed record DbfColumnDef
{
    /// <summary>The field name (≤ 10 ASCII chars; written NUL-padded into the 11-byte descriptor slot).</summary>
    public string Name { get; init; }

    /// <summary>The single-character field type code (e.g. <c>C</c>, <c>N</c>, <c>I</c>, <c>V</c>, <c>M</c>).</summary>
    public char Type { get; init; }

    /// <summary>The on-disk field length in bytes (1–255; the descriptor length byte @16).</summary>
    public int Length { get; init; }

    /// <summary>The decimal-places count for <c>N</c>/<c>F</c> fields (descriptor byte @17).</summary>
    public int Decimal { get; init; }

    /// <summary>True for a NULL-able field (descriptor flag <c>0x02</c>; consumes a <c>_NullFlags</c> bit).</summary>
    public bool Nullable { get; init; }

    /// <summary>True for a binary / NOCPTRANS field (descriptor flag <c>0x04</c>).</summary>
    public bool Binary { get; init; }

    /// <summary>Optional AutoIncrement seed (Integer columns only); adds flag <c>0x08</c> + Next/Step bytes.</summary>
    public DbfAutoIncrement? AutoIncrement { get; init; }

    /// <summary>Create a column definition.</summary>
    public DbfColumnDef(string name, char type, int length = 0, int decimalCount = 0,
        bool nullable = false, bool binary = false, DbfAutoIncrement? autoIncrement = null)
    {
        Name = name;
        Type = type;
        // Fixed-width types have a canonical descriptor length — fill it in when the caller passes 0
        // so `new DbfColumnDef("QTY", 'I')` just works (VFP: CREATE TABLE t (id I) needs no width).
        // Variable-width types (C/N/F/V/Q) keep the given length (0 → a schema error at create time).
        Length = length > 0 ? length : DefaultFixedWidth(type);
        // Currency always carries 4 implied decimals; VFP9 records them in the descriptor (decimal byte 4).
        Decimal = decimalCount == 0 && type is 'Y' or 'y' ? 4 : decimalCount;
        Nullable = nullable;
        Binary = binary;
        AutoIncrement = autoIncrement;
    }

    /// <summary>The canonical descriptor length for a fixed-width field type, or 0 for variable-width
    /// types (Character/Numeric/Float/Varchar/Varbinary), which must state their own length.</summary>
    private static int DefaultFixedWidth(char type) => type switch
    {
        'I' or 'i' => 4,   // Integer (4-byte LE)
        '+' => 4,          // AutoIncrement Integer
        'Y' or 'y' => 8,   // Currency (int64 / 10000)
        'B' or 'b' => 8,   // Double (IEEE-754)
        'D' or 'd' => 8,   // Date (YYYYMMDD)
        'T' or 't' => 8,   // DateTime (Julian day + ms, two int32)
        'L' or 'l' => 1,   // Logical
        'M' or 'm' => 4,   // Memo block pointer
        'G' or 'g' => 4,   // General (OLE) block pointer
        'P' or 'p' => 4,   // Picture block pointer
        'W' or 'w' => 4,   // Blob block pointer (FPT)
        _ => 0,            // C / N / F / V / Q — caller must specify a length
    };
}

/// <summary>
/// CREATE-time options for <see cref="DbfWriter.Create(string, System.Collections.Generic.IEnumerable{DbfColumnDef}, DbfCreateOptions)"/>
/// (plan §D4): the code-page byte written at header offset 29 (default <c>0x03</c> = CP1252),
/// the FoxPro memo (<c>.fpt</c>) block size (default 64), an optional relative <c>.dbc</c>
/// backlink path (default <see langword="null"/> → an all-zero backlink = free table), and the
/// concurrency <see cref="Write.LockMode"/> + character <see cref="System.Text.Encoding"/> used
/// when the freshly created table is reopened as a ready <see cref="DbfWriter"/>.
/// </summary>
public sealed record DbfCreateOptions
{
    /// <summary>The code-page byte written at header offset 29. Default <c>0x03</c> (Windows ANSI / CP1252).</summary>
    public byte CodePage { get; init; } = 0x03;

    /// <summary>The FoxPro <c>.fpt</c> memo block size (header u16 BE @6). Default 64.</summary>
    public int MemoBlockSize { get; init; } = 64;

    /// <summary>
    /// Optional relative <c>.dbc</c> database-container path written into the 263-byte backlink.
    /// <see langword="null"/> (the default) writes an all-zero backlink — a free table.
    /// </summary>
    public string? BacklinkPath { get; init; }

    /// <summary>
    /// The concurrency strategy for the reopened writer (plan §D3). Default <see cref="Write.LockMode.Exclusive"/>
    /// — VFP parity: <c>CREATE TABLE</c> opens the new table EXCLUSIVELY, so a freshly created table is
    /// immediately PACK/ZAP/INDEX-able (the §D2 exclusivity gate is satisfied out of the box). Pass
    /// <see cref="Write.LockMode.Shared"/> explicitly when a second handle must coexist with the writer while
    /// it is still open (that stays fully supported); on <see cref="DbfWriter.Dispose"/> the file becomes a
    /// normal on-disk table either way.
    /// </summary>
    public LockMode LockMode { get; init; } = LockMode.Exclusive;

    /// <summary>Optional explicit encoding override for the reopened writer (else resolved from the code page).</summary>
    public Encoding? Encoding { get; init; }

    /// <summary>
    /// When <see langword="false"/> (the default) <see cref="DbfWriter.Create(string, System.Collections.Generic.IEnumerable{DbfColumnDef}, DbfCreateOptions)"/>
    /// refuses to clobber an existing file at the target path (<c>CreateNew</c> semantics) and throws
    /// <see cref="DbfWriteException"/>. Set <see langword="true"/> to atomically replace any existing
    /// <c>.dbf</c>/<c>.fpt</c> at that path.
    /// </summary>
    public bool Overwrite { get; init; }
}
