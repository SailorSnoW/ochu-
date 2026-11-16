// IL transform: 'callvirt' on a method that is not virtual tells what the receiver was.
//
// The compiler calls a non-virtual instance method with 'callvirt' for the null check, and
// with 'call' when it knows the receiver is not null. Two shapes the decompiler prefers
// compile to 'call':
//
//     x?.M();              the source had   if (x != null) x.M();
//     new T().M();         the source had   T t = new T(); t.M();
//
// (in both, the compiler keeps the single-use local on the stack, so the rest of the IL is
// the same). When the IL has 'callvirt' there, the second form is restored.
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreCallVirt : IILTransform
{
    public void Run(ILFunction function, ILTransformContext context)
    {
        var functions = function.Descendants.OfType<ILFunction>().ToList();
        functions.Add(function);
        foreach (ILFunction f in functions)
        {
            if (f.Method == null || !Program.Enabled("RestoreCallVirt", f.Method.MetadataToken)) continue;
            foreach (Call direct in Own(f).OfType<Call>().ToList()) StructReceiver(f, direct, context);
            foreach (CallVirt call in Own(f).OfType<CallVirt>().ToList())
            {
                if (!call.Method.IsStatic && call.Method.IsOverridable && call.Arguments.Count > 0 && call.Parent != null) LateFallback(call);
                if (!call.Method.IsStatic && call.Method.IsOverridable && call.Arguments.Count > 0 && call.Parent != null && call.ConstrainedTo == null) VirtualNullCheck(f, call, context);
                if (call.Method.IsStatic || call.Method.IsOverridable || call.Arguments.Count == 0) continue;
                if (call.Method.DeclaringType.IsReferenceType != true || call.Method.DeclaringType.Kind == TypeKind.Interface) continue;
                if (call.ConstrainedTo != null || call.Parent == null) continue;
                ILInstruction receiver = call.Arguments[0];
                if (receiver is NewObj) NewReceiver(f, call, (NewObj)receiver);
                else if (receiver is NullableUnwrap) { StoredReceiver(f, call, (NullableUnwrap)receiver, context); NullCheck(call, (NullableUnwrap)receiver); ChainedNullValue(call, receiver as NullableUnwrap); }
            }
            RawIL raw = RawIL.Read(f, context);
            if (raw != null) ValueReceivers(f, raw);
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

    // nullable.rewrap(callvirt M(nullable.unwrap(ldloc x), ...)) as a statement
    //   -> if (x != null) { callvirt M(ldloc x, ...) }
    private static void NullCheck(CallVirt call, NullableUnwrap unwrap)
    {
        var rewrap = call.Parent as NullableRewrap;
        var load = unwrap.Argument as LdLoc;
        if (rewrap != null && load != null && Fallback(call, unwrap, rewrap, load)) return;
        if (rewrap != null && load != null && NullValue(call, unwrap, rewrap, load)) return;
        if (rewrap == null || load == null || !(rewrap.Parent is Block) || ((Block)rewrap.Parent).Kind != BlockKind.ControlFlow) return;
        if (rewrap.SlotInfo != Block.InstructionSlot) return;
        // No other unwrap under this rewrap: 'a?.B(c?.D)' is left alone.
        if (rewrap.Descendants.OfType<NullableUnwrap>().Count() != 1) return;
        ILVariable v = load.Variable;
        var block = new Block();
        var check = new IfInstruction(new Comp(ComparisonKind.Inequality, Sign.None, new LdLoc(v), new LdNull()), block);
        rewrap.ReplaceWith(check);
        unwrap.ReplaceWith(new LdLoc(v));
        block.Instructions.Add(call);
    }

    // 'v?.M();' with M virtual, v a local read only there: the compiler keeps v on the stack
    // ('dup; brtrue; pop'). 'ldloc v; brfalse E; ldloc v; callvirt M' reads the slot twice:
    // 'if (v != null) v.M();'.
    private static void VirtualNullCheck(ILFunction f, CallVirt call, ILTransformContext context)
    {
        var unwrap = call.Arguments[0] as NullableUnwrap;
        var rewrap = call.Parent as NullableRewrap;
        if (unwrap == null || rewrap == null) return;
        var load = unwrap.Argument as LdLoc;
        if (load == null || load.Variable.Kind != VariableKind.Local || !load.Variable.Index.HasValue) return;
        if (!(rewrap.Parent is Block) || ((Block)rewrap.Parent).Kind != BlockKind.ControlFlow || rewrap.SlotInfo != Block.InstructionSlot) return;
        if (rewrap.Descendants.OfType<NullableUnwrap>().Count() != 1) return;
        RawIL raw = RawIL.Read(f, context);
        if (raw == null) return;
        int slot = load.Variable.Index.Value;
        bool twice = false;
        foreach (var r in load.ILRanges.Concat(unwrap.ILRanges).Concat(rewrap.ILRanges))
        {
            RawIL.Op first, jump, again;
            if (r.Start == r.End || !raw.IsAt(r.Start, out first) || !RawIL.IsLdLoc(first.Code) || first.Operand != slot) continue;
            foreach (int back in new[] { 0, -2 })
            {
                if (!raw.Near(r.Start, back, out first) || !RawIL.IsLdLoc(first.Code) || first.Operand != slot) continue;
                if (!raw.Near(r.Start, back + 1, out jump) || (jump.Code != System.Reflection.Metadata.ILOpCode.Brfalse && jump.Code != System.Reflection.Metadata.ILOpCode.Brfalse_s)) continue;
                if (!raw.Near(r.Start, back + 2, out again) || !RawIL.IsLdLoc(again.Code) || again.Operand != slot) continue;
                twice = true;
            }
        }
        if (!twice) return;
        ILVariable v = load.Variable;
        var block = new Block();
        var check = new IfInstruction(new Comp(ComparisonKind.Inequality, Sign.None, new LdLoc(v), new LdNull()), block);
        rewrap.ReplaceWith(check);
        unwrap.ReplaceWith(new LdLoc(v));
        block.Instructions.Add(call);
    }

    // nullable.rewrap(callvirt M(nullable.unwrap(ldloc x))) ?? fallback
    //   -> x != null ? callvirt M(ldloc x) : fallback
    private static bool Fallback(CallVirt call, NullableUnwrap unwrap, NullableRewrap rewrap, LdLoc load)
    {
        var coalesce = rewrap.Parent as NullCoalescingInstruction;
        if (coalesce == null || coalesce.ValueInst != rewrap) return false;
        if (rewrap.Descendants.OfType<NullableUnwrap>().Count() != 1) return false;
        ILVariable v = load.Variable;
        ILInstruction fallback = coalesce.FallbackInst;
        coalesce.FallbackInst = new Nop();
        rewrap.Argument = new Nop();
        unwrap.ReplaceWith(new LdLoc(v));
        coalesce.ReplaceWith(new IfInstruction(new Comp(ComparisonKind.Inequality, Sign.None, new LdLoc(v), new LdNull()), call, fallback));
        return true;
    }

    // The same with more calls on the result: 'x?.M().N(a)' -> 'x != null ? x.M().N(a) : null'.
    private static void ChainedNullValue(CallVirt call, NullableUnwrap unwrap)
    {
        if (unwrap == null || unwrap.Parent != call || call.Parent == null || call.Parent is NullableRewrap) return;
        var load = unwrap.Argument as LdLoc;
        if (load == null) return;
        ILInstruction up = call;
        while (up.Parent != null && !(up.Parent is NullableRewrap))
        {
            // What x is the first operand of: nothing is evaluated before it.
            if (!(up.Parent is CallInstruction) || up.ChildIndex != 0) return;
            up = up.Parent;
        }
        var rewrap = up.Parent as NullableRewrap;
        if (rewrap == null || rewrap.Parent == null || rewrap.Parent is NullCoalescingInstruction || rewrap.Argument != up || rewrap.Argument.ResultType != StackType.O) return;
        if (rewrap.Parent is Block && rewrap.SlotInfo == Block.InstructionSlot) return;
        if (rewrap.Descendants.OfType<NullableUnwrap>().Count() != 1) return;
        ILVariable v = load.Variable;
        ILInstruction whole = rewrap.Argument;
        rewrap.Argument = new Nop();
        unwrap.ReplaceWith(new LdLoc(v));
        rewrap.ReplaceWith(new IfInstruction(new Comp(ComparisonKind.Inequality, Sign.None, new LdLoc(v), new LdNull()), whole, new LdNull()));
    }

    // nullable.rewrap(callvirt M(nullable.unwrap(ldloc x))) read as a value, M returning a reference
    //   -> x != null ? callvirt M(ldloc x) : null
    private static bool NullValue(CallVirt call, NullableUnwrap unwrap, NullableRewrap rewrap, LdLoc load)
    {
        if (rewrap.Argument != call || rewrap.Parent == null || rewrap.Parent is NullCoalescingInstruction) return false;
        if (rewrap.Parent is Block && rewrap.SlotInfo == Block.InstructionSlot) return false;
        if (call.Method.ReturnType.IsReferenceType != true) return false;
        if (rewrap.Descendants.OfType<NullableUnwrap>().Count() != 1) return false;
        ILVariable v = load.Variable;
        rewrap.Argument = new Nop();
        unwrap.ReplaceWith(new LdLoc(v));
        rewrap.ReplaceWith(new IfInstruction(new Comp(ComparisonKind.Inequality, Sign.None, new LdLoc(v), new LdNull()), call, new LdNull()));
        return true;
    }

    // 'T t = new T(a); t.M();' on a struct builds t in place ('ldloca t; a; call T::.ctor').
    // The decompiler prints 'new T(a).M()', which builds the value on the stack and stores it
    // ('newobj; stloc; ldloca').
    private static void ValueReceivers(ILFunction f, RawIL raw)
    {
        foreach (AddressOf address in Own(f).OfType<AddressOf>().ToList())
        {
            // 'T? v = null; v.M()' clears the variable and reads its address again ('ldloca v;
            // initobj T; ldloca v; call M'); 'default(T?).M()' keeps the address ('ldloca t; dup;
            // initobj T; call M').
            var cleared = address.Value as DefaultValue;
            if (cleared != null && address.Parent is Call && address.ChildIndex == 0 && cleared.Type.IsReferenceType == false)
            {
                bool again = false;
                foreach (var r in cleared.ILRanges.Concat(address.ILRanges))
                {
                    RawIL.Op init, before, after;
                    if (r.Start == r.End) continue;
                    for (int at = r.Start; at < r.End && !again; at++)
                    {
                        if (!raw.IsAt(at, out init) || init.Code != System.Reflection.Metadata.ILOpCode.Initobj) continue;
                        if (raw.Near(at, -1, out before) && IsLdLoca(before) && raw.Near(at, 1, out after) && IsLdLoca(after) && after.Operand == before.Operand) again = true;
                    }
                }
                ILInstruction holder = address;
                bool leading = again;
                while (leading && !(holder.Parent is Block && holder.SlotInfo == Block.InstructionSlot))
                {
                    ILInstruction parent = holder.Parent;
                    if (parent == null || holder.ChildIndex != 0 || !(parent is CallInstruction || parent is StLoc || parent is Leave || parent is Comp || parent is IfInstruction
                        || parent is BinaryNumericInstruction || parent is Conv || parent is StObj || parent is LdObj || parent is LdFlda || parent is AddressOf)) { leading = false; break; }
                    holder = parent;
                }
                if (leading && ((Block)holder.Parent).Kind == BlockKind.ControlFlow)
                {
                    ILVariable slot = f.RegisterVariable(VariableKind.Local, cleared.Type);
                    address.Value = new Nop();
                    address.ReplaceWith(new LdLoca(slot));
                    ((Block)holder.Parent).Instructions.Insert(holder.ChildIndex, new StLoc(slot, cleared));
                }
                continue;
            }
            var created = address.Value as NewObj;
            if (created == null || address.Parent == null || created.Method.DeclaringType.IsReferenceType != false) continue;
            if (!(address.Parent is Call) || address.ChildIndex != 0) continue;
            int end = RawIL.EndOf(created);
            RawIL.Op ctor;
            // The range of the 'newobj' the decompiler rebuilt starts at the 'call' of the
            // constructor (and may go on over the 'ldloca' that follows).
            bool inPlace = false;
            ctor = default(RawIL.Op);
            foreach (var r in created.ILRanges)
                if (r.Start != r.End && raw.IsAt(r.Start, out ctor) && ctor.Code == System.Reflection.Metadata.ILOpCode.Call) { inPlace = true; break; }
            if (end < 0 || !inPlace) continue;
            IMethod built = raw.Method(ctor);
            if (built == null || !built.IsConstructor) continue;
            ILInstruction statement = address;
            bool first = true;
            while (!(statement.Parent is Block && statement.SlotInfo == Block.InstructionSlot))
            {
                ILInstruction parent = statement.Parent;
                if (parent == null || statement.ChildIndex != 0 || !(parent is CallInstruction || parent is StLoc || parent is Leave || parent is Comp || parent is IfInstruction
                    || parent is BinaryNumericInstruction || parent is Conv || parent is StObj || parent is LdObj || parent is LdFlda || parent is AddressOf)) { first = false; break; }
                statement = parent;
            }
            if (!first) continue;
            var block = (Block)statement.Parent;
            if (block.Kind != BlockKind.ControlFlow) continue;
            ILVariable v = f.RegisterVariable(VariableKind.Local, created.Method.DeclaringType);
            address.Value = new Nop();
            address.ReplaceWith(new LdLoca(v));
            block.Instructions.Insert(statement.ChildIndex, new StLoc(v, created));
        }
    }

    // 'x?.M() ?? d' puts d first in the IL ('x; brtrue A; d; br E; A: x; callvirt M'). With the
    // call first, the source was 'x == null ? d : x.M()' (or two returns), whatever M is.
    private static void LateFallback(CallVirt call)
    {
        var unwrap = call.Arguments[0] as NullableUnwrap;
        var rewrap = call.Parent as NullableRewrap;
        if (unwrap == null || rewrap == null) return;
        var load = unwrap.Argument as LdLoc;
        var coalesce = rewrap.Parent as NullCoalescingInstruction;
        if (load == null || coalesce == null || coalesce.ValueInst != rewrap) return;
        // A value: with a reference, the test of the result ('dup; brtrue; pop; d') comes last too.
        if (call.Method.ReturnType.IsReferenceType != false) return;
        int access = RawIL.EndOf(call), other = RawIL.StartOf(coalesce.FallbackInst);
        if (access < 0 || other < 0 || other < access) return;
        Fallback(call, unwrap, rewrap, load);
    }

    // 'e?.M()' with e computed: 'T v = e; if (v != null) v.M();' when the IL stores e and reads
    // it back for the test ('stloc v; ldloc v; brtrue'; '?.' keeps it on the stack with 'dup').
    private static void StoredReceiver(ILFunction f, CallVirt call, NullableUnwrap unwrap, ILTransformContext context)
    {
        if (unwrap.Argument is LdLoc || !(call.Parent is NullableRewrap)) return;
        var rewrap = (NullableRewrap)call.Parent;
        if (rewrap.Descendants.OfType<NullableUnwrap>().Count() != 1) return;
        RawIL raw = RawIL.Read(f, context);
        int end = RawIL.EndOf(unwrap.Argument);
        if (raw == null || end < 0) return;
        RawIL.Op store, load, jump;
        if (!raw.IsAt(end, out store) || !RawIL.IsStLoc(store.Code))
        {
            if (!raw.EndsAt(end, out store) || !RawIL.IsStLoc(store.Code)) return;
        }
        if (!raw.Near(store.Offset, 1, out load) || !RawIL.IsLdLoc(load.Code) || load.Operand != store.Operand) return;
        if (!raw.Near(store.Offset, 2, out jump) || (jump.Code != System.Reflection.Metadata.ILOpCode.Brtrue && jump.Code != System.Reflection.Metadata.ILOpCode.Brtrue_s
            && jump.Code != System.Reflection.Metadata.ILOpCode.Brfalse && jump.Code != System.Reflection.Metadata.ILOpCode.Brfalse_s)) return;
        IType type = raw.LocalType(store.Operand);
        if (type == null || type.IsReferenceType != true) return;
        // The value is the first thing its statement evaluates.
        ILInstruction statement = rewrap;
        while (!(statement.Parent is Block && statement.SlotInfo == Block.InstructionSlot))
        {
            ILInstruction parent = statement.Parent;
            if (parent == null || statement.ChildIndex != 0) return;
            if (!(parent is StLoc || parent is Leave || parent is NullCoalescingInstruction)) return;
            statement = parent;
        }
        var block = (Block)statement.Parent;
        if (block.Kind != BlockKind.ControlFlow) return;
        ILVariable v = f.RegisterVariable(VariableKind.Local, type);
        ILInstruction value = unwrap.Argument;
        unwrap.Argument = new LdLoc(v);
        block.Instructions.Insert(statement.ChildIndex, new StLoc(v, value));
        if (statement != rewrap) return;
        // As a statement: 'if (v != null) v.M();', the receiver at the place the IL reads it
        // again (after an early 'ret' when the jump is a 'brtrue').
        bool onTrue = jump.Code == System.Reflection.Metadata.ILOpCode.Brtrue || jump.Code == System.Reflection.Metadata.ILOpCode.Brtrue_s;
        RawIL.Op again;
        if (!(onTrue ? raw.IsAt(jump.Operand, out again) : raw.Near(jump.Offset, 1, out again)) || !RawIL.IsLdLoc(again.Code) || again.Operand != store.Operand) return;
        var receiver = new LdLoc(v);
        receiver.AddILRange(new ICSharpCode.Decompiler.Util.Interval(again.Offset, again.End));
        RawIL.Op exit;
        var whole = f.Body as BlockContainer;
        if (onTrue && whole != null && f.ReturnType.Kind == TypeKind.Void && raw.Near(jump.Offset, 1, out exit) && exit.Code == System.Reflection.Metadata.ILOpCode.Ret)
        {
            // 'brtrue L; ret; L:': 'if (v == null) return; v.M();'
            var leave = new IfInstruction(new Comp(ComparisonKind.Equality, Sign.None, new LdLoc(v), new LdNull()), new Leave(whole));
            int at = rewrap.ChildIndex;
            unwrap.ReplaceWith(receiver);
            block.Instructions[at] = leave;
            block.Instructions.Insert(at + 1, call);
            return;
        }
        var body = new Block();
        var check = new IfInstruction(new Comp(ComparisonKind.Inequality, Sign.None, new LdLoc(v), new LdNull()), body);
        rewrap.ReplaceWith(check);
        unwrap.ReplaceWith(receiver);
        body.Instructions.Add(call);
    }
    // 'new S(a).M()' builds the struct on the stack and stores it in a temporary ('newobj;
    // stloc t; ldloca t'). 'S v = new S(a); v.M();' builds it in place: 'ldloca v; a; call
    // .ctor; ldloca v'.
    private static void StructReceiver(ILFunction f, Call call, ILTransformContext context)
    {
        if (call.Method.IsStatic || call.Arguments.Count == 0 || call.Parent == null || call.Method.IsConstructor) return;
        ILInstruction receiver = call.Arguments[0];
        var created = receiver is AddressOf ? ((AddressOf)receiver).Value as NewObj : receiver as NewObj;
        if (created == null || created.Method.DeclaringType.IsReferenceType != false) return;
        RawIL raw = RawIL.Read(f, context);
        int start = RawIL.StartOf(created), end = RawIL.EndOf(created);
        if (raw == null || start < 0 || end < 0) return;
        RawIL.Op ctor, address, again;
        // The range of the rebuilt 'newobj' ends at its last argument, at the call, or at the
        // 'ldloca' that reads the struct back.
        bool found = (raw.IsAt(end, out ctor) && ctor.Code == System.Reflection.Metadata.ILOpCode.Call) || (raw.EndsAt(end, out ctor) && ctor.Code == System.Reflection.Metadata.ILOpCode.Call);
        if (!found && raw.EndsAt(end, out ctor) && IsLdLoca(ctor)) found = raw.Near(ctor.Offset, -1, out ctor) && ctor.Code == System.Reflection.Metadata.ILOpCode.Call;
        if (!found) return;
        IMethod built = raw.Method(ctor);
        if (built == null || !built.IsConstructor) return;
        if (!(raw.IsAt(start, out address) && IsLdLoca(address)) && !(raw.Near(start, -1, out address) && IsLdLoca(address))) return;
        if (!raw.Near(ctor.Offset, 1, out again) || !IsLdLoca(again) || again.Operand != address.Operand) return;
        ILInstruction statement = call;
        while (!(statement.Parent is Block && statement.SlotInfo == Block.InstructionSlot))
        {
            ILInstruction parent = statement.Parent;
            if (parent == null || statement.ChildIndex != 0) return;
            if (!(parent is CallInstruction || parent is StLoc || parent is Leave || parent is Comp || parent is IfInstruction
                || parent is BinaryNumericInstruction || parent is Conv || parent is StObj || parent is LdObj || parent is LdFlda)) return;
            statement = parent;
        }
        var block = (Block)statement.Parent;
        if (block.Kind != BlockKind.ControlFlow) return;
        ILVariable v = f.RegisterVariable(VariableKind.Local, created.Method.DeclaringType);
        receiver.ReplaceWith(new LdLoca(v));
        if (created.Parent != null) created.ReplaceWith(new Nop());
        block.Instructions.Insert(statement.ChildIndex, new StLoc(v, created));
    }

    private static bool IsLdLoca(RawIL.Op op)
    {
        return op.Code == System.Reflection.Metadata.ILOpCode.Ldloca || op.Code == System.Reflection.Metadata.ILOpCode.Ldloca_s;
    }
    // callvirt M(newobj T(...), ...) evaluated first in its statement
    //   -> stloc t(newobj T(...)); callvirt M(ldloc t, ...)
    private static void NewReceiver(ILFunction f, CallVirt call, NewObj created)
    {
        ILInstruction statement = call;
        while (!(statement.Parent is Block && statement.SlotInfo == Block.InstructionSlot))
        {
            ILInstruction parent = statement.Parent;
            if (parent == null || statement.ChildIndex != 0) return;
            if (!(parent is CallInstruction || parent is StLoc || parent is Leave || parent is Comp || parent is IfInstruction
                || parent is BinaryNumericInstruction || parent is Conv || parent is StObj || parent is LdObj || parent is LdFlda)) return;
            statement = parent;
        }
        var block = (Block)statement.Parent;
        if (block.Kind != BlockKind.ControlFlow) return;
        ILVariable v = f.RegisterVariable(VariableKind.Local, created.Method.DeclaringType);
        created.ReplaceWith(new LdLoc(v));
        block.Instructions.Insert(statement.ChildIndex, new StLoc(v, created));
    }
}
