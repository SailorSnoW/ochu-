// IL transform: layout of statements and local traffic, decided by the raw method body.
// Runs before the other transforms of this project (what it builds is read by their rules).
//
// 1. Condition assigned in the branches of an 'if'.
//        bool v;  if (c) { if (d) v = p; else v = q; } else v = r;  if (v) T
//    IL: c; brfalse R; d; brfalse Q; p; stloc v; br J; Q: q; stloc v; br J; R: r; stloc v; J: ldloc v; brfalse
//    is printed 'if (c ? (d ? p : q) : r) T', which jumps from each value. v is a slot
//    written once per branch and read once, by the test.
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreLayout : IILTransform
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
            BranchAssignedConditions(f, raw, context);
            StoreThenSet(f, raw);
            ReturnAfterProtected(f, raw);
            LocalsUnderNamedArguments(f, raw);
            AssignedInsideValue(f, raw);
            InitializerArguments(f, raw);
            foreach (Block block in RawIL.Own(f).OfType<Block>().ToList())
                if (block.Kind == BlockKind.ControlFlow && block.Parent != null) SectionEndInLoop(f, block, raw);
            SharedSlots(f, raw);
            CoalescedStackValues(f, raw);
            StructLocals(f, raw);
            ElseInTry(f, raw);
            foreach (Block block in RawIL.Own(f).OfType<Block>().ToList())
                if (block.Kind == BlockKind.ControlFlow && block.Parent != null) TryUnderTest(f, block, raw);
        }
    }

    private static bool IsStatement(ILInstruction inst)
    {
        var block = inst.Parent as Block;
        return block != null && block.Kind == BlockKind.ControlFlow && inst.SlotInfo == Block.InstructionSlot;
    }

    private static bool IsBranchOnBool(ILOpCode c)
    {
        return c == ILOpCode.Brfalse || c == ILOpCode.Brfalse_s || c == ILOpCode.Brtrue || c == ILOpCode.Brtrue_s;
    }

    private static bool StoreAfter(ILInstruction value, RawIL raw, out RawIL.Op store)
    {
        store = default(RawIL.Op);
        int end = RawIL.EndOf(value);
        if (end < 0) return false;
        if (raw.IsAt(end, out store) && RawIL.IsStLoc(store.Code)) return true;
        return raw.EndsAt(end, out store) && RawIL.IsStLoc(store.Code);
    }

    // 2. Value stored, then passed to a setter.
    //        T v = e;  a[k] = v;               IL: e; stloc v; a; k; ldloc v; call set_Item
    //    is printed 'T v = (a[k] = e);', which compiles to 'a; k; e; dup; stloc t; call set_Item;
    //    ldloc t; stloc v'.
    private static void StoreThenSet(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            var inline = store.Value as Block;
            if (inline == null || inline.Kind != BlockKind.CallInlineAssign || inline.Instructions.Count != 1 || !IsStatement(store)) continue;
            if (store.Variable.Kind != VariableKind.Local || !store.Variable.Index.HasValue) continue;
            var setter = inline.Instructions[0] as CallInstruction;
            if (setter == null || setter.Arguments.Count == 0) continue;
            var inner = setter.Arguments[setter.Arguments.Count - 1] as StLoc;
            if (inner == null || !inline.FinalInstruction.MatchLdLoc(inner.Variable) || inner.Variable.LoadCount != 1 || inner.Variable.StoreCount != 1) continue;
            int end = RawIL.EndOf(inner.Value), i0;
            RawIL.Op kept, before;
            if (end < 0 || !raw.IsAt(end, out kept) || !RawIL.IsStLoc(kept.Code) || kept.Operand != store.Variable.Index.Value) continue;
            if (!raw.Near(kept.Offset, -1, out before) || before.Code == ILOpCode.Dup) continue;
            // Then the other arguments, the variable and the call, with no jump in between.
            if (!raw.IndexAt.TryGetValue(kept.Offset, out i0)) continue;
            bool shaped = false;
            for (int i = i0 + 1; i < raw.Ops.Count && i < i0 + 40; i++)
            {
                RawIL.Op op = raw.Ops[i];
                if (raw.Targets.Contains(op.Offset) || RawIL.IsCondBranch(op.Code) || RawIL.IsStLoc(op.Code)) break;
                if (!RawIL.IsCall(op.Code)) continue;
                IMethod called = raw.Method(op);
                if (called != null && called.Equals(setter.Method)) { shaped = RawIL.IsLdLoc(raw.Ops[i - 1].Code) && raw.Ops[i - 1].Operand == kept.Operand; break; }
            }
            if (!shaped) continue;
            // The other arguments are evaluated after the value now: nothing in them may depend on the order.
            bool plain = true;
            for (int a = 0; a + 1 < setter.Arguments.Count; a++)
                foreach (ILInstruction d in setter.Arguments[a].Descendants.Concat(new[] { setter.Arguments[a] }))
                    if (d is CallInstruction || d is StLoc || d is StObj) plain = false;
            if (!plain) continue;
            var block = (Block)store.Parent;
            ILInstruction value = inner.Value;
            inner.Value = new Nop();
            inline.Instructions.Clear();
            setter.Arguments[setter.Arguments.Count - 1] = new LdLoc(store.Variable);
            store.Value = value;
            block.Instructions.Insert(store.ChildIndex + 1, setter);
        }
    }

    // 3. 'return;' after a protected block, inside another one.
    //        try { using (...) { A }  return; } catch { }      IL: A; leave N; <handler>; N: leave END
    //    is printed 'using (...) { A; return; }', whose 'leave' goes to END itself.
    private static void ReturnAfterProtected(ILFunction f, RawIL raw)
    {
        if (f.ReturnType.Kind != TypeKind.Void) return;
        foreach (Leave exit in RawIL.Own(f).OfType<Leave>().ToList())
        {
            if (!exit.IsLeavingFunction || !exit.Value.MatchNop()) continue;
            var inner = exit.Parent as Block;
            var body = inner == null ? null : inner.Parent as BlockContainer;
            if (body == null || body.Blocks.Count != 1 || body.Kind != ContainerKind.Normal || inner.Instructions.Count < 2 || inner.Instructions[inner.Instructions.Count - 1] != exit) continue;
            ILInstruction statement = body.Parent;
            bool guarded = (statement is UsingInstruction && ((UsingInstruction)statement).Body == body) || (statement is TryFinally && ((TryFinally)statement).TryBlock == body) || (statement is LockInstruction && ((LockInstruction)statement).Body == body);
            if (!guarded || !IsStatement(statement)) continue;
            var outer = (Block)statement.Parent;
            if (statement.ChildIndex != outer.Instructions.Count - 1) continue;
            int start = RawIL.StartOfCode(inner.Instructions[0]);
            if (start < 0) continue;
            int[] found = null;
            foreach (int[] region in raw.Regions)
                if (region[4] == 1 && region[0] == start && (found == null || region[1] < found[1])) found = region;
            RawIL.Op leave, next, target;
            if (found == null || !raw.EndsAt(found[1], out leave) || (leave.Code != ILOpCode.Leave && leave.Code != ILOpCode.Leave_s)) continue;
            if (leave.Operand != found[3] || !raw.IsAt(found[3], out next) || (next.Code != ILOpCode.Leave && next.Code != ILOpCode.Leave_s)) continue;
            if (!raw.IsAt(next.Operand, out target) || target.Code != ILOpCode.Ret) continue;
            BlockContainer exitTarget = exit.TargetContainer;
            inner.Instructions[inner.Instructions.Count - 1] = new Leave(body);
            outer.Instructions.Add(new Leave(exitTarget));
        }
    }

    // 4. Variable computed before the first argument is used.
    //        T a = A;  T b = B;  r = M(f(a), g(b));       IL: A; B; stloc b; call f; ldloc b; call g; call M
    //    (a is read once and stays on the stack, b cannot.) Is printed
    //    'T a = A; r = M(y: g(B), x: f(a));', which calls g before f and stores its result.
    private static void LocalsUnderNamedArguments(ILFunction f, RawIL raw)
    {
        foreach (Block named in RawIL.Own(f).OfType<Block>().ToList())
        {
            if (named.Kind != BlockKind.CallWithNamedArgs || named.Parent == null || named.Instructions.Count != 1) continue;
            var first = named.Instructions[0] as StLoc;
            var final = named.FinalInstruction as CallInstruction;
            if (first == null || final == null || first.Variable.LoadCount != 1 || first.Variable.StoreCount != 1) continue;
            LdLoc use = final.Arguments.OfType<LdLoc>().FirstOrDefault(l => l.Variable == first.Variable);
            if (use == null) continue;
            // The stored value: the argument itself, or what it is read from ('f(X)', 'X.f').
            ILInstruction value = first.Value;
            RawIL.Op store = default(RawIL.Op);
            bool found = false;
            while (value != null && !found)
            {
                if (!(value is LdLoc) && value.ResultType != StackType.Ref && StoreAfter(value, raw, out store)
                    && raw.Ops.Count(o => (RawIL.IsLdLoc(o.Code) || RawIL.IsStLoc(o.Code) || o.Code == ILOpCode.Ldloca || o.Code == ILOpCode.Ldloca_s) && o.Operand == store.Operand) == 2) { found = true; break; }
                var call = value as CallInstruction;
                if (call != null && call.Arguments.Count == 1) value = call.Arguments[0];
                else if (value is LdObj) value = ((LdObj)value).Target;
                else if (value is LdFlda) value = ((LdFlda)value).Target;
                else value = null;
            }
            if (!found || value == null) continue;
            int slot = store.Operand, i0;
            // The value is stored, something else is done with what is under it, then it is read.
            RawIL.Op next;
            if (!raw.Near(store.Offset, 1, out next) || RawIL.IsLdLoc(next.Code) || !raw.IndexAt.TryGetValue(store.Offset, out i0)) continue;
            bool read = false;
            for (int i = i0 + 2; i < raw.Ops.Count && i < i0 + 12 && !read; i++)
            {
                if (raw.Targets.Contains(raw.Ops[i].Offset)) break;
                if (RawIL.IsLdLoc(raw.Ops[i].Code) && raw.Ops[i].Operand == slot) read = true;
            }
            if (!read) continue;
            IType type = raw.LocalType(slot);
            if (type == null || type.Kind == TypeKind.Unknown) continue;
            if (f.Variables.Any(x => x.Kind != VariableKind.Parameter && x.Index == slot && (x.LoadCount + x.StoreCount + x.AddressCount) > 0)) continue;
            // The block is the first thing its statement evaluates.
            ILInstruction statement = named;
            bool leading = true;
            while (!IsStatement(statement) && leading)
            {
                ILInstruction parent = statement.Parent;
                var outer = parent as CallInstruction;
                if (outer != null) leading = outer.Arguments.Count > 0 && outer.Arguments[0] == statement;
                else leading = parent is StLoc || parent is Conv;
                statement = parent;
            }
            if (!leading || statement == null || !IsStatement(statement)) continue;
            var block = (Block)statement.Parent;
            ILVariable v = f.RegisterVariable(VariableKind.Local, type);
            value.ReplaceWith(new LdLoc(v));
            ILInstruction argument = first.Value;
            first.Value = new Nop();
            named.FinalInstruction = new Nop();
            use.ReplaceWith(argument);
            named.ReplaceWith(final);
            block.Instructions.Insert(statement.ChildIndex, new StLoc(v, value));
        }
    }

    // 5. Variable assigned inside the value of an assignment.
    //        a[k] = (v = e);                   IL: a; k; e; dup; stloc v; stelem
    //    is printed 'v = (a[k] = e);', which goes through a temporary when v is a variable
    //    assigned elsewhere too ('dup; stloc t; stelem; ldloc t; stloc v').
    private static void AssignedInsideValue(ILFunction f, RawIL raw)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            var copy = store.Value as StObj;
            ILVariable v = store.Variable;
            if (copy == null || !IsStatement(store) || v.Kind != VariableKind.Local || !v.Index.HasValue || v.StoreCount < 2 || v.AddressCount != 0) continue;
            int end = RawIL.EndOf(copy.Value);
            RawIL.Op dup, kept, write;
            if (end < 0 || !raw.IsAt(end, out dup) || dup.Code != ILOpCode.Dup) continue;
            if (!raw.Near(end, 1, out kept) || !RawIL.IsStLoc(kept.Code) || kept.Operand != v.Index.Value) continue;
            if (!raw.Near(end, 2, out write)) continue;
            bool writes = write.Code == ILOpCode.Stfld || write.Code == ILOpCode.Stsfld || write.Code == ILOpCode.Stobj || write.Code == ILOpCode.Stelem
                || (write.Code >= ILOpCode.Stelem_i && write.Code <= ILOpCode.Stelem_ref) || (write.Code >= ILOpCode.Stind_ref && write.Code <= ILOpCode.Stind_r8) || write.Code == ILOpCode.Stind_i;
            if (!writes) continue;
            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            ILInstruction value = copy.Value;
            copy.Value = new Nop();
            store.Value = value;
            block.Instructions[at] = copy;
            copy.Value = store;
        }
    }

    // End of a statement without the ranges of its exits (see RawIL.StartOfCode).
    private static int EndOfCode(ILInstruction inst)
    {
        int max = -1;
        var stack = new Stack<ILInstruction>();
        stack.Push(inst);
        while (stack.Count > 0)
        {
            ILInstruction i = stack.Pop();
            if (i is ILFunction) continue;
            if (!(i is Leave) && !(i is Branch))
                foreach (var r in i.ILRanges)
                    if (r.Start != r.End && r.End > max) max = r.End;
            foreach (ILInstruction c in i.Children) stack.Push(c);
        }
        return max;
    }

    // 6. End of a 'switch' section inside an endless loop.
    //        while (true) { ...; if (c) { S; break-of-the-switch; } ... }
    //    has S between the test and the rest of the loop, and 'br END' of the switch after it.
    //    The decompiler prints 'if (c) break;' and S after the loop.
    private static void SectionEndInLoop(ILFunction f, Block block, RawIL raw)
    {
        int n = block.Instructions.Count;
        if (n < 3) return;
        // The section ends with 'break;' (of a switch or of an outer loop) or 'continue;'.
        ILInstruction last = block.Instructions[n - 1];
        var exit = last as Leave;
        if (exit != null && (exit.IsLeavingFunction || !exit.Value.MatchNop())) return;
        if (exit == null && !(last is Branch)) return;
        BlockContainer loop = null;
        int k;
        for (k = n - 3; k >= 0; k--)
        {
            loop = block.Instructions[k] as BlockContainer;
            if (loop != null && loop.Kind == ContainerKind.Loop) break;
            loop = null;
        }
        if (loop == null) return;
        var breaks = loop.Descendants.OfType<Leave>().Where(l => l.TargetContainer == loop).ToList();
        if (breaks.Count != 1 || !(breaks[0].Parent is Block || breaks[0].Parent is IfInstruction)) return;
        int loopStart = RawIL.StartOfCode(loop), loopEnd = EndOfCode(loop);
        if (loopStart < 0 || loopEnd < 0) return;
        for (int i = k + 1; i < n - 1; i++)
        {
            int at = RawIL.StartOfCode(block.Instructions[i]), end = EndOfCode(block.Instructions[i]);
            if (at < 0 || at <= loopStart || at >= loopEnd || end > loopEnd) return;
        }
        var moved = new List<ILInstruction>();
        for (int i = k + 1; i < n; i++) moved.Add(block.Instructions[i]);
        block.Instructions.RemoveRange(k + 1, n - k - 1);
        if (breaks[0].Parent is Block)
        {
            var holder = (Block)breaks[0].Parent;
            int index = breaks[0].ChildIndex;
            holder.Instructions.RemoveAt(index);
            for (int i = 0; i < moved.Count; i++) holder.Instructions.Insert(index + i, moved[i]);
        }
        else
        {
            var inner = new Block();
            inner.Instructions.AddRange(moved);
            breaks[0].ReplaceWith(inner);
        }
    }

    // 7. Object created in a variable, filled, then passed.
    //        x = new T(a);  x.f = b;  M(x);        IL: newobj T; stloc x; ldloc x; b; stfld f; ldarg.0; ldloc x; call M
    //    is printed 'M(new T(a) { f = b });' ('newobj; dup; b; stfld' under the receiver) when
    //    the decompiler finds no other use of that value.
    private static void InitializerArguments(ILFunction f, RawIL raw)
    {
        foreach (Block init in RawIL.Own(f).OfType<Block>().ToList())
        {
            if (init.Kind != BlockKind.ObjectInitializer || init.Parent == null || init.Instructions.Count < 2) continue;
            var call = init.Parent as CallInstruction;
            if (call == null || !IsStatement(call)) continue;
            bool leading = true;
            for (int a = 0; a < init.ChildIndex; a++) if (!(call.Arguments[a] is LdLoc)) leading = false;
            if (!leading || call.Arguments[init.ChildIndex] != init) continue;
            var first = init.Instructions[0] as StLoc;
            var final = init.FinalInstruction as LdLoc;
            if (first == null || final == null || final.Variable != first.Variable || !(first.Value is NewObj)) continue;
            ILVariable temp = first.Variable;
            RawIL.Op stored, loaded;
            int end = RawIL.EndOf(first.Value);
            if (end < 0 || !(raw.IsAt(end, out stored) && RawIL.IsStLoc(stored.Code)) && !(raw.EndsAt(end, out stored) && RawIL.IsStLoc(stored.Code))) continue;
            if (!raw.Near(stored.Offset, 1, out loaded) || !RawIL.IsLdLoc(loaded.Code) || loaded.Operand != stored.Operand) continue;
            int slot = stored.Operand;
            IType type = raw.LocalType(slot);
            if (type == null || type.Kind == TypeKind.Unknown || type.IsReferenceType != true) continue;
            // Every member is set through the variable itself.
            var statements = init.Instructions.Skip(1).ToList();
            bool plain = true;
            foreach (ILInstruction s in statements)
            {
                if (!(s is StObj || s is CallInstruction)) plain = false;
                if (s.Descendants.OfType<Block>().Any(b => b.Kind == BlockKind.ObjectInitializer || b.Kind == BlockKind.CollectionInitializer)) plain = false;
            }
            if (!plain) continue;
            ILVariable v = f.Variables.FirstOrDefault(x => x.Kind == VariableKind.Local && x.Index == slot && x.Type.Equals(type) && x.StoreCount > 0);
            if (v == null) v = f.RegisterVariable(VariableKind.Local, type);
            var block = (Block)call.Parent;
            int at = call.ChildIndex;
            ILInstruction created = first.Value;
            first.Value = new Nop();
            init.Instructions.Clear();
            init.FinalInstruction = new Nop();
            foreach (ILInstruction s in statements)
                foreach (LdLoc use in s.Descendants.OfType<LdLoc>().Where(l => l.Variable == temp).ToList()) use.ReplaceWith(new LdLoc(v));
            init.ReplaceWith(new LdLoc(v));
            block.Instructions.Insert(at, new StLoc(v, created));
            for (int i = 0; i < statements.Count; i++) block.Instructions.Insert(at + 1 + i, statements[i]);
        }
    }

    // 8. One variable assigned in several loops.
    //        int z;  while (a) { z = e; use(z, z); }  ...  while (b) { z = g; use(z, z); }
    //    The decompiler splits a slot whose values never meet into one variable per value,
    //    each declared in its own block: the compiler gives each a slot. The slot of the IL is
    //    one, and the compiler shares only the slots of its own temporaries: these values are
    //    stored by statements and read more than once, which a temporary is not.
    private static void SharedSlots(ILFunction f, RawIL raw)
    {
        foreach (var group in f.Variables.Where(x => x.Kind == VariableKind.Local && x.Index.HasValue && x.Function == f).GroupBy(x => x.Index.Value).ToList())
        {
            var parts = group.Where(x => x.StoreCount + x.LoadCount + x.AddressCount > 0).ToList();
            if (parts.Count < 2) continue;
            IType type = raw.LocalType(group.Key);
            if (type == null || type.Kind == TypeKind.Unknown || type.IsKnownType(KnownTypeCode.Boolean)) continue;
            if (!IsNumber(type) && type.IsReferenceType != true) continue;
            bool plain = true;
            foreach (ILVariable x in parts)
            {
                if (x.StoreCount < 1 || x.AddressCount != 0 || x.LoadCount < 1 || !x.Type.Equals(type) || x.StoreInstructions.Count != x.StoreCount) { plain = false; break; }
                foreach (var s in x.StoreInstructions)
                {
                    var store = s as StLoc;
                    if (store == null || !IsStatement(store)) { plain = false; break; }
                    // Declared inside a loop: outside one, the decompiler declares the variables side by side and their names would collide anyway.
                    if (!store.Ancestors.OfType<BlockContainer>().Any(c => c.Kind == ContainerKind.While || c.Kind == ContainerKind.DoWhile || c.Kind == ContainerKind.Loop || c.Kind == ContainerKind.For)) { plain = false; break; }
                }
                if (!plain) break;
            }
            // No value is read right where it is stored (a temporary, or a value the compiler would keep on the stack).
            for (int i = 0; i + 1 < raw.Ops.Count && plain; i++)
                if (RawIL.IsStLoc(raw.Ops[i].Code) && raw.Ops[i].Operand == group.Key && RawIL.IsLdLoc(raw.Ops[i + 1].Code) && raw.Ops[i + 1].Operand == group.Key) plain = false;
            // Nor kept while it is stored somewhere else ('dup; stloc': the temporary of an assignment used as a value).
            for (int i = 1; i < raw.Ops.Count && plain; i++)
                if (RawIL.IsStLoc(raw.Ops[i].Code) && raw.Ops[i].Operand == group.Key && raw.Ops[i - 1].Code == ILOpCode.Dup) plain = false;
            if (!plain) continue;
            ILVariable kept = parts[0];
            foreach (var use in RawIL.Own(f).OfType<IInstructionWithVariableOperand>().ToList())
                if (use.Variable != kept && parts.Contains(use.Variable)) use.Variable = kept;
        }
    }

    // 9. Struct values held in variables of their own.
    //        S a = e;  x = a.P;  ...  S b = g;  y = b.P;       IL: e; stloc a; ldloca a; call get_P ... g; stloc b; ldloca b; call get_P
    //    is printed 'x = e.P; y = g.P;', where both values go through one temporary: the
    //    compiler uses the slot of a temporary again, never the slot of a variable. Two slots
    //    of one type, each written once in front of its 'ldloca', are not both temporaries.
    private static void StructLocals(ILFunction f, RawIL raw)
    {
        var found = new List<KeyValuePair<AddressOf, int>>();
        foreach (AddressOf address in RawIL.Own(f).OfType<AddressOf>().ToList())
        {
            RawIL.Op store, next;
            if (!StoreAfter(address.Value, raw, out store) || !raw.Near(store.Offset, 1, out next)) continue;
            if ((next.Code != ILOpCode.Ldloca && next.Code != ILOpCode.Ldloca_s) || next.Operand != store.Operand) continue;
            int slot = store.Operand;
            if (raw.Ops.Count(o => (RawIL.IsLdLoc(o.Code) || RawIL.IsStLoc(o.Code) || o.Code == ILOpCode.Ldloca || o.Code == ILOpCode.Ldloca_s) && o.Operand == slot) != 2) continue;
            IType type = raw.LocalType(slot);
            if (type == null || type.Kind != TypeKind.Struct || IsNumber(type) || type.IsKnownType(KnownTypeCode.Boolean) || !type.Equals(address.Type)) continue;
            if (f.Variables.Any(x => x.Kind != VariableKind.Parameter && x.Index == slot && (x.LoadCount + x.StoreCount + x.AddressCount) > 0)) continue;
            found.Add(new KeyValuePair<AddressOf, int>(address, slot));
        }
        foreach (var group in found.GroupBy(p => raw.LocalType(p.Value)).ToList())
        {
            if (group.Select(p => p.Value).Distinct().Count() < 2 || group.Select(p => p.Value).Distinct().Count() != group.Count()) continue;
            foreach (var pair in group)
            {
                AddressOf address = pair.Key;
                ILInstruction statement = address;
                while (statement != null && !IsStatement(statement)) statement = statement.Parent;
                if (statement == null) continue;
                // Nothing with an effect is evaluated before the value in its statement.
                bool first = true;
                foreach (ILInstruction d in statement.Descendants)
                {
                    if (d == address || address.IsDescendantOf(d) || d.IsDescendantOf(address)) continue;
                    if (!EvaluatedBefore(d, address, statement)) continue;
                    if (!(d is LdLoc || d is LdLoca || d is LdStr || d is LdcI4 || d is LdNull || d is LdcI8 || d is LdcF4 || d is LdcF8)) first = false;
                }
                if (!first) continue;
                var block = (Block)statement.Parent;
                ILVariable v = f.RegisterVariable(VariableKind.Local, address.Type);
                ILInstruction value = address.Value;
                address.Value = new Nop();
                address.ReplaceWith(new LdLoca(v));
                block.Instructions.Insert(statement.ChildIndex, new StLoc(v, value));
            }
        }
    }

    // True when d is evaluated before 'target' in 'root' (children are evaluated in order).
    private static bool EvaluatedBefore(ILInstruction d, ILInstruction target, ILInstruction root)
    {
        var path = new List<ILInstruction>();
        for (ILInstruction p = target; p != null && p != root.Parent; p = p.Parent) path.Add(p);
        ILInstruction child = d;
        for (ILInstruction p = d.Parent; p != null && p != root.Parent; child = p, p = p.Parent)
        {
            int at = path.IndexOf(p);
            if (at <= 0) continue;
            return child.ChildIndex < path[at - 1].ChildIndex;
        }
        return false;
    }

    // 10. 'try' under a test.
    //        if (c) { try { } catch { } }      IL: c; brfalse NEXT; <try>
    //    is printed 'if (!c) continue; try { } catch { }', where the compiler puts a 'nop' in
    //    front of the 'try' (a label stands there: the end of the 'if').
    private static void TryUnderTest(ILFunction f, Block block, RawIL raw)
    {
        int n = block.Instructions.Count;
        for (int k = 0; k + 2 < n; k++)
        {
            var guard = block.Instructions[k] as IfInstruction;
            if (guard == null || !guard.FalseInst.MatchNop()) continue;
            ILInstruction exit = guard.TrueInst;
            var only = exit as Block;
            if (only != null && only.Instructions.Count == 1) exit = only.Instructions[0];
            ILInstruction last = block.Instructions[n - 1];
            var jump = exit as Branch;
            var leave = exit as Leave;
            bool same = (jump != null && last is Branch && ((Branch)last).TargetBlock == jump.TargetBlock)
                || (leave != null && leave.Value.MatchNop() && !leave.IsLeavingFunction && last is Leave && ((Leave)last).TargetContainer == leave.TargetContainer && ((Leave)last).Value.MatchNop());
            // With another exit the test keeps it as its 'else': 'if (c) { try { } ... } else { break; }'.
            bool other = !same && (jump != null || leave != null) && (last is Branch || last is Leave);
            if (!same && !other) continue;
            ILInstruction statement = block.Instructions[k + 1];
            BlockContainer body = null;
            if (statement is TryCatch) body = ((TryCatch)statement).TryBlock as BlockContainer;
            else if (statement is TryFinally) body = ((TryFinally)statement).TryBlock as BlockContainer;
            if (body == null || body.EntryPoint.Instructions.Count == 0) continue;
            int start = RawIL.StartOfCode(body.EntryPoint.Instructions[0]);
            RawIL.Op before;
            if (start < 0 || !raw.Regions.Any(r => r[0] == start) || !raw.EndsAt(start, out before) || !RawIL.IsCondBranch(before.Code)) continue;
            int conditionEnd = RawIL.EndOf(guard.Condition);
            if (conditionEnd != before.Offset && conditionEnd != before.End) continue;
            ILInstruction condition = guard.Condition, positive;
            guard.Condition = new Nop();
            if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); condition = positive; }
            else condition = Comp.LogicNot(condition);
            var moved = new List<ILInstruction>();
            for (int i = k + 1; i < n - 1; i++) moved.Add(block.Instructions[i]);
            block.Instructions.RemoveRange(k + 1, n - k - 2);
            var inner = new Block();
            inner.Instructions.AddRange(moved);
            guard.Condition = condition;
            guard.TrueInst = inner;
            if (other)
            {
                var otherwise = new Block();
                otherwise.Instructions.Add(jump != null ? (ILInstruction)new Branch(jump.TargetBlock) : new Leave(leave.TargetContainer, leave.Value.Clone()));
                guard.FalseInst = otherwise;
            }
            return;
        }
    }

    // 11. 'else' that ends a 'try'.
    //        try { ...; if (c) { T } else { R } } catch { }  break;       IL: T; br L; R; L: leave
    //    is printed 'try { ...; if (c) { T; break; } R } catch { }  break;', where T leaves the
    //    'try' by a 'leave' of its own.
    private static void ElseInTry(ILFunction f, RawIL raw)
    {
        foreach (TryCatch statement in RawIL.Own(f).OfType<TryCatch>().ToList())
        {
            if (!IsStatement(statement)) continue;
            var body = statement.TryBlock as BlockContainer;
            var outer = (Block)statement.Parent;
            if (body == null || body.Blocks.Count != 1 || statement.ChildIndex + 1 >= outer.Instructions.Count) continue;
            var follow = outer.Instructions[statement.ChildIndex + 1] as Leave;
            if (follow == null || !follow.Value.MatchNop() || follow.IsLeavingFunction) continue;
            Block block = body.EntryPoint;
            int n = block.Instructions.Count;
            var closing = n == 0 ? null : block.Instructions[n - 1] as Leave;
            if (closing == null || closing.TargetContainer != body) continue;
            for (int k = 0; k + 2 < n; k++)
            {
                var test = block.Instructions[k] as IfInstruction;
                if (test == null || !test.FalseInst.MatchNop()) continue;
                var then = test.TrueInst as Block;
                if (then == null || then.Instructions.Count < 2) continue;
                var exit = then.Instructions[then.Instructions.Count - 1] as Leave;
                if (exit == null || !exit.Value.MatchNop() || exit.TargetContainer != follow.TargetContainer) continue;
                int thenEnd = EndOfCode(then.Instructions[then.Instructions.Count - 2]), restStart = RawIL.StartOfCode(block.Instructions[k + 1]);
                RawIL.Op jump, target;
                // (The range of the last statement may include the jump that follows it.)
                if (thenEnd < 0 || restStart < 0) continue;
                if (!(raw.IsAt(thenEnd, out jump) && (jump.Code == ILOpCode.Br || jump.Code == ILOpCode.Br_s)) && !(raw.EndsAt(thenEnd, out jump) && (jump.Code == ILOpCode.Br || jump.Code == ILOpCode.Br_s))) continue;
                if (jump.End != restStart) continue;
                if (!raw.IsAt(jump.Operand, out target) || (target.Code != ILOpCode.Leave && target.Code != ILOpCode.Leave_s) || !raw.RegionEnds.Contains(target.End)) continue;
                then.Instructions.RemoveAt(then.Instructions.Count - 1);
                var rest = new Block();
                var moved = new List<ILInstruction>();
                for (int i = k + 1; i < n - 1; i++) moved.Add(block.Instructions[i]);
                block.Instructions.RemoveRange(k + 1, n - k - 2);
                rest.Instructions.AddRange(moved);
                test.FalseInst = rest;
                // T itself may end with 'if (!d) break; U' ('d; brtrue L; leave OUT; L: U'), printed 'if (d) { U }' while T ended on its exit.
                var tail = then.Instructions[then.Instructions.Count - 1] as IfInstruction;
                var guarded = tail == null ? null : tail.TrueInst as Block;
                if (guarded != null && tail.FalseInst.MatchNop() && guarded.Instructions.Count > 0)
                {
                    int conditionEnd = RawIL.EndOf(tail.Condition), bodyStart = RawIL.StartOfCode(guarded.Instructions[0]);
                    RawIL.Op skip, quit;
                    if (conditionEnd >= 0 && ((raw.IsAt(conditionEnd, out skip) && RawIL.IsCondBranch(skip.Code)) || (raw.EndsAt(conditionEnd, out skip) && RawIL.IsCondBranch(skip.Code)))
                        && raw.Near(skip.Offset, 1, out quit) && (quit.Code == ILOpCode.Leave || quit.Code == ILOpCode.Leave_s) && quit.End == bodyStart && skip.Operand == bodyStart)
                    {
                        ILInstruction condition = tail.Condition, positive;
                        tail.Condition = new Nop();
                        if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); condition = positive; }
                        else condition = Comp.LogicNot(condition);
                        var inside = guarded.Instructions.ToList();
                        guarded.Instructions.Clear();
                        then.Instructions.RemoveAt(then.Instructions.Count - 1);
                        then.Instructions.Add(new IfInstruction(condition, new Leave(follow.TargetContainer)));
                        then.Instructions.AddRange(inside);
                    }
                }
                break;
            }
        }
    }

    // 12. Variable read twice from the stack.
    //        T a = X;  bool b = a != null;  T r = a ?? e;      IL: X; dup; ldnull; cgt.un; stloc b; dup; brtrue L; pop; e; L: stloc r
    //    (a is read by 'dup': it has no slot.) Is printed 'T s = X; bool b = s != null;
    //    if (s == null) s = e; T r = s;', where s is assigned twice and gets a slot.
    private static void CoalescedStackValues(ILFunction f, RawIL raw)
    {
        foreach (StLoc first in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            ILVariable s = first.Variable;
            if (s.Kind != VariableKind.StackSlot || s.StoreCount != 2 || s.AddressCount != 0 || !IsStatement(first) || first.Parent == null) continue;
            var block = (Block)first.Parent;
            int end = RawIL.EndOf(first.Value);
            RawIL.Op dup;
            if (end < 0 || !raw.IsAt(end, out dup) || dup.Code != ILOpCode.Dup) continue;
            // Statements that read it, then 'if (s == null) s = e;', then the statement that takes it.
            int j = first.ChildIndex + 1;
            while (j < block.Instructions.Count && !(block.Instructions[j] is IfInstruction) && block.Instructions[j] is StLoc
                && block.Instructions[j].Descendants.OfType<LdLoc>().Any(l => l.Variable == s)) j++;
            if (j + 1 >= block.Instructions.Count) continue;
            var test = block.Instructions[j] as IfInstruction;
            var taken = block.Instructions[j + 1] as StLoc;
            if (test == null || taken == null || !test.FalseInst.MatchNop() || !taken.Value.MatchLdLoc(s)) continue;
            ILInstruction left, right;
            if (!test.Condition.MatchCompEqualsNull(out left) || !left.MatchLdLoc(s)) continue;
            var then = test.TrueInst as Block;
            var second = then != null && then.Instructions.Count == 1 ? then.Instructions[0] as StLoc : test.TrueInst as StLoc;
            if (second == null || second.Variable != s) continue;
            int reads = 2;
            for (int i = first.ChildIndex + 1; i < j; i++) reads += block.Instructions[i].Descendants.OfType<LdLoc>().Count(l => l.Variable == s);
            if (s.LoadCount != reads) continue;
            if (taken.Variable.Type.IsReferenceType != true) continue;
            ILVariable v = f.RegisterVariable(VariableKind.Local, taken.Variable.Type);
            right = second.Value;
            second.Value = new Nop();
            for (int i = first.ChildIndex + 1; i < j; i++)
                foreach (LdLoc use in block.Instructions[i].Descendants.OfType<LdLoc>().Where(l => l.Variable == s).ToList()) use.ReplaceWith(new LdLoc(v));
            ILInstruction value = first.Value;
            first.Value = new Nop();
            block.Instructions[first.ChildIndex] = new StLoc(v, value);
            taken.Value = new NullCoalescingInstruction(NullCoalescingKind.Ref, new LdLoc(v), right) { UnderlyingResultType = StackType.O };
            block.Instructions.RemoveAt(j);
        }
    }

    private static bool IsNumber(IType type)
    {
        var d = type.GetDefinition();
        if (d == null) return false;
        switch (d.KnownTypeCode)
        {
            case KnownTypeCode.Int32: case KnownTypeCode.UInt32: case KnownTypeCode.Int64: case KnownTypeCode.UInt64:
            case KnownTypeCode.Int16: case KnownTypeCode.UInt16: case KnownTypeCode.Byte: case KnownTypeCode.SByte:
            case KnownTypeCode.Single: case KnownTypeCode.Double: case KnownTypeCode.Char:
                return true;
        }
        return false;
    }

    // A conditional with a value in each branch (not 'a && b', 'a || b').
    private static bool IsChoice(ILInstruction inst)
    {
        var choice = inst as IfInstruction;
        int k;
        return choice != null && choice.ResultType == StackType.I4 && !choice.TrueInst.MatchLdcI4(out k) && !choice.FalseInst.MatchLdcI4(out k);
    }

    private static void Leaves(ILInstruction inst, List<ILInstruction> leaves)
    {
        if (IsChoice(inst))
        {
            Leaves(((IfInstruction)inst).TrueInst, leaves);
            Leaves(((IfInstruction)inst).FalseInst, leaves);
        }
        else leaves.Add(inst);
    }

    private static ILInstruction Assign(ILInstruction inst, ILVariable v, RawIL raw)
    {
        if (!IsChoice(inst)) return new StLoc(v, inst);
        var choice = (IfInstruction)inst;
        ILInstruction condition = choice.Condition, one = choice.TrueInst, two = choice.FalseInst, positive;
        int a = RawIL.StartOf(one), b = RawIL.StartOf(two);
        choice.Condition = new Nop();
        choice.TrueInst = new Nop();
        choice.FalseInst = new Nop();
        if (a >= 0 && b >= 0 && b < a)
        {
            if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); condition = positive; }
            else
            {
                // '!e' kept as a value: 'e; ldc.i4.0; ceq' is in the range of the negation.
                int end = RawIL.EndOf(condition);
                RawIL.Op zero, equal;
                condition = Comp.LogicNot(condition);
                if (end >= 0 && raw.IsAt(end, out zero) && zero.Code == ILOpCode.Ldc_i4_0 && raw.Near(end, 1, out equal) && equal.Code == ILOpCode.Ceq) condition.AddILRange(new ICSharpCode.Decompiler.Util.Interval(end, equal.End));
            }
            ILInstruction swap = one; one = two; two = swap;
        }
        var then = new Block();
        then.Instructions.Add(Assign(one, v, raw));
        var otherwise = new Block();
        otherwise.Instructions.Add(Assign(two, v, raw));
        return new IfInstruction(condition, then, otherwise);
    }

    private static void BranchAssignedConditions(ILFunction f, RawIL raw, ILTransformContext context)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (test.Parent == null || !IsStatement(test)) continue;
            // 'a && b && X': X is the value.
            IfInstruction holder = null;
            ILInstruction value = test.Condition;
            for (;;)
            {
                var both = value as IfInstruction;
                if (both == null || IsChoice(both) || !both.FalseInst.MatchLdcI4(0)) break;
                holder = both;
                value = both.TrueInst;
            }
            if (!IsChoice(value)) continue;
            if (holder != null && !test.FalseInst.MatchNop()) continue;
            var leaves = new List<ILInstruction>();
            Leaves(value, leaves);
            int slot = -1;
            var offsets = new HashSet<int>();
            bool same = true;
            foreach (ILInstruction leaf in leaves)
            {
                RawIL.Op store;
                if (!StoreAfter(leaf, raw, out store) || (slot >= 0 && store.Operand != slot) || !offsets.Add(store.Offset)) { same = false; break; }
                slot = store.Operand;
            }
            if (!same || slot < 0) continue;
            IType type = raw.LocalType(slot);
            if (type == null || !type.IsKnownType(KnownTypeCode.Boolean)) continue;
            int stores = 0, loads = 0, at = -1;
            bool other = false;
            for (int i = 0; i < raw.Ops.Count; i++)
            {
                RawIL.Op op = raw.Ops[i];
                if (RawIL.IsStLoc(op.Code) && op.Operand == slot) stores++;
                if (RawIL.IsLdLoc(op.Code) && op.Operand == slot) { loads++; at = i; }
                if ((op.Code == ILOpCode.Ldloca || op.Code == ILOpCode.Ldloca_s) && op.Operand == slot) other = true;
            }
            if (other || stores != leaves.Count || loads != 1 || at + 1 >= raw.Ops.Count || !IsBranchOnBool(raw.Ops[at + 1].Code)) continue;
            if (f.Variables.Any(x => x.Kind != VariableKind.Parameter && x.Index == slot && (x.LoadCount + x.StoreCount + x.AddressCount) > 0)) continue;

            var block = (Block)test.Parent;
            ILVariable v = f.RegisterVariable(VariableKind.Local, type);
            if (holder == null)
            {
                test.Condition = new Nop();
                ILInstruction assigned = Assign(value, v, raw);
                test.Condition = new LdLoc(v);
                block.Instructions.Insert(test.ChildIndex, assigned);
            }
            else
            {
                holder.TrueInst = new Nop();
                ILInstruction guard = holder.Condition;
                holder.Condition = new Nop();
                if (holder == test.Condition) test.Condition = guard; else holder.ReplaceWith(guard);
                ILInstruction body = test.TrueInst;
                test.TrueInst = new Nop();
                var inner = new Block();
                inner.Instructions.Add(Assign(value, v, raw));
                inner.Instructions.Add(new IfInstruction(new LdLoc(v), body));
                test.TrueInst = inner;
            }
        }
    }
}
