// C# transform: layout of statements that only the syntax can carry, decided by the raw body.
//
// 1. 'else' that leaves, before what follows a 'switch'.
//        if (c) { switch (x) { case A: ...; return a; case B: return b; } } else { return d; } R
//    IL: c; brfalse ELSE; tests; br REST; sections; ELSE: d; ret; REST: R
//    is printed 'if (c) { switch (x) { ...; default: R } } return d;', which puts R before
//    'return d'.
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreLayoutSyntax : IAstTransform
{
    public void Run(AstNode rootNode, TransformContext context)
    {
        ElseBeforeDefault(rootNode, context);
        ForIncrementLocals(rootNode, context);
        TypeSwitches(rootNode, context);
        RetriedIfs(rootNode, context);
        ForeachCollections(rootNode, context);
    }

    private static ILVariable VariableOf(Expression e)
    {
        var id = e as IdentifierExpression;
        var rr = id == null ? null : id.GetResolveResult() as ILVariableResolveResult;
        return rr == null ? null : rr.Variable;
    }

    // 2. 'for' whose increment reads a variable of the body.
    //        N nx;  for (...; c != null; c = nx) { ...; nx = c.next; ... }
    //    IL: ...; stloc nx; ...; ldloc nx; stloc c; <condition>
    //    is printed 'while (c != null) { ...; N nx = c.next; ...; c = nx; }', where nx is stored
    //    once and read once with nothing under it on the stack: the compiler keeps it there and
    //    there is no slot. The increment of a 'for' is behind a label, the slot stays.
    private static void ForIncrementLocals(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var loop in rootNode.Descendants.OfType<WhileStatement>().ToList())
        {
            var body = loop.EmbeddedStatement as BlockStatement;
            var parent = loop.Parent as BlockStatement;
            if (body == null || parent == null || body.Statements.Count < 2) continue;
            var step = body.Statements.Last() as ExpressionStatement;
            var assign = step == null ? null : step.Expression as AssignmentExpression;
            if (assign == null || assign.Operator != AssignmentOperatorType.Assign) continue;
            ILVariable target = VariableOf(assign.Left), source = VariableOf(assign.Right);
            if (target == null || source == null || target == source || source.Kind != VariableKind.Local || !source.Index.HasValue || !target.Index.HasValue) continue;
            if (target.Kind != VariableKind.Local || source.Function == null || source.Function.Method == null || source.Function != target.Function) continue;
            string name = ((IdentifierExpression)assign.Right).Identifier;
            VariableDeclarationStatement declaration = null;
            foreach (var d in body.Statements.OfType<VariableDeclarationStatement>())
                if (d.Variables.Count == 1 && d.Variables.First().Name == name && !d.Variables.First().Initializer.IsNull) declaration = d;
            if (declaration == null) continue;
            if (!loop.Condition.DescendantsAndSelf.OfType<IdentifierExpression>().Any(i => VariableOf(i) == target)) continue;
            // No 'continue' of this loop: it would skip the increment.
            bool skips = false;
            foreach (var jump in body.Descendants.OfType<ContinueStatement>())
            {
                bool inside = false;
                for (AstNode p = jump.Parent; p != null && p != body; p = p.Parent)
                    if (p is ForStatement || p is ForeachStatement || p is WhileStatement || p is DoWhileStatement) inside = true;
                if (!inside) skips = true;
            }
            if (skips) continue;
            RawIL raw;
            if (!bodies.TryGetValue(source.Function.Method, out raw)) { raw = RawIL.Read(context.TypeSystem, source.Function.Method); bodies[source.Function.Method] = raw; }
            if (raw == null) continue;
            int slot = source.Index.Value, stores = 0, loads = 0, others = 0, at = -1;
            for (int i = 0; i < raw.Ops.Count; i++)
            {
                RawIL.Op op = raw.Ops[i];
                if (RawIL.IsStLoc(op.Code) && op.Operand == slot) stores++;
                if (RawIL.IsLdLoc(op.Code) && op.Operand == slot) { loads++; at = i; }
                if ((op.Code == ILOpCode.Ldloca || op.Code == ILOpCode.Ldloca_s) && op.Operand == slot) others++;
            }
            if (stores != 1 || loads != 1 || others != 0 || at + 1 >= raw.Ops.Count) continue;
            RawIL.Op next = raw.Ops[at + 1];
            if (!RawIL.IsStLoc(next.Code) || next.Operand != target.Index.Value) continue;

            var statement = new ForStatement();
            var empty = new VariableDeclarationStatement(declaration.Type.Clone(), name);
            Expression value = declaration.Variables.First().Initializer.Detach();
            var first = new ExpressionStatement(new AssignmentExpression(new IdentifierExpression(name).CopyAnnotationsFrom(assign.Right), value));
            declaration.ReplaceWith(first);
            step.Remove();
            statement.Condition = loop.Condition.Detach();
            statement.Iterators.Add(step);
            statement.EmbeddedStatement = body.Detach();
            parent.Statements.InsertBefore(loop, empty);
            loop.ReplaceWith(statement);
        }
    }

    // 3. 'switch' on the type of a value.
    //        switch (o) { case A a: X; case B b: Y; default: Z }      (every section leaves)
    //    IL: o; isinst A; brfalse N1; o; unbox.any A; stloc a; br SX; N1: o; isinst B; stloc b; ldloc b; brtrue SY; br SZ; SX: ...
    //    has its tests first, each one jumping to its section. Is printed
    //    'if (!(o is A a)) { if (o is B b) { Y } Z } X', where each body follows its test.
    private static bool PatternTest(Expression condition, bool negated, out string subject, out DeclarationExpression declared)
    {
        subject = null;
        declared = null;
        while (condition is ParenthesizedExpression) condition = ((ParenthesizedExpression)condition).Expression;
        if (negated)
        {
            var not = condition as UnaryOperatorExpression;
            if (not == null || not.Operator != UnaryOperatorType.Not) return false;
            condition = not.Expression;
            while (condition is ParenthesizedExpression) condition = ((ParenthesizedExpression)condition).Expression;
        }
        var test = condition as BinaryOperatorExpression;
        if (test == null || test.Operator != BinaryOperatorType.IsPattern) return false;
        var value = test.Left as IdentifierExpression;
        declared = test.Right as DeclarationExpression;
        if (value == null || declared == null || !(declared.Designation is SingleVariableDesignation)) return false;
        subject = value.Identifier;
        return true;
    }

    private static ILVariable DeclaredVariable(DeclarationExpression declared)
    {
        foreach (AstNode n in declared.DescendantsAndSelf)
            foreach (var rr in n.Annotations.OfType<ILVariableResolveResult>()) return rr.Variable;
        return null;
    }

    private static bool Exits(IEnumerable<Statement> statements)
    {
        Statement last = statements.LastOrDefault();
        var block = last as BlockStatement;
        if (block != null) last = block.Statements.LastOrDefault();
        return last is ReturnStatement || last is ThrowStatement;
    }

    private static void TypeSwitches(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var top in rootNode.Descendants.OfType<IfElseStatement>().ToList())
        {
            var parent = top.Parent as BlockStatement;
            if (parent == null || !top.FalseStatement.IsNull) continue;
            string subject, name;
            DeclarationExpression declared;
            if (!PatternTest(top.Condition, true, out subject, out declared)) continue;
            var patterns = new List<DeclarationExpression>();
            var sections = new List<List<Statement>>();
            List<Statement> fallback = null;
            IfElseStatement test = top;
            bool shaped = true;
            while (true)
            {
                // 'if (!(o is T v)) { inner } rest': rest is the section of T.
                var rest = new List<Statement>();
                for (AstNode n = test.NextSibling; n != null; n = n.NextSibling) if (n is Statement) rest.Add((Statement)n);
                var inner = test.TrueStatement as BlockStatement;
                if (inner == null || rest.Count == 0 || !Exits(rest) || !test.FalseStatement.IsNull) { shaped = false; break; }
                patterns.Add(declared);
                sections.Add(rest);
                var next = inner.Statements.FirstOrDefault() as IfElseStatement;
                if (next == null || !next.FalseStatement.IsNull) { shaped = false; break; }
                if (PatternTest(next.Condition, true, out name, out declared) && name == subject) { test = next; continue; }
                // 'if (o is T v) { body } rest': the last section, rest is 'default'.
                if (!PatternTest(next.Condition, false, out name, out declared) || name != subject) { shaped = false; break; }
                var body = next.TrueStatement as BlockStatement;
                var after = new List<Statement>();
                for (AstNode n = next.NextSibling; n != null; n = n.NextSibling) if (n is Statement) after.Add((Statement)n);
                if (body == null || !Exits(body.Statements) || after.Count == 0 || !Exits(after)) { shaped = false; break; }
                patterns.Add(declared);
                sections.Add(body.Statements.ToList());
                fallback = after;
                break;
            }
            if (!shaped || fallback == null || patterns.Count < 2) continue;
            // In the IL each pattern variable is stored, then a jump goes to its section, past the last test.
            var variables = patterns.Select(DeclaredVariable).ToList();
            if (variables.Any(v => v == null || !v.Index.HasValue || v.Function == null || v.Function.Method == null)) continue;
            IMethod method = variables[0].Function.Method;
            RawIL raw;
            if (!bodies.TryGetValue(method, out raw)) { raw = RawIL.Read(context.TypeSystem, method); bodies[method] = raw; }
            if (raw == null) continue;
            var stores = new List<int>();
            foreach (ILVariable v in variables)
            {
                int at = -1, count = 0;
                for (int i = 0; i < raw.Ops.Count; i++)
                    if (RawIL.IsStLoc(raw.Ops[i].Code) && raw.Ops[i].Operand == v.Index.Value) { at = i; count++; }
                stores.Add(count == 1 ? at : -1);
            }
            if (stores.Any(s => s < 0 || s + 2 >= raw.Ops.Count)) continue;
            int lastStore = raw.Ops[stores.Max()].Offset;
            bool tested = true;
            foreach (int s in stores)
            {
                RawIL.Op next = raw.Ops[s + 1], then = raw.Ops[s + 2];
                bool byJump = (next.Code == ILOpCode.Br || next.Code == ILOpCode.Br_s) && next.Operand > lastStore;
                bool byTest = RawIL.IsLdLoc(next.Code) && next.Operand == raw.Ops[s].Operand && (then.Code == ILOpCode.Brtrue || then.Code == ILOpCode.Brtrue_s) && then.Operand > lastStore;
                if (!byJump && !byTest) tested = false;
            }
            if (!tested) continue;

            var statement = new SwitchStatement();
            statement.Expression = new IdentifierExpression(subject);
            for (int i = 0; i < patterns.Count; i++)
            {
                var section = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
                patterns[i].Remove();
                section.CaseLabels.Add(new CaseLabel(patterns[i]));
                foreach (Statement s in sections[i]) { s.Remove(); section.Statements.Add(s); }
                statement.SwitchSections.Add(section);
            }
            var last = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
            last.CaseLabels.Add(new CaseLabel());
            foreach (Statement s in fallback) { s.Remove(); last.Statements.Add(s); }
            statement.SwitchSections.Add(last);
            top.ReplaceWith(statement);
        }
    }

    // 4. 'if' entered again by a 'goto'.
    //        again: if (c) { ...; goto again; ... }       IL: A: c; brfalse END; ...; br A; ...; END:
    //    is printed 'while (c) { ...; continue; ...; break; }', which the compiler tests at the
    //    bottom ('br C; B: ...; C: c; brtrue B'). Here the test stands before the body and the
    //    body does not end with a jump back.
    private static void RetriedIfs(AstNode rootNode, TransformContext context)
    {
        foreach (var loop in rootNode.Descendants.OfType<WhileStatement>().ToList())
        {
            var body = loop.EmbeddedStatement as BlockStatement;
            var parent = loop.Parent as BlockStatement;
            if (body == null || parent == null || body.Statements.Count < 2 || !(body.Statements.Last() is BreakStatement)) continue;
            var literal = loop.Condition as PrimitiveExpression;
            if (literal != null) continue;
            var jumps = new List<ContinueStatement>();
            bool other = false;
            foreach (AstNode n in body.Descendants)
            {
                if (!(n is ContinueStatement) && !(n is BreakStatement)) continue;
                if (n == body.Statements.Last()) continue;
                bool inside = false;
                for (AstNode p = n.Parent; p != null && p != body; p = p.Parent)
                    if (p is ForStatement || p is ForeachStatement || p is WhileStatement || p is DoWhileStatement || (p is SwitchStatement && n is BreakStatement)) inside = true;
                if (inside) continue;
                if (n is ContinueStatement) jumps.Add((ContinueStatement)n); else other = true;
            }
            if (other || jumps.Count == 0) continue;
            int test = FirstOffset(loop.Condition), first = FirstOffset(body.Statements.First());
            if (test < 0 || first < 0 || test >= first) continue;
            string name = "again_" + test.ToString("x4");
            foreach (ContinueStatement jump in jumps) jump.ReplaceWith(new GotoStatement(name));
            body.Statements.Last().Remove();
            var statement = new IfElseStatement();
            statement.Condition = loop.Condition.Detach();
            statement.TrueStatement = body.Detach();
            parent.Statements.InsertBefore(loop, new LabelStatement { Label = name });
            loop.ReplaceWith(statement);
        }
    }

    // 5. Collection of a 'foreach' over an array.
    //        foreach (T x in e) { }            IL: e; stloc t; ldc.i4.0; stloc i; br C; ...
    //    is printed 'a = e; foreach (T x in a) { }' when several loops share the slot of t (the
    //    decompiler takes a slot stored twice for a variable), and 'a = e; for (int i = 0;
    //    i < a.Length; i++) { ... a[i] ... }' when the element is read once (it stays on the
    //    stack: no variable is left). A variable assigned several times has a slot of its own,
    //    and each 'foreach' copies it. The slot is written only in front of 'ldc.i4.0; stloc i;
    //    br': it is the copy the compiler iterates on.
    private static void ForeachCollections(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        var done = new HashSet<ILVariable>();
        foreach (var loop in rootNode.Descendants.OfType<ForeachStatement>().ToList())
        {
            ILVariable v = VariableOf(loop.InExpression);
            if (v == null || v.Kind != VariableKind.Local || !v.Index.HasValue || v.Type.Kind != TypeKind.Array || v.Function == null || v.Function.Method == null || !done.Add(v)) continue;
            var entity = loop.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (entity == null) continue;
            // Every use: the collection of a loop, and the assignment right before it.
            var pairs = new List<KeyValuePair<Statement, Statement>>();
            var seen = new HashSet<AstNode>();
            bool only = true;
            foreach (var id in entity.Descendants.OfType<IdentifierExpression>().Where(i => VariableOf(i) == v).ToList())
            {
                Statement user;
                if (id.Parent is ForeachStatement) user = (Statement)id.Parent;
                else if (id.Parent is AssignmentExpression && ((AssignmentExpression)id.Parent).Left == id && id.Parent.Parent is ExpressionStatement) user = (Statement)id.Parent.Parent;
                else user = id.Ancestors.OfType<ForStatement>().FirstOrDefault();
                if (user is ExpressionStatement)
                {
                    // 'a = e;' in front of a loop over a.
                    var assign = ((ExpressionStatement)user).Expression as AssignmentExpression;
                    if (assign == null || assign.Left != id || assign.Operator != AssignmentOperatorType.Assign || !(user.NextSibling is ForeachStatement || user.NextSibling is ForStatement)) { only = false; break; }
                    continue;
                }
                var each = user as ForeachStatement;
                var counted = user as ForStatement;
                if (each != null && each.InExpression != id) { only = false; break; }
                if (counted != null && !CountsOver(counted, v)) { only = false; break; }
                if (user == null) { only = false; break; }
                if (!seen.Add(user)) continue;
                Statement before = user.PrevSibling as Statement;
                var set = before as ExpressionStatement;
                var assigned = set == null ? null : set.Expression as AssignmentExpression;
                var declared = before as VariableDeclarationStatement;
                bool sets = assigned != null && assigned.Operator == AssignmentOperatorType.Assign && VariableOf(assigned.Left) == v;
                bool declares = declared != null && declared.Variables.Count == 1 && !declared.Variables.First().Initializer.IsNull
                    && declared.Variables.First().Annotations.OfType<ILVariableResolveResult>().Any(r => r.Variable == v);
                if (!sets && !declares) { only = false; break; }
                pairs.Add(new KeyValuePair<Statement, Statement>(user, before));
            }
            // (One loop alone: the variable stays on the stack, both forms give the same instructions.)
            if (!only || pairs.Count < 2) continue;
            int declarations = entity.Descendants.OfType<VariableInitializer>().Count(d => d.Annotations.OfType<ILVariableResolveResult>().Any(r => r.Variable == v));
            if (declarations != pairs.Count(p => p.Value is VariableDeclarationStatement)) continue;
            RawIL raw;
            if (!bodies.TryGetValue(v.Function.Method, out raw)) { raw = RawIL.Read(context.TypeSystem, v.Function.Method); bodies[v.Function.Method] = raw; }
            if (raw == null) continue;
            int stores = 0;
            bool shaped = true;
            for (int i = 0; i + 3 < raw.Ops.Count; i++)
            {
                if (!RawIL.IsStLoc(raw.Ops[i].Code) || raw.Ops[i].Operand != v.Index.Value) continue;
                stores++;
                if (raw.Ops[i + 1].Code != ILOpCode.Ldc_i4_0 || !RawIL.IsStLoc(raw.Ops[i + 2].Code) || (raw.Ops[i + 3].Code != ILOpCode.Br && raw.Ops[i + 3].Code != ILOpCode.Br_s)) shaped = false;
            }
            if (!shaped || stores < pairs.Count) continue;
            var names = new HashSet<string>(entity.Descendants.OfType<Identifier>().Select(n => n.Name));
            foreach (var pair in pairs)
            {
                Expression value;
                var declared = pair.Value as VariableDeclarationStatement;
                if (declared != null) value = declared.Variables.First().Initializer.Detach();
                else value = ((AssignmentExpression)((ExpressionStatement)pair.Value).Expression).Right.Detach();
                pair.Value.Remove();
                var each = pair.Key as ForeachStatement;
                if (each != null) { each.InExpression = value; continue; }
                var counted = (ForStatement)pair.Key;
                string element = "element";
                for (int n = 2; names.Contains(element); n++) element = "element" + n;
                names.Add(element);
                IndexerExpression read = counted.EmbeddedStatement.Descendants.OfType<IndexerExpression>().First(x => VariableOf(x.Target) == v);
                read.ReplaceWith(new IdentifierExpression(element));
                var replacement = new ForeachStatement
                {
                    VariableType = context.TypeSystemAstBuilder.ConvertType(((ArrayType)v.Type).ElementType),
                    VariableDesignation = new SingleVariableDesignation { Identifier = element },
                    InExpression = value,
                    EmbeddedStatement = counted.EmbeddedStatement.Detach()
                };
                counted.ReplaceWith(replacement);
            }
        }
    }

    // 'for (int i = 0; i < a.Length; i++) { ... a[i] ... }' with one read of the element, and
    // no other use of i or a.
    private static bool CountsOver(ForStatement loop, ILVariable v)
    {
        if (loop.Initializers.Count != 1 || loop.Iterators.Count != 1) return false;
        var start = loop.Initializers.First() as VariableDeclarationStatement;
        if (start == null || start.Variables.Count != 1) return false;
        var zero = start.Variables.First().Initializer as PrimitiveExpression;
        if (zero == null || !(zero.Value is int) || (int)zero.Value != 0) return false;
        string index = start.Variables.First().Name;
        var bound = loop.Condition as BinaryOperatorExpression;
        if (bound == null || bound.Operator != BinaryOperatorType.LessThan) return false;
        var counter = bound.Left as IdentifierExpression;
        var length = bound.Right as MemberReferenceExpression;
        if (counter == null || counter.Identifier != index || length == null || length.MemberName != "Length" || VariableOf(length.Target) != v) return false;
        var step = loop.Iterators.First() as ExpressionStatement;
        var increment = step == null ? null : step.Expression as UnaryOperatorExpression;
        if (increment == null || (increment.Operator != UnaryOperatorType.PostIncrement && increment.Operator != UnaryOperatorType.Increment)) return false;
        var stepped = increment.Expression as IdentifierExpression;
        if (stepped == null || stepped.Identifier != index) return false;
        // In the body: 'a[i]' once, in the first statement, nothing else on a or i.
        var body = loop.EmbeddedStatement as BlockStatement;
        if (body == null || body.Statements.Count == 0) return false;
        var onIndex = body.Descendants.OfType<IdentifierExpression>().Where(x => x.Identifier == index).ToList();
        var onArray = body.Descendants.OfType<IdentifierExpression>().Where(x => VariableOf(x) == v).ToList();
        if (onIndex.Count != 1 || onArray.Count != 1) return false;
        var read = onArray[0].Parent as IndexerExpression;
        if (read == null || read.Target != onArray[0] || read.Arguments.Count != 1 || read.Arguments.First() != onIndex[0]) return false;
        if (read.Parent is AssignmentExpression && ((AssignmentExpression)read.Parent).Left == read) return false;
        return read.Ancestors.Contains(body.Statements.First());
    }

    internal static int FirstOffset(AstNode node)
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

    private static bool Leaves(Statement s)
    {
        var block = s as BlockStatement;
        if (block != null) s = block.Statements.LastOrDefault();
        return s is ReturnStatement || s is ThrowStatement;
    }

    private static bool HasBreak(AstNode root)
    {
        foreach (var jump in root.Descendants.OfType<BreakStatement>())
        {
            bool inside = false;
            for (AstNode p = jump.Parent; p != null && p != root; p = p.Parent)
                if (p is ForStatement || p is ForeachStatement || p is WhileStatement || p is DoWhileStatement || p is SwitchStatement) inside = true;
            if (!inside) return true;
        }
        return false;
    }

    private static void ElseBeforeDefault(AstNode rootNode, TransformContext context)
    {
        foreach (var test in rootNode.Descendants.OfType<IfElseStatement>().ToList())
        {
            if (test.Parent == null || !test.FalseStatement.IsNull) continue;
            var after = test.NextSibling as ReturnStatement;
            if (after == null || after.Expression.IsNull || after.NextSibling is Statement) continue;
            var body = test.TrueStatement as BlockStatement;
            Statement only = body != null && body.Statements.Count == 1 ? body.Statements.First() : body == null ? test.TrueStatement : null;
            var statement = only as SwitchStatement;
            if (statement == null) continue;
            var sections = statement.SwitchSections.ToList();
            if (sections.Count < 2) continue;
            var fallback = sections[sections.Count - 1];
            if (fallback.CaseLabels.Count != 1 || !fallback.CaseLabels.First().Expression.IsNull) continue;
            bool closed = true;
            int last = -1;
            foreach (var section in sections)
            {
                if (section.Statements.Count == 0 || !Leaves(section.Statements.Last()) || HasBreak(section)) { closed = false; break; }
                if (section.Descendants.OfType<GotoCaseStatement>().Any() || section.Descendants.OfType<GotoDefaultStatement>().Any()) { closed = false; break; }
                if (section == fallback) continue;
                int start = FirstOffset(section);
                if (start < 0) { closed = false; break; }
                if (start > last) last = start;
            }
            if (!closed) continue;
            int rest = FirstOffset(fallback), other = FirstOffset(after);
            // The 'return' after the 'if' stands between the labelled sections and 'default'.
            if (rest < 0 || other < 0 || !(last < other && other < rest)) continue;
            var moved = fallback.Statements.ToList();
            if (moved.Count == 1 && moved[0] is BlockStatement) moved = ((BlockStatement)moved[0]).Statements.ToList();
            if (moved.Any(m => m is VariableDeclarationStatement || m is LabelStatement)) continue;
            fallback.Remove();
            after.Remove();
            var otherwise = new BlockStatement();
            otherwise.Statements.Add(after);
            AstNode at = test;
            var parent = test.Parent as BlockStatement;
            if (parent == null) continue;
            test.FalseStatement = otherwise;
            foreach (Statement m in moved)
            {
                m.Remove();
                parent.Statements.InsertAfter((Statement)at, m);
                at = m;
            }
        }
    }
}
