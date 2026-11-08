# VFP9 codepage fixtures

All files contain synthetic byte sequences, not business data.
`mkcodepages.prg` is run by `CodePageIdentificationVfp9OracleTests` through the
existing serialized VFP9 helper. Do not launch the script outside that helper.

- Each SBCS DBF has 256 C(2) records: byte 0x00..0xFF followed by `X`.
  VFP creates the table; the probe writes the bytes through FOPEN/FWRITE to avoid
  the session converting literals. `.unicode` is UTF-16LE without a BOM.
- For ordinary codepages, VFP `STRCONV(bytes,5,cp,1)` supplies the Unicode result.
- CP1255/1256: this VFP9 installation rejects CREATE TABLE with error 1914, but
  STRCONV explicitly supports both pages. VFP copies its CP1252 table and writes
  the documented 0x7D/0x7E identifier through FWRITE. These are explicitly marked
  VFP-made fixtures, not evidence that this installation supports their table drivers.
  .NET has both encodings available and therefore resolves their identifiers.
- DBCS files use U+4E00 in the first record. `.sample` contains the two bytes
  returned by VFP STRCONV for 932/936/949/950. Other records are raw probe bytes.
- `id*.dbf`: VFP copies its table and patches byte 29 to decimal 0/175/125/126/174/3,
  opens it with CPDIALOG OFF, and replaces record 229 with bytes C3 A4.
  Locally, CPDBF is zero except for id3 (1252); the record bytes and identifiers
  survive unchanged. CP1255/1256 availability differs between VFP and .NET.
  The portable tests simulate an unavailable .NET page through RegisterCodePage
  on 0xAE and verify DefaultEncoding with both CP1252 and CP1251.

The public tests compare every character and every rewritten DBF byte, allowing
only header bytes 1..3 (last-update date) to differ. The internal oracle rebuilds
and checks the checked-in fixtures, conversions and unknown-driver observations.

The documented identifiers and CPCONVERT signature are also described in the
[VFP9 help](https://www.vfphelp.com/vfp9/_5WN12PPYW.htm).

## CP620 (Mazovia) and CP895 (Kamenicky): explicit Unicode policy

VFP9 defines no Unicode mapping for these pages: STRCONV raises error 1914.
The internal oracle asserts this and reproduces every `.dbf`, `.1250`, `.852`,
`.437` and `.back*` file. These binary fixtures are VFP9 evidence, not Unicode
tables. The orchestrator explicitly chose the following policy:

1. Bytes 00..7F are ASCII (including controls). Start with the common published
   Mazovia or Kamenicky Unicode table for 80..FF.
2. If CPCONVERT to 1250 and 852 yields the same Unicode character **and both
   conversions return the original byte**, that agreed character wins.
3. Otherwise the published table decides, including when both targets agree on
   a non-reversible substitute. Target disagreement alone never defines Unicode.
4. All 256 bytes must decode and re-encode identically. Unassigned positions would
   retain their corresponding C1/private character instead of `?`; these chosen
   tables have no unassigned positions or duplicate Unicode characters.

The two `cp*.standard.txt` files contain the **unmodified** published baselines:
16 rows of 16 hexadecimal Unicode values, in byte order. They are not VFP-made.
Sources (checked 2026-11-09):

- CP620: the common [Mazovia table](https://en.wikipedia.org/wiki/Mazovia_encoding),
  namely CP437 with its Polish letters replaced. The Polish positions agree with
  [Linux kbd's Mazovia symbols](https://github.com/legionus/kbd/blob/master/src/libkeymap/syms.mazovia.h).
  That keyboard symbol file is not a complete DOS character table; its Latin-1
  defaults must not replace CP437 graphics. No curly-quote or Fido variant is used.
- CP895: [Free Pascal's Kamenicky Unicode table](https://github.com/fpc/FPCSource/blob/master/rtl/ucmaps/cp895.txt)
  (Tomas Hajny, table version 1.1), matching the common
  [Kamenicky table](https://en.wikipedia.org/wiki/Kamenick%C3%BD_encoding).
  In particular AD is U+00A7, not the alternate CP437-derived U+00A1.

### Every reversible agreement that overrides the published baseline

| Page | Byte | Published | VFP1250 byte | VFP852 byte | Chosen Unicode |
| --- | --- | --- | --- | --- | --- |
| 620 | A8 | U+00BF | A7 | F5 | U+00A7 |
| 895 | B6 | U+2562 | C2 | B6 | U+00C2 |
| 895 | DD | U+258C | DE | DD | U+0162 |

There are 167 reversible agreements for CP620 and 181 for CP895, including ASCII.
The three rows above are the complete override list. All other bytes retain the
published character. The encoding comment and public tests pin these exceptions.

### Every target disagreement and which conversion is a substitute

There are 47 disagreements for CP620 and 39 for CP895. In every case the published
character wins. The target(s) listed below lack that character and produce a
best-fit/substitute value. This describes the observed output, not a guarantee
that VFP's legacy substitutions visually resemble the original character.

| Page | Substitute target(s) | Bytes (hex) |
| --- | --- | --- |
| 620 and 895 | 1250 | B0 B1 B2 B3 B4 B9 BA BB BC BF C0 C1 C2 C3 C4 C5 C8 C9 CA CB CC CD CE D9 DA DB DC DF FE |
| 620 and 895 | 852 | E6 F1 FA |
| 620 | Both 1250 and 852 | 85 88 8A 8B 96 97 9B 9D 9F AB AC AD E3 F9 FD |
| 895 | Both 1250 and 852 | AC B5 C6 C7 E3 F9 FD |

For example, F1 is U+00B1 in both published tables and via 1250. The 852 target
substitutes U+00AD (CP620) or U+0163 (CP895), even though those bytes round-trip.
Both targets substitute the published U+2219 at F9: 1250 gives U+2022, while
852 gives U+00A8 (CP620) or U+02D8 (CP895).

The complete per-byte audit is in [cp620.decisions.csv](cp620.decisions.csv) and
[cp895.decisions.csv](cp895.decisions.csv): published value, both converted bytes
and Unicode values, both returned bytes, final character, decision and substitute
targets. Undefined CP1250 target bytes are shown as their .NET C1 characters.
These CSVs also expose equal but non-reversible substitutions, which do not
override the standard. `LegacyDosCodePageTests` independently applies the policy
to the published baselines and VFP evidence, checks every decoded/encoded byte,
and reads/rewrites all 256 records of each VFP-made DBF byte-exactly (except date).
