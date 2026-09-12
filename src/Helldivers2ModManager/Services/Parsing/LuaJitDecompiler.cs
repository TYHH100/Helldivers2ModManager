using System.Globalization;
using System.Text;

namespace Helldivers2ModManager.Services.Parsing;

using LuaConstantKind = LuaJitDumpDecoder.LuaConstantKind;

/// <summary>
/// Best-effort structural decompiler: rebuilds Lua-like source from a decoded LuaJIT dump.
///
/// FAITHFULNESS CONTRACT (security-review context — wrong output is worse than no output):
///  - Statement order always follows instruction order; nothing is reordered or deduplicated.
///  - Expressions fold only inside a straight-line run (no jump target in between) and only for
///    pure producers; CALL results are always materialized as explicit assignments so side
///    effects can never be silently dropped.
///  - Control flow is rebuilt strictly from the actual jump structure. Branch semantics per
///    LuaJIT: the instruction following a comparison/test is a JMP that is taken when the test
///    FAILS — i.e. IST jumps when truthy (fall-through = falsy), ISF jumps when falsy
///    (fall-through = truthy), comparisons jump when the comparison is false. Generic-for loops
///    come in two layouts (body after ITERC, or ITERC at the loop bottom with an entry jump);
///    both are reconstructed. Anything that does not fit (repeat-until, irreducible flow,
///    unknown MULTRES context) makes the whole prototype bail to the authoritative listing —
///    guessed source is never emitted.
///  - The generated text is a review aid, not compilable Lua: embedded-dump strings are elided
///    with a marker and local names come from registers (slotN) when debug info is stripped.
///    The authoritative bytecode listing ships alongside it in the same report.
/// </summary>
internal static class LuaJitDecompiler
{
    private const int MaxOutputChars = 2_000_000;

    public static bool TryDecompile(LuaJitDumpDecoder.LuaDumpModel model, out string source, out string? bailReason)
    {
        try
        {
            var ctx = new Ctx(model);
            if (!EmitFunction(ctx, model.Root, out var body, out var reason))
            {
                source = string.Empty;
                bailReason = reason;
                return false;
            }

            source = body;
            bailReason = null;
            return true;
        }
        catch (DecompileBail ex)
        {
            source = string.Empty;
            bailReason = ex.Message;
            return false;
        }
    }

    private sealed class DecompileBail(string message) : Exception(message);

    private sealed class Ctx(LuaJitDumpDecoder.LuaDumpModel model)
    {
        public readonly LuaJitDumpDecoder.LuaDumpModel Model = model;
        public readonly Dictionary<LuaJitDumpDecoder.LuaDumpProto, string> FunctionCache = [];
    }

    // ------------------------------------------------------------------ function level

    private static bool EmitFunction(Ctx ctx, LuaJitDumpDecoder.LuaDumpProto proto, out string source, out string? reason)
    {
        if (ctx.FunctionCache.TryGetValue(proto, out var cached))
        {
            source = cached;
            reason = null;
            return true;
        }

        if (proto.Instructions.Count == 0)
        {
            source = proto.IsVararg ? "function(...)\nend" : "function()\nend";
            reason = null;
            ctx.FunctionCache[proto] = source;
            return true;
        }

        var analysis = new ProtoAnalysis(proto);
        if (analysis.HasUnsupportedControlFlow)
        {
            source = string.Empty;
            reason = $"proto #{proto.Id}: contains control flow the source restoration cannot verify (pc: {string.Join(", ", analysis.UnsupportedPcs)})";
            return false;
        }

        var bodySb = new StringBuilder(4 * 1024);
        var emitter = new Emitter(ctx, proto, analysis, bodySb);
        emitter.EmitRange(2, proto.Instructions.Count + 2);
        if (emitter.BailReason is not null)
        {
            source = string.Empty;
            reason = emitter.BailReason;
            return false;
        }

        var text = BuildHeader(proto) + bodySb + "end";
        ctx.FunctionCache[proto] = text;
        source = text;
        reason = null;
        return true;
    }

    private static string BuildHeader(LuaJitDumpDecoder.LuaDumpProto proto)
    {
        var sb = new StringBuilder(64);
        sb.Append("function(");
        var names = proto.VarInfos.Where(static v => !v.Internal).Select(static v => v.Name).ToList();
        for (var i = 0; i < proto.NumParams; i++)
        {
            if (i > 0)
                sb.Append(", ");
            sb.Append(i < names.Count ? SanitizeName(names[i]) : SlotName(i));
        }

        if (proto.IsVararg)
        {
            if (proto.NumParams > 0)
                sb.Append(", ");
            sb.Append("...");
        }

        sb.Append(")\n");
        return sb.ToString();
    }

