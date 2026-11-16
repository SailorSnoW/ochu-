// C# transforms: what the IL says and only the syntax can carry.
//
// 1. ExplicitFloatCasts: '(float)f' on a float is a 'conv.r4' in the IL (RestoreExpressions
//    marks them); the decompiler prints 'f'.
//
// 2. ForeachVariableTypes: 'foreach (Base x in listOfDerived)' has a local of type Base.
//    The decompiler gives the variable the element type of the collection.
//
// 3. ShortCircuitLocals: the compiler evaluates 'a && b' and 'a || b' without a branch
//    ('and', 'or') when b is a local or a parameter. A branch in the IL (the decompiler then
//    prints '&&') means the right operand was something else: nested conditions, or a longer
//    expression grouped to the right. '(bool)b' compiles to that branch in every position.
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreSyntax : IAstTransform
{
    public void Run(AstNode rootNode, TransformContext context)
    {
        if (Program.With.Contains("RestoreFloatCast")) ExplicitFloatCasts(rootNode);
        if (Program.With.Contains("RestoreForeachType")) ForeachVariableTypes(rootNode, context);
        if (Program.With.Contains("RestoreShortCircuit")) { ShortCircuitLocals(rootNode, context); BitwiseBooleans(rootNode); }
        if (Program.With.Contains("RestoreRangeSwitch")) RangeSwitches(rootNode, context);
        if (Program.With.Contains("RestoreSwitchLabels")) { SwitchLabels(rootNode, context); EmptySections(rootNode, context); SectionOrder(rootNode, context); }
        if (Program.With.Contains("RestoreConditional")) ExplicitConditionals(rootNode);
        if (Program.With.Contains("RestoreBaseCall")) BaseCalls(rootNode);
        if (Program.With.Contains("RestoreThrowCheck")) ThrowChecks(rootNode, context);
        if (Program.With.Contains("RestoreIntLiteral")) IntLiterals(rootNode, context);
        if (Program.With.Contains("RestoreInitializerOrder")) { FieldOrder(rootNode, context); InitializerOrder(rootNode, context); }
        if (Program.With.Contains("RestoreNarrowingCast")) { NarrowingCasts(rootNode); ConvertedConditionals(rootNode); BranchConversions(rootNode); BoxedFallbacks(rootNode); ImplicitConversions(rootNode, context); EnumSlots(rootNode, context); NullableDefaults(rootNode); EmptyCompounds(rootNode); NullableEnumMasks(rootNode, context); BranchCasts(rootNode); }
        if (Program.With.Contains("RestoreForeachType")) { DictionaryEnumerators(rootNode, context); StringLoops(rootNode, context); RestoreClosures.ForeachOverCopy(rootNode, context); }
        if (Program.With.Contains("RestoreCallVirt")) { NullConditionalValues(rootNode); NullConditionalTests(rootNode); GenericNullConditionals(rootNode, context); IdentityCasts(rootNode, context); UnboxedPatterns(rootNode, context); }
        if (Program.With.Contains("RestoreNarrowingCast")) { UnsignedNegations(rootNode, context); BranchConstants(rootNode, context); }
        if (Program.With.Contains("RestoreExplicitCompound")) ExplicitCompounds(rootNode);
        if (Program.With.Contains("RestoreValueReceiver")) { ValueReceivers(rootNode, context); ConcatToString(rootNode, context); BoxedConcats(rootNode); NullOnTheLeft(rootNode, context); }
    }

    // 11. NarrowingCasts: '(uint)x' on a 64-bit value is one 'conv.u4'. The decompiler prints
    //     '(uint)(int)x', which is a 'conv.i4'; and for a 'conv.i4' whose result is used as a
    //     'uint' it prints '(uint)x'.
    private static bool Is64(Expression e)
    {
        var rr = e.GetResolveResult();
        ITypeDefinition def = rr == null ? null : rr.Type.GetDefinition();
        return def != null && (def.KnownTypeCode == KnownTypeCode.Int64 || def.KnownTypeCode == KnownTypeCode.UInt64);
    }

    private static void NarrowingCasts(AstNode rootNode)
    {
        foreach (var outer in rootNode.Descendants.OfType<CastExpression>().ToList())
        {
            if (outer.Parent == null) continue;
            string to = outer.Type.ToString();
            if (to != "uint" && to != "int") continue;
            var inner = Unwrap(outer.Expression) as CastExpression;
            if (inner != null)
            {
                string via = inner.Type.ToString();
                if (via == to || (via != "uint" && via != "int") || !Is64(inner.Expression)) continue;
                inner.ReplaceWith(inner.Expression.Detach());
                continue;
            }
            if (outer.Parent is CastExpression || !Is64(outer.Expression)) continue;
            // The conversion is noted on the cast, or on its operand when the decompiler
            // wrote the cast anew.
            Conv conv = outer.Annotations.OfType<Conv>().FirstOrDefault() ?? Unwrap(outer.Expression).Annotations.OfType<Conv>().FirstOrDefault();
            if (conv == null || conv.InputType != StackType.I8) continue;
            var wanted = to == "uint" ? ICSharpCode.Decompiler.IL.PrimitiveType.I4 : ICSharpCode.Decompiler.IL.PrimitiveType.U4;
            if (conv.TargetType != wanted) continue;
            var cast = new CastExpression(new ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveType(to == "uint" ? "int" : "uint"), outer.Expression.Detach());
            outer.Expression = cast;
        }
    }

    // 17. ConvertedConditionals: '(byte)(c ? 1 : 0)' is a 'conv.u1' after the two constants.
    //     The decompiler prints 'c ? ((byte)1) : ((byte)0)', two constants and no conversion.
    private static void ConvertedConditionals(AstNode rootNode)
    {
        foreach (var node in rootNode.Descendants.OfType<ConditionalExpression>().ToList())
        {
            var yes = Unwrap(node.TrueExpression) as CastExpression;
            var no = Unwrap(node.FalseExpression) as CastExpression;
            if (yes == null || no == null || node.Parent == null) continue;
            if (!(Unwrap(yes.Expression) is PrimitiveExpression) || !(Unwrap(no.Expression) is PrimitiveExpression)) continue;
            string to = yes.Type.ToString();
            if (to != no.Type.ToString() || !(yes.Type is ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveType)) continue;
            // The conditional has no instruction of its own (it was a 'conv' of a comparison):
            // the nearest expression that has one must hold exactly that conversion.
            AstNode holder = node.Parent;
            while (holder != null && !(holder is Statement) && !holder.Annotations.OfType<ILInstruction>().Any()) holder = holder.Parent;
            ILInstruction top = holder == null ? null : holder.Annotations.OfType<ILInstruction>().FirstOrDefault();
            if (top == null) continue;
            int convs = top.Descendants.OfType<Conv>().Count(c => c.Argument is Comp && c.TargetType != ICSharpCode.Decompiler.IL.PrimitiveType.I4 && c.TargetType != ICSharpCode.Decompiler.IL.PrimitiveType.U4);
            int shaped = holder.Descendants.OfType<ConditionalExpression>().Count(c => Unwrap(c.TrueExpression) is CastExpression && Unwrap(c.FalseExpression) is CastExpression);
            if (convs != 1 || shaped != 1) continue;
            var cast = new CastExpression();
            node.ReplaceWith(cast);
            node.TrueExpression = Unwrap(yes.Expression).Detach();
            node.FalseExpression = Unwrap(no.Expression).Detach();
            cast.Type = yes.Type.Clone();
            cast.Expression = new ParenthesizedExpression(node);
        }
    }

    // 18. BranchConversions: 'c ? 1 : (int)l' converts in one branch only ('l; conv.i4; br E;
    //     ldc.i4.1; E:'). The decompiler prints '(int)(c ? 1 : l)', which widens the constant
    //     and converts after the two branches ('ldc.i4.1; conv.i8; E: conv.i4').
    private static void BranchConversions(AstNode rootNode)
    {
        foreach (var cast in rootNode.Descendants.OfType<CastExpression>().ToList())
        {
            var node = Unwrap(cast.Expression) as ConditionalExpression;
            if (node == null || cast.Parent == null || !(cast.Type is ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveType)) continue;
            IfInstruction inst = cast.Annotations.OfType<IfInstruction>().FirstOrDefault() ?? node.Annotations.OfType<IfInstruction>().FirstOrDefault();
            if (inst == null) continue;
            // One branch is on 32 bits already (a constant, '(int)l + 12'), the other is
            // the converted one. Either branch of the instruction may be the first (RestoreConditional).
            var yesType = Unwrap(node.TrueExpression).GetResolveResult();
            var noType = Unwrap(node.FalseExpression).GetResolveResult();
            if (yesType == null || noType == null) continue;
            // (The same with 'uint': 'c ? (uint)l : u', 'l; conv.u4' in one branch.)
            bool unsigned = cast.Type.ToString() == "uint";
            KnownTypeCode narrowCode = unsigned ? KnownTypeCode.UInt32 : KnownTypeCode.Int32;
            bool narrowFirst = Unwrap(node.TrueExpression) is PrimitiveExpression || yesType.Type.IsKnownType(narrowCode);
            Expression constant = narrowFirst ? node.TrueExpression : node.FalseExpression;
            Expression other = narrowFirst ? node.FalseExpression : node.TrueExpression;
            var narrowType = narrowFirst ? yesType : noType;
            if (!(Unwrap(constant) is PrimitiveExpression) && !narrowType.Type.IsKnownType(narrowCode)) continue;
            if (Unwrap(other) is PrimitiveExpression) continue;
            ILInstruction a = inst.TrueInst, b = inst.FalseInst;
            var narrowTarget = unsigned ? ICSharpCode.Decompiler.IL.PrimitiveType.U4 : ICSharpCode.Decompiler.IL.PrimitiveType.I4;
            bool aConverts = a is Conv && ((Conv)a).InputType == StackType.I8 && ((Conv)a).TargetType == narrowTarget;
            bool bConverts = b is Conv && ((Conv)b).InputType == StackType.I8 && ((Conv)b).TargetType == narrowTarget;
            if (aConverts == bConverts || a.ResultType != StackType.I4 || b.ResultType != StackType.I4) continue;
            if (cast.Type.ToString() != "int" && !unsigned) continue;
            if (unsigned && Unwrap(constant) is PrimitiveExpression) continue;
            var rr = Unwrap(other).GetResolveResult();
            if (rr == null || !rr.Type.IsKnownType(KnownTypeCode.Int64)) continue;
            if (Unwrap(constant) is PrimitiveExpression)
            {
                object value = ((PrimitiveExpression)Unwrap(constant)).Value;
                if (!(value is int) && !(value is long)) continue;
                constant.ReplaceWith(new PrimitiveExpression(System.Convert.ToInt32(value)));
            }
            var inner = new CastExpression();
            other.ReplaceWith(inner);
            inner.Type = cast.Type.Clone();
            inner.Expression = other;
            cast.ReplaceWith(node.Detach());
        }
    }

    // 18b. BranchCasts: 'c ? (short)(a - b) : (short)0' converts in the first branch and
    //      loads a constant in the other ('sub; conv.i2; br E; ldc.i4.0; E:'). The decompiler
    //      prints '(short)(c ? ((short)(a - b)) : 0)', a second 'conv.i2' after the branches.
    private static void BranchCasts(AstNode rootNode)
    {
        foreach (var cast in rootNode.Descendants.OfType<CastExpression>().ToList())
        {
            var node = Unwrap(cast.Expression) as ConditionalExpression;
            if (node == null || cast.Parent == null || !(cast.Type is ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveType) || cast.Annotations.OfType<Conv>().Any()) continue;
            string to = cast.Type.ToString();
            if (to != "short" && to != "ushort" && to != "byte" && to != "sbyte" && to != "char") continue;
            foreach (bool first in new[] { true, false })
            {
                var inner = Unwrap(first ? node.TrueExpression : node.FalseExpression) as CastExpression;
                Expression constant = first ? node.FalseExpression : node.TrueExpression;
                if (inner == null || inner.Type.ToString() != to || !(Unwrap(constant) is PrimitiveExpression)) continue;
                IfInstruction inst = node.Annotations.OfType<IfInstruction>().FirstOrDefault();
                if (inst == null || inst.Parent is Conv || !(inst.TrueInst is Conv ^ inst.FalseInst is Conv)) break;
                var typed = new CastExpression();
                constant.ReplaceWith(typed);
                typed.Type = cast.Type.Clone();
                typed.Expression = constant;
                cast.ReplaceWith(node.Detach());
                break;
            }
        }
    }

    // 19. BoxedFallbacks: 'a ?? b', b a struct that implements the interface a has, boxes b
    //     and nothing else. The decompiler prints '(I)(a ?? ((object)b))', a 'castclass'.
    private static void BoxedFallbacks(AstNode rootNode)
    {
        foreach (var cast in rootNode.Descendants.OfType<CastExpression>().ToList())
        {
            var node = Unwrap(cast.Expression) as BinaryOperatorExpression;
            if (node == null || cast.Parent == null || node.Operator != BinaryOperatorType.NullCoalescing) continue;
            var boxed = Unwrap(node.Right) as CastExpression;
            if (boxed == null || boxed.Type.ToString() != "object") continue;
            var left = node.Left.GetResolveResult();
            var whole = cast.GetResolveResult();
            var value = boxed.Expression.GetResolveResult();
            // (The same with System.Enum on the left and an enum on the right.)
            if (left == null || whole == null || value == null || (left.Type.Kind != TypeKind.Interface && !left.Type.IsKnownType(KnownTypeCode.Enum)) || !left.Type.Equals(whole.Type)) continue;
            if (value.Type.IsReferenceType != false || !value.Type.GetAllBaseTypes().Any(t => t.Equals(left.Type))) continue;
            boxed.Type = cast.Type.Clone();
            cast.ReplaceWith(new ParenthesizedExpression(node.Detach()));
        }
    }

    // 20. NullConditionalValues: 'x?.M() != null' (or 'x?.P != null'), M not virtual and returning a Nullable<T>,
    //     calls M with 'call' (the receiver was just tested). The decompiler prints
    //     'x != null && x.M().HasValue', a 'callvirt'. Same branches otherwise.
    private static void NullConditionalValues(AstNode rootNode)
    {
        foreach (var member in rootNode.Descendants.OfType<MemberReferenceExpression>().ToList())
        {
            if (member.MemberName != "HasValue" || member.Parent == null) continue;
            // M() or a property P: the getter is the call.
            Expression invocation = Unwrap(member.Target);
            var target = (invocation is InvocationExpression ? ((InvocationExpression)invocation).Target : invocation) as MemberReferenceExpression;
            var receiver = target == null ? null : Unwrap(target.Target) as IdentifierExpression;
            if (receiver == null || !(receiver.GetResolveResult() is ILVariableResolveResult)) continue;
            var call = invocation.Annotations.OfType<CallInstruction>().FirstOrDefault();
            if (call == null || call.OpCode != OpCode.Call) continue;
            IMethod m = call.Method;
            if (m.IsStatic || m.IsOverridable || m.DeclaringType.IsReferenceType != true || m.DeclaringType.Kind == TypeKind.Interface) continue;
            AstNode self = member;
            while (self.Parent is ParenthesizedExpression) self = self.Parent;
            var and = self.Parent as BinaryOperatorExpression;
            if (and == null || and.Operator != BinaryOperatorType.ConditionalAnd) continue;
            Expression test;
            if (and.Right == self) test = and.Left;
            else
            {
                AstNode inner = and;
                while (inner.Parent is ParenthesizedExpression) inner = inner.Parent;
                var up = inner.Parent as BinaryOperatorExpression;
                if (up == null || up.Operator != BinaryOperatorType.ConditionalAnd || up.Right != inner) continue;
                test = up.Left;
            }
            while (Unwrap(test) is BinaryOperatorExpression && ((BinaryOperatorExpression)Unwrap(test)).Operator == BinaryOperatorType.ConditionalAnd)
                test = ((BinaryOperatorExpression)Unwrap(test)).Right;
            var check = Unwrap(test) as BinaryOperatorExpression;
            if (check == null || check.Operator != BinaryOperatorType.InEquality || !(Unwrap(check.Right) is NullReferenceExpression)) continue;
            var tested = Unwrap(check.Left) as IdentifierExpression;
            if (tested == null || tested.Identifier != receiver.Identifier) continue;
            AstNode slot = test;
            while (slot.Parent is ParenthesizedExpression) slot = slot.Parent;
            var holder = slot.Parent as BinaryOperatorExpression;
            if (holder == null || holder.Operator != BinaryOperatorType.ConditionalAnd) continue;

            var marker = new NullReferenceExpression();
            member.ReplaceWith(marker);
            receiver.ReplaceWith(new UnaryOperatorExpression(UnaryOperatorType.NullConditional, receiver.Clone()));
            marker.ReplaceWith(new BinaryOperatorExpression(new UnaryOperatorExpression(UnaryOperatorType.NullConditionalRewrap, invocation.Detach()), BinaryOperatorType.InEquality, new NullReferenceExpression()));
            Expression other = holder.Left == slot ? holder.Right : holder.Left;
            holder.ReplaceWith(other.Detach());
        }
    }

    // 28. NullConditionalTests: the same for the other tests on a local or a parameter:
    //        x != null && x.M()         ->  x?.M() ?? false
    //        x != null && x.P != null   ->  x?.P != null
    //        x != null && x.M() == c    ->  x?.M() == c
    //     when M (not virtual) is called with 'call': written on 'x' it is a 'callvirt'.
    private static void NullConditionalTests(AstNode rootNode)
    {
        foreach (var check in rootNode.Descendants.OfType<BinaryOperatorExpression>().ToList())
        {
            if (check.Operator != BinaryOperatorType.InEquality || check.Parent == null || !(Unwrap(check.Right) is NullReferenceExpression)) continue;
            var tested = Unwrap(check.Left) as IdentifierExpression;
            if (tested == null || !(tested.GetResolveResult() is ILVariableResolveResult)) continue;
            AstNode slot = check;
            while (slot.Parent is ParenthesizedExpression) slot = slot.Parent;
            var holder = slot.Parent as BinaryOperatorExpression;
            if (holder == null || holder.Operator != BinaryOperatorType.ConditionalAnd) continue;
            // The operand evaluated right after the test.
            Expression next;
            if (holder.Left == slot) next = holder.Right;
            else
            {
                AstNode h = holder;
                while (h.Parent is ParenthesizedExpression) h = h.Parent;
                var up = h.Parent as BinaryOperatorExpression;
                if (up == null || up.Operator != BinaryOperatorType.ConditionalAnd || up.Left != h) continue;
                next = up.Right;
            }
            while (Unwrap(next) is BinaryOperatorExpression && ((BinaryOperatorExpression)Unwrap(next)).Operator == BinaryOperatorType.ConditionalAnd)
                next = ((BinaryOperatorExpression)Unwrap(next)).Left;
            Expression e = Unwrap(next);
            Expression access = e;
            bool compared = false, toNull = false;
            var comparison = e as BinaryOperatorExpression;
            if (comparison != null)
            {
                compared = true;
                access = Unwrap(comparison.Left);
                toNull = Unwrap(comparison.Right) is NullReferenceExpression;
                if (toNull) { if (comparison.Operator != BinaryOperatorType.InEquality) continue; }
                else
                {
                    if (comparison.Operator != BinaryOperatorType.Equality && comparison.Operator != BinaryOperatorType.GreaterThan && comparison.Operator != BinaryOperatorType.GreaterThanOrEqual
                        && comparison.Operator != BinaryOperatorType.LessThan && comparison.Operator != BinaryOperatorType.LessThanOrEqual) continue;
                    var constant = Unwrap(comparison.Right).GetResolveResult();
                    if (constant == null || !constant.IsCompileTimeConstant || constant.ConstantValue == null) continue;
                }
            }
            var invocation = access as InvocationExpression;
            var member = (invocation != null ? invocation.Target : access) as MemberReferenceExpression;
            var receiver = member == null ? null : Unwrap(member.Target) as IdentifierExpression;
            if (receiver == null || receiver.Identifier != tested.Identifier) continue;
            var call = access.Annotations.OfType<CallInstruction>().FirstOrDefault();
            if (call == null || call.OpCode != OpCode.Call) continue;
            IMethod m = call.Method;
            if (m.IsStatic || m.IsOverridable || m.DeclaringType.IsReferenceType != true || m.DeclaringType.Kind == TypeKind.Interface) continue;
            if (!compared && !m.ReturnType.IsKnownType(KnownTypeCode.Boolean)) continue;
            if (compared && toNull && m.ReturnType.IsReferenceType != true) continue;
            if (compared && !toNull && (m.ReturnType.IsReferenceType != false || m.ReturnType.IsKnownType(KnownTypeCode.NullableOfT))) continue;

            var marker = new NullReferenceExpression();
            access.ReplaceWith(marker);
            receiver.ReplaceWith(new UnaryOperatorExpression(UnaryOperatorType.NullConditional, receiver.Clone()));
            Expression wrapped = new UnaryOperatorExpression(UnaryOperatorType.NullConditionalRewrap, access);
            if (!compared) wrapped = new ParenthesizedExpression(new BinaryOperatorExpression(wrapped, BinaryOperatorType.NullCoalescing, new PrimitiveExpression(false)));
            marker.ReplaceWith(wrapped);
            Expression other = holder.Left == slot ? holder.Right : holder.Left;
            holder.ReplaceWith(other.Detach());
        }
    }

    // 29. UnsignedNegations: '-(int)u' is a 'neg'. The decompiler prints '(int)(0 - u)', a 'sub'.
    private static void UnsignedNegations(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var node in rootNode.Descendants.OfType<BinaryOperatorExpression>().ToList())
        {
            if (node.Operator != BinaryOperatorType.Subtract || node.Parent == null) continue;
            long zero;
            if (!(Unwrap(node.Left) is PrimitiveExpression) || !Constant(node.Left, out zero) || zero != 0) continue;
            var sub = node.Annotations.OfType<BinaryNumericInstruction>().FirstOrDefault();
            if (sub == null || sub.Operator != BinaryNumericOperator.Sub) continue;
            RawIL raw = Body(MethodOf(sub), context, bodies);
            int end = RawIL.EndOf(sub.Right);
            RawIL.Op op;
            if (raw == null || end < 0 || !raw.IsAt(end, out op) || op.Code != ILOpCode.Neg) continue;
            var rr = node.Right.GetResolveResult();
            if (rr == null || !rr.Type.IsKnownType(KnownTypeCode.UInt32)) continue;
            AstNode outer = node;
            while (outer.Parent is ParenthesizedExpression) outer = outer.Parent;
            var cast = outer.Parent as CastExpression;
            if (cast == null || cast.Type.ToString() != "int") continue;
            Expression operand = node.Right.Detach();
            cast.ReplaceWith(new UnaryOperatorExpression(UnaryOperatorType.Minus, new CastExpression(new ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveType("int"), operand)));
        }
    }

    // 30. BranchConstants: 'c ? (sbyte)0 : (sbyte)1' stores one of two constants. The decompiler
    //     prints '(sbyte)(c ? 0 : 1)', which converts after the branches ('conv.i1').
    private static void BranchConstants(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var cast in rootNode.Descendants.OfType<CastExpression>().ToList())
        {
            var node = Unwrap(cast.Expression) as ConditionalExpression;
            if (node == null || cast.Parent == null) continue;
            string to = cast.Type.ToString();
            if (to != "sbyte" && to != "byte" && to != "short" && to != "ushort") continue;
            var yes = Unwrap(node.TrueExpression) as PrimitiveExpression;
            var no = Unwrap(node.FalseExpression) as PrimitiveExpression;
            if (yes == null || no == null || !(yes.Value is int) || !(no.Value is int)) continue;
            IfInstruction inst = node.Annotations.OfType<IfInstruction>().FirstOrDefault() ?? cast.Annotations.OfType<IfInstruction>().FirstOrDefault();
            if (inst == null || !(inst.TrueInst is LdcI4) || !(inst.FalseInst is LdcI4)) continue;
            RawIL raw = Body(MethodOf(inst), context, bodies);
            int end = System.Math.Max(RawIL.EndOf(inst.TrueInst), RawIL.EndOf(inst.FalseInst));
            RawIL.Op op;
            if (raw == null || end < 0 || !raw.IsAt(end, out op)) continue;
            if (op.Code == ILOpCode.Conv_i1 || op.Code == ILOpCode.Conv_u1 || op.Code == ILOpCode.Conv_i2 || op.Code == ILOpCode.Conv_u2) continue;
            // The constant that comes last in the IL ends just before 'op': both were read.
            yes.ReplaceWith(new CastExpression(cast.Type.Clone(), new PrimitiveExpression(yes.Value)));
            no.ReplaceWith(new CastExpression(cast.Type.Clone(), new PrimitiveExpression(no.Value)));
            cast.ReplaceWith(new ParenthesizedExpression(node.Detach()));
        }
    }

    // (31, LiftedConstants, is gone: the constant of a lifted comparison written where it
    // stands is read by the matcher, docs/matching.md.)

    // 32. IdentityCasts: '((T)x).P' on a local x of type T copies x first ('ldloc x; stloc t;
    //     ldloca t'). The decompiler prints the cast when the two types differ only by their
    //     nullable annotations; the IL reads x in place ('ldloca x').
    private static void IdentityCasts(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var cast in rootNode.Descendants.OfType<CastExpression>().ToList())
        {
            AstNode outer = cast;
            while (outer.Parent is ParenthesizedExpression) outer = outer.Parent;
            var member = outer.Parent as MemberReferenceExpression;
            var local = Unwrap(cast.Expression) as IdentifierExpression;
            if (member == null || member.Target != outer || local == null) continue;
            var variable = local.GetResolveResult() as ILVariableResolveResult;
            var to = cast.GetResolveResult();
            if (variable == null || to == null || variable.Type.IsReferenceType != false) continue;
            if (variable.Type.ReflectionName != to.Type.ReflectionName) continue;
            // The decompiler reads the variable through a copy of its own ('addressof(ldloc x)')
            // where the IL has 'ldloca x'.
            bool inPlace = false;
            // The copy has no range of its own: the call that reads it tells ('ldloca x; call get_P').
            AstNode reader = member.Parent is InvocationExpression && ((InvocationExpression)member.Parent).Target == member ? member.Parent : member;
            foreach (var call in reader.Annotations.OfType<CallInstruction>())
            {
                if (call.Arguments.Count != 1 || !(call.Arguments[0] is AddressOf)) continue;
                RawIL raw = Body(MethodOf(call), context, bodies);
                RawIL.Op op, before;
                foreach (var r in call.ILRanges)
                {
                    if (raw == null || r.Start == r.End || !raw.IsAt(r.Start, out op) || !RawIL.IsCall(op.Code) || !raw.Near(r.Start, -1, out before)) continue;
                    if (before.Code != ILOpCode.Ldloca && before.Code != ILOpCode.Ldloca_s) continue;
                    // The variable of the decompiler may have lost its slot number: its type then.
                    IType slot = raw.LocalType(before.Operand);
                    if (variable.Variable.Index.HasValue ? before.Operand == variable.Variable.Index.Value : slot != null && slot.ReflectionName == variable.Type.ReflectionName) inPlace = true;
                }
            }
            if (!inPlace) continue;
            outer.ReplaceWith(local.Detach());
        }
    }

    // 33. GenericNullConditionals: 'e?.M()' on a value of a type parameter reads it through a
    //     temporary ('e; stloc t; ldloca t; dup; ldobj T; box T; brtrue L; pop; d; br E; L:
    //     constrained. T; callvirt M'). The decompiler prints 'T t = e; ... (t != null) ?
    //     t.M() : d', which tests a boxed copy and calls on the variable.
    private static void GenericNullConditionals(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var declaration in rootNode.Descendants.OfType<VariableDeclarationStatement>().ToList())
        {
            if (declaration.Variables.Count != 1 || declaration.Parent == null) continue;
            VariableInitializer variable = declaration.Variables.First();
            if (variable.Initializer.IsNull) continue;
            ILVariable v = null;
            foreach (var rr in variable.Annotations.OfType<ILVariableResolveResult>()) v = rr.Variable;
            if (v == null || !v.Index.HasValue || v.Kind != VariableKind.Local || v.Type.Kind != TypeKind.TypeParameter || v.Function == null || v.Function.Method == null) continue;
            var entity = declaration.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            var next = declaration.NextSibling as Statement;
            if (entity == null || next == null) continue;
            var uses = entity.Descendants.OfType<IdentifierExpression>().Where(i => i.Identifier == variable.Name).ToList();
            if (uses.Count != 2 || !uses.All(u => u.Ancestors.Contains(next))) continue;
            // The slot: stored once, read once by address, with the test right behind.
            RawIL raw = Body(v.Function.Method, context, bodies);
            if (raw == null) continue;
            int stores = 0, reads = 0, at = -1;
            for (int i = 0; i < raw.Ops.Count; i++)
            {
                RawIL.Op op = raw.Ops[i];
                if (RawIL.IsStLoc(op.Code) && op.Operand == v.Index.Value) { stores++; at = i; }
                else if ((RawIL.IsLdLoc(op.Code) || op.Code == ILOpCode.Ldloca || op.Code == ILOpCode.Ldloca_s) && op.Operand == v.Index.Value) reads++;
            }
            if (stores != 1 || reads != 1 || at + 5 >= raw.Ops.Count) continue;
            if (raw.Ops[at + 1].Code != ILOpCode.Ldloca && raw.Ops[at + 1].Code != ILOpCode.Ldloca_s) continue;
            if (raw.Ops[at + 2].Code != ILOpCode.Dup || raw.Ops[at + 3].Code != ILOpCode.Ldobj || raw.Ops[at + 4].Code != ILOpCode.Box) continue;
            if (raw.Ops[at + 5].Code != ILOpCode.Brtrue && raw.Ops[at + 5].Code != ILOpCode.Brtrue_s) continue;

            // '(t != null) ? t.M(a) : d', printed with the test either way round.
            var choice = uses[0].Ancestors.OfType<ConditionalExpression>().FirstOrDefault();
            if (choice == null || !uses[1].Ancestors.Contains(choice)) continue;
            Expression condition = Unwrap(choice.Condition);
            bool negated = false;
            var not = condition as UnaryOperatorExpression;
            if (not != null && not.Operator == UnaryOperatorType.Not) { negated = true; condition = Unwrap(not.Expression); }
            var test = condition as BinaryOperatorExpression;
            if (test == null || !(Unwrap(test.Right) is NullReferenceExpression) || !(Unwrap(test.Left) is IdentifierExpression)) continue;
            if (test.Operator == BinaryOperatorType.Equality) negated = !negated;
            else if (test.Operator != BinaryOperatorType.InEquality) continue;
            Expression whenSet = negated ? choice.FalseExpression : choice.TrueExpression;
            Expression whenNull = negated ? choice.TrueExpression : choice.FalseExpression;
            var call = Unwrap(whenSet) as InvocationExpression;
            var target = call == null ? null : call.Target as MemberReferenceExpression;
            var receiver = target == null ? null : Unwrap(target.Target) as IdentifierExpression;
            if (receiver == null || receiver.Identifier != variable.Name) continue;
            if (call.Arguments.Any(a => a.DescendantsAndSelf.OfType<IdentifierExpression>().Any(i => i.Identifier == variable.Name))) continue;

            Expression fallback = whenNull.Detach();
            call.Remove();
            receiver.ReplaceWith(new UnaryOperatorExpression(UnaryOperatorType.NullConditional, new ParenthesizedExpression(variable.Initializer.Detach())));
            Expression value = new UnaryOperatorExpression(UnaryOperatorType.NullConditionalRewrap, call);
            if (!(Unwrap(fallback) is NullReferenceExpression)) value = new ParenthesizedExpression(new BinaryOperatorExpression(value, BinaryOperatorType.NullCoalescing, fallback));
            choice.ReplaceWith(value);
            declaration.Remove();
        }
    }

    // 34. UnboxedPatterns: 'if (o is T x) return x;' on a value type stores the unboxed value
    //     and reads it back ('unbox.any T; stloc x; ldloc x'). The decompiler prints
    //     'if (o is T) return (T)o;', which keeps it on the stack.
    private static void UnboxedPatterns(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var test in rootNode.Descendants.OfType<IfElseStatement>().ToList())
        {
            var check = Unwrap(test.Condition) as IsExpression;
            var subject = check == null ? null : Unwrap(check.Expression) as IdentifierExpression;
            if (subject == null || test.Parent == null) continue;
            string type = check.Type.ToString();
            var casts = test.TrueStatement.Descendants.OfType<CastExpression>().Where(c => c.Type.ToString() == type && Unwrap(c.Expression) is IdentifierExpression
                && ((IdentifierExpression)Unwrap(c.Expression)).Identifier == subject.Identifier).ToList();
            if (casts.Count != 1) continue;
            var unbox = casts[0].Annotations.OfType<UnboxAny>().FirstOrDefault();
            if (unbox == null) continue;
            RawIL raw = Body(MethodOf(unbox), context, bodies);
            RawIL.Op store = default(RawIL.Op), load;
            bool kept = false;
            if (raw != null)
                foreach (var r in unbox.ILRanges)
                {
                    RawIL.Op op;
                    if (r.Start == r.End || !raw.IsAt(r.Start, out op) || op.Code != ILOpCode.Unbox_any) continue;
                    if (raw.Near(r.Start, 1, out store) && RawIL.IsStLoc(store.Code) && raw.Near(r.Start, 2, out load) && RawIL.IsLdLoc(load.Code) && load.Operand == store.Operand) kept = true;
                }
            if (!kept || raw.Ops.Count(o => RawIL.IsStLoc(o.Code) && o.Operand == store.Operand) != 1) continue;
            var entity = test.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (entity == null) continue;
            string name = FreshName(entity, "unboxed");
            var declaration = new DeclarationExpression { Type = check.Type.Detach(), Designation = new SingleVariableDesignation { Identifier = name } };
            Expression value = check.Expression.Detach();
            check.ReplaceWith(new BinaryOperatorExpression(value, BinaryOperatorType.IsPattern, declaration));
            casts[0].ReplaceWith(new IdentifierExpression(name));
        }
    }

    // 21. ImplicitConversions: 's != 1.0', s of a class that converts implicitly to double,
    //     is 'call op_Implicit' and nothing else. The decompiler prints '(double)s != 1.0', and
    //     the compiler adds a 'conv.r8' to a conversion written as a cast.
    private static void ImplicitConversions(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var cast in rootNode.Descendants.OfType<CastExpression>().ToList())
        {
            if (!(cast.Parent is BinaryOperatorExpression)) continue;
            string to = cast.Type.ToString();
            if (to != "double" && to != "float") continue;
            var call = cast.Annotations.OfType<Call>().FirstOrDefault();
            if (call == null || call.Method.Name != "op_Implicit" || call.Method.Parameters.Count != 1) continue;
            ITypeDefinition owner = call.Method.DeclaringTypeDefinition;
            if (owner == null || owner.Methods.Count(m => m.Name == "op_Implicit" && m.Parameters.Count == 1 && owner.Equals(m.Parameters[0].Type.GetDefinition())) != 1) continue;
            RawIL raw = Body(MethodOf(call), context, bodies);
            int end = RawIL.EndOf(call);
            RawIL.Op next, before;
            if (raw == null || end < 0 || !raw.IsAt(end, out next) || !raw.Near(end, -1, out before) || before.Code != ILOpCode.Call) continue;
            if (next.Code == ILOpCode.Conv_r8 || next.Code == ILOpCode.Conv_r4) continue;
            cast.ReplaceWith(cast.Expression.Detach());
        }
    }

    // 22. EnumSlots: 'E x = c ? E.A : E.B; F(); new T(x);' keeps x on the stack. The decompiler
    //     declares the stack slot as an int and converts it where it is read: a 'conv' more
    //     when E is not on 32 bits.
    private static void EnumSlots(AstNode rootNode, TransformContext context)
    {
        foreach (var declaration in rootNode.Descendants.OfType<VariableDeclarationStatement>().ToList())
        {
            if (declaration.Variables.Count != 1 || declaration.Parent == null || declaration.Type.ToString() != "int") continue;
            VariableInitializer variable = declaration.Variables.First();
            ILVariable v = null;
            foreach (var rr in variable.Annotations.OfType<ILVariableResolveResult>()) v = rr.Variable;
            if (v == null || v.Kind != VariableKind.StackSlot) continue;
            var choice = Unwrap(variable.Initializer) as ConditionalExpression;
            if (choice == null) continue;
            var yes = Unwrap(choice.TrueExpression) as PrimitiveExpression;
            var no = Unwrap(choice.FalseExpression) as PrimitiveExpression;
            if (yes == null || no == null || !(yes.Value is int) || !(no.Value is int)) continue;
            var entity = declaration.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (entity == null) continue;
            var uses = entity.Descendants.OfType<IdentifierExpression>().Where(i => i.Identifier == variable.Name).ToList();
            if (uses.Count != 1) continue;
            var cast = uses[0].Parent as CastExpression;
            var type = cast == null ? null : cast.GetResolveResult();
            if (type == null || type.Type.Kind != TypeKind.Enum) continue;
            System.TypeCode code = ReflectionHelper.GetTypeCode(type.Type.GetEnumUnderlyingType());
            object a, b;
            try
            {
                a = System.Convert.ChangeType(yes.Value, code, System.Globalization.CultureInfo.InvariantCulture);
                b = System.Convert.ChangeType(no.Value, code, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (System.Exception) { continue; }
            yes.ReplaceWith(context.TypeSystemAstBuilder.ConvertConstantValue(type.Type, a));
            no.ReplaceWith(context.TypeSystemAstBuilder.ConvertConstantValue(type.Type, b));
            declaration.Type = cast.Type.Clone();
            cast.ReplaceWith(uses[0].Detach());
        }
    }

    // 23. NullableDefaults: 'n.GetValueOrDefault()' on a 'bool?' is that call alone. The
    //     decompiler prints 'n == true', which also tests HasValue.
    private static void NullableDefaults(AstNode rootNode)
    {
        foreach (var node in rootNode.Descendants.OfType<BinaryOperatorExpression>().ToList())
        {
            // 'n != true' for the negated call: '!n.GetValueOrDefault()'.
            bool negated = node.Operator == BinaryOperatorType.InEquality;
            if ((node.Operator != BinaryOperatorType.Equality && !negated) || node.Parent == null) continue;
            var constant = Unwrap(node.Right) as PrimitiveExpression;
            if (constant == null || !(constant.Value is bool) || !(bool)constant.Value) continue;
            var insts = node.Annotations.OfType<ILInstruction>().ToList();
            if (insts.Count != 1 || !(insts[0] is Call)) continue;
            IMethod m = ((Call)insts[0]).Method;
            if (m.Name != "GetValueOrDefault" || m.Parameters.Count != 0 || !m.DeclaringType.IsKnownType(KnownTypeCode.NullableOfT)) continue;
            Expression left = node.Left.Detach();
            if (!(left is IdentifierExpression) && !(left is MemberReferenceExpression)) left = new ParenthesizedExpression(left);
            Expression value = new InvocationExpression(new MemberReferenceExpression(left, "GetValueOrDefault"));
            if (negated) value = new UnaryOperatorExpression(UnaryOperatorType.Not, value);
            node.ReplaceWith(value);
        }
    }

    // 24. EmptyCompounds: 'a[i] += 0' on a byte keeps only the conversion of a compound
    //     assignment ('ldelema; dup; ldind.u1; conv.u1; stind.i1'). The decompiler prints
    //     'ref byte r = ref a[i]; r = r;', which has no conversion.
    private static void EmptyCompounds(AstNode rootNode)
    {
        foreach (var declaration in rootNode.Descendants.OfType<VariableDeclarationStatement>().ToList())
        {
            if (declaration.Variables.Count != 1 || declaration.Parent == null) continue;
            VariableInitializer variable = declaration.Variables.First();
            var address = Unwrap(variable.Initializer) as DirectionExpression;
            var copy = declaration.NextSibling as ExpressionStatement;
            var assignment = copy == null ? null : copy.Expression as AssignmentExpression;
            if (address == null || assignment == null || assignment.Operator != AssignmentOperatorType.Assign) continue;
            var left = Unwrap(assignment.Left) as IdentifierExpression;
            var right = Unwrap(assignment.Right) as IdentifierExpression;
            if (left == null || right == null || left.Identifier != variable.Name || right.Identifier != variable.Name) continue;
            var entity = declaration.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (entity == null || entity.Descendants.OfType<IdentifierExpression>().Count(i => i.Identifier == variable.Name) != 2) continue;
            string type = declaration.Type.ToString();
            if (type != "ref byte" && type != "ref sbyte" && type != "ref short" && type != "ref ushort") continue;
            // The store converts what it read.
            var store = assignment.Annotations.OfType<StObj>().FirstOrDefault();
            if (store == null || !(store.Value is Conv) || !(((Conv)store.Value).Argument is LdObj)) continue;
            copy.Expression = new AssignmentExpression(address.Expression.Detach(), AssignmentOperatorType.Add, new PrimitiveExpression(0));
            declaration.Remove();
        }
    }

    private static string FreshName(EntityDeclaration entity, string wanted)
    {
        var taken = new HashSet<string>(entity.Descendants.OfType<Identifier>().Select(i => i.Name));
        if (!taken.Contains(wanted)) return wanted;
        for (int n = 2; ; n++) if (!taken.Contains(wanted + n)) return wanted + n;
    }

    // 25. DictionaryEnumerators: 'IDictionaryEnumerator e = d.GetEnumerator(); try { while
    //     (e.MoveNext()) { DictionaryEntry x = e.Entry; ... } } finally { (e as IDisposable)?.Dispose(); }'
    //     reads 'get_Entry'. The decompiler prints 'foreach (DictionaryEntry x in d)', which
    //     reads 'get_Current' and unboxes it.
    private static void DictionaryEnumerators(AstNode rootNode, TransformContext context)
    {
        foreach (var entity in rootNode.DescendantsAndSelf.OfType<EntityDeclaration>().ToList())
        {
            if (!(entity is MethodDeclaration) && !(entity is Accessor) && !(entity is ConstructorDeclaration)) continue;
            var loops = entity.Descendants.OfType<ForeachStatement>().Where(l => l.VariableType.ToString().EndsWith("DictionaryEntry")).ToList();
            if (loops.Count == 0 || loops.Any(l => l.Ancestors.Any(a => a is LambdaExpression || a is AnonymousMethodExpression))) continue;
            var method = entity.GetSymbol() as IMethod;
            RawIL raw = method == null ? null : RawIL.Read(context.TypeSystem, method);
            if (raw == null) continue;
            int entries = 0;
            foreach (RawIL.Op op in raw.Ops)
            {
                IMethod callee = op.Code == ILOpCode.Callvirt ? raw.Method(op) : null;
                if (callee != null && callee.Name == "get_Entry" && callee.DeclaringType.FullName == "System.Collections.IDictionaryEnumerator") entries++;
            }
            if (entries != loops.Count) continue;
            foreach (ForeachStatement loop in loops)
            {
                var designation = loop.VariableDesignation as SingleVariableDesignation;
                var body = loop.EmbeddedStatement as BlockStatement;
                if (designation == null || body == null) continue;
                string name = FreshName(entity, "enumerator");
                AstType collections = new MemberType(new SimpleType("System"), "Collections");
                var declaration = new VariableDeclarationStatement(new MemberType(collections, "IDictionaryEnumerator"), name,
                    new InvocationExpression(new MemberReferenceExpression(loop.InExpression.Detach(), "GetEnumerator")));
                body.Remove();
                var first = new VariableDeclarationStatement(loop.VariableType.Detach(), designation.Identifier, new MemberReferenceExpression(new IdentifierExpression(name), "Entry"));
                if (body.Statements.Count == 0) body.Statements.Add(first);
                else body.Statements.InsertBefore(body.Statements.First(), first);
                var loopStatement = new WhileStatement(new InvocationExpression(new MemberReferenceExpression(new IdentifierExpression(name), "MoveNext")), body);
                var disposable = new AsExpression(new IdentifierExpression(name), new MemberType(new SimpleType("System"), "IDisposable"));
                var dispose = new UnaryOperatorExpression(UnaryOperatorType.NullConditionalRewrap,
                    new InvocationExpression(new MemberReferenceExpression(new UnaryOperatorExpression(UnaryOperatorType.NullConditional, new ParenthesizedExpression(disposable)), "Dispose")));
                var protect = new TryCatchStatement();
                protect.TryBlock = new BlockStatement { loopStatement };
                protect.FinallyBlock = new BlockStatement { new ExpressionStatement(dispose) };
                // The loop may be the embedded statement of an 'if' without a block.
                var parent = loop.Parent as BlockStatement;
                if (parent == null) { loop.ReplaceWith(new BlockStatement { declaration, protect }); continue; }
                parent.Statements.InsertBefore(loop, declaration);
                loop.ReplaceWith(protect);
            }
        }
    }

    // 26. StringLoops: 'for (int i = 0; i < s.Length; i++) { char c = s[i]; ... }' reads s
    //     each time. The decompiler prints 'foreach (char c in s)', which copies s first.
    private static void StringLoops(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var loop in rootNode.Descendants.OfType<ForeachStatement>().ToList())
        {
            var designation = loop.VariableDesignation as SingleVariableDesignation;
            var body = loop.EmbeddedStatement as BlockStatement;
            var source = Unwrap(loop.InExpression) as IdentifierExpression;
            var parent = loop.Parent as BlockStatement;
            if (designation == null || body == null || source == null || parent == null || loop.VariableType.ToString() != "char") continue;
            var rr = source.GetResolveResult();
            if (rr == null || !rr.Type.IsKnownType(KnownTypeCode.String)) continue;
            var load = source.Annotations.OfType<LdLoc>().FirstOrDefault();
            if (load == null) continue;
            RawIL raw = Body(MethodOf(load), context, bodies);
            int start = RawIL.StartOf(load);
            RawIL.Op first, read;
            // The loop test reads the length of s itself ('s; callvirt get_Length'): a
            // 'foreach' reads its copy, and its collection is the 's' stored in the copy.
            if (raw == null || start < 0 || !raw.IsAt(start, out first) || !(RawIL.IsLdArg(first.Code) || RawIL.IsLdLoc(first.Code))) continue;
            if (!raw.Near(start, 1, out read) || read.Code != ILOpCode.Callvirt) continue;
            IMethod callee = raw.Method(read);
            if (callee == null || callee.Name != "get_Length" || !callee.DeclaringType.IsKnownType(KnownTypeCode.String)) continue;
            var entity = loop.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (entity == null) continue;
            string i = FreshName(entity, "i");
            body.Remove();
            var element = new VariableDeclarationStatement(loop.VariableType.Detach(), designation.Identifier, new IndexerExpression(source.Clone(), new IdentifierExpression(i)));
            if (body.Statements.Count == 0) body.Statements.Add(element);
            else body.Statements.InsertBefore(body.Statements.First(), element);
            var counted = new ForStatement();
            counted.Initializers.Add(new VariableDeclarationStatement(new ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveType("int"), i, new PrimitiveExpression(0)));
            counted.Condition = new BinaryOperatorExpression(new IdentifierExpression(i), BinaryOperatorType.LessThan, new MemberReferenceExpression(source.Clone(), "Length"));
            counted.Iterators.Add(new ExpressionStatement(new UnaryOperatorExpression(UnaryOperatorType.PostIncrement, new IdentifierExpression(i))));
            counted.EmbeddedStatement = body;
            loop.ReplaceWith(counted);
        }
    }

    // 27. NullableEnumMasks: '(n & E.A) == E.A' on a nullable enum. The decompiler prints
    //     '((uint?)n & 2u) == 2', a comparison of Nullable<uint> values.
    private static void NullableEnumMasks(AstNode rootNode, TransformContext context)
    {
        foreach (var node in rootNode.Descendants.OfType<BinaryOperatorExpression>().ToList())
        {
            if (node.Operator != BinaryOperatorType.Equality && node.Operator != BinaryOperatorType.InEquality) continue;
            var mask = Unwrap(node.Left) as BinaryOperatorExpression;
            var wanted = Unwrap(node.Right) as PrimitiveExpression;
            if (mask == null || wanted == null || (mask.Operator != BinaryOperatorType.BitwiseAnd && mask.Operator != BinaryOperatorType.BitwiseOr)) continue;
            var cast = Unwrap(mask.Left) as CastExpression;
            var bits = Unwrap(mask.Right) as PrimitiveExpression;
            if (cast == null || bits == null) continue;
            string to = cast.Type.ToString();
            if (to != "uint?" && to != "int?") continue;
            var rr = cast.Expression.GetResolveResult();
            if (rr == null || !rr.Type.IsKnownType(KnownTypeCode.NullableOfT) || rr.Type.TypeArguments.Count != 1 || rr.Type.TypeArguments[0].Kind != TypeKind.Enum) continue;
            IType type = rr.Type.TypeArguments[0];
            System.TypeCode code = ReflectionHelper.GetTypeCode(type.GetEnumUnderlyingType());
            object a, b;
            try
            {
                a = System.Convert.ChangeType(bits.Value, code, System.Globalization.CultureInfo.InvariantCulture);
                b = System.Convert.ChangeType(wanted.Value, code, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (System.Exception) { continue; }
            bits.ReplaceWith(context.TypeSystemAstBuilder.ConvertConstantValue(type, a));
            wanted.ReplaceWith(context.TypeSystemAstBuilder.ConvertConstantValue(type, b));
            cast.ReplaceWith(cast.Expression.Detach());
        }
    }

    // 12. ExplicitCompounds: 'x.f = x.f + v' (RestoreExpressions marks them), which the
    //     decompiler prints 'x.f += v'.
    private static void ExplicitCompounds(AstNode rootNode)
    {
        foreach (var node in rootNode.Descendants.OfType<AssignmentExpression>().ToList())
        {
            if (node.Operator == AssignmentOperatorType.Assign) continue;
            string marker;
            // Marked by RestoreExpressions, or a store the decompiler itself read as explicit
            // ('x.s.f = x.s.f * v': the address of x.s taken twice) and folded when printing.
            bool written = false;
            foreach (var store in node.Annotations.OfType<StObj>())
            {
                var operation = store.Value as BinaryNumericInstruction;
                var target = store.Target as LdFlda;
                if (operation == null || target == null || !(operation.Left is LdObj) || !(target.Target is LdFlda)) continue;
                ITypeDefinition def = target.Field.Type.GetDefinition();
                if (def == null) continue;
                switch (def.KnownTypeCode)
                {
                    case KnownTypeCode.Int32: case KnownTypeCode.UInt32: case KnownTypeCode.Int64: case KnownTypeCode.UInt64:
                    case KnownTypeCode.Single: case KnownTypeCode.Double:
                        written = true;
                        break;
                }
            }
            if (!written && !node.Annotations.OfType<ILInstruction>().Any(i => RestoreExpressions.Explicit.TryGetValue(i, out marker))) continue;
            BinaryOperatorType op;
            switch (node.Operator)
            {
                case AssignmentOperatorType.Add: op = BinaryOperatorType.Add; break;
                case AssignmentOperatorType.Subtract: op = BinaryOperatorType.Subtract; break;
                case AssignmentOperatorType.Multiply: op = BinaryOperatorType.Multiply; break;
                case AssignmentOperatorType.Divide: op = BinaryOperatorType.Divide; break;
                case AssignmentOperatorType.Modulus: op = BinaryOperatorType.Modulus; break;
                case AssignmentOperatorType.ShiftLeft: op = BinaryOperatorType.ShiftLeft; break;
                case AssignmentOperatorType.ShiftRight: op = BinaryOperatorType.ShiftRight; break;
                case AssignmentOperatorType.BitwiseAnd: op = BinaryOperatorType.BitwiseAnd; break;
                case AssignmentOperatorType.BitwiseOr: op = BinaryOperatorType.BitwiseOr; break;
                case AssignmentOperatorType.ExclusiveOr: op = BinaryOperatorType.ExclusiveOr; break;
                default: continue;
            }
            Expression right = node.Right.Detach();
            node.Operator = AssignmentOperatorType.Assign;
            node.Right = new BinaryOperatorExpression(node.Left.Clone(), op, right);
        }
    }

    // 13. ValueReceivers: '((int)e).ToString(p)' evaluates e where it stands and calls the
    //     method on a temporary ('e; stloc t; ldloca t; call'). The decompiler declares the
    //     temporary before the statement, which moves e in front of everything else in it.
    private static void ValueReceivers(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var declaration in rootNode.Descendants.OfType<VariableDeclarationStatement>().ToList())
        {
            if (declaration.Variables.Count != 1 || declaration.Parent == null) continue;
            VariableInitializer variable = declaration.Variables.First();
            if (variable.Initializer.IsNull) continue;
            ILVariable v = null;
            foreach (var rr in variable.Annotations.OfType<ILVariableResolveResult>()) v = rr.Variable;
            if (v == null || !v.Index.HasValue || v.Kind != VariableKind.Local || v.Function == null || v.Function.Method == null) continue;
            if (v.Type.IsReferenceType != false || v.StoreCount != 1) continue;
            var next = declaration.NextSibling as Statement;
            var entity = declaration.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (next == null || entity == null) continue;
            var uses = entity.Descendants.OfType<IdentifierExpression>().Where(i => i.Identifier == variable.Name).ToList();
            if (uses.Count != 1 || !uses[0].Ancestors.Contains(next)) continue;
            var access = uses[0].Parent as MemberReferenceExpression;
            if (MarshalByRefOperand(variable, v, uses[0], context, bodies)) { declaration.Remove(); continue; }
            if (access == null || access.Target != uses[0] || !(access.Parent is InvocationExpression) || ((InvocationExpression)access.Parent).Target != access) continue;
            // In the IL: one store, read back at once by address, and nowhere else.
            RawIL raw = Body(v.Function.Method, context, bodies);
            if (raw == null) continue;
            int stores = 0, reads = 0, at = -1;
            for (int i = 0; i < raw.Ops.Count; i++)
            {
                RawIL.Op op = raw.Ops[i];
                if (RawIL.IsStLoc(op.Code) && op.Operand == v.Index.Value) { stores++; at = i; }
                else if ((RawIL.IsLdLoc(op.Code) || op.Code == ILOpCode.Ldloca || op.Code == ILOpCode.Ldloca_s) && op.Operand == v.Index.Value) reads++;
            }
            if (stores != 1 || reads != 1 || at + 1 >= raw.Ops.Count) continue;
            RawIL.Op read = raw.Ops[at + 1];
            if ((read.Code != ILOpCode.Ldloca && read.Code != ILOpCode.Ldloca_s) || read.Operand != v.Index.Value) continue;
            // A variable or a field has an address of its own: no temporary there.
            Expression initial = Unwrap(variable.Initializer);
            bool computed = initial is CastExpression || initial is InvocationExpression || initial is BinaryOperatorExpression || initial is UnaryOperatorExpression || initial is ConditionalExpression || initial is ObjectCreateExpression;
            if (initial is MemberReferenceExpression && initial.GetSymbol() is IProperty) computed = true;
            if (!computed) continue;
            Expression value = variable.Initializer.Detach();
            var valueType = value.GetResolveResult();
            if (!(value is CastExpression) && (valueType == null || !valueType.Type.Equals(v.Type)))
                value = new CastExpression(context.TypeSystemAstBuilder.ConvertType(v.Type), value);
            uses[0].ReplaceWith(new ParenthesizedExpression(value));
            declaration.Remove();
        }
    }

    // 15. MarshalByRefOperand: '"a" + x.f', f a field of primitive type in a class derived
    //     from MarshalByRefObject. The compiler does not take the address of such a field
    //     to convert it: 'x.f; stloc t; ldloca t; constrained. T; callvirt Object::ToString'.
    //     The decompiler declares t before the statement; an explicit 'x.f.ToString()' is
    //     'ldflda f; call T::ToString' (ConcatToString).
    private static bool MarshalByRefOperand(VariableInitializer variable, ILVariable v, IdentifierExpression use, TransformContext context, Dictionary<IMethod, RawIL> bodies)
    {
        var concat = use.Parent as BinaryOperatorExpression;
        if (concat == null || concat.Operator != BinaryOperatorType.Add) return false;
        if (!IsStringConcat(concat)) return false;
        Expression initial = Unwrap(variable.Initializer);
        var field = initial.GetSymbol() as IField;
        if (!(initial is MemberReferenceExpression) || field == null || field.IsStatic || field.DeclaringType == null) return false;
        if (!field.DeclaringType.GetAllBaseTypes().Any(t => t.FullName == "System.MarshalByRefObject")) return false;
        RawIL raw = Body(v.Function.Method, context, bodies);
        if (raw == null) return false;
        int stores = 0, reads = 0, at = -1;
        for (int i = 0; i < raw.Ops.Count; i++)
        {
            RawIL.Op op = raw.Ops[i];
            if (RawIL.IsStLoc(op.Code) && op.Operand == v.Index.Value) { stores++; at = i; }
            else if ((RawIL.IsLdLoc(op.Code) || op.Code == ILOpCode.Ldloca || op.Code == ILOpCode.Ldloca_s) && op.Operand == v.Index.Value) reads++;
        }
        if (stores != 1 || reads != 1 || at < 1 || at + 2 >= raw.Ops.Count) return false;
        if (raw.Ops[at - 1].Code != ILOpCode.Ldfld) return false;
        RawIL.Op read = raw.Ops[at + 1];
        if ((read.Code != ILOpCode.Ldloca && read.Code != ILOpCode.Ldloca_s) || read.Operand != v.Index.Value) return false;
        if (raw.Ops[at + 2].Code != ILOpCode.Constrained) return false;
        // 'text = text + x.f + ","': the first operand, read before x.f, was put in a stack
        // slot of its own by the decompiler.
        var before = variable.Parent.PrevSibling as VariableDeclarationStatement;
        var left = Unwrap(concat.Left) as IdentifierExpression;
        if (before != null && left != null && concat.Right == use && before.Variables.Count == 1 && before.Variables.First().Name == left.Identifier
            && Unwrap(before.Variables.First().Initializer) is IdentifierExpression)
        {
            ILVariable slot = null;
            foreach (var rr in before.Variables.First().Annotations.OfType<ILVariableResolveResult>()) slot = rr.Variable;
            var entity = use.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (slot != null && slot.Kind == VariableKind.StackSlot && entity != null
                && entity.Descendants.OfType<IdentifierExpression>().Count(i => i.Identifier == left.Identifier) == 1)
            {
                left.ReplaceWith(before.Variables.First().Initializer.Detach());
                before.Remove();
            }
        }
        use.ReplaceWith(variable.Initializer.Detach());
        return true;
    }

    // A '+' of a string concatenation: the inner nodes of 'a + b + c' carry no type.
    private static bool IsStringConcat(BinaryOperatorExpression node)
    {
        for (AstNode n = node; n is BinaryOperatorExpression && ((BinaryOperatorExpression)n).Operator == BinaryOperatorType.Add; n = n.Parent)
        {
            var rr = ((BinaryOperatorExpression)n).GetResolveResult();
            if (rr != null && rr.Type.Kind != TypeKind.Unknown) return rr.Type.IsKnownType(KnownTypeCode.String);
        }
        return false;
    }

    // 36. NullOnTheLeft: 'T v = e; if (null == v)' keeps v in its slot ('e; stloc v; ldloc v;
    //     brtrue'): null is pushed first, so the value cannot stay on the stack, and the test
    //     is then reduced to the variable alone. 'v == null' reads the stack ('e; brtrue').
    private static void NullOnTheLeft(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var node in rootNode.Descendants.OfType<BinaryOperatorExpression>().ToList())
        {
            if (node.Operator != BinaryOperatorType.Equality && node.Operator != BinaryOperatorType.InEquality) continue;
            var read = Unwrap(node.Left) as IdentifierExpression;
            if (read == null || !(Unwrap(node.Right) is NullReferenceExpression)) continue;
            var rr = read.GetResolveResult() as ILVariableResolveResult;
            ILVariable v = rr == null ? null : rr.Variable;
            if (v == null || v.Kind != VariableKind.Local || !v.Index.HasValue || v.Function == null || v.Function.Method == null) continue;
            if (v.LoadCount != 1 || v.StoreCount != 1 || v.AddressCount != 0) continue;
            RawIL raw = Body(v.Function.MoveNextMethod ?? v.Function.Method, context, bodies);
            if (raw == null) continue;
            int slot = v.Index.Value, stores = 0, loads = 0, at = -1;
            for (int i = 0; i < raw.Ops.Count; i++)
            {
                if (RawIL.IsStLoc(raw.Ops[i].Code) && raw.Ops[i].Operand == slot) { stores++; at = i; }
                else if ((RawIL.IsLdLoc(raw.Ops[i].Code) || raw.Ops[i].Code == ILOpCode.Ldloca || raw.Ops[i].Code == ILOpCode.Ldloca_s) && raw.Ops[i].Operand == slot) loads++;
            }
            if (stores != 1 || loads != 1 || at <= 0 || at + 2 >= raw.Ops.Count || raw.Ops[at - 1].Code == ILOpCode.Dup) continue;
            if (!RawIL.IsLdLoc(raw.Ops[at + 1].Code) || raw.Ops[at + 1].Operand != slot || raw.Targets.Contains(raw.Ops[at + 1].Offset)) continue;
            ILOpCode jump = raw.Ops[at + 2].Code;
            if (jump != ILOpCode.Brtrue && jump != ILOpCode.Brtrue_s && jump != ILOpCode.Brfalse && jump != ILOpCode.Brfalse_s) continue;
            Expression left = node.Left.Detach(), right = node.Right.Detach();
            node.Left = right;
            node.Right = left;
        }
    }

    // 35. BoxedConcats: 'string.Concat("a", n, "b")' boxes n and calls Concat(object, object,
    //     object) ('box T; call Concat'). The decompiler prints '"a" + n + "b"', which the
    //     pinned compiler turns into 'n.ToString()' and Concat(string, string, string): the
    //     '+' of a compiler older than Roslyn 3.3 (SmartThreadPool), or the call written out.
    private static void BoxedConcats(AstNode rootNode)
    {
        foreach (var node in rootNode.Descendants.OfType<BinaryOperatorExpression>().ToList())
        {
            if (node.Operator != BinaryOperatorType.Add || node.Parent == null) continue;
            CallInstruction call = node.Annotations.OfType<CallInstruction>().FirstOrDefault(c => c.Method.Name == "Concat" && c.Method.DeclaringType.IsKnownType(KnownTypeCode.String));
            if (call == null || call.Method.Parameters.Count == 0) continue;
            IType first = call.Method.Parameters[0].Type;
            bool objects = first.IsKnownType(KnownTypeCode.Object) || (first.Kind == TypeKind.Array && ((ArrayType)first).ElementType.IsKnownType(KnownTypeCode.Object));
            if (!objects || !call.Descendants.OfType<Box>().Any(b => b.Type.IsReferenceType == false)) continue;
            var operands = new List<Expression>();
            Expression spine = node;
            while (true)
            {
                var add = spine as BinaryOperatorExpression;
                if (add == null || add.Operator != BinaryOperatorType.Add || (add != node && add.Annotations.OfType<CallInstruction>().Any())) break;
                operands.Insert(0, add.Right);
                spine = add.Left;
            }
            operands.Insert(0, spine);
            var target = new MemberReferenceExpression(new TypeReferenceExpression(new ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveType("string")), "Concat");
            var invocation = new InvocationExpression(target);
            foreach (Expression operand in operands) invocation.Arguments.Add(Unwrap(operand).Detach());
            node.ReplaceWith(invocation);
        }
    }

    // 14. ConcatToString: '"a" + n.ToString() + "b"' evaluates n where it stands; without
    //     the call the compiler evaluates every operand first and converts afterwards. The
    //     decompiler drops the call.
    private static void ConcatToString(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var node in rootNode.Descendants.OfType<BinaryOperatorExpression>().ToList())
        {
            if (node.Operator != BinaryOperatorType.Add) continue;
            var whole = node.GetResolveResult();
            // The inner nodes of a chain have no type of their own: the one of the chain.
            for (AstNode up = node.Parent; (whole == null || whole.Type.Kind == TypeKind.Unknown || whole.Type.Kind == TypeKind.None) && up != null; up = up.Parent)
            {
                if (up is ParenthesizedExpression) continue;
                var outer = up as BinaryOperatorExpression;
                if (outer == null || outer.Operator != BinaryOperatorType.Add) break;
                whole = outer.GetResolveResult();
            }
            if (!IsStringConcat(node) && (whole == null || !whole.Type.IsKnownType(KnownTypeCode.String))) continue;
            foreach (Expression operand in new[] { node.Left, node.Right })
            {
                Expression bare = Unwrap(operand);
                if (bare is BinaryOperatorExpression && ((BinaryOperatorExpression)bare).Operator == BinaryOperatorType.Add && bare == operand) continue;
                var rr = bare.GetResolveResult();
                ITypeDefinition def = rr == null ? null : rr.Type.GetDefinition();
                if (def == null || def.KnownTypeCode < KnownTypeCode.Boolean || def.KnownTypeCode > KnownTypeCode.Double) continue;
                bool explicitCall = false;
                foreach (var inst in bare.Annotations.OfType<ILInstruction>())
                {
                    RawIL raw = Body(MethodOf(inst), context, bodies);
                    int end = RawIL.EndOf(inst);
                    RawIL.Op store, load, call;
                    // 'ldflda f; call T::ToString()': the call written on a field. Without it the
                    // field of a MarshalByRefObject is copied first (MarshalByRefOperand).
                    if (raw != null && end >= 0 && bare is MemberReferenceExpression && bare.GetSymbol() is IField && raw.IsAt(end, out call) && call.Code == ILOpCode.Call
                        && raw.Near(end, -1, out load) && load.Code == ILOpCode.Ldflda)
                    {
                        IMethod direct = raw.Method(call);
                        if (direct != null && direct.Name == "ToString" && direct.Parameters.Count == 0) explicitCall = true;
                        continue;
                    }
                    // On a parameter or a local: 'ldarga x; call ToString' where the operand stands.
                    int first = RawIL.StartOf(inst);
                    if (raw != null && first >= 0 && raw.IsAt(first, out load) && (load.Code == ILOpCode.Ldarga || load.Code == ILOpCode.Ldarga_s || load.Code == ILOpCode.Ldloca || load.Code == ILOpCode.Ldloca_s)
                        && raw.Near(first, 1, out call) && call.Code == ILOpCode.Call)
                    {
                        IMethod direct = raw.Method(call);
                        if (direct != null && direct.Name == "ToString" && direct.Parameters.Count == 0) explicitCall = true;
                    }
                    if (raw == null || end < 0 || !raw.IsAt(end, out store) || !RawIL.IsStLoc(store.Code)) continue;
                    if (!raw.Near(end, 1, out load) || (load.Code != ILOpCode.Ldloca && load.Code != ILOpCode.Ldloca_s) || load.Operand != store.Operand) continue;
                    if (!raw.Near(end, 2, out call) || call.Code != ILOpCode.Call) continue;
                    IMethod callee = raw.Method(call);
                    if (callee != null && callee.Name == "ToString" && callee.Parameters.Count == 0) explicitCall = true;
                }
                if (!explicitCall) continue;
                var holder = new NullReferenceExpression();
                operand.ReplaceWith(holder);
                Expression target = bare is IdentifierExpression || bare is MemberReferenceExpression || bare is InvocationExpression || bare is IndexerExpression ? bare.Detach() : new ParenthesizedExpression(bare.Detach());
                holder.ReplaceWith(new InvocationExpression(new MemberReferenceExpression(target, "ToString")));
            }
        }
    }

    private static RawIL Body(IMethod method, TransformContext context, Dictionary<IMethod, RawIL> bodies)
    {
        RawIL raw;
        if (method == null) return null;
        if (!bodies.TryGetValue(method, out raw)) { raw = RawIL.Read(context.TypeSystem, method); bodies[method] = raw; }
        return raw;
    }

    // 8. ThrowChecks: 'T x = e; if (x == null) throw E;' keeps the value on the stack
    //    ('e; dup; brtrue L; E; throw'). The decompiler prints 'T x = e ?? throw E;', which
    //    pops the null before throwing ('dup; brtrue L; pop; E; throw').
    private static void ThrowChecks(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var node in rootNode.Descendants.OfType<BinaryOperatorExpression>().ToList())
        {
            if (node.Operator != BinaryOperatorType.NullCoalescing) continue;
            var thrown = Unwrap(node.Right) as ThrowExpression;
            if (thrown == null) continue;
            NullCoalescingInstruction inst = node.Annotations.OfType<NullCoalescingInstruction>().FirstOrDefault();
            if (inst == null) continue;
            RawIL raw = Body(MethodOf(inst), context, bodies);
            int end = RawIL.EndOf(inst.ValueInst);
            RawIL.Op op, next;
            if (raw == null || end < 0 || !raw.IsAt(end, out op)) continue;
            if (op.Code == ILOpCode.Dup && !raw.Near(end, 1, out op)) continue;
            if (op.Code != ILOpCode.Brtrue && op.Code != ILOpCode.Brtrue_s) continue;
            RawIL.Op before;
            if (!raw.Near(op.Offset, -1, out before) || before.Code != ILOpCode.Dup) continue;
            if (!raw.Near(op.Offset, 1, out next) || next.Code == ILOpCode.Pop) continue;

            string name;
            Statement statement;
            var initializer = node.Parent as VariableInitializer;
            var assignment = node.Parent as AssignmentExpression;
            if (initializer != null && initializer.Parent is VariableDeclarationStatement && ((VariableDeclarationStatement)initializer.Parent).Variables.Count == 1)
            {
                name = initializer.Name;
                statement = (Statement)initializer.Parent;
            }
            else if (assignment != null && assignment.Operator == AssignmentOperatorType.Assign && assignment.Right == node && assignment.Left is IdentifierExpression && assignment.Parent is ExpressionStatement)
            {
                name = ((IdentifierExpression)assignment.Left).Identifier;
                statement = (Statement)assignment.Parent;
            }
            else
            {
                // In an expression: the value is the first thing its statement evaluates (the
                // stack is empty under it), 'return (e ?? throw E).M();'. It gets a local.
                AstNode top = node;
                while (true)
                {
                    AstNode p = top.Parent;
                    if (p is ParenthesizedExpression || p is MemberReferenceExpression) { top = p; continue; }
                    if (p is InvocationExpression && ((InvocationExpression)p).Target == top) { top = p; continue; }
                    // First argument of a static method (an extension method, printed later as
                    // a call on the value): nothing is evaluated before it either.
                    var call = p as InvocationExpression;
                    var callee = call == null ? null : call.GetSymbol() as IMethod;
                    if (callee != null && callee.IsStatic && call.Arguments.FirstOrDefault() == top) { top = p; continue; }
                    break;
                }
                AstNode holder = top.Parent;
                if (holder is VariableInitializer && holder.Parent is VariableDeclarationStatement && ((VariableDeclarationStatement)holder.Parent).Variables.Count == 1) holder = holder.Parent;
                else if (holder is AssignmentExpression && ((AssignmentExpression)holder).Right == top && ((AssignmentExpression)holder).Left is IdentifierExpression && holder.Parent is ExpressionStatement) holder = holder.Parent;
                else if (!(holder is ReturnStatement) && !(holder is ExpressionStatement)) continue;
                statement = (Statement)holder;
                var outer = statement.Parent as BlockStatement;
                var rr = node.Left.GetResolveResult();
                if (outer == null || rr == null || rr.Type.Kind == TypeKind.Unknown) continue;
                var entity = statement.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
                if (entity == null) continue;
                var taken = new HashSet<string>(entity.Descendants.OfType<Identifier>().Select(i => i.Name));
                name = "obj";
                for (int n = 2; taken.Contains(name); n++) name = "obj" + n;
                Expression first = Unwrap(node.Left).Detach();
                Expression error = thrown.Expression.Detach();
                node.ReplaceWith(new IdentifierExpression(name));
                outer.Statements.InsertBefore(statement, new VariableDeclarationStatement(context.TypeSystemAstBuilder.ConvertType(rr.Type), name, first));
                outer.Statements.InsertBefore(statement, new IfElseStatement(
                    new BinaryOperatorExpression(new IdentifierExpression(name), BinaryOperatorType.Equality, new NullReferenceExpression()),
                    new BlockStatement { new ThrowStatement(error) }));
                continue;
            }
            var block = statement.Parent as BlockStatement;
            if (block == null) continue;
            Expression exception = thrown.Expression.Detach();
            Expression value = Unwrap(node.Left).Detach();
            node.ReplaceWith(value);
            var check = new IfElseStatement(
                new BinaryOperatorExpression(new IdentifierExpression(name), BinaryOperatorType.Equality, new NullReferenceExpression()),
                new BlockStatement { new ThrowStatement(exception) });
            block.Statements.InsertAfter(statement, check);
        }
    }

    // 9. IntLiterals: an integer literal converted to 'float?' or 'double?' is not folded
    //    ('ldc.i4 12; conv.r4; newobj Nullable'); the decompiler prints '12f' ('ldc.r4 12').
    private static void IntLiterals(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var node in rootNode.Descendants.OfType<PrimitiveExpression>().ToList())
        {
            if (!(node.Value is float) && !(node.Value is double)) continue;
            double value = System.Convert.ToDouble(node.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (double.IsNaN(value) || value != System.Math.Floor(value) || System.Math.Abs(value) > 1000000) continue;
            bool integer = false;
            foreach (var inst in node.Annotations.OfType<ILInstruction>())
            {
                RawIL raw = Body(MethodOf(inst), context, bodies);
                if (raw == null) continue;
                foreach (var r in inst.ILRanges)
                {
                    RawIL.Op op, conv;
                    if (!raw.IsAt(r.Start, out op) || !RawIL.IsLdcI4(op.Code) || op.Operand != (int)value) continue;
                    if (!raw.Near(op.Offset, 1, out conv)) continue;
                    if (conv.Code == (node.Value is float ? ILOpCode.Conv_r4 : ILOpCode.Conv_r8)) integer = true;
                }
            }
            if (!integer) continue;
            var literal = new PrimitiveExpression((int)value);
            literal.CopyAnnotationsFrom(node);
            node.ReplaceWith(literal);
        }
    }

    // 10. InitializerOrder: field initializers run in the order of the declarations, and an
    //     auto-property with an initializer takes its place among the fields. The decompiler
    //     prints the properties after the fields: each one goes back in front of the field
    //     that the constructor initializes next.
    private static List<int> StoreOrder(RawIL raw, IDecompilerTypeSystem typeSystem, ITypeDefinition def, bool isStatic)
    {
        var order = new List<int>();
        foreach (var op in raw.Ops)
        {
            if (!isStatic && op.Code == ILOpCode.Call)
            {
                IMethod callee = raw.Method(op);
                if (callee != null && callee.IsConstructor) break;
            }
            if (op.Code != (isStatic ? ILOpCode.Stsfld : ILOpCode.Stfld)) continue;
            IField field = null;
            try { field = typeSystem.MainModule.ResolveEntity(System.Reflection.Metadata.Ecma335.MetadataTokens.EntityHandle(op.Operand), new ICSharpCode.Decompiler.TypeSystem.GenericContext(def.TypeParameters)) as IField; }
            catch (System.BadImageFormatException) { }
            if (field == null || field.DeclaringTypeDefinition == null || !field.DeclaringTypeDefinition.Equals(def)) continue;
            int token = System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(field.MemberDefinition.MetadataToken);
            if (!order.Contains(token)) order.Add(token);
        }
        return order;
    }

    // 10b. FieldOrder: the same between fields. The decompiler prints them in the order of the
    //      metadata, which is not the order of the 'stfld' / 'stsfld' of the constructor when
    //      the fields were shuffled (obfuscator runtime). The initialized fields trade places
    //      among themselves.
    private static void FieldOrder(AstNode rootNode, TransformContext context)
    {
        foreach (var type in rootNode.DescendantsAndSelf.OfType<TypeDeclaration>().ToList())
        {
            var def = type.GetSymbol() as ITypeDefinition;
            if (def == null) continue;
            foreach (bool isStatic in new[] { false, true })
            {
                var fields = new List<FieldDeclaration>();
                var tokens = new Dictionary<FieldDeclaration, int>();
                bool known = true;
                foreach (var fd in type.Members.OfType<FieldDeclaration>())
                {
                    if (fd.Variables.Count != 1 || fd.Variables.Single().Initializer.IsNull || fd.HasModifier(Modifiers.Const) || fd.HasModifier(Modifiers.Static) != isStatic) continue;
                    var f = fd.Variables.Single().GetSymbol() as IField ?? fd.GetSymbol() as IField;
                    if (f == null) { known = false; break; }
                    fields.Add(fd);
                    tokens[fd] = System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(f.MetadataToken);
                }
                if (!known || fields.Count < 2) continue;
                List<int> order = null;
                foreach (IMethod ctor in def.Methods.Where(m => m.IsConstructor && m.IsStatic == isStatic))
                {
                    RawIL raw = RawIL.Read(context.TypeSystem, ctor);
                    if (raw == null) continue;
                    var o = StoreOrder(raw, context.TypeSystem, def, isStatic);
                    if (order == null || o.Count > order.Count) order = o;
                }
                if (order == null || fields.Any(fd => !order.Contains(tokens[fd]))) continue;
                var sorted = fields.OrderBy(fd => order.IndexOf(tokens[fd])).ToList();
                if (sorted.SequenceEqual(fields)) continue;
                // Each place takes the field that is initialized at that rank.
                var places = new List<FieldDeclaration>();
                foreach (var fd in fields)
                {
                    var place = new FieldDeclaration();
                    fd.ReplaceWith(place);
                    places.Add(place);
                }
                for (int i = 0; i < places.Count; i++) places[i].ReplaceWith(sorted[i]);
            }
        }
    }

    private static void InitializerOrder(AstNode rootNode, TransformContext context)
    {
        foreach (var type in rootNode.DescendantsAndSelf.OfType<TypeDeclaration>().ToList())
        {
            var def = type.GetSymbol() as ITypeDefinition;
            if (def == null) continue;
            if (!type.Members.OfType<PropertyDeclaration>().Any(p => !p.Initializer.IsNull)) continue;
            // field token -> the declaration that initializes it
            var declared = new Dictionary<int, EntityDeclaration>();
            foreach (var member in type.Members)
            {
                var fd = member as FieldDeclaration;
                var pd = member as PropertyDeclaration;
                if (fd != null)
                {
                    var symbols = new List<ISymbol> { fd.GetSymbol() };
                    foreach (var v in fd.Variables) symbols.Add(v.GetSymbol());
                    foreach (var f in symbols.OfType<IField>())
                        declared[System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(f.MetadataToken)] = fd;
                }
                else if (pd != null && !pd.Initializer.IsNull)
                {
                    var p = pd.GetSymbol() as IProperty;
                    if (p == null) continue;
                    IField backing = def.Fields.FirstOrDefault(f => f.Name == "<" + p.Name + ">k__BackingField");
                    if (backing != null) declared[System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(backing.MetadataToken)] = pd;
                }
            }
            foreach (bool isStatic in new[] { false, true })
            {
                List<int> order = null;
                foreach (IMethod ctor in def.Methods.Where(m => m.IsConstructor && m.IsStatic == isStatic))
                {
                    RawIL raw = RawIL.Read(context.TypeSystem, ctor);
                    if (raw == null) continue;
                    var o = StoreOrder(raw, context.TypeSystem, def, isStatic);
                    if (order == null || o.Count > order.Count) order = o;
                }
                if (order == null) continue;
                EntityDeclaration lastField = null, lastPlaced = null;
                var pending = new List<PropertyDeclaration>();
                foreach (int token in order)
                {
                    EntityDeclaration d;
                    if (!declared.TryGetValue(token, out d)) continue;
                    if (d is PropertyDeclaration) { pending.Add((PropertyDeclaration)d); continue; }
                    foreach (var p in pending) { p.Remove(); type.Members.InsertBefore(d, p); }
                    pending.Clear();
                    lastField = d;
                }
                // Initialized after every field: behind the last one.
                lastPlaced = lastField;
                foreach (var p in pending)
                {
                    if (lastPlaced == null) break;
                    p.Remove();
                    type.Members.InsertAfter(lastPlaced, p);
                    lastPlaced = p;
                }
            }
        }
    }

    private static string FloatCast(AstNode node)
    {
        foreach (var inst in node.Annotations.OfType<ILInstruction>())
        {
            string type;
            if (RestoreExpressions.FloatCasts.TryGetValue(inst, out type)) return type;
        }
        return null;
    }

    private static void ExplicitFloatCasts(AstNode rootNode)
    {
        foreach (var node in rootNode.Descendants.OfType<Expression>().ToList())
        {
            string type = FloatCast(node);
            if (type == null || node.Parent == null) continue;
            // The outermost node of the value, once.
            if (node.Parent is CastExpression || node.Parent is ParenthesizedExpression) continue;
            if (node.Parent is Expression && FloatCast(node.Parent) != null) continue;
            if (node is CastExpression) continue;
            var cast = new CastExpression();
            node.ReplaceWith(cast);
            cast.Type = new ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveType(type);
            cast.Expression = node;
        }
    }

    // 5. ExplicitConditionals: 'c ? y : false' and 'c ? y : true', which the decompiler
    //    prints 'c && y' and '!c || y' (RestoreConditional marks them).
    private static void ExplicitConditionals(AstNode rootNode)
    {
        foreach (var node in rootNode.Descendants.OfType<BinaryOperatorExpression>().ToList())
        {
            if (node.Operator != BinaryOperatorType.ConditionalAnd && node.Operator != BinaryOperatorType.ConditionalOr) continue;
            object marker;
            IfInstruction marked = null;
            foreach (var inst in node.Annotations.OfType<IfInstruction>())
                if (RestoreConditional.Explicit.TryGetValue(inst, out marker)) marked = inst;
            if (marked == null) continue;
            bool and = node.Operator == BinaryOperatorType.ConditionalAnd;
            if (and != marked.FalseInst.MatchLdcI4(0)) continue;
            Expression condition = node.Left.Detach(), value = node.Right.Detach();
            if (!and)
            {
                // '!c || y' was 'c ? y : true'.
                var not = condition as UnaryOperatorExpression;
                if (not != null && not.Operator == UnaryOperatorType.Not) condition = not.Expression.Detach();
                else condition = new UnaryOperatorExpression(UnaryOperatorType.Not, new ParenthesizedExpression(condition));
            }
            var conditional = new ConditionalExpression(condition, value, new PrimitiveExpression(!and));
            conditional.CopyAnnotationsFrom(node);
            node.ReplaceWith(conditional);
        }
    }

    // 6. BaseCalls: 'call' (not 'callvirt') of a virtual method on 'this' is 'base.M()'. The
    //    decompiler prints 'M()' when the class is sealed, which compiles to 'callvirt'.
    private static void BaseCalls(AstNode rootNode)
    {
        foreach (var node in rootNode.Descendants.OfType<Expression>().ToList())
        {
            Call call = null;
            foreach (var inst in node.Annotations.OfType<ILInstruction>())
                if (inst.GetType() == typeof(Call)) call = (Call)inst;
            if (call == null || call.Method.IsStatic || call.Arguments.Count == 0) continue;
            if (!call.Method.IsVirtual && !call.Method.IsOverride) continue;
            var self = call.Arguments[0] as LdLoc;
            if (self == null || self.Variable.Kind != VariableKind.Parameter || self.Variable.Index != -1) continue;
            IMethod caller = MethodOf(call);
            if (caller == null || caller.DeclaringTypeDefinition == null || call.Method.DeclaringTypeDefinition == null) continue;
            if (caller.DeclaringTypeDefinition.Equals(call.Method.DeclaringTypeDefinition)) continue;
            if (caller.DeclaringTypeDefinition.Kind != TypeKind.Class) continue;
            // In a lambda of a closure class 'base.M()' goes through a generated method; the
            // 'call' there is the one the compiler emits on a captured 'this' for a sealed method.
            if (caller.DeclaringTypeDefinition.Name.StartsWith("<>c")) continue;
            // The member access that names the method or the property.
            Expression access = node;
            var invocation = node as InvocationExpression;
            if (invocation != null) access = invocation.Target;
            var assignment = node as AssignmentExpression;
            if (assignment != null) access = assignment.Left;
            var id = access as IdentifierExpression;
            var member = access as MemberReferenceExpression;
            if (id != null)
            {
                var based = new MemberReferenceExpression(new BaseReferenceExpression(), id.Identifier);
                foreach (var t in id.TypeArguments.ToList()) based.TypeArguments.Add(t.Detach());
                based.CopyAnnotationsFrom(id);
                id.ReplaceWith(based);
            }
            else if (member != null && member.Target is ThisReferenceExpression)
            {
                member.Target.ReplaceWith(new BaseReferenceExpression());
            }
        }
    }

    // 7. RangeSwitches: a 'switch' with few labels has no table in the IL, only comparisons,
    //    and adjacent labels are tested as a range: 'x - lo <= n' (unsigned). The decompiler
    //    prints these as an 'if': '(uint)(x - 1) <= 1u'. No other source gives the range test.
    private sealed class LabelSet
    {
        public string Subject;
        public Expression SubjectNode;
        public readonly SortedSet<long> Values = new SortedSet<long>();
        public bool HasRange;
    }

    private static Expression Unwrap(Expression e)
    {
        while (e is ParenthesizedExpression) e = ((ParenthesizedExpression)e).Expression;
        return e;
    }

    private static bool Constant(Expression e, out long value)
    {
        value = 0;
        var rr = Unwrap(e).GetResolveResult();
        if (rr == null || !rr.IsCompileTimeConstant || rr.ConstantValue == null) return false;
        try { value = System.Convert.ToInt64(rr.ConstantValue, System.Globalization.CultureInfo.InvariantCulture); }
        catch (System.Exception) { return false; }
        return true;
    }

    private static bool Subject(Expression e, LabelSet set)
    {
        e = Unwrap(e);
        if (!(e is IdentifierExpression) && !(e is MemberReferenceExpression)) return false;
        string text = e.ToString();
        if (set.Subject == null) { set.Subject = text; set.SubjectNode = e; }
        return set.Subject == text;
    }

    // One test of 'positive' polarity ('x == c', range '<=') or of the other ('x != c', range '>').
    private static bool Labels(Expression e, bool positive, LabelSet set)
    {
        e = Unwrap(e);
        var binary = e as BinaryOperatorExpression;
        if (binary == null) return false;
        if (binary.Operator == (positive ? BinaryOperatorType.ConditionalOr : BinaryOperatorType.ConditionalAnd))
            return Labels(binary.Left, positive, set) && Labels(binary.Right, positive, set);
        long c;
        if (binary.Operator == (positive ? BinaryOperatorType.Equality : BinaryOperatorType.InEquality))
        {
            if (!Constant(binary.Right, out c) || !Subject(binary.Left, set)) return false;
            set.Values.Add(c);
            return true;
        }
        if (binary.Operator != (positive ? BinaryOperatorType.LessThanOrEqual : BinaryOperatorType.GreaterThan)) return false;
        // (uint)(x - lo) <= n
        var cast = Unwrap(binary.Left) as CastExpression;
        long n, lo = 0;
        if (!Constant(binary.Right, out n) || n < 1 || n > 64) return false;
        Expression inner;
        if (cast == null)
        {
            // An enum on 'byte' or 'ushort': the decompiler prints 'x - lo <= (E)n' without a
            // cast, which compiles to a signed comparison of the truncated difference.
            inner = Unwrap(binary.Left);
            var narrow = inner as BinaryOperatorExpression;
            var rr = narrow == null || narrow.Operator != BinaryOperatorType.Subtract ? null : Unwrap(narrow.Left).GetResolveResult();
            if (rr == null || rr.Type.Kind != TypeKind.Enum) return false;
            System.TypeCode under = ReflectionHelper.GetTypeCode(rr.Type.GetEnumUnderlyingType());
            if (under != System.TypeCode.Byte && under != System.TypeCode.UInt16) return false;
        }
        else
        {
            string type = cast.Type.ToString();
            if (type != "uint" && type != "ulong") return false;
            inner = Unwrap(cast.Expression);
        }
        var difference = inner as BinaryOperatorExpression;
        if (difference != null && difference.Operator == BinaryOperatorType.Subtract)
        {
            if (!Constant(difference.Right, out lo) || !Subject(difference.Left, set)) return false;
        }
        else if (!Subject(inner, set)) return false;
        for (long v = lo; v <= lo + n; v++) set.Values.Add(v);
        set.HasRange = true;
        return true;
    }

    private static void Flatten(Expression e, List<Expression> list)
    {
        var binary = Unwrap(e) as BinaryOperatorExpression;
        if (binary != null && binary.Operator == BinaryOperatorType.ConditionalAnd) { Flatten(binary.Left, list); Flatten(binary.Right, list); }
        else list.Add(e);
    }

    private static bool EndsWithJump(Statement s)
    {
        var block = s as BlockStatement;
        if (block != null) s = block.Statements.LastOrDefault();
        return s is ReturnStatement || s is ThrowStatement || s is BreakStatement || s is ContinueStatement || s is GotoStatement;
    }

    // A 'break;' or 'continue;' of the statement that belongs to something outside it.
    private static bool HasOuterBreak(AstNode root)
    {
        foreach (var jump in root.DescendantsAndSelf.OfType<BreakStatement>())
        {
            bool inside = false;
            for (AstNode p = jump.Parent; p != null && p != root.Parent; p = p.Parent)
                if (p is ForStatement || p is ForeachStatement || p is WhileStatement || p is DoWhileStatement || p is SwitchStatement) inside = true;
            if (!inside) return true;
        }
        return false;
    }

    private static void RangeSwitches(AstNode rootNode, TransformContext context)
    {
        foreach (var test in rootNode.Descendants.OfType<IfElseStatement>().ToList())
        {
            if (test.Parent == null) continue;
            Expression condition = Unwrap(test.Condition);
            bool negated = false;
            var not = condition as UnaryOperatorExpression;
            if (not != null && not.Operator == UnaryOperatorType.Not) { condition = Unwrap(not.Expression); negated = true; }
            var set = new LabelSet();
            bool positive = true;
            Expression extra = null;
            if (!Labels(condition, true, set) || !set.HasRange)
            {
                set = new LabelSet();
                positive = false;
                if (!Labels(condition, false, set) || !set.HasRange)
                {
                    // 'range && rest' without 'else': 'case 1: case 2: if (rest) ... break;'
                    var operands = new List<Expression>();
                    Flatten(condition, operands);
                    set = new LabelSet();
                    positive = true;
                    if (negated || !test.FalseStatement.IsNull || operands.Count < 2 || !Labels(operands[0], true, set) || !set.HasRange) continue;
                    extra = operands[1].Detach();
                    for (int i = 2; i < operands.Count; i++) extra = new BinaryOperatorExpression(extra, BinaryOperatorType.ConditionalAnd, operands[i].Detach());
                }
            }
            if (negated) positive = !positive;
            if (set.Values.Count < 2 || set.Values.Count > 64) continue;
            var subjectResult = set.SubjectNode.GetResolveResult();
            if (subjectResult == null || subjectResult.Type.Kind == TypeKind.Unknown) continue;
            IType type = subjectResult.Type;
            IType underlying = type.Kind == TypeKind.Enum ? type.GetEnumUnderlyingType() : type;
            System.TypeCode code = ReflectionHelper.GetTypeCode(underlying);
            if (code < System.TypeCode.Char || code > System.TypeCode.UInt64) continue;
            Statement whenListed = positive ? test.TrueStatement : test.FalseStatement;
            Statement otherwise = positive ? test.FalseStatement : test.TrueStatement;
            if (HasOuterBreak(test.TrueStatement) || (!test.FalseStatement.IsNull && HasOuterBreak(test.FalseStatement))) continue;

            var statement = new SwitchStatement();
            statement.Expression = set.SubjectNode.Clone();
            var listed = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
            bool convertible = true;
            foreach (long value in set.Values)
            {
                object boxed;
                try { boxed = System.Convert.ChangeType(value, code, System.Globalization.CultureInfo.InvariantCulture); }
                catch (System.Exception) { convertible = false; break; }
                listed.CaseLabels.Add(new CaseLabel(context.TypeSystemAstBuilder.ConvertConstantValue(type, boxed)));
            }
            if (!convertible) continue;
            if (!whenListed.IsNull)
            {
                bool jumps = EndsWithJump(whenListed);
                Statement inner = whenListed.Detach();
                if (extra != null) { inner = new IfElseStatement(extra, inner); jumps = false; }
                listed.Statements.Add(inner);
                if (!jumps) listed.Statements.Add(new BreakStatement());
            }
            else listed.Statements.Add(new BreakStatement());
            statement.SwitchSections.Add(listed);
            if (!otherwise.IsNull)
            {
                var rest = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
                rest.CaseLabels.Add(new CaseLabel());
                bool jumps = EndsWithJump(otherwise);
                rest.Statements.Add(otherwise.Detach());
                if (!jumps) rest.Statements.Add(new BreakStatement());
                statement.SwitchSections.Add(rest);
            }
            // 'T x = e; if (...)' with x read nowhere else: the switch is on e, as the compiler's own
            // temporary. Only when the labels are one range: the value is then read once, and a
            // variable of the source read once would not have been kept in the IL. With several
            // ranges it is read once per range, and it may well be a variable of the source.
            bool oneRange = set.Values.Max - set.Values.Min + 1 == set.Values.Count;
            var declaration = oneRange ? test.PrevSibling as VariableDeclarationStatement : null;
            var identifier = set.SubjectNode as IdentifierExpression;
            AstNode scope = test.Parent;
            test.ReplaceWith(statement);
            if (declaration != null && identifier != null && declaration.Variables.Count == 1 && declaration.Variables.First().Name == identifier.Identifier && !declaration.Variables.First().Initializer.IsNull)
            {
                int uses = scope.Descendants.OfType<IdentifierExpression>().Count(i => i.Identifier == identifier.Identifier);
                if (uses == 1)
                {
                    statement.Expression = declaration.Variables.First().Initializer.Detach();
                    declaration.Remove();
                }
            }
        }
    }

    private static IMethod MethodOf(ILInstruction inst)
    {
        while (inst != null && !(inst is ILFunction)) inst = inst.Parent;
        return inst == null ? null : ((ILFunction)inst).Method;
    }

    // 4. SwitchLabels: a 'switch' instruction of the IL starts and ends with a label of the
    //    source. When its first or last entry goes where 'default' goes, the source had that
    //    'case' next to 'default' (or alone, with nothing to do); the decompiler drops it, and
    //    the compiler then makes a shorter table, or none.
    private static void SwitchLabels(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var statement in rootNode.Descendants.OfType<SwitchStatement>().ToList())
        {
            SwitchInstruction inst = null;
            foreach (var a in statement.Annotations.OfType<ILInstruction>())
            {
                if (a is SwitchInstruction) inst = (SwitchInstruction)a;
                var container = a as BlockContainer;
                if (inst == null && container != null && container.EntryPoint.Instructions.Count > 0) inst = container.EntryPoint.Instructions[0] as SwitchInstruction;
            }
            if (inst == null) continue;
            IMethod method = MethodOf(inst);
            if (method == null) continue;
            RawIL raw;
            if (!bodies.TryGetValue(method, out raw)) { raw = RawIL.Read(context.TypeSystem, method); bodies[method] = raw; }
            if (raw == null) continue;
            RawIL.Op op = default(RawIL.Op), probe;
            bool found = false;
            foreach (var r in inst.ILRanges)
                if (raw.IsAt(r.Start, out probe) && probe.Code == ILOpCode.Switch) { op = probe; found = true; }
            // No table on the instruction itself: a switch compiled as comparisons around smaller
            // tables. Those between its start and its first section are its own.
            var ops = new List<RawIL.Op>();
            if (found) ops.Add(op); else ops.AddRange(DispatchTables(statement, inst, raw));
            foreach (RawIL.Op current in ops)
            {
            op = current;
            int[] table = raw.Switches[op.Offset];
            if (table.Length < 2) continue;
            // Lowest value: 'ldc lo; sub' before the switch.
            long lo = 0;
            RawIL.Op sub, constant, next;
            if (raw.Near(op.Offset, -1, out sub) && sub.Code == ILOpCode.Sub && raw.Near(op.Offset, -2, out constant) && RawIL.IsLdcI4(constant.Code)) lo = constant.Operand;
            else if (raw.Near(op.Offset, -1, out sub) && sub.Code == ILOpCode.Add && raw.Near(op.Offset, -2, out constant) && RawIL.IsLdcI4(constant.Code)) lo = -(long)constant.Operand;
            // Where 'default' goes: the jump after the table, or what follows it.
            if (!raw.Near(op.Offset, 1, out next)) continue;
            int otherwise = next.Code == ILOpCode.Br || next.Code == ILOpCode.Br_s ? next.Operand : next.Offset;
            // After a partial table come more comparisons: 'default' is where its section starts.
            if (!found) { otherwise = DefaultStart(statement, inst); if (otherwise < 0) continue; }
            var missing = new List<long>();
            if (table[0] == otherwise) missing.Add(lo);
            if (table[table.Length - 1] == otherwise) missing.Add(lo + table.Length - 1);
            // Two labels do not make a table: when only two entries are labels for sure (they
            // lead somewhere else, or they are the ends of the table), the others were labels too.
            if (table.Count(t => t != otherwise) + missing.Count <= 2)
            {
                // Not a label the statement has: then 'otherwise' is the section of that label.
                var written = new HashSet<long>();
                bool known = true;
                foreach (var writtenSection in statement.SwitchSections)
                    foreach (var writtenLabel in writtenSection.CaseLabels)
                    {
                        if (writtenLabel.Expression.IsNull) continue;
                        var labelValue = writtenLabel.Expression.GetResolveResult();
                        if (labelValue == null || !labelValue.IsCompileTimeConstant || labelValue.ConstantValue == null) { known = false; continue; }
                        try { written.Add(System.Convert.ToInt64(labelValue.ConstantValue, System.Globalization.CultureInfo.InvariantCulture)); }
                        catch (System.Exception) { known = false; }
                    }
                for (int k = 1; known && k + 1 < table.Length; k++)
                    if (table[k] == otherwise && !written.Contains(lo + k)) missing.Add(lo + k);
                if (known && missing.Any(m => written.Contains(m))) missing.RemoveAll(m => written.Contains(m));
            }
            if (missing.Count == 0) continue;

            var rr = statement.Expression.GetResolveResult();
            if (rr == null || rr.Type.Kind == TypeKind.Unknown) continue;
            IType type = rr.Type;
            IType underlying = type.Kind == TypeKind.Enum ? type.GetEnumUnderlyingType() : type;
            System.TypeCode code = ReflectionHelper.GetTypeCode(underlying);
            if (code < System.TypeCode.Char || code > System.TypeCode.UInt64) continue;
            ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection section = null;
            CaseLabel defaultLabel = null;
            foreach (var s in statement.SwitchSections)
                foreach (var l in s.CaseLabels)
                    if (l.Expression.IsNull) { section = s; defaultLabel = l; }
            if (section == null)
            {
                section = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
                section.Statements.Add(new BreakStatement());
                statement.SwitchSections.Add(section);
            }
            foreach (long value in missing)
            {
                object boxed;
                try { boxed = System.Convert.ChangeType(value, code, System.Globalization.CultureInfo.InvariantCulture); }
                catch (System.Exception) { continue; }
                var label = new CaseLabel(context.TypeSystemAstBuilder.ConvertConstantValue(type, boxed));
                if (defaultLabel != null) section.CaseLabels.InsertBefore(defaultLabel, label);
                else section.CaseLabels.Add(label);
            }
            }
        }
    }

    // 4c. SectionOrder: the compiler emits the sections of a switch in the order of the source,
    //     whatever the values of their labels. Where each label goes is read in the tests
    //     ('ldc k; beq S', 'brfalse S' for 0, the entries of a 'switch' table); the sections are
    //     printed in the order of those targets, 'default' at the place of the last 'br'.
    private static void SectionOrder(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var statement in rootNode.Descendants.OfType<SwitchStatement>().ToList())
        {
            var sections = statement.SwitchSections.ToList();
            if (sections.Count < 3) continue;
            SwitchInstruction inst = null;
            foreach (var a in statement.Annotations.OfType<ILInstruction>())
            {
                if (a is SwitchInstruction) inst = (SwitchInstruction)a;
                var container = a as BlockContainer;
                if (inst == null && container != null && container.EntryPoint.Instructions.Count > 0) inst = container.EntryPoint.Instructions[0] as SwitchInstruction;
            }
            if (inst == null) continue;
            RawIL raw = Body(MethodOf(inst), context, bodies);
            if (raw == null) continue;
            int from = RawIL.StartOf(inst), body = int.MaxValue;
            ILFunction owner = inst.Ancestors.OfType<ILFunction>().FirstOrDefault();
            foreach (var section in sections)
                foreach (AstNode n in section.Statements.SelectMany(x => x.DescendantsAndSelf))
                    foreach (var a in n.Annotations.OfType<ILInstruction>())
                    {
                        if (a is Leave || a is Branch || a.Ancestors.OfType<ILFunction>().FirstOrDefault() != owner) continue;
                        int start = RawIL.StartOfCode(a);
                        if (start > from && start < body) body = start;
                    }
            int first, last;
            if (from < 0 || body == int.MaxValue || !raw.IndexAt.TryGetValue(from, out first) || !raw.IndexAt.TryGetValue(body, out last) || last - first < 4) continue;
            // Where no label goes: the last 'br' of the tests, or the first section when they fall into it.
            RawIL.Op end = raw.Ops[last - 1];
            int nowhere = end.Code == ILOpCode.Br || end.Code == ILOpCode.Br_s ? end.Operand : body;
            var targets = new Dictionary<long, int>();
            // (The value is read from its variable, or from a temporary stored first.)
            bool plain = true, stored = RawIL.IsLdLoc(raw.Ops[first].Code) || RawIL.IsLdArg(raw.Ops[first].Code);
            for (int i = first; i < last && plain; i++)
            {
                RawIL.Op op = raw.Ops[i];
                ILOpCode c = op.Code;
                if (RawIL.IsStLoc(c)) { stored = true; continue; }
                if (!stored) continue;
                if (RawIL.IsLdcI4(c) || c == ILOpCode.Ldc_i8)
                {
                    long value = op.Operand;
                    int j = i + 1;
                    if (c == ILOpCode.Ldc_i8) { if (!raw.Wide.TryGetValue(op.Offset, out value)) { plain = false; break; } }
                    else if (raw.Ops[j].Code == ILOpCode.Conv_u8) { value = (uint)op.Operand; j++; }
                    else if (raw.Ops[j].Code == ILOpCode.Conv_i8) j++;
                    ILOpCode use = raw.Ops[j].Code;
                    if (use == ILOpCode.Beq || use == ILOpCode.Beq_s) { if (targets.ContainsKey(value)) plain = false; targets[value] = raw.Ops[j].Operand; }
                    else if (use == ILOpCode.Sub && j + 1 < last && raw.Ops[j + 1].Code == ILOpCode.Switch)
                    {
                        int[] table = raw.Switches[raw.Ops[j + 1].Offset];
                        int otherwise = raw.Ops[j + 2].Code == ILOpCode.Br || raw.Ops[j + 2].Code == ILOpCode.Br_s ? raw.Ops[j + 2].Operand : raw.Ops[j + 2].Offset;
                        for (int k = 0; k < table.Length; k++)
                            if (table[k] != otherwise && table[k] != nowhere) { if (targets.ContainsKey(value + k)) plain = false; targets[value + k] = table[k]; }
                        i = j + 1;
                    }
                    else if (!(RawIL.IsCondBranch(use))) plain = false;
                    continue;
                }
                if (c == ILOpCode.Switch)
                {
                    // Without 'ldc lo; sub': the lowest label is 0.
                    int[] table = raw.Switches[op.Offset];
                    int otherwise = raw.Ops[i + 1].Code == ILOpCode.Br || raw.Ops[i + 1].Code == ILOpCode.Br_s ? raw.Ops[i + 1].Operand : raw.Ops[i + 1].Offset;
                    for (int k = 0; k < table.Length; k++)
                        if (table[k] != otherwise && table[k] != nowhere) { if (targets.ContainsKey(k)) plain = false; targets[k] = table[k]; }
                    continue;
                }
                if ((c == ILOpCode.Brfalse || c == ILOpCode.Brfalse_s) && RawIL.IsLdLoc(raw.Ops[i - 1].Code)) { if (targets.ContainsKey(0)) plain = false; targets[0] = op.Operand; continue; }
                if (RawIL.IsLdLoc(c) || RawIL.IsLdArg(c) || RawIL.IsCondBranch(c) || c == ILOpCode.Br || c == ILOpCode.Br_s || c == ILOpCode.Conv_u8 || c == ILOpCode.Conv_i8) continue;
                plain = false;
            }
            if (!plain || targets.Count == 0) continue;
            var starts = new List<int>();
            bool known = true;
            foreach (var section in sections)
            {
                int start = -1;
                foreach (CaseLabel label in section.CaseLabels)
                {
                    if (label.Expression.IsNull) { if (start >= 0 && start != nowhere) known = false; start = nowhere; continue; }
                    var labelValue = label.Expression.GetResolveResult();
                    long value;
                    int target;
                    if (labelValue == null || !labelValue.IsCompileTimeConstant || labelValue.ConstantValue == null) { known = false; break; }
                    try { value = System.Convert.ToInt64(labelValue.ConstantValue, System.Globalization.CultureInfo.InvariantCulture); }
                    catch (System.Exception) { known = false; break; }
                    if (!targets.TryGetValue(value, out target) || (start >= 0 && start != target)) { known = false; break; }
                    start = target;
                }
                if (!known || start < 0) { known = false; break; }
                starts.Add(start);
            }
            if (!known || starts.Distinct().Count() != starts.Count) continue;
            var order = Enumerable.Range(0, sections.Count).OrderBy(i => starts[i]).ToList();
            if (order.SequenceEqual(Enumerable.Range(0, sections.Count))) continue;
            foreach (var section in sections) section.Remove();
            foreach (int i in order) statement.SwitchSections.Add(sections[i]);
        }
    }

    // 4b. EmptySections: 'case a: case b: break;' with nothing to do, in a switch without
    //     'default' that is compiled as comparisons: 'ldloc t; ldc a; beq END', END being
    //     where the last 'br' of the tests goes. The decompiler drops the section, and the
    //     compiler then tests fewer values.
    private static void EmptySections(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var statement in rootNode.Descendants.OfType<SwitchStatement>().ToList())
        {
            if (statement.SwitchSections.Any(s => s.CaseLabels.Any(l => l.Expression.IsNull))) continue;
            SwitchInstruction inst = null;
            foreach (var a in statement.Annotations.OfType<ILInstruction>())
            {
                if (a is SwitchInstruction) inst = (SwitchInstruction)a;
                var container = a as BlockContainer;
                if (inst == null && container != null && container.EntryPoint.Instructions.Count > 0) inst = container.EntryPoint.Instructions[0] as SwitchInstruction;
            }
            if (inst == null) continue;
            RawIL raw = Body(MethodOf(inst), context, bodies);
            if (raw == null) continue;
            int from = RawIL.StartOf(inst), body = int.MaxValue;
            ILFunction owner = inst.Ancestors.OfType<ILFunction>().FirstOrDefault();
            foreach (var section in statement.SwitchSections)
                foreach (AstNode n in section.Statements.SelectMany(x => x.DescendantsAndSelf))
                    foreach (var a in n.Annotations.OfType<ILInstruction>())
                    {
                        if (a is Leave || a is Branch || a.Ancestors.OfType<ILFunction>().FirstOrDefault() != owner) continue;
                        int start = RawIL.StartOfCode(a);
                        if (start > from && start < body) body = start;
                    }
            int first, last;
            if (from < 0 || body == int.MaxValue || !raw.IndexAt.TryGetValue(from, out first) || !raw.IndexAt.TryGetValue(body, out last) || last - first < 4) continue;
            // The tests: loads, constants, comparisons; the last one is the 'br' of 'no label'.
            // (At the end of a method that 'br' is a 'ret'; the labels still jump to the last one.)
            RawIL.Op end = raw.Ops[last - 1];
            int nowhere;
            if (end.Code == ILOpCode.Br || end.Code == ILOpCode.Br_s) nowhere = end.Operand;
            else if (end.Code == ILOpCode.Ret && raw.Ops[raw.Ops.Count - 1].Code == ILOpCode.Ret) nowhere = raw.Ops[raw.Ops.Count - 1].Offset;
            else continue;
            var targets = new Dictionary<long, int>();
            var pivots = new List<long>();
            bool plain = true;
            for (int i = first; i < last && plain; i++)
            {
                RawIL.Op op = raw.Ops[i];
                ILOpCode c = op.Code;
                if (RawIL.IsLdcI4(c) || c == ILOpCode.Ldc_i8)
                {
                    long value = op.Operand;
                    int j = i + 1;
                    if (c == ILOpCode.Ldc_i8) { if (!raw.Wide.TryGetValue(op.Offset, out value)) { plain = false; break; } }
                    else if (raw.Ops[j].Code == ILOpCode.Conv_u8) { value = (uint)op.Operand; j++; }
                    else if (raw.Ops[j].Code == ILOpCode.Conv_i8) j++;
                    if (raw.Ops[j].Code == ILOpCode.Beq || raw.Ops[j].Code == ILOpCode.Beq_s) targets[value] = raw.Ops[j].Operand;
                    // 'ldc m; bgt UPPER' splits the labels in two: m is the highest label of the lower half, and it has
                    // no test of its own when it goes where 'no label' goes.
                    else if (raw.Ops[j].Code == ILOpCode.Bgt || raw.Ops[j].Code == ILOpCode.Bgt_s || raw.Ops[j].Code == ILOpCode.Bgt_un || raw.Ops[j].Code == ILOpCode.Bgt_un_s) pivots.Add(value);
                    continue;
                }
                if (RawIL.IsLdLoc(c) || RawIL.IsStLoc(c) || RawIL.IsLdArg(c) || RawIL.IsCondBranch(c) || c == ILOpCode.Br || c == ILOpCode.Br_s || c == ILOpCode.Ret || c == ILOpCode.Conv_u8 || c == ILOpCode.Conv_i8) continue;
                // The value itself, in front of its store.
                if (i > first && raw.Ops.Skip(first).Take(i - first).Any(o => RawIL.IsStLoc(o.Code))) plain = false;
            }
            if (!plain || targets.Count == 0) continue;
            var written = new HashSet<long>();
            bool known = true;
            foreach (var writtenSection in statement.SwitchSections)
                foreach (var writtenLabel in writtenSection.CaseLabels)
                {
                    var labelValue = writtenLabel.Expression.GetResolveResult();
                    if (labelValue == null || !labelValue.IsCompileTimeConstant || labelValue.ConstantValue == null) { known = false; continue; }
                    try { written.Add(System.Convert.ToInt64(labelValue.ConstantValue, System.Globalization.CultureInfo.InvariantCulture)); }
                    catch (System.Exception) { known = false; }
                }
            if (!known) continue;
            var missing = targets.Where(t => t.Value == nowhere && !written.Contains(t.Key)).Select(t => t.Key).OrderBy(v => v).ToList();
            foreach (long pivot in pivots)
                if (!written.Contains(pivot) && !targets.ContainsKey(pivot) && !missing.Contains(pivot)) missing.Add(pivot);
            missing.Sort();
            if (missing.Count == 0 || targets.Any(t => t.Value != nowhere && !written.Contains(t.Key))) continue;
            var rr = statement.Expression.GetResolveResult();
            if (rr == null || rr.Type.Kind == TypeKind.Unknown) continue;
            IType type = rr.Type;
            IType underlying = type.Kind == TypeKind.Enum ? type.GetEnumUnderlyingType() : type;
            System.TypeCode code = ReflectionHelper.GetTypeCode(underlying);
            if (code < System.TypeCode.Char || code > System.TypeCode.UInt64) continue;
            // The tests are split in two halves at 'ldc m; bgt': m is the label of rank n / 2 among n. A label
            // that goes nowhere and stands last in its half has no test at all; when m is the label of rank
            // (n + 1) / 2, one more label stands above the highest. Its value is not in the IL (any value above
            // compiles to the same tests): the next member of the enum is taken.
            if (pivots.Count == 1 && type.Kind == TypeKind.Enum)
            {
                var all = written.Concat(missing).Distinct().OrderBy(v => v).ToList();
                int n = all.Count;
                if (n >= 4 && all[n / 2 - 1] != pivots[0] && all[(n + 1) / 2 - 1] == pivots[0])
                {
                    long highest = all[n - 1];
                    var above = new List<long>();
                    foreach (IField member in type.GetFields(f => f.IsConst && f.IsStatic))
                    {
                        object constant = member.GetConstantValue();
                        if (constant == null) continue;
                        long v;
                        try { v = System.Convert.ToInt64(constant, System.Globalization.CultureInfo.InvariantCulture); } catch (System.Exception) { continue; }
                        if (v > highest) above.Add(v);
                    }
                    if (above.Count > 0) missing.Add(above.Min());
                }
            }
            var empty = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
            foreach (long value in missing)
            {
                object boxed;
                try { boxed = System.Convert.ChangeType(value, code, System.Globalization.CultureInfo.InvariantCulture); }
                catch (System.Exception) { continue; }
                empty.CaseLabels.Add(new CaseLabel(context.TypeSystemAstBuilder.ConvertConstantValue(type, boxed)));
            }
            if (empty.CaseLabels.Count != missing.Count) continue;
            empty.Statements.Add(new BreakStatement());
            statement.SwitchSections.Add(empty);
        }
    }

    private static int DefaultStart(SwitchStatement statement, SwitchInstruction inst)
    {
        ILFunction owner = inst.Ancestors.OfType<ILFunction>().FirstOrDefault();
        int best = -1;
        foreach (var section in statement.SwitchSections)
        {
            if (!section.CaseLabels.Any(l => l.Expression.IsNull)) continue;
            foreach (AstNode n in section.Statements.SelectMany(x => x.DescendantsAndSelf))
                foreach (var a in n.Annotations.OfType<ILInstruction>())
                {
                    if (a.Ancestors.OfType<ILFunction>().FirstOrDefault() != owner) continue;
                    int start = RawIL.StartOf(a);
                    if (start >= 0 && (best < 0 || start < best)) best = start;
                }
        }
        return best;
    }
    private static List<RawIL.Op> DispatchTables(SwitchStatement statement, SwitchInstruction inst, RawIL raw)
    {
        var found = new List<RawIL.Op>();
        int from = RawIL.StartOf(inst), body = int.MaxValue;
        ILFunction owner = inst.Ancestors.OfType<ILFunction>().FirstOrDefault();
        foreach (var section in statement.SwitchSections)
            foreach (AstNode n in section.Statements.SelectMany(x => x.DescendantsAndSelf))
                foreach (var a in n.Annotations.OfType<ILInstruction>())
                {
                    if (a.Ancestors.OfType<ILFunction>().FirstOrDefault() != owner) continue;
                    int start = RawIL.StartOf(a);
                    if (start > from && start < body) body = start;
                }
        if (from < 0 || body == int.MaxValue) return found;
        foreach (RawIL.Op op in raw.Ops)
            if (op.Code == ILOpCode.Switch && op.Offset >= from && op.Offset < body) found.Add(op);
        return found;
    }

    private static readonly Dictionary<int, ImmutableArray<IType>> NoLocals = new Dictionary<int, ImmutableArray<IType>>();

    private static void ForeachVariableTypes(AstNode rootNode, TransformContext context)
    {
        foreach (var loop in rootNode.Descendants.OfType<ForeachStatement>().ToList())
        {
            ILVariable v = null;
            foreach (var rr in loop.Annotations.OfType<ILVariableResolveResult>()) v = rr.Variable;
            if (v == null && loop.VariableDesignation != null)
                foreach (var rr in loop.VariableDesignation.Annotations.OfType<ILVariableResolveResult>()) v = rr.Variable;
            if (v == null || v.Function == null || v.Function.Method == null) continue;
            // A variable the decompiler made for the loop has no slot: the one the IL stores
            // 'get_Current()' in, after the collection of this loop.
            int slot = v.Index.HasValue ? v.Index.Value : -1;
            if (slot < 0)
            {
                RawIL body = RawIL.Read(context.TypeSystem, v.Function.Method);
                int from = -1;
                foreach (AstNode n in loop.InExpression.DescendantsAndSelf)
                    foreach (var inst in n.Annotations.OfType<ILInstruction>())
                        if (inst.Ancestors.OfType<ILFunction>().FirstOrDefault() == v.Function) from = System.Math.Max(from, RawIL.EndOf(inst));
                int at;
                if (body == null || from < 0 || !body.IndexAt.TryGetValue(from, out at)) continue;
                for (int k = at; k < body.Ops.Count && k < at + 8; k++)
                {
                    if (!RawIL.IsCall(body.Ops[k].Code)) continue;
                    IMethod callee = body.Method(body.Ops[k]);
                    if (callee == null || callee.Name != "get_Current") continue;
                    if (k + 1 < body.Ops.Count && RawIL.IsStLoc(body.Ops[k + 1].Code)) slot = body.Ops[k + 1].Operand;
                    break;
                }
                if (slot < 0) continue;
            }
            if (v.Kind != VariableKind.ForeachLocal && v.Kind != VariableKind.Local) continue;
            ImmutableArray<IType> locals = RawIL.LocalTypes(context.TypeSystem, v.Function.Method);
            if (slot >= locals.Length) continue;
            IType declared = locals[slot];
            // The decompiler may also print the element type of the collection for a
            // variable it knows to be of the declared type.
            IType printed = v.Type;
            var printedType = loop.VariableType.GetResolveResult();
            if (declared.Equals(v.Type) && printedType != null && printedType.Type.Kind != TypeKind.Unknown && !(loop.VariableType is SimpleType && ((SimpleType)loop.VariableType).Identifier == "var")) printed = printedType.Type;
            if (declared.Kind == TypeKind.Unknown || declared.Equals(printed)) continue;
            // Only a wider reference type: anything else is the decompiler's own reading of the slot.
            if (declared.IsReferenceType != true || printed.IsReferenceType != true) continue;
            if (!printed.GetAllBaseTypes().Any(b => b.Equals(declared))) continue;
            loop.VariableType = context.TypeSystemAstBuilder.ConvertType(declared);
        }
    }

    // A local or parameter read that the IL follows with a branch, not with 'and' / 'or'.
    // (The decompiler prints '&&' for both.)
    private static bool IsBranchedLocal(Expression e, TransformContext context, Dictionary<IMethod, RawIL> bodies)
    {
        var id = e as IdentifierExpression;
        if (id == null) return false;
        ILVariable v = null;
        foreach (var rr in id.Annotations.OfType<ILVariableResolveResult>()) v = rr.Variable;
        if (v == null) return false;
        int end = -1;
        IMethod method = null;
        foreach (var inst in id.Annotations.OfType<ILInstruction>())
        {
            foreach (var r in inst.ILRanges) if (r.Start != r.End && r.End > end) end = r.End;
            ILInstruction p = inst;
            while (p != null && !(p is ILFunction)) p = p.Parent;
            if (p != null) method = ((ILFunction)p).Method;
        }
        if (end < 0 || method == null) return false;
        RawIL raw;
        if (!bodies.TryGetValue(method, out raw)) { raw = RawIL.Read(context.TypeSystem, method); bodies[method] = raw; }
        RawIL.Op op;
        if (raw == null || !raw.IsAt(end, out op)) return false;
        if (op.Code == ILOpCode.Ldind_u1 && !raw.Near(end, 1, out op)) return false;
        if (op.Code == ILOpCode.And || op.Code == ILOpCode.Or) return false;
        // The range may include the instruction that consumed the value.
        RawIL.Op last;
        if (raw.EndsAt(end, out last) && (last.Code == ILOpCode.And || last.Code == ILOpCode.Or)) return false;
        return true;
    }

    private static void ShortCircuitLocals(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var node in rootNode.Descendants.OfType<BinaryOperatorExpression>().ToList())
        {
            if (node.Operator != BinaryOperatorType.ConditionalAnd && node.Operator != BinaryOperatorType.ConditionalOr) continue;
            // Printed without parentheses, 'a && (b && c)' is read '(a && b) && c': the operand
            // that ends up on the right of the first '&&' is b.
            Expression right = node.Right;
            while (right is BinaryOperatorExpression && ((BinaryOperatorExpression)right).Operator == node.Operator)
                right = ((BinaryOperatorExpression)right).Left;
            if (!IsBranchedLocal(right, context, bodies)) continue;
            var cast = new CastExpression();
            right.ReplaceWith(cast);
            cast.Type = new ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveType("bool");
            cast.Expression = right;
        }
    }

    // 16. BitwiseBooleans: 'a | b' and 'a & b' on booleans are an 'or' / 'and' in the IL.
    //     The decompiler prints '||' / '&&' when b has no side effect, which the compiler
    //     only evaluates without a branch when b is a local or a parameter.
    private static void BitwiseBooleans(AstNode rootNode)
    {
        foreach (var node in rootNode.Descendants.OfType<BinaryOperatorExpression>().ToList())
        {
            if (node.Operator != BinaryOperatorType.ConditionalAnd && node.Operator != BinaryOperatorType.ConditionalOr) continue;
            var inst = node.Annotations.OfType<BinaryNumericInstruction>().FirstOrDefault();
            if (inst == null) continue;
            if (inst.Operator != (node.Operator == BinaryOperatorType.ConditionalAnd ? BinaryNumericOperator.BitAnd : BinaryNumericOperator.BitOr)) continue;
            Expression right = Unwrap(node.Right);
            if (right is IdentifierExpression || right is PrimitiveExpression) continue;
            node.Operator = node.Operator == BinaryOperatorType.ConditionalAnd ? BinaryOperatorType.BitwiseAnd : BinaryOperatorType.BitwiseOr;
        }
    }
}
