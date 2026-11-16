// IL transform: shapes of statements the decompiler prints in a form that compiles to other
// instructions. Each is decided by the raw method body. Runs after the other transforms of
// this project.
//
// 1. Assignment in both branches, one of them inside another assignment.
//        if (c) x = a; else d[k] = (x = b);    IL: c; brfalse E; a; stloc x; br END; E: d; k; b; dup; stloc x; call set_Item
//    is printed 'x = c ? a : (d[k] = b);', which stores x once, after the two values meet.
//
// 2. Two jumps.
//        if (c) goto A;  goto B;               IL: c; <branch when false> B; br A
//    The conditional branch of the IL goes where the second statement goes, and the 'br'
//    where the body of the 'if' goes. The decompiler prints either orientation.
//
// 3. Exit test of a loop.
//        for (; a && b; ) { ... }              IL: br C; B: ...; C: a; brfalse EXIT; b; brtrue B
//    is printed 'for (; a; ) { if (!b) break; ... }' when b follows a at the bottom of the loop.
//
// 4. Value stored, then copied.
//        x = e;  y = x;                        IL: e; stloc x; <address of y>; ldloc x; stind
//    is printed 'x = (y = e);', which is 'e; dup; stloc t' with the address of y loaded first.
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreShapes : IILTransform
{
    public void Run(ILFunction function, ILTransformContext context)
    {
        var functions = function.Descendants.OfType<ILFunction>().ToList();
        functions.Add(function);
        foreach (ILFunction f in functions)
        {
            if (f.Method == null) continue;
            RawIL raw = RawIL.Read(f, context);
            if (raw == null) continue;
            if (On(f))
            {
                IfElseInlineAssign(f, raw);
                JumpOrientation(f, raw);
                LoopConditions(f, raw);
                TopTestedLoops(f, raw);
                StoreThenCopy(f, raw);
                TrueFirstConditionals(f, raw);
                PatternVariables(f, raw);
                ValueReceivers(f, raw);
                ArrayInitializerTails(f, raw);
                StructInitializers(f, raw);
                ReturnInElse(f, raw);
                NullableTemps(f, raw);
                ShortCircuitPrefixes(f, raw, context);
                ConditionalsByBranch(f, raw);
                NullFallbacks(f, raw);
                InlineStores(f, raw);
                GuardsBeforeReturn(f, raw);
                IfElseOrder(f, raw);
                InlineFieldStores(f, raw);
                ShortCircuitConditions(f, raw, context);
                StoredConditions(f, raw);
                IfElseNestedAssign(f, raw);
                IfElseAssignByJump(f, raw);
                NestFirstOperand(f, raw);
                ElseOfFirstOperand(f, raw);
                ReturnToElse(f, raw);
                SharedPatternSlots(f, raw);
                NullConditionalLoops(f, raw);
                StackedFlags(f, raw, context);
                SlotVariables(f, raw);
                BlocksInOrder(f);
            }
        }
    }

    // On for every method; '-RestoreShapes' in the hints switches it off for one.
    private static bool On(ILFunction f)
    {
        HashSet<string> set;
        if (f.Method.MetadataToken.IsNil || !Program.Hints.TryGetValue(System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(f.Method.MetadataToken), out set)) return true;
        return !set.Contains("-RestoreShapes");
    }

    private static bool IsStatement(ILInstruction inst)
    {
        var block = inst.Parent as Block;
        return block != null && block.Kind == BlockKind.ControlFlow && inst.SlotInfo == Block.InstructionSlot;
    }

    private static bool AtOrBefore(RawIL raw, int offset, System.Func<ILOpCode, bool> test, out RawIL.Op op)
    {
        if (raw.IsAt(offset, out op) && test(op.Code)) return true;
        if (raw.EndsAt(offset, out op) && test(op.Code)) return true;
        return false;
    }

    private static bool IsBr(ILOpCode c)
    {
        return c == ILOpCode.Br || c == ILOpCode.Br_s;
    }

    private static ILInstruction Negate(ILInstruction condition)
    {
        ILInstruction positive;
        if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); return positive; }
        if (condition.Parent != null) condition.ReplaceWith(new Nop());
        return Comp.LogicNot(condition);
    }

    private static ILInstruction Take(ILInstruction inst)
    {
        if (inst.Parent != null) inst.ReplaceWith(new Nop());
        return inst;
    }

    // ---- 1 ----
    private static void IfElseInlineAssign(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            var choice = store.Value as IfInstruction;
            if (choice == null || !IsStatement(store) || store.Variable.Kind == VariableKind.StackSlot) continue;
            var tb = choice.TrueInst as Block;
            var fb = choice.FalseInst as Block;
            bool inlineTrue = tb != null && tb.Kind == BlockKind.CallInlineAssign, inlineFalse = fb != null && fb.Kind == BlockKind.CallInlineAssign;
            if (inlineTrue == inlineFalse) continue;
            Block inline = inlineTrue ? tb : fb;
            ILInstruction plain = inlineTrue ? choice.FalseInst : choice.TrueInst;
            if (inline.Instructions.Count != 1) continue;
            var final = inline.FinalInstruction as LdLoc;
            if (final == null) continue;
            var inners = inline.Instructions[0].Descendants.OfType<StLoc>().Where(s => s.Variable == final.Variable).ToList();
            if (inners.Count != 1 || final.Variable.LoadCount != 1 || final.Variable.StoreCount != 1) continue;
            StLoc inner = inners[0];
            RawIL.Op dup, kept, s1, jump;
            int valueEnd = RawIL.EndOf(inner.Value);
            if (valueEnd < 0 || !AtOrBefore(raw, valueEnd, c => c == ILOpCode.Dup, out dup)) continue;
            if (!raw.Near(dup.Offset, 1, out kept) || !RawIL.IsStLoc(kept.Code)) continue;
            if (!AtOrBefore(raw, RawIL.EndOf(plain), RawIL.IsStLoc, out s1) || s1.Operand != kept.Operand || s1.Offset == kept.Offset) continue;
            // The plain branch comes first and ends with a jump over the other one.
            if (s1.Offset > kept.Offset) continue;
            if (!raw.Near(s1.Offset, 1, out jump) || !IsBr(jump.Code) || jump.Operand <= kept.Offset) continue;

            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            ILVariable v = store.Variable;
            ILInstruction condition = choice.Condition;
            store.Value = new Nop();
            choice.Condition = new Nop();
            choice.TrueInst = new Nop();
            choice.FalseInst = new Nop();
            if (inlineTrue) condition = Negate(condition);
            ILInstruction call = inline.Instructions[0];
            inline.Instructions.Clear();
            ILInstruction value = inner.Value;
            inner.Value = new Nop();
            inner.ReplaceWith(new StLoc(v, value));
            var then = new Block();
            then.Instructions.Add(new StLoc(v, plain));
            var otherwise = new Block();
            otherwise.Instructions.Add(call);
            block.Instructions[at] = new IfInstruction(condition, then, otherwise);
        }
    }

    // ---- 2 ----
    private static void JumpOrientation(ILFunction f, RawIL raw)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test) || !test.FalseInst.MatchNop()) continue;
            var block = (Block)test.Parent;
            int at = test.ChildIndex;
            if (at + 2 != block.Instructions.Count) continue;
            var first = test.TrueInst as Branch;
            var body = test.TrueInst as Block;
            if (first == null && body != null && body.Instructions.Count == 1) first = body.Instructions[0] as Branch;
            var second = block.Instructions[at + 1] as Branch;
            if (first == null || second == null || first.TargetBlock == second.TargetBlock) continue;
            int a = first.TargetBlock.StartILOffset, b = second.TargetBlock.StartILOffset;
            if (a <= 0 || b <= 0 || a == b) continue;
            RawIL.Op branch, jump;
            int end = RawIL.EndOf(test.Condition);
            if (end < 0 || !AtOrBefore(raw, end, RawIL.IsCondBranch, out branch)) continue;
            if (!raw.Near(branch.Offset, 1, out jump) || !IsBr(jump.Code)) continue;
            // As printed: the conditional branch goes to b and the 'br' to a.
            if (branch.Operand != a || jump.Operand != b) continue;
            ILInstruction condition = test.Condition;
            test.Condition = new Nop();
            test.Condition = Negate(condition);
            Block ta = first.TargetBlock, tb = second.TargetBlock;
            first.TargetBlock = tb;
            second.TargetBlock = ta;
        }
    }

    // ---- 3 ----
    private static void LoopConditions(ILFunction f, RawIL raw)
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
            if (body.Parent != loop || body == header || body.IncomingEdgeCount != 1) continue;
            while (body.Instructions.Count > 1)
            {
                var guard = body.Instructions[0] as IfInstruction;
                if (guard == null || !guard.FalseInst.MatchNop()) break;
                var leave = guard.TrueInst as Leave;
                var inner = guard.TrueInst as Block;
                if (leave == null && inner != null && inner.Instructions.Count == 1) leave = inner.Instructions[0] as Leave;
                if (leave == null || leave.TargetContainer != loop || !leave.Value.MatchNop()) break;
                int a = RawIL.StartOf(test.Condition), b = RawIL.StartOf(guard.Condition), rest = RawIL.StartOf(body.Instructions[1]);
                if (a < 0 || b < 0 || rest < 0 || b <= a || rest >= a) break;
                // The test of b follows the one of a: a conditional branch ends a, right before b.
                RawIL.Op before;
                if (!raw.Near(b, -1, out before) || !RawIL.IsCondBranch(before.Code)) break;
                ILInstruction first = test.Condition, second = guard.Condition;
                test.Condition = new Nop();
                guard.Condition = new Nop();
                body.Instructions.RemoveAt(0);
                test.Condition = IfInstruction.LogicAnd(first, Negate(second));
            }
        }
    }

    // ---- 4 ----
    private static void StoreThenCopy(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            var copy = store.Value as StObj;
            if (copy == null || !IsStatement(store) || store.Variable.Kind != VariableKind.Local) continue;
            // Through a reference, into a field of an object held in a variable, or into a static field.
            var intoField = copy.Target as LdFlda;
            bool viaAddress = copy.Target is LdLoc || (intoField != null && intoField.Target is LdLoc);
            if (!viaAddress && !(copy.Target is LdsFlda)) continue;
            RawIL.Op kept, before, address, again;
            int end = RawIL.EndOf(copy.Value);
            if (end < 0 || !raw.IsAt(end, out kept) || !RawIL.IsStLoc(kept.Code)) continue;
            if (store.Variable.Index.HasValue && store.Variable.Index.Value != kept.Operand) continue;
            if (!raw.Near(kept.Offset, -1, out before) || before.Code == ILOpCode.Dup) continue;
            if (viaAddress)
            {
                if (!raw.Near(kept.Offset, 1, out address) || !(RawIL.IsLdArg(address.Code) || RawIL.IsLdLoc(address.Code))) continue;
                if (!raw.Near(kept.Offset, 2, out again) || !RawIL.IsLdLoc(again.Code) || again.Operand != kept.Operand) continue;
            }
            else
            {
                if (!raw.Near(kept.Offset, 1, out again) || !RawIL.IsLdLoc(again.Code) || again.Operand != kept.Operand) continue;
                if (!raw.Near(kept.Offset, 2, out address) || address.Code != ILOpCode.Stsfld) continue;
            }
            var block = (Block)store.Parent;
            ILInstruction value = copy.Value;
            copy.Value = new Nop();
            store.Value = value;
            copy.Value = new LdLoc(store.Variable);
            block.Instructions.Insert(store.ChildIndex + 1, copy);
        }
    }
    // ---- 5 ----
    // 'c ? y : true' is 'c; brtrue Y; ldc.i4.1; br END; Y: y'. The decompiler prints '!c || y',
    // which has the constant last ('c; brfalse T; y; br END; T: ldc.i4.1').
    internal static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ILInstruction, object> TrueFirst = new System.Runtime.CompilerServices.ConditionalWeakTable<ILInstruction, object>();
    private static readonly object Marker = new object();

    private static void TrueFirstConditionals(ILFunction f, RawIL raw)
    {
        foreach (IfInstruction choice in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (choice.ResultType != StackType.I4 || !choice.TrueInst.MatchLdcI4(1) || choice.FalseInst is LdcI4) continue;
            RawIL.Op branch, one, jump;
            int end = RawIL.EndOf(choice.Condition);
            if (end < 0 || !AtOrBefore(raw, end, RawIL.IsCondBranch, out branch)) continue;
            if (!raw.Near(branch.Offset, 1, out one) || one.Code != ILOpCode.Ldc_i4_1) continue;
            if (!raw.Near(branch.Offset, 2, out jump) || !IsBr(jump.Code) || branch.Operand != jump.End) continue;
            int rest = RawIL.StartOf(choice.FalseInst);
            if (rest < jump.End || jump.Operand <= rest) continue;
            // The same tree with the constant in the other branch is 'c2 ? y : true', which
            // RestoreSyntax prints for the conditionals RestoreConditional marks.
            ILInstruction condition = choice.Condition, value = choice.FalseInst;
            choice.Condition = new Nop();
            choice.FalseInst = new Nop();
            var written = new IfInstruction(Negate(condition), value, new LdcI4(1));
            written.AddILRange(choice);
            choice.ReplaceWith(written);
            RestoreConditional.Explicit.GetValue(written, _ => Marker);
        }
    }

    // Lowest offset of a statement, its exits left out: every 'return;' of a void method is
    // the same 'leave' for the decompiler, with the place of another 'ret'.
    private static int StartOfCode(ILInstruction inst)
    {
        int min = -1;
        var stack = new Stack<ILInstruction>();
        stack.Push(inst);
        while (stack.Count > 0)
        {
            ILInstruction i = stack.Pop();
            if (i is ILFunction) continue;
            if (!(i is Leave) && !(i is Branch))
                foreach (var r in i.ILRanges)
                    if (r.Start != r.End && (min < 0 || r.Start < min)) min = r.Start;
            foreach (ILInstruction c in i.Children) stack.Push(c);
        }
        return min;
    }

    // True when every store of the slot is followed at once by its address being taken.
    private static bool StoredForAddress(RawIL raw, int slot)
    {
        int stores = 0, addresses = 0;
        foreach (RawIL.Op op in raw.Ops)
        {
            if (RawIL.IsStLoc(op.Code) && op.Operand == slot) stores++;
            if ((op.Code == ILOpCode.Ldloca || op.Code == ILOpCode.Ldloca_s) && op.Operand == slot) addresses++;
        }
        if (stores != addresses) return false;
        for (int i = 0; i < raw.Ops.Count; i++)
        {
            if (!RawIL.IsStLoc(raw.Ops[i].Code) || raw.Ops[i].Operand != slot) continue;
            if (i + 1 >= raw.Ops.Count || (raw.Ops[i + 1].Code != ILOpCode.Ldloca && raw.Ops[i + 1].Code != ILOpCode.Ldloca_s) || raw.Ops[i + 1].Operand != slot) return false;
            if (raw.Targets.Contains(raw.Ops[i + 1].Offset)) return false;
        }
        return true;
    }

    private static int SlotUses(RawIL raw, int slot)
    {
        int n = 0;
        foreach (RawIL.Op op in raw.Ops)
            if ((RawIL.IsLdLoc(op.Code) || RawIL.IsStLoc(op.Code) || op.Code == ILOpCode.Ldloca || op.Code == ILOpCode.Ldloca_s) && op.Operand == slot) n++;
        return n;
    }

    // ---- 6 ----
    // Pattern variable.
    //        F(e is T v ? v.f : d);                IL: <operands of F>; e; stloc v; ldloc v; brtrue
    //        if (!(e is T v) || v != c) return;    IL: e; stloc v; ldloc v; c; beq       (T a value type)
    // are printed 'T v = e;' and a test of v. A variable declared before the statement is
    // evaluated before the operands of F; and one that is read once, at once, would have
    // stayed on the stack.
    private static void PatternVariables(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            ILVariable v = store.Variable;
            if (!IsStatement(store) || v.Kind != VariableKind.Local || v.StoreCount != 1 || v.AddressCount != 0 || v.LoadCount == 0 || !v.Index.HasValue) continue;
            if (store.Value is LdLoc || store.Value is Block) continue;
            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            if (at + 1 >= block.Instructions.Count) continue;
            ILInstruction next = block.Instructions[at + 1];
            RawIL.Op kept, read, branch;
            int end = RawIL.EndOf(store.Value), start = RawIL.StartOf(store.Value);
            if (end < 0 || start < 0 || !raw.IsAt(end, out kept) || !RawIL.IsStLoc(kept.Code) || kept.Operand != v.Index.Value) continue;
            if (!raw.Near(kept.Offset, 1, out read) || !RawIL.IsLdLoc(read.Code) || read.Operand != kept.Operand) continue;
            if (raw.Targets.Contains(read.Offset)) continue;
            LdLoc first = next.Descendants.OfType<LdLoc>().FirstOrDefault(l => l.Variable == v);
            if (first == null) continue;
            var test = first.Parent as Comp;
            if (test == null) continue;
            bool reference = v.Type.IsReferenceType == true;
            if (reference)
            {
                // The value is tested for null in the middle of the next statement.
                if (!test.Right.MatchLdNull() || test.Left != first) continue;
                if (test.Kind != ComparisonKind.Equality && test.Kind != ComparisonKind.Inequality) continue;
                if (!raw.Near(kept.Offset, 2, out branch) || (branch.Code != ILOpCode.Brtrue && branch.Code != ILOpCode.Brtrue_s && branch.Code != ILOpCode.Brfalse && branch.Code != ILOpCode.Brfalse_s)) continue;
                // What the next statement evaluates before the test is in the IL before the value.
                bool between = false, earlier = false;
                foreach (ILInstruction d in next.Descendants)
                {
                    if (d == first) break;
                    foreach (var r in d.ILRanges)
                    {
                        if (r.Start == r.End) continue;
                        if (r.Start >= start) between = true; else earlier = true;
                    }
                }
                if (between || !earlier) continue;
                ILVariable p = f.RegisterVariable(VariableKind.PatternLocal, v.Type);
                p.Name = v.Name;
                foreach (LdLoc use in f.Descendants.OfType<LdLoc>().Where(l => l.Variable == v).ToList()) use.Variable = p;
                ILInstruction value = store.Value;
                store.Value = new Nop();
                ILInstruction match = new MatchInstruction(p, value) { CheckNotNull = true, CheckType = true };
                if (test.Kind == ComparisonKind.Equality) match = Comp.LogicNot(match);
                test.ReplaceWith(match);
                block.Instructions.RemoveAt(at);
            }
            else
            {
                // Read once, right after the store, by the test of the next statement.
                if (v.Type.IsReferenceType != false || v.LoadCount != 1 || SlotUses(raw, kept.Operand) != 2) continue;
                if (v.Type.Kind != TypeKind.Enum && v.Type.Kind != TypeKind.Struct) continue;
                var guard = next as IfInstruction;
                if (guard == null || !first.IsDescendantOf(guard.Condition) || test.Left != first) continue;
                if (test.LiftingKind != ComparisonLiftingKind.None) continue;
                if (!(test.Right is LdcI4)) continue;
                if (test.Parent != guard && !(test.Parent is Comp && test.Parent.Parent == guard)) continue;
                ILVariable p = f.RegisterVariable(VariableKind.PatternLocal, v.Type);
                p.Name = v.Name;
                first.Variable = p;
                ILInstruction value = store.Value;
                store.Value = new Nop();
                var holder = new Nop();
                test.ReplaceWith(holder);
                holder.ReplaceWith(IfInstruction.LogicAnd(new MatchInstruction(p, value) { CheckNotNull = false, CheckType = true }, test));
                block.Instructions.RemoveAt(at);
            }
        }
    }

    // ---- 7 ----
    // Method called on a computed value, in the middle of an expression.
    //        s + f.ToString() + ((int)e).ToString()      IL: s; <f.ToString()>; e; stloc t; ldloca t; call ToString
    // is printed with 'int t = (int)e;' before the statement and what was already on the
    // stack kept in variables of its own.
    private static void ValueReceivers(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            ILVariable v = store.Variable;
            // (The slot may be stored several times: the temporary of several such calls, each a variable of its own here.)
            if (!IsStatement(store) || v.Kind != VariableKind.Local || v.StoreCount != 1 || v.AddressCount != 1 || v.LoadCount != 0 || !v.Index.HasValue) continue;
            if (v.Type.IsReferenceType != false) continue;
            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            if (at + 1 >= block.Instructions.Count) continue;
            ILInstruction next = block.Instructions[at + 1];
            RawIL.Op kept, address;
            int end = RawIL.EndOf(store.Value);
            if (end < 0 || !raw.IsAt(end, out kept) || !RawIL.IsStLoc(kept.Code) || kept.Operand != v.Index.Value) continue;
            if (!raw.Near(kept.Offset, 1, out address) || (address.Code != ILOpCode.Ldloca && address.Code != ILOpCode.Ldloca_s) || address.Operand != kept.Operand) continue;
            // A copy of a variable is a variable of the source ('ldarga p' otherwise), and
            // 'ldfld f; stloc t; ldloca t; constrained.' is the field of a MarshalByRefObject
            // written bare in a concatenation (RestoreSyntax, MarshalByRefOperand).
            RawIL.Op before, after;
            // ('((int)e).ToString()' on an enum variable is a load too, of another type.)
            if (store.Value is LdLoc && ((LdLoc)store.Value).Variable.Type.Equals(v.Type)) continue;
            // (A field of another struct type is always copied: that one is the compiler's.)
            ITypeDefinition primitive = v.Type.GetDefinition();
            if (primitive != null && primitive.KnownTypeCode >= KnownTypeCode.Boolean && primitive.KnownTypeCode <= KnownTypeCode.Double
                && raw.Near(kept.Offset, -1, out before) && before.Code == ILOpCode.Ldfld && raw.Near(kept.Offset, 2, out after) && after.Code == ILOpCode.Constrained) continue;
            if (!StoredForAddress(raw, kept.Operand) || raw.Ops.Any(o => RawIL.IsLdLoc(o.Code) && o.Operand == kept.Operand)) continue;
            LdLoca use = next.Descendants.OfType<LdLoca>().FirstOrDefault(l => l.Variable == v);
            var call = use == null ? null : use.Parent as CallInstruction;
            if (call == null || call.Arguments.Count == 0 || call.Arguments[0] != use || call.Method.IsStatic) continue;
            // What the next statement evaluates before: plain loads, and stack slots filled
            // by the statements just before, in order.
            var slots = new List<LdLoc>();
            bool plain = true;
            foreach (ILInstruction d in next.Descendants)
            {
                if (d == use) break;
                var load = d as LdLoc;
                if (load != null && load.Variable.Kind == VariableKind.StackSlot && load.Variable.LoadCount == 1) { slots.Add(load); continue; }
                if (!(d is LdLoc || d is LdcI4 || d is LdStr || d is LdNull || d is LdcF4 || d is LdcF8 || d is LdcI8 || d is LdsFlda || d is LdLoca || d is LdElema)) { plain = false; break; }
            }
            if (!plain || slots.Count > at) continue;
            bool ordered = true;
            for (int i = 0; i < slots.Count && ordered; i++)
            {
                var earlier = block.Instructions[at - slots.Count + i] as StLoc;
                ILVariable s = slots[i].Variable;
                if (earlier == null || earlier.Variable != s || s.LoadCount != 1 || s.StoreCount != 1 || s.AddressCount != 0) ordered = false;
            }
            if (!ordered) continue;
            ILInstruction value = store.Value;
            store.Value = new Nop();
            use.ReplaceWith(new AddressOf(value, v.Type));
            for (int i = 0; i < slots.Count; i++)
            {
                var earlier = (StLoc)block.Instructions[at - slots.Count + i];
                ILInstruction filled = earlier.Value;
                earlier.Value = new Nop();
                slots[i].ReplaceWith(filled);
            }
            block.Instructions.RemoveRange(at - slots.Count, slots.Count + 1);
        }
    }

    // ---- 8 ----
    // Fields of a struct assigned after it is cleared.
    //        T x = default(T);  x.f = a;           IL: ldloca x; initobj T; ldloca x; a; stfld f
    // is printed 'T x = new T { f = a };', which fills a temporary and copies it ('ldloc t; stloc x').
    private static void StructInitializers(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            var init = store.Value as Block;
            if (init == null || init.Kind != BlockKind.ObjectInitializer || !IsStatement(store) || init.Instructions.Count < 2) continue;
            var first = init.Instructions[0] as StLoc;
            var final = init.FinalInstruction as LdLoc;
            if (first == null || final == null || final.Variable != first.Variable || !(first.Value is DefaultValue)) continue;
            ILVariable v = store.Variable, temp = first.Variable;
            if (v.Kind != VariableKind.Local || v.StoreCount != 1 || !v.Index.HasValue) continue;
            // Every statement of the initializer stores through the address of the temporary.
            bool simple = true;
            for (int i = 1; i < init.Instructions.Count && simple; i++)
            {
                var set = init.Instructions[i] as StObj;
                var field = set == null ? null : set.Target as LdFlda;
                if (field == null || !(field.Target is LdLoca) || ((LdLoca)field.Target).Variable != temp) simple = false;
                else if (set.Value.Descendants.OfType<IInstructionWithVariableOperand>().Any(u => u.Variable == temp)) simple = false;
            }
            if (!simple) continue;
            // 'ldloca x' in front of the first value, 'initobj' before it.
            int start = RawIL.StartOf(((StObj)init.Instructions[1]).Value);
            RawIL.Op address = default(RawIL.Op), clear, cleared;
            bool found = false;
            for (int back = 1; back <= 2 && !found; back++)
                found = start >= 0 && raw.Near(start, -back, out address) && (address.Code == ILOpCode.Ldloca || address.Code == ILOpCode.Ldloca_s);
            if (!found || address.Operand != v.Index.Value) continue;
            if (!raw.Near(address.Offset, -1, out clear) || clear.Code != ILOpCode.Initobj) continue;
            if (!raw.Near(address.Offset, -2, out cleared) || (cleared.Code != ILOpCode.Ldloca && cleared.Code != ILOpCode.Ldloca_s) || cleared.Operand != address.Operand) continue;

            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            var statements = init.Instructions.ToList();
            init.Instructions.Clear();
            ILInstruction created = first.Value;
            first.Value = new Nop();
            store.Value = created;
            for (int i = 1; i < statements.Count; i++)
            {
                foreach (LdLoca use in statements[i].Descendants.OfType<LdLoca>().Where(l => l.Variable == temp).ToList())
                    use.ReplaceWith(new LdLoca(v));
                block.Instructions.Insert(at + i, statements[i]);
            }
        }
    }

    // ---- 9 ----
    //        if (c) { R } else { return; }  S      where R ends with 'if (d) { ...; return; }'
    // compiles to 'c; brfalse X; R; X: ret; S': no jump is left at the end of R, its last test
    // goes straight to S. It is printed 'if (!c) return;  R  S', which has the 'ret' before R.
    private static void ReturnInElse(ILFunction f, RawIL raw)
    {
        if (f.ReturnType.Kind != TypeKind.Void) return;
        foreach (Block block in RawIL.Own(f).OfType<Block>().ToList())
        {
            if (block.Kind != BlockKind.ControlFlow) continue;
            for (int k = 0; k + 2 < block.Instructions.Count; k++)
            {
                var test = block.Instructions[k] as IfInstruction;
                if (test == null || !test.FalseInst.MatchNop()) continue;
                var exit = test.TrueInst as Leave;
                var body = test.TrueInst as Block;
                if (exit == null && body != null && body.Instructions.Count == 1) exit = body.Instructions[0] as Leave;
                if (exit == null || !exit.IsLeavingFunction || !exit.Value.MatchNop()) continue;
                RawIL.Op branch, ret, before;
                int end = RawIL.EndOf(test.Condition);
                if (end < 0 || !AtOrBefore(raw, end, RawIL.IsCondBranch, out branch)) continue;
                if (!raw.IsAt(branch.Operand, out ret) || ret.Code != ILOpCode.Ret || branch.Operand <= branch.End) continue;
                if (!raw.Near(ret.Offset, -1, out before) || before.Code != ILOpCode.Ret) continue;
                if (ret.Offset == raw.Ops[raw.Ops.Count - 1].Offset) continue;
                // The statements between the test and that 'ret'.
                int m = -1;
                for (int i = k + 1; i < block.Instructions.Count; i++)
                {
                    int s = StartOfCode(block.Instructions[i]);
                    if (s == ret.End) { m = i - 1; break; }
                    if (s < branch.End || s >= ret.Offset) break;
                }
                if (m <= k) continue;
                var last = block.Instructions[m] as IfInstruction;
                if (last == null || !last.FalseInst.MatchNop() || !last.TrueInst.HasFlag(InstructionFlags.EndPointUnreachable)) continue;

                ILInstruction condition = test.Condition;
                test.Condition = new Nop();
                test.TrueInst = new Nop();
                var moved = new List<ILInstruction>();
                for (int i = k + 1; i <= m; i++) moved.Add(block.Instructions[i]);
                block.Instructions.RemoveRange(k, m - k + 1);
                var then = new Block();
                then.Instructions.AddRange(moved);
                var otherwise = new Block();
                otherwise.Instructions.Add(new Leave(exit.TargetContainer));
                block.Instructions.Insert(k, new IfInstruction(Negate(condition), then, otherwise));
                break;
            }
        }
    }

    // ---- 10 ----
    // Nullable operand of a lifted operator.
    //        if (v == e)                           IL: e; stloc t; ldloca t; call get_HasValue
    // with e of type T? is printed 'T? x = e; if (v == x)': declared, x is copied again into
    // a temporary of the operator.
    private static void NullableTemps(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            ILVariable v = store.Variable;
            if (!IsStatement(store) || v.Kind != VariableKind.Local || v.StoreCount != 1 || v.LoadCount != 1 || v.AddressCount != 0 || !v.Index.HasValue) continue;
            if (!v.Type.IsKnownType(KnownTypeCode.NullableOfT)) continue;
            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            if (at + 1 >= block.Instructions.Count) continue;
            ILInstruction next = block.Instructions[at + 1];
            RawIL.Op kept, address, call;
            int end = RawIL.EndOf(store.Value);
            if (end < 0 || !AtOrBefore(raw, end, RawIL.IsStLoc, out kept) || kept.Operand != v.Index.Value) continue;
            if (!raw.Near(kept.Offset, 1, out address) || (address.Code != ILOpCode.Ldloca && address.Code != ILOpCode.Ldloca_s) || address.Operand != kept.Operand) continue;
            if (!raw.Near(kept.Offset, 2, out call) || call.Code != ILOpCode.Call) continue;
            IMethod has = raw.Method(call);
            if (has == null || has.Name != "get_HasValue") continue;
            LdLoc use = next.Descendants.OfType<LdLoc>().FirstOrDefault(l => l.Variable == v);
            if (use == null || !(use.Parent is CallInstruction || use.Parent is Comp)) continue;
            // What the statement evaluates before it has no effect: loads, and the wrapping of the other operand.
            bool plain = true;
            foreach (ILInstruction d in next.Descendants)
            {
                if (d == use) break;
                var wrap = d as NewObj;
                if (wrap != null && wrap.Method.DeclaringType.IsKnownType(KnownTypeCode.NullableOfT)) continue;
                if (!(d is LdLoc || d is LdcI4 || d is LdNull || d is LdcF4 || d is LdcF8 || d is LdcI8 || d is LdStr)) { plain = false; break; }
            }
            if (!plain) continue;
            ILInstruction value = store.Value;
            store.Value = new Nop();
            use.ReplaceWith(value);
            block.Instructions.RemoveAt(at);
        }
    }

    // ---- 11 ----
    // Value of the first operands of a condition.
    //        bool b = x && y;  if (b && z)         IL: x; brfalse F; y; br E; F: ldc.i4.0; E: brfalse SKIP; z; brfalse SKIP
    // is printed 'if (x && y && z)', which branches on every operand.
    private static void Conjuncts(ILInstruction condition, bool negated, List<KeyValuePair<ILInstruction, bool>> list)
    {
        ILInstruction lhs, rhs, inner;
        if (!negated && condition.MatchLogicAnd(out lhs, out rhs)) { Conjuncts(lhs, false, list); Conjuncts(rhs, false, list); }
        else if (negated && condition.MatchLogicOr(out lhs, out rhs)) { Conjuncts(lhs, true, list); Conjuncts(rhs, true, list); }
        else if (condition.MatchLogicNot(out inner) && (negated || inner.MatchLogicOr(out lhs, out rhs))) Conjuncts(inner, !negated, list);
        else list.Add(new KeyValuePair<ILInstruction, bool>(condition, negated));
    }

    private static ILInstruction And(List<KeyValuePair<ILInstruction, bool>> parts, int from, int to)
    {
        ILInstruction r = Operand(parts[from]);
        for (int i = from + 1; i < to; i++) r = IfInstruction.LogicAnd(r, Operand(parts[i]));
        return r;
    }

    private static ILInstruction Operand(KeyValuePair<ILInstruction, bool> part)
    {
        ILInstruction inst = Take(part.Key), positive;
        if (!part.Value) return inst;
        return inst.MatchLogicNot(out positive) ? Take(positive) : Comp.LogicNot(inst);
    }

    private static void ShortCircuitPrefixes(ILFunction f, RawIL raw, ILTransformContext context)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test)) continue;
            var loop = test.Parent.Parent as BlockContainer;
            if (loop != null && loop.Kind != ContainerKind.Normal && loop.Kind != ContainerKind.Switch) continue;
            var parts = new List<KeyValuePair<ILInstruction, bool>>();
            Conjuncts(test.Condition, false, parts);
            if (parts.Count < 3) continue;
            int cut = -1;
            for (int i = 2; i < parts.Count && cut < 0; i++)
            {
                int start = RawIL.StartOf(parts[i].Key);
                RawIL.Op branch, zero, jump, probe;
                if (start <= 0 || !raw.Near(start, -1, out branch)) continue;
                // The range of a field read may start at its 'ldfld': the object is loaded before.
                if ((RawIL.IsLdArg(branch.Code) || RawIL.IsLdLoc(branch.Code)) && !raw.Targets.Contains(start) && raw.Near(start, -2, out probe)) { start = branch.Offset; branch = probe; }
                if (branch.Code != ILOpCode.Brfalse && branch.Code != ILOpCode.Brfalse_s) continue;
                if (!raw.Near(branch.Offset, -1, out zero) || zero.Code != ILOpCode.Ldc_i4_0) continue;
                if (!raw.Near(branch.Offset, -2, out jump) || !IsBr(jump.Code) || jump.Operand != branch.Offset) continue;
                int first = RawIL.StartOf(parts[0].Key);
                if (first < 0 || first > jump.Offset) continue;
                cut = i;
            }
            if (cut < 0) continue;
            var block = (Block)test.Parent;
            ILVariable v = f.RegisterVariable(VariableKind.Local, context.TypeSystem.FindType(KnownTypeCode.Boolean));
            ILInstruction prefix = And(parts, 0, cut), rest = And(parts, cut, parts.Count);
            test.Condition = IfInstruction.LogicAnd(new LdLoc(v), rest);
            block.Instructions.Insert(test.ChildIndex, new StLoc(v, prefix));
        }
    }

    // ---- 12 ----
    // Orientation of 'c ? a : b' when its values have no place in the IL (array initializers):
    // the compiler tests a plain boolean with 'brtrue' ('c; brtrue A; b; br E; A: a').
    private static void ConditionalsByBranch(ILFunction f, RawIL raw)
    {
        foreach (IfInstruction choice in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (choice.ResultType == StackType.Void || choice.TrueInst is LdcI4 || choice.FalseInst is LdcI4) continue;
            var a = choice.TrueInst as Block;
            var b = choice.FalseInst as Block;
            if (a == null || b == null || a.Kind != BlockKind.ArrayInitializer || b.Kind != BlockKind.ArrayInitializer) continue;
            if (RawIL.StartOf(a) >= 0 && RawIL.StartOf(b) >= 0) continue;
            ILInstruction positive;
            bool negated = choice.Condition.MatchLogicNot(out positive);
            if (!negated) positive = choice.Condition;
            if (positive is Comp || positive is IfInstruction || positive.ResultType != StackType.I4) continue;
            RawIL.Op branch;
            int end = RawIL.EndOf(positive);
            if (end < 0 || !raw.IsAt(end, out branch)) continue;
            bool onTrue = branch.Code == ILOpCode.Brtrue || branch.Code == ILOpCode.Brtrue_s;
            bool onFalse = branch.Code == ILOpCode.Brfalse || branch.Code == ILOpCode.Brfalse_s;
            if (!onTrue && !onFalse) continue;
            // 'brtrue' is the test of 'c', 'brfalse' the one of '!c'.
            if (onTrue != negated) continue;
            ILInstruction t = choice.TrueInst, e = choice.FalseInst, condition = choice.Condition;
            choice.TrueInst = new Nop();
            choice.FalseInst = new Nop();
            choice.Condition = new Nop();
            choice.Condition = Negate(condition);
            choice.TrueInst = e;
            choice.FalseInst = t;
        }
    }

    // ---- 13 ----
    //        x == null ? d : x.f                   IL: ldloc x; brfalse D; ldloc x; ldfld f; br E; D: d; E:
    // is printed 'x?.f ?? d', which is 'x != null ? x.f : d' for the compiler: 'ldloc x;
    // brtrue A; d; br E; A: ldloc x; ldfld f'.
    private static void NullFallbacks(ILFunction f, RawIL raw)
    {
        foreach (NullCoalescingInstruction coalesce in RawIL.Own(f).OfType<NullCoalescingInstruction>().ToList())
        {
            var rewrap = coalesce.ValueInst as NullableRewrap;
            if (rewrap == null) continue;
            var unwraps = rewrap.Descendants.OfType<NullableUnwrap>().ToList();
            if (unwraps.Count != 1) continue;
            var load = unwraps[0].Argument as LdLoc;
            if (load == null || unwraps[0].ResultType != StackType.O) continue;
            if (load.Variable.Kind != VariableKind.Local && load.Variable.Kind != VariableKind.Parameter) continue;
            // A value: with a reference result, the fallback is tested on the result itself.
            if (rewrap.Argument.ResultType == StackType.O) continue;
            bool found = false;
            RawIL.Op op, branch = default(RawIL.Op), again;
            foreach (var r in load.ILRanges)
            {
                if (!raw.IsAt(r.Start, out op) || !(RawIL.IsLdLoc(op.Code) || RawIL.IsLdArg(op.Code))) continue;
                // The range is the one of the test, or of the read that follows it.
                if (raw.Near(r.Start, 1, out branch) && (branch.Code == ILOpCode.Brfalse || branch.Code == ILOpCode.Brfalse_s)
                    && raw.Near(r.Start, 2, out again) && again.Code == op.Code && again.Operand == op.Operand) { found = true; break; }
                if (raw.Near(r.Start, -1, out branch) && (branch.Code == ILOpCode.Brfalse || branch.Code == ILOpCode.Brfalse_s)
                    && raw.Near(r.Start, -2, out again) && again.Code == op.Code && again.Operand == op.Operand) { found = true; break; }
            }
            if (!found) continue;
            // The fallback is where the branch goes.
            RawIL.Op jump;
            if (!raw.EndsAt(branch.Operand, out jump) || !IsBr(jump.Code) || jump.Operand <= branch.Operand) continue;
            ILVariable v = load.Variable;
            ILInstruction value = rewrap.Argument, fallback = coalesce.FallbackInst;
            rewrap.Argument = new Nop();
            coalesce.FallbackInst = new Nop();
            unwraps[0].ReplaceWith(new LdLoc(v));
            var written = new IfInstruction(new Comp(ComparisonKind.Equality, Sign.None, new LdLoc(v), new LdNull()), fallback, value);
            written.AddILRange(coalesce);
            coalesce.ReplaceWith(written);
        }
    }

    // ---- 14 ----
    // Assignment as an argument.
    //        F(a, v = new T { p = 1 });            IL: a; newobj T; dup; 1; call set_p; dup; stloc v; call F
    // is printed 'T t = new T { p = 1 }; v = t; F(a, t);', which creates the object before a.
    private static void InlineStores(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            ILVariable s = store.Variable;
            if (!IsStatement(store) || s.Kind != VariableKind.StackSlot || s.StoreCount != 1 || s.LoadCount != 2 || s.AddressCount != 0) continue;
            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            if (at + 2 >= block.Instructions.Count) continue;
            var copy = block.Instructions[at + 1] as StLoc;
            ILInstruction next = block.Instructions[at + 2];
            if (copy == null || !copy.Value.MatchLdLoc(s) || copy.Variable.Kind != VariableKind.Local || !copy.Variable.Index.HasValue) continue;
            var uses = next.Descendants.OfType<LdLoc>().Where(l => l.Variable == s).ToList();
            if (uses.Count != 1 || !(uses[0].Parent is CallInstruction)) continue;
            RawIL.Op dup, kept;
            int end = RawIL.EndOf(store.Value), start = RawIL.StartOf(store.Value);
            if (end < 0 || start < 0 || !AtOrBefore(raw, end, c => c == ILOpCode.Dup, out dup)) continue;
            if (!raw.Near(dup.Offset, 1, out kept) || !RawIL.IsStLoc(kept.Code) || kept.Operand != copy.Variable.Index.Value) continue;
            // What the statement evaluates first is in the IL before the value, and has no effect.
            bool plain = true, earlier = false;
            foreach (ILInstruction d in next.Descendants)
            {
                if (d == uses[0]) break;
                if (!(d is LdLoc || d is LdcI4 || d is LdStr || d is LdNull || d is LdcF4 || d is LdcF8 || d is LdcI8 || d is LdsFlda || d is LdFlda || d is LdObj)) { plain = false; break; }
                foreach (var r in d.ILRanges)
                {
                    if (r.Start == r.End) continue;
                    if (r.Start >= start) plain = false; else earlier = true;
                }
            }
            if (!plain || !earlier) continue;
            ILInstruction value = store.Value;
            store.Value = new Nop();
            uses[0].ReplaceWith(new StLoc(copy.Variable, value));
            block.Instructions.RemoveRange(at, 2);
        }
    }

    // ---- 15 ----
    //        if (!c) return;  X  return;           IL: c; brtrue L; ret; L: X; ret
    // away from the end of the method is printed 'if (c) { X }  return;' ('c; brfalse L; X; L: ret').
    // In a protected block the two 'ret' are 'leave' to the end of the method.
    private static bool IsExit(RawIL raw, RawIL.Op op)
    {
        if (op.Code == ILOpCode.Ret) return true;
        RawIL.Op landing;
        return (op.Code == ILOpCode.Leave || op.Code == ILOpCode.Leave_s) && !raw.RegionEnds.Contains(op.End) && raw.IsAt(op.Operand, out landing) && landing.Code == ILOpCode.Ret;
    }

    private static void GuardsBeforeReturn(ILFunction f, RawIL raw)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test) || !test.FalseInst.MatchNop()) continue;
            var block = (Block)test.Parent;
            int at = test.ChildIndex;
            if (at + 2 != block.Instructions.Count) continue;
            var exit = block.Instructions[at + 1] as Leave;
            if (exit == null || !exit.Value.MatchNop()) continue;
            if (exit.IsLeavingFunction && f.ReturnType.Kind != TypeKind.Void) continue;
            // Not the end of the method itself: RestoreEarlyReturn decides there.
            if (block.Parent is BlockContainer && block.Parent.Parent is ILFunction) continue;
            var body = test.TrueInst as Block;
            if (body == null || body.Instructions.Count == 0 || body.HasFlag(InstructionFlags.EndPointUnreachable)) continue;
            RawIL.Op branch, early;
            int end = RawIL.EndOf(test.Condition);
            if (end < 0 || !AtOrBefore(raw, end, RawIL.IsCondBranch, out branch)) continue;
            if (!raw.Near(branch.Offset, 1, out early) || branch.Operand != early.End) continue;
            // 'break;' out of a 'switch' in a protected block is a 'leave' too: 'if (!c) break;  X  break;'.
            // (Elsewhere it is a 'br', which the compiler folds into the test.)
            bool leaves = (early.Code == ILOpCode.Leave || early.Code == ILOpCode.Leave_s) && !raw.RegionEnds.Contains(early.End);
            BlockContainer target = exit.TargetContainer;
            if (!exit.IsLeavingFunction && target.Kind == ContainerKind.Normal)
            {
                // The end of a protected block ('lock', 'using', 'try') that a 'break;' or a
                // 'return;' follows: leaving early is that statement.
                ILInstruction holder = target.Parent;
                bool wraps = block == target.EntryPoint && target.Blocks.Count == 1 && ((holder is LockInstruction && ((LockInstruction)holder).Body == target) || (holder is TryFinally && ((TryFinally)holder).TryBlock == target)
                    || (holder is TryCatch && ((TryCatch)holder).TryBlock == target) || (holder is UsingInstruction && ((UsingInstruction)holder).Body == target));
                var outer = wraps && IsStatement(holder) ? (Block)holder.Parent : null;
                var after = outer != null && holder.ChildIndex + 1 < outer.Instructions.Count ? outer.Instructions[holder.ChildIndex + 1] as Leave : null;
                if (after == null || !after.Value.MatchNop() || !leaves) continue;
                if (after.IsLeavingFunction ? f.ReturnType.Kind != TypeKind.Void : after.TargetContainer.Kind != ContainerKind.Switch) continue;
                target = after.TargetContainer;
            }
            else if (exit.IsLeavingFunction ? !IsExit(raw, early) : !(leaves && target.Kind == ContainerKind.Switch)) continue;
            if (StartOfCode(body) != early.End) continue;
            ILInstruction condition = test.Condition;
            test.Condition = new Nop();
            test.TrueInst = new Nop();
            var statements = body.Instructions.ToList();
            body.Instructions.Clear();
            block.Instructions.RemoveAt(at);
            block.Instructions.Insert(at, new IfInstruction(Negate(condition), new Leave(target)));
            for (int i = 0; i < statements.Count; i++) block.Instructions.Insert(at + 1 + i, statements[i]);
        }
    }

    // ---- 16 ----
    // The branch of an 'if' that the IL has first is its 'then': the compiler never puts the
    // 'else' in front.
    private static void IfElseOrder(ILFunction f, RawIL raw)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test)) continue;
            var then = test.TrueInst as Block;
            var otherwise = test.FalseInst as Block;
            if (then == null || otherwise == null || then.Instructions.Count == 0 || otherwise.Instructions.Count == 0) continue;
            int t = StartOfCode(then), e = StartOfCode(otherwise), c = RawIL.EndOf(test.Condition);
            if (t < 0 || e < 0 || c < 0 || e >= t || e < c) continue;
            // The test falls into the 'else' of the tree, and jumps to its 'then'.
            RawIL.Op branch, jump;
            if (!AtOrBefore(raw, c, RawIL.IsCondBranch, out branch) || branch.End != e || branch.Operand != t) continue;
            // The first branch ends with a jump over the second, or does not reach its end.
            if (!raw.EndsAt(t, out jump)) continue;
            if (!(IsBr(jump.Code) && jump.Operand > t) && jump.Code != ILOpCode.Ret && jump.Code != ILOpCode.Throw && jump.Code != ILOpCode.Leave && jump.Code != ILOpCode.Leave_s) continue;
            ILInstruction condition = test.Condition;
            test.Condition = new Nop();
            test.TrueInst = new Nop();
            test.FalseInst = new Nop();
            test.Condition = Negate(condition);
            test.TrueInst = otherwise;
            test.FalseInst = then;
        }
    }

    // ---- 17 ----
    // Field assignment as an argument.
    //        F(a, this.f = new T { p = 1 });       IL: a; ldarg.0; newobj T; ...; dup; stloc t; stfld f; ldloc t; call F
    // is printed 'T t = new T { p = 1 }; this.f = t; F(a, t);'.
    private static void InlineFieldStores(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            ILVariable s = store.Variable;
            if (!IsStatement(store) || s.Kind != VariableKind.StackSlot || s.StoreCount != 1 || s.LoadCount != 2 || s.AddressCount != 0) continue;
            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            if (at + 3 >= block.Instructions.Count) continue;
            var copy = block.Instructions[at + 1] as StLoc;
            var assign = block.Instructions[at + 2] as StObj;
            ILInstruction next = block.Instructions[at + 3];
            if (copy == null || assign == null || !copy.Value.MatchLdLoc(s) || !assign.Value.MatchLdLoc(s)) continue;
            ILVariable v = copy.Variable;
            if (v.Kind != VariableKind.Local || !v.Index.HasValue || v.StoreCount != 1 || v.LoadCount != 1 || v.AddressCount != 0) continue;
            var field = assign.Target as LdFlda;
            bool onThis = field != null && field.Target is LdLoc && ((LdLoc)field.Target).Variable.Kind == VariableKind.Parameter && ((LdLoc)field.Target).Variable.Index == -1;
            if (!onThis && !(assign.Target is LdsFlda)) continue;
            var uses = next.Descendants.OfType<LdLoc>().Where(l => l.Variable == v).ToList();
            if (uses.Count != 1 || !(uses[0].Parent is CallInstruction)) continue;
            RawIL.Op dup, kept, set, read;
            int end = RawIL.EndOf(store.Value);
            if (end < 0 || !AtOrBefore(raw, end, c => c == ILOpCode.Dup, out dup)) continue;
            if (!raw.Near(dup.Offset, 1, out kept) || !RawIL.IsStLoc(kept.Code) || kept.Operand != v.Index.Value) continue;
            if (!raw.Near(dup.Offset, 2, out set) || (set.Code != ILOpCode.Stfld && set.Code != ILOpCode.Stsfld)) continue;
            if (!raw.Near(dup.Offset, 3, out read) || !RawIL.IsLdLoc(read.Code) || read.Operand != kept.Operand) continue;
            // What the statement evaluates first: loads, and stack slots filled just before.
            var slots = new List<LdLoc>();
            bool plain = true;
            foreach (ILInstruction d in next.Descendants)
            {
                if (d == uses[0]) break;
                var load = d as LdLoc;
                if (load != null && load.Variable.Kind == VariableKind.StackSlot) { slots.Add(load); continue; }
                if (!(d is LdLoc || d is LdcI4 || d is LdStr || d is LdNull || d is LdcF4 || d is LdcF8 || d is LdcI8)) { plain = false; break; }
            }
            if (!plain || slots.Count > at) continue;
            bool ordered = true;
            for (int i = 0; i < slots.Count && ordered; i++)
            {
                var earlier = block.Instructions[at - slots.Count + i] as StLoc;
                ILVariable slot = slots[i].Variable;
                if (earlier == null || earlier.Variable != slot || slot.LoadCount != 1 || slot.StoreCount != 1 || slot.AddressCount != 0) ordered = false;
            }
            if (!ordered) continue;
            ILInstruction value = store.Value;
            store.Value = new Nop();
            assign.Value = value;
            block.Instructions.RemoveAt(at + 2);
            uses[0].ReplaceWith(assign);
            for (int i = 0; i < slots.Count; i++)
            {
                var earlier = (StLoc)block.Instructions[at - slots.Count + i];
                ILInstruction filled = earlier.Value;
                earlier.Value = new Nop();
                slots[i].ReplaceWith(filled);
            }
            block.Instructions.RemoveRange(at - slots.Count, slots.Count + 2);
        }
    }

    // ---- 18 ----
    // Condition of a conditional expression kept as a value.
    //        bool b = x || y;  F(b ? p : q);       IL: x; brtrue T; y; br E; T: ldc.i4.1; E: brtrue P; q; br N; P: p; N: call F
    // is printed 'F((x || y) ? p : q)', which branches on x and on y.
    private static void ShortCircuitConditions(ILFunction f, RawIL raw, ILTransformContext context)
    {
        foreach (IfInstruction choice in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (choice.ResultType == StackType.Void || IsStatement(choice)) continue;
            ILInstruction logic = choice.Condition;
            var both = logic as IfInstruction;
            var compared = logic as Comp;
            if (both != null && !both.TrueInst.MatchLdcI4(1) && !both.FalseInst.MatchLdcI4(0)) continue;
            if (both == null && (compared == null || compared.LiftingKind != ComparisonLiftingKind.None)) continue;
            if (choice.TrueInst.MatchLdcI4(1) || choice.FalseInst.MatchLdcI4(0)) continue;
            // The alternative comes first, right after the test of the value.
            int alternative = RawIL.StartOf(choice.FalseInst), consequence = RawIL.StartOf(choice.TrueInst), start = RawIL.StartOf(logic);
            RawIL.Op branch, constant, jump;
            if (alternative <= 0 || consequence < alternative || start < 0 || !raw.Near(alternative, -1, out branch) || branch.End != alternative) continue;
            if (branch.Code != ILOpCode.Brtrue && branch.Code != ILOpCode.Brtrue_s) continue;
            if (!raw.Near(branch.Offset, -1, out constant)) continue;
            if (both != null)
            {
                if (constant.Code != ILOpCode.Ldc_i4_0 && constant.Code != ILOpCode.Ldc_i4_1) continue;
                if (!raw.Near(branch.Offset, -2, out jump) || !IsBr(jump.Code) || jump.Operand != branch.Offset || start > jump.Offset) continue;
            }
            else
            {
                // 'bool b = x < y;  b ? p : q' is 'clt; brtrue': the test itself would be 'blt'.
                if (constant.Code != ILOpCode.Clt && constant.Code != ILOpCode.Clt_un && constant.Code != ILOpCode.Cgt && constant.Code != ILOpCode.Cgt_un && constant.Code != ILOpCode.Ceq) continue;
                if (compared.Kind == ComparisonKind.Equality && (compared.Right.MatchLdcI4(0) || compared.Right.MatchLdNull())) continue;
                if (compared.Kind == ComparisonKind.Inequality) continue;
                if (start > constant.Offset || raw.Targets.Contains(branch.Offset)) continue;
            }
            // The statement that holds it evaluates nothing before, apart from loads.
            ILInstruction statement = choice;
            while (statement != null && !IsStatement(statement)) statement = statement.Parent;
            if (statement == null || statement is IfInstruction && ((IfInstruction)statement).Condition != choice && !choice.IsDescendantOf(((IfInstruction)statement).Condition)) continue;
            if (statement is BlockContainer || statement is TryInstruction) continue;
            bool plain = true;
            foreach (ILInstruction d in statement.Descendants)
            {
                if (d == logic || d.IsDescendantOf(logic)) break;
                if (!(d is LdLoc || d is LdcI4 || d is LdStr || d is LdNull || d is LdcF4 || d is LdcF8 || d is LdcI8 || d is LdsFlda || d is LdLoca)) { plain = false; break; }
            }
            if (!plain) continue;
            var block = (Block)statement.Parent;
            var loop = block.Parent as BlockContainer;
            if (loop != null && loop.Kind != ContainerKind.Normal && loop.Kind != ContainerKind.Switch && block == loop.EntryPoint) continue;
            ILVariable v = f.RegisterVariable(VariableKind.Local, context.TypeSystem.FindType(KnownTypeCode.Boolean));
            choice.Condition = new LdLoc(v);
            block.Instructions.Insert(statement.ChildIndex, new StLoc(v, logic));
        }
    }

    // True when no test of two strings in the method is followed by 'brtrue': a 'switch' on a
    // string jumps to its sections that way; a chain of 'if' falls into them ('brfalse').
    internal static bool NoStringSwitch(ILFunction f, ILTransformContext context)
    {
        RawIL raw = RawIL.Read(f, context);
        if (raw == null) return false;
        int tests = 0;
        for (int i = 0; i + 1 < raw.Ops.Count; i++)
        {
            if (raw.Ops[i].Code != ILOpCode.Call) continue;
            IMethod m = raw.Method(raw.Ops[i]);
            if (m == null || m.Name != "op_Equality" || !m.DeclaringType.IsKnownType(KnownTypeCode.String)) continue;
            ILOpCode next = raw.Ops[i + 1].Code;
            if (next != ILOpCode.Brfalse && next != ILOpCode.Brfalse_s) return false;
            tests++;
        }
        return tests >= 2;
    }

    // ---- 19 ----
    // Loop tested at its top.
    //        while (true) { if (!c) break; body }  IL: T: c; <branch when false> EXIT; body; br T; EXIT:
    // is printed 'while (c) { body }', which the compiler tests at the bottom ('br C; B: body; C: c; brtrue B').
    private static void TopTestedLoops(ILFunction f, RawIL raw)
    {
        foreach (BlockContainer loop in RawIL.Own(f).OfType<BlockContainer>().ToList())
        {
            if (loop.Kind != ContainerKind.While || loop.Blocks.Count != 2) continue;
            Block header = loop.EntryPoint;
            if (header.Instructions.Count != 1) continue;
            var test = header.Instructions[0] as IfInstruction;
            if (test == null) continue;
            var toBody = test.TrueInst as Branch;
            var exit = test.FalseInst as Leave;
            if (toBody == null || exit == null || exit.TargetContainer != loop || !exit.Value.MatchNop()) continue;
            Block body = toBody.TargetBlock;
            if (body.Parent != loop || body == header || body.IncomingEdgeCount != 1 || body.Instructions.Count < 2) continue;
            var back = body.Instructions[body.Instructions.Count - 1] as Branch;
            if (back == null || back.TargetBlock != header) continue;
            // 'continue' would go to the test: left alone, unless the raw body has that many
            // jumps back to a test that stands at the top (there 'continue' goes to it in both forms).
            int backs = loop.Descendants.OfType<Branch>().Count(b => b.TargetBlock == header);
            int testAt = RawIL.StartOf(test.Condition);
            if (backs != 1 && (testAt < 0 || raw.Ops.Count(o => o.Operand == testAt && o.Offset > testAt && (IsBr(o.Code) || RawIL.IsCondBranch(o.Code))) < backs)) continue;
            int c = RawIL.StartOf(test.Condition), cEnd = RawIL.EndOf(test.Condition), b0 = StartOfCode(body.Instructions[0]);
            RawIL.Op branch, jump = default(RawIL.Op);
            if (c < 0 || cEnd < 0 || b0 < 0 || c >= b0 || !AtOrBefore(raw, cEnd, RawIL.IsCondBranch, out branch)) continue;
            // The test jumps forward, out of the loop, and the body ends with a jump back to it.
            if (branch.End != b0 || branch.Operand <= b0) continue;
            if (!raw.EndsAt(branch.Operand, out jump)) continue;
            // (A body that ends with 'if (x) return;' jumps back from that test: 'brfalse T; ret'.)
            bool backFromTest = jump.Code == ILOpCode.Ret && raw.Ops.Any(o => o.Offset >= b0 && o.Offset < jump.Offset && RawIL.IsCondBranch(o.Code) && o.Operand == c) && !raw.Ops.Any(o => o.Offset >= b0 && o.Offset < jump.Offset && IsBr(o.Code) && o.Operand == c);
            if (!backFromTest && (!IsBr(jump.Code) || jump.Operand != c)) continue;
            if (raw.Ops.Any(o => IsBr(o.Code) && o.Operand == c && o.Offset < c)) continue;
            ILInstruction condition = test.Condition;
            test.Condition = new Nop();
            var statements = body.Instructions.ToList();
            body.Instructions.Clear();
            header.Instructions.Clear();
            header.Instructions.Add(new IfInstruction(Negate(condition), new Leave(loop)));
            header.Instructions.AddRange(statements);
            loop.Blocks.Remove(body);
            loop.Kind = ContainerKind.Loop;
        }
    }

    // ---- 20 ----
    // Variable assigned, then tested.
    //        b = b | x;  if (b)                    IL: ldloc b; x; or; stloc b; ldloc b; brfalse
    // is printed 'if (b | x)' when b is not read afterwards. A variable read once would not
    // have been stored: this one has other uses, it is the one the expression reads.
    private static void StoredConditions(ILFunction f, RawIL raw)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test)) continue;
            var loop = test.Parent.Parent as BlockContainer;
            if (loop != null && loop.Kind != ContainerKind.Normal && loop.Kind != ContainerKind.Switch) continue;
            if (test.Condition is LdLoc || test.Condition.ResultType != StackType.I4) continue;
            ILInstruction positive;
            if (test.Condition.MatchLogicNot(out positive) && positive is LdLoc) continue;
            RawIL.Op branch, read, kept, before;
            int end = RawIL.EndOf(test.Condition), bodyStart = RawIL.StartOf(test.TrueInst);
            // The expression ends where it is stored: 'stloc b; ldloc b; brfalse' follows.
            if (end < 0 || bodyStart < 0 || !AtOrBefore(raw, end, RawIL.IsStLoc, out kept)) continue;
            if (!raw.Near(kept.Offset, 1, out read) || !RawIL.IsLdLoc(read.Code) || read.Operand != kept.Operand || raw.Targets.Contains(read.Offset)) continue;
            if (!raw.Near(kept.Offset, 2, out branch) || (branch.Code != ILOpCode.Brfalse && branch.Code != ILOpCode.Brfalse_s)) continue;
            // (The body may start with the creation of a closure, which has no place in the tree.)
            if (bodyStart < branch.End) continue;
            if (!raw.Near(kept.Offset, -1, out before) || before.Code == ILOpCode.Dup) continue;
            IType type = raw.LocalType(kept.Operand);
            if (type == null || !type.IsKnownType(KnownTypeCode.Boolean)) continue;
            // The variable of that slot which the expression reads.
            var read2 = test.Condition.Descendants.OfType<LdLoc>().Where(l => l.Variable.Kind == VariableKind.Local && l.Variable.Index == kept.Operand).Select(l => l.Variable).Distinct().ToList();
            if (read2.Count != 1) continue;
            ILVariable v = read2[0];
            var block = (Block)test.Parent;
            ILInstruction condition = test.Condition;
            test.Condition = new LdLoc(v);
            block.Instructions.Insert(test.ChildIndex, new StLoc(v, condition));
        }
    }

    // ---- 21 ----
    // Assignment in both branches, one of them through another variable.
    //        if (c) x = a; else x = (y = b);       IL: c; brfalse E; a; stloc x; br END; E: b; dup; stloc y; stloc x
    // is printed 'x = c ? a : (y = b);'.
    private static void IfElseNestedAssign(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            var choice = store.Value as IfInstruction;
            if (choice == null || !IsStatement(store) || store.Variable.Kind != VariableKind.Local || !store.Variable.Index.HasValue) continue;
            if (choice.ResultType == StackType.Void) continue;
            int t = RawIL.StartOf(choice.TrueInst), e = RawIL.StartOf(choice.FalseInst);
            if (t < 0 || e < 0 || t == e) continue;
            bool trueFirst = t < e;
            ILInstruction first = trueFirst ? choice.TrueInst : choice.FalseInst, second = trueFirst ? choice.FalseInst : choice.TrueInst;
            var nested = second as StLoc;
            if (nested == null || first is StLoc) continue;
            RawIL.Op s1, jump, dup, inner, s2;
            if (!AtOrBefore(raw, RawIL.EndOf(first), RawIL.IsStLoc, out s1) || s1.Operand != store.Variable.Index.Value) continue;
            if (!raw.Near(s1.Offset, 1, out jump) || !IsBr(jump.Code)) continue;
            if (!AtOrBefore(raw, RawIL.EndOf(nested.Value), c => c == ILOpCode.Dup, out dup)) continue;
            if (!raw.Near(dup.Offset, 1, out inner) || !RawIL.IsStLoc(inner.Code)) continue;
            if (!raw.Near(dup.Offset, 2, out s2) || !RawIL.IsStLoc(s2.Code) || s2.Operand != s1.Operand || jump.Operand < s2.End) continue;

            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            ILVariable v = store.Variable;
            ILInstruction condition = choice.Condition;
            store.Value = new Nop();
            choice.Condition = new Nop();
            choice.TrueInst = new Nop();
            choice.FalseInst = new Nop();
            if (!trueFirst) condition = Negate(condition);
            var then = new Block();
            then.Instructions.Add(new StLoc(v, first));
            var otherwise = new Block();
            otherwise.Instructions.Add(new StLoc(v, second));
            block.Instructions[at] = new IfInstruction(condition, then, otherwise);
        }
    }

    // ---- 22 ----
    // Assignment in both branches when the second value has no place in the IL (an array
    // initializer): the first branch ends with 'stloc x; br END', and 'stloc x' is the
    // instruction right before END.
    private static void IfElseAssignByJump(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            var choice = store.Value as IfInstruction;
            if (choice == null || !IsStatement(store) || store.Variable.Kind != VariableKind.Local || !store.Variable.Index.HasValue) continue;
            if (choice.ResultType == StackType.Void) continue;
            int t = RawIL.StartOf(choice.TrueInst), e = RawIL.StartOf(choice.FalseInst);
            if (t < 0 && e < 0) continue;
            bool trueFirst = e < 0 || (t >= 0 && t < e);
            ILInstruction first = trueFirst ? choice.TrueInst : choice.FalseInst, second = trueFirst ? choice.FalseInst : choice.TrueInst;
            var rebuilt = second as Block;
            if (rebuilt == null || rebuilt.Kind != BlockKind.ArrayInitializer) continue;
            RawIL.Op s1, jump, s2, branch;
            if (!AtOrBefore(raw, RawIL.EndOf(first), RawIL.IsStLoc, out s1) || s1.Operand != store.Variable.Index.Value) continue;
            if (!raw.Near(s1.Offset, 1, out jump) || !IsBr(jump.Code) || jump.Operand <= jump.End) continue;
            if (!raw.EndsAt(jump.Operand, out s2) || !RawIL.IsStLoc(s2.Code) || s2.Operand != s1.Operand || s2.Offset <= jump.Offset) continue;
            // The test jumps to the second branch, right after that 'br'.
            int end = RawIL.EndOf(choice.Condition);
            if (end < 0 || !AtOrBefore(raw, end, RawIL.IsCondBranch, out branch) || branch.Operand != jump.End) continue;

            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            ILVariable v = store.Variable;
            ILInstruction condition = choice.Condition;
            store.Value = new Nop();
            choice.Condition = new Nop();
            choice.TrueInst = new Nop();
            choice.FalseInst = new Nop();
            if (!trueFirst) condition = Negate(condition);
            var then = new Block();
            then.Instructions.Add(new StLoc(v, first));
            var otherwise = new Block();
            otherwise.Instructions.Add(new StLoc(v, second));
            block.Instructions[at] = new IfInstruction(condition, then, otherwise);
        }
    }

    // ---- 23 ----
    //        if (a) { if (b) return;  rest }       IL: a; brfalse END; b; brfalse L; ret; L: rest; END: ret
    // at the end of a void method is printed 'if (!a || b) return;  rest'. With '||' the test
    // of a would be followed by a 'ret' of its own or jump to the one of b: here it jumps to
    // the last 'ret'.
    private static void Disjuncts(ILInstruction condition, bool negated, List<KeyValuePair<ILInstruction, bool>> list)
    {
        ILInstruction lhs, rhs, inner;
        if (!negated && condition.MatchLogicOr(out lhs, out rhs)) { Disjuncts(lhs, false, list); Disjuncts(rhs, false, list); }
        else if (negated && condition.MatchLogicAnd(out lhs, out rhs)) { Disjuncts(lhs, true, list); Disjuncts(rhs, true, list); }
        else if (condition.MatchLogicNot(out inner) && (negated || inner.MatchLogicAnd(out lhs, out rhs))) Disjuncts(inner, !negated, list);
        else list.Add(new KeyValuePair<ILInstruction, bool>(condition, negated));
    }

    // ---- 30 ----
    // One variable per slot. The compiler gives every variable of the source a slot of its own
    // and only shares the slots of its temporaries; the decompiler splits a slot into one
    // variable per run of uses ('string v = a; ...' in one switch section, 'string v2 = b; ...'
    // in the next, where the source had one 'v' for the whole switch). A temporary is read
    // right where it is stored ('stloc t; ldloc t', 'dup; stloc t') or through its address:
    // a slot whose stores are all read later, after something else, is a variable.
    private static void SlotVariables(ILFunction f, RawIL raw)
    {
        var bySlot = new Dictionary<int, List<ILVariable>>();
        foreach (ILVariable v in f.Variables)
        {
            if (v.Kind != VariableKind.Local || !v.Index.HasValue || v.Function != f || v.StoreCount + v.LoadCount + v.AddressCount == 0) continue;
            List<ILVariable> list;
            if (!bySlot.TryGetValue(v.Index.Value, out list)) { list = new List<ILVariable>(); bySlot[v.Index.Value] = list; }
            list.Add(v);
        }
        foreach (var entry in bySlot)
        {
            List<ILVariable> webs = entry.Value;
            int slot = entry.Key;
            if (webs.Count < 2 || webs.Any(w => w.AddressCount != 0 || w.StoreCount == 0 || w.LoadCount == 0 || !w.Type.Equals(webs[0].Type))) continue;
            bool variable = true;
            int stores = 0, readLater = 0;
            for (int i = 0; i < raw.Ops.Count && variable; i++)
            {
                RawIL.Op op = raw.Ops[i];
                if ((op.Code == ILOpCode.Ldloca || op.Code == ILOpCode.Ldloca_s) && op.Operand == slot) variable = false;
                if (!RawIL.IsStLoc(op.Code) || op.Operand != slot) continue;
                stores++;
                if (i == 0 || i + 1 >= raw.Ops.Count) { variable = false; break; }
                RawIL.Op next = raw.Ops[i + 1];
                // A read that comes after something else was done (a jump, a store), and is not
                // one more test of a 'switch' ('ldloc t; ldc; beq'): what a temporary never has.
                bool apart = false;
                for (int k = i + 1; k < raw.Ops.Count; k++)
                {
                    RawIL.Op later = raw.Ops[k];
                    if (RawIL.IsStLoc(later.Code) && later.Operand == slot) break;
                    if (k > i + 1 && (RawIL.IsCondBranch(later.Code) || later.Code == ILOpCode.Br || later.Code == ILOpCode.Br_s || raw.Targets.Contains(later.Offset) || RawIL.IsStLoc(later.Code)
                        || later.Code == ILOpCode.Stfld || later.Code == ILOpCode.Stsfld || later.Code == ILOpCode.Pop)) apart = true;
                    if (!apart || !RawIL.IsLdLoc(later.Code) || later.Operand != slot || k + 2 >= raw.Ops.Count) continue;
                    bool tested = RawIL.IsLdcI4(raw.Ops[k + 1].Code) && (RawIL.IsCondBranch(raw.Ops[k + 2].Code) || raw.Ops[k + 2].Code == ILOpCode.Sub);
                    if (!tested && !RawIL.IsCondBranch(raw.Ops[k + 1].Code)) { readLater++; break; }
                }
                if (raw.Ops[i - 1].Code == ILOpCode.Dup) continue;
                // (Or read later with something pushed in between: no temporary is, outside the shapes below.)
                if (!(RawIL.IsLdLoc(next.Code) && next.Operand == slot)) readLater++;
                // The index of a 'foreach' over an array, and the copy of the array: 'stloc a; ldc.i4.0; stloc i; br'.
                if (next.Code == ILOpCode.Br || next.Code == ILOpCode.Br_s)
                {
                    RawIL.Op test, array, length;
                    if (!raw.IsAt(next.Operand, out test) || !RawIL.IsLdLoc(test.Code) || test.Operand != slot) variable = false;
                    else if (raw.Near(next.Operand, 1, out array) && RawIL.IsLdLoc(array.Code) && raw.Near(next.Operand, 2, out length) && length.Code == ILOpCode.Ldlen) variable = false;
                }
                if (RawIL.IsLdcI4(next.Code) && i + 3 < raw.Ops.Count && RawIL.IsStLoc(raw.Ops[i + 2].Code) && (raw.Ops[i + 3].Code == ILOpCode.Br || raw.Ops[i + 3].Code == ILOpCode.Br_s)) variable = false;
            }
            if (!variable || stores < 2 || readLater == 0) continue;
            // Every store is a statement of its own.
            var uses = RawIL.Own(f).Where(d => (d is StLoc && webs.Contains(((StLoc)d).Variable)) || (d is LdLoc && webs.Contains(((LdLoc)d).Variable))).ToList();
            if (uses.Count != webs.Sum(w => w.StoreCount + w.LoadCount)) continue;
            ILVariable kept = webs[0];
            foreach (ILInstruction use in uses)
            {
                var store = use as StLoc;
                if (store != null) { if (store.Variable != kept) store.Variable = kept; }
                else if (((LdLoc)use).Variable != kept) ((LdLoc)use).Variable = kept;
            }
        }
    }

    // ---- 29 ----
    // Labelled blocks in the order of the IL. What is left as 'goto' and labels (code of
    // another compiler, C++/CLI in XNA) is printed in the order the decompiler walked the
    // blocks; the compiler emits them as written.
    private static void BlocksInOrder(ILFunction f)
    {
        foreach (BlockContainer container in RawIL.Own(f).OfType<BlockContainer>().ToList())
        {
            if (container.Kind != ContainerKind.Normal || container.Blocks.Count < 3) continue;
            var rest = container.Blocks.Skip(1).ToList();
            if (rest.Any(b => b.StartILOffset <= 0) || rest.Select(b => b.StartILOffset).Distinct().Count() != rest.Count) continue;
            var sorted = rest.OrderBy(b => b.StartILOffset).ToList();
            if (sorted.SequenceEqual(rest)) continue;
            Block entry = container.Blocks[0];
            container.Blocks.Clear();
            container.Blocks.Add(entry);
            container.Blocks.AddRange(sorted);
        }
    }

    // ---- 23b ----
    //        if (a) { if (b) return; } else { return; }      IL: a; brfalse R2; b; brfalse L; ret; R2: ret; L:
    // in a void method is printed 'if (!a || b) return;' ('break;' in a loop that ends the
    // method). With '||' the test of a would jump to the 'ret' of b: here it jumps to a second
    // 'ret' right after it.
    private static void ElseOfFirstOperand(ILFunction f, RawIL raw)
    {
        if (f.ReturnType.Kind != TypeKind.Void || raw.Ops.Count == 0) return;
        var body = f.Body as BlockContainer;
        if (body == null) return;
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (test.Parent == null || !test.FalseInst.MatchNop() || !(test.Parent is Block) || ((Block)test.Parent).Kind != BlockKind.ControlFlow) continue;
            var exit = test.TrueInst as Leave;
            var inner = test.TrueInst as Block;
            if (exit == null && inner != null && inner.Instructions.Count == 1) exit = inner.Instructions[0] as Leave;
            if (exit == null || !exit.Value.MatchNop()) continue;
            var parts = new List<KeyValuePair<ILInstruction, bool>>();
            Disjuncts(test.Condition, false, parts);
            if (parts.Count != 2) continue;
            RawIL.Op first, last, own, second;
            int firstEnd = RawIL.EndOf(parts[0].Key), lastEnd = RawIL.EndOf(parts[1].Key);
            if (firstEnd < 0 || lastEnd < 0 || !AtOrBefore(raw, firstEnd, RawIL.IsCondBranch, out first)) continue;
            if (!AtOrBefore(raw, lastEnd, RawIL.IsCondBranch, out last) || last.Offset <= first.Offset || !raw.Near(last.Offset, 1, out own) || own.Code != ILOpCode.Ret) continue;
            if (first.Operand != own.End || !raw.IsAt(own.End, out second) || second.Code != ILOpCode.Ret || last.Operand == own.End || last.Operand == own.Offset) continue;
            if (RawIL.StartOf(parts[1].Key) < first.End) continue;
            ILInstruction a = Take(parts[0].Key), b = Take(parts[1].Key), positive;
            if (parts[0].Value) a = a.MatchLogicNot(out positive) ? Take(positive) : Comp.LogicNot(a);
            if (parts[1].Value) b = b.MatchLogicNot(out positive) ? Take(positive) : Comp.LogicNot(b);
            var nested = new Block();
            nested.Instructions.Add(new IfInstruction(b, new Leave(body)));
            var otherwise = new Block();
            otherwise.Instructions.Add(new Leave(body));
            test.ReplaceWith(new IfInstruction(Negate(a), nested, otherwise));
        }
    }

    private static void NestFirstOperand(ILFunction f, RawIL raw)
    {
        if (f.ReturnType.Kind != TypeKind.Void || raw.Ops.Count == 0) return;
        var body = f.Body as BlockContainer;
        if (body == null || body.Blocks.Count != 1) return;
        Block block = body.EntryPoint;
        RawIL.Op lastOp = raw.Ops[raw.Ops.Count - 1];
        if (lastOp.Code != ILOpCode.Ret) return;
        for (int k = 0; k + 1 < block.Instructions.Count; k++)
        {
            var test = block.Instructions[k] as IfInstruction;
            if (test == null || !test.FalseInst.MatchNop()) continue;
            var exit = test.TrueInst as Leave;
            var inner = test.TrueInst as Block;
            if (exit == null && inner != null && inner.Instructions.Count == 1) exit = inner.Instructions[0] as Leave;
            if (exit == null || !exit.IsLeavingFunction || !exit.Value.MatchNop()) continue;
            var parts = new List<KeyValuePair<ILInstruction, bool>>();
            Disjuncts(test.Condition, false, parts);
            if (parts.Count < 2) continue;
            // The test of the first operand jumps to the last 'ret'; the one of the last operand
            // jumps over a 'ret' of its own.
            RawIL.Op first, last, own;
            int firstEnd = RawIL.EndOf(parts[0].Key), lastEnd = RawIL.EndOf(parts[parts.Count - 1].Key);
            if (firstEnd < 0 || lastEnd < 0 || !AtOrBefore(raw, firstEnd, RawIL.IsCondBranch, out first) || first.Operand != lastOp.Offset) continue;
            if (!AtOrBefore(raw, lastEnd, RawIL.IsCondBranch, out last) || !raw.Near(last.Offset, 1, out own) || own.Code != ILOpCode.Ret || last.Operand != own.End) continue;
            if (RawIL.StartOf(parts[1].Key) < first.End) continue;
            // Everything up to the end of the method goes inside.
            int n = block.Instructions.Count;
            var tail = block.Instructions[n - 1] as Leave;
            int end = tail != null && tail.IsLeavingFunction && tail.Value.MatchNop() ? n - 1 : n;
            if (end <= k + 1) continue;
            var operands = new List<ILInstruction>();
            foreach (var part in parts)
            {
                ILInstruction operand = Take(part.Key), positive;
                if (part.Value) operand = operand.MatchLogicNot(out positive) ? Take(positive) : Comp.LogicNot(operand);
                operands.Add(operand);
            }
            ILInstruction rest = operands[1];
            for (int i = 2; i < operands.Count; i++) rest = IfInstruction.LogicOr(rest, operands[i]);
            var moved = new List<ILInstruction>();
            for (int i = k + 1; i < end; i++) moved.Add(block.Instructions[i]);
            block.Instructions.RemoveRange(k, end - k);
            var nested = new Block();
            nested.Instructions.Add(new IfInstruction(rest, new Leave(exit.TargetContainer)));
            nested.Instructions.AddRange(moved);
            block.Instructions.Insert(k, new IfInstruction(Negate(operands[0]), nested));
            return;
        }
    }

    // ---- 24 ----
    // Loop on a length that may be missing.
    //        for (int i = 0; i < (a?.Length ?? 0); i++)      IL: ldloc i; a; dup; brtrue N; pop; ldc.i4.0; br T; N: ldlen; conv.i4; T: blt BODY
    // is printed 'while (true) { int n = i; T[] t = a; if (n >= ((t != null) ? t.Length : 0)) break; ... }'.
    private static void NullConditionalLoops(ILFunction f, RawIL raw)
    {
        foreach (BlockContainer loop in RawIL.Own(f).OfType<BlockContainer>().ToList())
        {
            if (loop.Kind != ContainerKind.Loop || loop.Blocks.Count != 1) continue;
            Block header = loop.EntryPoint;
            if (header.Instructions.Count < 5) continue;
            var left = header.Instructions[0] as StLoc;
            var array = header.Instructions[1] as StLoc;
            var test = header.Instructions[2] as IfInstruction;
            var back = header.Instructions[header.Instructions.Count - 1] as Branch;
            if (left == null || array == null || test == null || back == null || back.TargetBlock != header || !test.FalseInst.MatchNop()) continue;
            if (loop.Descendants.OfType<Branch>().Count(b => b.TargetBlock == header) != 1) continue;
            ILVariable s0 = left.Variable, s1 = array.Variable;
            if (s0.Kind != VariableKind.StackSlot || s1.Kind != VariableKind.StackSlot || s0.LoadCount != 1 || s1.LoadCount != 2 || s0.StoreCount != 1 || s1.StoreCount != 1) continue;
            if (!(left.Value is LdLoc)) continue;
            var exit = test.TrueInst as Leave;
            var exitBlock = test.TrueInst as Block;
            if (exit == null && exitBlock != null && exitBlock.Instructions.Count == 1) exit = exitBlock.Instructions[0] as Leave;
            if (exit == null || exit.TargetContainer != loop || !exit.Value.MatchNop()) continue;
            var compare = test.Condition as Comp;
            if (compare == null || compare.Kind != ComparisonKind.GreaterThanOrEqual || !compare.Left.MatchLdLoc(s0)) continue;
            var length = compare.Right as IfInstruction;
            if (length == null || !length.FalseInst.MatchLdcI4(0)) continue;
            var notNull = length.Condition as Comp;
            var counted = length.TrueInst as LdLen;
            if (notNull == null || notNull.Kind != ComparisonKind.Inequality || !notNull.Left.MatchLdLoc(s1) || !notNull.Right.MatchLdNull() || counted == null || !counted.Array.MatchLdLoc(s1)) continue;
            RawIL.Op dup, branch, pop, zero;
            int end = RawIL.EndOf(array.Value);
            if (end < 0 || !AtOrBefore(raw, end, c => c == ILOpCode.Dup, out dup)) continue;
            if (!raw.Near(dup.Offset, 1, out branch) || (branch.Code != ILOpCode.Brtrue && branch.Code != ILOpCode.Brtrue_s)) continue;
            if (!raw.Near(dup.Offset, 2, out pop) || pop.Code != ILOpCode.Pop) continue;
            if (!raw.Near(dup.Offset, 3, out zero) || zero.Code != ILOpCode.Ldc_i4_0) continue;

            ILInstruction index = left.Value, value = array.Value;
            left.Value = new Nop();
            array.Value = new Nop();
            var coalesce = new NullCoalescingInstruction(NullCoalescingKind.NullableWithValueFallback, new NullableRewrap(new LdLen(StackType.I4, new NullableUnwrap(StackType.O, value))), new LdcI4(0));
            coalesce.UnderlyingResultType = StackType.I4;
            var condition = new Comp(ComparisonKind.LessThan, compare.Sign, index, coalesce);
            var statements = header.Instructions.Skip(3).ToList();
            header.Instructions.Clear();
            var body = new Block();
            body.Instructions.AddRange(statements);
            loop.Blocks.Add(body);
            header.Instructions.Add(new IfInstruction(condition, new Branch(body), new Leave(loop)));
            loop.Kind = ContainerKind.While;
        }
    }

    // ---- 25 ----
    // Flag kept on the stack across a statement.
    //        bool b = false;  T v = e;  b |= x;  b |= y;  if (b)      IL: ldc.i4.0; e; stloc v; x; or; y; or; brfalse
    // is printed 'T v = e; if (false | x | y)': the constant is in the IL before e.
    private static void StackedFlags(ILFunction f, RawIL raw, ILTransformContext context)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test)) continue;
            var operands = new List<ILInstruction>();
            ILInstruction spine = test.Condition;
            BinaryNumericInstruction or;
            while ((or = spine as BinaryNumericInstruction) != null && or.Operator == BinaryNumericOperator.BitOr) { operands.Insert(0, or.Right); spine = or.Left; }
            if (operands.Count < 2 || !spine.MatchLdcI4(0)) continue;
            int constant = -1;
            RawIL.Op op;
            foreach (var r in spine.ILRanges) if (raw.IsAt(r.Start, out op) && op.Code == ILOpCode.Ldc_i4_0) constant = r.Start;
            if (constant < 0) continue;
            // The statements before the test that the IL has after the constant.
            var block = (Block)test.Parent;
            int from = test.ChildIndex;
            while (from > 0 && block.Instructions[from - 1] is StLoc && RawIL.StartOf(block.Instructions[from - 1]) > constant) from--;
            if (from == test.ChildIndex) continue;
            if (operands.Any(o => !(o is CallInstruction) || o.ResultType != StackType.I4)) continue;
            ILVariable v = f.RegisterVariable(VariableKind.Local, context.TypeSystem.FindType(KnownTypeCode.Boolean));
            foreach (ILInstruction operand in operands) operand.ReplaceWith(new Nop());
            test.Condition = new LdLoc(v);
            int at = test.ChildIndex;
            for (int i = 0; i < operands.Count; i++)
                block.Instructions.Insert(at + i, new StLoc(v, new BinaryNumericInstruction(BinaryNumericOperator.BitOr, new LdLoc(v), operands[i], false, Sign.None)));
            block.Instructions.Insert(from, new StLoc(v, new LdcI4(0)));
        }
    }

    // ---- 26 ----
    // Array initializer cut short.
    //        new T[n] { a, b, ((int)e).ToString(), c }
    // is printed 'T[] x = new T[n] { a, b, null, null }; x[2] = ...; x[3] = c;' when an element
    // goes through a temporary: the decompiler stops reading the initializer there. In the IL
    // every element is stored through a 'dup' of the array.
    private static void ArrayInitializerTails(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            var init = store.Value as Block;
            if (init == null || init.Kind != BlockKind.ArrayInitializer || !IsStatement(store) || init.Instructions.Count < 2) continue;
            var created = init.Instructions[0] as StLoc;
            if (created == null || !(created.Value is NewArr)) continue;
            ILVariable s = store.Variable, temp = created.Variable;
            if (s.StoreCount != 1 || s.AddressCount != 0) continue;
            // Index -> the element of the initializer.
            var elements = new Dictionary<int, StObj>();
            bool simple = true;
            for (int i = 1; i < init.Instructions.Count && simple; i++)
            {
                var element = init.Instructions[i] as StObj;
                var place = element == null ? null : element.Target as LdElema;
                int index;
                if (place == null || place.Indices.Count != 1 || !place.Array.MatchLdLoc(temp) || !place.Indices[0].MatchLdcI4(out index)) simple = false;
                else elements[index] = element;
            }
            if (!simple) continue;
            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            bool merged = false;
            while (at + 1 < block.Instructions.Count)
            {
                var assign = block.Instructions[at + 1] as StObj;
                var place = assign == null ? null : assign.Target as LdElema;
                int index;
                StObj element;
                if (place == null || place.Indices.Count != 1 || !place.Array.MatchLdLoc(s) || !place.Indices[0].MatchLdcI4(out index)) break;
                if (!elements.TryGetValue(index, out element) || !(element.Value is DefaultValue || element.Value is LdNull)) break;
                if (assign.Value.Descendants.OfType<LdLoc>().Any(l => l.Variable == s) || assign.Value.MatchLdLoc(s)) break;
                // 'dup; ldc index' in front of the value.
                int start = RawIL.StartOf(assign);
                RawIL.Op first, before;
                if (start < 0 || !raw.IsAt(start, out first)) break;
                if (first.Code != ILOpCode.Dup && !(raw.Near(start, -1, out before) && before.Code == ILOpCode.Dup)) break;
                ILInstruction value = assign.Value;
                assign.Value = new Nop();
                element.Value = value;
                block.Instructions.RemoveAt(at + 1);
                merged = true;
            }
            if (!merged) continue;
            // Read once, first thing in the next statement: the array was never a variable.
            if (s.Kind != VariableKind.StackSlot || s.LoadCount != 1 || at + 1 >= block.Instructions.Count) continue;
            ILInstruction next = block.Instructions[at + 1];
            ILInstruction leaf = next.Descendants.FirstOrDefault();
            if (leaf == null || !leaf.MatchLdLoc(s)) continue;
            store.Value = new Nop();
            leaf.ReplaceWith(init);
            block.Instructions.RemoveAt(at);
        }
    }

    // ---- 27 ----
    //        if (c) { T }  else { E }              at the end of a void method
    // where T ends with 'if (x) { ... }': the test of x jumps to the last 'ret' of the method.
    // Printed 'if (c) { T  return; }  E', that test would jump to the 'ret' that ends T.
    private static void ReturnToElse(ILFunction f, RawIL raw)
    {
        if (f.ReturnType.Kind != TypeKind.Void || raw.Ops.Count == 0) return;
        var body = f.Body as BlockContainer;
        if (body == null || body.Blocks.Count != 1) return;
        Block block = body.EntryPoint;
        RawIL.Op lastOp = raw.Ops[raw.Ops.Count - 1];
        if (lastOp.Code != ILOpCode.Ret) return;
        for (int k = 0; k + 1 < block.Instructions.Count; k++)
        {
            var test = block.Instructions[k] as IfInstruction;
            var then = test == null ? null : test.TrueInst as Block;
            if (then == null || !test.FalseInst.MatchNop() || then.Instructions.Count < 2) continue;
            var exit = then.Instructions[then.Instructions.Count - 1] as Leave;
            var inner = then.Instructions[then.Instructions.Count - 2] as IfInstruction;
            if (exit == null || !exit.IsLeavingFunction || !exit.Value.MatchNop() || inner == null || !inner.FalseInst.MatchNop()) continue;
            if (inner.TrueInst.HasFlag(InstructionFlags.EndPointUnreachable)) continue;
            RawIL.Op branch;
            int end = RawIL.EndOf(inner.Condition);
            if (end < 0 || !AtOrBefore(raw, end, RawIL.IsCondBranch, out branch) || branch.Operand != lastOp.Offset) continue;
            // What follows goes up to the end of the method, and has a place in the IL.
            int n = block.Instructions.Count;
            var tail = block.Instructions[n - 1] as Leave;
            int last = tail != null && tail.IsLeavingFunction && tail.Value.MatchNop() ? n - 1 : n;
            if (last <= k + 1) continue;
            int rest = StartOfCode(block.Instructions[k + 1]);
            if (rest < 0 || rest <= branch.Offset || rest >= lastOp.Offset) continue;
            then.Instructions.RemoveAt(then.Instructions.Count - 1);
            var moved = new List<ILInstruction>();
            for (int i = k + 1; i < last; i++) moved.Add(block.Instructions[i]);
            block.Instructions.RemoveRange(k + 1, last - k - 1);
            var otherwise = new Block();
            otherwise.Instructions.AddRange(moved);
            test.FalseInst = otherwise;
            return;
        }
    }

    // ---- 28 ----
    //        v = e as T;  if (v != null)           IL: e; isinst T; stloc v; ldloc v; brfalse
    // is printed 'if (e is T x)' with a variable of its own. A pattern variable has its slot
    // for itself: when the slot is stored elsewhere too, it is the variable that is stored there.
    private static void SharedPatternSlots(ILFunction f, RawIL raw)
    {
        foreach (MatchInstruction match in RawIL.Own(f).OfType<MatchInstruction>().ToList())
        {
            ILVariable p = match.Variable;
            if (!p.Index.HasValue || !match.CheckType || !match.CheckNotNull || match.IsDeconstructCall || match.SubPatterns.Count != 0) continue;
            var test = match.Parent as IfInstruction;
            if (test == null || test.Condition != match) continue;
            var outerTest = test.Parent as IfInstruction;
            if (!IsStatement(test) && (outerTest == null || outerTest.FalseInst != test || !IsStatement(outerTest))) continue;
            int slot = p.Index.Value, stores = 0;
            foreach (RawIL.Op op in raw.Ops) if (RawIL.IsStLoc(op.Code) && op.Operand == slot) stores++;
            if (stores < 2) continue;
            var others = f.Variables.Where(x => x != p && x.Kind == VariableKind.Local && x.Index == slot && x.Type.Equals(p.Type) && (x.StoreCount + x.LoadCount) > 0).ToList();
            // (Split by the decompiler, the variable of a slot is printed as one all the same.)
            if (others.Count == 0) continue;
            ILVariable v = others[0];
            if (!IsStatement(test))
            {
                // 'else if': the assignment needs a block of its own.
                var wrap = new Block();
                outerTest.FalseInst = wrap;
                wrap.Instructions.Add(test);
            }
            var block = (Block)test.Parent;
            ILInstruction operand = match.TestedOperand;
            match.TestedOperand = new Nop();
            foreach (LdLoc use in f.Descendants.OfType<LdLoc>().Where(l => l.Variable == p).ToList()) use.Variable = v;
            test.Condition = new Comp(ComparisonKind.Inequality, Sign.None, new LdLoc(v), new LdNull());
            block.Instructions.Insert(test.ChildIndex, new StLoc(v, new IsInst(operand, p.Type)));
        }
    }
}

