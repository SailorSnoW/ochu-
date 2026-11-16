// IL transform: single source forms read from the raw body (second parallel series).
//
// 1. Null-conditional test compared with true, inside an '||'.
//        if (a == null || a.f > 0 || e?.M() != true) { R }
//            IL: ...; e; dup; brtrue N; pop; ldc.i4.1; br T; N: call M; ldc.i4.0; ceq; T: brfalse SKIP; R; SKIP:
//    is printed 'if (a != null && !(a.f > 0)) { T v = e; if (v != null && v.M()) goto L; } R; L:',
//    which tests 'v.M()' by a jump of its own ('stloc v; ldloc v; brfalse').
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreForms : IILTransform
{
    public void Run(ILFunction function, ILTransformContext context)
    {
        var functions = function.Descendants.OfType<ILFunction>().ToList();
        functions.Add(function);
        foreach (ILFunction f in functions)
        {
            if (f.Method == null) continue;
            HashSet<string> set;
            if (!f.Method.MetadataToken.IsNil && Program.Hints.TryGetValue(System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(f.Method.MetadataToken), out set) && set.Contains("-RestoreForms")) continue;
            RawIL raw = RawIL.Read(f, context);
            if (raw == null) continue;
            NullConditionalNotTrue(f, raw);
            HandlerRetryLoops(f, raw);
            ReturnedOrAssignments(f, raw);
            StoredOrConditions(f, raw, context);
            SetterChains(f, raw);
            BreakGuards(f, raw);
            ReceiversInOr(f, raw);
            OwnSlotReceivers(f, raw);
        }
    }

    // 2. Loop in a handler whose body starts the protected block again.
    //        retry: try { } catch (E e) { while (c) { Y; goto retry; }  X }
    //            IL: stloc e; br C; B: Y; leave TRY; C: c; brtrue B; X; leave END
    //    is printed 'while (true) { try { } catch (E e) { if (!c) { X; break; } Y } }', which
    //    tests c first ('c; brtrue B' before X, then Y).
    private static void HandlerRetryLoops(ILFunction f, RawIL raw)
    {
        foreach (TryCatchHandler handler in RawIL.Own(f).OfType<TryCatchHandler>().ToList())
        {
            var container = handler.Body as BlockContainer;
            if (container == null || container.Blocks.Count != 1) continue;
            Block entry = container.EntryPoint;
            if (entry.Instructions.Count < 3) continue;
            var test = entry.Instructions[0] as IfInstruction;
            if (test == null || !test.FalseInst.MatchNop()) continue;
            var then = test.TrueInst as Block;
            if (then == null || then.Instructions.Count < 1 || !then.Instructions[then.Instructions.Count - 1].HasFlag(InstructionFlags.EndPointUnreachable)) continue;
            var end = entry.Instructions[entry.Instructions.Count - 1] as Leave;
            if (end == null || end.TargetContainer != container) continue;
            if (entry.Descendants.OfType<Leave>().Count(l => l.TargetContainer == container) != 1) continue;
            // The protected block is the body of an endless loop: leaving the handler starts it again.
            var protectedBlock = handler.Parent as TryCatch;
            var loopBlock = protectedBlock == null ? null : protectedBlock.Parent as Block;
            if (loopBlock == null || protectedBlock.ChildIndex != 0 || loopBlock.Instructions.Count != 2) continue;
            var again = loopBlock.Instructions[1] as Branch;
            if (again == null || again.TargetBlock != loopBlock) continue;
            int bodyStart = RawIL.StartOfCode(entry.Instructions[1]), conditionStart = RawIL.StartOf(test.Condition);
            RawIL.Op jump;
            if (bodyStart <= 0 || conditionStart < 0 || bodyStart >= conditionStart) continue;
            if (!raw.EndsAt(bodyStart, out jump) || (jump.Code != ILOpCode.Br && jump.Code != ILOpCode.Br_s) || jump.Operand != conditionStart) continue;

            ILInstruction condition = test.Condition;
            test.Condition = new Nop();
            var after = then.Instructions.ToList();
            then.Instructions.Clear();
            var statements = entry.Instructions.Skip(1).ToList();
            entry.Instructions.Clear();
            statements.RemoveAt(statements.Count - 1);
            var inner = new BlockContainer(ContainerKind.While);
            var header = new Block();
            var body = new Block();
            body.Instructions.AddRange(statements);
            body.Instructions.Add(new Branch(loopBlock));
            // Never reached: the statement builder takes a container for a loop when something
            // jumps back to its header, and drops a 'continue;' that ends the body.
            body.Instructions.Add(new Branch(header));
            ILInstruction positive;
            if (!condition.MatchLogicNot(out positive)) positive = Comp.LogicNot(condition); else ((Comp)condition).Left = new Nop();
            header.Instructions.Add(new IfInstruction(positive, new Branch(body), new Leave(inner)));
            inner.Blocks.Add(header);
            inner.Blocks.Add(body);
            entry.Instructions.Add(inner);
            entry.Instructions.AddRange(after);
        }
    }

    // 3. Value of an '||' assigned and returned.
    //        return x.f = a || b;      IL: x; a; brtrue T; b; br J; T: ldc.i4.1; J: dup; stloc t; stfld f; ldloc t; ret
    //    is printed 'if (!a) n = b; else n = 1; bool r = n != 0; x.f = n != 0; return r;'.
    private static void ReturnedOrAssignments(ILFunction f, RawIL raw)
    {
        foreach (IfInstruction choice in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            var block = choice.Parent as Block;
            if (block == null || choice.SlotInfo != Block.InstructionSlot) continue;
            int at = choice.ChildIndex;
            if (at + 4 != block.Instructions.Count) continue;
            var thenBlock = choice.TrueInst as Block;
            var elseBlock = choice.FalseInst as Block;
            if (thenBlock == null || elseBlock == null || thenBlock.Instructions.Count != 1 || elseBlock.Instructions.Count != 1) continue;
            var s1 = thenBlock.Instructions[0] as StLoc;
            var s2 = elseBlock.Instructions[0] as StLoc;
            if (s1 == null || s2 == null || s1.Variable != s2.Variable) continue;
            ILVariable s = s1.Variable;
            if (s.Kind != VariableKind.StackSlot || s.StoreCount != 2 || s.LoadCount != 2 || s.AddressCount != 0) continue;
            var copy = block.Instructions[at + 1] as StLoc;
            var assign = block.Instructions[at + 2] as StObj;
            var exit = block.Instructions[at + 3] as Leave;
            if (copy == null || assign == null || exit == null || !exit.IsLeavingFunction) continue;
            if (!copy.Value.MatchLdLoc(s) || !assign.Value.MatchLdLoc(s) || !exit.Value.MatchLdLoc(copy.Variable)) continue;
            if (copy.Variable.StoreCount != 1 || copy.Variable.LoadCount != 1 || copy.Variable.AddressCount != 0) continue;
            if (assign.Target.Descendants.Any(d => d.MatchLdLoc(s) || d.MatchLdLoc(copy.Variable))) continue;
            bool oneInElse = s2.Value.MatchLdcI4(1), oneInThen = s1.Value.MatchLdcI4(1);
            if (oneInElse == oneInThen) continue;
            // 'dup; stloc; stfld; ldloc; ret' ends the method, after the 'ldc.i4.1' of the '||'.
            int n = raw.Ops.Count;
            if (n < 7 || raw.Ops[n - 1].Code != ILOpCode.Ret || !RawIL.IsLdLoc(raw.Ops[n - 2].Code) || raw.Ops[n - 3].Code != ILOpCode.Stfld
                || !RawIL.IsStLoc(raw.Ops[n - 4].Code) || raw.Ops[n - 5].Code != ILOpCode.Dup || raw.Ops[n - 6].Code != ILOpCode.Ldc_i4_1) continue;

            ILInstruction condition = choice.Condition, other = oneInElse ? s1.Value : s2.Value;
            choice.Condition = new Nop();
            if (oneInElse) s1.Value = new Nop(); else s2.Value = new Nop();
            ILInstruction first;
            if (oneInElse)
            {
                ILInstruction positive;
                if (condition.MatchLogicNot(out positive)) { ((Comp)condition).Left = new Nop(); first = positive; }
                else first = Comp.LogicNot(condition);
            }
            else first = condition;
            assign.Value = IfInstruction.LogicOr(first, other);
            block.Instructions.RemoveRange(at, 3);
            exit.Value = assign;
        }
    }

    // 4. Value of an '||' that ends on a null-conditional comparison, kept, then tested.
    //        bool v = a || e?.f == k;  if (v) { T }  R
    //            IL: a; brtrue ONE; e; dup; brtrue N; pop; ldc.i4.0; br J; N: ldfld f; k; ceq; br J; ONE: ldc.i4.1; J: brfalse R; T; R:
    //    is printed 'if (!a) { U t = e; if (t == null || t.f != k) { R } }  T', which jumps on
    //    each operand: 'if (a || e?.f == k)' has no 'ldc.i4.1'.
    private static void StoredOrConditions(ILFunction f, RawIL raw, ILTransformContext context)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            ILVariable s = store.Variable;
            if (s.Kind != VariableKind.StackSlot || s.LoadCount != 2 || s.StoreCount != 1 || s.AddressCount != 0) continue;
            var inner = store.Parent as Block;
            if (inner == null || store.SlotInfo != Block.InstructionSlot || inner.Instructions.Count != 2 || store.ChildIndex != 0) continue;
            var second = inner.Instructions[1] as IfInstruction;
            if (second == null || !second.FalseInst.MatchNop()) continue;
            var rest = second.TrueInst as Block;
            if (rest == null || rest.Instructions.Count == 0 || !rest.Instructions[rest.Instructions.Count - 1].HasFlag(InstructionFlags.EndPointUnreachable)) continue;
            var test = second.Condition as IfInstruction;
            if (test == null || !test.TrueInst.MatchLdcI4(1)) continue;
            var isNull = test.Condition as Comp;
            if (isNull == null || isNull.Kind != ComparisonKind.Equality || !isNull.Right.MatchLdNull() || !isNull.Left.MatchLdLoc(s)) continue;
            var differs = test.FalseInst as Comp;
            if (differs == null || differs.Kind != ComparisonKind.Inequality || differs.LiftingKind != ComparisonLiftingKind.None) continue;
            var uses = differs.Left.Descendants.OfType<LdLoc>().Where(l => l.Variable == s).ToList();
            if (uses.Count != 1 || differs.Right.Descendants.Any(d => d.MatchLdLoc(s))) continue;
            var guard = inner.Parent as IfInstruction;
            if (guard == null || guard.TrueInst != inner || !guard.FalseInst.MatchNop()) continue;
            var block = guard.Parent as Block;
            if (block == null || guard.SlotInfo != Block.InstructionSlot || guard.ChildIndex + 1 >= block.Instructions.Count) continue;
            if (!block.Instructions[block.Instructions.Count - 1].HasFlag(InstructionFlags.EndPointUnreachable)) continue;
            var loop = block.Parent as BlockContainer;
            if (loop == null || loop.Kind != ContainerKind.Normal) continue;

            // The jump of the first operand lands on 'ldc.i4.1; brfalse'.
            RawIL.Op jump, one, branch, dup, b2, pop, zero;
            int end = RawIL.EndOf(guard.Condition);
            if (end < 0) continue;
            if (!(raw.IsAt(end, out jump) && RawIL.IsCondBranch(jump.Code)) && !(raw.EndsAt(end, out jump) && RawIL.IsCondBranch(jump.Code))) continue;
            if (!raw.IsAt(jump.Operand, out one) || one.Code != ILOpCode.Ldc_i4_1) continue;
            if (!raw.Near(one.Offset, 1, out branch) || (branch.Code != ILOpCode.Brfalse && branch.Code != ILOpCode.Brfalse_s)) continue;
            end = RawIL.EndOf(store.Value);
            if (end < 0) continue;
            if (!(raw.IsAt(end, out dup) && dup.Code == ILOpCode.Dup) && !(raw.EndsAt(end, out dup) && dup.Code == ILOpCode.Dup)) continue;
            if (!raw.Near(dup.Offset, 1, out b2) || !IsBrTrue(b2.Code)) continue;
            if (!raw.Near(dup.Offset, 2, out pop) || pop.Code != ILOpCode.Pop) continue;
            if (!raw.Near(dup.Offset, 3, out zero) || zero.Code != ILOpCode.Ldc_i4_0) continue;

            ILInstruction value = store.Value;
            store.Value = new Nop();
            uses[0].ReplaceWith(new NullableUnwrap(StackType.O, value));
            ILInstruction left = differs.Left, right = differs.Right;
            differs.Left = new Nop();
            differs.Right = new Nop();
            var equal = new Comp(ComparisonKind.Equality, ComparisonLiftingKind.CSharp, differs.InputType, differs.Sign, new NullableRewrap(left), right);
            ILInstruction a = guard.Condition, positive;
            guard.Condition = new Nop();
            if (a.MatchLogicNot(out positive)) ((Comp)a).Left = new Nop(); else positive = Comp.LogicNot(a);
            ILVariable v = f.RegisterVariable(VariableKind.Local, context.TypeSystem.FindType(KnownTypeCode.Boolean));
            int at = guard.ChildIndex;
            var tail = block.Instructions.Skip(at + 1).ToList();
            block.Instructions.RemoveRange(at, block.Instructions.Count - at);
            var then = new Block();
            then.Instructions.AddRange(tail);
            var after = rest.Instructions.ToList();
            rest.Instructions.Clear();
            block.Instructions.Add(new StLoc(v, IfInstruction.LogicOr(positive, equal)));
            block.Instructions.Add(new IfInstruction(new LdLoc(v), then));
            block.Instructions.AddRange(after);
        }
    }

    // 5. Variable assigned, then passed to a setter.
    //        v = e;  x.P = v;          IL: e; stloc v; x; ldloc v; call set_P
    //        x.P = ++v;                IL: x; ldloc v; ldc.i4.1; add; dup; stloc v; call set_P
    //    are both printed 'v = (x.P = e);' ('x; e; dup; stloc t; call set_P; ldloc t; stloc v').
    private static void SetterChains(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            var chain = store.Value as Block;
            var block = store.Parent as Block;
            if (chain == null || chain.Kind != BlockKind.CallInlineAssign || chain.Instructions.Count != 1) continue;
            if (block == null || block.Kind != BlockKind.ControlFlow || store.SlotInfo != Block.InstructionSlot) continue;
            ILVariable v = store.Variable;
            if (v.Kind != VariableKind.Local || !v.Index.HasValue) continue;
            var call = chain.Instructions[0] as CallInstruction;
            if (call == null || call.Arguments.Count < 1) continue;
            var inner = call.Arguments[call.Arguments.Count - 1] as StLoc;
            if (inner == null || !chain.FinalInstruction.MatchLdLoc(inner.Variable) || inner.Variable.LoadCount != 1 || inner.Variable.StoreCount != 1) continue;
            bool plain = true;
            for (int i = 0; i + 1 < call.Arguments.Count; i++) if (!(call.Arguments[i] is LdLoc)) plain = false;
            if (!plain) continue;
            int end = RawIL.EndOf(inner.Value);
            RawIL.Op op, next;
            if (end < 0 || !raw.IsAt(end, out op)) continue;
            bool before = RawIL.IsStLoc(op.Code) && op.Operand == v.Index.Value;
            bool inside = op.Code == ILOpCode.Dup && raw.Near(op.Offset, 1, out next) && RawIL.IsStLoc(next.Code) && next.Operand == v.Index.Value
                && raw.Near(op.Offset, 2, out next) && RawIL.IsCall(next.Code);
            // 'T v = (x.P = e);' takes v itself for the copy ('dup; stloc v; call'): the second
            // form only when e reads v, which is then no declaration.
            if (inside && !inner.Value.Descendants.Any(d => d.MatchLdLoc(v))) inside = false;
            if (!before && !inside) continue;
            ILInstruction value = inner.Value;
            inner.Value = new Nop();
            chain.Instructions.Clear();
            int at = store.ChildIndex;
            if (before)
            {
                call.Arguments[call.Arguments.Count - 1] = new LdLoc(v);
                block.Instructions[at] = new StLoc(v, value);
                block.Instructions.Insert(at + 1, call);
            }
            else
            {
                call.Arguments[call.Arguments.Count - 1] = new StLoc(v, value);
                block.Instructions[at] = call;
            }
        }
    }

    private static bool IsLeave(ILOpCode c)
    {
        return c == ILOpCode.Leave || c == ILOpCode.Leave_s;
    }

    private static ILInstruction Negated(ILInstruction condition)
    {
        ILInstruction positive;
        if (condition.MatchLogicNot(out positive)) { ((Comp)condition).Left = new Nop(); return positive; }
        return Comp.LogicNot(condition);
    }

    // 6. 'break;' out of a protected block, before the rest of the body.
    //        foreach (...) { if (c) break;  R  break; }       IL: c; brfalse N; leave END; N: R; leave END
    //    is printed 'if (!c) { R } break;' ('c; brtrue E; R; E: leave END').
    private static void BreakGuards(ILFunction f, RawIL raw)
    {
        bool changed = true;
        for (int round = 0; changed && round < 20; round++)
        {
            changed = false;
            foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
            {
                var block = test.Parent as Block;
                if (block == null || block.Kind != BlockKind.ControlFlow || test.SlotInfo != Block.InstructionSlot || !test.FalseInst.MatchNop()) continue;
                var then = test.TrueInst as Block;
                if (then == null || then.Instructions.Count == 0 || test.ChildIndex + 2 != block.Instructions.Count) continue;
                var exit = block.Instructions[test.ChildIndex + 1] as Leave;
                if (exit == null || !exit.Value.MatchNop() || exit.IsLeavingFunction) continue;
                if (exit.TargetContainer.Kind == ContainerKind.Normal || exit.TargetContainer.Kind == ContainerKind.Switch) continue;
                int bodyStart = RawIL.StartOfCode(then.Instructions[0]);
                RawIL.Op leave, branch;
                if (bodyStart <= 0 || !raw.EndsAt(bodyStart, out leave)) continue;
                RawIL.Op init;
                if ((leave.Code == ILOpCode.Ldloca || leave.Code == ILOpCode.Ldloca_s) && raw.IsAt(bodyStart, out init) && init.Code == ILOpCode.Initobj)
                {
                    bodyStart = leave.Offset;
                    if (!raw.EndsAt(bodyStart, out leave)) continue;
                }
                // (A 'default(T?)' argument has no place in the tree: 'ldloca; initobj; ldloc' before the body.)
                RawIL.Op o1, o2;
                if (RawIL.IsLdLoc(leave.Code) && raw.Near(leave.Offset, -1, out o1) && o1.Code == ILOpCode.Initobj && raw.Near(leave.Offset, -2, out o2) && (o2.Code == ILOpCode.Ldloca || o2.Code == ILOpCode.Ldloca_s))
                {
                    bodyStart = o2.Offset;
                    if (!raw.EndsAt(bodyStart, out leave)) continue;
                }
                if (!IsLeave(leave.Code) || raw.RegionEnds.Contains(leave.End)) continue;
                if (!raw.Near(leave.Offset, -1, out branch) || !RawIL.IsCondBranch(branch.Code) || branch.Operand != bodyStart) continue;
                int conditionStart = RawIL.StartOf(test.Condition);
                if (conditionStart < 0 || conditionStart > branch.Offset) continue;

                ILInstruction condition = test.Condition;
                test.Condition = new Nop();
                var body = then.Instructions.ToList();
                then.Instructions.Clear();
                test.Condition = Negated(condition);
                test.TrueInst = new Leave(exit.TargetContainer);
                int at = test.ChildIndex + 1;
                if (body[body.Count - 1].HasFlag(InstructionFlags.EndPointUnreachable)) block.Instructions.RemoveAt(at);
                for (int i = 0; i < body.Count; i++) block.Instructions.Insert(at + i, body[i]);
                changed = true;
            }
        }
    }

    // 7. Struct built on its slot for a call, second operand of an '||'.
    //        if (a) continue;  S s = new S(x);  if (!s.M(y)) continue;
    //            IL: a; brtrue C; ldloca s; x; call S::.ctor; ldloca s; y; call M; brfalse C
    //    is printed 'if (a || !new S(x).M(y)) continue;': an operand has no place for the
    //    declaration, and 'new S(x).M(y)' is a 'newobj' stored in a temporary.
    private static void ReceiversInOr(ILFunction f, RawIL raw)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            var block = test.Parent as Block;
            if (block == null || block.Kind != BlockKind.ControlFlow || test.SlotInfo != Block.InstructionSlot || !test.FalseInst.MatchNop()) continue;
            if (!(test.TrueInst is Branch) && !(test.TrueInst is Leave && ((Leave)test.TrueInst).Value.MatchNop())) continue;
            var either = test.Condition as IfInstruction;
            if (either == null || !either.TrueInst.MatchLdcI4(1)) continue;
            ILInstruction second = either.FalseInst;
            var created = second.Descendants.OfType<NewObj>().FirstOrDefault();
            if (created == null || !(created.Parent is AddressOf) || created.Method.DeclaringType.Kind != TypeKind.Struct) continue;
            var receiverCall = created.Parent.Parent as CallInstruction;
            if (receiverCall == null || receiverCall.Arguments.Count == 0 || receiverCall.Arguments[0] != created.Parent) continue;
            ILInstruction negatedCall;
            if (receiverCall != second && !(second.MatchLogicNot(out negatedCall) && negatedCall == receiverCall)) continue;
            // The constructor is a 'call' on an address, not a 'newobj'.
            bool inPlace = false;
            foreach (var r in created.ILRanges)
            {
                RawIL.Op op;
                if (r.Start != r.End && raw.IsAt(r.Start, out op) && op.Code == ILOpCode.Call) inPlace = true;
            }
            if (!inPlace) continue;
            ILInstruction first = either.Condition;
            either.Condition = new Nop();
            either.FalseInst = new Nop();
            ILInstruction jump = test.TrueInst is Branch ? (ILInstruction)new Branch(((Branch)test.TrueInst).TargetBlock) : new Leave(((Leave)test.TrueInst).TargetContainer);
            test.Condition = second;
            block.Instructions.Insert(test.ChildIndex, new IfInstruction(first, jump));
        }
    }

    // 8. Values each stored in a slot of their own to call a method on them.
    //        TimeSpan a = x - y;  int n = (int)a.TotalSeconds;  ...  TimeSpan b = z - y;  int m = (int)b.TotalSeconds;
    //            IL: x; y; call op_Subtraction; stloc a; ldloca a; call get_TotalSeconds ... stloc b; ldloca b
    //    is printed '(int)(x - y).TotalSeconds', twice: the copies the compiler makes for such
    //    calls share one slot. Slots of one type, each stored once and read once, were variables.
    private static void OwnSlotReceivers(ILFunction f, RawIL raw)
    {
        var stores = new Dictionary<int, List<int>>();
        var addresses = new Dictionary<int, List<int>>();
        var loaded = new HashSet<int>();
        for (int i = 0; i < raw.Ops.Count; i++)
        {
            RawIL.Op op = raw.Ops[i];
            if (RawIL.IsLdLoc(op.Code)) { loaded.Add(op.Operand); continue; }
            Dictionary<int, List<int>> into = RawIL.IsStLoc(op.Code) ? stores : (op.Code == ILOpCode.Ldloca || op.Code == ILOpCode.Ldloca_s) ? addresses : null;
            if (into == null) continue;
            List<int> list;
            if (!into.TryGetValue(op.Operand, out list)) { list = new List<int>(); into[op.Operand] = list; }
            list.Add(i);
        }
        var candidates = new List<int>();
        foreach (var kv in stores)
        {
            List<int> read;
            if (kv.Value.Count != 1 || loaded.Contains(kv.Key) || !addresses.TryGetValue(kv.Key, out read) || read.Count != 1) continue;
            int s = kv.Value[0];
            if (s == 0 || read[0] != s + 1 || raw.Ops[s - 1].Code == ILOpCode.Dup || s + 2 >= raw.Ops.Count || raw.Ops[s + 2].Code != ILOpCode.Call) continue;
            IType type = raw.LocalType(kv.Key);
            if (type == null || type.Kind != TypeKind.Struct) continue;
            candidates.Add(kv.Key);
        }
        List<ILInstruction> all = null;
        foreach (int slot in candidates)
        {
            IType type = raw.LocalType(slot);
            if (candidates.Count(c => raw.LocalType(c).Equals(type)) < 2) continue;
            RawIL.Op store = raw.Ops[stores[slot][0]];
            if (all == null) all = RawIL.Own(f).ToList();
            ILInstruction value = null;
            foreach (ILInstruction inst in all)
            {
                if (inst.Parent == null || (inst is Block && ((Block)inst).Kind == BlockKind.ControlFlow) || inst is BlockContainer || inst is ILFunction || inst is AddressOf) continue;
                int end = RawIL.EndOf(inst);
                if (end != store.Offset && end != store.End) continue;
                if (value == null || value.IsDescendantOf(inst)) value = inst;
            }
            if (value == null || value.ResultType == StackType.Void) continue;
            var address = value.Parent as AddressOf;
            if (address == null || !(address.Parent is Call) || address.ChildIndex != 0) continue;
            // The statement that holds it, through expressions only, which evaluates it first.
            ILInstruction statement = address;
            bool simple = true;
            while (!(statement.Parent is Block && ((Block)statement.Parent).Kind == BlockKind.ControlFlow && statement.SlotInfo == Block.InstructionSlot))
            {
                ILInstruction p = statement.Parent;
                if (p == null || !(p is CallInstruction || p is StObj || p is StLoc || p is Conv || p is BinaryNumericInstruction || p is Comp)) { simple = false; break; }
                statement = p;
            }
            if (!simple || RawIL.StartOf(statement) != RawIL.StartOf(value)) continue;
            bool first = false;
            foreach (ILInstruction d in statement.Descendants)
            {
                if (d == value) { first = true; break; }
                if (d.IsDescendantOf(value)) continue;
                break;
            }
            if (!first) continue;
            var block = (Block)statement.Parent;
            ILVariable v = f.RegisterVariable(VariableKind.Local, type);
            address.Value = new Nop();
            address.ReplaceWith(new LdLoca(v));
            block.Instructions.Insert(statement.ChildIndex, new StLoc(v, value));
            all = null;
        }
    }

    private static bool IsBrTrue(ILOpCode c)
    {
        return c == ILOpCode.Brtrue || c == ILOpCode.Brtrue_s;
    }

    private static void NullConditionalNotTrue(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            ILVariable s = store.Variable;
            if (s.Kind != VariableKind.StackSlot || s.LoadCount != 2 || s.StoreCount != 1 || s.AddressCount != 0) continue;
            var inner = store.Parent as Block;
            if (inner == null || store.SlotInfo != Block.InstructionSlot || inner.Instructions.Count != 2 || store.ChildIndex != 0) continue;
            var jump = inner.Instructions[1] as IfInstruction;
            if (jump == null || !jump.FalseInst.MatchNop()) continue;
            var skip = jump.TrueInst as Branch;
            var test = jump.Condition as IfInstruction;
            if (skip == null || test == null || !test.FalseInst.MatchLdcI4(0)) continue;
            var notNull = test.Condition as Comp;
            if (notNull == null || notNull.Kind != ComparisonKind.Inequality || !notNull.Right.MatchLdNull()) continue;
            var checkedLoad = notNull.Left as LdLoc;
            if (checkedLoad == null || checkedLoad.Variable != s) continue;
            ILInstruction access = test.TrueInst;
            if (access is LdLoc || access is IfInstruction || access is Comp || access.ResultType != StackType.I4) continue;
            var uses = access.Descendants.OfType<LdLoc>().Where(l => l.Variable == s).ToList();
            if (uses.Count != 1) continue;

            // The enclosing 'if (a) { v = e; if (...) goto L; }', then R up to the end of a block
            // that falls on L.
            var guard = inner.Parent as IfInstruction;
            if (guard == null || guard.TrueInst != inner || !guard.FalseInst.MatchNop()) continue;
            var block = guard.Parent as Block;
            if (block == null || guard.SlotInfo != Block.InstructionSlot || guard.ChildIndex + 1 >= block.Instructions.Count) continue;
            if (block.Instructions[block.Instructions.Count - 1].HasFlag(InstructionFlags.EndPointUnreachable)) continue;
            var outer = block.Parent as IfInstruction;
            if (outer == null || outer.TrueInst != block || !outer.FalseInst.MatchNop()) continue;
            var parent = outer.Parent as Block;
            if (parent == null || outer.SlotInfo != Block.InstructionSlot || outer.ChildIndex + 2 != parent.Instructions.Count) continue;
            var fall = parent.Instructions[outer.ChildIndex + 1] as Branch;
            if (fall == null || fall.TargetBlock != skip.TargetBlock) continue;

            // 'dup; brtrue; pop; ldc.i4.1' after the value.
            int end = RawIL.EndOf(store.Value);
            RawIL.Op dup, branch, pop, one;
            if (end < 0) continue;
            if (!(raw.IsAt(end, out dup) && dup.Code == ILOpCode.Dup) && !(raw.EndsAt(end, out dup) && dup.Code == ILOpCode.Dup)) continue;
            if (!raw.Near(dup.Offset, 1, out branch) || !IsBrTrue(branch.Code)) continue;
            if (!raw.Near(dup.Offset, 2, out pop) || pop.Code != ILOpCode.Pop) continue;
            if (!raw.Near(dup.Offset, 3, out one) || one.Code != ILOpCode.Ldc_i4_1) continue;

            ILInstruction value = store.Value;
            store.Value = new Nop();
            uses[0].ReplaceWith(new NullableUnwrap(StackType.O, value));
            test.TrueInst = new Nop();
            var notTrue = new Comp(ComparisonKind.Inequality, ComparisonLiftingKind.CSharp, StackType.I4, Sign.None, new NullableRewrap(access), new LdcI4(1));
            ILInstruction a = guard.Condition;
            guard.Condition = new Nop();
            var rest = new Block();
            int from = guard.ChildIndex + 1;
            var moved = block.Instructions.Skip(from).ToList();
            block.Instructions.RemoveRange(from, block.Instructions.Count - from);
            rest.Instructions.AddRange(moved);
            guard.ReplaceWith(new IfInstruction(IfInstruction.LogicOr(Comp.LogicNot(a), notTrue), rest));

            // L has one way in now: it is the rest of the block.
            Block target = fall.TargetBlock;
            var container = parent.Parent as BlockContainer;
            if (container != null && target.Parent == container && target.IncomingEdgeCount == 1 && target != container.EntryPoint)
            {
                parent.Instructions.RemoveAt(parent.Instructions.Count - 1);
                var tail = target.Instructions.ToList();
                target.Instructions.Clear();
                parent.Instructions.AddRange(tail);
                container.Blocks.Remove(target);
            }
        }
    }
}
