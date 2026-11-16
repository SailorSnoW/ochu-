// C# transform: shapes that only the syntax can carry, decided by the raw method body.
//
// 1. Switch with a section per label.
//        switch (e) { case A: break; case B: break; default: body; break; }
//    IL: e; stloc t; ldloc t; A; beq END; ldloc t; B; beq END; body; END:
//    is printed 'T x = e; if (x != A && x != B) { body }'. The slot of t is used again by
//    other temporaries of the method: the compiler never shares the slot of a variable of
//    the source. ('case A: case B:' on neighbouring values is a range test instead.)
//
// 2. Switch on a string with few labels.
//        switch (s) { case "a": A; break; case "b": B; break; default: C; break; }
//    IL: s; "a"; call op_Equality; brtrue LA; s; "b"; call op_Equality; brtrue LB; br LC; LA: A ...
//    is printed as a chain of 'if', which falls into each body ('brfalse').
//
// 3. Chain of 'if' on one value.
//        if (x == 0) A; else if (x == 1) B; else C;
//    IL: x; brtrue N1; A; br END; N1: x; 1; bne.un N2; B; br END; N2: C; END:
//    is printed as a 'switch', which has its tests first ('beq LA; beq LB; br LC').
//
// 4. Sections of a 'switch' on a string, in the order of the IL: the compiler emits them in
//    the order of the source. Each label is found by its literal ('ldstr; call op_Equality;
//    brtrue SECTION').
//
// 5. Section that is the only one to reach the end of its 'switch' on a string: what it does
//    is printed after the 'switch' ('case "a": break;'). The statements that the IL has
//    before the other sections are in that section.
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreShapesSyntax : IAstTransform
{
    public void Run(AstNode rootNode, TransformContext context)
    {
        SectionSwitches(rootNode, context);
        LabelSwitches(rootNode, context);
        SwitchTemporaries(rootNode, context);
        StringSwitches(rootNode, context);
        IfChains(rootNode, context);
        GotoTails(rootNode, context);
        SectionTails(rootNode, context);
        LastSectionTails(rootNode);
        StatementsBehindSwitch(rootNode);
        FixedDeclarators(rootNode, context);
        StringSectionOrder(rootNode, context);
        NullStarts(rootNode, context);
        TypeSwitches(rootNode, context);
    }

    // 9. Switch on the type of a value.
    //        switch (o) { case A a: X; case B b: Y; default: Z; }
    //    IL: o; stloc t; ldloc t; isinst A; stloc a; ldloc a; brtrue LX; ldloc t; isinst B; stloc b; ldloc b; brtrue LY; br LZ
    //    is printed 'if (!(o is A a)) { if (!(o is B b)) { Z } Y } X'. Tests written one by one
    //    read o itself each time ('ldarg o; isinst'); the switch reads a copy of it.
    private static bool TypeTest(Expression condition, out string subject, out AstType type, out string name, out bool negated)
    {
        subject = null; type = null; name = null; negated = false;
        condition = Unwrap(condition);
        var not = condition as UnaryOperatorExpression;
        if (not != null && not.Operator == UnaryOperatorType.Not) { negated = true; condition = Unwrap(not.Expression); }
        var test = condition as BinaryOperatorExpression;
        if (test == null || test.Operator != BinaryOperatorType.IsPattern) return false;
        var value = Unwrap(test.Left) as IdentifierExpression;
        var declared = test.Right as DeclarationExpression;
        var designation = declared == null ? null : declared.Designation as SingleVariableDesignation;
        if (value == null || designation == null) return false;
        subject = value.Identifier; type = declared.Type; name = designation.Identifier;
        return true;
    }

    private static bool Leaves(Statement s)
    {
        return s is ReturnStatement || s is ThrowStatement || s is ContinueStatement || s is GotoStatement;
    }

    private static void TypeSwitches(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var head in rootNode.Descendants.OfType<IfElseStatement>().ToList())
        {
            var block = head.Parent as BlockStatement;
            string subject, name;
            AstType type;
            bool negated;
            if (block == null || !head.FalseStatement.IsNull || !TypeTest(head.Condition, out subject, out type, out name, out negated)) continue;
            // Not the inner test of a chain that starts higher.
            var outer = block.Parent as IfElseStatement;
            string s2, n2; AstType t2; bool g2;
            if (outer != null && outer.TrueStatement == block && block.Statements.First() == head && TypeTest(outer.Condition, out s2, out t2, out n2, out g2) && s2 == subject) continue;
            var cases = new List<KeyValuePair<Expression, List<Statement>>>();
            List<Statement> fallback = null;
            IfElseStatement current = head;
            bool shaped = true;
            while (shaped)
            {
                string cs, cn; AstType ct; bool cg;
                if (!current.FalseStatement.IsNull || !TypeTest(current.Condition, out cs, out ct, out cn, out cg) || cs != subject) { shaped = false; break; }
                var following = new List<Statement>();
                for (AstNode next = current.NextSibling; next != null; next = next.NextSibling) if (next is Statement) following.Add((Statement)next);
                var inner = current.TrueStatement as BlockStatement;
                if (inner == null) { shaped = false; break; }
                var pattern = ((BinaryOperatorExpression)(cg ? Unwrap(((UnaryOperatorExpression)Unwrap(current.Condition)).Expression) : Unwrap(current.Condition))).Right;
                if (!cg)
                {
                    // The last label: 'if (o is T v) { body }  rest', rest being 'default'.
                    var body = inner.Statements.ToList();
                    if (body.Count == 0 || !Leaves(body[body.Count - 1])) { shaped = false; break; }
                    cases.Add(new KeyValuePair<Expression, List<Statement>>(pattern, body));
                    fallback = following;
                    break;
                }
                if (following.Count == 0 || !Leaves(following[following.Count - 1])) { shaped = false; break; }
                cases.Add(new KeyValuePair<Expression, List<Statement>>(pattern, following));
                var first = inner.Statements.FirstOrDefault() as IfElseStatement;
                string fs, fn; AstType ft; bool fg;
                if (first != null && first.FalseStatement.IsNull && TypeTest(first.Condition, out fs, out ft, out fn, out fg) && fs == subject) { current = first; continue; }
                fallback = inner.Statements.ToList();
                break;
            }
            if (!shaped || cases.Count < 2 || fallback == null) continue;
            if (fallback.Count > 0 && !Leaves(fallback[fallback.Count - 1])) continue;
            // The IL reads a copy of the value: one store of t, and 'ldloc t; isinst' for every label.
            ILInstruction any = null;
            foreach (AstNode n in head.Condition.DescendantsAndSelf) { any = n.Annotations.OfType<ILInstruction>().FirstOrDefault(); if (any != null) break; }
            IMethod method = MethodOf(any);
            if (method == null) continue;
            RawIL raw;
            if (!bodies.TryGetValue(method, out raw)) { raw = RawIL.Read(context.TypeSystem, method); bodies[method] = raw; }
            if (raw == null) continue;
            // The tests of a switch follow each other and jump to their sections: 'x; isinst T; stloc v;
            // ldloc v; brtrue SECTION', once per label, then 'br' to 'default'. (Tests written one by
            // one fall into their bodies: 'brfalse'.)
            int chain = -1, copy = -1;
            for (int i = 1; i + 3 < raw.Ops.Count && chain < 0; i++)
            {
                int k = i, found = 0;
                while (k + 3 < raw.Ops.Count && raw.Ops[k].Code == ILOpCode.Isinst && (RawIL.IsLdLoc(raw.Ops[k - 1].Code) || RawIL.IsLdArg(raw.Ops[k - 1].Code))
                    && raw.Ops[k - 1].Code == raw.Ops[i - 1].Code && raw.Ops[k - 1].Operand == raw.Ops[i - 1].Operand
                    && RawIL.IsStLoc(raw.Ops[k + 1].Code) && RawIL.IsLdLoc(raw.Ops[k + 2].Code) && raw.Ops[k + 2].Operand == raw.Ops[k + 1].Operand
                    && (raw.Ops[k + 3].Code == ILOpCode.Brtrue || raw.Ops[k + 3].Code == ILOpCode.Brtrue_s)) { found++; k += 5; }
                if (found == cases.Count) chain = i;
            }
            if (chain < 0) continue;
            // A switch on a parameter reads it for every test; a copy in a slot of its own is a variable of the source.
            RawIL.Op read = raw.Ops[chain - 1];
            if (RawIL.IsLdLoc(read.Code))
            {
                ILVariable held = null;
                foreach (IdentifierExpression id in head.Condition.DescendantsAndSelf.OfType<IdentifierExpression>())
                {
                    var rr = id.GetResolveResult() as ILVariableResolveResult;
                    if (rr != null && id.Identifier == subject) held = rr.Variable;
                }
                if (held == null || held.Kind != VariableKind.Local || held.Index != read.Operand)
                {
                    var stores = Enumerable.Range(0, raw.Ops.Count).Where(i => RawIL.IsStLoc(raw.Ops[i].Code) && raw.Ops[i].Operand == read.Operand).ToList();
                    if (stores.Count != 1 || stores[0] != chain - 2 || stores[0] == 0 || !(RawIL.IsLdArg(raw.Ops[stores[0] - 1].Code) || RawIL.IsLdLoc(raw.Ops[stores[0] - 1].Code))) continue;
                    copy = read.Operand;
                }
            }
            Expression value = new IdentifierExpression(subject);
            if (copy >= 0)
            {
                IType copyType = raw.LocalType(copy);
                var entity = head.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
                if (copyType == null || copyType.Kind == TypeKind.Unknown || entity == null) continue;
                var names = new HashSet<string>(entity.Descendants.OfType<Identifier>().Select(n => n.Name));
                string copyName = "value";
                for (int n = 2; names.Contains(copyName); n++) copyName = "value" + n;
                block.Statements.InsertBefore(head, new VariableDeclarationStatement(context.TypeSystemAstBuilder.ConvertType(copyType), copyName, value));
                value = new IdentifierExpression(copyName);
            }

            var statement = new SwitchStatement { Expression = value };
            foreach (var entry in cases)
            {
                var section = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
                section.CaseLabels.Add(new CaseLabel(entry.Key.Detach()));
                foreach (Statement s in entry.Value) section.Statements.Add(s.Detach());
                statement.SwitchSections.Add(section);
            }
            if (fallback.Count > 0)
            {
                var section = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
                section.CaseLabels.Add(new CaseLabel());
                foreach (Statement s in fallback) section.Statements.Add(s.Detach());
                statement.SwitchSections.Add(section);
            }
            head.ReplaceWith(statement);
        }
    }

    // 7. Variable that starts as null and takes the first value that is not.
    //        T v = null;  v = v ?? a;  v = v ?? b;  return v;
    //    IL: ldnull; dup; brtrue L1; pop; a; L1: dup; brtrue L2; pop; b; L2: ret  (v never
    //    leaves the stack). Is printed 'object v = null ?? a; if (v == null) { v = b; }
    //    return (T)v;', which stores v and folds the constant away.
    private static void NullStarts(AstNode rootNode, TransformContext context)
    {
        foreach (var declaration in rootNode.Descendants.OfType<VariableDeclarationStatement>().ToList())
        {
            if (declaration.Variables.Count != 1 || !(declaration.Parent is BlockStatement)) continue;
            VariableInitializer variable = declaration.Variables.Single();
            var first = Unwrap(variable.Initializer) as BinaryOperatorExpression;
            if (first == null || first.Operator != BinaryOperatorType.NullCoalescing || !(Unwrap(first.Left) is NullReferenceExpression)) continue;
            var rr = first.Right.GetResolveResult();
            if (rr == null || rr.Type.IsReferenceType != true || rr.Type.Kind == TypeKind.Null) continue;
            IType type = rr.Type;
            string name = variable.Name;
            // The statements that follow: 'if (v == null) { v = e; }', e of the same type.
            var steps = new List<IfElseStatement>();
            bool same = true;
            for (AstNode next = declaration.NextSibling; next != null; next = next.NextSibling)
            {
                var test = next as IfElseStatement;
                if (test == null || !test.FalseStatement.IsNull) break;
                var isNull = Unwrap(test.Condition) as BinaryOperatorExpression;
                if (isNull == null || isNull.Operator != BinaryOperatorType.Equality || !(Unwrap(isNull.Right) is NullReferenceExpression)) break;
                var tested = Unwrap(isNull.Left) as IdentifierExpression;
                if (tested == null || tested.Identifier != name) break;
                var assign = Single(test.TrueStatement) as ExpressionStatement;
                var store = assign == null ? null : assign.Expression as AssignmentExpression;
                var target = store == null ? null : store.Left as IdentifierExpression;
                if (target == null || target.Identifier != name || store.Operator != AssignmentOperatorType.Assign) break;
                var valueType = store.Right.GetResolveResult();
                if (valueType == null || !valueType.Type.Equals(type)) { same = false; break; }
                steps.Add(test);
            }
            if (!same) continue;
            var block = (BlockStatement)declaration.Parent;
            // Every other use is a read through a cast to that type.
            bool reads = true;
            var casts = new List<CastExpression>();
            foreach (var use in block.Descendants.OfType<IdentifierExpression>().Where(i => i.Identifier == name).ToList())
            {
                if (steps.Any(s => use.Ancestors.Contains(s))) continue;
                var cast = use.Parent as CastExpression;
                if (cast == null) { reads = false; break; }
                var to = cast.GetResolveResult();
                if (to == null || !to.Type.Equals(type)) { reads = false; break; }
                casts.Add(cast);
            }
            if (!reads) continue;
            Expression value = first.Right.Detach();
            declaration.Type = context.TypeSystemAstBuilder.ConvertType(type);
            variable.Initializer = new NullReferenceExpression();
            Statement at = declaration;
            var all = new List<Expression> { value };
            foreach (IfElseStatement step in steps)
            {
                all.Add(((AssignmentExpression)((ExpressionStatement)Single(step.TrueStatement)).Expression).Right.Detach());
                step.Remove();
            }
            foreach (Expression e in all)
            {
                var statement = new ExpressionStatement(new AssignmentExpression(new IdentifierExpression(name), new BinaryOperatorExpression(new IdentifierExpression(name), BinaryOperatorType.NullCoalescing, e)));
                block.Statements.InsertAfter(at, statement);
                at = statement;
            }
            foreach (CastExpression cast in casts) cast.ReplaceWith(new IdentifierExpression(name));
        }
    }

    private static Expression Unwrap(Expression e)
    {
        while (e is ParenthesizedExpression) e = ((ParenthesizedExpression)e).Expression;
        return e;
    }

    private static void Flatten(Expression e, List<Expression> operands)
    {
        e = Unwrap(e);
        var both = e as BinaryOperatorExpression;
        if (both != null && both.Operator == BinaryOperatorType.ConditionalAnd) { Flatten(both.Left, operands); Flatten(both.Right, operands); }
        else operands.Add(e);
    }

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

    private static bool EndsWithJump(Statement s)
    {
        var block = s as BlockStatement;
        if (block != null) s = block.Statements.LastOrDefault();
        return s is ReturnStatement || s is BreakStatement || s is ContinueStatement || s is ThrowStatement || s is GotoStatement;
    }

    private static void SectionSwitches(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var test in rootNode.Descendants.OfType<IfElseStatement>().ToList())
        {
            if (test.Parent == null || !test.FalseStatement.IsNull) continue;
            var declaration = test.PrevSibling as VariableDeclarationStatement;
            if (declaration == null || declaration.Variables.Count != 1) continue;
            VariableInitializer variable = declaration.Variables.First();
            if (variable.Initializer.IsNull) continue;
            var operands = new List<Expression>();
            Flatten(test.Condition, operands);
            if (operands.Count < 2) continue;
            var labels = new List<Expression>();
            foreach (Expression operand in operands)
            {
                var differs = operand as BinaryOperatorExpression;
                if (differs == null || differs.Operator != BinaryOperatorType.InEquality) break;
                var subject = Unwrap(differs.Left) as IdentifierExpression;
                var rr = differs.Right.GetResolveResult();
                if (subject == null || subject.Identifier != variable.Name || rr == null || !rr.IsCompileTimeConstant) break;
                labels.Add(differs.Right);
            }
            if (labels.Count != operands.Count) continue;
            var entity = declaration.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (entity == null) continue;
            if (entity.Descendants.OfType<IdentifierExpression>().Count(i => i.Identifier == variable.Name) != labels.Count) continue;
            if (HasOuterBreak(test.TrueStatement)) continue;
            ILVariable v = null;
            foreach (var rr in variable.Annotations.OfType<ILVariableResolveResult>()) v = rr.Variable;
            if (v == null || !v.Index.HasValue || v.Kind != VariableKind.Local || v.Function == null || v.Function.Method == null) continue;
            IType type = v.Type;
            IType underlying = type.Kind == TypeKind.Enum ? type.GetEnumUnderlyingType() : type;
            System.TypeCode code = ReflectionHelper.GetTypeCode(underlying);
            if (code < System.TypeCode.Char || code > System.TypeCode.UInt64) continue;
            RawIL raw;
            if (!bodies.TryGetValue(v.Function.Method, out raw)) { raw = RawIL.Read(context.TypeSystem, v.Function.Method); bodies[v.Function.Method] = raw; }
            if (raw == null) continue;
            // The slot is stored more than once, and here it is read once per label, each
            // time for a 'beq'.
            int stores = 0;
            foreach (RawIL.Op op in raw.Ops) if (RawIL.IsStLoc(op.Code) && op.Operand == v.Index.Value) stores++;
            if (stores < 2) continue;
            bool shaped = false;
            for (int i = 0; i + 3 * labels.Count < raw.Ops.Count && !shaped; i++)
            {
                if (!RawIL.IsStLoc(raw.Ops[i].Code) || raw.Ops[i].Operand != v.Index.Value) continue;
                bool all = true;
                for (int k = 0; k < labels.Count && all; k++)
                {
                    RawIL.Op load = raw.Ops[i + 1 + 3 * k], constant = raw.Ops[i + 2 + 3 * k], branch = raw.Ops[i + 3 + 3 * k];
                    all = RawIL.IsLdLoc(load.Code) && load.Operand == v.Index.Value && RawIL.IsLdcI4(constant.Code) && (branch.Code == ILOpCode.Beq || branch.Code == ILOpCode.Beq_s);
                }
                shaped = all;
            }
            if (!shaped) continue;

            var statement = new SwitchStatement();
            statement.Expression = variable.Initializer.Detach();
            foreach (Expression label in labels)
            {
                var section = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
                section.CaseLabels.Add(new CaseLabel(label.Detach()));
                section.Statements.Add(new BreakStatement());
                statement.SwitchSections.Add(section);
            }
            var rest = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
            rest.CaseLabels.Add(new CaseLabel());
            bool jumps = EndsWithJump(test.TrueStatement);
            rest.Statements.Add(test.TrueStatement.Detach());
            if (!jumps) rest.Statements.Add(new BreakStatement());
            statement.SwitchSections.Add(rest);
            test.ReplaceWith(statement);
            declaration.Remove();
        }
    }

    // The same with the labels on one section:
    //        switch (e) { case A: case B: body; break; }
    // IL: e; stloc t; ldloc t; A; beq BODY; ldloc t; B; bne.un END; BODY: ...
    // is printed 'T x = e; if (x == A || x == B) { body }'.
    private static void FlattenOr(Expression e, List<Expression> operands)
    {
        e = Unwrap(e);
        var either = e as BinaryOperatorExpression;
        if (either != null && either.Operator == BinaryOperatorType.ConditionalOr) { FlattenOr(either.Left, operands); FlattenOr(either.Right, operands); }
        else operands.Add(e);
    }

    private static void LabelSwitches(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var test in rootNode.Descendants.OfType<IfElseStatement>().ToList())
        {
            if (test.Parent == null || !test.FalseStatement.IsNull) continue;
            var declaration = test.PrevSibling as VariableDeclarationStatement;
            if (declaration == null || declaration.Variables.Count != 1) continue;
            VariableInitializer variable = declaration.Variables.First();
            if (variable.Initializer.IsNull) continue;
            var operands = new List<Expression>();
            FlattenOr(test.Condition, operands);
            if (operands.Count < 2) continue;
            var labels = new List<Expression>();
            foreach (Expression operand in operands)
            {
                var equals = operand as BinaryOperatorExpression;
                if (equals == null || equals.Operator != BinaryOperatorType.Equality) break;
                var subject = Unwrap(equals.Left) as IdentifierExpression;
                var rr = equals.Right.GetResolveResult();
                if (subject == null || subject.Identifier != variable.Name || rr == null || !rr.IsCompileTimeConstant) break;
                labels.Add(equals.Right);
            }
            if (labels.Count != operands.Count) continue;
            var entity = declaration.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (entity == null) continue;
            if (entity.Descendants.OfType<IdentifierExpression>().Count(i => i.Identifier == variable.Name) != labels.Count) continue;
            if (HasOuterBreak(test.TrueStatement)) continue;
            ILVariable v = null;
            foreach (var rr in variable.Annotations.OfType<ILVariableResolveResult>()) v = rr.Variable;
            if (v == null || !v.Index.HasValue || v.Kind != VariableKind.Local || v.Function == null || v.Function.Method == null) continue;
            IType type = v.Type;
            IType underlying = type.Kind == TypeKind.Enum ? type.GetEnumUnderlyingType() : type;
            System.TypeCode code = ReflectionHelper.GetTypeCode(underlying);
            if (code < System.TypeCode.Char || code > System.TypeCode.UInt64) continue;
            RawIL raw;
            if (!bodies.TryGetValue(v.Function.Method, out raw)) { raw = RawIL.Read(context.TypeSystem, v.Function.Method); bodies[v.Function.Method] = raw; }
            if (raw == null) continue;
            // A slot stored more than once (a temporary), read once per label: 'beq' to the
            // body for all but the last, 'bne.un' over it for the last.
            int stores = 0;
            foreach (RawIL.Op op in raw.Ops) if (RawIL.IsStLoc(op.Code) && op.Operand == v.Index.Value) stores++;
            if (stores < 2) continue;
            bool shaped = false;
            for (int i = 0; i + 3 * labels.Count < raw.Ops.Count && !shaped; i++)
            {
                if (!RawIL.IsStLoc(raw.Ops[i].Code) || raw.Ops[i].Operand != v.Index.Value) continue;
                bool all = true;
                for (int k = 0; k < labels.Count && all; k++)
                {
                    RawIL.Op load = raw.Ops[i + 1 + 3 * k], constant = raw.Ops[i + 2 + 3 * k], branch = raw.Ops[i + 3 + 3 * k];
                    bool lastOne = k == labels.Count - 1;
                    bool jump = lastOne ? (branch.Code == ILOpCode.Bne_un || branch.Code == ILOpCode.Bne_un_s) : (branch.Code == ILOpCode.Beq || branch.Code == ILOpCode.Beq_s);
                    all = RawIL.IsLdLoc(load.Code) && load.Operand == v.Index.Value && RawIL.IsLdcI4(constant.Code) && jump;
                }
                shaped = all;
            }
            if (!shaped) continue;

            var statement = new SwitchStatement();
            statement.Expression = variable.Initializer.Detach();
            var section = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
            foreach (Expression label in labels) section.CaseLabels.Add(new CaseLabel(label.Detach()));
            bool jumps = EndsWithJump(test.TrueStatement);
            section.Statements.Add(test.TrueStatement.Detach());
            if (!jumps) section.Statements.Add(new BreakStatement());
            statement.SwitchSections.Add(section);
            test.ReplaceWith(statement);
            declaration.Remove();
        }
    }
    // 'T x = e; switch (x) { }' with x read nowhere else and its slot stored elsewhere too:
    // the temporary of 'switch (e)'. A variable of the source keeps its slot for itself.
    private static void SwitchTemporaries(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var statement in rootNode.Descendants.OfType<SwitchStatement>().ToList())
        {
            var subject = Unwrap(statement.Expression) as IdentifierExpression;
            var declaration = statement.PrevSibling as VariableDeclarationStatement;
            if (subject == null || declaration == null || declaration.Variables.Count != 1) continue;
            VariableInitializer variable = declaration.Variables.First();
            if (variable.Initializer.IsNull || variable.Name != subject.Identifier) continue;
            var entity = declaration.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (entity == null || entity.Descendants.OfType<IdentifierExpression>().Count(i => i.Identifier == variable.Name) != 1) continue;
            ILVariable v = null;
            foreach (var rr in variable.Annotations.OfType<ILVariableResolveResult>()) v = rr.Variable;
            if (v == null || !v.Index.HasValue || v.Kind != VariableKind.Local || v.Function == null || v.Function.Method == null) continue;
            RawIL raw;
            if (!bodies.TryGetValue(v.Function.Method, out raw)) { raw = RawIL.Read(context.TypeSystem, v.Function.Method); bodies[v.Function.Method] = raw; }
            if (raw == null) continue;
            int stores = 0, addresses = 0;
            foreach (RawIL.Op op in raw.Ops)
            {
                if (RawIL.IsStLoc(op.Code) && op.Operand == v.Index.Value) stores++;
                if ((op.Code == ILOpCode.Ldloca || op.Code == ILOpCode.Ldloca_s) && op.Operand == v.Index.Value) addresses++;
            }
            if (stores < 2 || addresses != 0) continue;
            statement.Expression = variable.Initializer.Detach();
            declaration.Remove();
        }
    }
    private static IMethod MethodOf(ILInstruction inst)
    {
        while (inst != null && !(inst is ILFunction)) inst = inst.Parent;
        return inst == null ? null : ((ILFunction)inst).Method;
    }

    // 'x == "lit"' or '!(x == "lit")': the name of x, the literal, and whether it is negated.
    private static bool StringTest(Expression condition, out string name, out Expression literal, out bool negated, out ILInstruction call)
    {
        name = null; literal = null; negated = false; call = null;
        condition = Unwrap(condition);
        var not = condition as UnaryOperatorExpression;
        if (not != null && not.Operator == UnaryOperatorType.Not) { negated = true; condition = Unwrap(not.Expression); }
        var equal = condition as BinaryOperatorExpression;
        if (equal == null || equal.Operator != BinaryOperatorType.Equality) return false;
        var subject = Unwrap(equal.Left) as IdentifierExpression;
        var value = Unwrap(equal.Right) as PrimitiveExpression;
        if (subject == null || value == null || !(value.Value is string)) return false;
        call = equal.Annotations.OfType<CallInstruction>().FirstOrDefault(c => c.Method.Name == "op_Equality");
        if (call == null) return false;
        name = subject.Identifier;
        literal = equal.Right;
        return true;
    }

    private static Statement Single(Statement s)
    {
        var block = s as BlockStatement;
        if (block != null && block.Statements.Count == 1) return block.Statements.First();
        return s;
    }

    private static void StringSwitches(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var top in rootNode.Descendants.OfType<IfElseStatement>().ToList())
        {
            if (top.Parent == null) continue;
            var labels = new List<Expression>();
            var sections = new List<Statement>();
            var targets = new List<int>();
            string subject = null;
            Statement rest = top;
            RawIL raw = null;
            int last = -1;
            bool shaped = true;
            while (shaped)
            {
                var test = Single(rest) as IfElseStatement;
                string name;
                Expression literal;
                bool negated;
                ILInstruction call;
                if (test == null || !StringTest(test.Condition, out name, out literal, out negated, out call)) break;
                if (subject != null && name != subject) break;
                Statement match = negated ? test.FalseStatement : test.TrueStatement;
                Statement other = negated ? test.TrueStatement : test.FalseStatement;
                if (match.IsNull) break;
                IMethod method = MethodOf(call);
                if (method == null) break;
                if (!bodies.TryGetValue(method, out raw)) { raw = RawIL.Read(context.TypeSystem, method); bodies[method] = raw; }
                // 'call op_Equality; brtrue SECTION', the sections in the order of their labels.
                RawIL.Op branch;
                int end = RawIL.EndOf(call);
                if (raw == null || end < 0 || !raw.IsAt(end, out branch) || (branch.Code != ILOpCode.Brtrue && branch.Code != ILOpCode.Brtrue_s)) { shaped = labels.Count > 0; if (!shaped) break; else { break; } }
                if (targets.Count > 0 && branch.Operand <= targets[targets.Count - 1]) { shaped = false; break; }
                subject = name;
                labels.Add(literal);
                sections.Add(match);
                targets.Add(branch.Operand);
                last = branch.End;
                rest = other;
            }
            if (!shaped || labels.Count < 2 || raw == null) continue;
            // After the last test: a jump to what is left (the 'default' section, or the end).
            RawIL.Op jump;
            if (!raw.IsAt(last, out jump) || (jump.Code != ILOpCode.Br && jump.Code != ILOpCode.Br_s) || jump.Operand <= targets[targets.Count - 1]) continue;
            if (targets[0] != jump.End) continue;
            bool breaks = sections.Any(s => HasOuterBreak(s)) || (!rest.IsNull && HasOuterBreak(rest));
            if (breaks) continue;

            var statement = new SwitchStatement();
            statement.Expression = new IdentifierExpression(subject);
            for (int i = 0; i < labels.Count; i++)
            {
                var section = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
                section.CaseLabels.Add(new CaseLabel(labels[i].Detach()));
                bool jumps = EndsWithJump(sections[i]);
                section.Statements.Add(sections[i].Detach());
                if (!jumps) section.Statements.Add(new BreakStatement());
                statement.SwitchSections.Add(section);
            }
            if (!rest.IsNull)
            {
                var otherwise = new ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection();
                otherwise.CaseLabels.Add(new CaseLabel());
                bool jumps = EndsWithJump(rest);
                otherwise.Statements.Add(rest.Detach());
                if (!jumps) otherwise.Statements.Add(new BreakStatement());
                statement.SwitchSections.Add(otherwise);
            }
            top.ReplaceWith(statement);
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

    private static void IfChains(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var statement in rootNode.Descendants.OfType<SwitchStatement>().ToList())
        {
            if (statement.Parent == null) continue;
            var subject = Unwrap(statement.Expression) as IdentifierExpression;
            IMethod method;
            RawIL raw;
            int valueSlot = -1;
            if (subject == null)
            {
                // The value is computed: 'T v = e; if (v == a) A; else if (v == b) B;' stores it
                // in a slot of its own and reads it for each test ('e; stloc v; ldloc v; a; bne.un').
                if (!(statement.Parent is BlockStatement)) continue;
                ILInstruction computed = statement.Expression.Annotations.OfType<ILInstruction>().FirstOrDefault();
                method = MethodOf(computed);
                if (method == null) continue;
                if (!bodies.TryGetValue(method, out raw)) { raw = RawIL.Read(context.TypeSystem, method); bodies[method] = raw; }
                int valueEnd = -1;
                foreach (ILInstruction part in statement.Expression.Annotations.OfType<ILInstruction>()) valueEnd = System.Math.Max(valueEnd, RawIL.EndOf(part));
                RawIL.Op stored, loaded;
                if (raw == null || valueEnd < 0) continue;
                // (The range of the value may go over the 'stloc'.)
                if (!(raw.IsAt(valueEnd, out stored) && RawIL.IsStLoc(stored.Code)) && !(raw.EndsAt(valueEnd, out stored) && RawIL.IsStLoc(stored.Code))) continue;
                if (!raw.Near(stored.Offset, 1, out loaded) || !RawIL.IsLdLoc(loaded.Code) || loaded.Operand != stored.Operand) continue;
                if (raw.Ops.Count(o => RawIL.IsStLoc(o.Code) && o.Operand == stored.Operand) != 1 || raw.Ops.Count(o => RawIL.IsLdLoc(o.Code) && o.Operand == stored.Operand) < 2) continue;
                valueSlot = stored.Operand;
            }
            else
            {
                var rr = subject.GetResolveResult() as ILVariableResolveResult;
                if (rr == null || rr.Variable.Function == null || rr.Variable.Function.Method == null) continue;
                if (rr.Variable.Kind != VariableKind.Local && rr.Variable.Kind != VariableKind.Parameter) continue;
                method = rr.Variable.Function.Method;
                if (!bodies.TryGetValue(method, out raw)) { raw = RawIL.Read(context.TypeSystem, method); bodies[method] = raw; }
            }
            if (raw == null) continue;
            var cases = new List<ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection>();
            ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection fallback = null;
            bool simple = true;
            foreach (var section in statement.SwitchSections)
            {
                if (section.CaseLabels.Count != 1 || section.Statements.Count == 0) { simple = false; break; }
                if (section.CaseLabels.First().Expression.IsNull) fallback = section; else cases.Add(section);
                // The section ends with 'break;' or leaves by itself; no other 'break' or 'goto case' in it.
                Statement last = section.Statements.Last();
                var lastBlock = last as BlockStatement;
                if (lastBlock != null && section.Statements.Count == 1) last = lastBlock.Statements.LastOrDefault();
                if (!(last is BreakStatement || last is ReturnStatement || last is ContinueStatement || last is ThrowStatement)) { simple = false; break; }
                if (section.Descendants.OfType<GotoCaseStatement>().Any() || section.Descendants.OfType<GotoDefaultStatement>().Any()) { simple = false; break; }
                foreach (var jump in section.Descendants.OfType<BreakStatement>())
                {
                    if (jump == last) continue;
                    bool inside = false;
                    for (AstNode p = jump.Parent; p != null && p != section; p = p.Parent)
                        if (p is ForStatement || p is ForeachStatement || p is WhileStatement || p is DoWhileStatement || p is SwitchStatement) inside = true;
                    if (!inside) simple = false;
                }
            }
            if (!simple || cases.Count < 2) continue;
            // Every labelled section follows its own test: a conditional branch that jumps over it.
            var starts = new List<int>();
            // 'if (x == 0) A; else if (x != 1) throw E; else B;' has the fallback before the last
            // body: 'x; 1; beq B; E; throw; B:'. The last test jumps to its body over a fallback
            // that leaves.
            bool inverted = false;
            if (fallback != null)
            {
                Statement leaves = fallback.Statements.Last();
                if (leaves is BlockStatement && fallback.Statements.Count == 1) leaves = ((BlockStatement)leaves).Statements.LastOrDefault();
                int lastStart = FirstOffset(cases[cases.Count - 1].Statements.First()), restStart = FirstOffset(fallback.Statements.First());
                RawIL.Op jump;
                if ((leaves is ThrowStatement || leaves is ReturnStatement) && lastStart > 0 && restStart > 0 && restStart < lastStart && raw.Near(restStart, -1, out jump)
                    && (jump.Code == ILOpCode.Beq || jump.Code == ILOpCode.Beq_s || jump.Code == ILOpCode.Brfalse || jump.Code == ILOpCode.Brfalse_s) && jump.Operand == lastStart
                    && FirstOffset(cases[cases.Count - 2].Statements.First()) < restStart) inverted = true;
            }
            foreach (var section in cases)
            {
                int start = FirstOffset(section.Statements.First());
                RawIL.Op branch;
                if (inverted && section == cases[cases.Count - 1]) { starts.Add(start); continue; }
                if (start <= 0 || !raw.Near(start, -1, out branch) || !RawIL.IsCondBranch(branch.Code) || branch.Operand <= start) { simple = false; break; }
                if (branch.Code != ILOpCode.Bne_un && branch.Code != ILOpCode.Bne_un_s && branch.Code != ILOpCode.Brtrue && branch.Code != ILOpCode.Brtrue_s) { simple = false; break; }
                if (starts.Count > 0 && start <= starts[starts.Count - 1]) { simple = false; break; }
                starts.Add(start);
            }
            if (!simple) continue;
            if (!inverted && fallback != null && FirstOffset(fallback.Statements.First()) >= 0 && FirstOffset(fallback.Statements.First()) < starts[starts.Count - 1]) continue;

            if (subject == null)
            {
                IType valueType = raw.LocalType(valueSlot);
                var entity = statement.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
                if (valueType == null || valueType.Kind == TypeKind.Unknown || entity == null) continue;
                var names = new HashSet<string>(entity.Descendants.OfType<Identifier>().Select(n => n.Name));
                string name = "value";
                for (int n = 2; names.Contains(name); n++) name = "value" + n;
                var declaration = new VariableDeclarationStatement(context.TypeSystemAstBuilder.ConvertType(valueType), name, statement.Expression.Detach());
                ((BlockStatement)statement.Parent).Statements.InsertBefore(statement, declaration);
                subject = new IdentifierExpression(name);
            }
            IfElseStatement head = null, tail = null;
            foreach (var section in cases)
            {
                var test = new IfElseStatement();
                bool swapped = inverted && section == cases[cases.Count - 1];
                test.Condition = new BinaryOperatorExpression(subject.Clone(), swapped ? BinaryOperatorType.InEquality : BinaryOperatorType.Equality, section.CaseLabels.First().Expression.Detach());
                test.TrueStatement = swapped ? BodyOf(fallback) : BodyOf(section);
                if (swapped) test.FalseStatement = BodyOf(section);
                if (head == null) head = test; else tail.FalseStatement = test;
                tail = test;
            }
            if (fallback != null && !inverted)
            {
                BlockStatement rest = BodyOf(fallback);
                if (rest.Statements.Count > 0) tail.FalseStatement = rest;
            }
            statement.ReplaceWith(head);
        }
    }

    // The statements of a section as a block, without the 'break;' that ends it.
    private static BlockStatement BodyOf(ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection section)
    {
        var block = new BlockStatement();
        var statements = section.Statements.ToList();
        if (statements.Count == 1 && statements[0] is BlockStatement) statements = ((BlockStatement)statements[0]).Statements.ToList();
        foreach (Statement s in statements)
        {
            s.Remove();
            if (s is BreakStatement) continue;
            block.Statements.Add(s);
        }
        return block;
    }

    private static void StringSectionOrder(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var statement in rootNode.Descendants.OfType<SwitchStatement>().ToList())
        {
            var sections = statement.SwitchSections.ToList();
            if (sections.Count < 3) continue;
            ILInstruction subject = statement.Expression.Annotations.OfType<ILInstruction>().FirstOrDefault();
            IMethod method = MethodOf(subject);
            if (method == null) continue;
            RawIL raw;
            if (!bodies.TryGetValue(method, out raw)) { raw = RawIL.Read(context.TypeSystem, method); bodies[method] = raw; }
            if (raw == null) continue;
            // Literal -> where its test jumps.
            var targets = new Dictionary<string, int>();
            var twice = new HashSet<string>();
            for (int i = 0; i + 2 < raw.Ops.Count; i++)
            {
                string literal = raw.String(raw.Ops[i]);
                if (literal == null || raw.Ops[i + 1].Code != ILOpCode.Call) continue;
                ILOpCode jump = raw.Ops[i + 2].Code;
                if (jump != ILOpCode.Brtrue && jump != ILOpCode.Brtrue_s) continue;
                IMethod equal = raw.Method(raw.Ops[i + 1]);
                if (equal == null || equal.Name != "op_Equality") continue;
                if (targets.ContainsKey(literal)) twice.Add(literal);
                targets[literal] = raw.Ops[i + 2].Operand;
            }
            var starts = new List<int>();
            int fallback = -1;
            bool known = true;
            for (int s = 0; s < sections.Count && known; s++)
            {
                int start = -1;
                foreach (CaseLabel label in sections[s].CaseLabels)
                {
                    if (label.Expression.IsNull) { fallback = s; continue; }
                    var literal = Unwrap(label.Expression) as PrimitiveExpression;
                    int target;
                    if (literal == null || !(literal.Value is string) || twice.Contains((string)literal.Value) || !targets.TryGetValue((string)literal.Value, out target)) { known = false; break; }
                    if (start >= 0 && start != target) { known = false; break; }
                    start = target;
                }
                if (start < 0 && fallback != s) known = false;
                starts.Add(start);
            }
            if (!known) continue;
            // 'default' keeps its place when it is first or last; elsewhere its place is not known.
            if (fallback > 0 && fallback < sections.Count - 1) continue;
            if (fallback >= 0 && sections[fallback].CaseLabels.Count != 1) continue;
            var labelled = Enumerable.Range(0, sections.Count).Where(i => i != fallback).ToList();
            var order = labelled.OrderBy(i => starts[i]).ToList();
            if (order.SequenceEqual(labelled)) continue;
            if (order.Select(i => starts[i]).Distinct().Count() != order.Count) continue;
            if (fallback == 0) order.Insert(0, fallback);
            else if (fallback > 0) order.Add(fallback);
            foreach (var section in sections) section.Remove();
            foreach (int i in order) statement.SwitchSections.Add(sections[i]);
        }
    }

    private static Dictionary<string, int> StringTargets(RawIL raw, HashSet<string> twice)
    {
        var targets = new Dictionary<string, int>();
        for (int i = 0; i + 2 < raw.Ops.Count; i++)
        {
            string literal = raw.String(raw.Ops[i]);
            if (literal == null || raw.Ops[i + 1].Code != ILOpCode.Call) continue;
            ILOpCode jump = raw.Ops[i + 2].Code;
            if (jump != ILOpCode.Brtrue && jump != ILOpCode.Brtrue_s) continue;
            IMethod equal = raw.Method(raw.Ops[i + 1]);
            if (equal == null || equal.Name != "op_Equality") continue;
            if (targets.ContainsKey(literal)) twice.Add(literal);
            targets[literal] = raw.Ops[i + 2].Operand;
        }
        return targets;
    }

    // 5c. The same for any switch: one section ends with 'break;', the others leave by
    //     themselves, and what follows the switch stands in the IL before the start of a
    //     section printed after that one (a 'default' that throws, most often).
    private static void LastSectionTails(AstNode rootNode)
    {
        foreach (var statement in rootNode.Descendants.OfType<SwitchStatement>().ToList())
        {
            var sections = statement.SwitchSections.ToList();
            if (sections.Count < 2 || !(statement.Parent is BlockStatement)) continue;
            ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection open = null;
            bool known = true;
            int others = -1;
            foreach (var section in sections)
            {
                var content = section.Statements.ToList();
                if (content.Count == 1 && content[0] is BlockStatement) content = ((BlockStatement)content[0]).Statements.ToList();
                Statement last = content.LastOrDefault();
                if (last is BreakStatement)
                {
                    if (open != null || content.Count < 2 || content.Take(content.Count - 1).Any(s => HasOuterBreak(s))) { known = false; break; }
                    open = section;
                    continue;
                }
                if (!(last is ReturnStatement || last is ThrowStatement || last is ContinueStatement || last is GotoCaseStatement || last is GotoDefaultStatement) || HasOuterBreak(section)) { known = false; break; }
                if (open != null)
                {
                    int start = FirstOffset(content[0]);
                    if (start > others) others = start;
                }
            }
            if (!known || open == null || others < 0) continue;
            var body = open.Statements.ToList();
            if (body.Count == 1 && body[0] is BlockStatement) body = ((BlockStatement)body[0]).Statements.ToList();
            int previous = -1;
            foreach (Statement s in body) { int o = FirstOffset(s); if (o > previous) previous = o; }
            if (previous < 0 || previous >= others) continue;
            var moved = new List<Statement>();
            for (AstNode next = statement.NextSibling; next != null; next = next.NextSibling)
            {
                var following = next as Statement;
                if (following == null) continue;
                int start = FirstOffset(following);
                if (start <= previous || start >= others) break;
                moved.Add(following);
                previous = start;
            }
            if (moved.Count == 0 || moved.Any(m => HasOuterBreak(m) || m is VariableDeclarationStatement || m is LabelStatement || m.Descendants.OfType<GotoStatement>().Any())) continue;
            Statement end = body[body.Count - 1];
            foreach (Statement m in moved)
            {
                m.Remove();
                var holder = end.Parent as BlockStatement;
                if (holder != null) holder.Statements.InsertBefore(end, m); else open.Statements.InsertBefore(end, m);
            }
        }
    }

    // 5d. The reverse: the end of a section stands in the IL after every section, a later
    //     one included (a 'default' that throws).
    //        switch (x) { case a: A; return; case b: B; break; default: throw; }  C;
    //     is printed with C at the end of 'case b' and 'break;' in 'case a', at the end of a
    //     void method.
    private static void StatementsBehindSwitch(AstNode rootNode)
    {
        foreach (var statement in rootNode.Descendants.OfType<SwitchStatement>().ToList())
        {
            var sections = statement.SwitchSections.ToList();
            var parent = statement.Parent as BlockStatement;
            var method = parent == null ? null : parent.Parent as MethodDeclaration;
            if (sections.Count < 3 || method == null || statement.NextSibling is Statement || method.ReturnType.ToString() != "void") continue;
            var contents = new List<List<Statement>>();
            var starts = new List<int>();
            bool known = true;
            foreach (var section in sections)
            {
                var content = section.Statements.ToList();
                if (content.Count == 1 && content[0] is BlockStatement) content = ((BlockStatement)content[0]).Statements.ToList();
                if (content.Count == 0) { known = false; break; }
                int start = FirstOffset(content[0]);
                if (start < 0) { known = false; break; }
                contents.Add(content);
                starts.Add(start);
            }
            if (!known) continue;
            int last = starts.Max(), holder = -1, from = -1;
            for (int s = 0; s < sections.Count && known; s++)
            {
                List<Statement> content = contents[s];
                if (starts[s] == last || !(content[content.Count - 1] is BreakStatement)) continue;
                for (int j = 1; j < content.Count - 1; j++)
                {
                    int o = FirstOffset(content[j]);
                    if (o < 0 || o <= last) continue;
                    if (holder >= 0) known = false;
                    holder = s; from = j;
                    break;
                }
            }
            if (!known || holder < 0) continue;
            // Everything from there on is behind the last section; the other sections that reach the end return.
            List<Statement> body = contents[holder];
            bool behind = true;
            for (int j = from; j < body.Count - 1; j++)
            {
                int o = FirstOffset(body[j]);
                if ((o >= 0 && o <= last) || body[j] is VariableDeclarationStatement || body[j] is LabelStatement || HasOuterBreak(body[j])) behind = false;
            }
            for (int j = 0; j < from; j++) if (HasOuterBreak(body[j])) behind = false;
            if (!behind) continue;
            var breaks = new List<BreakStatement>();
            for (int s = 0; s < sections.Count && behind; s++)
            {
                if (s == holder) continue;
                List<Statement> content = contents[s];
                for (int j = 0; j < content.Count - 1; j++) if (HasOuterBreak(content[j])) behind = false;
                var end = content[content.Count - 1] as BreakStatement;
                if (end != null) breaks.Add(end);
                else if (!(content[content.Count - 1] is ReturnStatement || content[content.Count - 1] is ThrowStatement)) behind = false;
            }
            if (!behind) continue;
            foreach (BreakStatement b in breaks) b.ReplaceWith(new ReturnStatement());
            for (int j = from; j < body.Count - 1; j++)
            {
                body[j].Remove();
                parent.Statements.Add(body[j]);
            }
        }
    }

    // 5b. The same when the other sections reach the end as well:
    //        switch (s) { case "a": break; case "b": B; goto L; default: goto L; }  A; goto L;  L:
    //     is 'switch (s) { case "a": A; break; case "b": B; break; }', the sections in the order
    //     of the IL (the tests of "a" jump to A).
    private static void GotoTails(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var statement in rootNode.Descendants.OfType<SwitchStatement>().ToList())
        {
            var sections = statement.SwitchSections.ToList();
            if (sections.Count < 2 || !(statement.Parent is BlockStatement)) continue;
            ILInstruction subject = statement.Expression.Annotations.OfType<ILInstruction>().FirstOrDefault();
            IMethod method = MethodOf(subject);
            if (method == null) continue;
            RawIL raw;
            if (!bodies.TryGetValue(method, out raw)) { raw = RawIL.Read(context.TypeSystem, method); bodies[method] = raw; }
            if (raw == null) continue;
            var twice = new HashSet<string>();
            Dictionary<string, int> targets = StringTargets(raw, twice);
            // What follows: statements, 'goto L;', 'L:'.
            var moved = new List<Statement>();
            GotoStatement exit = null;
            LabelStatement label = null;
            for (AstNode next = statement.NextSibling; next != null; next = next.NextSibling)
            {
                if (!(next is Statement)) continue;
                if (exit != null) { label = next as LabelStatement; break; }
                exit = next as GotoStatement;
                if (exit == null) moved.Add((Statement)next);
            }
            if (exit == null || label == null || label.Label != exit.Label || moved.Count == 0 || moved.Any(m => HasOuterBreak(m) || m is VariableDeclarationStatement || m is LabelStatement)) continue;
            ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection open = null, fallback = null;
            var starts = new Dictionary<ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection, int>();
            bool known = true;
            foreach (var section in sections)
            {
                var content = section.Statements.ToList();
                if (content.Count == 1 && content[0] is BlockStatement) content = ((BlockStatement)content[0]).Statements.ToList();
                if (section.CaseLabels.Any(l => l.Expression.IsNull))
                {
                    var jump = content.Count == 1 ? content[0] as GotoStatement : null;
                    if (fallback != null || section.CaseLabels.Count != 1 || jump == null || jump.Label != label.Label) { known = false; break; }
                    fallback = section;
                    continue;
                }
                int start = -1;
                foreach (CaseLabel l in section.CaseLabels)
                {
                    var literal = Unwrap(l.Expression) as PrimitiveExpression;
                    int target;
                    if (literal == null || !(literal.Value is string) || twice.Contains((string)literal.Value) || !targets.TryGetValue((string)literal.Value, out target) || (start >= 0 && start != target)) { known = false; break; }
                    start = target;
                }
                if (!known) break;
                starts[section] = start;
                if (content.Count == 1 && content[0] is BreakStatement)
                {
                    if (open != null) { known = false; break; }
                    open = section;
                    continue;
                }
                var last = content.LastOrDefault() as GotoStatement;
                if (last == null || last.Label != label.Label || HasOuterBreak(section) || section.Descendants.OfType<GotoStatement>().Count() != 1) { known = false; break; }
            }
            if (!known || open == null || fallback == null || starts[open] != FirstOffset(moved[0])) continue;
            foreach (var section in sections)
            {
                if (section == open || section == fallback) continue;
                section.Descendants.OfType<GotoStatement>().Single().ReplaceWith(new BreakStatement());
            }
            Statement end = open.Statements.First();
            foreach (Statement m in moved)
            {
                m.Remove();
                open.Statements.InsertBefore(end, m);
            }
            exit.Remove();
            fallback.Remove();
            var entity = statement.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (entity != null && !entity.Descendants.OfType<GotoStatement>().Any(g => g.Label == label.Label)) label.Remove();
            var ordered = statement.SwitchSections.OrderBy(s => starts[s]).ToList();
            foreach (var section in ordered) section.Remove();
            foreach (var section in ordered) statement.SwitchSections.Add(section);
        }
    }

    private static void SectionTails(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var statement in rootNode.Descendants.OfType<SwitchStatement>().ToList())
        {
            var sections = statement.SwitchSections.ToList();
            if (sections.Count < 2 || !(statement.Parent is BlockStatement)) continue;
            ILInstruction subject = statement.Expression.Annotations.OfType<ILInstruction>().FirstOrDefault();
            IMethod method = MethodOf(subject);
            if (method == null) continue;
            RawIL raw;
            if (!bodies.TryGetValue(method, out raw)) { raw = RawIL.Read(context.TypeSystem, method); bodies[method] = raw; }
            if (raw == null) continue;
            var twice = new HashSet<string>();
            Dictionary<string, int> targets = StringTargets(raw, twice);
            ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection open = null;
            int own = -1, others = -1;
            bool known = true;
            foreach (var section in sections)
            {
                bool empty = section.Statements.Count == 1 && section.Statements.First() is BreakStatement;
                int start = -1;
                foreach (CaseLabel label in section.CaseLabels)
                {
                    if (label.Expression.IsNull) continue;
                    var literal = Unwrap(label.Expression) as PrimitiveExpression;
                    int target;
                    if (literal == null || !(literal.Value is string) || twice.Contains((string)literal.Value) || !targets.TryGetValue((string)literal.Value, out target)) { known = false; break; }
                    start = target;
                }
                if (!known) break;
                if (empty)
                {
                    if (open != null || start < 0 || section.CaseLabels.Count != 1) { known = false; break; }
                    open = section;
                    own = start;
                }
                else
                {
                    // Every other section leaves by itself.
                    Statement last = section.Statements.LastOrDefault();
                    var block = last as BlockStatement;
                    if (block != null) last = block.Statements.LastOrDefault();
                    if (!(last is ReturnStatement || last is ThrowStatement || last is ContinueStatement) || HasOuterBreak(section)) { known = false; break; }
                    if (start > others) others = start;
                }
            }
            if (!known || open == null || others < 0 || own >= others) continue;
            // What follows the switch and comes before the other sections in the IL.
            var moved = new List<Statement>();
            int previous = own - 1;
            for (AstNode next = statement.NextSibling; next != null; next = next.NextSibling)
            {
                var following = next as Statement;
                if (following == null) continue;
                int start = FirstOffset(following);
                if (start <= previous || start >= others) break;
                if (moved.Count == 0 && start != own) break;
                moved.Add(following);
                previous = start;
            }
            if (moved.Count == 0 || moved.Any(m => HasOuterBreak(m) || m is VariableDeclarationStatement || m is LabelStatement)) continue;
            Statement end = open.Statements.First();
            foreach (Statement m in moved)
            {
                m.Remove();
                open.Statements.InsertBefore(end, m);
            }
            if (EndsWithJump(moved[moved.Count - 1])) end.Remove();
        }
    }

    // 6. 'fixed (T* a = x, b = y) { }' unpins a first, then b; two nested 'fixed' statements
    //    unpin the inner one first. The decompiler always nests them.
    private static void FixedDeclarators(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var outer in rootNode.Descendants.OfType<FixedStatement>().ToList())
        {
            if (outer.Parent == null) continue;
            Statement only = Single(outer.EmbeddedStatement);
            var inner = only as FixedStatement;
            if (inner == null || outer.Type.ToString() != inner.Type.ToString()) continue;
            PinnedRegion first = outer.Annotations.OfType<PinnedRegion>().FirstOrDefault(), second = inner.Annotations.OfType<PinnedRegion>().FirstOrDefault();
            if (first == null || second == null || !first.Variable.Index.HasValue || !second.Variable.Index.HasValue) continue;
            IMethod method = MethodOf(first);
            if (method == null) continue;
            RawIL raw;
            if (!bodies.TryGetValue(method, out raw)) { raw = RawIL.Read(context.TypeSystem, method); bodies[method] = raw; }
            if (raw == null) continue;
            // Last store of each pinned slot: the one that unpins it.
            int a = -1, b = -1;
            foreach (RawIL.Op op in raw.Ops)
            {
                if (!RawIL.IsStLoc(op.Code)) continue;
                if (op.Operand == first.Variable.Index.Value) a = op.Offset;
                if (op.Operand == second.Variable.Index.Value) b = op.Offset;
            }
            if (a < 0 || b < 0 || a > b) continue;
            foreach (var variable in inner.Variables.ToList())
            {
                variable.Remove();
                outer.Variables.Add(variable);
            }
            outer.EmbeddedStatement = inner.EmbeddedStatement.Detach();
        }
    }
}
// C# transform that runs last: what the earlier C# transforms of the decompiler decide.
//
// 1. 'out T x' never read is printed 'out var _'. A discard takes a temporary of the compiler,
//    which is used again by the temporaries that follow; a variable keeps its slot. When the
//    slot is used nowhere else in the IL, the variable is given back (with no free temporary
//    of that type the two compile to the same instructions).
internal sealed class RestoreShapesLast : IAstTransform
{
    public void Run(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var declared in rootNode.Descendants.OfType<OutVarDeclarationExpression>().ToList())
        {
            if (declared.Variable.Name != "_") continue;
            ILVariable v = null;
            foreach (var rr in declared.Variable.Annotations.OfType<ILVariableResolveResult>()) v = rr.Variable;
            if (v == null || !v.Index.HasValue || v.Kind != VariableKind.Local || v.Function == null || v.Function.Method == null) continue;
            RawIL raw;
            if (!bodies.TryGetValue(v.Function.Method, out raw)) { raw = RawIL.Read(context.TypeSystem, v.Function.Method); bodies[v.Function.Method] = raw; }
            if (raw == null) continue;
            int uses = 0, addresses = 0;
            foreach (RawIL.Op op in raw.Ops)
            {
                if ((RawIL.IsLdLoc(op.Code) || RawIL.IsStLoc(op.Code)) && op.Operand == v.Index.Value) uses++;
                if ((op.Code == ILOpCode.Ldloca || op.Code == ILOpCode.Ldloca_s) && op.Operand == v.Index.Value) addresses++;
            }
            if (uses != 0 || addresses != 1) continue;
            // Only when another slot of that type is a temporary the discard could have taken:
            // one that no variable of the decompiled method stands for.
            IType type = raw.LocalType(v.Index.Value);
            bool spare = false;
            for (int slot = 0; slot < raw.Locals.Length && !spare; slot++)
                if (slot != v.Index.Value && type != null && raw.Locals[slot].Equals(type) && !v.Function.Variables.Any(x => x.Kind != VariableKind.Parameter && x.Index == slot && (x.LoadCount + x.StoreCount + x.AddressCount) > 0)) spare = true;
            if (!spare) continue;
            declared.Variable.Name = "unused" + v.Index.Value;
        }
    }
}