using System.Buffers.Binary;
using System.Text;

namespace Helldivers2ModManager.Services.Parsing;

/// <summary>
/// Static decoder for the LuaJIT serialized bytecode dump format (magic 1B 4C 4A, "ESC LJ").
///
/// SECURITY CONTRACT: this decoder is a pure bytes → structure → text transformation. It never
/// compiles, loads, evaluates or otherwise executes anything it parses, and it must never grow
/// such a path. Mod files are untrusted input; every read is bounds-checked and every structure
/// count is capped so a hostile dump cannot drive unbounded allocation, deep recursion or
/// runaway output. Format reference: ljd (LuaJIT raw-bytecode decompiler, GPL-3/MIT mixture —
/// format constants reimplemented, no source copied), cross-validated against real HD2 script
/// mod dumps (Bingus Shared Loader v3, 2026-09-12).
///
/// Layout summary (little-endian unless noted):
///   header  : "1B 4C 4A" + version byte (1=2.0, 2=2.1) + flags ULEB (bit0 BE, bit1 stripped,
///             bit2 ffi, bit3 fr2) [+ source name when not stripped]
///   stream  : prototype blocks written children-first (post order); each block:
///               ULEB size | byte flags (CHILD|VARARG|FFI|NOJIT|ILOOP) | byte numparams
///               byte framesize | byte upvalues | ULEB kgc | ULEB knum | ULEB nins
///               [ULEB dbgsize [+ ULEB firstline + ULEB lines] when not stripped]
///               nins × uint32 instructions | upvalues × uint16 | KGC × kgc | KNUM × knum
///               [debug info]
///   footer  : single 0x00 (empty ULEB = "no more prototypes")
///   KGC     : ULEB type; 0=CHILD(pop stack) 1=TABLE 2=I64 3=U64 4=COMPLEX ≥5=string(len-5)
///   KNUM    : 33-bit ULEB (bit0=isnum) [+ ULEB high half] → double, or plain signed int
/// </summary>
internal static class LuaJitDumpDecoder
{
    public const int DumpVersionLuaJit20 = 1;
    public const int DumpVersionLuaJit21 = 2;

    /// <summary>Trusted structure budget; shared by the inspection service.</summary>
    internal sealed class Limits
    {
        public int MaxDumpBytes { get; init; } = 32 * 1024 * 1024;
        public int MaxPrototypes { get; init; } = 8_192;
        public int MaxDepth { get; init; } = 16;
        public int MaxInstructionsPerProto { get; init; } = 1_000_000;
        public int MaxComplexConstants { get; init; } = 1_000_000;
        public int MaxNumericConstants { get; init; } = 1_000_000;
        public int MaxStringLength { get; init; } = 4 * 1024 * 1024;
    }

    [Serializable]
    internal sealed class LuaDumpException(string message) : Exception(message);

    internal enum OperandKind
    {
        None = 0, Var, Dst, Bs, Rbs, Uv, Lit, Slit, Pri, Num, Str, Tab, Fun, Cdt, Jmp,
    }

    internal readonly record struct OpcodeDef(string Name, OperandKind A, OperandKind B, OperandKind Cd, bool HasB);

