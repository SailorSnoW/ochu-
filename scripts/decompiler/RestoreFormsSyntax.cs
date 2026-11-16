// C# transform: single source forms that only the syntax can carry (second parallel series).
//
// 1. Switch on type patterns.
//        switch (x) { case T1 a: A; case T2 b: B; default: C; }
//            IL: x; isinst T1; stloc a; ldloc a; brtrue LA; x; isinst T2; stloc b; ldloc b; brtrue LB; br LC; LA: A; LB: B; LC: C
//    is printed 'if (!(x is T1 a)) { if (x is T2 b) { B } C } A', which falls into each body
//    ('brfalse').
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreFormsSyntax : IAstTransform
{
    public void Run(AstNode rootNode, TransformContext context)
    {
        PatternSwitches(rootNode, context);
        WidenedBranches(rootNode);
        NarrowedOperands(rootNode);
        ZeroTestedSwitches(rootNode, context);
        SectionExitTails(rootNode, context);
        IdleStringLabels(rootNode, context);
        ContinuesInTry(rootNode, context);
        LeaveChains(rootNode, context);
    }

    private static Expression Unwrap(Expression e)
    {
        while (e is ParenthesizedExpression) e = ((ParenthesizedExpression)e).Expression;
        return e;
    }

    private static bool EndsWithJump(Statement s)
    {
        var block = s as BlockStatement;
        if (block != null) s = block.Statements.LastOrDefault();
        return s is ReturnStatement || s is ThrowStatement || s is ContinueStatement;
    }

    // 'x is T v': the subject and the declaration.
    private static bool TypeTest(Expression e, out IdentifierExpression subject, out DeclarationExpression declaration)
    {
        subject = null;
        declaration = null;
        var test = Unwrap(e) as BinaryOperatorExpression;
        if (test == null || test.Operator != BinaryOperatorType.IsPattern) return false;
        subject = Unwrap(test.Left) as IdentifierExpression;
        declaration = test.Right as DeclarationExpression;
        return subject != null && declaration != null && declaration.Designation is SingleVariableDesignation;
    }

    private static bool InNestedFunction(AstNode node, AstNode entity)
    {
        for (AstNode p = node.Parent; p != null && p != entity; p = p.Parent)
            if (p is AnonymousMethodExpression || p is LambdaExpression || p is LocalFunctionDeclarationStatement) return true;
        return false;
    }

    private static IMethod MethodOf(ILInstruction inst)
    {
        while (inst != null && !(inst is ILFunction)) inst = inst.Parent;
        return inst == null ? null : ((ILFunction)inst).Method;
    }

    private static string FreshName(AstNode entity, string stem)
    {
        var used = new HashSet<string>(entity.Descendants.OfType<Identifier>().Select(i => i.Name));
        if (!used.Contains(stem)) return stem;
        for (int n = 2; ; n++) if (!used.Contains(stem + n)) return stem + n;
    }

    // 2. Conditional widened in each branch.
    //        c ? (long)x : 1L          IL: c; brtrue A; ldc.i4.1; conv.i8; br E; A: x; conv.i8; E:
    //    is printed '(long)(c ? x : 1)', one 'conv.i8' after the two branches.
    private static void WidenedBranches(AstNode rootNode)
    {
        foreach (var cast in rootNode.Descendants.OfType<CastExpression>().ToList())
        {
            var node = Unwrap(cast.Expression) as ConditionalExpression;
            if (node == null || cast.Parent == null || cast.Type.ToString() != "long") continue;
            IfInstruction inst = cast.Annotations.OfType<IfInstruction>().FirstOrDefault() ?? node.Annotations.OfType<IfInstruction>().FirstOrDefault();
            if (inst == null) continue;
            if (!Widens(inst.TrueInst) || !Widens(inst.FalseInst) || !(inst.TrueInst is Conv || inst.FalseInst is Conv)) continue;
            foreach (Expression branch in new[] { node.TrueExpression, node.FalseExpression })
            {
                var inner = new CastExpression();
                branch.ReplaceWith(inner);
                inner.Type = cast.Type.Clone();
                inner.Expression = branch;
            }
            cast.ReplaceWith(node.Detach());
        }
    }

    private static bool Widens(ILInstruction inst)
    {
        long value;
        if (inst.MatchLdcI8(out value)) return true;
        var conv = inst as Conv;
        return conv != null && conv.InputType == StackType.I4 && conv.TargetType == ICSharpCode.Decompiler.IL.PrimitiveType.I8;
    }

    // 3. '(uint)l' written by the decompiler around an operand of '&' that the IL narrows
    //    with 'conv.i4': '(uint)(int)l' (a 'conv.u4' otherwise).
    private static void NarrowedOperands(AstNode rootNode)
    {
        foreach (var cast in rootNode.Descendants.OfType<CastExpression>().ToList())
        {
            if (cast.Parent == null || cast.Type.ToString() != "uint") continue;
            if (Unwrap(cast.Expression) is CastExpression) continue;
            var rr = cast.Expression.GetResolveResult();
            if (rr == null || !rr.Type.IsKnownType(KnownTypeCode.Int64)) continue;
            var binary = cast.Parent as BinaryOperatorExpression;
            if (binary == null || binary.Operator != BinaryOperatorType.BitwiseAnd) continue;
            var and = binary.Annotations.OfType<BinaryNumericInstruction>().FirstOrDefault();
            if (and == null) continue;
            ILInstruction operand = cast == binary.Left ? and.Left : and.Right;
            var conv = operand as Conv;
            if (conv == null || conv.InputType != StackType.I8 || conv.TargetType != ICSharpCode.Decompiler.IL.PrimitiveType.I4) continue;
            Expression value = cast.Expression;
            var inner = new CastExpression();
            value.ReplaceWith(inner);
            inner.Type = new ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveType("int");
            inner.Expression = value;
        }
    }

    // 4. Value tested against zero before a 'switch' on it.
    //        T v = e;  if (v != 0) { switch (v) { ... } }
    //            IL: e; stloc v; ldloc v; brfalse END; ldloc v; <comparisons of the switch>
    //    is printed 'switch (e) { ... }': the zero test reads as one more label that goes to
    //    the end. A 'case 0:' would be in the comparisons (or in a table), not before them.
    private static void ZeroTestedSwitches(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var statement in rootNode.Descendants.OfType<SwitchStatement>().ToList())
        {
            if (statement.Parent == null || !(statement.Parent is BlockStatement)) continue;
            if (Unwrap(statement.Expression) is IdentifierExpression) continue;
            SwitchInstruction inst = null;
            foreach (var a in statement.Annotations.OfType<ILInstruction>())
            {
                if (a is SwitchInstruction) inst = (SwitchInstruction)a;
                var container = a as BlockContainer;
                if (inst == null && container != null && container.EntryPoint.Instructions.Count > 0) inst = container.EntryPoint.Instructions[0] as SwitchInstruction;
            }
            if (inst == null) continue;
            bool plain = true;
            foreach (var section in statement.SwitchSections)
                foreach (var label in section.CaseLabels)
                {
                    var value = label.Expression.IsNull ? null : label.Expression.GetResolveResult();
                    if (value == null || !value.IsCompileTimeConstant || value.ConstantValue == null) { plain = false; continue; }
                    try { if (System.Convert.ToInt64(value.ConstantValue, System.Globalization.CultureInfo.InvariantCulture) == 0) plain = false; }
                    catch (System.Exception) { plain = false; }
                }
            if (!plain) continue;
            IMethod method = MethodOf(inst);
            if (method == null) continue;
            RawIL raw;
            if (!bodies.TryGetValue(method, out raw)) { raw = RawIL.Read(context.TypeSystem, method); bodies[method] = raw; }
            if (raw == null) continue;
            int end = RawIL.EndOf(inst.Value);
            RawIL.Op store, load, branch, again, constant;
            if (end < 0) continue;
            if (!(raw.IsAt(end, out store) && RawIL.IsStLoc(store.Code)) && !(raw.EndsAt(end, out store) && RawIL.IsStLoc(store.Code))) continue;
            if (!raw.Near(store.Offset, 1, out load) || !RawIL.IsLdLoc(load.Code) || load.Operand != store.Operand) continue;
            if (!raw.Near(store.Offset, 2, out branch) || (branch.Code != ILOpCode.Brfalse && branch.Code != ILOpCode.Brfalse_s)) continue;
            if (!raw.Near(store.Offset, 3, out again) || !RawIL.IsLdLoc(again.Code) || again.Operand != store.Operand) continue;
            if (!raw.Near(store.Offset, 4, out constant) || !RawIL.IsLdcI4(constant.Code)) continue;
            var type = statement.Expression.GetResolveResult();
            var entity = statement.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (type == null || type.Type.Kind == TypeKind.Unknown || entity == null) continue;

            string name = FreshName(entity, "tested");
            Expression subject = Unwrap(statement.Expression).Detach();
            var declaration = new VariableDeclarationStatement(context.TypeSystemAstBuilder.ConvertType(type.Type), name, subject);
            var guard = new IfElseStatement();
            statement.ReplaceWith(guard);
            Expression zero = type.Type.Kind == TypeKind.Enum
                ? (Expression)new CastExpression(context.TypeSystemAstBuilder.ConvertType(type.Type), new PrimitiveExpression(0))
                : new PrimitiveExpression(0);
            guard.Condition = new BinaryOperatorExpression(new IdentifierExpression(name), BinaryOperatorType.InEquality, zero);
            statement.Expression = new IdentifierExpression(name);
            guard.TrueStatement = new BlockStatement { statement };
            guard.Parent.InsertChildBefore(guard, declaration, BlockStatement.StatementRole);
        }
    }

    private static int FirstOffset(AstNode node)
    {
        int min = -1;
        foreach (AstNode n in node.DescendantsAndSelf)
            foreach (var inst in n.Annotations.OfType<ILInstruction>())
            {
                if (inst is Leave || inst is Branch) continue;
                int s = RawIL.StartOfCode(inst);
                if (s >= 0 && (min < 0 || s < min)) min = s;
            }
        return min;
    }

    private static ILVariable VariableOf(VariableInitializer v)
    {
        ILVariable found = null;
        foreach (var rr in v.Annotations.OfType<ILVariableResolveResult>()) found = rr.Variable;
        return found;
    }

    // 5. End of a section written after its 'switch'.
    //        T v;  switch (e) { case A: v = a; break; case B: v = b; ...; return v; default: throw; }  X(v); return v;
    //            IL: section A ends with 'br TAIL', and TAIL comes after the last section
    //    is printed with the tail at the end of section A, the only one that reaches it, and
    //    one variable per section (the compiler gave them one slot: one variable of the source).
    private static void SectionExitTails(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var statement in rootNode.Descendants.OfType<SwitchStatement>().ToList())
        {
            var outer = statement.Parent as BlockStatement;
            if (outer == null || outer.Statements.LastOrDefault() != statement) continue;
            var sections = statement.SwitchSections.ToList();
            if (sections.Count < 2) continue;
            ILInstruction subject = statement.Expression.Annotations.OfType<ILInstruction>().FirstOrDefault();
            IMethod method = MethodOf(subject);
            if (method == null) continue;
            var starts = new Dictionary<ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection, int>();
            bool known = true;
            foreach (var section in sections)
            {
                Statement last = section.Statements.LastOrDefault();
                var lastBlock = last as BlockStatement;
                if (lastBlock != null) last = lastBlock.Statements.LastOrDefault();
                if (!(last is ReturnStatement || last is ThrowStatement) || section.Descendants.OfType<BreakStatement>().Any() || section.Descendants.OfType<GotoStatement>().Any()
                    || section.Descendants.OfType<GotoCaseStatement>().Any() || section.Descendants.OfType<GotoDefaultStatement>().Any()) { known = false; break; }
                int start = FirstOffset(section.Statements.First());
                if (start < 0) { known = false; break; }
                starts[section] = start;
            }
            if (!known) continue;
            int lastStart = starts.Values.Max();
            RawIL raw;
            if (!bodies.TryGetValue(method, out raw)) { raw = RawIL.Read(context.TypeSystem, method); bodies[method] = raw; }
            if (raw == null) continue;
            foreach (var section in sections)
            {
                if (starts[section] == lastStart) continue;
                var body = section.Statements.Count == 1 ? section.Statements.First() as BlockStatement : null;
                if (body == null) continue;
                var list = body.Statements.ToList();
                int nextStart = starts.Values.Where(s => s > starts[section]).Min();
                int k = -1;
                for (int i = 1; i < list.Count; i++)
                {
                    int at = FirstOffset(list[i]);
                    if (at > lastStart && FirstOffset(list[i - 1]) < nextStart) { k = i; break; }
                    if (at < 0 || at > nextStart) break;
                }
                if (k < 0) continue;
                int tail = FirstOffset(list[k]);
                // A 'br' of the section goes there, and nothing falls on it.
                RawIL.Op before;
                if (!raw.EndsAt(tail, out before)) continue;
                if (before.Code != ILOpCode.Throw && before.Code != ILOpCode.Ret && before.Code != ILOpCode.Br && before.Code != ILOpCode.Br_s && before.Code != ILOpCode.Leave && before.Code != ILOpCode.Leave_s) continue;
                if (!raw.Ops.Any(o => (o.Code == ILOpCode.Br || o.Code == ILOpCode.Br_s) && o.Operand == tail && o.Offset >= starts[section] && o.Offset < nextStart)) continue;
                var moved = list.Skip(k).ToList();
                if (moved.Any(m => m is VariableDeclarationStatement || m is LabelStatement)) continue;
                // Variables of the section that the tail reads: declared before the 'switch'. The
                // variable of the same slot in another section is the same one.
                var names = new HashSet<string>(moved.SelectMany(m => m.DescendantsAndSelf.OfType<IdentifierExpression>()).Select(i => i.Identifier));
                var hoisted = new List<VariableDeclarationStatement>();
                bool possible = true;
                foreach (var declaration in list.Take(k).OfType<VariableDeclarationStatement>())
                {
                    if (!declaration.Variables.Any(v => names.Contains(v.Name))) continue;
                    if (declaration.Variables.Count != 1 || declaration.Variables.First().Initializer.IsNull) { possible = false; break; }
                    hoisted.Add(declaration);
                }
                if (!possible || list.Take(k).Any(s => !(s is VariableDeclarationStatement) && s.Descendants.OfType<VariableInitializer>().Any(v => names.Contains(v.Name)))) continue;
                var again = new List<VariableDeclarationStatement>();
                foreach (var declaration in hoisted)
                {
                    VariableInitializer variable = declaration.Variables.First();
                    ILVariable v = VariableOf(variable);
                    if (v == null || !v.Index.HasValue) { possible = false; break; }
                    foreach (var other in outer.Descendants.OfType<VariableInitializer>().Where(o => o != variable && o.Name == variable.Name))
                    {
                        ILVariable w = VariableOf(other);
                        var otherDeclaration = other.Parent as VariableDeclarationStatement;
                        if (w == null || !w.Index.HasValue || w.Index.Value != v.Index.Value || w.Function != v.Function || otherDeclaration == null
                            || otherDeclaration.Variables.Count != 1 || other.Initializer.IsNull || !(otherDeclaration.Parent is BlockStatement)) { possible = false; break; }
                        again.Add(otherDeclaration);
                    }
                    if (!possible) break;
                }
                if (!possible) continue;

                foreach (var declaration in hoisted)
                {
                    VariableInitializer variable = declaration.Variables.First();
                    outer.Statements.InsertBefore(statement, new VariableDeclarationStatement(declaration.Type.Clone(), variable.Name));
                }
                foreach (var declaration in hoisted.Concat(again))
                {
                    VariableInitializer variable = declaration.Variables.First();
                    declaration.ReplaceWith(new ExpressionStatement(new AssignmentExpression(new IdentifierExpression(variable.Name), variable.Initializer.Detach())));
                }
                foreach (Statement m in moved) { m.Remove(); outer.Statements.Add(m); }
                body.Statements.Add(new BreakStatement());
                break;
            }
        }
    }

    // 6. Labels of a 'switch' on a string that do nothing.
    //        switch (s) { case "a": case "b": break; case "c": C; break; }
    //            IL: s; "a"; call op_Equality; brtrue END; s; "b"; call op_Equality; brtrue END; s; "c"; ...; brtrue LC; br END
    //    is printed without them: their tests are in the IL, and jump where the 'switch' ends.
    private static void IdleStringLabels(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var statement in rootNode.Descendants.OfType<SwitchStatement>().ToList())
        {
            var sections = statement.SwitchSections.ToList();
            if (sections.Count == 0) continue;
            var written = new Dictionary<string, ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection>();
            bool strings = true;
            foreach (var section in sections)
                foreach (CaseLabel label in section.CaseLabels)
                {
                    var literal = label.Expression.IsNull ? null : Unwrap(label.Expression) as PrimitiveExpression;
                    if (literal == null || !(literal.Value is string) || written.ContainsKey((string)literal.Value)) { strings = false; continue; }
                    written[(string)literal.Value] = section;
                }
            if (!strings || written.Count == 0) continue;
            ILInstruction subject = null;
            foreach (AstNode n in statement.DescendantsAndSelf)
            {
                subject = n.Annotations.OfType<ILInstruction>().FirstOrDefault();
                if (subject != null) break;
            }
            IMethod method = MethodOf(subject);
            if (method == null) continue;
            RawIL raw;
            if (!bodies.TryGetValue(method, out raw)) { raw = RawIL.Read(context.TypeSystem, method); bodies[method] = raw; }
            if (raw == null) continue;
            // The tests of the labels the statement has: one run of 'load; ldstr; call; brtrue'.
            var o = raw.Ops;
            System.Func<int, bool> isTest = i => i >= 1 && i + 2 < o.Count && raw.String(o[i]) != null && o[i + 1].Code == ILOpCode.Call
                && (o[i + 2].Code == ILOpCode.Brtrue || o[i + 2].Code == ILOpCode.Brtrue_s) && raw.Method(o[i + 1]) != null && raw.Method(o[i + 1]).Name == "op_Equality"
                && (RawIL.IsLdLoc(o[i - 1].Code) || RawIL.IsLdArg(o[i - 1].Code));
            string firstLabel = written.Keys.First();
            var places = Enumerable.Range(0, o.Count).Where(i => isTest(i) && raw.String(o[i]) == firstLabel).ToList();
            if (places.Count != 1) continue;
            int first = places[0];
            while (isTest(first - 4)) first -= 4;
            var tests = new List<int>();
            for (int i = first; isTest(i); i += 4) tests.Add(i);
            int after = tests[tests.Count - 1] + 3;
            if (after >= o.Count || (o[after].Code != ILOpCode.Br && o[after].Code != ILOpCode.Br_s)) continue;
            int end = o[after].Operand;
            if (tests.Count(t => written.ContainsKey(raw.String(o[t]))) != written.Count) continue;
            var idle = new List<string>();
            ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection pending = null;
            bool sound = true;
            var added = new List<KeyValuePair<List<string>, ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection>>();
            foreach (int t in tests)
            {
                string literal = raw.String(o[t]);
                ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection section;
                if (written.TryGetValue(literal, out section))
                {
                    if (idle.Count > 0) { added.Add(new KeyValuePair<List<string>, ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection>(idle, section)); idle = new List<string>(); }
                    pending = section;
                    continue;
                }
                if (o[t + 2].Operand != end || idle.Contains(literal)) { sound = false; break; }
                idle.Add(literal);
            }
            if (!sound) continue;
            if (idle.Count > 0) added.Add(new KeyValuePair<List<string>, ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection>(idle, null));
            foreach (var pair in added)
            {
                var section = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
                foreach (string literal in pair.Key) section.CaseLabels.Add(new CaseLabel(new PrimitiveExpression(literal)));
                section.Statements.Add(new BreakStatement());
                if (pair.Value != null) statement.SwitchSections.InsertBefore(pair.Value, section);
                else statement.SwitchSections.Add(section);
            }
        }
    }

    // 7. 'continue;' inside a protected block.
    //        try { if (a) continue;  switch (x) { case 1: continue; case 2: y = 1; break; } } finally { }  break;
    //            IL: each 'continue' is a 'leave LOOP'; the block ends with a 'leave' to what follows it
    //    is printed 'try { if (!a) { switch (x) { case 1: break; case 2: y = 1; goto END; default: goto END; } } }
    //    finally { }  continue;  ...  END: break;', which leaves the block once, at its end, for the loop.
    private static void ContinuesInTry(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var protectedStatement in rootNode.Descendants.OfType<TryCatchStatement>().ToList())
        {
            var loopExit = protectedStatement.NextSibling as ContinueStatement;
            if (loopExit == null || protectedStatement.Parent == null) continue;
            // 'if' without 'else' at the end of the block, any number of times, then a 'switch'.
            var wrappers = new List<IfElseStatement>();
            BlockStatement list = protectedStatement.TryBlock;
            SwitchStatement choice = null;
            while (true)
            {
                Statement last = list.Statements.LastOrDefault();
                var test = last as IfElseStatement;
                if (test != null && test.FalseStatement.IsNull && test.TrueStatement is BlockStatement) { wrappers.Add(test); list = (BlockStatement)test.TrueStatement; continue; }
                choice = last as SwitchStatement;
                break;
            }
            if (choice == null) continue;
            var jumps = protectedStatement.Descendants.OfType<GotoStatement>().ToList();
            if (jumps.Count == 0 || jumps.Select(j => j.Label).Distinct().Count() != 1) continue;
            string name = jumps[0].Label;
            var entity = protectedStatement.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (entity == null || entity.Descendants.OfType<GotoStatement>().Count(j => j.Label == name) != jumps.Count) continue;
            var labels = entity.Descendants.OfType<LabelStatement>().Where(l => l.Label == name).ToList();
            if (labels.Count != 1) continue;
            var landing = labels[0].NextSibling as Statement;
            var beforeLabel = labels[0].PrevSibling as Statement;
            if (!(landing is BreakStatement) || !(beforeLabel is ContinueStatement || beforeLabel is ReturnStatement || beforeLabel is BreakStatement || beforeLabel is ThrowStatement)) continue;
            bool shaped = true;
            foreach (var section in choice.SwitchSections)
            {
                Statement last = section.Statements.LastOrDefault();
                var block = last as BlockStatement;
                if (block != null && section.Statements.Count == 1) last = block.Statements.LastOrDefault();
                if (!(last is BreakStatement || last is GotoStatement || last is ContinueStatement || last is ReturnStatement || last is ThrowStatement)) shaped = false;
                foreach (var jump in section.Descendants.OfType<GotoStatement>()) if (jump != last) shaped = false;
                foreach (var jump in section.Descendants.OfType<BreakStatement>()) if (jump != last) shaped = false;
            }
            if (!shaped || jumps.Any(j => !j.Ancestors.Contains(choice))) continue;
            SwitchInstruction inst = null;
            foreach (var a in choice.Annotations.OfType<ILInstruction>())
            {
                if (a is SwitchInstruction) inst = (SwitchInstruction)a;
                var container = a as BlockContainer;
                if (inst == null && container != null && container.EntryPoint.Instructions.Count > 0) inst = container.EntryPoint.Instructions[0] as SwitchInstruction;
            }
            IMethod method = MethodOf(inst);
            if (method == null) continue;
            RawIL raw;
            if (!bodies.TryGetValue(method, out raw)) { raw = RawIL.Read(context.TypeSystem, method); bodies[method] = raw; }
            if (raw == null) continue;
            int at = -1;
            RawIL.Op probe;
            foreach (var r in inst.ILRanges)
                if (raw.IsAt(r.Start, out probe) && probe.Code == ILOpCode.Switch) at = r.Start;
            if (at < 0) continue;
            // The innermost protected block around the 'switch' ends with a 'leave' to a place
            // of the loop body, and its other 'leave' all go further: to the bottom of the
            // loop, where 'continue' goes. (With 'continue;' after the block it is the last
            // 'leave' that goes there.)
            int[] region = null;
            foreach (int[] r in raw.Regions)
                if (r[0] <= at && at < r[1] && (region == null || r[1] - r[0] < region[1] - region[0])) region = r;
            if (region == null) continue;
            RawIL.Op final;
            if (!raw.EndsAt(region[1], out final) || (final.Code != ILOpCode.Leave && final.Code != ILOpCode.Leave_s)) continue;
            var others = raw.Ops.Where(o => o.Offset >= region[0] && o.Offset < final.Offset && (o.Code == ILOpCode.Leave || o.Code == ILOpCode.Leave_s)
                && !raw.Regions.Any(r => r[0] >= region[0] && r[3] <= region[1] && o.Offset >= r[0] && o.Offset < r[3])).ToList();
            int elsewhere = others.Count;
            if (others.Any(o => o.Operand != others[0].Operand || o.Operand <= final.Operand)) continue;
            if (elsewhere < 2) continue;

            foreach (var section in choice.SwitchSections.ToList())
            {
                Statement last = section.Statements.LastOrDefault();
                var block = last as BlockStatement;
                if (block != null && section.Statements.Count == 1) last = block.Statements.LastOrDefault();
                if (last is BreakStatement) last.ReplaceWith(new ContinueStatement());
                else if (last is GotoStatement)
                {
                    bool alone = section.Statements.Count == 1 && section.CaseLabels.Count == 1 && section.CaseLabels.First().Expression.IsNull;
                    if (alone) section.Remove(); else last.ReplaceWith(new BreakStatement());
                }
            }
            foreach (var test in wrappers)
            {
                var block = (BlockStatement)test.TrueStatement;
                var parent = (BlockStatement)test.Parent;
                var inner = block.Statements.ToList();
                foreach (Statement s in inner) { s.Remove(); parent.Statements.Add(s); }
                Expression condition = test.Condition.Detach();
                var comparison = Unwrap(condition) as BinaryOperatorExpression;
                if (comparison != null && comparison.Operator == BinaryOperatorType.InEquality) comparison.Operator = BinaryOperatorType.Equality;
                else if (comparison != null && comparison.Operator == BinaryOperatorType.Equality) comparison.Operator = BinaryOperatorType.InEquality;
                else condition = new UnaryOperatorExpression(UnaryOperatorType.Not, new ParenthesizedExpression(condition));
                test.Condition = condition;
                test.TrueStatement = new BlockStatement { new ContinueStatement() };
            }
            loopExit.ReplaceWith(landing.Clone());
            landing.Remove();
            labels[0].Remove();
        }
    }

    // 8. Protected blocks that end one in the other.
    //        try { A } catch { B; try { C } catch { D } }
    //            IL: A; leave E2; B; C; leave E1; D; leave E1; E1: leave E2; E2:
    //    is printed with 'goto END;' at the end of the inner blocks: the decompiler follows
    //    each 'leave' that lands on another one. A 'goto' of the source jumps there directly.
    private static void LeaveChains(AstNode rootNode, TransformContext context)
    {
        var touched = new HashSet<AstNode>();
        foreach (var jump in rootNode.Descendants.OfType<GotoStatement>().ToList())
        {
            var block = jump.Parent as BlockStatement;
            if (block == null || block.Statements.LastOrDefault() != jump) continue;
            bool redundant = false;
            AstNode node = block;
            while (true)
            {
                TryCatchStatement outer = null;
                if (node.Parent is TryCatchStatement && ((TryCatchStatement)node.Parent).TryBlock == node) outer = (TryCatchStatement)node.Parent;
                else if (node.Parent is CatchClause) outer = node.Parent.Parent as TryCatchStatement;
                if (outer == null) break;
                AstNode next = outer.NextSibling;
                var label = next as LabelStatement;
                if (label != null) { redundant = label.Label == jump.Label; break; }
                var parent = outer.Parent as BlockStatement;
                if (next is Statement || parent == null || parent.Statements.LastOrDefault() != outer) break;
                node = parent;
            }
            if (!redundant) continue;
            var entity = jump.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            var method = entity == null ? null : entity.GetSymbol() as IMethod;
            if (method == null || InNestedFunction(jump, entity)) continue;
            RawIL raw = RawIL.Read(context.TypeSystem, method);
            if (raw == null) continue;
            bool chained = false;
            foreach (RawIL.Op op in raw.Ops)
            {
                RawIL.Op landing;
                if ((op.Code == ILOpCode.Leave || op.Code == ILOpCode.Leave_s) && raw.IsAt(op.Operand, out landing)
                    && (landing.Code == ILOpCode.Leave || landing.Code == ILOpCode.Leave_s) && raw.RegionEnds.Contains(landing.End)) chained = true;
            }
            if (!chained) continue;
            touched.Add(entity);
            jump.Remove();
        }
        foreach (AstNode entity in touched)
            foreach (var label in entity.Descendants.OfType<LabelStatement>().ToList())
            {
                if (entity.Descendants.OfType<GotoStatement>().Any(g => g.Label == label.Label)) continue;
                var empty = label.NextSibling as EmptyStatement;
                if (empty != null) empty.Remove();
                else if (label.NextSibling is Statement) continue;
                label.Remove();
            }
    }

    private static void PatternSwitches(AstNode rootNode, TransformContext context)
    {
        foreach (var top in rootNode.Descendants.OfType<IfElseStatement>().ToList())
        {
            if (top.Parent == null || !top.FalseStatement.IsNull || !(top.Parent is BlockStatement)) continue;
            var not = Unwrap(top.Condition) as UnaryOperatorExpression;
            if (not == null || not.Operator != UnaryOperatorType.Not) continue;
            IdentifierExpression subject1, subject2;
            DeclarationExpression first, second;
            if (!TypeTest(not.Expression, out subject1, out first)) continue;
            var inner = top.TrueStatement as BlockStatement;
            if (inner == null || inner.Statements.Count < 2) continue;
            var nested = inner.Statements.First() as IfElseStatement;
            if (nested == null || !nested.FalseStatement.IsNull || !TypeTest(nested.Condition, out subject2, out second)) continue;
            if (subject1.Identifier != subject2.Identifier) continue;
            if (!EndsWithJump(nested.TrueStatement) || !EndsWithJump(inner)) continue;
            var after = new List<Statement>();
            for (AstNode n = top.NextSibling; n != null; n = n.NextSibling) { var s = n as Statement; if (s != null) after.Add(s); }
            if (after.Count == 0 || !EndsWithJump(after[after.Count - 1]) || after.Any(s => s is LabelStatement)) continue;
            var entity = top.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            var method = entity == null ? null : entity.GetSymbol() as IMethod;
            if (method == null || InNestedFunction(top, entity)) continue;
            if (top.Descendants.OfType<BreakStatement>().Any() || after.Any(s => s.DescendantsAndSelf.OfType<BreakStatement>().Any())) continue;
            RawIL raw = RawIL.Read(context.TypeSystem, method);
            if (raw == null) continue;
            // Both tests jump to their sections, and the first section follows the jump to 'default'.
            int found = 0;
            for (int i = 0; i + 9 < raw.Ops.Count; i++)
            {
                var o = raw.Ops;
                if (o[i].Code != ILOpCode.Isinst || !RawIL.IsStLoc(o[i + 1].Code) || !RawIL.IsLdLoc(o[i + 2].Code) || o[i + 1].Operand != o[i + 2].Operand) continue;
                if (o[i + 3].Code != ILOpCode.Brtrue && o[i + 3].Code != ILOpCode.Brtrue_s) continue;
                if (o[i + 5].Code != ILOpCode.Isinst || !RawIL.IsStLoc(o[i + 6].Code) || !RawIL.IsLdLoc(o[i + 7].Code) || o[i + 6].Operand != o[i + 7].Operand) continue;
                if (o[i + 8].Code != ILOpCode.Brtrue && o[i + 8].Code != ILOpCode.Brtrue_s) continue;
                if (o[i + 9].Code != ILOpCode.Br && o[i + 9].Code != ILOpCode.Br_s) continue;
                if (o[i + 3].Operand != o[i + 9].End || o[i + 8].Operand <= o[i + 3].Operand || o[i + 9].Operand <= o[i + 8].Operand) continue;
                found++;
            }
            if (found != 1) continue;
            if (entity.Descendants.OfType<BinaryOperatorExpression>().Count(b => b.Operator == BinaryOperatorType.IsPattern) != 2) continue;

            var statement = new SwitchStatement();
            statement.Expression = subject1.Clone();
            var one = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
            one.CaseLabels.Add(new CaseLabel(first.Detach()));
            foreach (Statement s in after) { s.Remove(); one.Statements.Add(s); }
            statement.SwitchSections.Add(one);
            var two = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
            two.CaseLabels.Add(new CaseLabel(second.Detach()));
            two.Statements.Add(nested.TrueStatement.Detach());
            statement.SwitchSections.Add(two);
            nested.Remove();
            var otherwise = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
            otherwise.CaseLabels.Add(new CaseLabel());
            foreach (Statement s in inner.Statements.ToList()) { s.Remove(); otherwise.Statements.Add(s); }
            statement.SwitchSections.Add(otherwise);
            top.ReplaceWith(statement);
        }
    }
}
