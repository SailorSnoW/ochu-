// IL transform: locals of the source that the decompiler folds into the expression that
// uses them. Each is decided by the raw method body.
//
// The compiler keeps a value on the stack when nothing is pushed under it afterwards. A value
// stored in a slot and read back later, with other things pushed in between, was a local of
// the source:
//
// 1. Read once.
//        T v = e;  x.f = v.g;                  IL: e; stloc v; ldarg.0; ldloc v; ldfld g; stfld f
//    is printed 'x.f = e.g;', which pushes x first.
//
// 2. Assigned, then read again.
//        T v = e;  x.f = v;  return v;         IL: e; stloc v; ldarg.0; ldloc v; stfld f; ldloc v; ret
//    is printed 'return x.f = e;', which keeps a copy ('dup') or stores it in a temporary
//    after the target is pushed.
//
// 3. Assignment as the value of a property assignment.
//        a[k] = (v = e);                       IL: a; k; e; dup; stloc v; call set_Item
//    is printed 'v = (a[k] = e);'. That compiles to the same instructions when the compiler
//    can use v as its temporary, but not when the address of v is taken ('out v' earlier):
//    the value then goes through a temporary of its own.
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreLocals : IILTransform
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
            if (Program.Enabled("RestoreLocal", f.Method.MetadataToken)) { Uninline(f, raw); UninlineAddressed(f, raw); NestedAssignments(f, raw); PositionalArguments(f); DeadStores(f, raw); ReceiverCopies(f, raw); BottomTestedLoops(f); }
        }
    }

    private static bool IsStatement(ILInstruction inst)
    {
        var block = inst.Parent as Block;
        return block != null && block.Kind == BlockKind.ControlFlow && inst.SlotInfo == Block.InstructionSlot;
    }

    private static bool IsPlain(ILInstruction d)
    {
        return d is LdLoc || d is LdcI4 || d is LdcI8 || d is LdcF4 || d is LdcF8 || d is LdNull || d is LdStr || d is LdFlda || d is LdsFlda || d is LdLoca || d is LdObj || d is Nop || d is Conv;
    }

    // True when 'node' is the first thing its statement does, apart from loads.
    private static bool EvaluatedFirst(ILInstruction statement, ILInstruction node)
    {
        foreach (ILInstruction d in statement.Descendants)
        {
            if (d == node) return true;
            if (d.IsDescendantOf(node) || node.IsDescendantOf(d)) continue;
            if (!IsPlain(d)) return false;
        }
        return false;
    }

    // The statement that holds 'inst', through expressions only; null when a construct with
    // an evaluation order of its own is in between.
    private static ILInstruction StatementOf(ILInstruction inst)
    {
        ILInstruction child = inst;
        while (!IsStatement(child))
        {
            ILInstruction p = child.Parent;
            if (p == null) return null;
            var test = p as IfInstruction;
            if (test != null) { if (test.Condition != child || !IsStatement(test)) return null; }
            else if (p is Block && ((Block)p).Kind == BlockKind.CallWithNamedArgs) { }
            else if (!(p is CallInstruction || p is StObj || p is StLoc || p is LdObj || p is LdFlda || p is BinaryNumericInstruction || p is Comp || p is Conv || p is Leave || p is LdElema || p is LdLen || p is UnboxAny || p is Box || p is CastClass || p is IsInst || p is Throw || p is BitNot || p is AddressOf)) return null;
            child = p;
        }
        return child;
    }

    // 4. Read once by address.
    //        int n = e;  s = "a" + n;              IL: e; stloc n; ldstr "a"; ldloca n; call ToString
    //    is printed 's = "a" + e;', which pushes "a" first. (Stored and read by address at
    //    once, the slot is the copy the compiler makes to call a method on a value.)
    private static void UninlineAddressed(ILFunction f, RawIL raw)
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
        List<ILInstruction> all = null;
        foreach (var kv in stores)
        {
            int slot = kv.Key;
            List<int> read;
            if (kv.Value.Count != 1 || loaded.Contains(slot) || !addresses.TryGetValue(slot, out read) || read.Count != 1) continue;
            int s = kv.Value[0];
            RawIL.Op store = raw.Ops[s];
            if (s == 0 || read[0] <= s + 1 || raw.Ops[s - 1].Code == ILOpCode.Dup || raw.Targets.Contains(store.End)) continue;
            // The address is the receiver of a call, right away.
            // (Its plain arguments may be pushed first: 'ldloca n; ldloc a; call M'.)
            int called = read[0] + 1;
            while (called < raw.Ops.Count && (RawIL.IsLdLoc(raw.Ops[called].Code) || RawIL.IsLdArg(raw.Ops[called].Code) || RawIL.IsLdcI4(raw.Ops[called].Code)) && !raw.Targets.Contains(raw.Ops[called].Offset)) called++;
            if (called >= raw.Ops.Count || raw.Ops[called].Code != ILOpCode.Call) continue;
            bool straight = true;
            for (int i = s + 1; i < read[0] && straight; i++)
            {
                ILOpCode c = raw.Ops[i].Code;
                if (RawIL.IsCondBranch(c) || c == ILOpCode.Br || c == ILOpCode.Br_s || c == ILOpCode.Leave || c == ILOpCode.Leave_s || c == ILOpCode.Ret || c == ILOpCode.Switch || c == ILOpCode.Throw || c == ILOpCode.Endfinally) straight = false;
                if (raw.Targets.Contains(raw.Ops[i].Offset)) straight = false;
            }
            if (!straight) continue;
            IType type = raw.LocalType(slot);
            if (type == null || type.Kind == TypeKind.Unknown || type.Kind == TypeKind.Pointer || type.Kind == TypeKind.ByReference || type.IsReferenceType != false) continue;

            if (all == null) all = RawIL.Own(f).ToList();
            ILInstruction value = null;
            foreach (ILInstruction inst in all)
            {
                if (inst.Parent == null || (inst is Block && ((Block)inst).Kind == BlockKind.ControlFlow) || inst is BlockContainer || inst is ILFunction || inst is AddressOf) continue;
                // The range of a value the decompiler inlined includes the 'stloc' that stored it.
                int end = RawIL.EndOf(inst);
                if (end != store.Offset && end != store.End) continue;
                if (value == null || value.IsDescendantOf(inst)) value = inst;
            }
            if (value == null || value.ResultType == StackType.Void || IsStatement(value)) continue;
            var address = value.Parent as AddressOf;
            if (address == null || !(address.Parent is Call) || address.ChildIndex != 0) continue;
            ILInstruction statement = StatementOf(address);
            if (statement == null || !EvaluatedFirst(statement, value)) continue;
            if (RawIL.StartOf(statement) != RawIL.StartOf(value)) continue;
            var block = (Block)statement.Parent;
            ILVariable v = f.RegisterVariable(VariableKind.Local, type);
            address.Value = new Nop();
            address.ReplaceWith(new LdLoca(v));
            block.Instructions.Insert(statement.ChildIndex, new StLoc(v, value));
            all = null;
        }
    }

    private static void Uninline(ILFunction f, RawIL raw)
    {
        var stores = new Dictionary<int, List<int>>();   // slot -> indices in raw.Ops
        var loads = new Dictionary<int, List<int>>();
        var addressed = new HashSet<int>();
        for (int i = 0; i < raw.Ops.Count; i++)
        {
            RawIL.Op op = raw.Ops[i];
            Dictionary<int, List<int>> into = RawIL.IsStLoc(op.Code) ? stores : RawIL.IsLdLoc(op.Code) ? loads : null;
            if (op.Code == ILOpCode.Ldloca || op.Code == ILOpCode.Ldloca_s) addressed.Add(op.Operand);
            if (into == null) continue;
            List<int> list;
            if (!into.TryGetValue(op.Operand, out list)) { list = new List<int>(); into[op.Operand] = list; }
            list.Add(i);
        }
        List<ILInstruction> all = null;
        foreach (var kv in stores)
        {
            int slot = kv.Key;
            List<int> read;
            if (kv.Value.Count != 1 || addressed.Contains(slot) || !loads.TryGetValue(slot, out read)) continue;
            if (read.Count != 1 && read.Count != 2) continue;
            int s = kv.Value[0];
            RawIL.Op store = raw.Ops[s];
            if (s == 0 || read[0] < s || raw.Ops[s - 1].Code == ILOpCode.Dup || raw.Targets.Contains(store.End)) continue;
            // Straight code from the store to the last read.
            bool straight = true;
            for (int i = s + 1; i < read[read.Count - 1] && straight; i++)
            {
                ILOpCode c = raw.Ops[i].Code;
                if (RawIL.IsCondBranch(c) || c == ILOpCode.Br || c == ILOpCode.Br_s || c == ILOpCode.Leave || c == ILOpCode.Leave_s || c == ILOpCode.Ret || c == ILOpCode.Switch || c == ILOpCode.Throw || c == ILOpCode.Endfinally) straight = false;
                if (raw.Targets.Contains(raw.Ops[i].Offset)) straight = false;
            }
            if (!straight) continue;
            IType type = raw.LocalType(slot);
            if (type == null || type.Kind == TypeKind.Unknown || type.Kind == TypeKind.Pointer || type.Kind == TypeKind.ByReference) continue;

            // The expression that the store ends: the outermost one that stops there.
            if (all == null) all = RawIL.Own(f).ToList();
            ILInstruction value = null;
            foreach (ILInstruction inst in all)
            {
                if (inst.Parent == null || (inst is Block && ((Block)inst).Kind == BlockKind.ControlFlow) || inst is BlockContainer || inst is ILFunction || inst is AddressOf) continue;
                if (RawIL.EndOf(inst) != store.Offset) continue;
                if (value == null || value.IsDescendantOf(inst)) value = inst;
            }
            // A struct read through its address ('ldloc v; ldfld g' on a value): the range of
            // the value includes the store.
            if (value == null)
                foreach (ILInstruction inst in all)
                    if (inst.Parent is AddressOf && inst.Parent.Parent is LdFlda && RawIL.EndOf(inst) == store.End && type.Kind == TypeKind.Struct) value = inst;
            // The range of an inlined value may include the 'stloc' that stored it.
            if (value == null)
                foreach (ILInstruction inst in all)
                {
                    if (inst.Parent == null || (inst is Block && ((Block)inst).Kind == BlockKind.ControlFlow) || inst is BlockContainer || inst is ILFunction || inst is AddressOf || !(inst is CallInstruction) || inst.ResultType == StackType.Ref || inst.ResultType == StackType.Void) continue;
                    if (RawIL.EndOf(inst) != store.End) continue;
                    if (value == null || value.IsDescendantOf(inst)) value = inst;
                }
            if (value == null || value.ResultType == StackType.Void || IsStatement(value)) continue;
            // A parent that stops at the same place only wraps it (initializer block, conversion).
            while (value.Parent != null && !IsStatement(value.Parent) && RawIL.EndOf(value.Parent) == store.Offset && !(value.Parent is StLoc) && !(value.Parent is AddressOf)) value = value.Parent;
            if (value.Parent is StLoc && IsStatement(value.Parent)) continue;   // still a local

            if (read.Count == 1)
            {
                // Read back at once: a temporary of the compiler, or a local of the source in a
                // unit compiled with /debug+ (every declared local keeps its slot there).
                if (read[0] == s + 1 && !Program.KeepsLocals(f.Method)) continue;
                ILInstruction statement = StatementOf(value);
                if (statement == null || statement == value || !EvaluatedFirst(statement, value)) continue;
                if (RawIL.StartOf(statement) != RawIL.StartOf(value)) continue;
                var block = (Block)statement.Parent;
                ILVariable v = f.RegisterVariable(VariableKind.Local, type);
                var holder = new Nop();
                var address = value.Parent as AddressOf;
                value.ReplaceWith(holder);
                if (address != null) address.ReplaceWith(new LdLoca(v));
                else holder.ReplaceWith(new LdLoc(v));
                block.Instructions.Insert(statement.ChildIndex, new StLoc(v, value));
                all = null;
            }
            else
            {
                // 'x.f = e' used as a value.
                var assign = value.Parent as StObj;
                if (assign == null || assign.Value != value || IsStatement(assign)) continue;
                ILInstruction statement = StatementOf(assign);
                if (statement == null || !EvaluatedFirst(statement, assign)) continue;
                // (A target that is not plain is fine when the IL has it after the store: 'T v = e; a[2 * i] = v;'.)
                bool plainTarget = assign.Target.Descendants.All(IsPlain) && IsPlain(assign.Target);
                if (!plainTarget && !(RawIL.StartOf(assign.Target) > store.Offset)) continue;
                if (RawIL.StartOf(statement) != RawIL.StartOf(value)) continue;
                // The first read is the assignment.
                RawIL.Op after;
                if (!raw.Near(raw.Ops[read[0]].Offset, 1, out after)) continue;
                bool element = after.Code == ILOpCode.Stelem || (after.Code >= ILOpCode.Stelem_i && after.Code <= ILOpCode.Stelem_ref);
                if (after.Code != ILOpCode.Stfld && after.Code != ILOpCode.Stsfld && !element) continue;
                var kept = statement as StLoc;
                if (kept != null && kept.Value == assign && kept.Variable.Kind == VariableKind.Local)
                {
                    // 'T w = (x.f = e);': w is the variable itself.
                    var owner = (Block)statement.Parent;
                    kept.Value = new Nop();
                    assign.Value = new Nop();
                    kept.Value = value;
                    assign.Value = new LdLoc(kept.Variable);
                    owner.Instructions.Insert(statement.ChildIndex + 1, assign);
                    all = null;
                    continue;
                }
                if (!plainTarget && !element) continue;
                var block = (Block)statement.Parent;
                ILVariable v = f.RegisterVariable(VariableKind.Local, type);
                var holder = new Nop();
                assign.ReplaceWith(holder);
                holder.ReplaceWith(new LdLoc(v));
                assign.Value = new LdLoc(v);
                int at = statement.ChildIndex;
                block.Instructions.Insert(at, assign);
                block.Instructions.Insert(at, new StLoc(v, value));
                all = null;
            }
        }
    }

    // 4. Arguments in the order of the parameters.
    //        F(b: e3, r: e1, g: e2)
    //    is how the decompiler keeps an evaluation order that differs from the order of the
    //    parameters. Once the variable that caused it is back (rule 1), the arguments are in
    //    the order of the IL: 'F(e1, e2, v)'.
    internal static void PositionalArguments(ILFunction f)
    {
        foreach (Block named in RawIL.Own(f).OfType<Block>().ToList())
        {
            if (named.Kind != BlockKind.CallWithNamedArgs || named.Parent == null) continue;
            var call = named.FinalInstruction as CallInstruction;
            if (call == null || !named.Instructions.All(i => i is StLoc)) continue;
            var values = new Dictionary<ILVariable, StLoc>();
            foreach (StLoc s in named.Instructions) values[s.Variable] = s;
            // Start of each argument, in the order of the parameters.
            int previous = -1;
            bool ordered = true;
            foreach (ILInstruction argument in call.Arguments)
            {
                ILInstruction value = argument;
                var load = argument as LdLoc;
                StLoc stored;
                if (load != null && values.TryGetValue(load.Variable, out stored)) value = stored.Value;
                int start = RawIL.StartOf(value);
                if (start < 0) continue;
                if (start < previous) ordered = false;
                previous = start;
            }
            if (!ordered) continue;
            foreach (ILInstruction argument in call.Arguments.ToList())
            {
                var load = argument as LdLoc;
                StLoc stored;
                if (load == null || !values.TryGetValue(load.Variable, out stored)) continue;
                ILInstruction value = stored.Value;
                stored.Value = new Nop();
                load.ReplaceWith(value);
            }
            named.Instructions.Clear();
            named.FinalInstruction = new Nop();
            named.ReplaceWith(call);
        }
    }

    // 6. Copy the compiler makes of a value type variable to call a method on it.
    //        "a" + n          (n an 'int?')        IL: ldstr "a"; ldloc n; stloc t; ldloca t; constrained. T; callvirt ToString
    //    is printed 'T t = n; ... "a" + t' when the decompiler keeps the copy (two of them in
    //    one slot). Every store of the slot has that shape: the copy is read where it is made.
    private static void ReceiverCopies(ILFunction f, RawIL raw)
    {
        var copies = new HashSet<int>();
        var refused = new HashSet<int>();
        for (int i = 0; i < raw.Ops.Count; i++)
        {
            RawIL.Op op = raw.Ops[i];
            if (!RawIL.IsStLoc(op.Code)) continue;
            bool shaped = i > 0 && i + 3 < raw.Ops.Count && RawIL.IsLdLoc(raw.Ops[i - 1].Code) && (raw.Ops[i + 1].Code == ILOpCode.Ldloca || raw.Ops[i + 1].Code == ILOpCode.Ldloca_s) && raw.Ops[i + 1].Operand == op.Operand
                && raw.Ops[i + 2].Code == ILOpCode.Constrained && raw.Ops[i + 3].Code == ILOpCode.Callvirt && !raw.Targets.Contains(raw.Ops[i + 1].Offset);
            if (shaped) copies.Add(op.Operand); else refused.Add(op.Operand);
        }
        foreach (RawIL.Op op in raw.Ops)
            if (RawIL.IsLdLoc(op.Code)) refused.Add(op.Operand);
        copies.ExceptWith(refused);
        if (copies.Count == 0) return;
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            ILVariable t = store.Variable;
            var source = store.Value as LdLoc;
            if (source == null || !IsStatement(store) || t.Kind != VariableKind.Local || !t.Index.HasValue || !copies.Contains(t.Index.Value)) continue;
            // (A copy of another type is a conversion: '((E)n).ToString()'.)
            if (!source.Variable.Type.Equals(t.Type)) continue;
            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            if (at + 1 >= block.Instructions.Count) continue;
            // Read once, by what the next statement evaluates (the test of an 'if', not its branches).
            ILInstruction next = block.Instructions[at + 1];
            ILInstruction scope = next is IfInstruction ? ((IfInstruction)next).Condition : next;
            var uses = scope.Descendants.Where(d => (d is LdLoc && ((LdLoc)d).Variable == t) || (d is LdLoca && ((LdLoca)d).Variable == t)).ToList();
            if (uses.Count != 1) continue;
            if (uses[0] is LdLoc) uses[0].ReplaceWith(new LdLoc(source.Variable)); else uses[0].ReplaceWith(new LdLoca(source.Variable));
            block.Instructions.RemoveAt(at);
        }
    }

    // 7. Loop tested at its bottom.
    //        while (c) { body }                    IL: br C; B: body; C: c; brtrue B
    //    is left as 'while (true) { if (!c) break; body }' when the test was not alone in its
    //    block before rule 6. The test stands after the body in the IL.
    private static void BottomTestedLoops(ILFunction f)
    {
        foreach (BlockContainer loop in RawIL.Own(f).OfType<BlockContainer>().ToList())
        {
            if (loop.Kind != ContainerKind.Loop || loop.Blocks.Count != 1) continue;
            Block header = loop.EntryPoint;
            if (header.Instructions.Count < 3) continue;
            var test = header.Instructions[0] as IfInstruction;
            var back = header.Instructions[header.Instructions.Count - 1] as Branch;
            if (test == null || back == null || back.TargetBlock != header || !test.FalseInst.MatchNop()) continue;
            var exit = test.TrueInst as Leave;
            var then = test.TrueInst as Block;
            if (exit == null && then != null && then.Instructions.Count == 1) exit = then.Instructions[0] as Leave;
            if (exit == null || exit.TargetContainer != loop || !exit.Value.MatchNop()) continue;
            if (loop.Descendants.OfType<Branch>().Count(b => b.TargetBlock == header) != 1) continue;
            int c = RawIL.StartOf(test.Condition), b0 = RawIL.StartOf(header.Instructions[1]);
            if (c < 0 || b0 < 0 || c < b0) continue;
            ILInstruction condition = test.Condition, positive;
            test.Condition = new Nop();
            if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); condition = positive; }
            else condition = Comp.LogicNot(condition);
            var body = new Block();
            var moved = new List<ILInstruction>();
            for (int i = 1; i < header.Instructions.Count; i++) moved.Add(header.Instructions[i]);
            header.Instructions.Clear();
            body.Instructions.AddRange(moved);
            header.Instructions.Add(new IfInstruction(condition, new Branch(body), new Leave(loop)));
            loop.Blocks.Add(body);
            loop.Kind = ContainerKind.While;
        }
    }

    // 5. Value stored and never read, in the slot of a variable.
    //        bool b;  ...  b = M();  ...  b = N();  if (b) { }       IL: call M; stloc b; ...; call N; stloc b; ldloc b
    //    The decompiler gives the first store a variable of its own ('bool b2 = M();'), which
    //    the compiler drops ('call M; pop'). A slot is never shared by two variables of the
    //    source: the store goes to the variable that has the slot.
    private static void DeadStores(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            ILVariable dead = store.Variable;
            if (!IsStatement(store) || dead.Kind != VariableKind.Local || !dead.Index.HasValue || dead.LoadCount != 0 || dead.AddressCount != 0 || dead.StoreCount != 1) continue;
            if (!(store.Value is CallInstruction)) continue;
            int end = RawIL.EndOf(store.Value);
            RawIL.Op kept;
            if (end < 0 || !raw.IsAt(end, out kept) || !RawIL.IsStLoc(kept.Code) || kept.Operand != dead.Index.Value) continue;
            var others = f.Variables.Where(x => x != dead && x.Kind == VariableKind.Local && x.Index == dead.Index && x.Function == f && x.LoadCount + x.StoreCount + x.AddressCount > 0).ToList();
            if (others.Count != 1 || others[0].LoadCount == 0 || !others[0].Type.Equals(dead.Type)) continue;
            store.Variable = others[0];
        }
    }

    // 3b. The same with a field or an array element.
    //        x.f = (v = e);                        IL: x; e; dup; stloc v; stfld f
    //     is printed 'v = (x.f = e);', which compiles to 'dup; stloc t; stfld f; ldloc t' and
    //     then either 'stloc v' or the value kept on the stack for the next read of v. When
    //     nothing reads the slot right after the store, the slot is v itself.
    private static void NestedStores(ILFunction f, RawIL raw)
    {
        foreach (StLoc outer in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            var assign = outer.Value as StObj;
            if (assign == null || !IsStatement(outer) || outer.Variable.Kind != VariableKind.Local || !outer.Variable.Index.HasValue) continue;
            int end = RawIL.EndOf(assign.Value);
            RawIL.Op dup, stored, store, next;
            if (end >= 0 && raw.IsAt(end, out dup) && RawIL.IsStLoc(dup.Code) && dup.Operand == outer.Variable.Index.Value && RawIL.StartOf(assign.Target) > dup.Offset)
            {
                // 3c. 'v = e; a[i] = v;' printed 'v = (a[i] = e);': 'e; stloc v' before the target is pushed.
                RawIL.Op before;
                if (!raw.Near(end, -1, out before) || before.Code == ILOpCode.Dup) continue;
                var owner = (Block)outer.Parent;
                ILInstruction first = assign.Value;
                assign.Value = new Nop();
                outer.Value = new Nop();
                outer.Value = first;
                assign.Value = new LdLoc(outer.Variable);
                owner.Instructions.Insert(outer.ChildIndex + 1, assign);
                continue;
            }
            if (end < 0 || !raw.IsAt(end, out dup) || dup.Code != ILOpCode.Dup) continue;
            if (!raw.Near(end, 1, out stored) || !RawIL.IsStLoc(stored.Code) || stored.Operand != outer.Variable.Index.Value) continue;
            if (!raw.Near(end, 2, out store)) continue;
            bool isStore = store.Code == ILOpCode.Stfld || store.Code == ILOpCode.Stsfld || store.Code == ILOpCode.Stobj || (store.Code >= ILOpCode.Stelem_i && store.Code <= ILOpCode.Stelem_ref) || store.Code == ILOpCode.Stelem;
            if (!isStore) continue;
            // (A read that a jump reaches is not the read of a temporary.)
            if (raw.Near(end, 3, out next) && RawIL.IsLdLoc(next.Code) && next.Operand == stored.Operand && !raw.Targets.Contains(next.Offset)
                && raw.Ops.Count(o => RawIL.IsStLoc(o.Code) && o.Operand == stored.Operand) == 1) continue;
            var block = (Block)outer.Parent;
            int at = outer.ChildIndex;
            ILInstruction value = assign.Value;
            assign.Value = new Nop();
            outer.Value = new Nop();
            assign.Value = new StLoc(outer.Variable, value);
            block.Instructions[at] = assign;
        }
    }

    private static void NestedAssignments(ILFunction f, RawIL raw)
    {
        NestedStores(f, raw);
        foreach (Block inline in RawIL.Own(f).OfType<Block>().ToList())
        {
            // { call set(target..., stloc temp(value)); final: ldloc temp }
            if (inline.Kind != BlockKind.CallInlineAssign || inline.Instructions.Count != 1) continue;
            var setter = inline.Instructions[0] as CallInstruction;
            var final = inline.FinalInstruction as LdLoc;
            if (setter == null || final == null || setter.Arguments.Count == 0) continue;
            var first = setter.Arguments[setter.Arguments.Count - 1] as StLoc;
            if (first == null || final.Variable != first.Variable) continue;
            ILInstruction last = first;
            ILVariable temp = first.Variable;
            int end = RawIL.EndOf(first.Value);
            RawIL.Op op, stored, call;
            if (end < 0 || !raw.IsAt(end, out op)) continue;
            var outer = inline.Parent as StLoc;
            if (outer != null && IsStatement(outer) && outer.Variable.Kind == VariableKind.Local && outer.Variable.AddressCount > 0 && op.Code == ILOpCode.Dup)
            {
                // 3. 'v = (a[k] = e)' with 'dup; stloc v; call set': 'a[k] = (v = e)'.
                if (!raw.Near(end, 1, out stored) || !RawIL.IsStLoc(stored.Code) || !raw.Near(end, 2, out call) || !RawIL.IsCall(call.Code)) continue;
                if (!outer.Variable.Index.HasValue || outer.Variable.Index.Value != stored.Operand) continue;
                // Nothing reads the slot right after the call: it is the variable itself.
                RawIL.Op next;
                if (raw.Near(end, 3, out next) && RawIL.IsLdLoc(next.Code) && next.Operand == stored.Operand)
                {
                    RawIL.Op again;
                    if (raw.Near(end, 4, out again) && RawIL.IsStLoc(again.Code)) continue;
                }
                var block = (Block)outer.Parent;
                int at = outer.ChildIndex;
                ILInstruction value = first.Value;
                first.Value = new Nop();
                inline.Instructions.Clear();
                last.ReplaceWith(new StLoc(outer.Variable, value));
                block.Instructions[at] = setter;
                continue;
            }
            if (outer != null && IsStatement(outer) && outer.Variable.Kind == VariableKind.Local && outer.Variable.Index.HasValue && RawIL.IsStLoc(op.Code) && op.Operand == outer.Variable.Index.Value)
            {
                // 2b. 'v = e; a[k] = v;' printed 'v = (a[k] = e);': 'e; stloc v; a; k; ldloc v; call set'.
                RawIL.Op before;
                if (!raw.Near(end, -1, out before) || before.Code == ILOpCode.Dup) continue;
                bool plainTarget = true;
                for (int i = 0; i + 1 < setter.Arguments.Count; i++)
                    if (!IsPlain(setter.Arguments[i]) || !setter.Arguments[i].Descendants.All(IsPlain)) plainTarget = false;
                if (!plainTarget) continue;
                var block = (Block)outer.Parent;
                int at = outer.ChildIndex;
                ILInstruction value = first.Value;
                first.Value = new Nop();
                inline.Instructions.Clear();
                last.ReplaceWith(new LdLoc(outer.Variable));
                outer.Value = value;
                block.Instructions.Insert(at + 1, setter);
                continue;
            }
            if (RawIL.IsStLoc(op.Code) && !IsStatement(inline) && !(inline.Parent is StLoc))
            {
                // 2. with a property: 'T v = e; a[k] = v; return v;' printed 'return a[k] = e;'.
                RawIL.Op before;
                if (!raw.Near(end, -1, out before) || before.Code == ILOpCode.Dup) continue;
                ILInstruction statement = StatementOf(inline);
                if (statement == null || !EvaluatedFirst(statement, inline)) continue;
                if (RawIL.StartOf(statement) != RawIL.StartOf(first.Value)) continue;
                bool plainTarget = true;
                for (int i = 0; i + 1 < setter.Arguments.Count; i++)
                    if (!IsPlain(setter.Arguments[i]) || !setter.Arguments[i].Descendants.All(IsPlain)) plainTarget = false;
                if (!plainTarget) continue;
                IType type = raw.LocalType(op.Operand);
                if (type == null || type.Kind == TypeKind.Unknown) continue;
                var block = (Block)statement.Parent;
                ILVariable v = f.RegisterVariable(VariableKind.Local, type);
                ILInstruction value = first.Value;
                first.Value = new Nop();
                inline.Instructions.Clear();
                last.ReplaceWith(new LdLoc(v));
                var holder = new Nop();
                inline.ReplaceWith(holder);
                holder.ReplaceWith(new LdLoc(v));
                int at = statement.ChildIndex;
                block.Instructions.Insert(at, setter);
                block.Instructions.Insert(at, new StLoc(v, value));
            }
        }
    }
}
