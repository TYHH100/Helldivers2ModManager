using System.Globalization;
using System.Text;

namespace Helldivers2ModManager.Services.Parsing;

using LuaConstantKind = LuaJitDumpDecoder.LuaConstantKind;

/// <summary>
/// Renders a decoded LuaJIT dump into a human-readable bytecode listing (similar in spirit to
/// `luajit -bl`). Pure formatting over <see cref="LuaJitDumpDecoder.LuaDumpModel"/> — no parsing
/// of untrusted data happens here and nothing is ever executed.
/// </summary>
internal static class LuaJitDumpDisassembler
{
    private const int MaxStringDisplayChars = 220;
    private const int MaxTableDisplayChars = 400;
    private const int MaxStringsSummary = 800;
    private const int MaxOutputChars = 2_000_000;

    public static string Render(LuaJitDumpDecoder.LuaDumpModel model)
    {
        var sb = new StringBuilder(64 * 1024);
        var versionText = model.Version == LuaJitDumpDecoder.DumpVersionLuaJit20 ? "LuaJIT 2.0" : "LuaJIT 2.1";
        var totalInstructions = CountInstructions(model.Root);
        sb.AppendLine($"; dump 版本: {model.Version} ({versionText}) | debug 信息: {(model.Stripped ? "已剥离" : "保留")} | FFI: {(model.HasFfi ? "引用" : "无")} | 原型: {model.ProtoCount} | 指令: {totalInstructions} | 字节: {model.ConsumedBytes}");
        if (!string.IsNullOrEmpty(model.SourceName))
            sb.AppendLine($"; 来源名: {model.SourceName}");
        sb.AppendLine("; 注: 指令编号 0001 为函数头（FUNCF/FUNCV，不含于原始 dump），跳转目标按此编号解析。");
        sb.AppendLine();

        RenderProto(sb, model, model.Root, headerLabel: "（根）");
        return sb.Length <= MaxOutputChars ? sb.ToString() : sb.ToString(0, MaxOutputChars) + "\n… [输出超长，已截断]";
    }

    private static int CountInstructions(LuaJitDumpDecoder.LuaDumpProto proto)
    {
        var total = proto.Instructions.Count + 1;
        foreach (var constant in proto.ComplexConstants)
        {
            if (constant.Kind == LuaConstantKind.Child && constant.Child is not null)
                total += CountInstructions(constant.Child);
        }

        return total;
    }

    private static void RenderProto(StringBuilder sb, LuaJitDumpDecoder.LuaDumpModel model, LuaJitDumpDecoder.LuaDumpProto proto, string headerLabel)
    {
        sb.Append("══ PROTO #").Append(proto.Id).Append(headerLabel)
            .Append(": ").Append(proto.IsVararg ? "vararg" : $"参数 {proto.NumParams}")
            .Append(" | frame ").Append(proto.FrameSize)
            .Append(" | upvalue ").Append(proto.UpvalueRefs.Count)
            .Append(" | 指令 ").Append(proto.Instructions.Count + 1);
        if (!model.Stripped && proto.LineCount > 0)
            sb.Append(" | 原始行 ").Append(proto.FirstLineNumber).Append('-').Append(proto.FirstLineNumber + proto.LineCount);
        sb.AppendLine(" ══");

        if (proto.UpvalueRefs.Count > 0)
        {
            sb.Append("  .upvalues:");
            for (var i = 0; i < proto.UpvalueRefs.Count; i++)
            {
                var name = i < proto.UpvalueNames.Count && proto.UpvalueNames[i].Length > 0
                    ? proto.UpvalueNames[i]
                    : $"uv{i}";
                sb.Append(' ').Append(name);
            }

            sb.AppendLine();
        }

        if (proto.ComplexConstants.Count > 0)
        {
            sb.AppendLine("  .constants");
            for (var i = 0; i < proto.ComplexConstants.Count; i++)
            {
                sb.Append("    KGC[").Append(i).Append("] = ");
                sb.AppendLine(RenderConstantDeclaration(proto.ComplexConstants[i]));
            }
        }

        if (proto.NumericConstants.Count > 0)
        {
            sb.AppendLine("  .numeric-constants");
            for (var i = 0; i < proto.NumericConstants.Count; i++)
            {
                var constant = proto.NumericConstants[i];
                sb.Append("    KNUM[").Append(i).Append("] = ")
                    .AppendLine(constant.IsDouble ? FormatDouble(constant.DoubleValue) : constant.IntValue.ToString(CultureInfo.InvariantCulture));
            }
        }

        if (proto.VarInfos.Count > 0)
        {
            sb.Append("  .locals:");
            foreach (var info in proto.VarInfos)
                sb.Append(' ').Append(info.Name);
            sb.AppendLine();
        }

        sb.AppendLine("  .bytecode");
        var version = model.Version;
        // Listing index 1 = synthetic function header; raw instruction i lives at listing i + 2.
        var headerOpcode = proto.IsVararg ? "FUNCV" : "FUNCF";
        sb.Append("  0001  ").Append(headerOpcode.PadRight(8)).Append(' ').Append(proto.FrameSize)
            .AppendLine(proto.IsVararg ? "                 ; vararg 函数头" : "                 ; 固定参数函数头");
        for (var i = 0; i < proto.Instructions.Count; i++)
        {
            RenderInstruction(sb, model, proto, i);
            if (sb.Length > MaxOutputChars)
            {
                sb.AppendLine("  … [清单超长截断]");
                return;
            }
        }

        sb.AppendLine();

        foreach (var constant in proto.ComplexConstants)
        {
            if (constant.Kind == LuaConstantKind.Child && constant.Child is not null)
                RenderProto(sb, model, constant.Child, string.Empty);
        }
    }

