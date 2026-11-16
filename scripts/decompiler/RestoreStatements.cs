// IL transform: statements the decompiler folds into a shorter form that compiles to other
// instructions. Each is decided by the raw method body.
//
// 1. Null check through a local.
//        T v = e;  if (v != null) v.M(a);      IL: e; stloc v; ldloc v; brfalse L; ldloc v; a; callvirt M
//    is printed 'e?.M(a);', which compiles to 'e; dup; brtrue; pop'. When the IL stores the
//    receiver in a local, the local is given back.
//
// 2. lock, compiled by an older compiler.
//        Monitor.Enter(v = e); try { } finally { Monitor.Exit(v); }
//    is printed 'lock (e) { }', which Roslyn compiles with Monitor.Enter(object, ref bool).
//    When every Monitor.Enter of the method is the one-argument overload, the statements are
//    written out.
//
// 3. Assignment before return.
//        x = e;  return x;                     IL: e; stloc x; ldloc x; ret
//    is printed 'return e;' when x is not read again.
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreStatements : IILTransform
{
    public void Run(ILFunction function, ILTransformContext context)
    {
        var functions = function.Descendants.OfType<ILFunction>().ToList();
        functions.Add(function);
        foreach (ILFunction f in functions)
        {
            if (f.Method == null) continue;
            // DECOMP_DEBUG=<full name>[;<full name>]: prints the tree of these methods as the transforms find it.
            string debug = System.Environment.GetEnvironmentVariable("DECOMP_DEBUG");
            if (debug != null && debug.Split(';').Contains(f.Method.FullName)) System.Console.Error.WriteLine("== " + f.Method.FullName + " " + f.Body);
            RawIL raw = RawIL.Read(f, context);
            if (raw == null) continue;
            if (Program.Enabled("RestoreNullCheckLocal", f.Method.MetadataToken)) NullCheckLocals(f, raw);
            if (Program.Enabled("RestoreMonitor", f.Method.MetadataToken)) Monitors(f, raw);
            if (Program.Enabled("RestoreReturnLocal", f.Method.MetadataToken)) ReturnVariables(f, raw);
            if (Program.Enabled("RestoreReturnLocal", f.Method.MetadataToken)) ReturnLocals(f, raw);
            if (Program.Enabled("RestoreNullCheckLocal", f.Method.MetadataToken)) NullCheckReturns(f, raw);
            if (Program.Enabled("RestoreInitializer", f.Method.MetadataToken)) Initializers(f, raw);
            if (Program.Enabled("RestoreReturnLocal", f.Method.MetadataToken)) TryResults(f, raw);
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

    private static void NullCheckLocals(ILFunction f, RawIL raw)
    {
        foreach (NullableRewrap rewrap in RawIL.Own(f).OfType<NullableRewrap>().ToList())
        {
            if (!IsStatement(rewrap)) continue;
            // 'e?.M(a);' or 'e?.f.M(a);': one receiver, checked once.
            ILInstruction call = rewrap.Argument;
            var unwraps = rewrap.Descendants.OfType<NullableUnwrap>().ToList();
            if (unwraps.Count != 1) continue;
            NullableUnwrap unwrap = unwraps[0];
            if (unwrap.Argument is LdLoc || unwrap.ResultType != StackType.O) continue;
            int end = RawIL.EndOf(unwrap.Argument);
            RawIL.Op store, load, branch, again;
            // The range of an inlined value may include the 'stloc' that stored it.
            if (end < 0) continue;
            if (!(raw.IsAt(end, out store) && RawIL.IsStLoc(store.Code)) && !(raw.EndsAt(end, out store) && RawIL.IsStLoc(store.Code))) continue;
            end = store.Offset;
            if (!raw.Near(end, 1, out load) || !RawIL.IsLdLoc(load.Code) || load.Operand != store.Operand) continue;
            if (!raw.Near(end, 2, out branch) || !IsBranchOnBool(branch.Code)) continue;
            if (!raw.Near(end, 3, out again)) continue;
            // 'if (v == null) return; v.M(a);': a 'ret' between the test and the call.
            bool leaves = false;
            if (again.Code == ILOpCode.Ret && (branch.Code == ILOpCode.Brtrue || branch.Code == ILOpCode.Brtrue_s))
            {
                if (!raw.Near(end, 4, out again) || !(f.Body is BlockContainer)) continue;
                leaves = true;
            }
            if (!RawIL.IsLdLoc(again.Code) || again.Operand != store.Operand) continue;
            IType type = raw.LocalType(store.Operand);
            if (type == null) continue;

            var block = (Block)rewrap.Parent;
            int at = rewrap.ChildIndex;
            ILVariable v = f.RegisterVariable(VariableKind.Local, type);
            ILInstruction receiver = unwrap.Argument;
            unwrap.Argument = new Nop();
            rewrap.Argument = new Nop();
            unwrap.ReplaceWith(new LdLoc(v));
            if (leaves)
            {
                block.Instructions[at] = call;
                block.Instructions.Insert(at, new IfInstruction(new Comp(ComparisonKind.Equality, Sign.None, new LdLoc(v), new LdNull()), new Leave((BlockContainer)f.Body)));
                block.Instructions.Insert(at, new StLoc(v, receiver));
                continue;
            }
            var then = new Block();
            then.Instructions.Add(call);
            block.Instructions[at] = new IfInstruction(new Comp(ComparisonKind.Inequality, Sign.None, new LdLoc(v), new LdNull()), then);
            block.Instructions.Insert(at, new StLoc(v, receiver));
        }
    }

    // The same with a value:
    //        T v = e;  if (v == null) return d;  return v.f;
    // is printed 'return e?.f ?? d;'.
    private static void NullCheckReturns(ILFunction f, RawIL raw)
    {
        foreach (NullCoalescingInstruction coalesce in RawIL.Own(f).OfType<NullCoalescingInstruction>().ToList())
        {
            var leave = coalesce.Parent as Leave;
            var rewrap = coalesce.ValueInst as NullableRewrap;
            if (leave == null || rewrap == null || !leave.IsLeavingFunction || !IsStatement(leave)) continue;
            var unwraps = rewrap.Descendants.OfType<NullableUnwrap>().ToList();
            if (unwraps.Count != 1) continue;
            NullableUnwrap unwrap = unwraps[0];
            if (unwrap.Argument is LdLoc || unwrap.ResultType != StackType.O) continue;
            int end = RawIL.EndOf(unwrap.Argument);
            RawIL.Op store, load, branch;
            if (end < 0) continue;
            if (!(raw.IsAt(end, out store) && RawIL.IsStLoc(store.Code)) && !(raw.EndsAt(end, out store) && RawIL.IsStLoc(store.Code))) continue;
            if (!raw.Near(store.Offset, 1, out load) || !RawIL.IsLdLoc(load.Code) || load.Operand != store.Operand) continue;
            if (!raw.Near(store.Offset, 2, out branch) || !IsBranchOnBool(branch.Code)) continue;
            IType type = raw.LocalType(store.Operand);
            if (type == null) continue;

            var block = (Block)leave.Parent;
            int at = leave.ChildIndex;
            BlockContainer target = leave.TargetContainer;
            ILVariable v = f.RegisterVariable(VariableKind.Local, type);
            ILInstruction receiver = unwrap.Argument, value = rewrap.Argument, fallback = coalesce.FallbackInst;
            unwrap.Argument = new Nop();
            unwrap.ReplaceWith(new LdLoc(v));
            rewrap.Argument = new Nop();
            coalesce.FallbackInst = new Nop();
            bool nullFirst = branch.Code == ILOpCode.Brtrue || branch.Code == ILOpCode.Brtrue_s;
            ILInstruction test, last;
            if (nullFirst)
            {
                test = new IfInstruction(new Comp(ComparisonKind.Equality, Sign.None, new LdLoc(v), new LdNull()), new Leave(target, fallback));
                last = new Leave(target, value);
            }
            else
            {
                test = new IfInstruction(new Comp(ComparisonKind.Inequality, Sign.None, new LdLoc(v), new LdNull()), new Leave(target, value));
                last = new Leave(target, fallback);
            }
            last.AddILRange(leave);
            block.Instructions[at] = last;
            block.Instructions.Insert(at, test);
            block.Instructions.Insert(at, new StLoc(v, receiver));
        }
    }

    // 4. Assignments after construction.
    //        T x = new T();  x.f = a;              IL: newobj T; stloc x; ldloc x; a; stfld f
    //    is printed 'T x = new T { f = a };', which keeps the object on the stack ('dup').
    // 6. Result assigned in both branches.
    //        T x;  if (c) x = a; else x = b;  return x;
    //    is printed 'if (c) return a; return b;'. The IL stores both values in one slot and reads
    //    it once: 'a; stloc x; br E; b; stloc x; E: ldloc x; ret'. Two returns have a 'ret' each.
    // The 'stloc' that follows a returned value. The ranges of the value may go on over it and
    // over the 'ldloc; ret' the two branches share: the first 'stloc' after its first range.
    private static bool StoreAfter(ILInstruction value, RawIL raw, out RawIL.Op store)
    {
        store = default(RawIL.Op);
        int start = RawIL.StartOf(value), i;
        if (start < 0 || !raw.IndexAt.TryGetValue(start, out i)) return false;
        var ends = new HashSet<int>();
        foreach (ILInstruction d in value.Descendants.Concat(new[] { value }))
            foreach (var r in d.ILRanges) if (r.Start != r.End) ends.Add(r.End);
        for (; i < raw.Ops.Count; i++)
        {
            RawIL.Op op = raw.Ops[i];
            if (RawIL.IsStLoc(op.Code)) { if (!ends.Contains(op.Offset) && !ends.Contains(op.End)) return false; store = op; return true; }
            if (RawIL.IsCondBranch(op.Code) || op.Code == ILOpCode.Br || op.Code == ILOpCode.Br_s || op.Code == ILOpCode.Ret || op.Code == ILOpCode.Switch || op.Code == ILOpCode.Throw) return false;
        }
        return false;
    }

    private static void ReturnVariables(ILFunction f, RawIL raw)
    {
        var body = f.Body as BlockContainer;
        if (body == null) return;
        foreach (Block block in RawIL.Own(f).OfType<Block>().ToList())
        {
            int n = block.Instructions.Count;
            if (n < 2 || block.Kind != BlockKind.ControlFlow) continue;
            var guard = block.Instructions[n - 2] as IfInstruction;
            var last = block.Instructions[n - 1] as Leave;
            if (guard == null || last == null || !guard.FalseInst.MatchNop() || last.TargetContainer != body || last.Value.MatchNop()) continue;
            var early = guard.TrueInst as Leave;
            var then = guard.TrueInst as Block;
            if (early == null && then != null && then.Instructions.Count > 0 && then.FinalInstruction.MatchNop()) early = then.Instructions[then.Instructions.Count - 1] as Leave;
            if (early == null || early.TargetContainer != body || early.Value.MatchNop()) continue;
            // 'Descendants' includes the instruction itself.
            if (guard.TrueInst.Descendants.OfType<Leave>().Count(l => l.TargetContainer == body) != 1) continue;
            RawIL.Op s1, s2, jump, load, ret;
            if (!StoreAfter(early.Value, raw, out s1) || !StoreAfter(last.Value, raw, out s2)) continue;
            if (s1.Operand != s2.Operand || s1.Offset == s2.Offset) continue;
            RawIL.Op firstStore = s1.Offset < s2.Offset ? s1 : s2, secondStore = s1.Offset < s2.Offset ? s2 : s1;
            if (!raw.Near(firstStore.Offset, 1, out jump) || (jump.Code != ILOpCode.Br && jump.Code != ILOpCode.Br_s) || jump.Operand != secondStore.End) continue;
            if (!raw.IsAt(secondStore.End, out load) || !RawIL.IsLdLoc(load.Code) || load.Operand != s1.Operand) continue;
            if (!raw.Near(load.Offset, 1, out ret) || ret.Code != ILOpCode.Ret) continue;
            int slot = s1.Operand, stores = 0, reads = 0;
            foreach (RawIL.Op op in raw.Ops)
            {
                if (RawIL.IsStLoc(op.Code) && op.Operand == slot) stores++;
                else if ((RawIL.IsLdLoc(op.Code) || op.Code == ILOpCode.Ldloca || op.Code == ILOpCode.Ldloca_s) && op.Operand == slot) reads++;
            }
            IType type = raw.LocalType(slot);
            if (stores != 2 || reads != 1 || type == null || type.Kind == TypeKind.Unknown) continue;

            ILVariable v = f.RegisterVariable(VariableKind.Local, type);
            ILInstruction a = early.Value, b = last.Value;
            early.Value = new Nop();
            last.Value = new Nop();
            if (then == null) { then = new Block(); guard.TrueInst = then; }
            else then.Instructions.RemoveAt(then.Instructions.Count - 1);
            then.Instructions.Add(new StLoc(v, a));
            var otherwise = new Block();
            otherwise.Instructions.Add(new StLoc(v, b));
            if (s1.Offset < s2.Offset) guard.FalseInst = otherwise;
            else
            {
                // The decompiler turned the test around: the branch the IL has first comes first.
                ILInstruction condition = guard.Condition, positive;
                guard.Condition = new Nop();
                guard.TrueInst = new Nop();
                if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); condition = positive; }
                else condition = Comp.LogicNot(condition);
                guard.Condition = condition;
                guard.TrueInst = otherwise;
                guard.FalseInst = then;
            }
            block.Instructions[n - 1] = new Leave(body, new LdLoc(v));
        }
    }

    // 'T[] x = new T[1]; x[0] = a; F(x, 0, 1);' printed 'F(new T[1] { a }, 0, 1);': the array is
    // stored and read back right after 'newarr', where the initializer has a 'dup'. It gets
    // its variable back, and Initializers writes the statements out.
    private static void HoistArrayArguments(ILFunction f, RawIL raw)
    {
        foreach (Block init in RawIL.Own(f).OfType<Block>().ToList())
        {
            if (init.Kind != BlockKind.ArrayInitializer || init.Parent == null || init.Parent is StLoc || init.Instructions.Count < 2) continue;
            var first = init.Instructions[0] as StLoc;
            var final = init.FinalInstruction as LdLoc;
            if (first == null || final == null || final.Variable != first.Variable || !(first.Value is NewArr)) continue;
            int start = RawIL.StartOf(init.Instructions[1]);
            RawIL.Op at0, back, loaded;
            if (start < 0 || !raw.IsAt(start, out at0)) continue;
            bool stored = false;
            for (int k = 1; k <= 8; k++)
            {
                if (!raw.Near(start, -k, out back)) break;
                if (back.Code != ILOpCode.Newarr) continue;
                if (raw.Near(back.Offset, 1, out at0) && RawIL.IsStLoc(at0.Code) && raw.Near(back.Offset, 2, out loaded) && RawIL.IsLdLoc(loaded.Code) && loaded.Operand == at0.Operand) stored = true;
                break;
            }
            if (!stored) continue;
            // Nothing but plain loads is evaluated before the array in its statement.
            ILInstruction statement = init;
            bool plain = true;
            while (plain && !(statement.Parent is Block && statement.SlotInfo == Block.InstructionSlot))
            {
                ILInstruction parent = statement.Parent;
                if (!(parent is CallInstruction || parent is StObj || parent is Leave)) { plain = false; break; }
                for (int k = 0; k < statement.ChildIndex; k++)
                {
                    ILInstruction before = parent.Children[k];
                    if (!(before is LdLoc || before is LdcI4 || before is LdNull || before is LdStr)) plain = false;
                }
                statement = parent;
            }
            if (!plain || !IsStatement(statement)) continue;
            var block = (Block)statement.Parent;
            ILVariable v = f.RegisterVariable(VariableKind.Local, first.Variable.Type);
            init.ReplaceWith(new LdLoc(v));
            block.Instructions.Insert(statement.ChildIndex, new StLoc(v, init));
        }
    }

    // 'T[] x = new T[n]; x[0] = c0; x[1] = c1; ... return x;' printed 'return new T[n] { c0, c1, ... }'.
    // With three constants or more the compiler fills an array of a primitive type from a
    // data blob ('ldtoken; call InitializeArray'). Where the IL stores each constant instead
    // ('newarr; dup; ldc i; ldc c; stelem' and so on), the source had separate statements:
    // the compiler keeps the variable on the stack, each use but the last being a 'dup'
    // (checked with scripts/dev/try-csharp.ps1). Only for an array that is returned, where
    // the variable is sure to stay on the stack.
    private static void ConstantArrayStatements(ILFunction f, RawIL raw)
    {
        foreach (Leave result in RawIL.Own(f).OfType<Leave>().ToList())
        {
            var init = result.Value as Block;
            if (!result.IsLeavingFunction || init == null || init.Kind != BlockKind.ArrayInitializer || init.Instructions.Count < 4 || !IsStatement(result)) continue;
            var first = init.Instructions[0] as StLoc;
            var final = init.FinalInstruction as LdLoc;
            if (first == null || final == null || final.Variable != first.Variable || !(first.Value is NewArr)) continue;
            bool constants = true;
            for (int i = 1; i < init.Instructions.Count && constants; i++)
            {
                var element = init.Instructions[i] as StObj;
                constants = element != null && (element.Value is LdcI4 || element.Value is LdcI8 || element.Value is LdcF4 || element.Value is LdcF8);
            }
            if (!constants) continue;
            int start = RawIL.StartOf(init.Instructions[1]);
            RawIL.Op at0, back, dup, next;
            if (start < 0 || !raw.IsAt(start, out at0)) continue;
            bool stored = false;
            for (int k = 0; k <= 8; k++)
            {
                if (!raw.Near(start, -k, out back)) break;
                if (back.Code != ILOpCode.Newarr) continue;
                stored = raw.Near(back.Offset, 1, out dup) && dup.Code == ILOpCode.Dup
                    && raw.Near(back.Offset, 2, out next) && next.Code != ILOpCode.Ldtoken;
                break;
            }
            if (!stored) continue;
            var block = (Block)result.Parent;
            int at = result.ChildIndex;
            // The decompiler's own variable is a stack slot, typed 'object'.
            if (f.Method == null) continue;
            ILVariable v = f.RegisterVariable(VariableKind.Local, new ArrayType(f.Method.Compilation, ((NewArr)first.Value).Type));
            ILVariable temp = first.Variable;
            var statements = init.Instructions.ToList();
            init.Instructions.Clear();
            ILInstruction created = first.Value;
            first.Value = new Nop();
            result.Value = new LdLoc(v);
            block.Instructions.Insert(at, new StLoc(v, created));
            for (int i = 1; i < statements.Count; i++)
            {
                foreach (LdLoc use in statements[i].Descendants.OfType<LdLoc>().Where(l => l.Variable == temp).ToList())
                    use.ReplaceWith(new LdLoc(v));
                block.Instructions.Insert(at + i, statements[i]);
            }
        }
    }

    private static void Initializers(ILFunction f, RawIL raw)
    {
        ConstantArrayStatements(f, raw);
        HoistArrayArguments(f, raw);
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            var init = store.Value as Block;
            if (init == null || (init.Kind != BlockKind.ObjectInitializer && init.Kind != BlockKind.ArrayInitializer) || !IsStatement(store) || init.Instructions.Count < 2) continue;
            var first = init.Instructions[0] as StLoc;
            var final = init.FinalInstruction as LdLoc;
            // The same for an array: 'T[] x = new T[n]; x[0] = a;' and not 'new T[n] { a }'.
            if (first == null || final == null || final.Variable != first.Variable || !(first.Value is NewObj || first.Value is NewArr)) continue;
            ILVariable v = store.Variable, temp = first.Variable;
            if (v.Kind != VariableKind.Local || v.StoreCount != 1) continue;
            RawIL.Op stored, loaded;
            int end = RawIL.EndOf(first.Value);
            // The 'newarr' of an array initializer is rebuilt by the decompiler, without its range:
            // the store is looked for in front of the first element.
            if (first.Value is NewArr) end = -1;
            if (first.Value is NewArr)
            {
                int start = RawIL.StartOf(init.Instructions[1]);
                RawIL.Op at0, back;
                if (start < 0 || !raw.IsAt(start, out at0)) continue;
                // The nearest 'newarr' before it: its index and its array load have no range either.
                for (int k = 1; k <= 8 && end < 0; k++)
                {
                    if (!raw.Near(start, -k, out back)) break;
                    if (back.Code != ILOpCode.Newarr) continue;
                    if (raw.Near(back.Offset, 1, out at0) && RawIL.IsStLoc(at0.Code)) end = at0.Offset;
                    break;
                }
            }
            if (end < 0) continue;
            if (first.Value is NewArr && raw.IsAt(end, out stored) && stored.Code == ILOpCode.Newarr) end = stored.End;
            if (!(raw.IsAt(end, out stored) && RawIL.IsStLoc(stored.Code)) && !(raw.EndsAt(end, out stored) && RawIL.IsStLoc(stored.Code))) continue;
            if (!raw.Near(stored.Offset, 1, out loaded) || !RawIL.IsLdLoc(loaded.Code) || loaded.Operand != stored.Operand) continue;
            if (v.Index.HasValue && v.Index.Value != stored.Operand) continue;

            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            var statements = init.Instructions.ToList();
            init.Instructions.Clear();
            ILInstruction created = first.Value;
            first.Value = new Nop();
            store.Value = created;
            for (int i = 1; i < statements.Count; i++)
            {
                foreach (LdLoc use in statements[i].Descendants.OfType<LdLoc>().Where(l => l.Variable == temp).ToList())
                    use.ReplaceWith(new LdLoc(v));
                block.Instructions.Insert(at + i, statements[i]);
            }
        }
    }

    // 5. Result of a 'try' returned after it.
    //        T x;  try { x = e; } catch { return d; }  return x;
    //    is printed 'try { return e; } catch { return d; }'. A 'return' inside a protected
    //    block stores its value in the one slot the compiler keeps for that ('stloc r; leave L',
    //    then 'L: ldloc r; ret' once). Here the 'try' and the handler end on two different
    //    slots, each with its own 'ldloc; ret': the first is a variable of the source.
    private static void TryResults(ILFunction f, RawIL raw)
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
            // No other return inside the 'try'.
            if (body.Descendants.OfType<Leave>().Count(l => l.IsLeavingFunction) != 1) continue;
            int end = RawIL.EndOf(result.Value);
            RawIL.Op store, leave, load, ret, load2;
            if (end < 0) continue;
            if (!(raw.IsAt(end, out store) && RawIL.IsStLoc(store.Code)) && !(raw.EndsAt(end, out store) && RawIL.IsStLoc(store.Code))) continue;
            if (!raw.Near(store.Offset, 1, out leave) || (leave.Code != ILOpCode.Leave && leave.Code != ILOpCode.Leave_s)) continue;
            if (!raw.IsAt(leave.Operand, out load) || !RawIL.IsLdLoc(load.Code) || load.Operand != store.Operand) continue;
            if (!raw.Near(load.Offset, 1, out ret) || ret.Code != ILOpCode.Ret) continue;
            // A second 'ldloc; ret' of another slot follows: the handlers return through it.
            if (!raw.Near(load.Offset, 2, out load2) || !RawIL.IsLdLoc(load2.Code) || load2.Operand == store.Operand) continue;
            IType type = raw.LocalType(store.Operand);
            if (type == null || type.Kind == TypeKind.Unknown) continue;
            BlockContainer function = result.TargetContainer;
            // 'lock' / 'using' / 'try finally' around the 'try' that the 'ldloc; ret' is beyond:
            // the result is returned after them.
            int levels = raw.Regions.Count(r => r[4] == 1 && r[0] <= store.Offset && store.Offset < r[1] && r[3] <= load.Offset);
            Block destination = outer;
            var crossed = new List<BlockContainer>();
            bool reachable = true;
            for (int level = 0; level < levels && reachable; level++)
            {
                var inside = destination.Parent as BlockContainer;
                ILInstruction holder = inside != null && inside.Blocks.Count == 1 && inside.Kind == ContainerKind.Normal ? inside.Parent : null;
                bool wraps = (holder is LockInstruction && ((LockInstruction)holder).Body == inside) || (holder is TryFinally && ((TryFinally)holder).TryBlock == inside) || (holder is UsingInstruction && ((UsingInstruction)holder).Body == inside);
                if (!wraps || !IsStatement(holder) || holder.ChildIndex != ((Block)holder.Parent).Instructions.Count - 1) { reachable = false; break; }
                crossed.Add(inside);
                destination = (Block)holder.Parent;
            }
            if (!reachable) continue;
            ILVariable v = f.RegisterVariable(VariableKind.Local, type);
            ILInstruction value = result.Value;
            result.Value = new Nop();
            block.Instructions[block.Instructions.Count - 1] = new StLoc(v, value);
            block.Instructions.Add(new Leave(body));
            foreach (BlockContainer c in crossed) c.EntryPoint.Instructions.Add(new Leave(c));
            destination.Instructions.Add(new Leave(function, new LdLoc(v)));
        }
    }

    private static void Monitors(ILFunction f, RawIL raw)
    {
        var locks = RawIL.Own(f).OfType<LockInstruction>().ToList();
        if (locks.Count == 0) return;
        IMethod enter = null, exit = null;
        IType type = null;
        bool assignedInCall = false;
        // Every lock of the method through one slot: one variable for all of them (named
        // variables never share a slot; the old compiler shared this temporary).
        int slot = -1;
        bool oneSlot = true;
        for (int i = 0; i < raw.Ops.Count; i++)
        {
            if (!RawIL.IsCall(raw.Ops[i].Code)) continue;
            IMethod m = raw.Method(raw.Ops[i]);
            if (m == null || m.DeclaringType.FullName != "System.Threading.Monitor") continue;
            if (m.Name == "Exit") exit = m;
            if (m.Name != "Enter") continue;
            if (m.Parameters.Count != 1 || i < 2) return;
            // 'ldloc v; call Enter' after a store, or 'dup; stloc v; call Enter'.
            RawIL.Op before = raw.Ops[i - 1];
            IType t;
            bool inCall;
            if (RawIL.IsLdLoc(before.Code)) { t = raw.LocalType(before.Operand); inCall = false; }
            else if (RawIL.IsStLoc(before.Code) && raw.Ops[i - 2].Code == ILOpCode.Dup) { t = raw.LocalType(before.Operand); inCall = true; }
            else return;
            if (t == null || (enter != null && (!t.Equals(type) || inCall != assignedInCall))) return;
            if (slot >= 0 && slot != before.Operand) oneSlot = false;
            slot = before.Operand;
            enter = m; type = t; assignedInCall = inCall;
        }
        if (enter == null || exit == null) return;
        ILVariable shared = null;
        foreach (LockInstruction l in locks)
        {
            if (!IsStatement(l)) continue;
            var block = (Block)l.Parent;
            int at = l.ChildIndex;
            ILVariable v = oneSlot && shared != null ? shared : f.RegisterVariable(VariableKind.Local, type);
            shared = v;
            ILInstruction on = l.OnExpression, body = l.Body;
            l.OnExpression = new Nop();
            l.Body = new Nop();
            var handler = new BlockContainer();
            var handlerBlock = new Block();
            var release = new Call(exit);
            release.Arguments.Add(new LdLoc(v));
            handlerBlock.Instructions.Add(release);
            handlerBlock.Instructions.Add(new Leave(handler));
            handler.Blocks.Add(handlerBlock);
            var acquire = new Call(enter);
            block.Instructions[at] = new TryFinally(body, handler);
            if (assignedInCall)
            {
                acquire.Arguments.Add(new StLoc(v, on));
                block.Instructions.Insert(at, acquire);
            }
            else
            {
                acquire.Arguments.Add(new LdLoc(v));
                block.Instructions.Insert(at, acquire);
                block.Instructions.Insert(at, new StLoc(v, on));
            }
        }
    }

    private static void ReturnLocals(ILFunction f, RawIL raw)
    {
        foreach (Leave leave in RawIL.Own(f).OfType<Leave>().ToList())
        {
            if (!leave.IsLeavingFunction || leave.Value.MatchNop() || leave.Value is LdLoc || !IsStatement(leave)) continue;
            int ret = -1;
            RawIL.Op op, load, store;
            foreach (var r in leave.ILRanges)
                if (raw.IsAt(r.Start, out op) && op.Code == ILOpCode.Ret) ret = r.Start;
            if (ret < 0 || !raw.Near(ret, -1, out load) || !RawIL.IsLdLoc(load.Code)) continue;
            if (!raw.Near(ret, -2, out store) || !RawIL.IsStLoc(store.Code) || store.Operand != load.Operand) continue;
            if (raw.Targets.Contains(load.Offset) || raw.Targets.Contains(ret)) continue;
            int valueEnd = RawIL.EndOf(leave.Value);
            if (valueEnd != store.Offset && valueEnd != store.End) continue;
            var candidates = f.Variables.Where(x => x.Kind == VariableKind.Local && x.Index == store.Operand).ToList();
            // A slot the decompiler split in several variables: the one the value reads.
            if (candidates.Count > 1) candidates = candidates.Where(x => leave.Value.Descendants.OfType<LdLoc>().Any(l => l.Variable == x)).ToList();
            if (candidates.Count != 1) continue;
            ILVariable v = candidates[0];
            var block = (Block)leave.Parent;
            ILInstruction value = leave.Value;
            leave.Value = new LdLoc(v);
            block.Instructions.Insert(leave.ChildIndex, new StLoc(v, value));
        }
    }
}