    private static readonly OpcodeDef[] V21Table = BuildTable(new[]
    {
        (0x00, "ISLT", OperandKind.Var, OperandKind.None, OperandKind.Var),
        (0x01, "ISGE", OperandKind.Var, OperandKind.None, OperandKind.Var),
        (0x02, "ISLE", OperandKind.Var, OperandKind.None, OperandKind.Var),
        (0x03, "ISGT", OperandKind.Var, OperandKind.None, OperandKind.Var),
        (0x04, "ISEQV", OperandKind.Var, OperandKind.None, OperandKind.Var),
        (0x05, "ISNEV", OperandKind.Var, OperandKind.None, OperandKind.Var),
        (0x06, "ISEQS", OperandKind.Var, OperandKind.None, OperandKind.Str),
        (0x07, "ISNES", OperandKind.Var, OperandKind.None, OperandKind.Str),
        (0x08, "ISEQN", OperandKind.Var, OperandKind.None, OperandKind.Num),
        (0x09, "ISNEN", OperandKind.Var, OperandKind.None, OperandKind.Num),
        (0x0A, "ISEQP", OperandKind.Var, OperandKind.None, OperandKind.Pri),
        (0x0B, "ISNEP", OperandKind.Var, OperandKind.None, OperandKind.Pri),
        (0x0C, "ISTC", OperandKind.Dst, OperandKind.None, OperandKind.Var),
        (0x0D, "ISFC", OperandKind.Dst, OperandKind.None, OperandKind.Var),
        (0x0E, "IST", OperandKind.None, OperandKind.None, OperandKind.Var),
        (0x0F, "ISF", OperandKind.None, OperandKind.None, OperandKind.Var),
        (0x10, "ISTYPE", OperandKind.Var, OperandKind.None, OperandKind.Lit),
        (0x11, "ISNUM", OperandKind.Var, OperandKind.None, OperandKind.Lit),
        (0x12, "MOV", OperandKind.Dst, OperandKind.None, OperandKind.Var),
        (0x13, "NOT", OperandKind.Dst, OperandKind.None, OperandKind.Var),
        (0x14, "UNM", OperandKind.Dst, OperandKind.None, OperandKind.Var),
        (0x15, "LEN", OperandKind.Dst, OperandKind.None, OperandKind.Var),
        (0x16, "ADDVN", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x17, "SUBVN", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x18, "MULVN", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x19, "DIVVN", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x1A, "MODVN", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x1B, "ADDNV", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x1C, "SUBNV", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x1D, "MULNV", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x1E, "DIVNV", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x1F, "MODNV", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x20, "ADDVV", OperandKind.Dst, OperandKind.Var, OperandKind.Var),
        (0x21, "SUBVV", OperandKind.Dst, OperandKind.Var, OperandKind.Var),
        (0x22, "MULVV", OperandKind.Dst, OperandKind.Var, OperandKind.Var),
        (0x23, "DIVVV", OperandKind.Dst, OperandKind.Var, OperandKind.Var),
        (0x24, "MODVV", OperandKind.Dst, OperandKind.Var, OperandKind.Var),
        (0x25, "POW", OperandKind.Dst, OperandKind.Var, OperandKind.Var),
        (0x26, "CAT", OperandKind.Dst, OperandKind.Rbs, OperandKind.Rbs),
        (0x27, "KSTR", OperandKind.Dst, OperandKind.None, OperandKind.Str),
        (0x28, "KCDATA", OperandKind.Dst, OperandKind.None, OperandKind.Cdt),
        (0x29, "KSHORT", OperandKind.Dst, OperandKind.None, OperandKind.Slit),
        (0x2A, "KNUM", OperandKind.Dst, OperandKind.None, OperandKind.Num),
        (0x2B, "KPRI", OperandKind.Dst, OperandKind.None, OperandKind.Pri),
        (0x2C, "KNIL", OperandKind.Bs, OperandKind.None, OperandKind.Bs),
        (0x2D, "UGET", OperandKind.Dst, OperandKind.None, OperandKind.Uv),
        (0x2E, "USETV", OperandKind.Uv, OperandKind.None, OperandKind.Var),
        (0x2F, "USETS", OperandKind.Uv, OperandKind.None, OperandKind.Str),
        (0x30, "USETN", OperandKind.Uv, OperandKind.None, OperandKind.Num),
        (0x31, "USETP", OperandKind.Uv, OperandKind.None, OperandKind.Pri),
        (0x32, "UCLO", OperandKind.Rbs, OperandKind.None, OperandKind.Jmp),
        (0x33, "FNEW", OperandKind.Dst, OperandKind.None, OperandKind.Fun),
        (0x34, "TNEW", OperandKind.Dst, OperandKind.None, OperandKind.Lit),
        (0x35, "TDUP", OperandKind.Dst, OperandKind.None, OperandKind.Tab),
        (0x36, "GGET", OperandKind.Dst, OperandKind.None, OperandKind.Str),
        (0x37, "GSET", OperandKind.Var, OperandKind.None, OperandKind.Str),
        (0x38, "TGETV", OperandKind.Dst, OperandKind.Var, OperandKind.Var),
        (0x39, "TGETS", OperandKind.Dst, OperandKind.Var, OperandKind.Str),
        (0x3A, "TGETB", OperandKind.Dst, OperandKind.Var, OperandKind.Lit),
        (0x3B, "TGETR", OperandKind.Dst, OperandKind.Var, OperandKind.Var),
        (0x3C, "TSETV", OperandKind.Var, OperandKind.Var, OperandKind.Var),
        (0x3D, "TSETS", OperandKind.Var, OperandKind.Var, OperandKind.Str),
        (0x3E, "TSETB", OperandKind.Var, OperandKind.Var, OperandKind.Lit),
        (0x3F, "TSETM", OperandKind.Bs, OperandKind.None, OperandKind.Num),
        (0x40, "TSETR", OperandKind.Var, OperandKind.Var, OperandKind.Var),
        (0x41, "CALLM", OperandKind.Bs, OperandKind.Lit, OperandKind.Lit),
        (0x42, "CALL", OperandKind.Bs, OperandKind.Lit, OperandKind.Lit),
        (0x43, "CALLMT", OperandKind.Bs, OperandKind.None, OperandKind.Lit),
        (0x44, "CALLT", OperandKind.Bs, OperandKind.None, OperandKind.Lit),
        (0x45, "ITERC", OperandKind.Bs, OperandKind.Lit, OperandKind.Lit),
        (0x46, "ITERN", OperandKind.Bs, OperandKind.Lit, OperandKind.Lit),
        (0x47, "VARG", OperandKind.Bs, OperandKind.Lit, OperandKind.Lit),
        (0x48, "ISNEXT", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x49, "RETM", OperandKind.Bs, OperandKind.None, OperandKind.Lit),
        (0x4A, "RET", OperandKind.Rbs, OperandKind.None, OperandKind.Lit),
        (0x4B, "RET0", OperandKind.Rbs, OperandKind.None, OperandKind.Lit),
        (0x4C, "RET1", OperandKind.Rbs, OperandKind.None, OperandKind.Lit),
        (0x4D, "FORI", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x4E, "JFORI", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x4F, "FORL", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x50, "IFORL", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x51, "JFORL", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x52, "ITERL", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x53, "IITERL", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x54, "JITERL", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x55, "LOOP", OperandKind.Rbs, OperandKind.None, OperandKind.Jmp),
        (0x56, "ILOOP", OperandKind.Rbs, OperandKind.None, OperandKind.Jmp),
        (0x57, "JLOOP", OperandKind.Rbs, OperandKind.None, OperandKind.Lit),
        (0x58, "JMP", OperandKind.Rbs, OperandKind.None, OperandKind.Jmp),
        (0x59, "FUNCF", OperandKind.Rbs, OperandKind.None, OperandKind.None),
        (0x5A, "IFUNCF", OperandKind.Rbs, OperandKind.None, OperandKind.None),
        (0x5B, "JFUNCF", OperandKind.Rbs, OperandKind.None, OperandKind.Lit),
        (0x5C, "FUNCV", OperandKind.Rbs, OperandKind.None, OperandKind.None),
        (0x5D, "IFUNCV", OperandKind.Rbs, OperandKind.None, OperandKind.None),
        (0x5E, "JFUNCV", OperandKind.Rbs, OperandKind.None, OperandKind.Lit),
        (0x5F, "FUNCC", OperandKind.Rbs, OperandKind.None, OperandKind.None),
        (0x60, "FUNCCW", OperandKind.Rbs, OperandKind.None, OperandKind.None),
    });