    private static void RenderInstruction(StringBuilder sb, LuaJitDumpDecoder.LuaDumpModel model, LuaJitDumpDecoder.LuaDumpProto proto, int index)
    {
        var instruction = proto.Instructions[index];
        var def = LuaJitDumpDecoder.OpcodeFor(model.Version, instruction.Opcode);
        var listingPc = index + 2;
        sb.Append("  ").AppendFormat(CultureInfo.InvariantCulture, "{0:D4}", listingPc).Append("  ");
        sb.Append(def.Name.PadRight(8)).Append(' ');

        var comment = new StringBuilder();
        AppendOperand(sb, model, proto, def.A, instruction.A, def, instruction, listingPc, comment);
        if (def.HasB)
        {
            sb.Append(' ');
            AppendOperand(sb, model, proto, def.B, instruction.B, def, instruction, listingPc, comment);
            sb.Append(' ');
            AppendOperand(sb, model, proto, def.Cd, instruction.C, def, instruction, listingPc, comment);
        }
        else
        {
            sb.Append(' ');
            AppendOperand(sb, model, proto, def.Cd, instruction.D, def, instruction, listingPc, comment);
        }

        if (comment.Length > 0)
        {
            sb.Append("   ; ").Append(comment);
        }

        sb.AppendLine();
    }

    private static void AppendOperand(
        StringBuilder sb,
        LuaJitDumpDecoder.LuaDumpModel model,
        LuaJitDumpDecoder.LuaDumpProto proto,
        LuaJitDumpDecoder.OperandKind kind,
        int rawValue,
        LuaJitDumpDecoder.OpcodeDef def,
        LuaJitDumpDecoder.LuaInstruction instruction,
        int listingPc,
        StringBuilder comment)
    {
        switch (kind)
        {
            case LuaJitDumpDecoder.OperandKind.None:
                sb.Append('-');
                break;
            case LuaJitDumpDecoder.OperandKind.Pri:
                sb.Append(rawValue switch { 0 => "nil", 1 => "false", 2 => "true", _ => rawValue.ToString(CultureInfo.InvariantCulture) });
                break;
            case LuaJitDumpDecoder.OperandKind.Jmp:
            {
                var delta = rawValue - 0x8000;
                var target = listingPc + 1 + delta;
                if (target >= 1 && target <= proto.Instructions.Count + 1)
                    sb.AppendFormat(CultureInfo.InvariantCulture, "-> {0:D4}", target);
                else
                    sb.Append("-> ???");
                comment.Append($"Δ{delta}");
                break;
            }
            case LuaJitDumpDecoder.OperandKind.Num:
            {
                var idx = rawValue;
                if (idx >= 0 && idx < proto.NumericConstants.Count)
                {
                    var constant = proto.NumericConstants[idx];
                    sb.Append("KNUM[").Append(idx).Append(']');
                    comment.Append(constant.IsDouble ? FormatDouble(constant.DoubleValue) : constant.IntValue.ToString(CultureInfo.InvariantCulture));
                }
                else
                {
                    sb.Append("KNUM?");
                    comment.Append($"idx {idx} 越界");
                }

                break;
            }
            case LuaJitDumpDecoder.OperandKind.Str:
            case LuaJitDumpDecoder.OperandKind.Tab:
            case LuaJitDumpDecoder.OperandKind.Fun:
            case LuaJitDumpDecoder.OperandKind.Cdt:
            {
                var idx = proto.ComplexConstants.Count - rawValue - 1;
                if (idx >= 0 && idx < proto.ComplexConstants.Count)
                {
                    var constant = proto.ComplexConstants[idx];
                    sb.Append("KGC[").Append(idx).Append(']');
                    comment.Append(RenderConstantDeclaration(constant));
                }
                else
                {
                    sb.Append("KGC?");
                    comment.Append($"idx {idx} 越界");
                }

                break;
            }
            case LuaJitDumpDecoder.OperandKind.Uv:
            {
                sb.Append("UV[").Append(rawValue).Append(']');
                if (rawValue < proto.UpvalueNames.Count && proto.UpvalueNames[rawValue].Length > 0)
                    comment.Append(proto.UpvalueNames[rawValue]);
                break;
            }
            case LuaJitDumpDecoder.OperandKind.Slit:
                sb.Append((short)rawValue);
                break;
            default:
                sb.Append(rawValue);
                break;
        }
    }