// IL transform: the rules of RestoreShapes that the other transforms of this project build on.
// Runs before them.
//
// 1. Variable assigned in both branches, then passed as an argument.
//        if (c) v = a; else v = b;  x = F(p, q, v);       IL: c; brfalse E; a; stloc v; br N; E: b; stloc v; N: p; q; ldloc v; call F
//    is printed 'x = F(arg2: c ? a : b, arg0: p, arg1: q);'. A conditional expression as an
//    argument stores once, after its two values meet.
internal sealed class RestoreShapesEarly : IILTransform
{
    public void Run(ILFunction function, ILTransformContext context)
    {
        var functions = function.Descendants.OfType<ILFunction>().ToList();
        functions.Add(function);
        foreach (ILFunction f in functions)
        {
            if (f.Method == null) continue;
            RawIL raw = RawIL.Read(f, context);
            if (raw == null) continue;
            NamedConditionals(f, raw);
            NamedStructs(f, raw);
            AssignedConditionals(f, raw);
            LongSwitches(f);
        }
    }

    private static bool LongConstant(ILInstruction inst, out long value)
    {
        if (inst.MatchLdcI8(out value)) return true;
        var widened = inst as Conv;
        int narrow;
        if (widened != null && widened.TargetType == PrimitiveType.I8 && widened.Argument.MatchLdcI4(out narrow)) { value = narrow; return true; }
        value = 0;
        return false;
    }