    private static readonly OpcodeDef[] V20Table = BuildTable(new[]
    {
        // LuaJIT 2.0: ISTYPE/ISNUM/TGETR/TSETR/ISNEXT slots differ (2.1 inserted new opcodes).
        (0x00, "ISLT", OperandKind.Var, OperandKind.None, OperandKind.Var),
        (0x01, "ISGE", OperandKind.Var, OperandKind.None, OperandKind.Var),
        (0x02, "ISLE", OperandKind.Var, OperandKind.None, OperandKind.Var),
        (0x03, "ISGT", OperandKind.Var, OperandKind.None, OperandKind.Var),
        (0x04, "ISEQV", OperandKind.Var, OperandKind.None, OperandKind.Var),
        (0x05, "ISNEV", OperandKind.Var, OperandKind.None, OperandKind.Var),
        (0x06, "ISEQS", OperandKind.Var, OperandKind.None, OperandKind.Str),
        (0x07, "ISNES", OperandKind.Var, OperandKind.None, OperandKind.Str),
        (0x08, "ISEQN", OperandKind.Var, OperandKind.None, OperandKind.Num),
        (0x09, "ISNEN", OperandKind.Var, OperandKind.None, OperandKind.Num),
        (0x0A, "ISEQP", OperandKind.Var, OperandKind.None, OperandKind.Pri),
        (0x0B, "ISNEP", OperandKind.Var, OperandKind.None, OperandKind.Pri),
        (0x0C, "ISTC", OperandKind.Dst, OperandKind.None, OperandKind.Var),
        (0x0D, "ISFC", OperandKind.Dst, OperandKind.None, OperandKind.Var),
        (0x0E, "IST", OperandKind.None, OperandKind.None, OperandKind.Var),
        (0x0F, "ISF", OperandKind.None, OperandKind.None, OperandKind.Var),
        (0x10, "MOV", OperandKind.Dst, OperandKind.None, OperandKind.Var),
        (0x11, "NOT", OperandKind.Dst, OperandKind.None, OperandKind.Var),
        (0x12, "UNM", OperandKind.Dst, OperandKind.None, OperandKind.Var),
        (0x13, "LEN", OperandKind.Dst, OperandKind.None, OperandKind.Var),
        (0x14, "ADDVN", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x15, "SUBVN", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x16, "MULVN", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x17, "DIVVN", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x18, "MODVN", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x19, "ADDNV", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x1A, "SUBNV", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x1B, "MULNV", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x1C, "DIVNV", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x1D, "MODNV", OperandKind.Dst, OperandKind.Var, OperandKind.Num),
        (0x1E, "ADDVV", OperandKind.Dst, OperandKind.Var, OperandKind.Var),
        (0x1F, "SUBVV", OperandKind.Dst, OperandKind.Var, OperandKind.Var),
        (0x20, "MULVV", OperandKind.Dst, OperandKind.Var, OperandKind.Var),
        (0x21, "DIVVV", OperandKind.Dst, OperandKind.Var, OperandKind.Var),
        (0x22, "MODVV", OperandKind.Dst, OperandKind.Var, OperandKind.Var),
        (0x23, "POW", OperandKind.Dst, OperandKind.Var, OperandKind.Var),
        (0x24, "CAT", OperandKind.Dst, OperandKind.Rbs, OperandKind.Rbs),
        (0x25, "KSTR", OperandKind.Dst, OperandKind.None, OperandKind.Str),
        (0x26, "KCDATA", OperandKind.Dst, OperandKind.None, OperandKind.Cdt),
        (0x27, "KSHORT", OperandKind.Dst, OperandKind.None, OperandKind.Slit),
        (0x28, "KNUM", OperandKind.Dst, OperandKind.None, OperandKind.Num),
        (0x29, "KPRI", OperandKind.Dst, OperandKind.None, OperandKind.Pri),
        (0x2A, "KNIL", OperandKind.Bs, OperandKind.None, OperandKind.Bs),
        (0x2B, "UGET", OperandKind.Dst, OperandKind.None, OperandKind.Uv),
        (0x2C, "USETV", OperandKind.Uv, OperandKind.None, OperandKind.Var),
        (0x2D, "USETS", OperandKind.Uv, OperandKind.None, OperandKind.Str),
        (0x2E, "USETN", OperandKind.Uv, OperandKind.None, OperandKind.Num),
        (0x2F, "USETP", OperandKind.Uv, OperandKind.None, OperandKind.Pri),
        (0x30, "UCLO", OperandKind.Rbs, OperandKind.None, OperandKind.Jmp),
        (0x31, "FNEW", OperandKind.Dst, OperandKind.None, OperandKind.Fun),
        (0x32, "TNEW", OperandKind.Dst, OperandKind.None, OperandKind.Lit),
        (0x33, "TDUP", OperandKind.Dst, OperandKind.None, OperandKind.Tab),
        (0x34, "GGET", OperandKind.Dst, OperandKind.None, OperandKind.Str),
        (0x35, "GSET", OperandKind.Var, OperandKind.None, OperandKind.Str),
        (0x36, "TGETV", OperandKind.Dst, OperandKind.Var, OperandKind.Var),
        (0x37, "TGETS", OperandKind.Dst, OperandKind.Var, OperandKind.Str),
        (0x38, "TGETB", OperandKind.Dst, OperandKind.Var, OperandKind.Lit),
        (0x39, "TSETV", OperandKind.Var, OperandKind.Var, OperandKind.Var),
        (0x3A, "TSETS", OperandKind.Var, OperandKind.Var, OperandKind.Str),
        (0x3B, "TSETB", OperandKind.Var, OperandKind.Var, OperandKind.Lit),
        (0x3C, "TSETM", OperandKind.Bs, OperandKind.None, OperandKind.Num),
        (0x3D, "CALLM", OperandKind.Bs, OperandKind.Lit, OperandKind.Lit),
        (0x3E, "CALL", OperandKind.Bs, OperandKind.Lit, OperandKind.Lit),
        (0x3F, "CALLMT", OperandKind.Bs, OperandKind.None, OperandKind.Lit),
        (0x40, "CALLT", OperandKind.Bs, OperandKind.None, OperandKind.Lit),
        (0x41, "ITERC", OperandKind.Bs, OperandKind.Lit, OperandKind.Lit),
        (0x42, "ITERN", OperandKind.Bs, OperandKind.Lit, OperandKind.Lit),
        (0x43, "VARG", OperandKind.Bs, OperandKind.Lit, OperandKind.Lit),
        (0x44, "ISNEXT", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x45, "RETM", OperandKind.Bs, OperandKind.None, OperandKind.Lit),
        (0x46, "RET", OperandKind.Rbs, OperandKind.None, OperandKind.Lit),
        (0x47, "RET0", OperandKind.Rbs, OperandKind.None, OperandKind.Lit),
        (0x48, "RET1", OperandKind.Rbs, OperandKind.None, OperandKind.Lit),
        (0x49, "FORI", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x4A, "JFORI", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x4B, "FORL", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x4C, "IFORL", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x4D, "JFORL", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x4E, "ITERL", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x4F, "IITERL", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x50, "JITERL", OperandKind.Bs, OperandKind.None, OperandKind.Jmp),
        (0x51, "LOOP", OperandKind.Rbs, OperandKind.None, OperandKind.Jmp),
        (0x52, "ILOOP", OperandKind.Rbs, OperandKind.None, OperandKind.Jmp),
        (0x53, "JLOOP", OperandKind.Rbs, OperandKind.None, OperandKind.Lit),
        (0x54, "JMP", OperandKind.Rbs, OperandKind.None, OperandKind.Jmp),
        (0x55, "FUNCF", OperandKind.Rbs, OperandKind.None, OperandKind.None),
        (0x56, "IFUNCF", OperandKind.Rbs, OperandKind.None, OperandKind.None),
        (0x57, "JFUNCF", OperandKind.Rbs, OperandKind.None, OperandKind.Lit),
        (0x58, "FUNCV", OperandKind.Rbs, OperandKind.None, OperandKind.None),
        (0x59, "IFUNCV", OperandKind.Rbs, OperandKind.None, OperandKind.None),
        (0x5A, "JFUNCV", OperandKind.Rbs, OperandKind.None, OperandKind.Lit),
        (0x5B, "FUNCC", OperandKind.Rbs, OperandKind.None, OperandKind.None),
        (0x5C, "FUNCCW", OperandKind.Rbs, OperandKind.None, OperandKind.None),
    });