    private static string SanitizeName(string name)
    {
        if (name.Length > 0 && (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
            name.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            return name;
        uint hash = 2166136261;
        foreach (var c in name)
        {
            hash ^= c;
            hash *= 16777619;
        }

        return "slot_" + hash.ToString("X", CultureInfo.InvariantCulture);
    }

    private static string SlotName(int slot) => $"slot{slot}";

    // ------------------------------------------------------------------ static analysis

    private sealed class ProtoAnalysis
    {
        public readonly LuaJitDumpDecoder.LuaDumpProto Proto;
        public readonly int PcCount;
        public readonly LuaJitDumpDecoder.OpcodeDef[] Defs;
        public readonly HashSet<int> Targets = [];
        /// <summary>while 循环顶（LOOP 所在 pc）→ 回边 JMP 的 pc。</summary>
        public readonly Dictionary<int, int> WhileLoops = [];
        /// <summary>数值 for：FORI pc → (FORL pc)。</summary>
        public readonly Dictionary<int, int> NumericFors = [];
        /// <summary>泛型 for：入口 pc → (ITERL pc, 循环体首 pc, ITERC pc)。</summary>
        public readonly Dictionary<int, (int IterlPc, int BodyStart, int IterCPc)> GenericForEntries = [];
        public bool HasUnsupportedControlFlow;
        public readonly List<int> UnsupportedPcs = [];

        public ProtoAnalysis(LuaJitDumpDecoder.LuaDumpProto proto)
        {
            Proto = proto;
            PcCount = proto.Instructions.Count + 2;
            Defs = new LuaJitDumpDecoder.OpcodeDef[PcCount];
            Defs[1] = new LuaJitDumpDecoder.OpcodeDef(
                proto.IsVararg ? "FUNCV" : "FUNCF",
                LuaJitDumpDecoder.OperandKind.None, LuaJitDumpDecoder.OperandKind.None, LuaJitDumpDecoder.OperandKind.None, false);
            for (var i = 0; i < proto.Instructions.Count; i++)
                Defs[i + 2] = LuaJitDumpDecoder.OpcodeFor(2, proto.Instructions[i].Opcode);

            // 第一遍 A：ISNEXT 入口（for-in pairs 的首迭代形式），其 ITERN/ITERL 随之消费。
            var consumedGenericPcs = new HashSet<int>();
            for (var pc = 2; pc < PcCount; pc++)
            {
                if (Defs[pc].Name != "ISNEXT")
                    continue;
                var c = pc + 1 + Delta(At(pc).D);
                if (c < PcCount && Defs[c].Name is "ITERN" or "ITERC" &&
                    c + 1 < PcCount && Defs[c + 1].Name == "ITERL")
                {
                    var b = c + 2 + Delta(At(c + 1).D);
                    if (b >= 2 && b <= c)
                    {
                        GenericForEntries[pc] = (c + 1, b, c);
                        Targets.Add(b);
                        Targets.Add(c + 2);
                        consumedGenericPcs.Add(c);
                        consumedGenericPcs.Add(c + 1);
                    }
                    else
                    {
                        HasUnsupportedControlFlow = true; UnsupportedPcs.Add(pc);
                    }
                }
                else
                {
                    HasUnsupportedControlFlow = true; UnsupportedPcs.Add(pc);
                }
            }

            // 第一遍 B：数值 for、剩余泛型 for（两种布局）。
            for (var pc = 2; pc < PcCount; pc++)
            {
                switch (Defs[pc].Name)
                {
                    case "FORI":
                    {
                        var exit = pc + 1 + Delta(At(pc).D);
                        var forl = exit - 1;
                        if (forl > pc + 1 && forl < PcCount && Defs[forl].Name == "FORL")
                        {
                            NumericFors[pc] = forl;
                            Targets.Add(pc + 1);
                            Targets.Add(exit);
                        }
                        else
                        {
                            HasUnsupportedControlFlow = true; UnsupportedPcs.Add(pc);
                        }

                        break;
                    }
                    case "ITERC":
                    case "ITERN":
                        if (!consumedGenericPcs.Contains(pc))
                            AnalyzeGenericFor(pc);
                        break;
                    case "JFORL" or "JITERL" or "JLOOP" or "ILOOP" or "IFORL" or "IITERL"
                        or "JFUNCF" or "JFUNCV" or "IFUNCF" or "IFUNCV" or "FUNCC" or "FUNCCW":
                        HasUnsupportedControlFlow = true; UnsupportedPcs.Add(pc);
                        break;
                }
            }

            // while：唯一合法的向后 JMP 落在 LOOP 上（Lua 无 continue，回边唯一）。
            for (var pc = 2; pc < PcCount; pc++)
            {
                if (Defs[pc].Name != "JMP")
                    continue;
                var target = pc + 1 + Delta(At(pc).D);
                if (target < pc && Defs[target].Name == "LOOP" && !WhileLoops.ContainsKey(target))
                {
                    WhileLoops[target] = pc;
                    Targets.Add(pc + 1);
                }
            }
        }

        private void AnalyzeGenericFor(int iterCPc)
        {
            var j = iterCPc + 1;
            if (j < PcCount && Defs[j].Name == "ITERL")
            {
                var t = j + 1 + Delta(At(j).D);
                if (t == iterCPc + 1)
                {
                    // 布局 A：体紧跟 ITERC 之后。
                    GenericForEntries[iterCPc] = (j, iterCPc + 1, iterCPc);
                    Targets.Add(iterCPc + 1);
                    Targets.Add(j + 1);
                    return;
                }

                if (t < iterCPc && t >= 2 && Defs[t - 1].Name == "JMP" && t - 1 + 1 + Delta(At(t - 1).D) == iterCPc)
                {
                    // 布局 B（LuaJIT 常见）：ITERC 在循环体底部，入口 JMP 先跳到 ITERC。
                    GenericForEntries[t - 1] = (j, t, iterCPc);
                    Targets.Add(t);
                    Targets.Add(j + 1);
                    return;
                }

                HasUnsupportedControlFlow = true; UnsupportedPcs.Add(iterCPc);
                return;
            }

            // 布局 A 变体：ITERL 在体之后（体非空且不含更高级结构）。
            for (var k = iterCPc + 1; k < PcCount; k++)
            {
                var name = Defs[k].Name;
                if (name == "ITERL" && k + 1 + Delta(At(k).D) == iterCPc + 1)
                {
                    GenericForEntries[iterCPc] = (k, iterCPc + 1, iterCPc);
                    Targets.Add(iterCPc + 1);
                    Targets.Add(k + 1);
                    return;
                }

                if (name is "FORI" or "ITERC" or "ITERN" or "LOOP" or "RET0" or "RET1" or "RET" or "CALLT" or "CALLMT" or "RETM")
                    break;
            }

            HasUnsupportedControlFlow = true; UnsupportedPcs.Add(iterCPc);
        }

        public LuaJitDumpDecoder.LuaInstruction At(int pc) => Proto.Instructions[pc - 2];

        private static int Delta(int rawD) => rawD - 0x8000;

        public bool IsConditional(int pc) => Defs[pc].Name is
            "ISLT" or "ISGE" or "ISLE" or "ISGT" or
            "ISEQV" or "ISNEV" or "ISEQS" or "ISNES" or "ISEQN" or "ISNEN" or "ISEQP" or "ISNEP" or
            "IST" or "ISF";
    }

    // ------------------------------------------------------------------ emitter

    private sealed class Emitter
    {
        private readonly Ctx _ctx;
        private readonly LuaJitDumpDecoder.LuaDumpProto _proto;
        private readonly ProtoAnalysis _an;
        private readonly StringBuilder _sb;
        private int _indent;

        /// <summary>当前直线段内的纯表达式槽（slot → 尚未被消费的表达式文本）。</summary>
        private readonly Dictionary<int, string> _pending = [];

        /// <summary>MULTRES 状态：最近一个 B==0 的 CALL/VARG 产生的多值表达式。</summary>
        private string? _multresExpr;
        private int _multresBase = -1;

        /// <summary>循环出口栈（break 的目标 = 最内层出口）。</summary>
        private readonly List<int> _loopExits = [];

        public string? BailReason { get; private set; }

        public Emitter(Ctx ctx, LuaJitDumpDecoder.LuaDumpProto proto, ProtoAnalysis analysis, StringBuilder sb)
        {
            _ctx = ctx;
            _proto = proto;
            _an = analysis;
            _sb = sb;
        }

        private void Bail(string reason) => BailReason ??= reason;

        private LuaJitDumpDecoder.LuaInstruction At(int pc) => _proto.Instructions[pc - 2];

        private LuaJitDumpDecoder.OpcodeDef Def(int pc) => _an.Defs[pc];

        private void Line(string text)
        {
            _sb.Append(' ', _indent * 4).AppendLine(text);
            if (_sb.Length > MaxOutputChars)
                throw new DecompileBail("decompiled output too large");
        }

        public void EmitRange(int from, int to)
        {
            var pc = from;
            while (pc < to)
            {
                if (BailReason is not null)
                    return;

                if (_an.Targets.Contains(pc))
                    ResetFoldState();

                if (_an.NumericFors.TryGetValue(pc, out var forlPc))
                {
                    EmitNumericFor(pc, forlPc);
                    pc = forlPc + 1;
                    continue;
                }

                if (_an.GenericForEntries.TryGetValue(pc, out var gf))
                {
                    EmitGenericFor(pc, gf.IterlPc, gf.BodyStart, gf.IterCPc);
                    pc = gf.IterlPc + 1;
                    continue;
                }

                if (_an.WhileLoops.TryGetValue(pc, out var backEdge))
                {
                    EmitWhile(pc, backEdge);
                    pc = backEdge + 1;
                    continue;
                }

                if (_an.IsConditional(pc))
                {
                    if (pc + 1 >= to || Def(pc + 1).Name != "JMP")
                    {
                        Bail($"comparison without JMP at pc {pc}");
                        return;
                    }

                    var jmpTarget = pc + 2 + (At(pc + 1).D - 0x8000);
                    if (jmpTarget <= pc + 1)
                    {
                        Bail($"comparison jump backwards at pc {pc}");
                        return;
                    }

                    pc = EmitIf(pc, jmpTarget, to);
                    continue;
                }

                switch (Def(pc).Name)
                {
                    case "JMP":
                    case "UCLO":
                    {
                        var target = pc + 1 + (At(pc).D - 0x8000);
                        if (target == to || target == to + 1 || target == pc + 1)
                        {
                            pc++; // 区域收口 / 越过区域边界的真值路径 / 关闭 upvalue 后直落：不产语句
                        }
                        else if (_loopExits.Count > 0 && target == _loopExits[^1])
                        {
                            Line("break");
                            pc++;
                        }
                        else if (IsSimpleReturn(target))
                        {
                            EmitReturnStatementAt(target);
                            pc++;
                        }
                        else
                        {
                            Bail($"jump at pc {pc} does not match the reconstructed structure");
                            return;
                        }

                        break;
                    }
                    case "RET0":
                        Line("return");
                        pc++;
                        break;
                    case "RET1":
                        Line($"return {ReadValue(At(pc).A)}");
                        pc++;
                        break;
                    case "CALLT":
                        Line($"return {RenderCallArgsAndFn(pc)}");
                        ResetFoldState();
                        pc++;
                        break;
                    case "CALLMT":
                        Line($"return {RenderCallArgsAndFn(pc)}");
                        ResetFoldState();
                        pc++;
                        break;
                    case "RETM":
                    {
                        var expr = TakeMultres(At(pc).A);
                        if (expr is null)
                        {
                            Bail($"RETM without MULTRES at pc {pc}");
                            return;
                        }

                        Line($"return {expr}");
                        pc++;
                        break;
                    }
                    case "RET":
                    {
                        var count = At(pc).D - 2;
                        if (count == 0)
                        {
                            var expr = TakeMultres(At(pc).A);
                            if (expr is null)
                            {
                                Bail($"RET MULTRES without MULTRES at pc {pc}");
                                return;
                            }

                            Line($"return {expr}");
                        }
                        else if (count == 1)
                        {
                            Line($"return {ReadValue(At(pc).A)}");
                        }
                        else if (count > 1)
                        {
                            var items = new List<string>();
                            for (var k = 0; k < count; k++)
                                items.Add(ReadValue(At(pc).A + k));
                            Line($"return {string.Join(", ", items)}");
                        }
                        else
                        {
                            Bail($"RET with negative count at pc {pc}");
                            return;
                        }

                        pc++;
                        break;
                    }
                    case "LOOP":
                        pc++; // 孤立 LOOP（无回边）只是 JIT 标记
                        break;
                    default:
                        pc += EmitSimple(pc);
                        break;
                }
            }

            MaterializePending(null);
        }

        private bool IsSimpleReturn(int pc) =>
            pc >= 2 && pc < _an.PcCount && Def(pc).Name is "RET0" or "RET1" or "RET";

        /// <summary>跳转目标本身是一条 return：渲染等价的 return 语句（寄存器直读，不折叠）。</summary>
        private void EmitReturnStatementAt(int pc)
        {
            switch (Def(pc).Name)
            {
                case "RET0":
                    Line("return");
                    break;
                case "RET1":
                    Line($"return {SlotName(At(pc).A)}");
                    break;
                case "RET":
                {
                    var count = At(pc).D - 2;
                    if (count == 1)
                        Line($"return {SlotName(At(pc).A)}");
                    else if (count > 1)
                    {
                        var items = new List<string>();
                        for (var k = 0; k < count; k++)
                            items.Add(SlotName(At(pc).A + k));
                        Line($"return {string.Join(", ", items)}");
                    }
                    else
                    {
                        Bail($"return jump to unsupported RET at pc {pc}");
                    }

                    break;
                }
            }
        }

        private void ResetFoldState() => MaterializePending(null);

        /// <summary>
        /// 物化未消费的折叠表达式为显式赋值（except 中的槽保留待结构头条件消费）。
        /// 折叠只是可读性优化；每条字节码指令都必须在还原文本中有对应语句，且语句顺序
        /// 必须与指令顺序一致——结构头渲染前物化，避免前置赋值被挤进循环/分支体内。
        /// </summary>
        private void MaterializePending(HashSet<int>? except)
        {
            foreach (var (slot, expr) in _pending.OrderBy(static kv => kv.Key))
            {
                if (except is not null && except.Contains(slot))
                    continue;
                Line($"{SlotName(slot)} = {expr}");
                _pending.Remove(slot);
            }

            _multresExpr = null;
            _multresBase = -1;
        }

        // ------------------------------------------------------------- control structures

        /// <summary>
        /// 发射 if/else。返回消费后的下一条 pc。两种形态（经真实样本与 ljd 交叉验证）：
        /// ① 跳转侧 = 合并点/出口（fall 为 then 体）：条件取发射指令的反转（ISLT→>= 等）；
        /// ② 值比较链（fall 尾部有无条件 JMP 指向更远合并点）：跳转侧 = then 体，条件不反转。
        /// </summary>
        private int EmitIf(int cmpPc, int target, int regionEnd)
        {
            var cond = RenderCondition(cmpPc);
            if (cond is null)
            {
                Bail($"unsupported condition at pc {cmpPc}");
                return target;
            }

            MaterializePending(KeepSlotsForCondition(cmpPc));

            // 形态②：fall 区以“跳向更远合并点”的无条件 JMP/UCLO 收尾。
            if (target - 1 >= cmpPc + 2 && Def(target - 1).Name is "JMP" or "UCLO")
            {
                var t2 = (target - 1) + 1 + (At(target - 1).D - 0x8000);
                if (t2 > target && t2 <= regionEnd)
                {
                    var opCond = RenderOpCondition(cmpPc);
                    if (opCond is null)
                    {
                        Bail($"unsupported condition at pc {cmpPc}");
                        return t2;
                    }

                    Line($"if {opCond} then");
                    _indent++;
                    ResetFoldState();
                    EmitRange(target, t2);
                    _indent--;
                    Line("else");
                    _indent++;
                    ResetFoldState();
                    EmitRange(cmpPc + 2, target - 1);
                    _indent--;
                    Line("end");
                    ResetFoldState();
                    return t2;
                }
            }

            // 形态①：then = [pc+2, min(target, regionEnd))。target 超出区域 = 真值路径直接
            // 离开当前区域（or 链的提前 return/continue），落路径才是 then 体。
            var thenEnd = Math.Min(target, regionEnd);

            var elseEnd = -1;
            if (thenEnd - 1 >= cmpPc + 2 && Def(thenEnd - 1).Name == "JMP")
            {
                var t3 = (thenEnd - 1) + 1 + (At(thenEnd - 1).D - 0x8000);
                if (t3 > thenEnd && t3 <= regionEnd)
                    elseEnd = t3;
            }

            Line($"if {cond} then");
            _indent++;
            ResetFoldState();
            EmitRange(cmpPc + 2, thenEnd);
            _indent--;
            if (elseEnd > 0)
            {
                Line("else");
                _indent++;
                ResetFoldState();
                EmitRange(target, elseEnd);
                _indent--;
                Line("end");
            }
            else
            {
                Line("end");
            }

            return target;
        }

        private void EmitNumericFor(int foriPc, int forlPc)
        {
            var a = At(foriPc).A;
            MaterializePending([a, a + 1, a + 2]);
            Line($"for {SlotName(a + 3)} = {ReadValue(a)}, {ReadValue(a + 1)}, {ReadValue(a + 2)} do");
            _indent++;
            ResetFoldState();
            _loopExits.Add(forlPc + 1);
            EmitRange(foriPc + 1, forlPc);
            _loopExits.RemoveAt(_loopExits.Count - 1);
            _indent--;
            Line("end");
            ResetFoldState();
        }

        private void EmitGenericFor(int entryPc, int iterlPc, int bodyStart, int iterCPc)
        {
            var a = At(iterCPc).A;
            MaterializePending([a - 3, a - 2, a - 1]);
            var iterExprs = $"{ReadValue(a - 3)}, {ReadValue(a - 2)}, {ReadValue(a - 1)}";
            Line($"for {SlotName(a)}, {SlotName(a + 1)}, {SlotName(a + 2)} in {iterExprs} do");
            _indent++;
            ResetFoldState();
            _loopExits.Add(iterlPc + 1);
            // 布局 A：体 [C+1, iterl)；布局 B：体 [t, C)。
            var bodyEnd = bodyStart == iterCPc + 1 ? iterlPc : iterCPc;
            EmitRange(bodyStart, bodyEnd);
            _loopExits.RemoveAt(_loopExits.Count - 1);
            _indent--;
            Line("end");
            ResetFoldState();
        }

        private void EmitWhile(int loopPc, int backEdgePc)
        {
            var cond = "true";
            var bodyStart = loopPc + 1;
            var condSlots = loopPc + 2 < backEdgePc && _an.IsConditional(loopPc + 1)
                ? new HashSet<int> { At(loopPc + 1).C }
                : new HashSet<int>();
            MaterializePending(condSlots);
            if (loopPc + 2 < backEdgePc && _an.IsConditional(loopPc + 1) && Def(loopPc + 2).Name == "JMP")
            {
                var exit = loopPc + 3 + (At(loopPc + 2).D - 0x8000);
                if (exit > loopPc + 3 && exit <= backEdgePc + 1)
                {
                    var rendered = RenderCondition(loopPc + 1);
                    if (rendered is null)
                    {
                        Bail($"unsupported while condition at pc {loopPc + 1}");
                        return;
                    }

                    cond = rendered;
                    bodyStart = exit;
                }
            }

            Line($"while {cond} do");
            _indent++;
            ResetFoldState();
            _loopExits.Add(backEdgePc + 1);
            EmitRange(bodyStart, backEdgePc);
            _loopExits.RemoveAt(_loopExits.Count - 1);
            _indent--;
            Line("end");
            ResetFoldState();
        }

        // ------------------------------------------------------------- conditions
        // LuaJIT 语义（经 ljd _COMPARISON_MAP 交叉验证）：比较/测试成立 → 执行其后紧跟的
        // JMP（跳到目标）；不成立 → 跳过 JMP 直落。因此 fall-through（then 区）条件是
        // 发射指令的“反转”：ISLT→>=、ISEQS→~=、IST→not、ISF→原式，等等。

        private string? RenderCondition(int pc)
        {
            var def = Def(pc);
            var ins = At(pc);
            switch (def.Name)
            {
                case "IST": return $"not ({ReadValue(ins.C)})";
                case "ISF": return ReadValue(ins.C);
                case "ISLT": return Cmp(ins.A, ">=", ins.C);
                case "ISGE": return Cmp(ins.A, "<", ins.C);
                case "ISLE": return Cmp(ins.A, ">", ins.C);
                case "ISGT": return Cmp(ins.A, "<=", ins.C);
                case "ISEQV": return Cmp(ins.A, "~=", ins.C);
                case "ISNEV": return Cmp(ins.A, "==", ins.C);
                case "ISEQS": return ResolveString(ins.D) is { } s ? $"{ReadValue(ins.A)} ~= {s}" : null;
                case "ISNES": return ResolveString(ins.D) is { } s2 ? $"{ReadValue(ins.A)} == {s2}" : null;
                case "ISEQN":
                case "ISNEN":
                {
                    var idx = ins.D;
                    if (idx < 0 || idx >= _proto.NumericConstants.Count)
                        return null;
                    var c = _proto.NumericConstants[idx];
                    var lit = c.IsDouble ? FormatNumber(c.DoubleValue) : c.IntValue.ToString(CultureInfo.InvariantCulture);
                    return $"{ReadValue(ins.A)} {(def.Name == "ISEQN" ? "~=" : "==")} {lit}";
                }
                case "ISEQP":
                case "ISNEP":
                {
                    var lit = ins.D switch { 0 => "nil", 1 => "false", 2 => "true", _ => null };
                    return lit is null ? null : $"{ReadValue(ins.A)} {(def.Name == "ISEQP" ? "~=" : "==")} {lit}";
                }
                default:
                    return null;
            }

            string Cmp(int a, string op, int d) => $"{ReadValue(a)} {op} {ReadValue(d)}";
        }

        /// <summary>if/while 条件引用的槽（这些槽的 pending 保留给条件渲染消费）。</summary>
        private HashSet<int> KeepSlotsForCondition(int pc)
        {
            var ins = At(pc);
            return Def(pc).Name is "IST" or "ISF"
                ? [ins.C]
                : [ins.A, ins.C];
        }

        /// <summary>发射指令自身的条件（未反转），供形态②的跳转侧 then 使用。</summary>
        private string? RenderOpCondition(int pc)
        {
            var def = Def(pc);
            var ins = At(pc);
            switch (def.Name)
            {
                case "IST": return ReadValue(ins.C);
                case "ISF": return $"not ({ReadValue(ins.C)})";
                case "ISLT": return CmpOp(ins.A, "<", ins.C);
                case "ISGE": return CmpOp(ins.A, ">=", ins.C);
                case "ISLE": return CmpOp(ins.A, "<=", ins.C);
                case "ISGT": return CmpOp(ins.A, ">", ins.C);
                case "ISEQV": return CmpOp(ins.A, "==", ins.C);
                case "ISNEV": return CmpOp(ins.A, "~=", ins.C);
                case "ISEQS": return ResolveString(ins.D) is { } s ? $"{ReadValue(ins.A)} == {s}" : null;
                case "ISNES": return ResolveString(ins.D) is { } s2 ? $"{ReadValue(ins.A)} ~= {s2}" : null;
                case "ISEQP":
                case "ISNEP":
                {
                    var lit = ins.D switch { 0 => "nil", 1 => "false", 2 => "true", _ => null };
                    return lit is null ? null : $"{ReadValue(ins.A)} {(def.Name == "ISEQP" ? "==" : "~=")} {lit}";
                }
                default:
                    return null;
            }

            string CmpOp(int a, string op, int d) => $"{ReadValue(a)} {op} {ReadValue(d)}";
        }

        // ------------------------------------------------------------- simple statements

        private int EmitSimple(int pc)
        {
            var def = Def(pc);
            var ins = At(pc);
            switch (def.Name)
            {
                case "MOV": Assign(ins.A, ReadValue(ins.C)); return 1;
                case "NOT": Assign(ins.A, $"not ({ReadValue(ins.C)})"); return 1;
                case "UNM": Assign(ins.A, $"-{Atom(ReadValue(ins.C))}"); return 1;
                case "LEN": Assign(ins.A, $"#{Atom(ReadValue(ins.C))}"); return 1;
                case "ADDVN": Assign(ins.A, Bin(ReadValue(ins.B), "+", NumLit(ins.C))); return 1;
                case "SUBVN": Assign(ins.A, Bin(ReadValue(ins.B), "-", NumLit(ins.C))); return 1;
                case "MULVN": Assign(ins.A, Bin(ReadValue(ins.B), "*", NumLit(ins.C))); return 1;
                case "DIVVN": Assign(ins.A, Bin(ReadValue(ins.B), "/", NumLit(ins.C))); return 1;
                case "MODVN": Assign(ins.A, Bin(ReadValue(ins.B), "%", NumLit(ins.C))); return 1;
                case "ADDNV": Assign(ins.A, Bin(NumLit(ins.C), "+", ReadValue(ins.B))); return 1;
                case "SUBNV": Assign(ins.A, Bin(NumLit(ins.C), "-", ReadValue(ins.B))); return 1;
                case "MULNV": Assign(ins.A, Bin(NumLit(ins.C), "*", ReadValue(ins.B))); return 1;
                case "DIVNV": Assign(ins.A, Bin(NumLit(ins.C), "/", ReadValue(ins.B))); return 1;
                case "MODNV": Assign(ins.A, Bin(NumLit(ins.C), "%", ReadValue(ins.B))); return 1;
                case "ADDVV": Assign(ins.A, Bin(ReadValue(ins.B), "+", ReadValue(ins.C))); return 1;
                case "SUBVV": Assign(ins.A, Bin(ReadValue(ins.B), "-", ReadValue(ins.C))); return 1;
                case "MULVV": Assign(ins.A, Bin(ReadValue(ins.B), "*", ReadValue(ins.C))); return 1;
                case "DIVVV": Assign(ins.A, Bin(ReadValue(ins.B), "/", ReadValue(ins.C))); return 1;
                case "MODVV": Assign(ins.A, Bin(ReadValue(ins.B), "%", ReadValue(ins.C))); return 1;
                case "POW": Assign(ins.A, Bin(ReadValue(ins.B), "^", ReadValue(ins.C))); return 1;
                case "CAT":
                {
                    var parts = new List<string>();
                    for (var s = ins.B; s <= ins.C; s++)
                        parts.Add(Atom(ReadValue(s)));
                    Assign(ins.A, string.Join(" .. ", parts));
                    return 1;
                }
                case "KSTR": Assign(ins.A, ResolveString(ins.D)); return 1;
                case "KSHORT": Assign(ins.A, ((short)ins.D).ToString(CultureInfo.InvariantCulture)); return 1;
                case "KNUM": Assign(ins.A, NumLit(ins.D)); return 1;
                case "KPRI":
                    Assign(ins.A, ins.D switch { 0 => "nil", 1 => "false", 2 => "true", _ => throw new DecompileBail($"KPRI type {ins.D}") });
                    return 1;
                case "KNIL":
                {
                    for (var s = ins.A; s <= ins.C; s++)
                        Assign(s, "nil");
                    return 1;
                }
                case "UGET": Assign(ins.A, UpvalueName(ins.D)); return 1;
                case "USETV": Line($"{UpvalueName(ins.A)} = {ReadValue(ins.C)}"); return 1;
                case "USETS": Line($"{UpvalueName(ins.A)} = {ResolveString(ins.D)}"); return 1;
                case "USETN": Line($"{UpvalueName(ins.A)} = {NumLit(ins.D)}"); return 1;
                case "USETP":
                    Line($"{UpvalueName(ins.A)} = {ins.D switch { 0 => "nil", 1 => "false", 2 => "true", _ => throw new DecompileBail($"USETP type {ins.D}") }}");
                    return 1;
                case "FNEW":
                {
                    if (ResolveChild(ins.D) is not { } child)
                    {
                        Bail($"FNEW without child proto at pc {pc}");
                        return 1;
                    }

                    // 子原型无法可靠还原时“跳过该部分”：用显式标注的占位闭包占位，
                    // 其完整逻辑以权威字节码清单呈现在报告中——绝不猜测性还原。
                    if (!EmitFunction(_ctx, child, out var body, out var reason))
                    {
                        Assign(ins.A, $"function(...) --[[原型 #{child.Id} 结构未能可靠还原（{reason}），完整逻辑见下方字节码清单]] end");
                        return 1;
                    }

                    Assign(ins.A, body);
                    return 1;
                }
                case "TNEW": Assign(ins.A, "{}"); return 1;
                case "TDUP":
                {
                    if (ResolveTable(ins.D) is not { } table)
                    {
                        Bail($"TDUP without table constant at pc {pc}");
                        return 1;
                    }

                    Assign(ins.A, LuaJitDumpDisassembler.RenderTableForSource(table));
                    return 1;
                }
                case "GGET": Assign(ins.A, RenderGlobalRead(ResolveString(ins.D))); return 1;
                case "GSET": Line($"{RenderGlobalRead(ResolveString(ins.D))} = {ReadValue(ins.A)}"); return 1;
                case "TGETV": Assign(ins.A, $"{Atom(ReadValue(ins.B))}[{ReadValue(ins.C)}]"); return 1;
                case "TGETS": Assign(ins.A, RenderFieldRead(ReadValue(ins.B), ResolveString(ins.C))); return 1;
                case "TGETB": Assign(ins.A, $"{Atom(ReadValue(ins.B))}[{ins.C}]"); return 1;
                case "TGETR": Assign(ins.A, $"{Atom(ReadValue(ins.B))}[{ReadValue(ins.C)}]"); return 1;
                case "TSETV": Line($"{Atom(ReadValue(ins.B))}[{ReadValue(ins.C)}] = {ReadValue(ins.A)}"); return 1;
                case "TSETS": Line($"{RenderFieldRead(ReadValue(ins.B), ResolveString(ins.C))} = {ReadValue(ins.A)}"); return 1;
                case "TSETB": Line($"{Atom(ReadValue(ins.B))}[{ins.C}] = {ReadValue(ins.A)}"); return 1;
                case "TSETR": Line($"{Atom(ReadValue(ins.B))}[{ReadValue(ins.C)}] = {ReadValue(ins.A)}"); return 1;
                case "TSETM":
                    Bail("TSETM without a preceding MULTRES call");
                    return 1;
                case "CALL": return EmitCall(pc);
                case "CALLM": return EmitCallM(pc);
                case "VARG":
                {
                    if (ins.B == 0)
                    {
                        _multresExpr = "...";
                        _multresBase = ins.A;
                        return 1;
                    }

                    if (ins.B == 2)
                    {
                        Assign(ins.A, "...");
                        return 1;
                    }

                    Bail("VARG with unsupported result count");
                    return 1;
                }
                case "ISTC":
                case "ISFC":
                    Bail($"{def.Name} couples a test with a copy");
                    return 1;
                default:
                    Bail($"instruction {def.Name} is not supported in source restoration");
                    return 1;
            }
        }

        private int EmitCall(int pc)
        {
            var ins = At(pc);
            switch (ins.B)
            {
                case 0:
                {
                    // MULTRES 结果：仅识别其后紧跟的 TSETM（{ f(...) } 表构造）。
                    if (pc + 1 < _an.PcCount && Def(pc + 1).Name == "TSETM")
                    {
                        var expr = RenderCallArgsAndFn(pc);
                        var tsetmA = At(pc + 1).A;
                        Line($"{SlotName(tsetmA - 1)} = {{ {expr} }}");
                        _pending.Remove(tsetmA - 1);
                        return 2;
                    }

                    _multresExpr = RenderCallArgsAndFn(pc);
                    _multresBase = ins.A;
                    return 1;
                }
                case 1:
                    Line(RenderCallArgsAndFn(pc));
                    return 1;
                case 2:
                {
                    // 有副作用的调用一律物化为显式赋值，绝不折叠进后续表达式。
                    Line($"{SlotName(ins.A)} = {RenderCallArgsAndFn(pc)}");
                    _pending.Remove(ins.A);
                    return 1;
                }
                default:
                {
                    var names = new List<string>();
                    for (var s = ins.A; s < ins.A + ins.B - 1; s++)
                        names.Add(SlotName(s));
                    Line($"{string.Join(", ", names)} = {RenderCallArgsAndFn(pc)}");
                    for (var s = ins.A; s < ins.A + ins.B - 1; s++)
                        _pending.Remove(s);
                    return 1;
                }
            }
        }

        private int EmitCallM(int pc)
        {
            var ins = At(pc);
            switch (ins.B)
            {
                case 0:
                    Bail("CALLM returning MULTRES");
                    return 1;
                case 1:
                    Line(RenderCallArgsAndFn(pc));
                    return 1;
                case 2:
                    Line($"{SlotName(ins.A)} = {RenderCallArgsAndFn(pc)}");
                    _pending.Remove(ins.A);
                    return 1;
                default:
                    Bail("CALLM with multiple named results");
                    return 1;
            }
        }

        /// <summary>
        /// 渲染 CALL/CALLM/CALLT/CALLMT 的调用表达式。参数计数遵循 LuaJIT 语义：
        /// CALL/CALLT 的 C/D = 参数数 + 1（0 表示 MULTRES）；CALLM/CALLMT 的 C/D = 固定参数数。
        /// </summary>
        private string RenderCallArgsAndFn(int pc)
        {
            var def = Def(pc);
            var ins = At(pc);
            var fn = ReadValue(ins.A);
            var args = new List<string>();
            if (def.Name is "CALL" or "CALLT")
            {
                var n = (def.Name == "CALL" ? ins.C : ins.D) - 1;
                if (n < 0)
                {
                    if (_multresExpr is null || _multresBase < ins.A + 1)
                        throw new DecompileBail($"call at pc {pc} uses MULTRES args that are not available");
                    for (var s = ins.A + 1; s < _multresBase; s++)
                        args.Add(ReadValue(s));
                    args.Add(_multresExpr);
                    _multresExpr = null;
                }
                else
                {
                    for (var s = ins.A + 1; s <= ins.A + n; s++)
                        args.Add(ReadValue(s));
                }
            }
            else
            {
                var n = def.Name == "CALLM" ? ins.C : ins.D;
                for (var s = ins.A + 1; s < ins.A + 1 + n; s++)
                    args.Add(ReadValue(s));
                if (_multresExpr is null)
                    throw new DecompileBail($"call at pc {pc} expects MULTRES args but none is pending");
                args.Add(_multresExpr);
                _multresExpr = null;
            }

            return $"{fn}({string.Join(", ", args)})";
        }

        // ------------------------------------------------------------- slot expression folding

        private void Assign(int slot, string expr) => _pending[slot] = expr;

        private string ReadValue(int slot)
        {
            if (_pending.TryGetValue(slot, out var expr))
            {
                _pending.Remove(slot);
                return expr;
            }

            return SlotName(slot);
        }

        private string? TakeMultres(int baseSlot)
        {
            if (_multresExpr is null || _multresBase != baseSlot)
                return null;
            var expr = _multresExpr;
            _multresExpr = null;
            _multresBase = -1;
            return expr;
        }

        // ------------------------------------------------------------- helpers

        private string UpvalueName(int uv) =>
            uv < _proto.UpvalueNames.Count && _proto.UpvalueNames[uv].Length > 0
                ? SanitizeName(_proto.UpvalueNames[uv])
                : $"uv{uv}";

        private string ResolveString(int kgcIndexFromEnd)
        {
            var idx = _proto.ComplexConstants.Count - kgcIndexFromEnd - 1;
            if (idx < 0 || idx >= _proto.ComplexConstants.Count)
                throw new DecompileBail($"string constant index {idx} out of range");
            var constant = _proto.ComplexConstants[idx];
            if (constant.Kind != LuaConstantKind.String)
                throw new DecompileBail($"constant {idx} is not a string");
            if (constant.RawBytes.Length >= 3 && constant.RawBytes[0] == 0x1B && constant.RawBytes[1] == 0x4C && constant.RawBytes[2] == 0x4A)
                return $"\"--[[内嵌 LuaJIT 字节码 {constant.RawBytes.Length} 字节，完整还原见下方清单]]\"";
            return LuaJitDumpDisassembler.QuoteString(constant.StringValue);
        }

        private LuaJitDumpDecoder.LuaDumpProto? ResolveChild(int kgcIndexFromEnd)
        {
            var idx = _proto.ComplexConstants.Count - kgcIndexFromEnd - 1;
            if (idx < 0 || idx >= _proto.ComplexConstants.Count)
                return null;
            return _proto.ComplexConstants[idx].Child;
        }

        private LuaJitDumpDecoder.LuaTableConstant? ResolveTable(int kgcIndexFromEnd)
        {
            var idx = _proto.ComplexConstants.Count - kgcIndexFromEnd - 1;
            if (idx < 0 || idx >= _proto.ComplexConstants.Count)
                return null;
            return _proto.ComplexConstants[idx].Table;
        }

        private string NumLit(int idx)
        {
            if (idx < 0 || idx >= _proto.NumericConstants.Count)
                throw new DecompileBail($"numeric constant index {idx} out of range");
            var c = _proto.NumericConstants[idx];
            return c.IsDouble ? FormatNumber(c.DoubleValue) : c.IntValue.ToString(CultureInfo.InvariantCulture);
        }

        private string Bin(string left, string op, string right) =>
            $"{Atom(left)} {op} {right}";

        /// <summary>GGET/GSET 的全局名渲染：标识符直接输出，否则退化为 _G["name"]。</summary>
        private static string RenderGlobalRead(string name)
        {
            if (name.Length >= 2 && name[0] == '"' && name[^1] == '"')
            {
                var inner = name[1..^1];
                if (inner.Length > 0 && (char.IsAsciiLetter(inner[0]) || inner[0] == '_') &&
                    inner.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                    return inner;
                return $"_G[{name}]";
            }

            return name;
        }

        private string RenderFieldRead(string tableExpr, string field)
        {
            if (tableExpr.StartsWith("function(", StringComparison.Ordinal))
                throw new DecompileBail("field access on an inline function literal");
            if (field.Length >= 2 && field[0] == '"' && field[^1] == '"')
            {
                var inner = field[1..^1];
                if (inner.Length > 0 && (char.IsAsciiLetter(inner[0]) || inner[0] == '_') &&
                    inner.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                    return $"{tableExpr}.{inner}";
                return $"{tableExpr}[{field}]";
            }

            return $"{tableExpr}[{field}]";
        }

        /// <summary>复合表达式作为操作数时加括号；原子（寄存器/字面量/字段链/调用链）不加。</summary>
        private static string Atom(string expr)
        {
            if (expr.Length == 0)
                return "(\"\")";
            var head = expr[0];
            var startsAtomic = char.IsAsciiLetterOrDigit(head) || head is '_' or '"' or '{';
            if (startsAtomic && !(expr.Contains(' ') && !expr.StartsWith('"') && !expr.EndsWith(')')))
                return expr;
            return $"({expr})";
        }

        private static string FormatNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return "(0/0)";
            return value.ToString("R", CultureInfo.InvariantCulture);
        }
    }
}
