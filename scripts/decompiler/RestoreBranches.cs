// IL transform: branches that meet again where the decompiler ends each of them on its own.
//
// 1. Result variable.
//        T r;  if (c) r = a; else r = b;  return r;
//    compiles to 'a; stloc r; br J; b; stloc r; J: ldloc r; ret'. The decompiler prints
//    'if (c) return a; return b;', which compiles to two 'ret'. Every 'return e;' whose value
//    is stored in the slot and followed by a jump to the one 'ldloc r; ret' is an assignment;
//    the 'if' they end get their 'else' back.
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreBranches : IILTransform
{
    public void Run(ILFunction function, ILTransformContext context)
    {
        var functions = function.Descendants.OfType<ILFunction>().ToList();
        functions.Add(function);
        foreach (ILFunction f in functions)
        {
            if (f.Method == null || !Program.Enabled("RestoreOrder", f.Method.MetadataToken)) continue;
            RawIL raw = RawIL.Read(f, context);
            if (raw == null) continue;
            ResultVariable(f, raw);
            SwitchResultVariable(f, raw);
            foreach (Block block in RawIL.Own(f).OfType<Block>().ToList())
                if (block.Kind == BlockKind.ControlFlow) ElseThatThrows(block, raw);
            foreach (Block block in RawIL.Own(f).OfType<Block>().ToList())
                if (block.Kind == BlockKind.ControlFlow) ReturnInLoop(block, raw);
            YieldBreaks(f, raw);
            NullConditionalValues(f, raw);
            LastOperandLocals(f, raw, context);
            ConvertedBranches(f, raw);
            PatternLocals(f, raw);
            foreach (Block block in RawIL.Own(f).OfType<Block>().ToList())
                if (block.Kind == BlockKind.ControlFlow && block.Parent != null) ElseAfterFinally(f, block, raw);
            NullChecksOnVariables(f, raw);
            AssignedCasts(f, raw);
            StackKeptResult(f, raw);
            CompilerTemporaries(f, raw);
            LoopConditions(f, raw);
            AssignmentArguments(f, raw);
            AddressedLocals(f, raw);
            InequalityLocals(f, raw, context);
            StackValuesInOrder(f, raw);
            StructInitializers(f, raw);
            TryResultVariables(f, raw);
            AssignedArguments(f, raw);
            InnerWhiles(f, raw);
            ReturnsAfterProtected(f, raw);
            foreach (Block block in RawIL.Own(f).OfType<Block>().ToList())
                if (block.Kind == BlockKind.ControlFlow && block.Parent != null) DefaultHoldsRest(f, block, raw);
            ExitsInTry(f, raw);
            if (NamedArgumentChoices(f, raw)) RestoreLocals.PositionalArguments(f);
            ConditionChoices(f, raw);
            StringSwitchHeads(f, raw);
            DefaultTests(f, raw);
            ReturnsAfterElse(f, raw);
            GuardsBeforeBreak(f, raw);
            BreakAfterProtected(f, raw);
            ReturnBehindProtected(f, raw);
            LoopTestInSection(f);
            GuardsBeforeTry(f, raw);
            foreach (Block block in RawIL.Own(f).OfType<Block>().ToList())
                if (block.Kind == BlockKind.ControlFlow && block.Parent != null) EndInLoop(f, block, raw);
        }
    }

    // 10. Null test of a variable in front of a call, in a protected block.
    //        if (v != null) v.f.M();           IL: ldloc v; brfalse E; ldloc v; ...
    //    is printed 'v?.f.M();', which inside a protected block compiles to
    //    'ldloc v; brtrue L; leave E; L: ...'.
    private static void NullChecksOnVariables(ILFunction f, RawIL raw)
    {
        foreach (NullableRewrap rewrap in RawIL.Own(f).OfType<NullableRewrap>().ToList())
        {
            if (!IsStatement(rewrap)) continue;
            var unwraps = rewrap.Descendants.OfType<NullableUnwrap>().ToList();
            if (unwraps.Count != 1 || !(unwraps[0].Argument is LdLoc) || unwraps[0].ResultType != StackType.O) continue;
            NullableUnwrap unwrap = unwraps[0];
            ILVariable v = ((LdLoc)unwrap.Argument).Variable;
            // 'v?.M();' with a 'call' on v itself: 'if (v != null) v.M();' would be a 'callvirt'.
            var direct = rewrap.Argument as Call;
            if (direct != null && !direct.Method.IsStatic && direct.Arguments.Count > 0 && direct.Arguments[0] == unwrap && direct.Method.DeclaringType.IsReferenceType == true) continue;
            int end = RawIL.EndOf(unwrap.Argument);
            RawIL.Op branch, target, again;
            // The variable has the range of its second load; the statement starts at the branch.
            if (end < 0 || !raw.IsAt(end, out branch) || !IsBranchOnBool(branch.Code)) raw.IsAt(RawIL.StartOf(rewrap), out branch);
            if (branch.Code != ILOpCode.Brfalse && branch.Code != ILOpCode.Brfalse_s) continue;
            if (!raw.Near(branch.Offset, -1, out again) || !(RawIL.IsLdLoc(again.Code) || RawIL.IsLdArg(again.Code))) continue;
            if (!raw.Near(branch.Offset, 1, out again) || !(RawIL.IsLdLoc(again.Code) || RawIL.IsLdArg(again.Code))) continue;
            // Only where the two forms differ: the jump lands on the 'leave' of a protected block.
            if (!raw.IsAt(branch.Operand, out target) || (target.Code != ILOpCode.Leave && target.Code != ILOpCode.Leave_s)) continue;
            var block = (Block)rewrap.Parent;
            int at = rewrap.ChildIndex;
            ILInstruction call = rewrap.Argument;
            rewrap.Argument = new Nop();
            unwrap.Argument = new Nop();
            unwrap.ReplaceWith(new LdLoc(v));
            var then = new Block();
            then.Instructions.Add(call);
            block.Instructions[at] = new IfInstruction(new Comp(ComparisonKind.Inequality, Sign.None, new LdLoc(v), new LdNull()), then);
        }
    }

    // 11. Cast assigned inside a test.
    //        T v;  if ((v = e as T) != null)       IL: e; isinst T; dup; stloc v; brfalse
    //    is printed 'if (e is T v)', which compiles to 'isinst T; stloc v; ldloc v; brfalse'.
    private static void AssignedCasts(ILFunction f, RawIL raw)
    {
        foreach (MatchInstruction match in RawIL.Own(f).OfType<MatchInstruction>().ToList())
        {
            if (!match.CheckType || !match.CheckNotNull || match.IsDeconstructCall || match.IsDeconstructTuple || match.SubPatterns.Count != 0 || match.Parent == null) continue;
            if (match.Variable.Type.IsReferenceType != true) continue;
            int end = RawIL.EndOf(match.TestedOperand);
            RawIL.Op cast, dup, store, branch;
            if (end < 0 || !raw.IsAt(end, out cast) || cast.Code != ILOpCode.Isinst) continue;
            if (!raw.Near(end, 1, out dup) || dup.Code != ILOpCode.Dup) continue;
            if (!raw.Near(end, 2, out store) || !RawIL.IsStLoc(store.Code)) continue;
            if (!raw.Near(end, 3, out branch) || !IsBranchOnBool(branch.Code)) continue;
            ILVariable old = match.Variable;
            if (old.AddressCount != 0) continue;
            ILVariable v = f.RegisterVariable(VariableKind.Local, old.Type);
            foreach (LdLoc load in RawIL.Own(f).OfType<LdLoc>().Where(l => l.Variable == old).ToList()) load.ReplaceWith(new LdLoc(v));
            ILInstruction tested = match.TestedOperand;
            match.TestedOperand = new Nop();
            var test = new Comp(ComparisonKind.Inequality, Sign.None, new StLoc(v, new IsInst(tested, old.Type)), new LdNull());
            test.AddILRange(match);
            match.ReplaceWith(test);
        }
    }

    // 12. Result kept on the stack.
    //        T r = e;  ...;  if (c) { A } else { B }  return r;
    //    reads r once: the compiler leaves it on the stack from 'e' to the 'ret' (no 'stloc'),
    //    and puts a 'ret' at the end of each branch. The decompiler prints
    //    'if (c) { A; return r; } B; return r;': two reads, hence a slot.
    private static void StackKeptResult(ILFunction f, RawIL raw)
    {
        var body = f.Body as BlockContainer;
        if (body == null || f.ReturnType.Kind == TypeKind.Void || f.MoveNextMethod != null) return;
        var returns = RawIL.Own(f).OfType<Leave>().Where(l => l.IsLeavingFunction && !l.Value.MatchNop()).ToList();
        if (returns.Count < 2 || !returns.All(l => l.Value is LdLoc)) return;
        ILVariable r = ((LdLoc)returns[0].Value).Variable;
        if (returns.Any(l => ((LdLoc)l.Value).Variable != r)) return;
        if (r.StoreCount != 1 || r.AddressCount != 0 || r.LoadCount != returns.Count || (r.Kind != VariableKind.Local && r.Kind != VariableKind.StackSlot)) return;
        var store = r.StoreInstructions.OfType<StLoc>().FirstOrDefault();
        if (store == null || !IsStatement(store) || store.Parent.Parent != body) return;
        int end = RawIL.EndOf(store.Value);
        RawIL.Op op;
        if (end < 0 || (raw.IsAt(end, out op) && RawIL.IsStLoc(op.Code)) || (raw.EndsAt(end, out op) && RawIL.IsStLoc(op.Code))) return;
        Leave final = returns.FirstOrDefault(l => l.Parent == store.Parent);
        var top = (Block)store.Parent;
        if (final == null || final.ChildIndex != top.Instructions.Count - 1) return;
        var assigning = new HashSet<Leave>(returns);
        var covered = new HashSet<Leave>();
        if (!CanMeet(top, store.ChildIndex + 1, assigning, covered) || covered.Count != assigning.Count) return;
        var result = new Leave(final.TargetContainer, new LdLoc(r));
        result.AddILRange(final);
        Meet(top, store.ChildIndex + 1, assigning, null);
        top.Instructions.Add(result);
    }

    // 13. Temporary of the compiler printed as a variable.
    //        a.M(e)        (e converted to the type of the parameter: string[] to object[])
    //    compiles to 'a; e; stloc t; ldloc t; call M'. A slot written once and read at once is
    //    never a variable of the source (the value would have stayed on the stack). The
    //    decompiler prints 'A x = a; T t = e; x.M(t);', which adds a slot.
    private static void CompilerTemporaries(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            ILVariable v = store.Variable;
            if (!IsStatement(store) || v.Kind != VariableKind.Local || !v.Index.HasValue || v.StoreCount != 1 || v.LoadCount != 1 || v.AddressCount != 0) continue;
            int slot = v.Index.Value;
            RawIL.Op stored, loaded;
            // Only the temporary of a conversion: with another value kept on the stack under it,
            // a variable of the source read at once goes through its slot as well.
            var produced = store.Value as CallInstruction;
            if (produced == null || produced is NewObj || produced.Method.ReturnType.Equals(v.Type) || produced.Method.ReturnType.Kind != TypeKind.Array || v.Type.Kind != TypeKind.Array) continue;
            if (!StoreAfter(store.Value, raw, out stored) || stored.Operand != slot) continue;
            if (!raw.Near(stored.Offset, 1, out loaded) || !RawIL.IsLdLoc(loaded.Code) || loaded.Operand != slot || raw.Targets.Contains(loaded.Offset)) continue;
            if (raw.Ops.Count(o => (RawIL.IsLdLoc(o.Code) || RawIL.IsStLoc(o.Code) || o.Code == ILOpCode.Ldloca || o.Code == ILOpCode.Ldloca_s) && o.Operand == slot) != 2) continue;
            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            if (at + 1 >= block.Instructions.Count) continue;
            ILInstruction next = block.Instructions[at + 1];
            if (!(next is CallInstruction || next is StObj || next is StLoc)) continue;
            // Values the statement pushes before it, which the decompiler had to name as well.
            var pending = new List<StLoc> { store };
            for (int i = at - 1; i >= 0; i--)
            {
                var earlier = block.Instructions[i] as StLoc;
                if (earlier == null || earlier.Variable.Kind != VariableKind.StackSlot || earlier.Variable.StoreCount != 1 || earlier.Variable.LoadCount != 1 || earlier.Variable.AddressCount != 0) break;
                pending.Insert(0, earlier);
            }
            if (pending.Count < 2) continue;
            var uses = new List<LdLoc>();
            bool ordered = true;
            foreach (ILInstruction d in next.Descendants)
            {
                if (uses.Count == pending.Count) break;
                if (d.MatchLdLoc(pending[uses.Count].Variable)) { uses.Add((LdLoc)d); continue; }
                if (d.Descendants.OfType<LdLoc>().Any(l => pending.Any(p => p.Variable == l.Variable))) continue;
                if (!IsPlainLoad(d)) { ordered = false; break; }
            }
            if (!ordered || uses.Count != pending.Count) continue;
            for (int i = 0; i < pending.Count; i++)
            {
                ILInstruction value = pending[i].Value;
                pending[i].Value = new Nop();
                uses[i].ReplaceWith(value);
            }
            block.Instructions.RemoveRange(at - (pending.Count - 1), pending.Count);
        }
    }

    private static bool IsPlainLoad(ILInstruction d)
    {
        return d is LdLoc || d is LdcI4 || d is LdNull || d is LdStr || d is LdFlda || d is LdsFlda || d is LdLoca || d is Nop;
    }

    // 7. Conditional expression with a branch converted to the type of the other.
    //        x.f = c ? new X() : y.M();        (M returns an interface X implements)
    //    compiles to '...; y.M(); br E; newobj X; stloc t; ldloc t; E: stfld f': the compiler
    //    passes the converted branch through a slot of the common type. The decompiler prints
    //    'if (!c) v = y.M(); else { I t = new X(); v = t; }  x.f = v;'.
    private static void ConvertedBranches(ILFunction f, RawIL raw)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test)) continue;
            var block = (Block)test.Parent;
            var one = test.TrueInst as Block;
            var two = test.FalseInst as Block;
            if (one == null || two == null || test.ChildIndex + 1 >= block.Instructions.Count) continue;
            bool swapped = one.Instructions.Count == 2;
            Block direct = swapped ? two : one, through = swapped ? one : two;
            if (direct.Instructions.Count != 1 || through.Instructions.Count != 2) continue;
            var a = direct.Instructions[0] as StLoc;
            var t = through.Instructions[0] as StLoc;
            var b = through.Instructions[1] as StLoc;
            if (a == null || t == null || b == null || a.Variable != b.Variable || !b.Value.MatchLdLoc(t.Variable)) continue;
            ILVariable s = a.Variable, temp = t.Variable;
            if (s.Kind != VariableKind.StackSlot || s.LoadCount != 1 || s.StoreCount != 2 || s.AddressCount != 0) continue;
            if (temp.LoadCount != 1 || temp.StoreCount != 1 || temp.AddressCount != 0) continue;
            ILInstruction next = block.Instructions[test.ChildIndex + 1];
            LdLoc use = null;
            bool plain = true;
            foreach (ILInstruction d in next.Descendants)
            {
                if (d.MatchLdLoc(s)) { use = (LdLoc)d; break; }
                if (!IsPlainLoad(d) && !use_IsAncestor(d, next, s)) plain = false;
            }
            if (use == null || !plain) continue;
            // 'stloc t; ldloc t' with nothing in between, then the end of the conditional.
            RawIL.Op store, load;
            if (!StoreAfter(t.Value, raw, out store) || !raw.Near(store.Offset, 1, out load) || !RawIL.IsLdLoc(load.Code) || load.Operand != store.Operand) continue;
            int startA = RawIL.StartOf(a.Value), startB = RawIL.StartOf(t.Value);
            if (startA < 0 || startB < 0 || startA == startB) continue;

            ILInstruction condition = test.Condition, valueA = a.Value, valueB = t.Value, positive;
            test.Condition = new Nop();
            a.Value = new Nop();
            t.Value = new Nop();
            // The compiler emits the value of the false branch first.
            ILInstruction whenTrue = swapped ? valueB : valueA, whenFalse = swapped ? valueA : valueB;
            if (RawIL.StartOf(whenTrue) < RawIL.StartOf(whenFalse))
            {
                if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); condition = positive; }
                else condition = Comp.LogicNot(condition);
                ILInstruction swap = whenTrue; whenTrue = whenFalse; whenFalse = swap;
            }
            use.ReplaceWith(new IfInstruction(condition, whenTrue, whenFalse));
            block.Instructions.RemoveAt(test.ChildIndex);
        }
    }

    // True when 'd' is on the path from 'statement' down to the load of 's'.
    private static bool use_IsAncestor(ILInstruction d, ILInstruction statement, ILVariable s)
    {
        return d.Descendants.Any(x => x.MatchLdLoc(s));
    }

    // 8. Pattern variable of a type parameter, read once.
    //        if (x is U u) return u;           IL: x; isinst U; brfalse E; x; unbox.any U; stloc u; ldloc u; ret
    //    is printed 'if (x is U) return (U)x;', which has no slot: an ordinary local read once
    //    right after its store would have stayed on the stack.
    private static void PatternLocals(ILFunction f, RawIL raw)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test)) continue;
            ILInstruction left, right;
            var comp = test.Condition as Comp;
            if (comp == null || comp.Kind != ComparisonKind.Inequality || !comp.Right.MatchLdNull()) continue;
            var isinst = comp.Left as IsInst;
            ILVariable x;
            if (isinst == null || !isinst.Argument.MatchLdLoc(out x) || isinst.Type.Kind != TypeKind.TypeParameter) continue;
            var casts = test.TrueInst.Descendants.OfType<UnboxAny>().Where(u => u.Argument.MatchLdLoc(x) && u.Type.Equals(isinst.Type)).ToList();
            if (test.TrueInst is UnboxAny) continue;
            if (casts.Count != 1) continue;
            UnboxAny cast = casts[0];
            int end = RawIL.EndOf(test.Condition);
            RawIL.Op branch, load, unbox, store, again;
            if (end < 0) continue;
            if (!(raw.IsAt(end, out branch) && IsBranchOnBool(branch.Code)) && !(raw.EndsAt(end, out branch) && IsBranchOnBool(branch.Code))) continue;
            if (!raw.Near(branch.Offset, 1, out load) || !(RawIL.IsLdLoc(load.Code) || RawIL.IsLdArg(load.Code))) continue;
            if (!raw.Near(branch.Offset, 2, out unbox) || unbox.Code != ILOpCode.Unbox_any) continue;
            if (!raw.Near(branch.Offset, 3, out store) || !RawIL.IsStLoc(store.Code)) continue;
            if (!raw.Near(branch.Offset, 4, out again) || !RawIL.IsLdLoc(again.Code) || again.Operand != store.Operand) continue;
            int reads = raw.Ops.Count(o => RawIL.IsLdLoc(o.Code) && o.Operand == store.Operand), writes = raw.Ops.Count(o => RawIL.IsStLoc(o.Code) && o.Operand == store.Operand);
            if (reads != 1 || writes != 1 || raw.Ops.Any(o => (o.Code == ILOpCode.Ldloca || o.Code == ILOpCode.Ldloca_s) && o.Operand == store.Operand)) continue;
            int castEnd = RawIL.EndOf(cast);
            if (castEnd != store.Offset && castEnd != store.End) continue;
            left = isinst.Argument;
            right = null;
            isinst.Argument = new Nop();
            ILVariable v = f.RegisterVariable(VariableKind.PatternLocal, isinst.Type);
            var match = new MatchInstruction(v, left) { CheckType = true, CheckNotNull = true };
            match.AddILRange(comp);
            test.Condition = match;
            cast.ReplaceWith(new LdLoc(v));
        }
    }

    // 9. 'else' after a branch that ends on a protected block.
    //        if (c) { A; try { } finally { } } else { R }       at the end of a void method
    //    has R right after the 'endfinally'. With 'if (c) { ...; return; } R' the 'leave' of
    //    the protected block goes to a 'ret' that stands between the two.
    private static void ElseAfterFinally(ILFunction f, Block block, RawIL raw)
    {
        var body = f.Body as BlockContainer;
        if (body == null || f.ReturnType.Kind != TypeKind.Void || f.MoveNextMethod != null) return;
        int n = block.Instructions.Count;
        for (int k = 0; k + 1 < n; k++)
        {
            var test = block.Instructions[k] as IfInstruction;
            if (test == null || !test.FalseInst.MatchNop()) continue;
            var then = test.TrueInst as Block;
            ILInstruction guarded = then == null ? test.TrueInst : then.Instructions.Count == 0 ? null : then.Instructions[then.Instructions.Count - 1];
            Leave exit = guarded as Leave;
            BlockContainer inner = null;
            if (exit != null)
            {
                // 'if (c) { ...; using { }  return; }'
                if (then == null || then.Instructions.Count < 2 || exit.TargetContainer != body || !exit.Value.MatchNop()) continue;
                guarded = then.Instructions[then.Instructions.Count - 2];
            }
            else if (guarded != null)
            {
                // 'if (c) { ...; using { ...; return; } }': the 'return' is the end of the block.
                if (guarded is TryFinally) inner = ((TryFinally)guarded).TryBlock as BlockContainer;
                else if (guarded is UsingInstruction) inner = ((UsingInstruction)guarded).Body as BlockContainer;
                else if (guarded is LockInstruction) inner = ((LockInstruction)guarded).Body as BlockContainer;
                if (inner == null || inner.Blocks.Count != 1 || inner.EntryPoint.Instructions.Count == 0) continue;
                exit = inner.EntryPoint.Instructions[inner.EntryPoint.Instructions.Count - 1] as Leave;
                if (exit == null || exit.TargetContainer != body || !exit.Value.MatchNop()) continue;
                if (guarded.Descendants.OfType<Leave>().Count(l => l.TargetContainer == body) != 1) continue;
            }
            if (!(guarded is TryFinally || guarded is UsingInstruction || guarded is LockInstruction)) continue;
            // What follows runs to the end of the method.
            ILInstruction last = block.Instructions[n - 1];
            var closing = last as Leave;
            bool closes = closing != null && closing.TargetContainer == body && closing.Value.MatchNop();
            if (!closes && !(block.Parent == body && body.Blocks.Count == 1)) continue;
            int restEnd = closes ? n - 1 : n;
            if (restEnd <= k + 1) continue;
            int restStart = RawIL.StartOf(block.Instructions[k + 1]);
            RawIL.Op before;
            if (restStart <= 0 || !raw.EndsAt(restStart, out before) || before.Code != ILOpCode.Endfinally) continue;

            if (inner != null) exit.ReplaceWith(new Leave(inner));
            else then.Instructions.RemoveAt(then.Instructions.Count - 1);
            var rest = new Block();
            var moved = new List<ILInstruction>();
            for (int i = k + 1; i < restEnd; i++) moved.Add(block.Instructions[i]);
            block.Instructions.RemoveRange(k + 1, restEnd - k - 1);
            rest.Instructions.AddRange(moved);
            test.FalseInst = rest;
            return;
        }
    }

    private static bool IsStatement(ILInstruction inst)
    {
        var block = inst.Parent as Block;
        return block != null && block.Kind == BlockKind.ControlFlow && inst.SlotInfo == Block.InstructionSlot;
    }

    private static ILInstruction StatementOf(ILInstruction inst)
    {
        while (inst != null && !IsStatement(inst)) inst = inst.Parent;
        return inst;
    }

    private static bool IsBranchOnBool(ILOpCode c)
    {
        return c == ILOpCode.Brfalse || c == ILOpCode.Brfalse_s || c == ILOpCode.Brtrue || c == ILOpCode.Brtrue_s;
    }

    // 5. Null test written out, as a value.
    //        T v = e;  x.f = (v != null) ? v.M() : null;       IL: e; stloc v; ldarg.0; ldloc v; brtrue L; ldnull; br E; L: ldloc v; callvirt M; E:
    //        t == null ? null : t.ToString()   (t of a type parameter)   IL: box T; brfalse N; ...; br E; N: ldnull; E:
    //    are printed 'e?.M()', which compiles to 'e; dup; brtrue L; pop; ldnull; br E; L: call M'
    //    ('box T; brtrue L; ldnull; br E' for a type parameter).
    private static void NullConditionalValues(ILFunction f, RawIL raw)
    {
        foreach (NullableRewrap rewrap in RawIL.Own(f).OfType<NullableRewrap>().ToList())
        {
            if (IsStatement(rewrap) || rewrap.Parent == null) continue;
            ILInstruction statement = StatementOf(rewrap);
            if (statement == null || statement.Descendants.OfType<NullableRewrap>().Count() != 1) continue;
            var unwraps = rewrap.Descendants.OfType<NullableUnwrap>().ToList();
            if (unwraps.Count != 1) continue;
            NullableUnwrap unwrap = unwraps[0];
            var call = rewrap.Argument as CallInstruction;
            if (call == null || call.Method.ReturnType.IsReferenceType != true) continue;
            int from = RawIL.StartOf(statement), to = RawIL.EndOf(statement);
            int a, b;
            if (from < 0 || to < 0 || !raw.IndexAt.TryGetValue(from, out a)) continue;
            if (!raw.IndexAt.TryGetValue(to, out b)) b = raw.Ops.Count;

            if (unwrap.Argument is LdLoca && unwrap.Parent is LdObjIfRef)
            {
                // Type parameter: one 'box T; brfalse' in the statement.
                ILVariable p = ((LdLoca)unwrap.Argument).Variable;
                if (p.Type.Kind != TypeKind.TypeParameter) continue;
                int found = 0;
                bool nullLast = false;
                for (int i = a; i + 1 < b; i++)
                {
                    if (raw.Ops[i].Code != ILOpCode.Box || !IsBranchOnBool(raw.Ops[i + 1].Code)) continue;
                    found++;
                    nullLast = raw.Ops[i + 1].Code == ILOpCode.Brfalse || raw.Ops[i + 1].Code == ILOpCode.Brfalse_s;
                }
                if (found != 1 || !nullLast) continue;
                ILInstruction value = rewrap.Argument, address = unwrap.Argument;
                unwrap.Argument = new Nop();
                unwrap.ReplaceWith(address);
                rewrap.Argument = new Nop();
                var test = new Comp(ComparisonKind.Equality, Sign.None, new Box(new LdLoc(p), p.Type), new LdNull());
                rewrap.ReplaceWith(new IfInstruction(test, new LdNull(), value));
                continue;
            }

            if (unwrap.Argument is LdLoc || unwrap.ResultType != StackType.O || unwrap.Parent != call || call.Arguments.Count == 0 || call.Arguments[0] != unwrap) continue;
            RawIL.Op store;
            if (!StoreAfter(unwrap.Argument, raw, out store) || RawIL.StartOf(unwrap.Argument) != from) continue;
            int s = raw.IndexAt[store.Offset], k = s + 1;
            // Plain loads between the store and the test: what the statement pushes first.
            while (k < raw.Ops.Count && k < s + 4 && (RawIL.IsLdArg(raw.Ops[k].Code) || (RawIL.IsLdLoc(raw.Ops[k].Code) && raw.Ops[k].Operand != store.Operand))) k++;
            if (k == s + 1 || k + 4 >= raw.Ops.Count) continue;
            if (!RawIL.IsLdLoc(raw.Ops[k].Code) || raw.Ops[k].Operand != store.Operand) continue;
            ILOpCode jump = raw.Ops[k + 1].Code;
            if (jump != ILOpCode.Brtrue && jump != ILOpCode.Brtrue_s) continue;
            if (raw.Ops[k + 2].Code != ILOpCode.Ldnull || !IsBr(raw.Ops[k + 3].Code) || !RawIL.IsLdLoc(raw.Ops[k + 4].Code) || raw.Ops[k + 4].Operand != store.Operand) continue;
            IType type = raw.LocalType(store.Operand);
            if (type == null || type.Kind == TypeKind.Unknown) continue;
            {
                var block = (Block)statement.Parent;
                ILVariable v = f.RegisterVariable(VariableKind.Local, type);
                ILInstruction receiver = unwrap.Argument, value = rewrap.Argument;
                unwrap.Argument = new Nop();
                unwrap.ReplaceWith(new LdLoc(v));
                rewrap.Argument = new Nop();
                var test = new Comp(ComparisonKind.Inequality, Sign.None, new LdLoc(v), new LdNull());
                rewrap.ReplaceWith(new IfInstruction(test, value, new LdNull()));
                block.Instructions.Insert(statement.ChildIndex, new StLoc(v, receiver));
            }
        }
    }

    // 6. Last comparison of a condition kept in a variable.
    //        if (a && b) { bool v = x == 0; if (v) T }       IL: ...; x; ldc.i4.0; ceq; brfalse E; T
    //    is printed 'if (a && b && x == 0) T', which branches on the comparison itself ('brtrue E').
    private static void LastOperandLocals(ILFunction f, RawIL raw, ILTransformContext context)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test) || !test.FalseInst.MatchNop()) continue;
            var loop = test.Parent.Parent as BlockContainer;
            if (loop != null && loop.Kind != ContainerKind.Normal && loop.Kind != ContainerKind.Switch) continue;
            ILInstruction last = test.Condition, lhs, rhs;
            IfInstruction holder = null;
            while (last.MatchLogicAnd(out lhs, out rhs)) { holder = (IfInstruction)last; last = rhs; }
            var comp = last as Comp;
            if (holder == null || comp == null || comp.LiftingKind != ComparisonLiftingKind.None) continue;
            if (comp.InputType == StackType.F4 || comp.InputType == StackType.F8) continue;
            int bodyStart = RawIL.StartOf(test.TrueInst);
            RawIL.Op branch, before;
            if (bodyStart <= 0 || !raw.EndsAt(bodyStart, out branch)) continue;
            if (branch.Code != ILOpCode.Brfalse && branch.Code != ILOpCode.Brfalse_s) continue;
            if (!raw.Near(branch.Offset, -1, out before)) continue;
            if (before.Code != ILOpCode.Ceq && before.Code != ILOpCode.Cgt && before.Code != ILOpCode.Cgt_un && before.Code != ILOpCode.Clt && before.Code != ILOpCode.Clt_un) continue;
            int compEnd = RawIL.EndOf(comp);
            if (compEnd != branch.Offset && compEnd != branch.End) continue;

            ILVariable v = f.RegisterVariable(VariableKind.Local, context.TypeSystem.FindType(KnownTypeCode.Boolean));
            ILInstruction first = holder.Condition, body = test.TrueInst;
            holder.Condition = new Nop();
            holder.TrueInst = new Nop();
            holder.ReplaceWith(first);
            test.TrueInst = new Nop();
            var inner = new Block();
            inner.Instructions.Add(new StLoc(v, comp));
            inner.Instructions.Add(new IfInstruction(new LdLoc(v), body));
            test.TrueInst = inner;
        }
    }

    // 3. Return inside an endless loop.
    //        while (true) { A; if (c) return e; B; }
    //    has 'e; ret' between A and B. The decompiler prints 'if (c) break;' and the return
    //    after the loop, which puts 'e; ret' after the jump that closes the loop.
    private static void ReturnInLoop(Block block, RawIL raw)
    {
        for (int k = 0; k + 1 < block.Instructions.Count; k++)
        {
            var loop = block.Instructions[k] as BlockContainer;
            int n = block.Instructions.Count;
            var result = block.Instructions[n - 1] as Leave;
            if (loop == null || loop.Kind != ContainerKind.Loop || result == null || !result.IsLeavingFunction || result.Value.MatchNop()) continue;
            var breaks = loop.Descendants.OfType<Leave>().Where(l => l.TargetContainer == loop).ToList();
            if (breaks.Count != 1 || !(breaks[0].Parent is Block || breaks[0].Parent is IfInstruction)) continue;
            // Everything between the loop and the return stands inside the loop in the IL.
            int loopStart = RawIL.StartOf(loop), loopEnd = RawIL.EndOf(loop);
            bool inside = loopStart >= 0;
            for (int i = k + 1; i < n && inside; i++)
            {
                int at = RawIL.StartOf(i == n - 1 ? result.Value : block.Instructions[i]);
                if (at < 0 || at <= loopStart || at >= loopEnd) inside = false;
                if (i < n - 1 && !(block.Instructions[i] is CallInstruction || block.Instructions[i] is StObj || block.Instructions[i] is StLoc)) inside = false;
            }
            if (!inside) continue;
            var moved = new List<ILInstruction>();
            for (int i = k + 1; i < n; i++) moved.Add(block.Instructions[i]);
            block.Instructions.RemoveRange(k + 1, n - k - 1);
            if (moved.Count == 1) breaks[0].ReplaceWith(result);
            else if (breaks[0].Parent is Block)
            {
                var holder = (Block)breaks[0].Parent;
                int index = breaks[0].ChildIndex;
                holder.Instructions.RemoveAt(index);
                for (int i = 0; i < moved.Count; i++) holder.Instructions.Insert(index + i, moved[i]);
            }
            else
            {
                var exit = new Block();
                exit.Instructions.AddRange(moved);
                breaks[0].ReplaceWith(exit);
            }
            return;
        }
    }

    private static bool IsPlainRead(ILInstruction d)
    {
        return IsPlainLoad(d) || d is LdObj || d is LdcI8 || d is LdcF4 || d is LdcF8;
    }

    // 15. Assignment passed as an argument.
    //        a.M(x.f = e)                      IL: a; x; e; dup; stloc t; stfld f; ldloc t; callvirt M
    //    is printed 'T s = e; T v = s; x.f = s; a.M(v);', which creates the object before 'a'
    //    is pushed. In the IL the receiver of the call comes first.
    private static void AssignmentArguments(ILFunction f, RawIL raw)
    {
        foreach (StLoc first in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            if (!IsStatement(first) || first.Parent == null) continue;
            var block = (Block)first.Parent;
            int at = first.ChildIndex;
            if (at + 3 >= block.Instructions.Count) continue;
            ILVariable s = first.Variable;
            if (s.StoreCount != 1 || s.LoadCount != 2 || s.AddressCount != 0) continue;
            var copy = block.Instructions[at + 1] as StLoc;
            var assign = block.Instructions[at + 2] as StObj;
            if (copy == null) { copy = block.Instructions[at + 2] as StLoc; assign = block.Instructions[at + 1] as StObj; }
            if (copy == null || assign == null || !copy.Value.MatchLdLoc(s) || !assign.Value.MatchLdLoc(s)) continue;
            ILVariable v = copy.Variable;
            if (v.StoreCount != 1 || v.LoadCount != 1 || v.AddressCount != 0) continue;
            if (!IsPlainRead(assign.Target) || !assign.Target.Descendants.All(IsPlainRead)) continue;
            var call = block.Instructions[at + 3] as CallInstruction;
            if (call == null) continue;
            // What the call pushes first may have been named too: 'S r = a;' in front.
            var earlier = new List<StLoc>();
            for (int i = at - 1; i >= 0; i--)
            {
                var pushed = block.Instructions[i] as StLoc;
                if (pushed == null || pushed.Variable.Kind != VariableKind.StackSlot || pushed.Variable.StoreCount != 1 || pushed.Variable.LoadCount != 1 || pushed.Variable.AddressCount != 0) break;
                if (!IsPlainRead(pushed.Value) || !pushed.Value.Descendants.All(IsPlainRead)) break;
                if (!call.Descendants.Any(x => x.MatchLdLoc(pushed.Variable))) break;
                earlier.Insert(0, pushed);
            }
            LdLoc use = null;
            var pushes = new List<LdLoc>();
            bool plain = true;
            foreach (ILInstruction d in call.Descendants)
            {
                if (d.MatchLdLoc(v)) { use = (LdLoc)d; break; }
                if (pushes.Count < earlier.Count && d.MatchLdLoc(earlier[pushes.Count].Variable)) { pushes.Add((LdLoc)d); continue; }
                if (d.Descendants.Any(x => x.MatchLdLoc(v))) continue;
                if (!IsPlainRead(d)) { plain = false; break; }
            }
            if (use == null || !plain || use.Parent != call || pushes.Count != earlier.Count) continue;
            // The call pushes something before the value is computed.
            int callStart = earlier.Count > 0 ? RawIL.StartOf(earlier[0].Value) : RawIL.StartOf(call), valueStart = RawIL.StartOf(first.Value);
            if (callStart < 0 || valueStart < 0 || callStart >= valueStart) continue;
            ILInstruction value = first.Value;
            first.Value = new Nop();
            for (int i = 0; i < earlier.Count; i++)
            {
                ILInstruction pushedValue = earlier[i].Value;
                earlier[i].Value = new Nop();
                pushes[i].ReplaceWith(pushedValue);
            }
            block.Instructions.RemoveRange(at - earlier.Count, 3 + earlier.Count);
            assign.Value = value;
            use.ReplaceWith(assign);
        }
    }

    // 16. Value type local read through its address only.
    //        int n = e;  return "[" + n.ToString() + "]";      IL: e; stloc n; ldstr "["; ldloca n; call ToString
    //    is printed '"[" + e.ToString() + "]"', which pushes "[" first and stores e in a
    //    temporary right before the call.
    private static void AddressedLocals(ILFunction f, RawIL raw)
    {
        foreach (AddressOf address in RawIL.Own(f).OfType<AddressOf>().ToList())
        {
            if (address.Parent == null || !(address.Parent is CallInstruction)) continue;
            RawIL.Op store, next;
            if (!StoreAfter(address.Value, raw, out store))
            {
                // 'ldlen; conv.i4' is one node, with the range of the 'ldlen'.
                int valueEnd = RawIL.EndOf(address.Value);
                if (valueEnd < 0 || !raw.IsAt(valueEnd, out next) || next.Code != ILOpCode.Conv_i4 || !raw.Near(valueEnd, 1, out store) || !RawIL.IsStLoc(store.Code)) continue;
            }
            if (!raw.Near(store.Offset, 1, out next)) continue;
            int slot = store.Operand;
            if ((next.Code == ILOpCode.Ldloca || next.Code == ILOpCode.Ldloca_s) && next.Operand == slot) continue;
            int reads = 0, others = 0, read = -1, s = raw.IndexAt[store.Offset];
            for (int i = 0; i < raw.Ops.Count; i++)
            {
                RawIL.Op op = raw.Ops[i];
                if (op.Operand != slot) continue;
                if (op.Code == ILOpCode.Ldloca || op.Code == ILOpCode.Ldloca_s) { reads++; read = i; }
                else if (RawIL.IsLdLoc(op.Code) || (RawIL.IsStLoc(op.Code) && i != s)) others++;
            }
            if (reads != 1 || others != 0 || read < s) continue;
            bool straight = true;
            for (int i = s + 1; i <= read && straight; i++)
            {
                ILOpCode c = raw.Ops[i].Code;
                if (RawIL.IsCondBranch(c) || IsBr(c) || c == ILOpCode.Leave || c == ILOpCode.Leave_s || c == ILOpCode.Ret || c == ILOpCode.Switch || c == ILOpCode.Throw || RawIL.IsCall(c) || c == ILOpCode.Newobj) straight = false;
                if (raw.Targets.Contains(raw.Ops[i].Offset)) straight = false;
            }
            if (!straight) continue;
            IType type = raw.LocalType(slot);
            if (type == null || type.Kind == TypeKind.Unknown) continue;
            // Up to the statement through calls only, with nothing evaluated before the value.
            ILInstruction statement = address;
            bool simple = true;
            while (!IsStatement(statement) && simple)
            {
                ILInstruction p = statement.Parent;
                if (p == null || !(p is CallInstruction || p is Leave || p is StObj || p is StLoc)) simple = false;
                statement = p;
            }
            if (!simple || statement == null) continue;
            bool firstEvaluated = false;
            foreach (ILInstruction d in statement.Descendants)
            {
                if (d == address) { firstEvaluated = true; break; }
                if (address.IsDescendantOf(d) || d.IsDescendantOf(address)) continue;
                if (!IsPlainRead(d)) break;
            }
            if (!firstEvaluated) continue;
            var block = (Block)statement.Parent;
            ILVariable v = f.RegisterVariable(VariableKind.Local, type);
            ILInstruction value = address.Value;
            address.Value = new Nop();
            address.ReplaceWith(new LdLoca(v));
            block.Instructions.Insert(statement.ChildIndex, new StLoc(v, value));
        }
    }

    // 17. Inequality kept in a variable.
    //        bool v = a != b;  if (v) T        IL: a; b; ceq; ldc.i4.0; ceq; brfalse E; T
    //    is printed 'if (a != b) T', which branches on the comparison ('beq E').
    private static void InequalityLocals(ILFunction f, RawIL raw, ILTransformContext context)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test)) continue;
            var loop = test.Parent.Parent as BlockContainer;
            if (loop != null && loop.Kind != ContainerKind.Normal && loop.Kind != ContainerKind.Switch) continue;
            var comp = test.Condition as Comp;
            if (comp == null || comp.LiftingKind != ComparisonLiftingKind.None || comp.InputType == StackType.F4 || comp.InputType == StackType.F8) continue;
            if (!test.FalseInst.MatchNop())
            {
                int t = RawIL.StartOf(test.TrueInst), e = RawIL.StartOf(test.FalseInst);
                if (t < 0 || e < 0 || t > e) continue;
            }
            int bodyStart = RawIL.StartOf(test.TrueInst);
            RawIL.Op branch, not, zero, compare;
            if (bodyStart <= 0 || !raw.EndsAt(bodyStart, out branch) || (branch.Code != ILOpCode.Brfalse && branch.Code != ILOpCode.Brfalse_s)) continue;
            if (!raw.Near(branch.Offset, -1, out not) || not.Code != ILOpCode.Ceq) continue;
            if (!raw.Near(branch.Offset, -2, out zero) || zero.Code != ILOpCode.Ldc_i4_0) continue;
            if (!raw.Near(branch.Offset, -3, out compare)) continue;
            if (compare.Code != ILOpCode.Ceq && compare.Code != ILOpCode.Cgt && compare.Code != ILOpCode.Cgt_un && compare.Code != ILOpCode.Clt && compare.Code != ILOpCode.Clt_un) continue;
            int start = RawIL.StartOf(comp);
            if (start < 0 || start > compare.Offset) continue;
            var block = (Block)test.Parent;
            ILVariable v = f.RegisterVariable(VariableKind.Local, context.TypeSystem.FindType(KnownTypeCode.Boolean));
            test.Condition = new LdLoc(v);
            block.Instructions.Insert(test.ChildIndex, new StLoc(v, comp));
        }
    }

    // 18. Argument computed after the receiver is pushed.
    //        v.Equals(t)       (v, t of type parameters)     IL: ldloca v; ldarg t; box T; constrained. callvirt Equals
    //    is printed 'object o = t; v.Equals(o)': the decompiler names the boxed value because
    //    it reads the receiver late. In the IL the receiver is pushed first.
    private static void StackValuesInOrder(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            ILVariable s = store.Variable;
            if (!IsStatement(store) || s.Kind != VariableKind.StackSlot || s.StoreCount != 1 || s.LoadCount != 1 || s.AddressCount != 0) continue;
            if (!(store.Value is Box)) continue;
            var block = (Block)store.Parent;
            if (store.ChildIndex + 1 >= block.Instructions.Count) continue;
            ILInstruction next = block.Instructions[store.ChildIndex + 1];
            int valueStart = RawIL.StartOf(store.Value);
            if (valueStart < 0) continue;
            LdLoc use = null;
            bool plain = true, pushedBefore = false;
            foreach (ILInstruction d in next.Descendants)
            {
                if (d.MatchLdLoc(s)) { use = (LdLoc)d; break; }
                if (d.Descendants.Any(x => x.MatchLdLoc(s))) continue;
                if (!IsPlainRead(d) && !(d is LdObjIfRef)) { plain = false; break; }
                int at = RawIL.StartOf(d);
                if (at >= 0 && at < valueStart) pushedBefore = true;
            }
            if (use == null || !plain || !pushedBefore || !(use.Parent is CallInstruction)) continue;
            ILInstruction value = store.Value;
            store.Value = new Nop();
            block.Instructions.RemoveAt(store.ChildIndex);
            use.ReplaceWith(value);
        }
    }

    // 19. Fields of a struct local assigned one by one.
    //        S s = default(S);  s.f = a;  s.g = b;         IL: ldloca s; initobj S; ldloca s; a; stfld f; ...
    //    is printed 'S s = new S { f = a, g = b };', which fills a temporary and copies it
    //    ('ldloc t; stloc s'): one slot more of that type.
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
            if (v.Kind != VariableKind.Local || v.Type.Kind != TypeKind.Struct) continue;
            // One slot of that type in the method: the fields were stored into the variable itself.
            if (raw.Locals.Count(t => t.Equals(v.Type)) != 1) continue;
            if (RawIL.Own(f).OfType<StLoc>().Any(x => x.Variable != v && x.Variable.Index != v.Index && x.Variable.Type.Equals(v.Type) && x.Variable.Kind == VariableKind.Local)) continue;
            bool simple = true;
            for (int i = 1; i < init.Instructions.Count && simple; i++)
            {
                var assign = init.Instructions[i] as StObj;
                var target = assign == null ? null : assign.Target as LdFlda;
                if (target == null || !target.Target.MatchLdLoca(temp)) simple = false;
                else if (assign.Value.Descendants.Any(x => x.MatchLdLoc(temp) || x.MatchLdLoca(temp))) simple = false;
            }
            if (!simple) continue;
            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            var statements = init.Instructions.ToList();
            init.Instructions.Clear();
            ILInstruction created = first.Value;
            first.Value = new Nop();
            store.Value = created;
            for (int i = 1; i < statements.Count; i++)
            {
                ((LdFlda)((StObj)statements[i]).Target).Target = new LdLoca(v);
                block.Instructions.Insert(at + i, statements[i]);
            }
        }
    }

    // 21. Variable assigned in a 'try' and returned after it.
    //        T r = null;  try { ...; r = e; } catch { r = null; ...; throw; }  return r;
    //    is printed 'try { ...; return e; } ...'. The slot the value goes through before the
    //    'leave' is written elsewhere too: the slot the compiler keeps for a 'return' inside a
    //    protected block is only written in front of a 'leave'.
    private static void TryResultVariables(ILFunction f, RawIL raw)
    {
        foreach (TryCatch statement in RawIL.Own(f).OfType<TryCatch>().ToList())
        {
            if (!IsStatement(statement)) continue;
            var body = statement.TryBlock as BlockContainer;
            if (body == null || body.Blocks.Count != 1) continue;
            var outer = (Block)statement.Parent;
            if (statement.ChildIndex != outer.Instructions.Count - 1) continue;
            Block block = body.EntryPoint;
            if (block.Instructions.Count == 0) continue;
            var result = block.Instructions[block.Instructions.Count - 1] as Leave;
            if (result == null || !result.IsLeavingFunction || result.Value.MatchNop() || result.Value is LdLoc) continue;
            if (statement.Descendants.OfType<Leave>().Count(l => l.IsLeavingFunction) != 1) continue;
            RawIL.Op store, leave, load, ret;
            if (!StoreAfter(result.Value, raw, out store)) continue;
            if (!raw.Near(store.Offset, 1, out leave) || (leave.Code != ILOpCode.Leave && leave.Code != ILOpCode.Leave_s)) continue;
            if (!raw.IsAt(leave.Operand, out load) || !RawIL.IsLdLoc(load.Code) || load.Operand != store.Operand) continue;
            if (!raw.Near(load.Offset, 1, out ret) || ret.Code != ILOpCode.Ret) continue;
            // Another write of the slot that no 'leave' follows.
            bool written = false;
            for (int i = 0; i + 1 < raw.Ops.Count; i++)
                if (RawIL.IsStLoc(raw.Ops[i].Code) && raw.Ops[i].Operand == store.Operand && raw.Ops[i + 1].Code != ILOpCode.Leave && raw.Ops[i + 1].Code != ILOpCode.Leave_s) written = true;
            if (!written) continue;
            IType type = raw.LocalType(store.Operand);
            if (type == null || type.Kind == TypeKind.Unknown) continue;
            var existing = f.Variables.Where(x => x.Kind == VariableKind.Local && x.Index == store.Operand && x.LoadCount + x.StoreCount + x.AddressCount > 0).ToList();
            // The decompiler split the slot into one variable per write that nothing reads: one again.
            if (existing.Count > 1)
            {
                if (existing.Any(x => x.LoadCount != 0 || x.AddressCount != 0 || !x.Type.Equals(existing[0].Type))) continue;
                foreach (StLoc write in RawIL.Own(f).OfType<StLoc>().Where(x => existing.Contains(x.Variable) && x.Variable != existing[0]).ToList())
                    write.Variable = existing[0];
            }
            ILVariable v = existing.Count >= 1 ? existing[0] : f.RegisterVariable(VariableKind.Local, type);
            BlockContainer function = result.TargetContainer;
            ILInstruction value = result.Value;
            result.Value = new Nop();
            block.Instructions[block.Instructions.Count - 1] = new StLoc(v, value);
            block.Instructions.Add(new Leave(body));
            outer.Instructions.Add(new Leave(function, new LdLoc(v)));
        }
    }

    // 22. Variable assigned inside an argument.
    //        a.M(v = e);  v.f = x;             IL: a; e; dup; stloc v; callvirt M; ldloc v; ...
    //    is printed 'A r = a; T s = e; T v = s; r.M(s);', which stores e before the call
    //    ('stloc; ldloc') and names the receiver.
    private static void AssignedArguments(ILFunction f, RawIL raw)
    {
        foreach (StLoc first in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            if (!IsStatement(first) || first.Parent == null) continue;
            var block = (Block)first.Parent;
            int at = first.ChildIndex;
            if (at + 2 >= block.Instructions.Count) continue;
            ILVariable s = first.Variable;
            if (s.Kind != VariableKind.StackSlot || s.StoreCount != 1 || s.LoadCount != 2 || s.AddressCount != 0) continue;
            var copy = block.Instructions[at + 1] as StLoc;
            var call = block.Instructions[at + 2] as CallInstruction;
            if (copy == null || call == null || !copy.Value.MatchLdLoc(s) || copy.Variable.Kind != VariableKind.Local || !copy.Variable.Index.HasValue) continue;
            // 'dup; stloc v' right after the value.
            RawIL.Op dup, store;
            int end = RawIL.EndOf(first.Value);
            if (end < 0 || !raw.IsAt(end, out dup) || dup.Code != ILOpCode.Dup || !raw.Near(end, 1, out store) || !RawIL.IsStLoc(store.Code) || store.Operand != copy.Variable.Index.Value) continue;
            var earlier = new List<StLoc>();
            for (int i = at - 1; i >= 0; i--)
            {
                var pushed = block.Instructions[i] as StLoc;
                if (pushed == null || pushed.Variable.Kind != VariableKind.StackSlot || pushed.Variable.StoreCount != 1 || pushed.Variable.LoadCount != 1 || pushed.Variable.AddressCount != 0) break;
                if (!IsPlainRead(pushed.Value) || !pushed.Value.Descendants.All(IsPlainRead)) break;
                if (!call.Descendants.Any(x => x.MatchLdLoc(pushed.Variable))) break;
                earlier.Insert(0, pushed);
            }
            LdLoc use = null;
            var pushes = new List<LdLoc>();
            bool plain = true;
            foreach (ILInstruction d in call.Descendants)
            {
                if (d.MatchLdLoc(s)) { use = (LdLoc)d; break; }
                if (pushes.Count < earlier.Count && d.MatchLdLoc(earlier[pushes.Count].Variable)) { pushes.Add((LdLoc)d); continue; }
                if (d.Descendants.Any(x => x.MatchLdLoc(s))) continue;
                if (!IsPlainRead(d)) { plain = false; break; }
            }
            if (use == null || !plain || use.Parent != call || pushes.Count != earlier.Count) continue;
            int callStart = earlier.Count > 0 ? RawIL.StartOf(earlier[0].Value) : RawIL.StartOf(call), valueStart = RawIL.StartOf(first.Value);
            if (callStart < 0 || valueStart < 0 || callStart >= valueStart) continue;
            ILInstruction value = first.Value;
            first.Value = new Nop();
            for (int i = 0; i < earlier.Count; i++)
            {
                ILInstruction pushedValue = earlier[i].Value;
                earlier[i].Value = new Nop();
                pushes[i].ReplaceWith(pushedValue);
            }
            block.Instructions.RemoveRange(at - earlier.Count, 2 + earlier.Count);
            use.ReplaceWith(new StLoc(copy.Variable, value));
        }
    }

    // 23. Loop at the start of an endless loop.
    //        while (true) { while (c) { A }  B }       IL: br C; L: A; C: c; brtrue L; B; br C
    //    is printed 'while (true) { if (c) { A; continue; } B }', which tests c first
    //    ('C: c; brfalse N; A; br C; N: B').
    private static void InnerWhiles(ILFunction f, RawIL raw)
    {
        foreach (BlockContainer loop in RawIL.Own(f).OfType<BlockContainer>().ToList())
        {
            if (loop.Kind != ContainerKind.Loop || loop.Blocks.Count != 1) continue;
            Block entry = loop.EntryPoint;
            if (entry.Instructions.Count < 2) continue;
            var test = entry.Instructions[0] as IfInstruction;
            if (test == null || !test.FalseInst.MatchNop()) continue;
            var then = test.TrueInst as Block;
            if (then == null || then.Instructions.Count < 2) continue;
            var again = then.Instructions[then.Instructions.Count - 1] as Branch;
            if (again == null || again.TargetBlock != entry) continue;
            if (then.Descendants.OfType<Branch>().Count(b => b.TargetBlock == entry) != 1 || then.Descendants.OfType<Leave>().Any(l => l.TargetContainer == loop)) continue;
            int bodyStart = RawIL.StartOf(then.Instructions[0]), conditionStart = RawIL.StartOf(test.Condition);
            RawIL.Op jump;
            if (bodyStart <= 0 || conditionStart < 0 || bodyStart >= conditionStart) continue;
            if (!raw.EndsAt(bodyStart, out jump) || !IsBr(jump.Code) || jump.Operand != conditionStart) continue;

            ILInstruction condition = test.Condition;
            test.Condition = new Nop();
            var statements = then.Instructions.ToList();
            then.Instructions.Clear();
            statements.RemoveAt(statements.Count - 1);
            var inner = new BlockContainer(ContainerKind.While);
            var header = new Block();
            var body = new Block();
            body.Instructions.AddRange(statements);
            body.Instructions.Add(new Branch(header));
            header.Instructions.Add(new IfInstruction(condition, new Branch(body), new Leave(inner)));
            inner.Blocks.Add(header);
            inner.Blocks.Add(body);
            entry.Instructions[0] = inner;
        }
    }

    // 24. Variable assigned in both branches, then passed.
    //        E b;  if (c) b = x; else b = y;  F(t, b, p)       IL: x; stloc b; br J; y; stloc b; J: t; ldloc b; p; call F
    //    is printed 'F(body: c ? x : y, type: t, parameters: p)': named arguments to keep the
    //    order, and a conditional that leaves its value on the stack.
    private static bool NamedArgumentChoices(ILFunction f, RawIL raw)
    {
        bool changed = false;
        foreach (Block named in RawIL.Own(f).OfType<Block>().ToList())
        {
            if (named.Kind != BlockKind.CallWithNamedArgs || named.Parent == null || named.Instructions.Count == 0) continue;
            var first = named.Instructions[0] as StLoc;
            var choice = first == null ? null : first.Value as IfInstruction;
            if (choice == null || choice.ResultType == StackType.Void) continue;
            int t = RawIL.StartOf(choice.TrueInst), e = RawIL.StartOf(choice.FalseInst);
            if (t < 0 || e < 0 || t == e) continue;
            bool trueFirst = t < e;
            ILInstruction one = trueFirst ? choice.TrueInst : choice.FalseInst, two = trueFirst ? choice.FalseInst : choice.TrueInst;
            RawIL.Op s1, s2, jump;
            if (!StoreAfter(one, raw, out s1) || !raw.Near(s1.Offset, 1, out jump) || !IsBr(jump.Code)) continue;
            if (!StoreAfter(two, raw, out s2) || s2.Operand != s1.Operand || s2.Offset == s1.Offset || jump.Operand != s2.End) continue;
            IType type = raw.LocalType(s1.Operand);
            if (type == null || type.Kind == TypeKind.Unknown) continue;
            // The call is the first thing its statement evaluates.
            ILInstruction statement = StatementOf(named);
            if (statement == null) continue;
            bool firstEvaluated = false;
            foreach (ILInstruction d in statement.Descendants)
            {
                if (d == named) { firstEvaluated = true; break; }
                if (named.IsDescendantOf(d) || d.IsDescendantOf(named)) continue;
                if (!IsPlainRead(d)) break;
            }
            if (!firstEvaluated) continue;
            for (ILInstruction p = named.Parent; p != statement; p = p.Parent)
                if (!(p is CallInstruction || p is CastClass || p is Leave || p is StObj || p is StLoc)) firstEvaluated = false;
            if (!firstEvaluated) continue;

            var block = (Block)statement.Parent;
            ILVariable v = f.RegisterVariable(VariableKind.Local, type);
            ILInstruction condition = choice.Condition, positive;
            first.Value = new Nop();
            choice.Condition = new Nop();
            choice.TrueInst = new Nop();
            choice.FalseInst = new Nop();
            if (!trueFirst)
            {
                if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); condition = positive; }
                else condition = Comp.LogicNot(condition);
            }
            var then = new Block();
            then.Instructions.Add(new StLoc(v, one));
            var otherwise = new Block();
            otherwise.Instructions.Add(new StLoc(v, two));
            first.Value = new LdLoc(v);
            block.Instructions.Insert(statement.ChildIndex, new IfInstruction(condition, then, otherwise));
            changed = true;
        }
        return changed;
    }

    // 32. Returns in front of the end of a switch section.
    //        if (a && b) return;  if (c) return;  X;  break;       IL: a; brfalse N; b; brfalse N; ret; N: c; brfalse M; ret; M: X
    //    is printed 'if ((!a || !b) && !c) { X }  break;', whose tests all jump to the end of
    //    the section. A test that jumps over a 'ret' of its own is a 'return;'.
    private static void Conjuncts(ILInstruction condition, List<ILInstruction> list)
    {
        ILInstruction lhs, rhs;
        if (condition.MatchLogicAnd(out lhs, out rhs)) { Conjuncts(lhs, list); Conjuncts(rhs, list); }
        else list.Add(condition);
    }

    private static void GuardsBeforeBreak(ILFunction f, RawIL raw)
    {
        var whole = f.Body as BlockContainer;
        if (whole == null || f.ReturnType.Kind != TypeKind.Void || f.MoveNextMethod != null) return;
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test) || test.Parent == null || !test.FalseInst.MatchNop()) continue;
            var block = (Block)test.Parent;
            int at = test.ChildIndex;
            if (at + 2 != block.Instructions.Count) continue;
            var exit = block.Instructions[at + 1] as Leave;
            var body = test.TrueInst as Block;
            if (exit == null || exit.IsLeavingFunction || !exit.Value.MatchNop() || exit.TargetContainer.Kind != ContainerKind.Switch || body == null || body.Instructions.Count == 0) continue;
            var parts = new List<ILInstruction>();
            Conjuncts(test.Condition, parts);
            int guards = 0;
            foreach (ILInstruction part in parts)
            {
                RawIL.Op branch, own;
                int end = RawIL.EndOf(part);
                if (end < 0 || !(raw.IsAt(end, out branch) && RawIL.IsCondBranch(branch.Code)) && !(raw.EndsAt(end, out branch) && RawIL.IsCondBranch(branch.Code))) break;
                if (!raw.Near(branch.Offset, 1, out own) || own.Code != ILOpCode.Ret || branch.Operand != own.End) break;
                guards++;
            }
            if (guards == 0) continue;
            foreach (ILInstruction part in parts) part.ReplaceWith(new Nop());
            test.Condition = new Nop();
            var statements = new List<ILInstruction>();
            for (int i = 0; i < guards; i++) statements.Add(new IfInstruction(NegatedDisjunction(parts[i]), new Leave(whole)));
            if (guards < parts.Count)
            {
                ILInstruction rest = parts[guards];
                for (int i = guards + 1; i < parts.Count; i++) rest = IfInstruction.LogicAnd(rest, parts[i]);
                test.TrueInst = new Nop();
                statements.Add(new IfInstruction(rest, body));
            }
            else
            {
                var moved = body.Instructions.ToList();
                body.Instructions.Clear();
                statements.AddRange(moved);
            }
            block.Instructions.RemoveAt(at);
            for (int i = 0; i < statements.Count; i++) block.Instructions.Insert(at + i, statements[i]);
        }
    }

    // 33. 'break;' after a protected block, in the last switch of a void method.
    //        switch (x) { ... default: foreach (..) { }  break; }      IL: endfinally; ret   (the last 'ret', which the other sections jump to)
    //    is printed 'foreach (..) { }  return;', which has a 'ret' of its own in front of the last one.
    private static void BreakAfterProtected(ILFunction f, RawIL raw)
    {
        var whole = f.Body as BlockContainer;
        if (whole == null || f.ReturnType.Kind != TypeKind.Void || f.MoveNextMethod != null || whole.Blocks.Count != 1 || raw.Ops.Count == 0) return;
        RawIL.Op final = raw.Ops[raw.Ops.Count - 1];
        if (final.Code != ILOpCode.Ret || !raw.CondTargets.Contains(final.Offset)) return;
        foreach (Leave exit in RawIL.Own(f).OfType<Leave>().ToList())
        {
            if (!exit.IsLeavingFunction || !exit.Value.MatchNop() || !IsStatement(exit) || exit.ChildIndex == 0) continue;
            var block = (Block)exit.Parent;
            if (exit.ChildIndex != block.Instructions.Count - 1) continue;
            // The 'return;' ends the protected block itself.
            var inner = block.Parent as BlockContainer;
            ILInstruction guarded = inner == null ? null : inner.Parent;
            bool holds = guarded != null && inner.Kind == ContainerKind.Normal && inner.Blocks.Count == 1 && ((guarded is TryFinally && ((TryFinally)guarded).TryBlock == inner)
                || (guarded is UsingInstruction && ((UsingInstruction)guarded).Body == inner) || (guarded is LockInstruction && ((LockInstruction)guarded).Body == inner));
            if (!holds || !IsStatement(guarded) || guarded.Descendants.OfType<Leave>().Count(l => l.IsLeavingFunction) != 1) continue;
            var outer = (Block)guarded.Parent;
            if (guarded.ChildIndex != outer.Instructions.Count - 1) continue;
            var section = outer.Parent as BlockContainer;
            if (section == null || section.Kind != ContainerKind.Switch || !IsStatement(section)) continue;
            var top = (Block)section.Parent;
            if (top.Parent != whole) continue;
            int after = section.ChildIndex + 1;
            bool last = after == top.Instructions.Count || (after == top.Instructions.Count - 1 && top.Instructions[after] is Leave && ((Leave)top.Instructions[after]).IsLeavingFunction && ((Leave)top.Instructions[after]).Value.MatchNop());
            if (!last) continue;
            int end = RawIL.EndOf(guarded);
            // (The range of the block goes over the 'ret' its 'leave' lands on.)
            if ((end != final.Offset && end != final.End) || !raw.RegionEnds.Contains(final.Offset)) continue;
            exit.ReplaceWith(new Leave(inner));
            outer.Instructions.Add(new Leave(section));
        }
    }

    // 34. 'return;' right after a protected block.
    //        lock (o) { A; }  return;              IL: A; leave R; <handler>; R: ret      (R is not the last 'ret')
    //    is printed 'lock (o) { A; break; }' in a loop that ends the method, or with the
    //    'return;' inside: both leave to the end of the method, past the other statements.
    private static void ReturnBehindProtected(ILFunction f, RawIL raw)
    {
        var whole = f.Body as BlockContainer;
        if (whole == null || f.ReturnType.Kind != TypeKind.Void || f.MoveNextMethod != null || raw.Ops.Count == 0) return;
        int final = raw.Ops[raw.Ops.Count - 1].Offset;
        foreach (Leave exit in RawIL.Own(f).OfType<Leave>().ToList())
        {
            if (!exit.Value.MatchNop() || !IsStatement(exit) || exit.ChildIndex == 0) continue;
            var block = (Block)exit.Parent;
            if (exit.ChildIndex != block.Instructions.Count - 1) continue;
            var inner = block.Parent as BlockContainer;
            ILInstruction guarded = inner == null ? null : inner.Parent;
            bool holds = guarded != null && inner.Kind == ContainerKind.Normal && inner.Blocks.Count == 1 && ((guarded is TryFinally && ((TryFinally)guarded).TryBlock == inner)
                || (guarded is UsingInstruction && ((UsingInstruction)guarded).Body == inner) || (guarded is LockInstruction && ((LockInstruction)guarded).Body == inner));
            if (!holds || !IsStatement(guarded) || exit.TargetContainer == inner) continue;
            // It leaves the method, or a loop that ends it.
            if (!exit.IsLeavingFunction)
            {
                BlockContainer loop = exit.TargetContainer;
                var top = loop.Parent as Block;
                if (loop.Kind == ContainerKind.Normal || loop.Kind == ContainerKind.Switch || top == null || top.Parent != whole || whole.Blocks.Count != 1) continue;
                int after = loop.ChildIndex + 1;
                bool last = after == top.Instructions.Count || (after == top.Instructions.Count - 1 && top.Instructions[after] is Leave && ((Leave)top.Instructions[after]).IsLeavingFunction && ((Leave)top.Instructions[after]).Value.MatchNop());
                if (!last) continue;
            }
            // The protected region that holds the statement in front of it, and where its 'leave' lands.
            int inside = RawIL.StartOf(block.Instructions[exit.ChildIndex - 1]);
            int[] region = null;
            foreach (int[] r in raw.Regions)
                if (inside >= r[0] && inside < r[1] && (region == null || r[1] - r[0] < region[1] - region[0])) region = r;
            RawIL.Op closing, landing;
            if (inside < 0 || region == null || !raw.EndsAt(region[1], out closing) || (closing.Code != ILOpCode.Leave && closing.Code != ILOpCode.Leave_s)) continue;
            if (closing.Operand != region[3] || !raw.IsAt(closing.Operand, out landing) || landing.Code != ILOpCode.Ret || landing.Offset == final) continue;
            var outer = (Block)guarded.Parent;
            exit.ReplaceWith(new Leave(inner));
            outer.Instructions.Insert(guarded.ChildIndex + 1, new Leave(whole));
            // What followed is not reached any more.
            while (outer.Instructions.Count > guarded.ChildIndex + 2) outer.Instructions.RemoveAt(outer.Instructions.Count - 1);
        }
    }

    // 35. Test of a loop written in the one section of a switch that reaches it.
    //        while (true) { switch (x) { case a: return A; case b: if (c) return true; break; case d: ... } }
    //    IL: the test of c stands between the sections. It is printed
    //    'do { switch (x) { ... case b: break; ... } } while (!c); return true;', which has the test after them all.
    private static void LoopTestInSection(ILFunction f)
    {
        foreach (BlockContainer loop in RawIL.Own(f).OfType<BlockContainer>().ToList())
        {
            if (loop.Kind != ContainerKind.DoWhile || loop.Blocks.Count != 2 || !IsStatement(loop)) continue;
            Block first = loop.Blocks[0], test = loop.Blocks[1];
            if (first.Instructions.Count != 2 || test.Instructions.Count != 1) continue;
            var section = first.Instructions[0] as BlockContainer;
            var toTest = first.Instructions[1] as Branch;
            var check = test.Instructions[0] as IfInstruction;
            if (section == null || section.Kind != ContainerKind.Switch || toTest == null || toTest.TargetBlock != test || check == null) continue;
            var again = check.TrueInst as Branch;
            var exit = check.FalseInst as Leave;
            if (again == null || again.TargetBlock != first || exit == null || exit.TargetContainer != loop || !exit.Value.MatchNop()) continue;
            if (loop.Descendants.OfType<Branch>().Count(b => b.TargetBlock == test) != 1 || loop.Descendants.OfType<Leave>().Count(l => l.TargetContainer == loop) != 1) continue;
            // What follows the loop: 'return v;' with a constant, or the end of a void method.
            var top = (Block)loop.Parent;
            if (loop.ChildIndex != top.Instructions.Count - 2) continue;
            var result = top.Instructions[top.Instructions.Count - 1] as Leave;
            if (result == null || !result.IsLeavingFunction || !(result.Value.MatchNop() || result.Value is LdcI4)) continue;
            // One section reaches the test.
            var sw = section.EntryPoint.Instructions.Count == 1 ? section.EntryPoint.Instructions[0] as SwitchInstruction : null;
            var reaching = section.Descendants.OfType<Leave>().Where(l => l.TargetContainer == section).ToList();
            if (sw == null || reaching.Count != 1 || !(reaching[0].Parent is SwitchSection)) continue;
            // The test stands before the start of another section.
            int at = RawIL.StartOf(check.Condition);
            if (at < 0 || !section.Blocks.Skip(1).Any(b => b.StartILOffset > at)) continue;
            ILInstruction condition = check.Condition, positive;
            check.Condition = new Nop();
            if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); condition = positive; }
            else condition = Comp.LogicNot(condition);
            var inside = new Block();
            inside.Instructions.Add(new IfInstruction(condition, new Leave(result.TargetContainer, result.Value.Clone())));
            inside.Instructions.Add(new Leave(section));
            section.Blocks.Add(inside);
            reaching[0].ReplaceWith(new Branch(inside));
            first.Instructions[1] = new Branch(first);
            loop.Blocks.RemoveAt(1);
            loop.Kind = ContainerKind.Loop;
            top.Instructions.RemoveAt(top.Instructions.Count - 1);
        }
    }

    // 36. Protected block under a test, at the end of the body of a loop.
    //        if (x != null) { try { } finally { } }        IL: x; brfalse NEXT; <try>
    //    is printed 'if (x == null) continue;  try { } finally { }', which marks a label in
    //    front of the 'try': the compiler then puts a 'nop' there ('brfalse NEXT; nop; <try>').
    private static void GuardsBeforeTry(ILFunction f, RawIL raw)
    {
        foreach (IfInstruction guard in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(guard) || guard.Parent == null || !guard.FalseInst.MatchNop()) continue;
            var skip = guard.TrueInst as Branch;
            var braces = guard.TrueInst as Block;
            if (skip == null && braces != null && braces.Instructions.Count == 1) skip = braces.Instructions[0] as Branch;
            var block = (Block)guard.Parent;
            int at = guard.ChildIndex, n = block.Instructions.Count;
            if (skip == null || at + 2 >= n) continue;
            var back = block.Instructions[n - 1] as Branch;
            if (back == null || back.TargetBlock != skip.TargetBlock) continue;
            ILInstruction next = block.Instructions[at + 1];
            if (!(next is TryCatch || next is TryFinally)) continue;
            // The 'try' starts right after the test, with no 'nop' in between.
            int end = RawIL.EndOf(guard.Condition);
            RawIL.Op branch, first;
            if (end < 0 || !(raw.IsAt(end, out branch) && RawIL.IsCondBranch(branch.Code)) && !(raw.EndsAt(end, out branch) && RawIL.IsCondBranch(branch.Code))) continue;
            if (!raw.Near(branch.Offset, 1, out first) || first.Code == ILOpCode.Nop || !raw.Regions.Any(r => r[0] == first.Offset)) continue;
            ILInstruction condition = guard.Condition, positive;
            guard.Condition = new Nop();
            if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); condition = positive; }
            else condition = Comp.LogicNot(condition);
            var body = new Block();
            var moved = new List<ILInstruction>();
            for (int i = at + 1; i < n - 1; i++) moved.Add(block.Instructions[i]);
            block.Instructions.RemoveRange(at, n - 1 - at);
            body.Instructions.AddRange(moved);
            block.Instructions.Insert(at, new IfInstruction(condition, body));
        }
    }

    // 31. Return after an 'else' that throws.
    //        if (c) { A } else { B; throw E; }  return x;      IL: c; brfalse L; A; br R; L: B; E; throw; R: x; ret
    //    is printed 'if (c) { A; return x; }  B; throw E;', which returns before B.
    private static void ReturnsAfterElse(ILFunction f, RawIL raw)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test) || test.Parent == null || !test.FalseInst.MatchNop()) continue;
            var then = test.TrueInst as Block;
            if (then == null || then.Instructions.Count < 2) continue;
            var exit = then.Instructions[then.Instructions.Count - 1] as Leave;
            if (exit == null || !exit.IsLeavingFunction || exit.Value.MatchNop()) continue;
            if (then.Descendants.OfType<Leave>().Count(l => l.IsLeavingFunction) != 1) continue;
            var block = (Block)test.Parent;
            int at = test.ChildIndex, n = block.Instructions.Count;
            if (at + 1 >= n || !(block.Instructions[n - 1] is Throw)) continue;
            bool plain = true;
            for (int i = at + 1; i < n; i++)
                if (block.Instructions[i].Descendants.Any(d => d is Leave || d is Branch)) plain = false;
            if (!plain) continue;
            int value = RawIL.StartOf(exit.Value), other = RawIL.StartOf(block.Instructions[n - 1]);
            RawIL.Op returned;
            if (value < 0 || other < 0 || value < other || RawIL.StartOf(then.Instructions[0]) > other) continue;
            // The value is right in front of the 'ret', and jumps reach it.
            if (!raw.IsAt(value, out returned) || !raw.Targets.Contains(value)) continue;
            var otherwise = new Block();
            var moved = new List<ILInstruction>();
            for (int i = at + 1; i < n; i++) moved.Add(block.Instructions[i]);
            block.Instructions.RemoveRange(at + 1, n - at - 1);
            otherwise.Instructions.AddRange(moved);
            then.Instructions.RemoveAt(then.Instructions.Count - 1);
            test.FalseInst = otherwise;
            block.Instructions.Add(exit);
        }
    }

    // 30. Test of the value in the 'default' of a switch on it.
    //        default: if (c == ' ' || M(c)) { A } else { return; } break;
    //    IL: (after the other sections) c; ' '; beq LA; c; call M; brfalse R; LA: A
    //    is printed 'default: if (!M(c)) return; goto case ' '; case ' ': A; break;': the
    //    decompiler takes the comparison for a label. A label of the source is tested with
    //    the others, in front of the sections.
    private static void DefaultTests(ILFunction f, RawIL raw)
    {
        foreach (BlockContainer container in RawIL.Own(f).OfType<BlockContainer>().ToList())
        {
            if (container.Kind != ContainerKind.Switch || container.EntryPoint.Instructions.Count != 1) continue;
            var sw = container.EntryPoint.Instructions[0] as SwitchInstruction;
            var subject = sw == null ? null : sw.Value as LdLoc;
            if (subject == null) continue;
            SwitchSection fallback = sw.Sections.FirstOrDefault(s => s.Labels.Contains(long.MinValue));
            var toRest = fallback == null ? null : fallback.Body as Branch;
            Block rest = toRest == null ? null : toRest.TargetBlock;
            if (rest == null || rest.Parent != container || rest.IncomingEdgeCount != 1 || rest.Instructions.Count != 2) continue;
            var test = rest.Instructions[0] as IfInstruction;
            var join = rest.Instructions[1] as Branch;
            if (test == null || join == null || !test.FalseInst.MatchNop()) continue;
            var exit = test.TrueInst as Leave;
            Block body = join.TargetBlock;
            if (exit == null || !exit.Value.MatchNop() || body.Parent != container || body.IncomingEdgeCount != 2) continue;
            SwitchSection labelled = sw.Sections.FirstOrDefault(s => s.Body is Branch && ((Branch)s.Body).TargetBlock == body);
            if (labelled == null || labelled == fallback || labelled.HasNullLabel || labelled.Labels.Count() != 1) continue;
            long k = labelled.Labels.Values.First();
            int start = RawIL.StartOf(test.Condition);
            RawIL.Op equal, constant;
            if (start < 0 || !raw.Near(start, -1, out equal) || (equal.Code != ILOpCode.Beq && equal.Code != ILOpCode.Beq_s)) continue;
            if (!raw.Near(start, -2, out constant) || !RawIL.IsLdcI4(constant.Code) || constant.Operand != k) continue;
            // (The range of a compound assignment starts after the 'ldarg.0' of its target.)
            int landing = equal.Operand, bodyStart = body.Instructions.Count == 0 ? -1 : RawIL.StartOf(body.Instructions[0]);
            RawIL.Op pushed;
            while (landing < bodyStart && raw.IsAt(landing, out pushed) && RawIL.IsLdArg(pushed.Code)) landing = pushed.End;
            if (bodyStart < 0 || landing != bodyStart) continue;

            ILInstruction condition = test.Condition, positive;
            test.Condition = new Nop();
            if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); condition = positive; }
            else condition = Comp.LogicNot(condition);
            test.TrueInst = new Nop();
            var then = new Block();
            var moved = body.Instructions.ToList();
            body.Instructions.Clear();
            then.Instructions.AddRange(moved);
            var otherwise = new Block();
            otherwise.Instructions.Add(exit);
            ILInstruction both = IfInstruction.LogicOr(new Comp(ComparisonKind.Equality, Sign.None, new LdLoc(subject.Variable), new LdcI4((int)k)), condition);
            rest.Instructions.Clear();
            rest.Instructions.Add(new IfInstruction(both, then, otherwise));
            sw.Sections.Remove(labelled);
            fallback.Labels = fallback.Labels.UnionWith(labelled.Labels);
            container.Blocks.Remove(body);
        }
    }

    // 29. First label of a 'switch' on a string, left out of it.
    //        switch (s) { case "a": case "b": A; break; case "c": B; break; }
    //    IL: s; "a"; op_Equality; brtrue LA; s; "b"; op_Equality; brtrue LA; s; "c"; ...
    //    is printed 'if (!(s == "a")) { switch (s) { case "b": break; case "c": B; goto E; } }  A;'
    //    when the decompiler takes the first test for an 'if'. The label goes back in front of
    //    the others, in the section that falls to what follows the 'if'.
    private static void StringSwitchHeads(ILFunction f, RawIL raw)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test) || test.Parent == null || !test.FalseInst.MatchNop()) continue;
            ILInstruction equal;
            if (!test.Condition.MatchLogicNot(out equal)) continue;
            var call = equal as Call;
            if (call == null || call.Method.Name != "op_Equality" || call.Arguments.Count != 2 || !call.Method.DeclaringType.IsKnownType(KnownTypeCode.String)) continue;
            var subject = call.Arguments[0] as LdLoc;
            var literal = call.Arguments[1] as LdStr;
            var then = test.TrueInst as Block;
            if (subject == null || literal == null || then == null || then.Instructions.Count != 1) continue;
            var container = then.Instructions[0] as BlockContainer;
            if (container == null || container.Kind != ContainerKind.Switch) continue;
            var sw = container.EntryPoint.Instructions.Count == 1 ? container.EntryPoint.Instructions[0] as SwitchInstruction : null;
            var map = sw == null ? null : sw.Value as StringToInt;
            if (map == null || !map.Argument.MatchLdLoc(subject.Variable) || map.Map.Any(m => m.Key == literal.Value || m.Key == null)) continue;
            // The first test jumps when the strings are equal, like the others.
            RawIL.Op jump;
            int end = RawIL.EndOf(call);
            if (end < 0 || !raw.IsAt(end, out jump) || (jump.Code != ILOpCode.Brtrue && jump.Code != ILOpCode.Brtrue_s)) continue;
            int count = map.Map.Count;
            SwitchSection leaving = null, fallback = null;
            bool plain = true;
            foreach (SwitchSection section in sw.Sections)
            {
                if (section.Labels.Contains(count)) { fallback = section; continue; }
                if (section.HasNullLabel || section.Labels.IsEmpty || section.Labels.Values.Any(x => x < 0 || x >= count)) { plain = false; break; }
                var leave = section.Body as Leave;
                if (leave != null && leave.TargetContainer == container && leave.Value.MatchNop()) leaving = section;
            }
            if (!plain || leaving == null || fallback == null || fallback.HasNullLabel) continue;
            var values = new List<(string, int)> { (literal.Value, 0) };
            foreach (var entry in map.Map) values.Add((entry.Key, entry.Value + 1));
            var taken = new ICSharpCode.Decompiler.Util.LongSet(0);
            foreach (SwitchSection section in sw.Sections)
            {
                if (section == fallback) continue;
                var labels = section.Labels.AddOffset(1);
                if (section == leaving) labels = labels.UnionWith(new ICSharpCode.Decompiler.Util.LongSet(0));
                section.Labels = labels;
                taken = taken.UnionWith(labels);
            }
            fallback.Labels = taken.Invert();
            ILInstruction argument = map.Argument;
            map.Argument = new Nop();
            sw.Value = new StringToInt(argument, values, map.ExpectedType);
            then.Instructions.Clear();
            test.ReplaceWith(container);
        }
    }

    // 28. Condition assigned in both branches.
    //        bool b;  if (c) b = x; else b = y;  if (b) { }    IL: x; stloc b; br J; y; stloc b; J: ldloc b; brfalse
    //    is printed 'if (c ? x : y) { }', which jumps from each branch.
    private static void ConditionChoices(ILFunction f, RawIL raw)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test) || test.Parent == null) continue;
            var choice = test.Condition as IfInstruction;
            // 'if (!b)': the same under a negation.
            ILInstruction inner;
            bool negated = choice == null && test.Condition.MatchLogicNot(out inner) && inner is IfInstruction;
            if (negated) { test.Condition.MatchLogicNot(out inner); choice = (IfInstruction)inner; }
            if (choice == null || choice.ResultType == StackType.Void) continue;
            ILInstruction same, other;
            if (choice.MatchLogicAnd(out same, out other) || choice.MatchLogicOr(out same, out other)) continue;
            int t = RawIL.StartOf(choice.TrueInst), e = RawIL.StartOf(choice.FalseInst);
            if (t < 0 || e < 0 || t == e) continue;
            bool trueFirst = t < e;
            ILInstruction one = trueFirst ? choice.TrueInst : choice.FalseInst, two = trueFirst ? choice.FalseInst : choice.TrueInst;
            RawIL.Op s1, s2, jump, read, tested;
            if (!StoreAfter(one, raw, out s1) || !raw.Near(s1.Offset, 1, out jump) || !IsBr(jump.Code)) continue;
            // (The second value may store in several places itself, 'a || b': its last store is the one in front of the join.)
            if (!raw.Near(jump.Operand, -1, out s2) || !RawIL.IsStLoc(s2.Code) || s2.Operand != s1.Operand || s2.Offset <= s1.Offset || jump.Operand != s2.End || RawIL.StartOf(two) <= s1.Offset) continue;
            if (!raw.IsAt(s2.End, out read) || !RawIL.IsLdLoc(read.Code) || read.Operand != s1.Operand) continue;
            if (!raw.Near(read.Offset, 1, out tested) || !IsBranchOnBool(tested.Code)) continue;
            IType type = raw.LocalType(s1.Operand);
            if (type == null || !type.IsKnownType(KnownTypeCode.Boolean)) continue;
            var block = (Block)test.Parent;
            ILVariable v = f.RegisterVariable(VariableKind.Local, type);
            ILInstruction condition = choice.Condition, positive;
            test.Condition = new Nop();
            choice.Condition = new Nop();
            choice.TrueInst = new Nop();
            choice.FalseInst = new Nop();
            if (!trueFirst)
            {
                if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); condition = positive; }
                else condition = Comp.LogicNot(condition);
            }
            var then = new Block();
            then.Instructions.Add(new StLoc(v, one));
            var otherwise = new Block();
            otherwise.Instructions.Add(new StLoc(v, two));
            test.Condition = negated ? (ILInstruction)Comp.LogicNot(new LdLoc(v)) : new LdLoc(v);
            block.Instructions.Insert(test.ChildIndex, new IfInstruction(condition, then, otherwise));
            StoredDisjunction((StLoc)otherwise.Instructions[0], raw);
        }
    }

    private static ILInstruction NegatedDisjunction(ILInstruction condition)
    {
        ILInstruction lhs, rhs, positive;
        if (condition.MatchLogicOr(out lhs, out rhs))
        {
            lhs.ReplaceWith(new Nop());
            rhs.ReplaceWith(new Nop());
            return IfInstruction.LogicAnd(NegatedDisjunction(lhs), NegatedDisjunction(rhs));
        }
        if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); return positive; }
        if (condition.Parent != null) condition.ReplaceWith(new Nop());
        return Comp.LogicNot(condition);
    }

    // 28b. 'b = !a || y' with the constant stored last.
    //        if (a) b = y; else b = true;          IL: a; <branch> T; y; stloc b; br J; T: ldc.i4.1; stloc b; J:
    //     ('b = !a || y' leaves the value on the stack and stores it once.)
    private static void StoredDisjunction(StLoc store, RawIL raw)
    {
        ILInstruction lhs, rhs;
        if (!store.Value.MatchLogicOr(out lhs, out rhs) || !IsStatement(store)) return;
        RawIL.Op kept, jump, last, constant;
        if (!StoreAfter(rhs, raw, out kept) || !raw.Near(kept.Offset, 1, out jump) || !IsBr(jump.Code)) return;
        if (!raw.Near(jump.Operand, -1, out last) || !RawIL.IsStLoc(last.Code) || last.Operand != kept.Operand || last.Offset <= jump.Offset) return;
        if (!raw.Near(jump.Operand, -2, out constant) || constant.Code != ILOpCode.Ldc_i4_1 || constant.Offset != jump.End) return;
        var block = (Block)store.Parent;
        ILVariable v = store.Variable;
        store.Value = new Nop();
        lhs.ReplaceWith(new Nop());
        rhs.ReplaceWith(new Nop());
        var then = new Block();
        then.Instructions.Add(new StLoc(v, rhs));
        var otherwise = new Block();
        otherwise.Instructions.Add(new StLoc(v, new LdcI4(1)));
        block.Instructions[store.ChildIndex] = new IfInstruction(NegatedDisjunction(lhs), then, otherwise);
    }

    // 25. Variable returned after a protected block.
    //        lock (o) { ...; A; }  return v;       IL: A; leave L; <handler>; L: ldloc v; ret
    //    is printed 'lock (o) { ...; A; return v; }', which goes through the slot the compiler
    //    keeps for returns inside a protected block ('ldloc v; stloc r; leave').
    private static void ReturnsAfterProtected(ILFunction f, RawIL raw)
    {
        foreach (Leave result in RawIL.Own(f).OfType<Leave>().ToList())
        {
            var load = result.Value as LdLoc;
            if (load == null || !result.IsLeavingFunction || !load.Variable.Index.HasValue || load.Variable.Kind != VariableKind.Local) continue;
            var block = result.Parent as Block;
            if (block == null || result.ChildIndex != block.Instructions.Count - 1 || block.Instructions.Count < 2) continue;
            var inner = block.Parent as BlockContainer;
            if (inner == null || inner.Blocks.Count != 1 || inner.Kind != ContainerKind.Normal) continue;
            ILInstruction statement = inner.Parent;
            if (!(statement is LockInstruction || statement is UsingInstruction || (statement is TryFinally && ((TryFinally)statement).TryBlock == inner))) continue;
            if (!IsStatement(statement)) continue;
            var outer = (Block)statement.Parent;
            if (statement.ChildIndex != outer.Instructions.Count - 1) continue;
            int end = RawIL.EndOf(block.Instructions[block.Instructions.Count - 2]);
            RawIL.Op leave, target, ret;
            if (end < 0 || !raw.IsAt(end, out leave) || (leave.Code != ILOpCode.Leave && leave.Code != ILOpCode.Leave_s)) continue;
            if (!raw.IsAt(leave.Operand, out target) || !RawIL.IsLdLoc(target.Code) || target.Operand != load.Variable.Index.Value) continue;
            if (!raw.Near(target.Offset, 1, out ret) || ret.Code != ILOpCode.Ret) continue;
            BlockContainer function = result.TargetContainer;
            block.Instructions[block.Instructions.Count - 1] = new Leave(inner);
            outer.Instructions.Add(new Leave(function, new LdLoc(load.Variable)));
        }
    }

    // Start of a statement, without the ranges of its 'return;': in the tree every 'ret' of a
    // void method is the same exit, and each 'leave' carries the offset of the first.
    private static int StartWithoutExits(ILInstruction inst)
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

    // True when falling off the end of 'block' leaves the method.
    private static bool IsTail(Block block, BlockContainer body)
    {
        while (true)
        {
            ILInstruction parent = block.Parent;
            if (parent == body) return body.Blocks.Count == 1;
            var test = parent as IfInstruction;
            var holder = test == null ? null : test.Parent as Block;
            if (holder == null) return false;
            int n = holder.Instructions.Count, at = test.ChildIndex;
            if (at != n - 1)
            {
                var closing = holder.Instructions[n - 1] as Leave;
                if (at != n - 2 || closing == null || closing.TargetContainer != body || !closing.Value.MatchNop()) return false;
            }
            block = holder;
        }
    }

    // 26. Rest of the method in the 'default' of a switch.
    //        switch (x) { case 1: A; break; case 2: break; default: R; break; }       at the end of a void method
    //    sends 'case 2' to the 'ret' that ends the method. The decompiler prints
    //    'switch (x) { case 1: A; return; case 2: return; }  R', where 'case 2' has a 'ret'
    //    of its own in front of R.
    private static void DefaultHoldsRest(ILFunction f, Block block, RawIL raw)
    {
        var body = f.Body as BlockContainer;
        if (body == null || f.ReturnType.Kind != TypeKind.Void || f.MoveNextMethod != null || raw.Ops.Count == 0) return;
        int lastRet = raw.Ops[raw.Ops.Count - 1].Offset;
        if (raw.Ops[raw.Ops.Count - 1].Code != ILOpCode.Ret) return;
        for (int k = 0; k + 1 < block.Instructions.Count; k++)
        {
            var sw = block.Instructions[k] as BlockContainer;
            if (sw == null || sw.Kind != ContainerKind.Switch || sw.EntryPoint.Instructions.Count == 0) continue;
            var inst = sw.EntryPoint.Instructions[sw.EntryPoint.Instructions.Count - 1] as SwitchInstruction;
            if (inst == null) continue;
            SwitchSection fallback = null;
            var breaks = new List<SwitchSection>();
            foreach (SwitchSection section in inst.Sections)
            {
                var leave = section.Body as Leave;
                if (leave == null || !leave.Value.MatchNop()) continue;
                if (leave.TargetContainer == sw && section.Labels.Contains(long.MaxValue)) fallback = section;
                else if (leave.TargetContainer == body) breaks.Add(section);
            }
            // One case leaves the method, and a branch of the switch goes to the last 'ret'
            // (the range of a 'return;' is not kept: every 'ret' is the same exit in the tree).
            int from = RawIL.StartOf(inst.Value), to = StartWithoutExits(block.Instructions[k + 1]);
            if (fallback == null || breaks.Count != 1) continue;
            if (from < 0 || to <= from) continue;
            bool direct = false;
            foreach (RawIL.Op op in raw.Ops)
                if (op.Offset >= from && op.Offset < to && RawIL.IsCondBranch(op.Code) && op.Operand == lastRet) direct = true;
            foreach (var table in raw.Switches)
                if (table.Key >= from && table.Key < to && table.Value.Contains(lastRet)) direct = true;
            if (!direct) continue;
            if (sw.Descendants.OfType<Leave>().Count(l => l.TargetContainer == sw) != 1) continue;
            int n = block.Instructions.Count;
            var closing = block.Instructions[n - 1] as Leave;
            if (closing != null && closing.TargetContainer == body && closing.Value.MatchNop() && block.Parent == body) n--;
            if (n <= k + 1 || !IsTail(block, body)) continue;
            // The case jumps straight to the end: a conditional branch or a switch table does.
            if (!raw.CondTargets.Contains(lastRet)) continue;

            var rest = new Block();
            var moved = new List<ILInstruction>();
            for (int i = k + 1; i < n; i++) moved.Add(block.Instructions[i]);
            block.Instructions.RemoveRange(k + 1, n - k - 1);
            rest.Instructions.AddRange(moved);
            if (!rest.HasFlag(InstructionFlags.EndPointUnreachable)) rest.Instructions.Add(new Leave(sw));
            sw.Blocks.Add(rest);
            fallback.Body = new Branch(rest);
            foreach (SwitchSection section in breaks) section.Body = new Leave(sw);
            return;
        }
    }

    // The block 'continue;' jumps to in a loop: its condition, or the increment of a 'for'.
    private static Block ContinueTarget(BlockContainer loop)
    {
        switch (loop.Kind)
        {
            case ContainerKind.While: return loop.EntryPoint;
            case ContainerKind.DoWhile: case ContainerKind.For: return loop.Blocks[loop.Blocks.Count - 1];
            default: return null;
        }
    }

    // 27. 'continue;' and 'break;' inside a 'try' that ends the body of a loop.
    //        try { ...; if (!c) continue;  R } catch { }           IL: c; brtrue L; leave NEXT; L: R
    //    is printed 'try { ...; if (c) { R } }' ('c; brfalse END; R; END: leave NEXT').
    //        try { ...; if (c) { T } else { break; } } catch { }   IL: T; br END; leave OUT; END: leave NEXT
    //    is printed 'try { ...; if (c) { T; continue; } } catch { }  break;' ('T; leave NEXT; leave OUT').
    private static void ExitsInTry(ILFunction f, RawIL raw)
    {
        foreach (TryCatch caught in RawIL.Own(f).OfType<TryCatch>().ToList())
        {
            if (!IsStatement(caught)) continue;
            var body = caught.TryBlock as BlockContainer;
            // 'try { } catch { } finally { }': the 'try' of the 'finally' holds the 'catch' alone.
            ILInstruction statement = caught;
            var holder = (Block)caught.Parent;
            var wrapped = holder.Parent as BlockContainer;
            var last = holder.Instructions[holder.Instructions.Count - 1] as Leave;
            if (wrapped != null && wrapped.Blocks.Count == 1 && wrapped.Parent is TryFinally && ((TryFinally)wrapped.Parent).TryBlock == wrapped && IsStatement(wrapped.Parent)
                && (holder.Instructions.Count == 1 || (holder.Instructions.Count == 2 && last != null && last.TargetContainer == wrapped))) statement = wrapped.Parent;
            var outer = (Block)statement.Parent;
            var loop = outer.Parent as BlockContainer;
            // The same under an 'if' of the loop: 'if (a) { try { } catch { } break; } rest'.
            var under = outer.Parent as IfInstruction;
            if (loop == null && under != null && under.TrueInst == outer && under.Parent is Block && statement.ChildIndex + 1 == outer.Instructions.Count - 1 && outer.Instructions[statement.ChildIndex + 1] is Leave)
                loop = under.Parent.Parent as BlockContainer;
            // Or the 'try' ends an 'if' that ends the body of the loop: 'if (a) { try { } catch { } }'.
            bool underTail = false;
            if (loop == null && under != null && under.TrueInst == outer && under.FalseInst.MatchNop() && under.Parent is Block && statement.ChildIndex == outer.Instructions.Count - 1
                && under.ChildIndex + 1 == ((Block)under.Parent).Instructions.Count - 1 && ((Block)under.Parent).Instructions[under.ChildIndex + 1] is Branch)
            {
                loop = under.Parent.Parent as BlockContainer;
                underTail = loop != null;
            }
            if (body == null || body.Blocks.Count != 1 || loop == null) continue;
            Block next = ContinueTarget(loop);
            Block block = body.EntryPoint;
            int n = block.Instructions.Count;
            if (next == null || next == outer || n == 0) continue;
            // Either the 'if' ends the block, or the 'leave' that closes the 'try' follows it.
            var closing = block.Instructions[n - 1] as Leave;
            int at = closing != null && closing.TargetContainer == body ? n - 2 : n - 1;
            var test = at < 0 ? null : block.Instructions[at] as IfInstruction;
            // The same at the end of an 'else' that ends the 'try'.
            bool nestedTail = false;
            while (test != null && test.FalseInst is Block && ((Block)test.FalseInst).Instructions.Count > 0 && ((Block)test.FalseInst).Instructions[((Block)test.FalseInst).Instructions.Count - 1] is IfInstruction)
            {
                block = (Block)test.FalseInst;
                n = block.Instructions.Count;
                at = n - 1;
                test = (IfInstruction)block.Instructions[at];
                nestedTail = true;
            }
            if (test == null || !test.FalseInst.MatchNop()) continue;
            var then = test.TrueInst as Block;
            if (then == null || then.Instructions.Count == 0) continue;
            int after = statement.ChildIndex + 1;
            if (!underTail && after != outer.Instructions.Count - 1) continue;
            ILInstruction follow = underTail ? ((Block)under.Parent).Instructions[under.ChildIndex + 1] : outer.Instructions[after];
            var toNext = follow as Branch;
            var toOut = follow as Leave;
            RawIL.Op branch, leave, jump, target;

            if (toNext != null && toNext.TargetBlock == next && (at == n - 2 || nestedTail))
            {
                // 'if (!a) continue; if (b) { }' printed 'if (a && b) { }': the 'leave' is behind the test of a.
                ILInstruction lhs, rhs;
                if (test.Condition.MatchLogicAnd(out lhs, out rhs))
                {
                    int lhsEnd = RawIL.EndOf(lhs), rhsStart = RawIL.StartOf(rhs);
                    if (lhsEnd >= 0 && rhsStart >= 0
                        && ((raw.IsAt(lhsEnd, out branch) && RawIL.IsCondBranch(branch.Code)) || (raw.EndsAt(lhsEnd, out branch) && RawIL.IsCondBranch(branch.Code)))
                        && raw.Near(branch.Offset, 1, out leave) && (leave.Code == ILOpCode.Leave || leave.Code == ILOpCode.Leave_s)
                        && leave.End == rhsStart && branch.Operand == rhsStart && raw.RegionEnds.Contains(leave.Operand))
                    {
                        var both = (IfInstruction)test.Condition;
                        both.Condition = new Nop();
                        both.TrueInst = new Nop();
                        test.Condition = rhs;
                        ILInstruction inner, negated;
                        if (lhs.MatchLogicNot(out inner)) { inner.ReplaceWith(new Nop()); negated = inner; }
                        else negated = Comp.LogicNot(lhs);
                        block.Instructions.Insert(at, new IfInstruction(negated, new Branch(next)));
                        continue;
                    }
                }
                // 'if (!c) continue;': a 'leave' right behind the test, to where the 'try' ends up.
                int conditionEnd = RawIL.EndOf(test.Condition), bodyStart = RawIL.StartOf(then.Instructions[0]);
                if (conditionEnd < 0 || bodyStart < 0) continue;
                if (!(raw.IsAt(conditionEnd, out branch) && RawIL.IsCondBranch(branch.Code)) && !(raw.EndsAt(conditionEnd, out branch) && RawIL.IsCondBranch(branch.Code))) continue;
                if (!raw.Near(branch.Offset, 1, out leave) || (leave.Code != ILOpCode.Leave && leave.Code != ILOpCode.Leave_s)) continue;
                if (leave.End != bodyStart || branch.Operand != bodyStart || !raw.RegionEnds.Contains(leave.Operand)) continue;
                ILInstruction condition = test.Condition, positive;
                test.Condition = new Nop();
                if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); condition = positive; }
                else condition = Comp.LogicNot(condition);
                var moved = then.Instructions.ToList();
                then.Instructions.Clear();
                block.Instructions.RemoveRange(at, n - at);
                block.Instructions.Add(new IfInstruction(condition, new Branch(next)));
                block.Instructions.AddRange(moved);
                if (!nestedTail && !block.Instructions[block.Instructions.Count - 1].HasFlag(InstructionFlags.EndPointUnreachable)) block.Instructions.Add(new Leave(body));
            }
            else if (!nestedTail && !underTail && toOut != null && toOut.TargetContainer == loop && toOut.Value.MatchNop() && then.Instructions.Count >= 2)
            {
                // 'else { break; }': the branch that goes on jumps over a 'leave' to the 'leave' of the 'try'.
                var again = then.Instructions[then.Instructions.Count - 1] as Branch;
                if (again == null || again.TargetBlock != next) continue;
                // 'br END; leave OUT; END: leave NEXT' where this 'try' ends (the end of the
                // branch is not always known: a switch has the range of its last 'break').
                int tryStart = StartWithoutExits(block), found = 0;
                if (tryStart < 0) continue;
                for (int i = 0; i + 2 < raw.Ops.Count; i++)
                {
                    jump = raw.Ops[i]; leave = raw.Ops[i + 1]; target = raw.Ops[i + 2];
                    if (jump.Offset <= tryStart || !IsBr(jump.Code) || jump.Operand != target.Offset) continue;
                    if ((leave.Code != ILOpCode.Leave && leave.Code != ILOpCode.Leave_s) || (target.Code != ILOpCode.Leave && target.Code != ILOpCode.Leave_s)) continue;
                    if (target.Operand == leave.Operand || !raw.RegionEnds.Contains(target.End)) continue;
                    // The first protected block that ends after the start of this one.
                    if (raw.RegionEnds.Any(e => e > tryStart && e < target.End)) continue;
                    found++;
                }
                if (found != 1) continue;
                then.Instructions.RemoveAt(then.Instructions.Count - 1);
                var otherwise = new Block();
                otherwise.Instructions.Add(new Leave(loop));
                test.FalseInst = otherwise;
                if (at == n - 1) block.Instructions.Add(new Leave(body));
                outer.Instructions[after] = new Branch(next);
            }
        }
    }

    // 20. End of a void method inside an endless loop.
    //        while (true) { ...; if (c) { A; return; } ... }
    //    has A between the test and the rest of the loop. The decompiler prints
    //    'if (c) break;' and A after the loop.
    private static void EndInLoop(ILFunction f, Block block, RawIL raw)
    {
        var body = f.Body as BlockContainer;
        if (body == null || f.ReturnType.Kind != TypeKind.Void || f.MoveNextMethod != null || block.Parent != body || body.Blocks.Count != 1) return;
        int n = block.Instructions.Count;
        var closing = n > 0 ? block.Instructions[n - 1] as Leave : null;
        if (closing != null && closing.TargetContainer == body && closing.Value.MatchNop()) n--;
        for (int k = 0; k + 1 < n; k++)
        {
            var loop = block.Instructions[k] as BlockContainer;
            if (loop == null || loop.Kind != ContainerKind.Loop) continue;
            var breaks = loop.Descendants.OfType<Leave>().Where(l => l.TargetContainer == loop).ToList();
            if (breaks.Count != 1 || !(breaks[0].Parent is Block || breaks[0].Parent is IfInstruction)) continue;
            int loopStart = RawIL.StartOf(loop), loopEnd = RawIL.EndOf(loop);
            bool inside = loopStart >= 0;
            for (int i = k + 1; i < n && inside; i++)
            {
                int at = RawIL.StartOf(block.Instructions[i]);
                if (at < 0 || at <= loopStart || at >= loopEnd) inside = false;
                // (Or an 'if' that goes nowhere else.)
                bool plainTest = block.Instructions[i] is IfInstruction && !block.Instructions[i].Descendants.Any(d => d is Leave || d is Branch || d is BlockContainer);
                if (!(block.Instructions[i] is CallInstruction || block.Instructions[i] is StObj || block.Instructions[i] is StLoc || plainTest)) inside = false;
            }
            if (!inside) continue;
            var moved = new List<ILInstruction>();
            for (int i = k + 1; i < n; i++) moved.Add(block.Instructions[i]);
            block.Instructions.RemoveRange(k + 1, n - k - 1);
            moved.Add(new Leave(body));
            if (breaks[0].Parent is Block)
            {
                var holder = (Block)breaks[0].Parent;
                int index = breaks[0].ChildIndex;
                holder.Instructions.RemoveAt(index);
                for (int i = 0; i < moved.Count; i++) holder.Instructions.Insert(index + i, moved[i]);
            }
            else
            {
                var exit = new Block();
                exit.Instructions.AddRange(moved);
                breaks[0].ReplaceWith(exit);
            }
            return;
        }
    }

    // 14. Second test of a loop, written in its condition.
    //        for (...; a && b; ...) { }        IL: br C; B: body; C: a; brfalse EXIT; b; brtrue B
    //    is printed 'for (...; a; ...) { if (!b) break; ... }' when the decompiler takes only
    //    the first test as the condition. b is in the condition when it stands after the body
    //    in the IL, right behind a.
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
            if (body.Parent != loop || body == header || body.Instructions.Count < 2) continue;
            var second = body.Instructions[0] as IfInstruction;
            if (second == null || !second.FalseInst.MatchNop()) continue;
            Leave quit = SingleLeave(second.TrueInst);
            if (quit == null || quit.TargetContainer != loop || !quit.Value.MatchNop()) continue;
            int a = RawIL.StartOf(test.Condition), aEnd = RawIL.EndOf(test.Condition), b = RawIL.StartOf(second.Condition), next = RawIL.StartOf(body.Instructions[1]);
            // A body of that test alone: what follows is the increment of the 'for'.
            var toStep = body.Instructions[1] as Branch;
            if (toStep != null && body.Instructions.Count == 2 && loop.Kind == ContainerKind.For && toStep.TargetBlock.Parent == loop && toStep.TargetBlock != header && toStep.TargetBlock != body && toStep.TargetBlock.Instructions.Count > 0)
                next = RawIL.StartOf(toStep.TargetBlock.Instructions[0]);
            RawIL.Op branch;
            if (a < 0 || b < 0 || next < 0 || b <= a || b <= next) continue;
            // b starts right after the branch of a.
            if (!(raw.IsAt(aEnd, out branch) && RawIL.IsCondBranch(branch.Code)) && !(raw.EndsAt(aEnd, out branch) && RawIL.IsCondBranch(branch.Code))) continue;
            if (branch.End != b) continue;
            ILInstruction first = test.Condition, negated = second.Condition, positive;
            test.Condition = new Nop();
            second.Condition = new Nop();
            if (negated.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); negated = positive; }
            else negated = Comp.LogicNot(negated);
            body.Instructions.RemoveAt(0);
            test.Condition = IfInstruction.LogicAnd(first, negated);
        }
    }

    // 4. 'yield break;' in front of the rest of an iterator.
    //        if (!c) yield break;  rest
    //    has 'ldc.i4.0; ret' in MoveNext between the test and the rest. The decompiler prints
    //    'if (c) { rest }', whose test jumps to the 'ldc.i4.0; ret' that ends MoveNext.
    private static void YieldBreaks(ILFunction f, RawIL raw)
    {
        var body = f.Body as BlockContainer;
        if (f.MoveNextMethod == null || body == null || body.Blocks.Count != 1) return;
        Block block = body.EntryPoint;
        while (true)
        {
            int n = block.Instructions.Count;
            if (n == 0) return;
            var last = block.Instructions[n - 1] as Leave;
            bool closing = last != null && last.TargetContainer == body && last.Value.MatchNop();
            int at = closing ? n - 2 : n - 1;
            if (at < 0) return;
            var test = block.Instructions[at] as IfInstruction;
            if (test == null || !test.FalseInst.MatchNop()) return;
            var then = test.TrueInst as Block;
            if (then == null || then.Instructions.Count == 0) return;
            int start = RawIL.StartOf(then.Instructions[0]);
            RawIL.Op ret, zero, branch, self;
            // A hoisted local is a field of the iterator: its 'ldarg.0' has no range.
            if (start > 0 && raw.EndsAt(start, out self) && self.Code == ILOpCode.Ldarg_0) start = self.Offset;
            // 'this' has the range of the copy MoveNext makes at its start: the block knows better.
            if (then.StartILOffset > 0 && raw.EndsAt(then.StartILOffset, out ret) && ret.Code == ILOpCode.Ret) start = then.StartILOffset;
            if (start < 0 || !raw.EndsAt(start, out ret) || ret.Code != ILOpCode.Ret) return;
            if (!raw.EndsAt(ret.Offset, out zero) || zero.Code != ILOpCode.Ldc_i4_0) return;
            if (!raw.EndsAt(zero.Offset, out branch) || !RawIL.IsCondBranch(branch.Code) || branch.Operand != start) return;

            ILInstruction condition = test.Condition, positive;
            test.Condition = new Nop();
            if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); condition = positive; }
            else condition = Comp.LogicNot(condition);
            var moved = then.Instructions.ToList();
            then.Instructions.Clear();
            block.Instructions.RemoveRange(at, n - at);
            block.Instructions.Add(new IfInstruction(condition, new Leave(body)));
            block.Instructions.AddRange(moved);
            if (!block.Instructions[block.Instructions.Count - 1].HasFlag(InstructionFlags.EndPointUnreachable)) block.Instructions.Add(new Leave(body));
        }
    }

    // 2. 'else' that throws, in a protected block.
    //        if (c) { T } else { R; throw; }
    //    compiles to 'T; br L; R; throw; L: leave'. The decompiler prints
    //    'if (c) { T; return; } R; throw;', where T ends on a 'leave' of its own. (Outside a
    //    protected block the 'br' to the end is a 'ret' in both forms.)
    private static void ElseThatThrows(Block block, RawIL raw)
    {
        int n = block.Instructions.Count;
        for (int k = 0; k + 1 < n; k++)
        {
            var test = block.Instructions[k] as IfInstruction;
            if (test == null || !test.FalseInst.MatchNop()) continue;
            var then = test.TrueInst as Block;
            if (then == null || then.Instructions.Count < 2) continue;
            // 'return;', or 'continue;' in a loop.
            ILInstruction exit = then.Instructions[then.Instructions.Count - 1];
            var exitLeave = exit as Leave;
            if (!(exit is Branch) && (exitLeave == null || !exitLeave.Value.MatchNop())) continue;
            ILInstruction last = block.Instructions[n - 1];
            if (!(last is Throw) && !(last is Rethrow)) continue;
            int thenEnd = RawIL.EndOf(then.Instructions[then.Instructions.Count - 2]), restStart = RawIL.StartOf(block.Instructions[k + 1]), restEnd = RawIL.EndOf(last);
            RawIL.Op jump, target;
            if (thenEnd < 0 || restStart < 0 || restEnd < 0 || !raw.IsAt(thenEnd, out jump) || !IsBr(jump.Code)) continue;
            if (jump.End != restStart || jump.Operand != restEnd || !raw.IsAt(restEnd, out target)) continue;
            if (target.Code != ILOpCode.Leave && target.Code != ILOpCode.Leave_s) continue;

            then.Instructions.RemoveAt(then.Instructions.Count - 1);
            var rest = new Block();
            var moved = new List<ILInstruction>();
            for (int i = k + 1; i < n; i++) moved.Add(block.Instructions[i]);
            block.Instructions.RemoveRange(k + 1, n - k - 1);
            rest.Instructions.AddRange(moved);
            test.FalseInst = rest;
            block.Instructions.Add(exit);
            return;
        }
    }

    private static bool Debug(ILFunction f)
    {
        string debug = System.Environment.GetEnvironmentVariable("DECOMP_DEBUG");
        return debug != null && debug.Split(';').Contains(f.Method.FullName);
    }

    private static bool IsBr(ILOpCode c)
    {
        return c == ILOpCode.Br || c == ILOpCode.Br_s;
    }

    // The 'stloc' that takes the value of an expression: at the end of its range, or the
    // last instruction inside it.
    private static bool StoreAfter(ILInstruction value, RawIL raw, out RawIL.Op store)
    {
        store = default(RawIL.Op);
        int end = RawIL.EndOf(value);
        if (end < 0) return false;
        if (raw.IsAt(end, out store) && RawIL.IsStLoc(store.Code)) return true;
        return raw.EndsAt(end, out store) && RawIL.IsStLoc(store.Code);
    }

    private static Leave SingleLeave(ILInstruction inst)
    {
        var block = inst as Block;
        if (block != null && block.Instructions.Count == 1) inst = block.Instructions[0];
        return inst as Leave;
    }

    private static void ResultVariable(ILFunction f, RawIL raw)
    {
        var body = f.Body as BlockContainer;
        if (body == null || f.ReturnType.Kind == TypeKind.Void) return;
        // The one 'ldloc r; ret' that branches jump to.
        int join = -1, slot = -1;
        for (int i = 1; i + 1 < raw.Ops.Count; i++)
        {
            RawIL.Op load = raw.Ops[i];
            if (!RawIL.IsLdLoc(load.Code) || raw.Ops[i + 1].Code != ILOpCode.Ret || !raw.Targets.Contains(load.Offset)) continue;
            if (!RawIL.IsStLoc(raw.Ops[i - 1].Code) || raw.Ops[i - 1].Operand != load.Operand) continue;
            if (join >= 0) return;
            join = load.Offset;
            slot = load.Operand;
        }
        if (join < 0) return;
        // Jumps to it come from 'stloc r; br J' only.
        for (int i = 1; i < raw.Ops.Count; i++)
        {
            RawIL.Op op = raw.Ops[i];
            if (op.Operand != join) continue;
            bool branch = IsBr(op.Code) || RawIL.IsCondBranch(op.Code) || op.Code == ILOpCode.Leave || op.Code == ILOpCode.Leave_s;
            if (!branch) continue;
            if (!IsBr(op.Code) || !RawIL.IsStLoc(raw.Ops[i - 1].Code) || raw.Ops[i - 1].Operand != slot) return;
        }
        foreach (int[] table in raw.Switches.Values) if (table.Contains(join)) return;

        var assigning = new HashSet<Leave>();
        Leave final = null;
        foreach (Leave leave in RawIL.Own(f).OfType<Leave>())
        {
            if (!leave.IsLeavingFunction || leave.Value.MatchNop()) continue;
            RawIL.Op store, next;
            if (!StoreAfter(leave.Value, raw, out store) || store.Operand != slot || !raw.Near(store.Offset, 1, out next)) continue;
            if (next.Offset == join) { if (final != null) return; final = leave; assigning.Add(leave); }
            else if (IsBr(next.Code) && next.Operand == join) assigning.Add(leave);
        }
        if (final == null || assigning.Count < 2) return;
        var top = final.Parent as Block;
        if (top == null || top.Kind != BlockKind.ControlFlow || final.ChildIndex != top.Instructions.Count - 1) return;
        var covered = new HashSet<Leave>();
        if (!CanMeet(top, 0, assigning, covered) || covered.Count != assigning.Count) return;
        IType type = raw.LocalType(slot);
        if (type == null || type.Kind == TypeKind.Unknown) return;
        // (The decompiler keeps the variables it has split off a slot, unused: only those in use count.)
        var existing = f.Variables.Where(x => x.Kind == VariableKind.Local && x.Index == slot && x.LoadCount + x.StoreCount + x.AddressCount > 0).ToList();
        if (existing.Count > 1) return;
        ILVariable v = existing.Count == 1 ? existing[0] : f.RegisterVariable(VariableKind.Local, type);
        var result = new Leave(final.TargetContainer, new LdLoc(v));
        result.AddILRange(final);
        Meet(top, 0, assigning, v);
        top.Instructions.Add(result);
    }

    // 1b. Result variable of a switch.
    //        T r;  switch (x) { case 0: r = a; break; ... default: r = d; break; }  return r;
    //     IL: a; stloc r; br J; ...; d; stloc r; J: ldloc r; ret
    //     is printed 'switch (x) { case 0: return a; ... }  return d;' when every section is
    //     one value (the sections of a 64-bit switch the decompiler did not put together).
    private static void SwitchResultVariable(ILFunction f, RawIL raw)
    {
        var body = f.Body as BlockContainer;
        if (body == null || f.ReturnType.Kind == TypeKind.Void) return;
        int join = -1, slot = -1;
        for (int i = 1; i + 1 < raw.Ops.Count; i++)
        {
            RawIL.Op load = raw.Ops[i];
            if (!RawIL.IsLdLoc(load.Code) || raw.Ops[i + 1].Code != ILOpCode.Ret || !raw.Targets.Contains(load.Offset)) continue;
            if (!RawIL.IsStLoc(raw.Ops[i - 1].Code) || raw.Ops[i - 1].Operand != load.Operand) continue;
            if (join >= 0) return;
            join = load.Offset;
            slot = load.Operand;
        }
        if (join < 0) return;
        foreach (BlockContainer container in RawIL.Own(f).OfType<BlockContainer>().ToList())
        {
            var top = container.Parent as Block;
            if (container.Kind != ContainerKind.Switch || top == null || top.Parent != body || container.ChildIndex != top.Instructions.Count - 2) continue;
            var final = top.Instructions[top.Instructions.Count - 1] as Leave;
            var sw = container.EntryPoint.Instructions.Count == 1 ? container.EntryPoint.Instructions[0] as SwitchInstruction : null;
            if (final == null || sw == null || !final.IsLeavingFunction || final.Value.MatchNop()) continue;
            RawIL.Op store, next;
            if (!StoreAfter(final.Value, raw, out store) || store.Operand != slot || !raw.Near(store.Offset, 1, out next) || next.Offset != join) continue;
            SwitchSection fallback = null;
            bool shaped = true;
            var returns = new List<Leave>();
            foreach (SwitchSection section in sw.Sections)
            {
                var leave = section.Body as Leave;
                if (leave != null && leave.TargetContainer == container && leave.Value.MatchNop()) { if (fallback != null) shaped = false; fallback = section; continue; }
                var jump = section.Body as Branch;
                Block target = jump == null ? null : jump.TargetBlock;
                var value = target == null || target.Parent != container || target.Instructions.Count != 1 || target.IncomingEdgeCount != 1 ? null : target.Instructions[0] as Leave;
                if (value == null || !value.IsLeavingFunction || value.Value.MatchNop() || !StoreAfter(value.Value, raw, out store) || store.Operand != slot
                    || !raw.Near(store.Offset, 1, out next) || !IsBr(next.Code) || next.Operand != join) { shaped = false; break; }
                returns.Add(value);
            }
            if (!shaped || fallback == null || returns.Count < 2 || returns.Count != container.Blocks.Count - 1) continue;
            IType type = raw.LocalType(slot);
            if (type == null || type.Kind == TypeKind.Unknown) continue;
            if (f.Variables.Any(x => x.Kind == VariableKind.Local && x.Index == slot && x.LoadCount + x.StoreCount + x.AddressCount > 0)) continue;
            ILVariable v = f.RegisterVariable(VariableKind.Local, type);
            foreach (Leave value in returns)
            {
                var block = (Block)value.Parent;
                ILInstruction e = value.Value;
                value.Value = new Nop();
                block.Instructions.Clear();
                block.Instructions.Add(new StLoc(v, e));
                block.Instructions.Add(new Leave(container));
            }
            ILInstruction last = final.Value;
            final.Value = new LdLoc(v);
            var rest = new Block();
            rest.Instructions.Add(new StLoc(v, last));
            rest.Instructions.Add(new Leave(container));
            container.Blocks.Add(rest);
            fallback.Body = new Branch(rest);
            return;
        }
    }

    // True when the statements of 'block' from 'from' on can end on a shared point: the last
    // one is an assigning return, and each 'if' before it that ends on one takes the rest
    // as its 'else'.
    private static bool CanMeet(Block block, int from, HashSet<Leave> assigning, HashSet<Leave> covered)
    {
        int n = block.Instructions.Count;
        if (n <= from) return false;
        var last = block.Instructions[n - 1] as Leave;
        if (last == null || !assigning.Contains(last)) return false;
        covered.Add(last);
        for (int k = from; k < n - 1; k++)
        {
            var test = block.Instructions[k] as IfInstruction;
            if (test == null || !test.FalseInst.MatchNop()) continue;
            if (!test.TrueInst.Descendants.OfType<Leave>().Any(assigning.Contains) && !assigning.Contains(test.TrueInst as Leave)) continue;
            Leave single = test.TrueInst as Leave;
            if (single != null) { covered.Add(single); continue; }
            var then = test.TrueInst as Block;
            if (then == null || !CanMeet(then, 0, assigning, covered)) return false;
        }
        return true;
    }

    private static void Meet(Block block, int from, HashSet<Leave> assigning, ILVariable v)
    {
        int n = block.Instructions.Count;
        for (int k = from; k < n - 1; k++)
        {
            var test = block.Instructions[k] as IfInstruction;
            if (test == null || !test.FalseInst.MatchNop()) continue;
            var then = test.TrueInst as Block;
            var single = test.TrueInst as Leave;
            if (single != null && assigning.Contains(single))
            {
                then = new Block();
                test.TrueInst = then;
                then.Instructions.Add(single);
            }
            if (then == null || then.Instructions.Count == 0 || !assigning.Contains(then.Instructions[then.Instructions.Count - 1] as Leave)) continue;
            Meet(then, 0, assigning, v);
            var rest = new Block();
            var moved = new List<ILInstruction>();
            for (int i = k + 1; i < n; i++) moved.Add(block.Instructions[i]);
            block.Instructions.RemoveRange(k + 1, n - k - 1);
            rest.Instructions.AddRange(moved);
            Meet(rest, 0, assigning, v);
            test.FalseInst = rest;
            return;
        }
        var last = (Leave)block.Instructions[n - 1];
        // No variable: the value is the same everywhere, the one 'return' that follows reads it.
        if (v == null) { block.Instructions.RemoveAt(n - 1); return; }
        ILInstruction value = last.Value;
        last.Value = new Nop();
        block.Instructions[n - 1] = new StLoc(v, value);
    }
}
