// IL transform: orientation of 'c ? a : b', read from the raw IL.
//
// The compiler emits a conditional expression as
//     <c>; brtrue CONSEQUENCE; <b>; br END; CONSEQUENCE: <a>; END:
// with the alternative first. The decompiler reads the branch as 'if (!c) b else a' and
// prints '!c ? b : a', which compiles to the two values in the other order. When the value
// of the true branch comes first in the IL, the source had the opposite condition: negate
// it and swap the branches.
//
// 'x && y' is the one conditional with the true branch first ('x; brfalse F; y; br END;
// F: ldc.i4.0'): left alone.
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreConditional : IILTransform
{
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ILInstruction, object> Explicit = new System.Runtime.CompilerServices.ConditionalWeakTable<ILInstruction, object>();
    private static readonly object Marker = new object();

    public void Run(ILFunction function, ILTransformContext context)
    {
        var functions = function.Descendants.OfType<ILFunction>().ToList();
        functions.Add(function);
        foreach (ILFunction f in functions)
        {
            if (f.Method == null || !Program.Enabled("RestoreConditional", f.Method.MetadataToken)) continue;
            BranchedConstants(f, RawIL.Read(f, context));
            foreach (IfInstruction i in Own(f).OfType<IfInstruction>().ToList())
            {
                if (i.ResultType == StackType.Void) continue;
                int t = First(i.TrueInst), e = First(i.FalseInst);
                // 'c ? y : false' has the constant first; 'c && y' has it last. Both are the same
                // tree, printed '&&': the first is marked for ExplicitConditionals.
                // Two constants ('c ? 0 : 1' on an enum or an integer) are no '&&'.
                bool constants = i.TrueInst is LdcI4 && i.FalseInst is LdcI4;
                if (!constants && (i.FalseInst.MatchLdcI4(0) || i.FalseInst.MatchLdcI4(1)) && IsBool(i.TrueInst))
                {
                    if (t >= 0 && e >= 0 && e < t) Explicit.GetValue(i, _ => Marker);
                    if (i.FalseInst.MatchLdcI4(0) || e < t) continue;
                }
                if (t < 0 || e < 0 || t >= e) continue;
                ILInstruction condition = i.Condition, positive;
                ILInstruction a = i.TrueInst, b = i.FalseInst;
                i.TrueInst = new Nop();
                i.FalseInst = new Nop();
                i.Condition = condition.MatchLogicNot(out positive) ? positive : Comp.LogicNot(condition);
                i.TrueInst = b;
                i.FalseInst = a;
            }
        }
    }

    // 'c ? 0 : 1' on an integer or an enum is 'c; brtrue L; ldc.i4.1; br E; L: ldc.i4.0'. The
    // decompiler reads it as the comparison '!c' and prints '!c ? 1 : 0', which puts the
    // constants in the other order.
    private static void BranchedConstants(ILFunction f, RawIL raw)
    {
        if (raw == null) return;
        foreach (Comp comp in Own(f).OfType<Comp>().ToList())
        {
            if (comp.Parent == null || comp.Parent is IfInstruction || comp.Parent is Comp) continue;
            if (comp.Kind != ComparisonKind.Equality || !comp.Right.MatchLdcI4(0) || !IsBool(comp.Left) || comp.Left is LdcI4) continue;
            int end = RawIL.EndOf(comp.Left);
            RawIL.Op branch, one, jump, zero;
            if (end < 0 || !raw.IsAt(end, out branch)) continue;
            if (branch.Code != System.Reflection.Metadata.ILOpCode.Brtrue && branch.Code != System.Reflection.Metadata.ILOpCode.Brtrue_s) continue;
            if (!raw.Near(end, 1, out one) || one.Code != System.Reflection.Metadata.ILOpCode.Ldc_i4_1) continue;
            if (!raw.Near(end, 2, out jump) || (jump.Code != System.Reflection.Metadata.ILOpCode.Br && jump.Code != System.Reflection.Metadata.ILOpCode.Br_s)) continue;
            if (!raw.Near(end, 3, out zero) || zero.Code != System.Reflection.Metadata.ILOpCode.Ldc_i4_0 || branch.Operand != zero.Offset || jump.Operand != zero.End) continue;
            ILInstruction condition = comp.Left;
            comp.Left = new Nop();
            comp.ReplaceWith(new IfInstruction(condition, new LdcI4(0), new LdcI4(1)));
        }
    }

    private static IEnumerable<ILInstruction> Own(ILFunction f)
    {
        var stack = new Stack<ILInstruction>();
        stack.Push(f.Body);
        while (stack.Count > 0)
        {
            ILInstruction i = stack.Pop();
            yield return i;
            foreach (ILInstruction c in i.Children) if (!(c is ILFunction)) stack.Push(c);
        }
    }

    private static bool IsBool(ILInstruction inst)
    {
        if (inst is Comp) return true;
        var call = inst as CallInstruction;
        if (call != null) return !(inst is NewObj) && call.Method.ReturnType.IsKnownType(KnownTypeCode.Boolean);
        var load = inst as LdLoc;
        if (load != null) return load.Variable.Type.IsKnownType(KnownTypeCode.Boolean);
        var obj = inst as LdObj;
        if (obj != null) return obj.Type.IsKnownType(KnownTypeCode.Boolean);
        var nested = inst as IfInstruction;
        if (nested != null) return IsBool(nested.TrueInst) || IsBool(nested.FalseInst);
        int value;
        return inst.MatchLdcI4(out value) && (value == 0 || value == 1);
    }

    // Lowest IL offset of an expression, -1 when none of its instructions has a range.
    private static int First(ILInstruction inst)
    {
        int min = -1;
        var stack = new Stack<ILInstruction>();
        stack.Push(inst);
        while (stack.Count > 0)
        {
            ILInstruction i = stack.Pop();
            if (i is ILFunction) continue;
            foreach (var r in i.ILRanges)
                if (r.Start != r.End && (min < 0 || r.Start < min)) min = r.Start;
            foreach (ILInstruction c in i.Children) stack.Push(c);
        }
        return min;
    }
}
