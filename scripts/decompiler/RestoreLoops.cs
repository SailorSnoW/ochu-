// IL transform: loops and null-conditional tests the decompiler folds differently from the IL.
//
// 1. Exit at the start of the body.
//        while (a) { if (!b) break; ... }      IL: br C; B: b; brfalse EXIT; ...; C: a; brtrue B
//    is printed 'while (a && b) { ... }', which tests a first ('C: a; brfalse EXIT; b; brtrue B').
//    When b comes before a in the IL, it was in the body.
//
// 2. Element read once.
//        foreach (T x in list) { if (x.M()) break; }
//    keeps x on the stack: no local is left for the decompiler to find, and it prints
//    'while (e.MoveNext())'. The read of Current is given a variable again.
//
// 3. Null-conditional test.
//        if (e?.P ?? false)                    IL: e; dup; brtrue N; pop; ldc.i4.0; br T; N: call get_P; T: brfalse
//    is printed 'T v = e; if (v != null && v.P)', which stores e ('stloc v; ldloc v; brfalse').
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreLoops : IILTransform
{
    public void Run(ILFunction function, ILTransformContext context)
    {
        var functions = function.Descendants.OfType<ILFunction>().ToList();
        functions.Add(function);
        foreach (ILFunction f in functions)
        {
            if (f.Method == null) continue;
            if (Program.Enabled("RestoreLoopExit", f.Method.MetadataToken)) LoopExits(f);
            if (Program.Enabled("RestoreNullConditional", f.Method.MetadataToken))
            {
                RawIL raw = RawIL.Read(f, context);
                if (raw != null) { NullConditionalTests(f, raw); NullConditionalReturns(f, raw); NullConditionalInequalities(f, raw); }
            }
        }
    }

    private static void LoopExits(ILFunction f)
    {
        foreach (BlockContainer loop in RawIL.Own(f).OfType<BlockContainer>().ToList())
        {
            if (loop.Kind != ContainerKind.While && loop.Kind != ContainerKind.For) continue;
            Block header = loop.EntryPoint;
            if (header.Instructions.Count != 1) continue;
            var test = header.Instructions[0] as IfInstruction;
            if (test == null) continue;
            var toBody = test.TrueInst as Branch;
            var exit = test.FalseInst as Leave;
            if (toBody == null || exit == null || exit.TargetContainer != loop || !exit.Value.MatchNop()) continue;
            Block body = toBody.TargetBlock;
            if (body.Parent != loop || body == header) continue;

            ILInstruction first, second;
            while (test.Condition.MatchLogicAnd(out first, out second))
            {
                int a = RawIL.StartOf(first), b = RawIL.StartOf(second);
                if (a < 0 || b < 0 || b >= a) break;
                var both = (IfInstruction)test.Condition;
                both.Condition = new Nop();
                both.TrueInst = new Nop();
                test.Condition = first;
                ILInstruction positive;
                ILInstruction negated = second.MatchLogicNot(out positive) ? Take(positive) : Comp.LogicNot(second);
                body.Instructions.Insert(0, new IfInstruction(negated, new Leave(loop)));
            }
            HoistCurrent(f, test.Condition, body, loop);
        }
    }

    private static ILInstruction Take(ILInstruction inst)
    {
        inst.ReplaceWith(new Nop());
        return inst;
    }

    private static ILVariable Receiver(CallInstruction call)
    {
        if (call == null || call.Arguments.Count != 1) return null;
        var address = call.Arguments[0] as LdLoca;
        if (address != null) return address.Variable;
        var load = call.Arguments[0] as LdLoc;
        return load != null ? load.Variable : null;
    }

    private static void HoistCurrent(ILFunction f, ILInstruction condition, Block body, BlockContainer loop)
    {
        var moveNext = condition as CallInstruction;
        if (moveNext == null || moveNext.Method.Name != "MoveNext" || body.Instructions.Count == 0) return;
        ILVariable enumerator = Receiver(moveNext);
        if (enumerator == null) return;
        var reads = loop.Descendants.OfType<CallInstruction>().Where(c => c.Method.Name == "get_Current" && Receiver(c) == enumerator).ToList();
        if (reads.Count != 1) return;
        CallInstruction current = reads[0];
        // Already 'T x = e.Current;' or 'T x = (T)e.Current;'.
        ILInstruction value = current;
        while (value.Parent is CastClass || value.Parent is UnboxAny || value.Parent is Conv) value = value.Parent;
        if (value.Parent is StLoc && value.Parent.Parent == body) return;
        // It must be the first call of the first statement: nothing runs before it.
        ILInstruction statement = body.Instructions[0];
        if (statement.Descendants.OfType<CallInstruction>().FirstOrDefault() != current) return;
        ILVariable v = f.RegisterVariable(VariableKind.Local, current.Method.ReturnType);
        current.ReplaceWith(new LdLoc(v));
        body.Instructions.Insert(0, new StLoc(v, current));
    }

    // 'return e?.f == c;' is printed 'T v = e; if (v == null) return false; return v.f == c;'.
    // Same clue as for a test: 'dup; brtrue; pop; ldc.i4.0' after the value.
    private static void NullConditionalReturns(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            ILVariable s = store.Variable;
            if (s.LoadCount != 2 || s.StoreCount != 1 || s.AddressCount != 0) continue;
            if (s.Kind != VariableKind.StackSlot && s.Kind != VariableKind.Local) continue;
            var block = store.Parent as Block;
            if (block == null || block.Kind != BlockKind.ControlFlow || store.SlotInfo != Block.InstructionSlot) continue;
            int at = store.ChildIndex;
            if (at + 2 >= block.Instructions.Count) continue;
            var test = block.Instructions[at + 1] as IfInstruction;
            var last = block.Instructions[at + 2] as Leave;
            if (test == null || last == null || !test.FalseInst.MatchNop() || !last.IsLeavingFunction) continue;
            var isNull = test.Condition as Comp;
            if (isNull == null || isNull.Kind != ComparisonKind.Equality || !isNull.Right.MatchLdNull()) continue;
            var checkedLoad = isNull.Left as LdLoc;
            if (checkedLoad == null || checkedLoad.Variable != s) continue;
            var exit = test.TrueInst as Leave;
            var body = test.TrueInst as Block;
            if (exit == null && body != null && body.Instructions.Count == 1) exit = body.Instructions[0] as Leave;
            if (exit == null || !exit.IsLeavingFunction || !exit.Value.MatchLdcI4(0)) continue;
            var comparison = last.Value as Comp;
            if (comparison == null || comparison.LiftingKind != ComparisonLiftingKind.None || comparison.Kind == ComparisonKind.Inequality) continue;
            var uses = comparison.Descendants.OfType<LdLoc>().Where(l => l.Variable == s).ToList();
            if (uses.Count != 1 || uses[0] == comparison.Left || uses[0] == comparison.Right) continue;
            if (comparison.Descendants.FirstOrDefault() != uses[0]) continue;
            bool inLeft = uses[0].IsDescendantOf(comparison.Left);
            if (!inLeft && !uses[0].IsDescendantOf(comparison.Right)) continue;
            int end = RawIL.EndOf(store.Value);
            RawIL.Op dup, branch, pop, zero;
            if (end < 0) continue;
            if (!(raw.IsAt(end, out dup) && dup.Code == ILOpCode.Dup) && !(raw.EndsAt(end, out dup) && dup.Code == ILOpCode.Dup)) continue;
            if (!raw.Near(dup.Offset, 1, out branch) || (branch.Code != ILOpCode.Brtrue && branch.Code != ILOpCode.Brtrue_s)) continue;
            if (!raw.Near(dup.Offset, 2, out pop) || pop.Code != ILOpCode.Pop) continue;
            if (!raw.Near(dup.Offset, 3, out zero) || zero.Code != ILOpCode.Ldc_i4_0) continue;

            ILInstruction value = store.Value;
            store.Value = new Nop();
            uses[0].ReplaceWith(new NullableUnwrap(StackType.O, value));
            ILInstruction left = comparison.Left, right = comparison.Right;
            comparison.Left = new Nop();
            comparison.Right = new Nop();
            if (inLeft) left = new NullableRewrap(left); else right = new NullableRewrap(right);
            last.Value = new Comp(comparison.Kind, ComparisonLiftingKind.CSharp, comparison.InputType, comparison.Sign, left, right);
            block.Instructions.RemoveAt(at + 1);
            block.Instructions.RemoveAt(at);
        }
    }

    // 'e?.a != c' is printed 'T v = e; if (v == null || v.a != c)': a comparison with a
    // nullable is true for '!=' when e is null. Clue: 'dup; brtrue; pop; ldc.i4.1' after e.
    private static void NullConditionalInequalities(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            ILVariable s = store.Variable;
            if (s.Kind != VariableKind.StackSlot || s.LoadCount != 2 || s.StoreCount != 1 || s.AddressCount != 0) continue;
            var block = store.Parent as Block;
            if (block == null || block.Kind != BlockKind.ControlFlow || store.SlotInfo != Block.InstructionSlot) continue;
            if (store.ChildIndex + 1 >= block.Instructions.Count) continue;
            ILInstruction next = block.Instructions[store.ChildIndex + 1];
            IfInstruction test = null;
            int tests = 0;
            foreach (IfInstruction i in next.Descendants.OfType<IfInstruction>())
            {
                var c = i.Condition as Comp;
                if (c == null || c.Kind != ComparisonKind.Equality || !i.TrueInst.MatchLdcI4(1)) continue;
                var checkedLoad = c.Left as LdLoc;
                if (checkedLoad == null || checkedLoad.Variable != s || !c.Right.MatchLdNull()) continue;
                test = i;
                tests++;
            }
            if (tests != 1) continue;
            if (next.Descendants.FirstOrDefault() != ((Comp)test.Condition).Left) continue;
            var comparison = test.FalseInst as Comp;
            if (comparison == null) continue;
            // '!v.M()' is 'v.M() != true'.
            ILInstruction negated;
            if (comparison.Kind == ComparisonKind.Equality && comparison.MatchLogicNot(out negated) && !(negated is LdLoc) && negated.ResultType == StackType.I4
                && comparison.LiftingKind == ComparisonLiftingKind.None && negated.Descendants.OfType<LdLoc>().Count(l => l.Variable == s) == 1)
            {
                comparison.Left = new Nop();
                comparison = new Comp(ComparisonKind.Inequality, Sign.None, negated, new LdcI4(1));
                test.FalseInst = comparison;
            }
            if (comparison.Kind != ComparisonKind.Inequality) continue;
            var uses = comparison.Descendants.OfType<LdLoc>().Where(l => l.Variable == s).ToList();
            if (uses.Count != 1 || uses[0] == comparison.Left || uses[0] == comparison.Right) continue;
            if (comparison.Descendants.FirstOrDefault(d => d is LdLoc || d is CallInstruction || d is LdObj) != uses[0]
                && comparison.Descendants.OfType<LdLoc>().FirstOrDefault() != uses[0]) continue;
            bool inLeft = uses[0].IsDescendantOf(comparison.Left);
            if (!inLeft && !uses[0].IsDescendantOf(comparison.Right)) continue;
            ILInstruction side = inLeft ? comparison.Left : comparison.Right;
            bool lifted = comparison.LiftingKind == ComparisonLiftingKind.CSharp && side is NullableRewrap;
            if (!lifted && comparison.LiftingKind != ComparisonLiftingKind.None) continue;
            int end = RawIL.EndOf(store.Value);
            RawIL.Op dup, branch, pop, one;
            if (end < 0) continue;
            if (!(raw.IsAt(end, out dup) && dup.Code == ILOpCode.Dup) && !(raw.EndsAt(end, out dup) && dup.Code == ILOpCode.Dup)) continue;
            if (!raw.Near(dup.Offset, 1, out branch) || (branch.Code != ILOpCode.Brtrue && branch.Code != ILOpCode.Brtrue_s)) continue;
            if (!raw.Near(dup.Offset, 2, out pop) || pop.Code != ILOpCode.Pop) continue;
            if (!raw.Near(dup.Offset, 3, out one) || one.Code != ILOpCode.Ldc_i4_1) continue;

            ILInstruction value = store.Value;
            store.Value = new Nop();
            uses[0].ReplaceWith(new NullableUnwrap(StackType.O, value));
            ILInstruction whole;
            if (lifted)
            {
                whole = comparison;
                test.FalseInst = new Nop();
            }
            else
            {
                ILInstruction left = comparison.Left, right = comparison.Right;
                comparison.Left = new Nop();
                comparison.Right = new Nop();
                if (inLeft) left = new NullableRewrap(left); else right = new NullableRewrap(right);
                whole = new Comp(comparison.Kind, ComparisonLiftingKind.CSharp, comparison.InputType, comparison.Sign, left, right);
            }
            test.ReplaceWith(whole);
            block.Instructions.RemoveAt(store.ChildIndex);
        }
    }

    private static void NullConditionalTests(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            ILVariable s = store.Variable;
            if (s.Kind != VariableKind.StackSlot || s.LoadCount != 2 || s.StoreCount != 1 || s.AddressCount != 0) continue;
            var block = store.Parent as Block;
            if (block == null || block.Kind != BlockKind.ControlFlow || store.SlotInfo != Block.InstructionSlot) continue;
            if (store.ChildIndex + 1 >= block.Instructions.Count) continue;
            ILInstruction next = block.Instructions[store.ChildIndex + 1];
            IfInstruction test = null;
            int tests = 0;
            foreach (IfInstruction i in next.Descendants.OfType<IfInstruction>())
            {
                var c = i.Condition as Comp;
                if (c == null || c.Kind != ComparisonKind.Inequality || !i.FalseInst.MatchLdcI4(0)) continue;
                var checkedLoad = c.Left as LdLoc;
                if (checkedLoad == null || checkedLoad.Variable != s || !c.Right.MatchLdNull()) continue;
                test = i;
                tests++;
            }
            if (tests != 1) continue;
            // The check is the first thing the statement evaluates, or only the object of a
            // field store comes before it ('x.f = e?.g == c': 'ldloc x' has no effect).
            if (next.Descendants.FirstOrDefault() != ((Comp)test.Condition).Left)
            {
                var assign = next as StObj;
                var field = assign == null ? null : assign.Target as LdFlda;
                if (field == null || !(field.Target is LdLoc) || ((LdLoc)field.Target).Variable == s || assign.Value != test) continue;
            }
            var uses = test.TrueInst.Descendants.OfType<LdLoc>().Where(l => l.Variable == s).ToList();
            if (test.TrueInst is LdLoc || uses.Count != 1) continue;
            if (test.TrueInst.ResultType != StackType.I4) continue;
            // 'dup; brtrue; pop; ldc.i4.0' after the value.
            int end = RawIL.EndOf(store.Value);
            RawIL.Op dup, branch, pop, zero;
            if (end < 0) continue;
            if (!(raw.IsAt(end, out dup) && dup.Code == ILOpCode.Dup) && !(raw.EndsAt(end, out dup) && dup.Code == ILOpCode.Dup)) continue;
            if (!raw.Near(dup.Offset, 1, out branch) || (branch.Code != ILOpCode.Brtrue && branch.Code != ILOpCode.Brtrue_s)) continue;
            if (!raw.Near(dup.Offset, 2, out pop) || pop.Code != ILOpCode.Pop) continue;
            if (!raw.Near(dup.Offset, 3, out zero) || zero.Code != ILOpCode.Ldc_i4_0) continue;

            ILInstruction value = store.Value, access = test.TrueInst;
            // 'e?.f > 0' is a comparison with a nullable: false when e is null, except '!='.
            // In 'e?.f == 1 && g', the test of e belongs to the first operand only.
            while (access is IfInstruction && ((IfInstruction)access).FalseInst.MatchLdcI4(0) && uses[0].IsDescendantOf(((IfInstruction)access).Condition))
                access = ((IfInstruction)access).Condition;
            var comparison = access as Comp;
            bool inLeft = false;
            if (comparison != null)
            {
                if (comparison.LiftingKind != ComparisonLiftingKind.None || comparison.Kind == ComparisonKind.Inequality) continue;
                inLeft = uses[0] == comparison.Left || uses[0].IsDescendantOf(comparison.Left);
                if (!inLeft && !(uses[0] == comparison.Right || uses[0].IsDescendantOf(comparison.Right))) continue;
            }
            else if (access.ResultType != StackType.I4 || access is IfInstruction) continue;
            store.Value = new Nop();
            uses[0].ReplaceWith(new NullableUnwrap(StackType.O, value));
            var place = new Nop();
            access.ReplaceWith(place);
            if (comparison != null)
            {
                ILInstruction left = comparison.Left, right = comparison.Right;
                comparison.Left = new Nop();
                comparison.Right = new Nop();
                if (inLeft) left = new NullableRewrap(left); else right = new NullableRewrap(right);
                place.ReplaceWith(new Comp(comparison.Kind, ComparisonLiftingKind.CSharp, comparison.InputType, comparison.Sign, left, right));
            }
            else
            {
                var coalesce = new NullCoalescingInstruction(NullCoalescingKind.NullableWithValueFallback, new NullableRewrap(access), new LdcI4(0));
                coalesce.UnderlyingResultType = StackType.I4;
                place.ReplaceWith(coalesce);
            }
            // The outer 'v != null &&' is now inside.
            ILInstruction whole = test.TrueInst;
            test.TrueInst = new Nop();
            test.ReplaceWith(whole);
            block.Instructions.RemoveAt(store.ChildIndex);
        }
    }
}