    private static OpcodeDef[] BuildTable((int Op, string Name, OperandKind A, OperandKind B, OperandKind Cd)[] defs)
    {
        var table = new OpcodeDef[256];
        foreach (var (op, name, a, b, cd) in defs)
            table[op] = new OpcodeDef(name, a, b, cd, b != OperandKind.None);
        for (var i = 0; i < 256; i++)
        {
            if (table[i].Name is null)
                table[i] = new OpcodeDef($"UNK_{i:X2}", OperandKind.Lit, OperandKind.Lit, OperandKind.Lit, true);
        }

        return table;
    }

    public static OpcodeDef OpcodeFor(int dumpVersion, byte opcode) =>
        (dumpVersion == DumpVersionLuaJit20 ? V20Table : V21Table)[opcode];

    // ------------------------------------------------------------------ model

    internal sealed class LuaDumpModel
    {
        public required int Version { get; init; }
        public required bool Stripped { get; init; }
        public required bool HasFfi { get; init; }
        public required string? SourceName { get; init; }
        public required LuaDumpProto Root { get; init; }
        public required int ConsumedBytes { get; init; }
        public required int ProtoCount { get; init; }
        /// <summary>嵌套内嵌 dump 的字符串常量（原样字节），按发现顺序。</summary>
        public List<byte[]> EmbeddedDumpCandidates { get; } = [];
        public long TotalInstructions { get; internal set; }
    }

