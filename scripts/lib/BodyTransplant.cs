// Gives the renamed module the method bodies of the pre-rename stage back.
//
// de4dot does not only rename: it rewrites about 2700 method bodies on the way (folded
// constants, 'neg' turned into '0 - x', nop changes). A decompilation of those bodies cannot
// recompile to the original IL. Both stages have the same metadata tokens
// (--preserve-tokens), so every body can be rebuilt from the faithful stage with its
// operands re-pointed, token by token, at the objects of the renamed module.
// Compiled by scripts/apply-names.ps1 with Add-Type (Windows PowerShell 5.1: C# 5 only).
using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

public sealed class BodyTransplant
{
    readonly ModuleDefMD target, source;
    public int Methods, Rewritten, Unresolved, TokenMismatches, Compared, Duplicated;
    public readonly List<string> Problems = new List<string>();

    public BodyTransplant(ModuleDefMD target, ModuleDefMD source) { this.target = target; this.source = source; }

    public void Run()
    {
        foreach (var st in source.GetTypes())
            foreach (var sm in st.Methods)
            {
                if (!sm.HasBody) continue;
                var tm = target.ResolveToken(sm.MDToken.Raw) as MethodDef;
                if (tm == null || !tm.HasBody) { Problems.Add("no body in target for 0x" + sm.MDToken.Raw.ToString("X8")); continue; }
                Methods++;
                Check(sm, tm);
                Transplant(sm, tm);
            }
    }

    // Where de4dot left a body alone, both stages must name the same tokens: this is
    // the evidence that operands can be re-pointed by token.
    void Check(MethodDef sm, MethodDef tm)
    {
        var a = sm.Body.Instructions; var b = tm.Body.Instructions;
        bool same = a.Count == b.Count;
        for (int i = 0; same && i < a.Count; i++) if (a[i].OpCode != b[i].OpCode) same = false;
        if (!same) { Rewritten++; return; }
        var ta = new List<uint>(); var tb = new List<uint>();
        bool ordered = true;
        for (int i = 0; i < a.Count; i++)
        {
            var x = a[i].Operand as IMDTokenProvider; var y = b[i].Operand as IMDTokenProvider;
            if (x == null || y == null) continue;
            Compared++;
            if (x.MDToken.Raw == y.MDToken.Raw) continue;
            // de4dot adds MemberRef / MethodSpec rows for references that depend on a generic
            // context and leaves the old row in place: the old token is still the right one.
            uint table = x.MDToken.Raw >> 24;
            if (table == 0x0A || table == 0x2B || table == 0x1B) { Duplicated++; continue; }
            ordered = false;
            ta.Add(x.MDToken.Raw); tb.Add(y.MDToken.Raw);
        }
        if (ordered) return;
        // Same opcodes, same tokens, another order: de4dot moved blocks around.
        ta.Sort(); tb.Sort();
        bool permuted = true;
        for (int i = 0; i < ta.Count; i++) if (ta[i] != tb[i]) permuted = false;
        if (permuted) { Rewritten++; return; }
        TokenMismatches++;
        if (Problems.Count < 12) Problems.Add("tokens differ in 0x" + sm.MDToken.Raw.ToString("X8"));
    }

    object Map(object operand, GenericParamContext gpc, MethodDef sm)
    {
        var tp = operand as IMDTokenProvider;
        if (tp == null) return operand;            // constants, strings
        if (operand is MethodSig) return MapSig((MethodSig)operand, gpc);   // calli
        uint tok = tp.MDToken.Raw;
        object r = tok == 0 ? null : target.ResolveToken(tok, gpc);
        if (r == null || r.GetType() != operand.GetType())
        {
            Unresolved++;
            if (Problems.Count < 50) Problems.Add("operand 0x" + tok.ToString("X8") + " (" + operand.GetType().Name + ") in 0x" + sm.MDToken.Raw.ToString("X8"));
            return null;
        }
        return r;
    }

    ITypeDefOrRef MapType(ITypeDefOrRef t, GenericParamContext gpc)
    {
        if (t == null) return null;
        return target.ResolveToken(t.MDToken.Raw, gpc) as ITypeDefOrRef;
    }

    MethodSig MapSig(MethodSig s, GenericParamContext gpc)
    {
        var r = new MethodSig(s.CallingConvention, s.GenParamCount, MapSig(s.RetType, gpc));
        foreach (var p in s.Params) r.Params.Add(MapSig(p, gpc));
        if (s.ParamsAfterSentinel != null)
        {
            r.ParamsAfterSentinel = new List<TypeSig>();
            foreach (var p in s.ParamsAfterSentinel) r.ParamsAfterSentinel.Add(MapSig(p, gpc));
        }
        return r;
    }

