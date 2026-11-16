// IL transform: gives a void method its early returns back.
//
// The decompiler treats every 'ret' of a void method as the same exit, so
//     if (!c) return;  body;          (IL: brtrue L; ret; L: body; ret)
// and
//     if (c) { body }                 (IL: brfalse E; body; E: ret)
// reach it as the same tree, and it prints whichever its nesting heuristics prefer. The
// compiler does not: the two forms give different IL. The method body still tells them
// apart: the first has a 'ret' between the condition and the body. This transform reads
// the raw IL and rewrites the trailing 'if' of a function to the form the IL has.
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreEarlyReturn : IILTransform
{
    public void Run(ILFunction function, ILTransformContext context)
    {
        var functions = function.Descendants.OfType<ILFunction>().ToList();
        functions.Add(function);
        foreach (ILFunction f in functions)
        {
            if (f.Method == null || f.ReturnType.Kind != TypeKind.Void) continue;
            var body = f.Body as BlockContainer;
            if (body == null) continue;
            RawIL raw = RawIL.Read(f, context);
            if (raw == null) continue;
            List<int> rets = raw.Ops.Where(o => o.Code == ILOpCode.Ret).Select(o => o.Offset).ToList();

            if (rets.Count >= 2 || raw.Ops.Any(o => o.Code == ILOpCode.Leave || o.Code == ILOpCode.Leave_s)) ExplicitReturns(f, body, raw);
            if (body.Blocks.Count != 1) continue;
            // Inside a protected block, 'return;' is a 'leave'.
            exits = raw.Ops.Where(o => o.Code == ILOpCode.Ret || o.Code == ILOpCode.Leave || o.Code == ILOpCode.Leave_s).Select(o => o.End - 1).ToList();
            tailContainer = null;
            currentRaw = f.Method.DeclaringTypeDefinition != null && f.Method.DeclaringTypeDefinition.Name.Contains("DisplayClass") ? raw : null;
            Rewrite(body.EntryPoint, body, rets, raw);
            currentRaw = null;
        }
    }

    // A conditional branch that jumps to a 'ret' which is not the last one: that 'ret' is a
    // 'return;' statement. ('break;' out of a loop that ends the method, or the end of an
    // 'else', is a jump to the end of the method: the compiler puts a 'ret' in its place, but
    // conditional branches that reach it still go to the last 'ret'.) The decompiler prints
    // 'break;' when both leave the method.
    private static void ExplicitReturns(ILFunction f, BlockContainer body, RawIL raw)
    {
        foreach (Leave leave in RawIL.Own(f).OfType<Leave>().ToList())
        {
            if (leave.TargetContainer == body || !leave.Value.MatchNop()) continue;
            bool atRet = false, byLeave = false;
            RawIL.Op op;
            // The last 'ret' is the end of the method itself.
            int last = raw.Ops[raw.Ops.Count - 1].Offset;
            foreach (var r in leave.ILRanges)
                // 'ret; ret' at the end: a jump to the next instruction would have been dropped.
            {
                if (r.Start != last && raw.IsAt(r.Start, out op) && op.Code == ILOpCode.Ret && (raw.CondTargets.Contains(r.Start) || op.End == last)) atRet = true;
                // 'if (c) return;' is 'c; brfalse N; ret; N:'. 'if (c) break;' jumps to the end
                // with the conditional branch itself.
                RawIL.Op skip;
                if (r.Start != last && raw.IsAt(r.Start, out op) && op.Code == ILOpCode.Ret && raw.Near(r.Start, -1, out skip) && RawIL.IsCondBranch(skip.Code) && skip.Operand == op.End && (leave.Parent is IfInstruction || IsWholeBranch(leave))) atRet = true;
                // In a protected block 'return;' is a 'leave' to the 'ret' that ends the method;
                // 'break;' is a jump to the end of the switch or of the loop.
                RawIL.Op target;
                if (raw.IsAt(r.Start, out op) && (op.Code == ILOpCode.Leave || op.Code == ILOpCode.Leave_s) && leave.TargetContainer.Kind != ContainerKind.Normal
                    && !raw.RegionEnds.Contains(op.End) && raw.IsAt(op.Operand, out target) && target.Code == ILOpCode.Ret) { atRet = true; byLeave = true; }
            }
            // (A 'break;' in a protected block is a 'br' even when a 'return;' follows the switch: the 'leave' is no 'break;'.)
            if (atRet && !IsLoopCondition(leave) && ((byLeave && ExplicitReturnFollows(leave.TargetContainer, raw)) || !ReturnFollows(leave.TargetContainer))) leave.TargetContainer = body;
        }
    }

    // The only statement of an 'if' without 'else'.
    private static bool IsWholeBranch(Leave leave)
    {
        ILInstruction inst = leave;
        var block = inst.Parent as Block;
        if (block != null && block.Instructions.Count == 1 && block.Parent is IfInstruction) inst = block;
        var test = inst.Parent as IfInstruction;
        return test != null && test.TrueInst == inst && test.FalseInst.MatchNop();
    }

    // The exit of 'while (c)', 'for (;c;)' or 'do while (c)': part of the loop statement.
    private static bool IsLoopCondition(Leave leave)
    {
        BlockContainer loop = leave.TargetContainer;
        // 'while (true)' has no condition: its first block is body.
        if (loop.Kind == ContainerKind.Normal || loop.Kind == ContainerKind.Switch || loop.Kind == ContainerKind.Loop) return false;
        ILInstruction holder = leave.Parent;
        if (holder is IfInstruction) holder = holder.Parent;
        Block condition = loop.Kind == ContainerKind.DoWhile ? loop.Blocks[loop.Blocks.Count - 1] : loop.EntryPoint;
        return holder == condition;
    }

    // True when a 'return;' statement follows the switch or the loop itself.
    // (The 'leave' that ends a protected block is the end of the block, not a statement.)
    private static bool ExplicitReturnFollows(BlockContainer container, RawIL raw)
    {
        var block = container.Parent as Block;
        if (block == null || container.ChildIndex + 1 >= block.Instructions.Count) return false;
        var next = block.Instructions[container.ChildIndex + 1] as Leave;
        if (next == null || !next.IsLeavingFunction || !next.Value.MatchNop()) return false;
        RawIL.Op op;
        foreach (var r in next.ILRanges)
            if (raw.IsAt(r.Start, out op) && (op.Code == ILOpCode.Leave || op.Code == ILOpCode.Leave_s) && !raw.RegionEnds.Contains(op.End)) return true;
        // Or right where the switch ends.
        int end = RawIL.EndOf(container);
        RawIL.Op landing;
        if (end >= 0 && raw.IsAt(end, out op) && (op.Code == ILOpCode.Leave || op.Code == ILOpCode.Leave_s) && !raw.RegionEnds.Contains(op.End)
            && raw.IsAt(op.Operand, out landing) && landing.Code == ILOpCode.Ret) return true;
        return false;
    }

    // True when the statement after 'inst' is a 'return;' of its own: the 'ret' is that one.
    private static bool ReturnFollows(ILInstruction inst)
    {
        while (true)
        {
            var block = inst.Parent as Block;
            if (block == null) return false;
            if (inst.ChildIndex + 1 < block.Instructions.Count)
            {
                // The 'leave' that ends the body of the method is its end, not a statement.
                var next = block.Instructions[inst.ChildIndex + 1] as Leave;
                bool last = inst.ChildIndex + 2 == block.Instructions.Count && block.Parent is BlockContainer && block.Parent.Parent is ILFunction;
                return next != null && !last;
            }
            ILInstruction parent = block.Parent;
            if (parent is IfInstruction) inst = parent;
            else if (parent is BlockContainer && parent.Parent is Block) inst = parent;
            else return false;
        }
    }

    // Lambdas are left out: their offsets are those of another body.
    private static int Start(ILInstruction inst)
    {
        return RawIL.StartOf(inst);
    }

    // True when the instruction just before 'offset' is a 'ret': what follows was reached
    // by jumping over an early return.
    [System.ThreadStatic] private static List<int> exits;
    [System.ThreadStatic] private static BlockContainer tailContainer;

    private static bool IsTailLeave(Leave leave, BlockContainer container)
    {
        return leave != null && leave.Value.MatchNop() && (leave.TargetContainer == container || (tailContainer != null && leave.TargetContainer == tailContainer));
    }

    private static bool RetBefore(List<int> rets, int offset)
    {
        if (offset > 0 && rets.Contains(offset - 1)) return true;
        // In a lambda, 'ldarg.0; ldfld <captured this>' is rebuilt by the decompiler as one
        // variable with the range of the 'ldfld': the expression starts one instruction earlier.
        RawIL raw = currentRaw;
        RawIL.Op self;
        if (raw != null && offset > 0 && raw.EndsAt(offset, out self) && self.Code == ILOpCode.Ldarg_0)
            return self.Offset > 0 && rets.Contains(self.Offset - 1);
        return false;
    }

    [System.ThreadStatic] private static RawIL currentRaw;

    // 'block' is in tail position: falling off its end leaves the function.
    private static void Rewrite(Block block, BlockContainer container, List<int> rets, RawIL raw)
    {
        ElseFromReturn(block, container, raw);
        Nest(block, container, raw);
        while (true)
        {
            int n = block.Instructions.Count;
            if (n == 0) return;
            var last = block.Instructions[n - 1] as Leave;
            bool explicitLeave = IsTailLeave(last, container);
            int at = explicitLeave ? n - 2 : n - 1;
            if (at < 0) return;
            var ifInst = block.Instructions[at] as IfInstruction;
            if (ifInst == null)
            {
                // The last statement holds a block of its own: its end is the end of the method too.
                ILInstruction tail = block.Instructions[at];
                BlockContainer inner = null;
                if (tail is TryFinally) inner = ((TryFinally)tail).TryBlock as BlockContainer;
                else if (tail is TryCatch) inner = ((TryCatch)tail).TryBlock as BlockContainer;
                else if (tail is UsingInstruction) inner = ((UsingInstruction)tail).Body as BlockContainer;
                else if (tail is LockInstruction) inner = ((LockInstruction)tail).Body as BlockContainer;
                if (inner != null && inner.Blocks.Count == 1 && inner.Kind == ContainerKind.Normal)
                {
                    BlockContainer outerTail = tailContainer;
                    tailContainer = inner;
                    Rewrite(inner.EntryPoint, container, exits, raw);
                    tailContainer = outerTail;
                }
                return;
            }
            if (!ifInst.FalseInst.MatchNop() && ElseToReturn(block, at, ifInst, container, rets, raw)) continue;
            if (!ifInst.FalseInst.MatchNop() || !Split(block, at, ifInst, container, rets))
            {
                // Both branches of a trailing if are in tail position too.
                var t = ifInst.TrueInst as Block;
                var e = ifInst.FalseInst as Block;
                if (t != null) Rewrite(t, container, rets, raw);
                if (e != null) Rewrite(e, container, rets, raw);
                return;
            }
        }
    }

    //     if (c) { T; return; }  E        at the end of a void method
    // where a conditional branch inside T jumps to the last 'ret' of the method, past E, was
    //     if (c) { T } else { E }
    // (the end of T is the end of the method: with 'return;' that branch would go to the 'ret'
    // that ends T, which here no conditional branch reaches).
    private static void ElseFromReturn(Block block, BlockContainer container, RawIL raw)
    {
        int n = block.Instructions.Count;
        if (n == 0 || raw.Ops.Count == 0) return;
        var last = block.Instructions[n - 1] as Leave;
        int end = IsTailLeave(last, container) ? n - 1 : n;
        RawIL.Op final = raw.Ops[raw.Ops.Count - 1];
        if (final.Code != ILOpCode.Ret) return;
        for (int k = 0; k + 1 < end; k++)
        {
            var guard = block.Instructions[k] as IfInstruction;
            if (guard == null || !guard.FalseInst.MatchNop()) continue;
            var then = guard.TrueInst as Block;
            if (then == null || then.Instructions.Count < 2) continue;
            // The exit that ends T: its last statement, or the last one of the protected block that ends it.
            Block exitBlock = then;
            BlockContainer protectedBody = null;
            ILInstruction tail = then.Instructions[then.Instructions.Count - 1];
            if (tail is TryFinally) protectedBody = ((TryFinally)tail).TryBlock as BlockContainer;
            else if (tail is UsingInstruction) protectedBody = ((UsingInstruction)tail).Body as BlockContainer;
            else if (tail is LockInstruction) protectedBody = ((LockInstruction)tail).Body as BlockContainer;
            if (protectedBody != null)
            {
                if (protectedBody.Blocks.Count != 1 || protectedBody.Kind != ContainerKind.Normal || protectedBody.EntryPoint.Instructions.Count < 2) continue;
                exitBlock = protectedBody.EntryPoint;
            }
            if (!IsTailLeave(exitBlock.Instructions[exitBlock.Instructions.Count - 1] as Leave, container)) continue;
            // Nothing else leaves the rest by falling off the block.
            int conditionEnd = RawIL.EndOf(guard.Condition), restStart = RawIL.StartOfCode(block.Instructions[k + 1]);
            RawIL.Op ret;
            if (conditionEnd < 0 || restStart <= conditionEnd || !raw.EndsAt(restStart, out ret)) continue;
            bool reaches = false;
            if (ret.Code == ILOpCode.Endfinally)
            {
                // T ends on a protected block whose 'leave' goes to the last 'ret': with
                // 'return;' it would go to a 'ret' right behind the handler.
                foreach (int[] region in raw.Regions)
                {
                    RawIL.Op leave;
                    if (region[3] != restStart || region[0] < conditionEnd || !raw.EndsAt(region[1], out leave)) continue;
                    if ((leave.Code == ILOpCode.Leave || leave.Code == ILOpCode.Leave_s) && leave.Operand == final.Offset && final.Offset > restStart) reaches = true;
                }
                if (!reaches) continue;
            }
            else
            {
                if (ret.Code != ILOpCode.Ret) continue;
                if (raw.CondTargets.Contains(ret.Offset) || ret.Offset >= final.Offset) continue;
                foreach (RawIL.Op op in raw.Ops)
                    if (op.Offset >= conditionEnd && op.Offset < ret.Offset && RawIL.IsCondBranch(op.Code) && op.Operand == final.Offset && op.End != restStart) reaches = true;
                if (!reaches) continue;
            }
            // The branch of the guard itself goes to the rest.
            RawIL.Op skip;
            if (!((raw.IsAt(conditionEnd, out skip) && RawIL.IsCondBranch(skip.Code)) || (raw.EndsAt(conditionEnd, out skip) && RawIL.IsCondBranch(skip.Code))) || skip.Operand != restStart) continue;
            if (protectedBody != null && ret.Code != ILOpCode.Endfinally) continue;
            exitBlock.Instructions.RemoveAt(exitBlock.Instructions.Count - 1);
            if (protectedBody != null) exitBlock.Instructions.Add(new Leave(protectedBody));
            var rest = new Block();
            var moved = new List<ILInstruction>();
            for (int i = k + 1; i < end; i++) moved.Add(block.Instructions[i]);
            block.Instructions.RemoveRange(k + 1, end - k - 1);
            rest.Instructions.AddRange(moved);
            guard.FalseInst = rest;
            // A slot used on both sides was one variable while the rest stood in the outer
            // block (two declarations of one name collide there and are merged); it stays one.
            var function = block.Ancestors.OfType<ILFunction>().FirstOrDefault();
            if (function != null)
            {
                var inThen = new Dictionary<int, ILVariable>();
                foreach (var use in then.Descendants.OfType<IInstructionWithVariableOperand>())
                    if (use.Variable.Kind == VariableKind.Local && use.Variable.Index.HasValue && use.Variable.Function == function) inThen[use.Variable.Index.Value] = use.Variable;
                var merged = new Dictionary<ILVariable, ILVariable>();
                foreach (var use in rest.Descendants.OfType<IInstructionWithVariableOperand>())
                {
                    ILVariable w = use.Variable, u;
                    if (w.Kind != VariableKind.Local || !w.Index.HasValue || w.Function != function || !inThen.TryGetValue(w.Index.Value, out u) || u == w || !u.Type.Equals(w.Type)) continue;
                    merged[w] = u;
                }
                if (merged.Count > 0)
                    foreach (var use in function.Descendants.OfType<IInstructionWithVariableOperand>().ToList())
                    {
                        ILVariable u;
                        if (merged.TryGetValue(use.Variable, out u)) use.Variable = u;
                    }
            }
            return;
        }
    }

    //     if (c) return;  rest            at the end of a void method
    // with no 'ret' in the IL between the test and the rest was
    //     if (!c) { rest }
    // ('return;' is always a 'ret' where it stands).
    private static void Nest(Block block, BlockContainer container, RawIL raw)
    {
        int n = block.Instructions.Count;
        if (n == 0) return;
        var last = block.Instructions[n - 1] as Leave;
        int end = IsTailLeave(last, container) ? n - 1 : n;
        for (int k = 0; k + 1 < end; k++)
        {
            var guard = block.Instructions[k] as IfInstruction;
            if (guard == null || !guard.FalseInst.MatchNop()) continue;
            var exit = guard.TrueInst as Leave;
            var body = guard.TrueInst as Block;
            if (exit == null && body != null && body.Instructions.Count == 1) exit = body.Instructions[0] as Leave;
            if (!IsTailLeave(exit, container)) continue;
            int conditionEnd = RawIL.EndOf(guard.Condition);
            RawIL.Op branch, next;
            if (conditionEnd < 0) continue;
            if (!(raw.IsAt(conditionEnd, out branch) && RawIL.IsCondBranch(branch.Code)) && !(raw.EndsAt(conditionEnd, out branch) && RawIL.IsCondBranch(branch.Code)))
            {
                // A lifted comparison ('a >= n + b' on a Nullable<T>) keeps the ranges of its
                // operands only: the branch is the instruction in front of what follows.
                int following = Start(block.Instructions[k + 1]);
                if (following <= conditionEnd || !raw.Near(following, -1, out branch) || !RawIL.IsCondBranch(branch.Code) || branch.Operand <= following) continue;
            }
            if (!raw.Near(branch.Offset, 1, out next) || next.Code == ILOpCode.Ret || next.Code == ILOpCode.Leave || next.Code == ILOpCode.Leave_s) continue;
            // (A loop that follows starts with a jump to its test, which no node carries.)
            bool loopNext = (next.Code == ILOpCode.Br || next.Code == ILOpCode.Br_s) && next.Operand > next.End && block.Instructions[k + 1] is BlockContainer && Start(block.Instructions[k + 1]) == next.End;
            if (Start(block.Instructions[k + 1]) != next.Offset && !loopNext) continue;

            ILInstruction condition = guard.Condition, positive;
            guard.Condition = new Nop();
            var rest = new Block();
            var moved = new List<ILInstruction>();
            for (int i = k + 1; i < end; i++) moved.Add(block.Instructions[i]);
            block.Instructions.RemoveRange(k, end - k);
            rest.Instructions.AddRange(moved);
            ILInstruction negated = condition.MatchLogicNot(out positive) ? Detach(positive) : Comp.LogicNot(condition);
            block.Instructions.Insert(k, new IfInstruction(negated, rest));
            return;
        }
    }

    //     if (c) { T } else { E }        at the end of a void method
    // where a conditional branch inside T jumps to the 'ret' that ends T was
    //     if (c) { T; return; }  E
    // (T ends with 'if (b) X;': written with 'else', that inner branch goes to the last 'ret').
    private static bool ElseToReturn(Block block, int at, IfInstruction ifInst, BlockContainer container, List<int> rets, RawIL raw)
    {
        var t = ifInst.TrueInst as Block;
        var e = ifInst.FalseInst as Block;
        if (t == null || e == null || t.Instructions.Count == 0 || e.Instructions.Count == 0) return false;
        bool negate;
        if (ReturnsFirst(t, e, raw)) negate = false;
        else if (ReturnsFirst(e, t, raw)) negate = true;
        else return false;
        Block first = negate ? e : t, second = negate ? t : e;
        ILInstruction condition = ifInst.Condition, positive;
        ifInst.TrueInst = new Nop();
        ifInst.FalseInst = new Nop();
        if (negate)
        {
            ifInst.Condition = new Nop();
            ifInst.Condition = condition.MatchLogicNot(out positive) ? Detach(positive) : Comp.LogicNot(condition);
        }
        first.Instructions.Add(new Leave(container));
        ifInst.TrueInst = first;
        var moved = second.Instructions.ToList();
        second.Instructions.Clear();
        for (int i = 0; i < moved.Count; i++) block.Instructions.Insert(at + 1 + i, moved[i]);
        Rewrite(first, container, rets, raw);
        return true;
    }

    private static bool ReturnsFirst(Block first, Block second, RawIL raw)
    {
        if (first.HasFlag(InstructionFlags.EndPointUnreachable)) return false;
        int r = RawIL.EndOf(first);
        RawIL.Op op;
        // The range of the last statement may include the 'ret' that follows it.
        if (r < 0) return false;
        // In a protected block: 'leave' to the end of the method where 'else' would have a 'br'.
        RawIL.Op target;
        if (((raw.IsAt(r, out op) && (op.Code == ILOpCode.Leave || op.Code == ILOpCode.Leave_s)) || (raw.EndsAt(r, out op) && (op.Code == ILOpCode.Leave || op.Code == ILOpCode.Leave_s)))
            && !raw.RegionEnds.Contains(op.End) && raw.IsAt(op.Operand, out target) && target.Code == ILOpCode.Ret)
            return RawIL.StartOf(second) == op.End;
        if (!(raw.IsAt(r, out op) && op.Code == ILOpCode.Ret) && !(raw.EndsAt(r, out op) && op.Code == ILOpCode.Ret)) return false;
        if (!raw.CondTargets.Contains(op.Offset)) return false;
        // (The 'return;' statements of the second block carry the place of other 'ret'.)
        return RawIL.StartOf(second) == op.End || RawIL.StartOfCode(second) == op.End;
    }

    // The operands of a conjunction, each with 'true' when it is to be negated:
    // '!(a || b)' is '!a && !b'.
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
        ILInstruction inst = Detach(part.Key), positive;
        if (!part.Value) return inst;
        return inst.MatchLogicNot(out positive) ? Detach(positive) : Comp.LogicNot(inst);
    }

    // An instruction has one parent: take it out of the old condition before reuse.
    // (Clone() is not an option: it does not support lambdas.)
    private static ILInstruction Detach(ILInstruction inst)
    {
        if (inst.Parent != null) inst.ReplaceWith(new Nop());
        return inst;
    }

    //     if (a && b && c) { body }
    // with a 'ret' in the IL before b and before the body was
    //     if (!a) return;  if (!(b && c)) return;  body
    // Returns false when the IL has no early return here.
    private static bool Split(Block block, int at, IfInstruction ifInst, BlockContainer container, List<int> rets)
    {
        ILInstruction then = ifInst.TrueInst;
        var thenBlock = then as Block;
        int bodyStart = Start(then);
        var parts = new List<KeyValuePair<ILInstruction, bool>>();
        Conjuncts(ifInst.Condition, false, parts);
        var cuts = new List<int>();   // a guard ends before parts[cut]
        for (int i = 1; i < parts.Count; i++) if (RetBefore(rets, Start(parts[i].Key))) cuts.Add(i);
        // Two estimates of where the body starts; either can be off after earlier transforms.
        bool whole = RetBefore(rets, bodyStart) || (thenBlock != null && RetBefore(rets, thenBlock.StartILOffset));
        if (whole) cuts.Add(parts.Count);
        if (cuts.Count == 0) return false;

        var replacement = new List<ILInstruction>();
        int from = 0;
        foreach (int cut in cuts)
        {
            replacement.Add(new IfInstruction(Comp.LogicNot(And(parts, from, cut)), new Leave(container)));
            from = cut;
        }
        bool trailingLeave = block.Instructions.Count > at + 1;
        if (whole)
        {
            if (thenBlock != null) { var body = thenBlock.Instructions.ToList(); thenBlock.Instructions.Clear(); replacement.AddRange(body); }
            else { ifInst.TrueInst = new Nop(); replacement.Add(then); }
        }
        else
        {
            ifInst.TrueInst = new Nop();
            replacement.Add(new IfInstruction(And(parts, from, parts.Count), then));
        }
        block.Instructions.RemoveRange(at, block.Instructions.Count - at);
        block.Instructions.AddRange(replacement);
        if (trailingLeave && !block.Instructions[block.Instructions.Count - 1].HasFlag(InstructionFlags.EndPointUnreachable))
            block.Instructions.Add(new Leave(container));
        return true;
    }
}