    internal sealed class LuaDumpProto
    {
        public int Id { get; internal set; }
        public bool IsVararg { get; internal set; }
        public bool HasChildFlag { get; internal set; }
        public bool JitDisabled { get; internal set; }
        public int NumParams { get; internal set; }
        public int FrameSize { get; internal set; }
        public List<LuaInstruction> Instructions { get; } = [];
        public List<LuaComplexConstant> ComplexConstants { get; } = [];
        public List<LuaNumericConstant> NumericConstants { get; } = [];
        public List<(ushort ParentIndex, ushort ChildIndex)> UpvalueRefs { get; } = [];
        public List<string> UpvalueNames { get; } = [];
        public List<LuaVarInfo> VarInfos { get; } = [];
        public int FirstLineNumber { get; internal set; }
        public int LineCount { get; internal set; }
        public List<int> LineMap { get; } = [];
        public int Depth { get; internal set; }
    }

    internal sealed class LuaInstruction
    {
        public required byte Opcode { get; init; }
        public required uint Raw { get; init; }
        public required byte A { get; init; }
        public required byte B { get; init; }
        public required byte C { get; init; }
        public required ushort D { get; init; }
    }

    internal enum LuaConstantKind { String, Table, Child, Int64, UInt64, Complex }

    internal sealed class LuaComplexConstant
    {
        public required LuaConstantKind Kind { get; init; }
        public string StringValue { get; internal set; } = string.Empty;
        public byte[] RawBytes { get; init; } = [];
        public LuaDumpProto? Child { get; internal set; }
        public LuaTableConstant? Table { get; internal set; }
        public long IntValue { get; internal set; }
        public double RealValue { get; internal set; }
        public double ImagValue { get; internal set; }
    }

    internal sealed class LuaTableConstant
    {
        public List<object?> ArrayItems { get; } = [];
        public List<(object? Key, object? Value)> HashItems { get; } = [];
    }

    internal readonly record struct LuaNumericConstant(bool IsDouble, double DoubleValue, long IntValue);

    internal sealed class LuaVarInfo
    {
        public required string Name { get; init; }
        public required bool Internal { get; init; }
        public required int StartPc { get; init; }
        public required int EndPc { get; init; }
    }

    // ------------------------------------------------------------------ reader

    private sealed class Reader(byte[] data, int offset, int end)
    {
        public int Pos { get; private set; } = offset;
        private readonly int _end = end;

        public bool Eof => Pos >= _end;

        public byte PeekByte()
        {
            if (Pos >= _end)
                throw new LuaDumpException("unexpected end of data");
            return data[Pos];
        }

        public byte ReadByte()
        {
            if (Pos >= _end)
                throw new LuaDumpException("unexpected end of data");
            return data[Pos++];
        }

        public void ReadBytes(int count, out ReadOnlySpan<byte> span)
        {
            if (count < 0 || _end - Pos < count)
                throw new LuaDumpException("unexpected end of data");
            span = data.AsSpan(Pos, count);
            Pos += count;
        }

        public uint ReadUInt32()
        {
            if (_end - Pos < 4)
                throw new LuaDumpException("unexpected end of data");
            var v = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(Pos, 4));
            Pos += 4;
            return v;
        }