    TypeSig MapSig(TypeSig s, GenericParamContext gpc)
    {
        if (s == null) return null;
        switch (s.ElementType)
        {
            case ElementType.Class: return new ClassSig(MapType(((ClassSig)s).TypeDefOrRef, gpc));
            case ElementType.ValueType: return new ValueTypeSig(MapType(((ValueTypeSig)s).TypeDefOrRef, gpc));
            case ElementType.GenericInst:
                {
                    var gi = (GenericInstSig)s;
                    var r = new GenericInstSig((ClassOrValueTypeSig)MapSig(gi.GenericType, gpc));
                    foreach (var a in gi.GenericArguments) r.GenericArguments.Add(MapSig(a, gpc));
                    return r;
                }
            case ElementType.SZArray: return new SZArraySig(MapSig(s.Next, gpc));
            case ElementType.Array:
                {
                    var a = (ArraySig)s;
                    return new ArraySig(MapSig(s.Next, gpc), a.Rank, new List<uint>(a.Sizes), new List<int>(a.LowerBounds));
                }
            case ElementType.ByRef: return new ByRefSig(MapSig(s.Next, gpc));
            case ElementType.Ptr: return new PtrSig(MapSig(s.Next, gpc));
            case ElementType.Pinned: return new PinnedSig(MapSig(s.Next, gpc));
            case ElementType.CModReqd: return new CModReqdSig(MapType(((ModifierSig)s).Modifier, gpc), MapSig(s.Next, gpc));
            case ElementType.CModOpt: return new CModOptSig(MapType(((ModifierSig)s).Modifier, gpc), MapSig(s.Next, gpc));
            case ElementType.Var: return new GenericVar(((GenericSig)s).Number, gpc.Type);
            case ElementType.MVar: return new GenericMVar(((GenericSig)s).Number, gpc.Method);
            case ElementType.FnPtr: return new FnPtrSig(MapSig(((FnPtrSig)s).MethodSig, gpc));
            case ElementType.Sentinel: return new SentinelSig();
            default:
                {
                    var c = s as CorLibTypeSig;
                    if (c != null)
                    {
                        var r = target.CorLibTypes.GetCorLibTypeSig(c.TypeDefOrRef);
                        if (r != null) return r;
                    }
                    throw new NotSupportedException("signature element " + s.ElementType);
                }
        }
    }

    void Transplant(MethodDef sm, MethodDef tm)
    {
        var gpc = new GenericParamContext(tm.DeclaringType, tm);
        var sb = sm.Body;
        var body = new CilBody(sb.InitLocals, new List<Instruction>(), new List<ExceptionHandler>(), new List<Local>());
        body.MaxStack = sb.MaxStack;
        body.KeepOldMaxStack = true;
        body.LocalVarSigTok = 0;
        var locals = new Dictionary<Local, Local>();
        foreach (var l in sb.Variables)
        {
            var n = new Local(MapSig(l.Type, gpc));
            body.Variables.Add(n);
            locals[l] = n;
        }
        var map = new Dictionary<Instruction, Instruction>();
        foreach (var i in sb.Instructions)
        {
            var n = new Instruction(i.OpCode, null);
            n.Offset = i.Offset;
            map[i] = n;
            body.Instructions.Add(n);
        }
        int unresolvedBefore = Unresolved;
        for (int k = 0; k < sb.Instructions.Count; k++)
        {
            object op = sb.Instructions[k].Operand;
            object mapped;
            var target1 = op as Instruction;
            var targets = op as IList<Instruction>;
            var local = op as Local;
            var par = op as Parameter;
            if (op == null) mapped = null;
            else if (target1 != null) mapped = map[target1];
            else if (targets != null)
            {
                var arr = new Instruction[targets.Count];
                for (int j = 0; j < arr.Length; j++) arr[j] = targets[j] == null ? null : map[targets[j]];
                mapped = arr;
            }
            else if (local != null) mapped = locals[local];
            else if (par != null) mapped = tm.Parameters[par.Index];
            else mapped = Map(op, gpc, sm);
            body.Instructions[k].Operand = mapped;
        }
        foreach (var eh in sb.ExceptionHandlers)
        {
            var n = new ExceptionHandler(eh.HandlerType);
            n.TryStart = eh.TryStart == null ? null : map[eh.TryStart];
            n.TryEnd = eh.TryEnd == null ? null : map[eh.TryEnd];
            n.HandlerStart = eh.HandlerStart == null ? null : map[eh.HandlerStart];
            n.HandlerEnd = eh.HandlerEnd == null ? null : map[eh.HandlerEnd];
            n.FilterStart = eh.FilterStart == null ? null : map[eh.FilterStart];
            n.CatchType = MapType(eh.CatchType, gpc);
            body.ExceptionHandlers.Add(n);
        }
        // A body with an operand that could not be re-pointed stays as de4dot left it.
        if (Unresolved == unresolvedBefore) tm.Body = body;
    }
}
