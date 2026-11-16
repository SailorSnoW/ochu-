// IL transform: expressions the decompiler prints in a form that compiles to other
// instructions. Each is decided by the raw method body.
//
// 1. Assignment in both branches.
//        if (c) x = a; else x = b;             IL: c; brfalse E; a; stloc x; br END; E: b; stloc x
//    is printed 'x = c ? a : b;', which stores once, after the two values meet.
//
// 2. Unsigned comparison with zero.
//        if (u > 0)                            IL: ldc.i4.0; bgt.un / ble.un
//    is printed 'u != 0', which compiles to brtrue / brfalse.
//
// 3. Assignment used as a value, through a property.
//        a.P = (b.P = v);                      IL: a; b; v; dup; stloc t; call set_P; ldloc t; call set_P
//    is printed 'T t = (b.P = v); a.P = t;' after 'A a2 = a;', because 'a' is read first.
//
// 4. Condition kept in a variable.
//        bool b = x != null;  if (b) ...       IL: x; ldnull; cgt.un; brfalse
//    is printed 'if (x != null)', which compiles to brfalse on x itself. A comparison that
//    leaves its result on the stack right before brtrue / brfalse was a variable read once.
//
// 5. Conversion to the same floating-point type: '(float)f' is a 'conv.r4' that the
//    decompiler drops. Marked here, printed by ExplicitFloatCasts.
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreExpressions : IILTransform
{
    // Argument of a 'conv.r4' on a float (or 'conv.r8' on a double) -> "float" / "double".
    internal static readonly ConditionalWeakTable<ILInstruction, string> FloatCasts = new ConditionalWeakTable<ILInstruction, string>();

    // 'x.f = x.f + v' written out by ExplicitCompound: the decompiler folds it back into
    // 'x.f += v' when it prints. RestoreSyntax unfolds the marked ones.
    internal static readonly ConditionalWeakTable<ILInstruction, string> Explicit = new ConditionalWeakTable<ILInstruction, string>();

    public void Run(ILFunction function, ILTransformContext context)
    {
        var functions = function.Descendants.OfType<ILFunction>().ToList();
        functions.Add(function);
        foreach (ILFunction f in functions)
        {
            if (f.Method == null) continue;
            RawIL raw = RawIL.Read(f, context);
            if (raw == null) continue;
            if (Program.Enabled("RestoreIfElseAssign", f.Method.MetadataToken)) IfElseAssignments(f, raw);
            if (Program.Enabled("RestoreConditional", f.Method.MetadataToken)) InterfaceConditionals(f);
            if (Program.Enabled("RestoreUnsignedZero", f.Method.MetadataToken)) { UnsignedZero(f, raw, context); UnsignedTruth(f, raw); }
            if (Program.Enabled("RestoreInlineAssign", f.Method.MetadataToken)) for (int round = 0; round < 3; round++) InlineAssignments(f);   // a chain is folded from its end
            if (Program.Enabled("RestoreConditionLocal", f.Method.MetadataToken)) { ConditionLocals(f, raw, context); ShortCircuitChoices(f, raw, context); }
            if (Program.Enabled("RestoreFloatCast", f.Method.MetadataToken)) MarkFloatCasts(f, raw);
            if (Program.Enabled("RestoreSplitReturn", f.Method.MetadataToken)) SplitReturns(f, raw);
            if (Program.Enabled("RestoreExplicitCompound", f.Method.MetadataToken)) ExplicitCompound(f, raw);
            if (Program.Enabled("RestoreDelegateCache", f.Method.MetadataToken)) DelegateCaches(f, raw, context);
        }
    }

    private static bool IsStatement(ILInstruction inst)
    {
        var block = inst.Parent as Block;
        return block != null && block.Kind == BlockKind.ControlFlow && inst.SlotInfo == Block.InstructionSlot;
    }

    // The raw instruction at 'offset', or the one that ends there: the range of an expression
    // may or may not include the instruction that consumed it.
    private static bool AtOrBefore(RawIL raw, int offset, System.Func<ILOpCode, bool> test, out RawIL.Op op)
    {
        if (raw.IsAt(offset, out op) && test(op.Code)) return true;
        if (raw.EndsAt(offset, out op) && test(op.Code)) return true;
        return false;
    }

    private static ILInstruction Negate(ILInstruction condition)
    {
        ILInstruction positive;
        if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); return positive; }
        return Comp.LogicNot(condition);
    }

    // 'F(c ? x.M() : (I)o)', M returning an interface derived from I: the compiler passes the
    // value of that branch through a temporary of type I ('callvirt M; stloc t; ldloc t'), so
    // that both branches leave the same type on the stack. The decompiler keeps the temporary
    // and writes the branches as statements, before the statement that reads the value.
    private static bool SlotValue(ILInstruction branch, out ILVariable slot, out StLoc value, out bool through)
    {
        slot = null; value = null; through = false;
        var block = branch as Block;
        if (block == null || !block.FinalInstruction.MatchNop() || block.Instructions.Count < 1 || block.Instructions.Count > 2) return false;
        var last = block.Instructions[block.Instructions.Count - 1] as StLoc;
        if (last == null || last.Variable.Kind != VariableKind.StackSlot) return false;
        slot = last.Variable;
        value = last;
        if (block.Instructions.Count == 1) return true;
        var first = block.Instructions[0] as StLoc;
        var load = last.Value as LdLoc;
        if (first == null || load == null || load.Variable != first.Variable) return false;
        ILVariable t = first.Variable;
        if (t.Kind != VariableKind.Local || t.StoreCount != 1 || t.LoadCount != 1 || t.AddressCount != 0) return false;
        value = first;
        through = true;
        return true;
    }

    private static bool IsPureLoad(ILInstruction inst)
    {
        if (inst is LdLoc || inst is LdcI4 || inst is LdNull || inst is LdStr || inst is LdsFlda) return true;
        var field = inst as LdFlda;
        return field != null && IsPureLoad(field.Target);
    }

    private static void InterfaceConditionals(ILFunction f)
    {
        foreach (IfInstruction choice in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (choice.Parent == null || !IsStatement(choice)) continue;
            ILVariable s1, s2;
            StLoc a, b;
            bool t1, t2;
            if (!SlotValue(choice.TrueInst, out s1, out a, out t1) || !SlotValue(choice.FalseInst, out s2, out b, out t2)) continue;
            if (s1 != s2 || (!t1 && !t2) || s1.StoreCount != 2 || s1.LoadCount != 1 || s1.AddressCount != 0) continue;
            var block = (Block)choice.Parent;
            int at = choice.ChildIndex;
            if (at + 1 >= block.Instructions.Count) continue;
            ILInstruction next = block.Instructions[at + 1];
            LdLoc read = next.Descendants.OfType<LdLoc>().FirstOrDefault(l => l.Variable == s1);
            if (read == null) continue;
            // Nothing but plain loads is evaluated before the value in its statement.
            bool first = true;
            for (ILInstruction i = read; i != next && first; i = i.Parent)
            {
                ILInstruction p = i.Parent;
                if (!(p is CallInstruction || p is StObj || p is StLoc || p is Leave || p is LdFlda || p is CastClass || p is Throw)) first = false;
                for (int k = 0; k < i.ChildIndex && first; k++)
                    if (!IsPureLoad(p.Children[k])) first = false;
            }
            if (!first) continue;
            ILInstruction condition = choice.Condition, yes = a.Value, no = b.Value;
            choice.Condition = new Nop();
            a.Value = new Nop();
            b.Value = new Nop();
            read.ReplaceWith(new IfInstruction(condition, yes, no));
            block.Instructions.RemoveAt(at);
        }
    }

    // The 'call T::.ctor' of a struct built in place: the range of the 'newobj' the decompiler
    // made of it starts there.
    private static bool BuiltInPlace(RawIL raw, ILInstruction created, out RawIL.Op ctor)
    {
        ctor = default(RawIL.Op);
        foreach (var r in created.ILRanges)
        {
            if (r.Start == r.End || !raw.IsAt(r.Start, out ctor) || ctor.Code != ILOpCode.Call) continue;
            IMethod made = raw.Method(ctor);
            if (made != null && made.IsConstructor) return true;
        }
        // Without a range of its own: the 'call' that follows its last argument.
        int end = RawIL.EndOf(created);
        if (created.ILRanges.All(r => r.Start == r.End) && end >= 0 && raw.IsAt(end, out ctor) && ctor.Code == ILOpCode.Call)
        {
            IMethod made = raw.Method(ctor);
            if (made != null && made.IsConstructor && made.DeclaringType.IsReferenceType == false) return true;
        }
        return false;
    }

    // A parameter is assigned with 'starg'.
    private static bool IsStLocOrArg(ILOpCode c)
    {
        return RawIL.IsStLoc(c) || c == ILOpCode.Starg || c == ILOpCode.Starg_s;
    }

    private static void IfElseAssignments(ILFunction f, RawIL raw)
    {
        // Again while something changes: 'x = a ? p : b ? q : r' is an 'else if' chain.
        for (int round = 0; round < 16 && IfElseAssignmentsOnce(f, raw); round++) { }
    }

    // The end of a value; an array initializer keeps the ranges of its elements only, its
    // last 'stelem' has none.
    private static int EndOfValue(RawIL raw, ILInstruction value)
    {
        int end = RawIL.EndOf(value);
        var init = value as Block;
        RawIL.Op store;
        if (end >= 0 && init != null && init.Kind == BlockKind.ArrayInitializer && raw.IsAt(end, out store)
            && (store.Code == ILOpCode.Stelem || (store.Code >= ILOpCode.Stelem_i && store.Code <= ILOpCode.Stelem_ref))) return store.End;
        return end;
    }

    private static bool StoredInBothBranches(RawIL raw, ILInstruction first, ILInstruction second)
    {
        RawIL.Op s1, s2, jump;
        if (!AtOrBefore(raw, EndOfValue(raw, first), IsStLocOrArg, out s1)) return false;
        if (!raw.Near(s1.Offset, 1, out jump) || (jump.Code != ILOpCode.Br && jump.Code != ILOpCode.Br_s)) return false;
        if (!AtOrBefore(raw, EndOfValue(raw, second), IsStLocOrArg, out s2) || s2.Operand != s1.Operand || s2.Offset == s1.Offset || RawIL.IsStLoc(s1.Code) != RawIL.IsStLoc(s2.Code)) return false;
        // To the end of the 'else', or beyond it when this 'if' ends an outer branch.
        return jump.Operand >= s2.End;
    }

    // A struct built in place in each branch: 'ldloca x; args; call .ctor; br' and again
    // 'ldloca x; args; call .ctor'. 'x = c ? new S(a) : new S(b)' builds a value ('newobj')
    // and stores it once.
    private static bool ConstructedInBothBranches(RawIL raw, ILInstruction first, ILInstruction second)
    {
        RawIL.Op c1, c2, jump;
        int x1, x2;
        if (!InPlace(raw, first, out c1, out x1) || !InPlace(raw, second, out c2, out x2) || x1 != x2 || c1.Offset == c2.Offset) return false;
        if (!raw.Near(c1.Offset, 1, out jump) || (jump.Code != ILOpCode.Br && jump.Code != ILOpCode.Br_s)) return false;
        return jump.Operand >= c2.End;
    }

    // One branch stores a value ('stloc x'), the other builds the struct in place.
    private static bool MixedStores(RawIL raw, ILInstruction first, ILInstruction second)
    {
        RawIL.Op last1, last2, jump;
        int x1, x2;
        if (!BranchStore(raw, first, out last1, out x1) || !BranchStore(raw, second, out last2, out x2) || x1 != x2 || last1.Offset == last2.Offset) return false;
        if (!raw.Near(last1.Offset, 1, out jump) || (jump.Code != ILOpCode.Br && jump.Code != ILOpCode.Br_s)) return false;
        return jump.Operand >= last2.End;
    }

    private static bool BranchStore(RawIL raw, ILInstruction value, out RawIL.Op last, out int slot)
    {
        if (InPlace(raw, value, out last, out slot)) return true;
        var nested = value as IfInstruction;
        if (nested != null && nested.ResultType != StackType.Void)
        {
            int t = RawIL.StartOf(nested.TrueInst), e = RawIL.StartOf(nested.FalseInst);
            if (t < 0 || e < 0 || t == e) return false;
            value = t > e ? nested.TrueInst : nested.FalseInst;
            if (InPlace(raw, value, out last, out slot)) return true;
        }
        if (!AtOrBefore(raw, RawIL.EndOf(value), RawIL.IsStLoc, out last)) return false;
        slot = last.Operand;
        return true;
    }
    private static bool InPlace(RawIL raw, ILInstruction value, out RawIL.Op call, out int slot)
    {
        slot = -1;
        call = default(RawIL.Op);
        // A nested choice: its last branch is the one the outer jump follows.
        var nested = value as IfInstruction;
        if (nested != null && nested.ResultType != StackType.Void)
        {
            int t = RawIL.StartOf(nested.TrueInst), e = RawIL.StartOf(nested.FalseInst);
            if (t < 0 || e < 0 || t == e) return false;
            return InPlace(raw, t > e ? nested.TrueInst : nested.FalseInst, out call, out slot);
        }
        if (!(value is NewObj)) return false;
        int end = RawIL.EndOf(value), start = RawIL.StartOf(value);
        if (end < 0 || start < 0) return false;
        // The range of the rebuilt 'newobj' stops at its last argument or at the call itself.
        if (!(raw.IsAt(end, out call) && call.Code == ILOpCode.Call) && !(raw.EndsAt(end, out call) && call.Code == ILOpCode.Call)) return false;
        IMethod ctor = raw.Method(call);
        if (ctor == null || !ctor.IsConstructor) return false;
        RawIL.Op address;
        if (raw.IsAt(start, out address) && (address.Code == ILOpCode.Ldloca || address.Code == ILOpCode.Ldloca_s)) { slot = address.Operand; return true; }
        if (raw.Near(start, -1, out address) && (address.Code == ILOpCode.Ldloca || address.Code == ILOpCode.Ldloca_s)) { slot = address.Operand; return true; }
        return false;
    }
    // A choice between structs built in place in the same slot, printed inside the expression
    // that reads it: the IL builds it first, in a variable ('T v; if (c) v = new T(a); else
    // v = new T(b); x.M(v);'). It gets its variable back; the rule below does the rest.
    private static bool ExtractConstructedChoices(ILFunction f, RawIL raw)
    {
        bool changed = false;
        foreach (IfInstruction choice in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (choice.ResultType == StackType.Void || choice.Parent == null || choice.Parent is IfInstruction) continue;
            // Kept by the decompiler in a named argument, to hold the order: 'F(c: x ? .. : .., a, b)'.
            var kept = choice.Parent as StLoc;
            var named = kept == null ? null : kept.Parent as Block;
            if (kept != null && (named == null || named.Kind != BlockKind.CallWithNamedArgs || kept.Variable.Kind != VariableKind.NamedArgument
                || named.Instructions.Count != 1 || !(named.FinalInstruction is CallInstruction))) continue;
            int t = RawIL.StartOf(choice.TrueInst), e = RawIL.StartOf(choice.FalseInst);
            if (t < 0 || e < 0 || t == e) continue;
            ILInstruction first = t < e ? choice.TrueInst : choice.FalseInst, second = t < e ? choice.FalseInst : choice.TrueInst;
            if (!ConstructedInBothBranches(raw, first, second)) continue;
            NewObj created = choice.Descendants.OfType<NewObj>().FirstOrDefault();
            if (created == null || created.Method.DeclaringType.IsReferenceType != false) continue;
            ILInstruction statement = kept == null ? (ILInstruction)choice : named;
            while (statement.Parent != null && !(statement.Parent is Block && statement.SlotInfo == Block.InstructionSlot)) statement = statement.Parent;
            var block = statement.Parent as Block;
            if (block == null || block.Kind != BlockKind.ControlFlow || statement is IfInstruction) continue;
            // Everything else in the statement comes after the choice in the IL.
            int end = RawIL.EndOf(choice);
            bool later = true;
            foreach (ILInstruction other in statement.Descendants)
            {
                if (other == choice || other.IsDescendantOf(choice) || choice.IsDescendantOf(other)) continue;
                foreach (var r in other.ILRanges) if (r.Start != r.End && r.Start < end) later = false;
            }
            if (!later) continue;
            ILVariable v = f.RegisterVariable(VariableKind.Local, created.Method.DeclaringType);
            var holder = new Nop();
            choice.ReplaceWith(holder);
            if (kept == null) holder.ReplaceWith(new LdLoc(v));
            else
            {
                var call = (CallInstruction)named.FinalInstruction;
                foreach (LdLoc use in call.Descendants.OfType<LdLoc>().Where(l => l.Variable == kept.Variable).ToList()) use.ReplaceWith(new LdLoc(v));
                named.Instructions.Clear();
                named.FinalInstruction = new Nop();
                named.ReplaceWith(call);
            }
            block.Instructions.Insert(statement.ChildIndex, new StLoc(v, choice));
            changed = true;
        }
        return changed;
    }

    private static bool IfElseAssignmentsOnce(ILFunction f, RawIL raw)
    {
        bool changed = ExtractConstructedChoices(f, raw);
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            var choice = store.Value as IfInstruction;
            if (choice == null || !IsStatement(store) || store.Variable.Kind == VariableKind.StackSlot) continue;
            if (choice.ResultType == StackType.Void) continue;
            int t = RawIL.StartOf(choice.TrueInst), e = RawIL.StartOf(choice.FalseInst);
            if (t < 0 || e < 0 || t == e) continue;
            bool trueFirst = t < e;
            ILInstruction first = trueFirst ? choice.TrueInst : choice.FalseInst;
            ILInstruction second = trueFirst ? choice.FalseInst : choice.TrueInst;
            if (!StoredInBothBranches(raw, first, second) && !ConstructedInBothBranches(raw, first, second) && !MixedStores(raw, first, second)) continue;

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
            changed = true;
        }
        return changed;
    }

    private static bool IsInteger(IType type)
    {
        ITypeDefinition def = type.GetDefinition();
        if (def == null) return false;
        switch (def.KnownTypeCode)
        {
            case KnownTypeCode.Int32: case KnownTypeCode.UInt32: case KnownTypeCode.Int64: case KnownTypeCode.UInt64:
            case KnownTypeCode.Int16: case KnownTypeCode.UInt16: case KnownTypeCode.Byte: case KnownTypeCode.SByte:
                return true;
        }
        return false;
    }

    private static bool IsIntegerValue(ILInstruction inst)
    {
        if (inst is BinaryNumericInstruction || inst is CompoundAssignmentInstruction) return true;
        var conv = inst as Conv;
        if (conv != null) return conv.TargetType != PrimitiveType.R4 && conv.TargetType != PrimitiveType.R8;
        IType type = null;
        if (inst is LdLoc) type = ((LdLoc)inst).Variable.Type;
        else if (inst is StLoc) type = ((StLoc)inst).Variable.Type;
        else if (inst is LdObj) type = ((LdObj)inst).Type;
        else if (inst is StObj) type = ((StObj)inst).Type;
        else if (inst is CallInstruction) type = ((CallInstruction)inst).Method.ReturnType;
        return type != null && (type.Kind == TypeKind.Enum || IsInteger(type));
    }

    private static bool IsUnsignedCompare(ILOpCode c)
    {
        // Not 'cgt.un': as a value, 'x != 0' compiles to it as well.
        return c == ILOpCode.Bgt_un || c == ILOpCode.Bgt_un_s || c == ILOpCode.Ble_un || c == ILOpCode.Ble_un_s;
    }

    private static void UnsignedZero(ILFunction f, RawIL raw, ILTransformContext context)
    {
        foreach (Comp comp in RawIL.Own(f).OfType<Comp>().ToList())
        {
            if (comp.Kind != ComparisonKind.Equality && comp.Kind != ComparisonKind.Inequality) continue;
            if (comp.InputType != StackType.I4 && comp.InputType != StackType.I8) continue;
            if (comp.LiftingKind != ComparisonLiftingKind.None) continue;
            long zero8;
            if (!comp.Right.MatchLdcI4(0) && !(comp.Right.MatchLdcI8(out zero8) && zero8 == 0)) continue;
            // 'cgt.un' on a bool is how the compiler ends an exception filter.
            if (!IsIntegerValue(comp.Left)) continue;
            // 'ldc.i4.0 [conv]; <unsigned compare>' right after the left operand.
            int end = RawIL.EndOf(comp.Left);
            RawIL.Op zero, next;
            if (end < 0 || !AtOrBefore(raw, end, c => c == ILOpCode.Ldc_i4_0, out zero)) continue;
            if (!raw.Near(zero.Offset, 1, out next)) continue;
            if (next.Code == ILOpCode.Conv_i8 || next.Code == ILOpCode.Conv_u8) { if (!raw.Near(zero.Offset, 2, out next)) continue; }
            if (!IsUnsignedCompare(next.Code)) continue;
            ILInstruction left = comp.Left, right = comp.Right;
            ComparisonKind kind = comp.Kind == ComparisonKind.Inequality ? ComparisonKind.GreaterThan : ComparisonKind.LessThanOrEqual;
            comp.Left = new Nop();
            comp.Right = new Nop();
            var replacement = new Comp(kind, Sign.Unsigned, left, right);
            replacement.AddILRange(comp);
            comp.ReplaceWith(replacement);
        }
    }

    // The operands of a disjunction, each with 'true' when it is to be negated:
    // '!(a && b)' is '!a || !b'.
    private static void Disjuncts(ILInstruction condition, bool negated, List<KeyValuePair<ILInstruction, bool>> list)
    {
        ILInstruction lhs, rhs, inner;
        if (!negated && condition.MatchLogicOr(out lhs, out rhs)) { Disjuncts(lhs, false, list); Disjuncts(rhs, false, list); }
        else if (negated && condition.MatchLogicAnd(out lhs, out rhs)) { Disjuncts(lhs, true, list); Disjuncts(rhs, true, list); }
        else if (condition.MatchLogicNot(out inner) && (negated || inner.MatchLogicAnd(out lhs, out rhs))) Disjuncts(inner, !negated, list);
        else list.Add(new KeyValuePair<ILInstruction, bool>(condition, negated));
    }

    // 6. Returns that follow each other.
    //        if (a) return;  if (b) return;        IL: a; brfalse N; ret; N: b; brfalse M; ret
    //    is printed 'if (a || b) return;', which has one 'ret'.
    private static void SplitReturns(ILFunction f, RawIL raw)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test) || !test.FalseInst.MatchNop()) continue;
            var exit = test.TrueInst as Leave;
            var body = test.TrueInst as Block;
            if (exit == null && body != null && body.Instructions.Count == 1) exit = body.Instructions[0] as Leave;
            if (exit == null || !exit.IsLeavingFunction) continue;
            if (!(exit.Value is Nop || exit.Value is LdcI4 || exit.Value is LdNull || exit.Value is LdStr || exit.Value is LdLoc || exit.Value is LdcF4 || exit.Value is LdcF8 || exit.Value is LdcI8)) continue;
            var parts = new List<KeyValuePair<ILInstruction, bool>>();
            Disjuncts(test.Condition, false, parts);
            if (parts.Count < 2) continue;
            // A new statement starts at each operand that has a 'ret' right before it.
            var cuts = new List<int>();
            for (int i = 1; i < parts.Count; i++)
            {
                int start = RawIL.StartOf(parts[i].Key);
                RawIL.Op before;
                if (start <= 0 || !raw.Near(start, -1, out before)) continue;
                // The range of a captured variable starts at its 'ldfld': the closure is loaded before.
                for (int back = 2; back <= 3 && (RawIL.IsLdLoc(before.Code) || RawIL.IsLdArg(before.Code)) && !raw.Targets.Contains(start); back++)
                    if (!raw.Near(start, -back, out before)) break;
                // In a protected block 'return;' is a 'leave' to the 'ret' that ends the method.
                RawIL.Op landing;
                bool leaves = exit.Value is Nop && (before.Code == ILOpCode.Leave || before.Code == ILOpCode.Leave_s) && !raw.RegionEnds.Contains(before.End) && raw.IsAt(before.Operand, out landing) && landing.Code == ILOpCode.Ret;
                if (before.Code == ILOpCode.Ret || leaves) cuts.Add(i);
            }
            if (cuts.Count == 0) continue;
            cuts.Add(parts.Count);

            var block = (Block)test.Parent;
            int at = test.ChildIndex;
            BlockContainer target = exit.TargetContainer;
            var operands = new List<ILInstruction>();
            foreach (var part in parts)
            {
                part.Key.ReplaceWith(new Nop());
                operands.Add(part.Value ? Negate(part.Key) : part.Key);
            }
            var statements = new List<ILInstruction>();
            int from = 0;
            foreach (int cut in cuts)
            {
                ILInstruction condition = operands[from];
                for (int i = from + 1; i < cut; i++) condition = IfInstruction.LogicOr(condition, operands[i]);
                var leave = new Leave(target, exit.Value.Clone());
                leave.AddILRange(exit);
                statements.Add(new IfInstruction(condition, leave));
                from = cut;
            }
            block.Instructions.RemoveAt(at);
            for (int i = 0; i < statements.Count; i++) block.Instructions.Insert(at + i, statements[i]);
        }
    }

    // An integer read as a condition: 'if (u)' in the tree, 'if (u > 0)' in the IL.
    private static void UnsignedTruth(ILFunction f, RawIL raw)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            var operands = new List<ILInstruction> { test.Condition };
            if (test.ResultType != StackType.Void && (test.TrueInst.MatchLdcI4(1) || test.FalseInst.MatchLdcI4(0)))
            {
                operands.Add(test.TrueInst);
                operands.Add(test.FalseInst);
            }
            foreach (ILInstruction value in operands)
            {
                if (value is Comp || value is IfInstruction || value is LdcI4) continue;
                if (value.ResultType != StackType.I4 && value.ResultType != StackType.I8) continue;
                if (!IsIntegerValue(value)) continue;
                int end = RawIL.EndOf(value);
                RawIL.Op zero, next;
                var assigned = value as StLoc;
                if (assigned != null && RawIL.EndOf(assigned.Value) >= 0) end = RawIL.EndOf(assigned.Value);
                if (end < 0) continue;
                // '--n > 0' on an unsigned n: 'dup; stloc n; ldc.i4.0; bgt.un'.
                bool found = AtOrBefore(raw, end, c => c == ILOpCode.Ldc_i4_0, out zero);
                for (int k = 1; k <= 2 && !found && assigned != null; k++)
                    found = raw.Near(end, k, out zero) && zero.Code == ILOpCode.Ldc_i4_0 && raw.IsAt(end, out next) && next.Code == ILOpCode.Dup;
                if (!found) continue;
                if (!raw.Near(zero.Offset, 1, out next)) continue;
                if (next.Code == ILOpCode.Conv_i8 || next.Code == ILOpCode.Conv_u8) { if (!raw.Near(zero.Offset, 2, out next)) continue; }
                if (!IsUnsignedCompare(next.Code)) continue;
                var holder = new Nop();
                value.ReplaceWith(holder);
                ILInstruction zeroValue = value.ResultType == StackType.I8 ? (ILInstruction)new LdcI8(0) : new LdcI4(0);
                holder.ReplaceWith(new Comp(ComparisonKind.GreaterThan, Sign.Unsigned, value, zeroValue));
            }
        }
    }

    private static bool IsPlainBefore(ILInstruction d)
    {
        return d is LdLoc || d is LdcI4 || d is LdcI8 || d is LdcF4 || d is LdcF8 || d is LdNull || d is LdStr || d is LdFlda || d is LdsFlda || d is LdLoca;
    }

    private static void InlineAssignments(ILFunction f)
    {
        foreach (StLoc store in RawIL.Own(f).OfType<StLoc>().ToList())
        {
            var value = store.Value as Block;
            if (value == null || value.Kind != BlockKind.CallInlineAssign || !IsStatement(store)) continue;
            ILVariable v = store.Variable;
            if (v.LoadCount != 1 || v.StoreCount != 1 || v.AddressCount != 0) continue;
            if (v.Kind != VariableKind.Local && v.Kind != VariableKind.StackSlot) continue;
            var block = (Block)store.Parent;
            int at = store.ChildIndex;
            if (at + 1 >= block.Instructions.Count) continue;
            ILInstruction next = block.Instructions[at + 1];
            // What the next statement evaluates before it reads v.
            LdLoc use = null;
            var slots = new List<LdLoc>();
            bool plain = true;
            foreach (ILInstruction d in next.Descendants)
            {
                var load = d as LdLoc;
                if (load != null && load.Variable == v) { use = load; break; }
                if (!IsPlainBefore(d)) { plain = false; break; }
                if (load != null && load.Variable.Kind == VariableKind.StackSlot) slots.Add(load);
            }
            if (use == null || !plain || slots.Count > at) continue;
            // The stack slots it reads first were filled by the statements just before, in order.
            bool ordered = true;
            for (int i = 0; i < slots.Count && ordered; i++)
            {
                var earlier = block.Instructions[at - slots.Count + i] as StLoc;
                ILVariable s = slots[i].Variable;
                if (earlier == null || earlier.Variable != s || s.LoadCount != 1 || s.StoreCount != 1 || s.AddressCount != 0) ordered = false;
            }
            if (!ordered) continue;

            store.Value = new Nop();
            use.ReplaceWith(value);
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

    private static ComparisonKind Opposite(ComparisonKind kind)
    {
        switch (kind)
        {
            case ComparisonKind.Equality: return ComparisonKind.Inequality;
            case ComparisonKind.Inequality: return ComparisonKind.Equality;
            case ComparisonKind.LessThan: return ComparisonKind.GreaterThanOrEqual;
            case ComparisonKind.LessThanOrEqual: return ComparisonKind.GreaterThan;
            case ComparisonKind.GreaterThan: return ComparisonKind.LessThanOrEqual;
            default: return ComparisonKind.LessThan;
        }
    }

    private static bool IsComparison(ILOpCode c)
    {
        return c == ILOpCode.Ceq || c == ILOpCode.Cgt || c == ILOpCode.Cgt_un || c == ILOpCode.Clt || c == ILOpCode.Clt_un;
    }

    private static bool IsBranchOnBool(ILOpCode c)
    {
        return c == ILOpCode.Brfalse || c == ILOpCode.Brfalse_s || c == ILOpCode.Brtrue || c == ILOpCode.Brtrue_s;
    }

    // The block that holds the condition of 'while', 'for' or 'do while'.
    private static bool IsLoopHeader(Block block)
    {
        var loop = block.Parent as BlockContainer;
        if (loop == null || loop.Kind == ContainerKind.Normal || loop.Kind == ContainerKind.Switch) return false;
        return block == (loop.Kind == ContainerKind.DoWhile ? loop.Blocks[loop.Blocks.Count - 1] : loop.EntryPoint);
    }

    // 'bool b = x && y; T v = b ? p : q;': the same value of '&&' ('br E; ldc.i4.0; E:'), tested
    // by the branch of a conditional expression. 'v = (x && y) ? p : q' branches on each operand.
    private static void ShortCircuitChoices(ILFunction f, RawIL raw, ILTransformContext context)
    {
        foreach (IfInstruction choice in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            var store = choice.Parent as StLoc;
            if (store == null || !IsStatement(store) || choice.ResultType == StackType.Void) continue;
            var logic = choice.Condition as IfInstruction;
            if (logic == null || logic.ResultType != StackType.I4 || !logic.FalseInst.MatchLdcI4(0)) continue;
            // After the last operand: its comparison as a value, 'br E; ldc.i4.0; E: brtrue / brfalse'.
            // (A branch of the conditional may have no range: 'default(T?)'.)
            int end = RawIL.EndOf(logic), i0;
            if (end < 0 || !raw.IndexAt.TryGetValue(end, out i0)) continue;
            bool built = false;
            for (int k = 0; k <= 4 && i0 + k + 2 < raw.Ops.Count && !built; k++)
            {
                RawIL.Op jump = raw.Ops[i0 + k], constant = raw.Ops[i0 + k + 1], branch = raw.Ops[i0 + k + 2];
                if (jump.Code == ILOpCode.Br || jump.Code == ILOpCode.Br_s)
                {
                    built = constant.Code == ILOpCode.Ldc_i4_0 && jump.Operand == branch.Offset && IsBranchOnBool(branch.Code);
                    break;
                }
                if (jump.Code != ILOpCode.Ceq && jump.Code != ILOpCode.Cgt && jump.Code != ILOpCode.Clt && jump.Code != ILOpCode.Cgt_un && jump.Code != ILOpCode.Clt_un && jump.Code != ILOpCode.Ldc_i4_0) break;
            }
            int start = RawIL.StartOf(logic);
            if (!built || start < 0 || RawIL.StartOf(store) != start) continue;
            var block = (Block)store.Parent;
            ILVariable v = f.RegisterVariable(VariableKind.Local, context.TypeSystem.FindType(KnownTypeCode.Boolean));
            choice.Condition = new LdLoc(v);
            block.Instructions.Insert(store.ChildIndex, new StLoc(v, logic));
        }
    }

    private static void ConditionLocals(ILFunction f, RawIL raw, ILTransformContext context)
    {
        foreach (IfInstruction test in RawIL.Own(f).OfType<IfInstruction>().ToList())
        {
            if (!IsStatement(test) || IsLoopHeader((Block)test.Parent)) continue;
            var comp = test.Condition as Comp;
            if (comp == null && ShortCircuitValue(f, test, raw, context)) continue;
            if (comp == null && FirstOperandLocal(f, test, raw, context)) continue;
            if (comp == null && FirstOperandValueNegated(f, test, raw, context)) continue;
            if (comp == null || comp.LiftingKind != ComparisonLiftingKind.None) continue;
            // With an else, the true branch must be the one that follows the test in the IL.
            if (!test.FalseInst.MatchNop())
            {
                int t = RawIL.StartOf(test.TrueInst), e = RawIL.StartOf(test.FalseInst);
                if (t < 0 || e < 0 || t > e) continue;
            }
            int end = RawIL.EndOf(comp);
            RawIL.Op branch, before;
            // 'x >= y' kept as a value is 'clt; ldc.i4.0; ceq': the range may stop at the first of the three.
            RawIL.Op zero, equal;
            if (end >= 0 && raw.IsAt(end, out zero) && zero.Code == ILOpCode.Ldc_i4_0 && raw.Near(end, 1, out equal) && equal.Code == ILOpCode.Ceq && raw.Near(end, -1, out before) && IsComparison(before.Code)) end = equal.End;
            if (end < 0 || !AtOrBefore(raw, end, IsBranchOnBool, out branch)) continue;
            if (!raw.Near(branch.Offset, -1, out before) || !IsComparison(before.Code)) continue;
            // The branch must be the one of this 'if': its body starts right after it.
            int bodyStart = RawIL.StartOf(test.TrueInst);
            if (bodyStart != branch.End) continue;
            bool skipsWhenFalse = branch.Code == ILOpCode.Brfalse || branch.Code == ILOpCode.Brfalse_s;
            bool isFloat = comp.InputType == StackType.F4 || comp.InputType == StackType.F8;
            if (!skipsWhenFalse && isFloat) continue;

            var block = (Block)test.Parent;
            ILVariable v = f.RegisterVariable(VariableKind.Local, context.TypeSystem.FindType(KnownTypeCode.Boolean));
            test.Condition = new Nop();
            ILInstruction stored = comp;
            if (!skipsWhenFalse)
            {
                ILInstruction left = comp.Left, right = comp.Right;
                comp.Left = new Nop();
                comp.Right = new Nop();
                var negated = new Comp(Opposite(comp.Kind), comp.Sign, left, right);
                negated.AddILRange(comp);
                stored = negated;
            }
            test.Condition = skipsWhenFalse ? (ILInstruction)new LdLoc(v) : Comp.LogicNot(new LdLoc(v));
            block.Instructions.Insert(test.ChildIndex, new StLoc(v, stored));
        }
    }

    // The branch right before a body that starts at 'bodyStart'. The range of a body that
    // begins with a compound assignment to a static field starts after its 'ldsfld'.
    private static bool BranchBefore(RawIL raw, int bodyStart, out RawIL.Op branch)
    {
        if (!raw.Near(bodyStart, -1, out branch)) return false;
        if (branch.Code == ILOpCode.Ldsfld && !raw.Targets.Contains(bodyStart) && !raw.Near(bodyStart, -2, out branch)) return false;
        return IsBranchOnBool(branch.Code);
    }

    // 'bool b = x && y; if (b)': the value of '&&' is built ('br E; ldc.i4.0; E:') and then
    // tested, where 'if (x && y)' branches on each operand.
    private static bool ShortCircuitValue(ILFunction f, IfInstruction test, RawIL raw, ILTransformContext context)
    {
        var logic = test.Condition as IfInstruction;
        if (logic == null || logic.ResultType != StackType.I4) return false;
        if (!logic.FalseInst.MatchLdcI4(0) && !logic.TrueInst.MatchLdcI4(1)) return false;
        if (ShortCircuitValueNegated(f, test, logic, raw, context)) return true;
        if (!test.FalseInst.MatchNop())
        {
            int t = RawIL.StartOf(test.TrueInst), e = RawIL.StartOf(test.FalseInst);
            if (t < 0 || e < 0 || t > e) return false;
        }
        int bodyStart = RawIL.StartOf(test.TrueInst);
        RawIL.Op branch, constant, jump;
        if (bodyStart <= 0 || !BranchBefore(raw, bodyStart, out branch)) return false;
        if (branch.Code != ILOpCode.Brfalse && branch.Code != ILOpCode.Brfalse_s) return false;
        if (!raw.Near(branch.Offset, -1, out constant) || !raw.Near(branch.Offset, -2, out jump)) return false;
        if (constant.Code != (logic.FalseInst.MatchLdcI4(0) ? ILOpCode.Ldc_i4_0 : ILOpCode.Ldc_i4_1)) return false;
        if ((jump.Code != ILOpCode.Br && jump.Code != ILOpCode.Br_s) || jump.Operand != branch.Offset) return false;
        // The whole condition is in front of the branch.
        int start = RawIL.StartOf(logic);
        if (start < 0 || start > jump.Offset) return false;
        var block = (Block)test.Parent;
        ILVariable v = f.RegisterVariable(VariableKind.Local, context.TypeSystem.FindType(KnownTypeCode.Boolean));
        test.Condition = new LdLoc(v);
        block.Instructions.Insert(test.ChildIndex, new StLoc(v, logic));
        return true;
    }

    // 'bool b = x > 1; if (b && y)': the first operand is a comparison kept as a value
    // ('cgt; brfalse'), where 'if (x > 1 && y)' branches on it ('ble').
    private static bool FirstOperandLocal(ILFunction f, IfInstruction test, RawIL raw, ILTransformContext context)
    {
        ILInstruction first = test.Condition, lhs, rhs, next = null;
        while (first.MatchLogicAnd(out lhs, out rhs)) { first = lhs; next = rhs; }
        var comp = first as Comp;
        if (comp == null || next == null || comp.LiftingKind != ComparisonLiftingKind.None) return false;
        int end = RawIL.EndOf(comp);
        RawIL.Op branch, before;
        if (end < 0 || !AtOrBefore(raw, end, IsBranchOnBool, out branch)) return false;
        if (branch.Code != ILOpCode.Brfalse && branch.Code != ILOpCode.Brfalse_s) return false;
        if (!raw.Near(branch.Offset, -1, out before) || !IsComparison(before.Code)) return false;
        // The branch is the one of this operand: the next operand starts right after it.
        if (RawIL.StartOf(next) != branch.End) return false;
        var block = (Block)test.Parent;
        ILVariable v = f.RegisterVariable(VariableKind.Local, context.TypeSystem.FindType(KnownTypeCode.Boolean));
        var holder = new Nop();
        comp.ReplaceWith(holder);
        holder.ReplaceWith(new LdLoc(v));
        block.Instructions.Insert(test.ChildIndex, new StLoc(v, comp));
        return true;
    }

    // 'bool b = x && y; if (!b && z)': the first operand is the value of an '&&', built
    // ('br E; ldc.i4.0; E:') and skipped on true ('brtrue'). The tree holds '(!x || !y) && z'.
    private static bool FirstOperandValueNegated(ILFunction f, IfInstruction test, RawIL raw, ILTransformContext context)
    {
        ILInstruction lhs, rhs;
        if (!test.Condition.MatchLogicAnd(out lhs, out rhs)) return false;
        var logic = lhs as IfInstruction;
        if (logic == null || logic.ResultType != StackType.I4 || !logic.TrueInst.MatchLdcI4(1)) return false;
        int next = RawIL.StartOf(rhs);
        RawIL.Op branch, constant, jump;
        if (next <= 0 || !raw.Near(next, -1, out branch) || branch.End != next) return false;
        if (branch.Code != ILOpCode.Brtrue && branch.Code != ILOpCode.Brtrue_s) return false;
        if (!raw.Near(branch.Offset, -1, out constant) || constant.Code != ILOpCode.Ldc_i4_0) return false;
        if (!raw.Near(branch.Offset, -2, out jump) || (jump.Code != ILOpCode.Br && jump.Code != ILOpCode.Br_s) || jump.Operand != branch.Offset) return false;
        int start = RawIL.StartOf(logic);
        if (start < 0 || start > jump.Offset) return false;
        var block = (Block)test.Parent;
        ILVariable v = f.RegisterVariable(VariableKind.Local, context.TypeSystem.FindType(KnownTypeCode.Boolean));
        var holder = new Nop();
        logic.ReplaceWith(holder);
        holder.ReplaceWith(Comp.LogicNot(new LdLoc(v)));
        block.Instructions.Insert(test.ChildIndex, new StLoc(v, Invert(logic)));
        return true;
    }
    private static ILInstruction Invert(ILInstruction condition)
    {
        ILInstruction lhs, rhs, positive;
        if (condition.MatchLogicAnd(out lhs, out rhs))
        {
            lhs.ReplaceWith(new Nop());
            rhs.ReplaceWith(new Nop());
            return IfInstruction.LogicOr(Invert(lhs), Invert(rhs));
        }
        if (condition.MatchLogicOr(out lhs, out rhs))
        {
            lhs.ReplaceWith(new Nop());
            rhs.ReplaceWith(new Nop());
            return IfInstruction.LogicAnd(Invert(lhs), Invert(rhs));
        }
        if (condition.MatchLogicNot(out positive)) { positive.ReplaceWith(new Nop()); return positive; }
        if (condition.Parent != null) condition.ReplaceWith(new Nop());
        return Comp.LogicNot(condition);
    }

    // The same when the body is skipped on true: 'bool b = x || y; if (!b)'. The tree holds
    // the opposite condition ('!x && !y'): the value of the IL is its negation.
    private static bool ShortCircuitValueNegated(ILFunction f, IfInstruction test, IfInstruction logic, RawIL raw, ILTransformContext context)
    {
        if (!test.FalseInst.MatchNop())
        {
            int t = RawIL.StartOf(test.TrueInst), e = RawIL.StartOf(test.FalseInst);
            if (t < 0 || e < 0 || t > e) return false;
        }
        int bodyStart = RawIL.StartOf(test.TrueInst);
        RawIL.Op branch, constant, jump;
        if (bodyStart <= 0 || !BranchBefore(raw, bodyStart, out branch)) return false;
        if (branch.Code != ILOpCode.Brtrue && branch.Code != ILOpCode.Brtrue_s) return false;
        if (!raw.Near(branch.Offset, -1, out constant) || !raw.Near(branch.Offset, -2, out jump)) return false;
        // Once negated, '&&' becomes '||' and ends with the other constant.
        // ('a || (b && c)' ends with the 0 of its '&&': the 1 of the '||' is further up.)
        if (constant.Code != ILOpCode.Ldc_i4_1 && constant.Code != ILOpCode.Ldc_i4_0) return false;
        if ((jump.Code != ILOpCode.Br && jump.Code != ILOpCode.Br_s) || jump.Operand != branch.Offset) return false;
        int start = RawIL.StartOf(logic);
        if (start < 0 || start > jump.Offset) return false;
        var block = (Block)test.Parent;
        ILVariable v = f.RegisterVariable(VariableKind.Local, context.TypeSystem.FindType(KnownTypeCode.Boolean));
        test.Condition = Comp.LogicNot(new LdLoc(v));
        block.Instructions.Insert(test.ChildIndex, new StLoc(v, Invert(logic)));
        return true;
    }

    // 8. Delegate kept in a static field.
    //        F ?? (F = new D(o.M))                 IL: ldsfld F; dup; brtrue E; pop; o; ldftn M; newobj D; dup; stsfld F; E:
    //    is how the compiler caches the delegate of a lambda. When the class of the lambdas is
    //    not recognised as such (one of its lambdas is only used by a virtualised method), the
    //    decompiler still drops the cache and prints 'new D(o.M)'.
    private static void DelegateCaches(ILFunction f, RawIL raw, ILTransformContext context)
    {
        foreach (NewObj created in RawIL.Own(f).OfType<NewObj>().ToList())
        {
            if (created.Method.DeclaringType.Kind != TypeKind.Delegate || created.Arguments.Count != 2 || !(created.Arguments[1] is LdFtn)) continue;
            if (created.Parent is StObj && created.Parent.Parent is NullCoalescingInstruction) continue;   // already written out
            // Read around the 'ldftn': the decompiler gives the delegate the range of the whole cache.
            int ftn = -1;
            RawIL.Op probe;
            foreach (var r in created.Arguments[1].ILRanges)
                if (raw.IsAt(r.Start, out probe) && probe.Code == ILOpCode.Ldftn) ftn = r.Start;
            RawIL.Op pop, branch, dup, load, made, dup2, store;
            if (ftn < 0) continue;
            if (!raw.Near(ftn, -2, out pop) || pop.Code != ILOpCode.Pop) continue;
            if (!raw.Near(ftn, -3, out branch) || (branch.Code != ILOpCode.Brtrue && branch.Code != ILOpCode.Brtrue_s)) continue;
            if (!raw.Near(ftn, -4, out dup) || dup.Code != ILOpCode.Dup) continue;
            if (!raw.Near(ftn, -5, out load) || load.Code != ILOpCode.Ldsfld) continue;
            if (!raw.Near(ftn, 1, out made) || made.Code != ILOpCode.Newobj) continue;
            if (!raw.Near(ftn, 2, out dup2) || dup2.Code != ILOpCode.Dup) continue;
            if (!raw.Near(ftn, 3, out store) || store.Code != ILOpCode.Stsfld || store.Operand != load.Operand) continue;
            IField field = null;
            try { field = context.TypeSystem.MainModule.ResolveEntity(System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle(load.Operand), new ICSharpCode.Decompiler.TypeSystem.GenericContext(f.Method.DeclaringTypeDefinition != null ? f.Method.DeclaringTypeDefinition.TypeParameters : null, f.Method.TypeParameters)) as IField; }
            catch (System.BadImageFormatException) { }
            if (field == null || !field.IsStatic || field.Type.Kind != TypeKind.Delegate) continue;
            var holder = new Nop();
            created.ReplaceWith(holder);
            var assign = new StObj(new LdsFlda(field), created, field.Type);
            holder.ReplaceWith(new NullCoalescingInstruction(NullCoalescingKind.Ref, new LdObj(new LdsFlda(field), field.Type), assign));
        }
    }

    private static bool IsPlainNumber(IType type)
    {
        ITypeDefinition def = type.GetDefinition();
        if (def == null) return false;
        switch (def.KnownTypeCode)
        {
            case KnownTypeCode.Int32: case KnownTypeCode.UInt32: case KnownTypeCode.Int64: case KnownTypeCode.UInt64:
            case KnownTypeCode.Single: case KnownTypeCode.Double:
                return true;
        }
        return false;
    }

    // 7. Assignment written out.
    //        x.f = x.f + v;                        IL: ldloc x; ldloc x; ldfld f; v; add; stfld f
    //    is printed 'x.f += v;', which loads x once ('ldloc x; dup').
    private static void ExplicitCompound(ILFunction f, RawIL raw)
    {
        // 'x.p.f = x.p.f * v' on a field of a struct field stays an assignment in the tree,
        // and is printed 'x.p.f *= v' all the same: marked as written out.
        foreach (StObj plain in RawIL.Own(f).OfType<StObj>().ToList())
        {
            var outer = plain.Target as LdFlda;
            var binary = plain.Value as BinaryNumericInstruction;
            var read = binary == null ? null : binary.Left as LdObj;
            if (outer == null || !(outer.Target is LdFlda) || read == null || !IsStatement(plain)) continue;
            if (!(read.Target is LdFlda) || read.Target.ToString() != outer.ToString()) continue;
            string marker;
            if (!Explicit.TryGetValue(plain, out marker)) Explicit.GetValue(plain, _ => "explicit");
        }
        foreach (CompoundAssignmentInstruction compound in RawIL.Own(f).OfType<CompoundAssignmentInstruction>().ToList())
        {
            if (!IsStatement(compound) || compound.TargetKind != CompoundTargetKind.Address || compound.EvalMode != CompoundEvalMode.EvaluatesToNewValue) continue;
            var target = compound.Target as LdFlda;
            if (target == null) continue;
            var holder = target.Target as LdLoc;
            int start = RawIL.StartOf(compound);
            RawIL.Op first, second;
            // A field of a struct field: 'x.p.f = x.p.f + v' loads the address of p twice
            // ('ldarg x; ldflda p; ldarg x; ldflda p; ldfld f'); 'x.p.f += v' once ('ldflda f; dup').
            var inner = target.Target as LdFlda;
            bool nested = false;
            if (holder == null && inner != null && inner.Target is LdLoc && start >= 0)
            {
                // The range may start at the second pair of loads.
                foreach (int shift in new[] { 0, -2 })
                {
                    RawIL.Op a0, a1, a2, a3, a4;
                    if (raw.Near(start, shift, out a0) && raw.Near(start, shift + 1, out a1) && raw.Near(start, shift + 2, out a2) && raw.Near(start, shift + 3, out a3) && raw.Near(start, shift + 4, out a4)
                        && (RawIL.IsLdLoc(a0.Code) || RawIL.IsLdArg(a0.Code)) && a2.Code == a0.Code && a2.Operand == a0.Operand
                        && a1.Code == ILOpCode.Ldflda && a3.Code == ILOpCode.Ldflda && a1.Operand == a3.Operand && a4.Code == ILOpCode.Ldfld) nested = true;
                }
            }
            if (holder == null && !nested) continue;
            // 'this' is loaded twice in both forms.
            if (!nested && holder.Variable.Kind == VariableKind.Parameter && holder.Variable.Index == -1) continue;
            if (start < 0 || !raw.IsAt(start, out first) || !raw.Near(start, 1, out second)) continue;
            if (nested) second = first;
            if (!RawIL.IsLdLoc(first.Code) && !RawIL.IsLdArg(first.Code)) continue;
            // The range may start at the second load: the first one is then just before.
            RawIL.Op earlier;
            if (!nested && second.Code == ILOpCode.Ldfld && raw.Near(start, -1, out earlier) && !raw.Targets.Contains(start)) { second = first; first = earlier; }
            if (RawIL.IsLdLoc(first.Code) != RawIL.IsLdLoc(second.Code) || RawIL.IsLdArg(first.Code) != RawIL.IsLdArg(second.Code) || first.Operand != second.Operand) continue;

            IType type = target.Field.Type;
            var numeric = compound as NumericCompoundAssign;
            var user = compound as UserDefinedCompoundAssign;
            if (numeric != null && (!IsPlainNumber(type) || numeric.IsLifted)) continue;
            if (numeric == null && (user == null || user.Method.Parameters.Count != 2)) continue;
            ILInstruction value = compound.Value;
            compound.Value = new Nop();
            compound.Target = new Nop();
            ILInstruction read = new LdObj(target.Clone(), type);
            ILInstruction result;
            if (numeric != null) result = new BinaryNumericInstruction(numeric.Operator, read, value, numeric.CheckForOverflow, numeric.Sign);
            else
            {
                var call = new Call(user.Method);
                call.Arguments.Add(read);
                call.Arguments.Add(value);
                result = call;
            }
            var store = new StObj(target, result, type);
            Explicit.GetValue(store, _ => "explicit");
            store.AddILRange(compound);
            compound.ReplaceWith(store);
        }
    }

    private static void MarkFloatCasts(ILFunction f, RawIL raw)
    {
        foreach (Conv conv in RawIL.Own(f).OfType<Conv>())
        {
            // The decompiler adds conversions of its own: only those the IL has.
            bool inBody = false;
            RawIL.Op op;
            foreach (var r in conv.ILRanges)
                if (raw.IsAt(r.Start, out op) && (op.Code == ILOpCode.Conv_r4 || op.Code == ILOpCode.Conv_r8)) inBody = true;
            if (!inBody) continue;
            if (conv.InputType == StackType.F4 && conv.TargetType == PrimitiveType.R4) FloatCasts.GetValue(conv.Argument, _ => "float");
            else if (conv.InputType == StackType.F8 && conv.TargetType == PrimitiveType.R8) FloatCasts.GetValue(conv.Argument, _ => "double");
        }
    }
}