        public ushort ReadUInt16()
        {
            if (_end - Pos < 2)
                throw new LuaDumpException("unexpected end of data");
            var v = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(Pos, 2));
            Pos += 2;
            return v;
        }

        public uint ReadUleb128()
        {
            uint value = ReadByte();
            if (value < 0x80)
                return value;
            value &= 0x7F;
            var shift = 0;
            while (true)
            {
                var b = ReadByte();
                shift += 7;
                if (shift > 28 && b > 0x0F)
                    throw new LuaDumpException("ULEB128 value out of range");
                value |= (uint)(b & 0x7F) << shift;
                if (b < 0x80)
                    return value;
                if (shift >= 28)
                    throw new LuaDumpException("ULEB128 too long");
            }
        }

        /// <summary>33-bit ULEB used by KNUM (bit0 = is-double flag); port of ljd binstream.</summary>
        public (bool IsNum, uint Value) ReadUleb128From33Bit()
        {
            var first = ReadByte();
            var isNum = (first & 0x01) != 0;
            uint value = (uint)first >> 1;
            if (value >= 0x40)
            {
                var bitshift = -1;
                value &= 0x3F;
                while (true)
                {
                    var b = ReadByte();
                    bitshift += 7;
                    value |= (uint)(b & 0x7F) << bitshift;
                    if (b < 0x80)
                        break;
                    if (bitshift > 35)
                        throw new LuaDumpException("33-bit ULEB128 too long");
                }
            }

            return (isNum, value);
        }
    }

    // ------------------------------------------------------------------ parse

    public static bool LooksLikeDump(ReadOnlySpan<byte> data) =>
        data.Length >= 5 && data[0] == 0x1B && data[1] == 0x4C && data[2] == 0x4A;

    /// <summary>
    /// Parses one complete dump starting at <paramref name="offset"/>. Returns false on any
    /// structural violation (the caller should treat the blob as unparseable binary, not as a
    /// reason to retry execution — there is no execution path anywhere in this pipeline).
    /// </summary>
    public static bool TryParse(
        byte[] data,
        int offset,
        int availableBytes,
        Limits limits,
        string? sourceName,
        out LuaDumpModel? model,
        out string? error)
    {
        model = null;
        error = null;
        availableBytes = Math.Min(availableBytes, data.Length - offset);
        if (offset < 0 || availableBytes < 5 || availableBytes > limits.MaxDumpBytes)
        {
            error = "dump size out of bounds";
            return false;
        }

        var end = offset + availableBytes;
        if (data[offset] != 0x1B || data[offset + 1] != 0x4C || data[offset + 2] != 0x4A)
        {
            error = "bad magic";
            return false;
        }

        var version = data[offset + 3];
        if (version is not (DumpVersionLuaJit20 or DumpVersionLuaJit21))
        {
            error = $"unsupported dump version {version}";
            return false;
        }

        var reader = new Reader(data, offset + 4, end);
        try
        {
            var flags = reader.ReadUleb128();
            if ((flags & 0x01) != 0)
            {
                error = "big-endian dump not supported";
                return false;
            }

            var stripped = (flags & 0x02) != 0;
            var hasFfi = (flags & 0x04) != 0;
            if ((flags & 0xF8) != 0)
            {
                error = $"unknown dump flags 0x{flags:X}";
                return false;
            }

            string? sourceNameFromDump = null;
            if (!stripped)
            {
                var nameLen = (int)reader.ReadUleb128();
                if (nameLen > 4096)
                    throw new LuaDumpException("source name too long");
                reader.ReadBytes(nameLen, out var nameSpan);
                sourceNameFromDump = Encoding.UTF8.GetString(nameSpan);
            }

            var prototypes = new List<LuaDumpProto>(16);
            var protoCount = 0;
            while (reader.Pos < end && reader.PeekByte() != 0x00)
            {
                if (++protoCount > limits.MaxPrototypes)
                    throw new LuaDumpException("too many prototypes");
                prototypes.Add(ReadPrototype(reader, prototypes, stripped, limits, depth: 0));
            }

            if (prototypes.Count != 1)
            {
                error = $"invalid prototype stack (count={prototypes.Count})";
                return false;
            }

            var consumed = reader.Pos - offset;
            if (reader.Pos < end && data[reader.Pos] == 0x00)
                consumed++; // trailing 0x00 terminator belongs to the dump

            var root = prototypes[0];
            var modelLocal = new LuaDumpModel
            {
                Version = version,
                Stripped = stripped,
                HasFfi = hasFfi,
                SourceName = sourceNameFromDump ?? sourceName,
                Root = root,
                ConsumedBytes = consumed,
                ProtoCount = CountPrototypes(root),
            };
            AssignProtoIds(root);
            CollectEmbeddedCandidates(root, modelLocal.EmbeddedDumpCandidates, limits.MaxStringLength);
            model = modelLocal;
            return true;
        }
        catch (LuaDumpException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or OverflowException)
        {
            error = $"malformed dump: {ex.Message}";
            return false;
        }
    }

    private static LuaDumpProto ReadPrototype(Reader reader, List<LuaDumpProto> stack, bool stripped, Limits limits, int depth)
    {
        if (depth > limits.MaxDepth)
            throw new LuaDumpException("prototype nesting too deep");

        var size = reader.ReadUleb128();
        if (size == 0)
            throw new LuaDumpException("empty prototype block");
        if (size > (uint)(int.MaxValue - 1) || (long)reader.Pos + size > int.MaxValue)
            throw new LuaDumpException("prototype block too large");
        var start = reader.Pos;
        // The declared size is validated at the end of the block; every individual read is
        // additionally bounds-checked against the whole buffer, so a hostile size cannot
        // drive reads out of range — it can only make the block fail validation.

        var proto = new LuaDumpProto { Depth = depth };

        var flagBits = reader.ReadByte();
        proto.HasChildFlag = (flagBits & 0x01) != 0;
        proto.IsVararg = (flagBits & 0x02) != 0;
        proto.JitDisabled = (flagBits & 0x08) != 0;
        // bit2 (FFI) and bit4 (ILOOP) are informational; everything above bit4 is unknown.
        if ((flagBits & 0xE0) != 0)
            throw new LuaDumpException($"unknown prototype flags 0x{flagBits:X}");

        proto.NumParams = reader.ReadByte();
        proto.FrameSize = reader.ReadByte();
        var upvalueCount = reader.ReadByte();
        var complexCount = (int)reader.ReadUleb128();
        var numericCount = (int)reader.ReadUleb128();
        var instructionCount = (int)reader.ReadUleb128();
        if (instructionCount > limits.MaxInstructionsPerProto ||
            complexCount > limits.MaxComplexConstants ||
            numericCount > limits.MaxNumericConstants)
            throw new LuaDumpException("prototype section count over limit");

        var firstLine = 0;
        var lineCount = 0;
        if (!stripped)
        {
            var debugSize = (int)reader.ReadUleb128();
            if (debugSize > 0)
            {
                firstLine = (int)reader.ReadUleb128();
                lineCount = (int)reader.ReadUleb128();
            }
        }

        proto.FirstLineNumber = firstLine;
        proto.LineCount = lineCount;

        // Instructions. The stored stream excludes the synthetic FUNCF/FUNCV header, which the
        // listing renderer prepends; keep raw storage faithful to the dump.
        for (var i = 0; i < instructionCount; i++)
        {
            var cw = reader.ReadUInt32();
            proto.Instructions.Add(new LuaInstruction
            {
                Opcode = (byte)(cw & 0xFF),
                Raw = cw,
                A = (byte)(cw >> 8),
                C = (byte)(cw >> 16),
                D = (ushort)(cw >> 16),
                B = (byte)(cw >> 24),
            });
        }

        for (var i = 0; i < upvalueCount; i++)
            proto.UpvalueRefs.Add((reader.ReadUInt16(), 0));

        // KGC constants. Strings and children interleave freely; children were written earlier
        // in the stream and are referenced by popping the completed-prototype stack.
        for (var i = 0; i < complexCount; i++)
        {
            var type = reader.ReadUleb128();
            if (type >= 5)
            {
                var len = (int)(type - 5);
                if (len > limits.MaxStringLength)
                    throw new LuaDumpException("string constant too long");
                reader.ReadBytes(len, out var span);
                var constant = new LuaComplexConstant { Kind = LuaConstantKind.String, RawBytes = span.ToArray() };
                constant.StringValue = DecodeLossy(constant.RawBytes);
                proto.ComplexConstants.Add(constant);
            }
            else if (type == 0)
            {
                if (stack.Count == 0)
                    throw new LuaDumpException("CHILD constant with empty prototype stack");
                var child = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                proto.ComplexConstants.Add(new LuaComplexConstant { Kind = LuaConstantKind.Child, Child = child });
            }
            else if (type == 1)
            {
                proto.ComplexConstants.Add(new LuaComplexConstant
                {
                    Kind = LuaConstantKind.Table,
                    Table = ReadTableConstant(reader, limits, depth),
                });
            }
            else if (type is 2 or 3 or 4)
            {
                // I64/U64/COMPLEX: two plain ULEB128 halves assembling the 64-bit pattern
                // (33-bit encoding is reserved for KNUM; see ljd constants.py).
                var constant = new LuaComplexConstant
                {
                    Kind = type switch
                    {
                        2 => LuaConstantKind.Int64,
                        3 => LuaConstantKind.UInt64,
                        _ => LuaConstantKind.Complex,
                    },
                };
                var lo = reader.ReadUleb128();
                var hi = reader.ReadUleb128();
                var bits = ((ulong)hi << 32) | lo;
                if (type == 4)
                {
                    constant.RealValue = BitConverter.UInt64BitsToDouble(bits);
                    var lo2 = reader.ReadUleb128();
                    var hi2 = reader.ReadUleb128();
                    constant.ImagValue = BitConverter.UInt64BitsToDouble(((ulong)hi2 << 32) | lo2);
                }
                else
                {
                    constant.IntValue = unchecked((long)bits);
                }

                proto.ComplexConstants.Add(constant);
            }
            else
            {
                throw new LuaDumpException($"unknown KGC type {type}");
            }
        }

        // KNUM constants.
        for (var i = 0; i < numericCount; i++)
        {
            var (isNum, lo) = reader.ReadUleb128From33Bit();
            if (isNum)
            {
                var hi = reader.ReadUleb128();
                proto.NumericConstants.Add(new LuaNumericConstant(
                    true, BitConverter.UInt64BitsToDouble(((ulong)hi << 32) | lo), 0));
            }
            else
            {
                proto.NumericConstants.Add(new LuaNumericConstant(false, 0, SignExtend(lo)));
            }
        }

        if (!stripped)
        {
            ReadDebugInfo(reader, proto, instructionCount, upvalueCount);
        }

        var consumed = reader.Pos - start;
        if (consumed != (int)size)
            throw new LuaDumpException($"prototype size mismatch (declared {size}, read {consumed})");
        return proto;
    }

    private static void ReadDebugInfo(Reader reader, LuaDumpProto proto, int instructionCount, int upvalueCount)
    {
        // Line map covers the synthetic function-header instruction too.
        var entrySize = proto.LineCount >= 65536 ? 4 : proto.LineCount >= 256 ? 2 : 1;
        proto.LineMap.Add(0);
        for (var i = 1; i <= instructionCount; i++)
        {
            var raw = entrySize switch
            {
                4 => reader.ReadUInt32(),
                2 => reader.ReadUInt16(),
                _ => reader.ReadByte(),
            };
            proto.LineMap.Add(proto.FirstLineNumber + (int)raw);
        }

        for (var i = 0; i < upvalueCount; i++)
            proto.UpvalueNames.Add(ReadZString(reader));

        var lastPc = 0;
        while (true)
        {
            var marker = reader.ReadByte();
            string name;
            bool internalName;
            if (marker >= 7)
            {
                var suffix = ReadZString(reader);
                name = (char)marker + suffix;
                internalName = false;
            }
            else if (marker == 0)
            {
                break;
            }
            else
            {
                name = marker switch
                {
                    1 => "(for index)",
                    2 => "(for limit)",
                    3 => "(for step)",
                    4 => "(for generator)",
                    5 => "(for state)",
                    6 => "(for control)",
                    _ => "(internal)",
                };
                internalName = true;
            }

            var startPc = lastPc + (int)reader.ReadUleb128();
            var endPc = startPc + (int)reader.ReadUleb128();
            lastPc = startPc;
            proto.VarInfos.Add(new LuaVarInfo { Name = name, Internal = internalName, StartPc = startPc, EndPc = endPc });
            if (proto.VarInfos.Count > 1_000_000)
                throw new LuaDumpException("variable info entries over limit");
        }
    }

    private static string ReadZString(Reader reader)
    {
        const int MaxZString = 64 * 1024;
        var bytes = new List<byte>(32);
        while (true)
        {
            var b = reader.ReadByte();
            if (b == 0)
                return Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Add(b);
            if (bytes.Count > MaxZString)
                throw new LuaDumpException("debug string too long");
        }
    }

    private static LuaTableConstant ReadTableConstant(Reader reader, Limits limits, int depth)
    {
        if (depth > limits.MaxDepth)
            throw new LuaDumpException("table constant nesting too deep");
        var table = new LuaTableConstant();
        var arrayCount = (int)reader.ReadUleb128();
        var hashCount = (int)reader.ReadUleb128();
        if (arrayCount > limits.MaxComplexConstants || hashCount > limits.MaxComplexConstants)
            throw new LuaDumpException("table constant size over limit");
        for (var i = 0; i < arrayCount; i++)
            table.ArrayItems.Add(ReadTableItem(reader, limits));
        for (var i = 0; i < hashCount; i++)
        {
            var key = ReadTableItem(reader, limits);
            var value = ReadTableItem(reader, limits);
            table.HashItems.Add((key, value));
        }

        return table;
    }

    private static object? ReadTableItem(Reader reader, Limits limits)
    {
        var type = reader.ReadUleb128();
        if (type >= 5)
        {
            var len = (int)(type - 5);
            if (len > limits.MaxStringLength)
                throw new LuaDumpException("table string too long");
            reader.ReadBytes(len, out var span);
            return DecodeLossy(span.ToArray());
        }

        switch (type)
        {
            case 0: return null;
            case 1: return false;
            case 2: return true;
            case 3:
                // KTAB_INT: plain ULEB128, sign-processed (ljd _read_signed_int).
                return SignExtend(reader.ReadUleb128());
            case 4:
                // KTAB_NUM: two plain ULEB128 halves assembling the double bit pattern.
                var lo = reader.ReadUleb128();
                var hi = reader.ReadUleb128();
                return BitConverter.UInt64BitsToDouble(((ulong)hi << 32) | lo);
            default:
                throw new LuaDumpException($"unknown table item type {type}");
        }
    }

    private static long SignExtend(uint value) =>
        (value & 0x80000000) != 0 ? -0x100000000L + value : value;

    private static int CountPrototypes(LuaDumpProto root)
    {
        var count = 1;
        foreach (var constant in root.ComplexConstants)
        {
            if (constant.Kind == LuaConstantKind.Child && constant.Child is not null)
                count += CountPrototypes(constant.Child);
        }

        return count;
    }

    private static void AssignProtoIds(LuaDumpProto root)
    {
        var next = 0;
        root.Id = next++;
        var queue = new Queue<LuaDumpProto>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var proto = queue.Dequeue();
            foreach (var constant in proto.ComplexConstants)
            {
                if (constant.Kind == LuaConstantKind.Child && constant.Child is { } child)
                {
                    child.Id = next++;
                    queue.Enqueue(child);
                }
            }
        }
    }

    private static void CollectEmbeddedCandidates(LuaDumpProto root, List<byte[]> sink, int maxStringLength)
    {
        var visited = new HashSet<LuaDumpProto>();
        var queue = new Queue<LuaDumpProto>();
        queue.Enqueue(root);
        visited.Add(root);
        while (queue.Count > 0)
        {
            var proto = queue.Dequeue();
            foreach (var constant in proto.ComplexConstants)
            {
                if (constant.Kind == LuaConstantKind.Child && constant.Child is { } child && visited.Add(child))
                {
                    queue.Enqueue(child);
                }
                else if (constant.Kind == LuaConstantKind.String &&
                         constant.RawBytes.Length >= 5 &&
                         constant.RawBytes[0] == 0x1B && constant.RawBytes[1] == 0x4C && constant.RawBytes[2] == 0x4A &&
                         constant.RawBytes.Length <= maxStringLength)
                {
                    sink.Add(constant.RawBytes);
                }
            }
        }
    }

    /// <summary>Lossy UTF-8 decode that never throws and never produces control-character spam.</summary>
    internal static string DecodeLossy(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text;
    }
}