    internal static string RenderConstantDeclaration(LuaJitDumpDecoder.LuaComplexConstant constant) => constant.Kind switch
    {
        LuaConstantKind.String => QuoteString(constant.StringValue),
        LuaConstantKind.Child => constant.Child is null ? "proto#?" : $"proto #{constant.Child.Id}",
        LuaConstantKind.Table => constant.Table is null ? "table" : RenderTableForSource(constant.Table),
        LuaConstantKind.Int64 => $"i64 {constant.IntValue} (0x{constant.IntValue:X})",
        LuaConstantKind.UInt64 => $"u64 0x{constant.IntValue:X}",
        LuaConstantKind.Complex => $"{FormatDouble(constant.RealValue)} + {FormatDouble(constant.ImagValue)}i",
        _ => "?",
    };

    internal static string RenderTableForSource(LuaJitDumpDecoder.LuaTableConstant table)
    {
        var sb = new StringBuilder(64);
        sb.Append('{');
        var first = true;
        for (var i = 0; i < table.ArrayItems.Count; i++)
        {
            AppendTablePart(sb, ref first);
            sb.Append('[').Append(i + 1).Append("]=").Append(RenderTableValue(table.ArrayItems[i]));
            if (sb.Length > MaxTableDisplayChars)
            {
                sb.Append(" …");
                return sb.ToString();
            }
        }

        foreach (var (key, value) in table.HashItems)
        {
            AppendTablePart(sb, ref first);
            if (key is string s && IsIdentifier(s))
                sb.Append(s);
            else
                sb.Append('[').Append(RenderTableValue(key)).Append(']');
            sb.Append('=').Append(RenderTableValue(value));
            if (sb.Length > MaxTableDisplayChars)
            {
                sb.Append(" …");
                return sb.ToString();
            }
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendTablePart(StringBuilder sb, ref bool first)
    {
        if (!first)
            sb.Append(", ");
        first = false;
    }

    private static string RenderTableValue(object? value) => value switch
    {
        null => "nil",
        true => "true",
        false => "false",
        string s => QuoteString(s),
        double d => FormatDouble(d),
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        _ => "?",
    };

    private static bool IsIdentifier(string s)
    {
        if (s.Length == 0 || s[0] is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '_'))
            return false;
        foreach (var c in s)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_'))
                return false;
        }

        return true;
    }

    internal static string QuoteString(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        sb.Append('"');
        var truncated = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (sb.Length >= MaxStringDisplayChars)
            {
                truncated = value.Length - i;
                break;
            }

            var c = value[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20 || c == 0x7F)
                        sb.Append("\\x").Append(((int)c).ToString("X2", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }

        if (truncated > 0)
            sb.Append("\"…[+").Append(truncated).Append(" 字符]");
        else
            sb.Append('"');
        return sb.ToString();
    }

    private static string FormatDouble(double value)
    {
        if (double.IsNaN(value))
            return "nan";
        if (double.IsInfinity(value))
            return value > 0 ? "inf" : "-inf";
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>去重后的字符串常量汇总（便于快速定位 URL / 路径 / 可疑 API 名）。</summary>
    public static List<string> CollectStrings(LuaJitDumpDecoder.LuaDumpModel model)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        var queue = new Queue<LuaJitDumpDecoder.LuaDumpProto>();
        queue.Enqueue(model.Root);
        while (queue.Count > 0 && result.Count < MaxStringsSummary)
        {
            var proto = queue.Dequeue();
            foreach (var constant in proto.ComplexConstants)
            {
                if (constant.Kind == LuaConstantKind.Child && constant.Child is not null)
                {
                    queue.Enqueue(constant.Child);
                }
                else if (constant.Kind == LuaConstantKind.String && seen.Add(constant.StringValue))
                {
                    result.Add(constant.StringValue);
                    if (result.Count >= MaxStringsSummary)
                        break;
                }
            }
        }

        return result;
    }
}
