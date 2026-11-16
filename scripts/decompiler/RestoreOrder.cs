// IL transform: statements in the order the IL has them.
//
//     if (c) { T }  R  S          where T does not reach its end (return, throw)
// compiles to 'c; brfalse LR; T; LR: R; S'. When the IL has R before T, the source was
//     if (!c) { R } else { T }  S
// ('c; brtrue LT; R; br LS; LT: T; LS: S'), or 'if (!c) { R }  T' when nothing follows T.
// The decompiler prefers the first form whenever T is short.
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreOrder : IILTransform
{
    public void Run(ILFunction function, ILTransformContext context)
    {
        var functions = function.Descendants.OfType<ILFunction>().ToList();
        functions.Add(function);
        foreach (ILFunction f in functions)
        {
            if (f.Method == null || !Program.Enabled("RestoreOrder", f.Method.MetadataToken)) continue;
            foreach (Block block in RawIL.Own(f).OfType<Block>().ToList())
                if (block.Kind == BlockKind.ControlFlow) Reorder(block);
            RawIL raw = RawIL.Read(f, context);
            if (raw != null)
                foreach (Block block in RawIL.Own(f).OfType<Block>().ToList())
                    if (block.Kind == BlockKind.ControlFlow) ReturnInElse(block, raw);
        }
    }

    private static bool IsVoidLeave(ILInstruction inst)
    {
        var block = inst as Block;
        if (block != null && block.Instructions.Count == 1) inst = block.Instructions[0];
        var leave = inst as Leave;
        return leave != null && leave.Value.MatchNop();
    }

    //     if (c) return;  R            in a block that something follows
    // with 'R; br E; ret; E:' in the IL was
    //     if (!c) { R } else { return; }
    // ('return;' is a 'ret' where it stands: here it stands after R).
    private static void ReturnInElse(Block block, RawIL raw)
    {
        for (int k = 0; k + 1 < block.Instructions.Count; k++)
        {
            var test = block.Instructions[k] as IfInstruction;
            if (test == null || !test.FalseInst.MatchNop() || !IsVoidLeave(test.TrueInst)) continue;
            var leave = test.TrueInst as Leave;
            if (leave == null) leave = (Leave)((Block)test.TrueInst).Instructions[0];
            if (!leave.IsLeavingFunction) continue;
            int n = block.Instructions.Count;
            ILInstruction last = block.Instructions[n - 1];
            if (last.HasFlag(InstructionFlags.EndPointUnreachable)) continue;
            int end = RawIL.EndOf(last), conditionStart = RawIL.StartOf(test.Condition), restStart = RawIL.StartOf(block.Instructions[k + 1]);
            RawIL.Op jump, ret;
            if (end < 0 || conditionStart < 0 || restStart < conditionStart || !raw.IsAt(end, out jump)) continue;
            // The range of a loop may include the jump that follows it.
            RawIL.Op inside;
            if (jump.Code != System.Reflection.Metadata.ILOpCode.Br && jump.Code != System.Reflection.Metadata.ILOpCode.Br_s && raw.EndsAt(end, out inside) && (inside.Code == System.Reflection.Metadata.ILOpCode.Br || inside.Code == System.Reflection.Metadata.ILOpCode.Br_s) && inside.Operand > inside.End) { jump = inside; end = inside.Offset; }
            if (jump.Code != System.Reflection.Metadata.ILOpCode.Br && jump.Code != System.Reflection.Metadata.ILOpCode.Br_s) continue;
            if (!raw.Near(end, 1, out ret)) continue;
            // In a protected block 'return;' is a 'leave' to the 'ret' that ends the method.
            RawIL.Op landing;
            bool leaves = (ret.Code == System.Reflection.Metadata.ILOpCode.Leave || ret.Code == System.Reflection.Metadata.ILOpCode.Leave_s) && !raw.RegionEnds.Contains(ret.End) && raw.IsAt(ret.Operand, out landing) && landing.Code == System.Reflection.Metadata.ILOpCode.Ret;
            if (ret.Code != System.Reflection.Metadata.ILOpCode.Ret && !leaves) continue;
            if (!raw.CondTargets.Contains(ret.Offset) || jump.Operand != ret.End) continue;

            ILInstruction condition = test.Condition, positive, exit = test.TrueInst;
            test.Condition = new Nop();
            test.TrueInst = new Nop();
            if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); condition = positive; }
            else condition = Comp.LogicNot(condition);
            var moved = new List<ILInstruction>();
            for (int i = k + 1; i < n; i++) moved.Add(block.Instructions[i]);
            block.Instructions.RemoveRange(k, n - k);
            var first = new Block();
            first.Instructions.AddRange(moved);
            var second = exit as Block;
            if (second == null) { second = new Block(); second.Instructions.Add(exit); }
            block.Instructions.Add(new IfInstruction(condition, first, second));
            return;
        }
    }

    private static void Reorder(Block block)
    {
        for (int k = 0; k + 1 < block.Instructions.Count; k++)
        {
            var test = block.Instructions[k] as IfInstruction;
            if (test == null || !test.FalseInst.MatchNop()) continue;
            ILInstruction exit = test.TrueInst;
            if (!exit.HasFlag(InstructionFlags.EndPointUnreachable) || IsVoidLeave(exit) || exit is Branch) continue;
            int exitStart = RawIL.StartOf(exit), conditionStart = RawIL.StartOf(test.Condition);
            if (exitStart < 0 || conditionStart < 0 || exitStart < conditionStart) continue;
            // Statements that follow in the tree and come first in the IL.
            int before = 0;
            bool known = true;
            for (int i = k + 1; i < block.Instructions.Count && known; i++)
            {
                int start = RawIL.StartOf(block.Instructions[i]);
                if (start < 0)
                {
                    // A closing 'leave' or branch has no place of its own: it stays where it is.
                    if (i == block.Instructions.Count - 1 && (block.Instructions[i] is Leave || block.Instructions[i] is Branch)) break;
                    known = false;
                }
                else if (start > conditionStart && start < exitStart) { if (before == i - k - 1) before++; else known = false; }
                else if (start < conditionStart) known = false;
            }
            if (!known || before == 0) continue;

            ILInstruction condition = test.Condition, positive;
            test.Condition = new Nop();
            test.TrueInst = new Nop();
            if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); condition = positive; }
            else condition = Comp.LogicNot(condition);
            var moved = new List<ILInstruction>();
            for (int i = 0; i < before; i++) moved.Add(block.Instructions[k + 1 + i]);
            block.Instructions.RemoveRange(k, before + 1);
            var first = new Block();
            first.Instructions.AddRange(moved);
            bool nothingAfter = k == block.Instructions.Count;
            // (The flags of a block are those of all its statements: only the last one tells whether its end is reached.)
            if (nothingAfter && first.Instructions.Count > 0 && first.Instructions[first.Instructions.Count - 1].HasFlag(InstructionFlags.EndPointUnreachable))
            {
                block.Instructions.Insert(k, new IfInstruction(condition, first));
                var exitBlock = exit as Block;
                if (exitBlock != null)
                {
                    var tail = exitBlock.Instructions.ToList();
                    exitBlock.Instructions.Clear();
                    block.Instructions.AddRange(tail);
                }
                else block.Instructions.Add(exit);
            }
            else
            {
                var second = exit as Block;
                if (second == null) { second = new Block(); second.Instructions.Add(exit); }
                block.Instructions.Insert(k, new IfInstruction(condition, first, second));
            }
            Reorder(first);
        }
    }
}