    // Switch on a 64-bit value.
    //        switch (l) { case -1: A; case 0: B; ... }
    //    IL: l; -1; sub; dup; 9; ble.un T; pop; br DEFAULT; T: conv.u4; switch (...)
    //    is printed 'long n = l - -1; switch (n) { case 0: ... case 9: switch (n) { case 0: A; ... } }':
    //    the decompiler does not join the range test and the table.
    private static void LongSwitches(ILFunction f)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test) || test.Parent == null || !test.FalseInst.MatchNop() || test.ChildIndex == 0) continue;
            var range = test.Condition as Comp;
            long last, lowest = 0;
            if (range == null || range.Kind != ComparisonKind.LessThanOrEqual || range.Sign != Sign.Unsigned || !LongConstant(range.Right, out last)) continue;
            var held = range.Left as LdLoc;
            var then = test.TrueInst as Block;
            var container = then == null || then.Instructions.Count != 1 ? null : then.Instructions[0] as BlockContainer;
            if (held == null || container == null || container.Kind != ContainerKind.Switch || container.EntryPoint.Instructions.Count != 1) continue;
            var sw = container.EntryPoint.Instructions[0] as SwitchInstruction;
            var narrowed = sw == null ? null : sw.Value as Conv;
            if (narrowed == null || narrowed.InputType != StackType.I8 || !narrowed.Argument.MatchLdLoc(held.Variable)) continue;
            ILVariable v = held.Variable;
            var block = (Block)test.Parent;
            var store = block.Instructions[test.ChildIndex - 1] as StLoc;
            if (store == null || store.Variable != v || v.StoreCount != 1 || v.LoadCount != 2 || v.AddressCount != 0) continue;
            ILInstruction subject = store.Value;
            var shifted = subject as BinaryNumericInstruction;
            if (shifted != null && shifted.Operator == BinaryNumericOperator.Sub && LongConstant(shifted.Right, out lowest)) subject = shifted.Left;
            else lowest = 0;
            SwitchSection fallback = sw.Sections.FirstOrDefault(s => s.Labels.Contains(long.MinValue));
            var leave = fallback == null ? null : fallback.Body as Leave;
            if (leave == null || leave.TargetContainer != container || !leave.Value.MatchNop() || fallback.HasNullLabel) continue;
            if (sw.Sections.Any(s => s != fallback && (s.HasNullLabel || s.Labels.IsEmpty || s.Labels.Values.Any(x => x < 0 || x > last)))) continue;
            var taken = ICSharpCode.Decompiler.Util.LongSet.Empty;
            foreach (SwitchSection section in sw.Sections)
            {
                if (section == fallback) continue;
                section.Labels = section.Labels.AddOffset(lowest);
                taken = taken.UnionWith(section.Labels);
            }
            fallback.Labels = taken.Invert();
            if (shifted != null && subject == shifted.Left) shifted.Left = new Nop(); else store.Value = new Nop();
            sw.Value = subject;
            then.Instructions.Clear();
            test.ReplaceWith(container);
            block.Instructions.RemoveAt(store.ChildIndex);
        }
    }

    private static bool IsStatement(ILInstruction inst)
    {
        var block = inst.Parent as Block;
        return block != null && block.Kind == BlockKind.ControlFlow && inst.SlotInfo == Block.InstructionSlot;
    }

    // The statement that evaluates 'node' before anything else; null when there is none.
    private static ILInstruction HeldFirst(ILInstruction node)
    {
        ILInstruction statement = node;
        while (statement != null && !IsStatement(statement))
        {
            ILInstruction p = statement.Parent;
            if (p is Block && ((Block)p).Kind != BlockKind.ControlFlow) return null;
            if (p is BlockContainer || p is ILFunction) return null;
            statement = p;
        }
        if (statement == null) return null;
        ILInstruction first = statement == node ? node : statement.Descendants.FirstOrDefault();
        if (first != node && (first == null || !first.IsDescendantOf(node))) return null;
        return statement;
    }

    private static bool StoreAt(RawIL raw, int offset, out RawIL.Op op)
    {
        if (raw.IsAt(offset, out op) && RawIL.IsStLoc(op.Code)) return true;
        if (raw.EndsAt(offset, out op) && RawIL.IsStLoc(op.Code)) return true;
        return false;
    }

    private static void NamedConditionals(ILFunction f, RawIL raw)
    {
        foreach (Block named in RawIL.Own(f).OfType<Block>().ToList())
        {
            if (named.Kind != BlockKind.CallWithNamedArgs || named.Parent == null || named.Instructions.Count == 0) continue;
            var call = named.FinalInstruction as CallInstruction;
            var store = named.Instructions[0] as StLoc;
            if (call == null || store == null) continue;
            // The block is the first thing its statement evaluates.
            ILInstruction statement = HeldFirst(named);
            if (statement == null) continue;
            var choice = store.Value as IfInstruction;
            if (choice == null || choice.ResultType == StackType.Void) continue;
            int t = RawIL.StartOf(choice.TrueInst), e = RawIL.StartOf(choice.FalseInst);
            if (t < 0 || e < 0 || t == e) continue;
            ILInstruction first = t < e ? choice.TrueInst : choice.FalseInst, second = t < e ? choice.FalseInst : choice.TrueInst;
            RawIL.Op s1, s2, jump;
            if (!StoreAt(raw, RawIL.EndOf(first), out s1)) continue;
            if (!raw.Near(s1.Offset, 1, out jump) || (jump.Code != ILOpCode.Br && jump.Code != ILOpCode.Br_s)) continue;
            if (!StoreAt(raw, RawIL.EndOf(second), out s2) || s2.Operand != s1.Operand || s2.Offset == s1.Offset || jump.Operand < s2.End) continue;
            IType type = raw.LocalType(s1.Operand);
            if (type == null || type.Kind == TypeKind.Unknown) continue;
            var uses = call.Descendants.OfType<LdLoc>().Where(l => l.Variable == store.Variable).ToList();
            if (uses.Count != 1) continue;

            ILVariable v = f.RegisterVariable(VariableKind.Local, type);
            var block = (Block)statement.Parent;
            store.Value = new Nop();
            named.Instructions.RemoveAt(0);
            uses[0].ReplaceWith(new LdLoc(v));
            if (named.Instructions.Count == 0)
            {
                named.FinalInstruction = new Nop();
                named.ReplaceWith(call);
            }
            block.Instructions.Insert(statement.ChildIndex, new StLoc(v, choice));
        }
    }

    // 2. Variable assigned in both branches, then assigned to a property.
    //        if (c) v = a; else v = b;  P = v;     IL: c; brfalse E; a; stloc v; br N; E: b; stloc v; N: ldloc v; call set_P
    //    is printed 'v = (P = c ? a : b);'.
    private static void AssignedConditionals(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            var inline = store.Value as Block;
            if (inline == null || inline.Kind != BlockKind.CallInlineAssign || !IsStatement(store) || inline.Instructions.Count != 1) continue;
            ILVariable v = store.Variable;
            if (v.Kind != VariableKind.Local || !v.Index.HasValue) continue;
            var setter = inline.Instructions[0] as CallInstruction;
            var final = inline.FinalInstruction as LdLoc;
            if (setter == null || final == null || setter.Arguments.Count == 0) continue;
            var inner = setter.Arguments[setter.Arguments.Count - 1] as StLoc;
            if (inner == null || inner.Variable != final.Variable) continue;
            var choice = inner.Value as IfInstruction;
            if (choice == null || choice.ResultType == StackType.Void) continue;
            bool plain = true;
            for (int i = 0; i + 1 < setter.Arguments.Count; i++)
                if (!(setter.Arguments[i] is LdLoc || setter.Arguments[i] is LdsFlda)) plain = false;
            if (!plain) continue;
            int t = RawIL.StartOf(choice.TrueInst), e = RawIL.StartOf(choice.FalseInst);
            if (t < 0 || e < 0 || t == e) continue;
            ILInstruction first = t < e ? choice.TrueInst : choice.FalseInst, second = t < e ? choice.FalseInst : choice.TrueInst;
            RawIL.Op s1, s2, jump;
            if (!StoreAt(raw, RawIL.EndOf(first), out s1) || s1.Operand != v.Index.Value) continue;
            if (!raw.Near(s1.Offset, 1, out jump) || (jump.Code != ILOpCode.Br && jump.Code != ILOpCode.Br_s)) continue;
            if (!StoreAt(raw, RawIL.EndOf(second), out s2) || s2.Operand != s1.Operand || s2.Offset == s1.Offset || jump.Operand < s2.End) continue;

            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            inner.Value = new Nop();
            inner.ReplaceWith(new LdLoc(v));
            inline.Instructions.Clear();
            store.Value = choice;
            block.Instructions.Insert(at + 1, setter);
        }
    }

    // 3. Struct built in a variable, then passed as an argument.
    //        T v = new T(a, b);  x = new U(p, q, v);          IL: ldloca v; a; b; call T::.ctor; p; q; ldloc v; newobj U
    //    is printed 'x = new U(arg2: new T(a, b), arg0: p, arg1: q);'. 'new T(a, b)' as an
    //    argument is a 'newobj', which leaves the value on the stack.
    private static void NamedStructs(ILFunction f, RawIL raw)
    {
        var built = new Dictionary<int, ILVariable>();   // slot -> its variable
        foreach (Block named in RawIL.Own(f).OfType<Block>().ToList())
        {
            if (named.Kind != BlockKind.CallWithNamedArgs || named.Parent == null || named.Instructions.Count == 0) continue;
            var call = named.FinalInstruction as CallInstruction;
            var store = named.Instructions[0] as StLoc;
            if (call == null || store == null) continue;
            ILInstruction statement = HeldFirst(named);
            if (statement == null) continue;
            var created = store.Value as NewObj;
            if (created == null || created.Method.DeclaringType.IsReferenceType != false) continue;
            // 'ldloca v' in front of the arguments, 'call .ctor' after them; v is read once, later.
            int start = RawIL.StartOf(created), end = RawIL.EndOf(created);
            RawIL.Op address = default(RawIL.Op), made;
            bool found = false;
            for (int back = 0; back <= 1 && !found && start >= 0; back++)
                found = raw.Near(start, -back, out address) && (address.Code == ILOpCode.Ldloca || address.Code == ILOpCode.Ldloca_s);
            if (!found || end < 0) continue;
            if (!(raw.IsAt(end, out made) && made.Code == ILOpCode.Call) && !(raw.EndsAt(end, out made) && made.Code == ILOpCode.Call)) continue;
            IMethod ctor = raw.Method(made);
            if (ctor == null || !ctor.IsConstructor) continue;
            // The slot is only built that way and read once each time: 'ldloca; ...; ldloc' in turn.
            // (Built several times, it is one variable assigned several times.)
            int slot = address.Operand, stores = 0, pending = 0, pairs = 0;
            bool alternate = true;
            foreach (RawIL.Op op in raw.Ops)
            {
                if ((op.Code == ILOpCode.Ldloca || op.Code == ILOpCode.Ldloca_s) && op.Operand == slot) { if (pending != 0) alternate = false; pending = 1; }
                if (RawIL.IsLdLoc(op.Code) && op.Operand == slot) { if (pending != 1) alternate = false; pending = 0; pairs++; }
                if (RawIL.IsStLoc(op.Code) && op.Operand == slot) stores++;
            }
            if (!alternate || pending != 0 || pairs == 0 || stores != 0) continue;
            IType type = raw.LocalType(slot);
            if (type == null || type.Kind == TypeKind.Unknown) continue;
            var uses = call.Descendants.OfType<LdLoc>().Where(l => l.Variable == store.Variable).ToList();
            if (uses.Count != 1) continue;

            ILVariable v;
            if (!built.TryGetValue(slot, out v)) { v = f.RegisterVariable(VariableKind.Local, type); built[slot] = v; }
            var block = (Block)statement.Parent;
            store.Value = new Nop();
            named.Instructions.RemoveAt(0);
            uses[0].ReplaceWith(new LdLoc(v));
            if (named.Instructions.Count == 0)
            {
                named.FinalInstruction = new Nop();
                named.ReplaceWith(call);
            }
            block.Instructions.Insert(statement.ChildIndex, new StLoc(v, created));
        }
    }
}