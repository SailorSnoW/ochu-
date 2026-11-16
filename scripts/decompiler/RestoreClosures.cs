// Closure shapes that only the syntax decides.
//
// 1. LambdaSyntax: a lambda nested in another one, whose closure is created outside of the
//    outer one, has its delegate cached in the closure when the outer one is written
//    '(...) => { }' and not when it is written 'delegate { }' (Roslyn, InLoopOrLambda looks
//    for loops and lambda expressions, not for anonymous methods). The decompiler prints
//    'delegate' for every lambda with a block body. A lambda whose body has the cache
//    ('ldfld <>9__N; dup; brtrue; pop' on a closure field) and no loop is printed '=>'.
//
// 2. CapturedUsing: 'using (T x = e) { }' with x captured. The decompiler only rebuilds a
//    'using' around a local; a captured one is left as 'T x = e; try { } finally { if (x !=
//    null) ((IDisposable)x).Dispose(); }', which declares x in the enclosing block: the
//    closure then has its fields in another order (a 'using' is a scope of its own, merged
//    after the variables of the enclosing one).
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.TypeSystem;
using SwitchSection = ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection;

internal sealed class RestoreClosures : IAstTransform
{
    public void Run(AstNode rootNode, TransformContext context)
    {
        CapturedUsing(rootNode);
        LambdaSyntax(rootNode, context);
        ForOverParameter(rootNode, context);
        ForeachCollectionLocal(rootNode, context);
    }

    // ---- 3. ForOverParameter: 'for (int i = 0; i < p.Length; i++) { T x = p[i]; ... }' over a
    //    parameter is printed 'foreach (T x in p)'. A 'foreach' copies the array or the string
    //    into a temporary and tests the length of the copy; the IL tests 'ldarg p' itself.
    private static void ForOverParameter(AstNode rootNode, TransformContext context)
    {
        foreach (var entity in rootNode.Descendants.OfType<EntityDeclaration>().ToList())
        {
            var loops = new List<KeyValuePair<ForeachStatement, ILVariable>>();
            foreach (var loop in entity.Descendants.OfType<ForeachStatement>())
            {
                ILVariable p = VariableOf(loop.InExpression);
                // (The same over a local: 'foreach' tests the length of a copy, in another slot.)
                if (!(loop.InExpression is IdentifierExpression) || p == null || (p.Kind != VariableKind.Parameter && p.Kind != VariableKind.Local && p.Kind != VariableKind.PatternLocal) || !p.Index.HasValue || p.Index.Value < 0) continue;
                if (p.Function == null || p.Function.Method == null) continue;
                if (!(p.Type.Kind == TypeKind.Array || p.Type.IsKnownType(KnownTypeCode.String))) continue;
                if (!(loop.VariableDesignation is SingleVariableDesignation)) continue;
                loops.Add(new KeyValuePair<ForeachStatement, ILVariable>(loop, p));
            }
            foreach (var group in loops.GroupBy(l => l.Value).ToList())
            {
                ILVariable p = group.Key;
                RawIL raw = RawIL.Read(context.TypeSystem, p.Function.Method);
                if (raw == null) continue;
                bool local = p.Kind != VariableKind.Parameter;
                // The copy a 'foreach' makes is stored once and read twice (element, length); a
                // variable of the source is read elsewhere too.
                if (local && raw.Ops.Count(o => RawIL.IsLdLoc(o.Code) && o.Operand == p.Index.Value) == 2 * raw.Ops.Count(o => RawIL.IsStLoc(o.Code) && o.Operand == p.Index.Value)) continue;
                int arg = local ? p.Index.Value : p.Index.Value + (p.Function.Method.IsStatic ? 0 : 1), tests = 0;
                for (int i = 1; i + 2 < raw.Ops.Count; i++)
                {
                    if (!(local ? RawIL.IsLdLoc(raw.Ops[i].Code) : RawIL.IsLdArg(raw.Ops[i].Code)) || raw.Ops[i].Operand != arg || !RawIL.IsLdLoc(raw.Ops[i - 1].Code)) continue;
                    int j = i + 1;
                    if (raw.Ops[j].Code == ILOpCode.Ldlen) { j++; if (raw.Ops[j].Code == ILOpCode.Conv_i4) j++; }
                    else if (raw.Ops[j].Code == ILOpCode.Callvirt) { IMethod m = raw.Method(raw.Ops[j]); if (m == null || m.Name != "get_Length") continue; j++; }
                    else continue;
                    if (j < raw.Ops.Count && (raw.Ops[j].Code == ILOpCode.Blt || raw.Ops[j].Code == ILOpCode.Blt_s) && raw.Ops[j].Operand < raw.Ops[j].Offset) tests++;
                }
                // Loops already printed 'for (...; i < p.Length; ...)' have that test as well.
                int plain = 0;
                foreach (var written in entity.Descendants.OfType<ForStatement>())
                {
                    var bound = written.Condition as BinaryOperatorExpression;
                    var length = bound == null || bound.Operator != BinaryOperatorType.LessThan ? null : bound.Right as MemberReferenceExpression;
                    if (length != null && length.MemberName == "Length" && length.Target is IdentifierExpression && VariableOf(length.Target) == p) plain++;
                }
                if (tests == 0 || tests != group.Count() + plain) continue;
                var names = new HashSet<string>(entity.Descendants.OfType<Identifier>().Select(n => n.Name));
                foreach (var pair in group)
                {
                    ForeachStatement loop = pair.Key;
                    string index = "index";
                    for (int n = 2; names.Contains(index); n++) index = "index" + n;
                    names.Add(index);
                    string element = ((SingleVariableDesignation)loop.VariableDesignation).Identifier;
                    var body = loop.EmbeddedStatement as BlockStatement;
                    Statement inner = loop.EmbeddedStatement.Detach();
                    if (body == null) { body = new BlockStatement(); body.Statements.Add(inner); }
                    Expression collection = loop.InExpression.Detach();
                    var read = new VariableDeclarationStatement(loop.VariableType.Detach(), element, new IndexerExpression(collection.Clone(), new IdentifierExpression(index)));
                    foreach (var rr in loop.VariableDesignation.Annotations.OfType<ILVariableResolveResult>()) read.Variables.Single().AddAnnotation(rr);
                    if (body.Statements.Count == 0) body.Statements.Add(read); else body.Statements.InsertBefore(body.Statements.First(), read);
                    var replacement = new ForStatement
                    {
                        Condition = new BinaryOperatorExpression(new IdentifierExpression(index), BinaryOperatorType.LessThan, new MemberReferenceExpression(collection, "Length")),
                        EmbeddedStatement = body
                    };
                    replacement.Initializers.Add(new VariableDeclarationStatement(new ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveType("int"), index, new PrimitiveExpression(0)));
                    replacement.Iterators.Add(new ExpressionStatement(new UnaryOperatorExpression(UnaryOperatorType.PostIncrement, new IdentifierExpression(index))));
                    loop.ReplaceWith(replacement);
                }
            }
        }
    }

    // ---- 3b. ForeachOverCopy: 'foreach (T x in e) { ... x ... }' with x read once keeps the
    //    element on the stack: no variable is left for the decompiler to recognise, and it
    //    prints 'T[] a = e; for (int i = 0; i < a.Length; i++) { ... a[i] ... }'. The slots of
    //    'a' and 'i' are stored by several loops of that shape ('stloc a; ldc.i4.0; stloc i;
    //    br', then 'ldloc a; ldloc i; ldelem' first in the body): temporaries, which variables
    //    of the source never share.
    internal static void ForeachOverCopy(AstNode rootNode, TransformContext context)
    {
        foreach (var loop in rootNode.Descendants.OfType<ForStatement>().ToList())
        {
            var copy = loop.PrevSibling as VariableDeclarationStatement;
            if (copy == null || copy.Variables.Count != 1 || copy.Variables.Single().Initializer.IsNull || loop.Initializers.Count != 1 || loop.Iterators.Count != 1) continue;
            var counter = loop.Initializers.Single() as VariableDeclarationStatement;
            if (counter == null || counter.Variables.Count != 1) continue;
            ILVariable a = VariableOf(copy.Variables.Single()), i = VariableOf(counter.Variables.Single());
            if (a == null || i == null || a.Kind != VariableKind.Local || i.Kind != VariableKind.Local || !a.Index.HasValue || !i.Index.HasValue || a.Type.Kind != TypeKind.Array) continue;
            if (a.Function == null || a.Function.Method == null || a.StoreCount != 1 || a.LoadCount != 2 || a.AddressCount != 0 || i.AddressCount != 0) continue;
            string array = copy.Variables.Single().Name, index = counter.Variables.Single().Name;
            var zero = counter.Variables.Single().Initializer as PrimitiveExpression;
            var bound = loop.Condition as BinaryOperatorExpression;
            var length = bound == null || bound.Operator != BinaryOperatorType.LessThan ? null : bound.Right as MemberReferenceExpression;
            var step = loop.Iterators.Single() as ExpressionStatement;
            var increment = step == null ? null : step.Expression as UnaryOperatorExpression;
            if (zero == null || !(zero.Value is int) || (int)zero.Value != 0 || length == null || length.MemberName != "Length") continue;
            if (!(bound.Left is IdentifierExpression) || ((IdentifierExpression)bound.Left).Identifier != index || !(length.Target is IdentifierExpression) || ((IdentifierExpression)length.Target).Identifier != array) continue;
            if (increment == null || increment.Operator != UnaryOperatorType.PostIncrement || !(increment.Expression is IdentifierExpression) || ((IdentifierExpression)increment.Expression).Identifier != index) continue;
            // One read of the element in the body, and nothing else on the array or the index.
            var reads = loop.EmbeddedStatement.Descendants.OfType<IndexerExpression>().Where(x => x.Target is IdentifierExpression && ((IdentifierExpression)x.Target).Identifier == array
                && x.Arguments.Count == 1 && x.Arguments.Single() is IdentifierExpression && ((IdentifierExpression)x.Arguments.Single()).Identifier == index).ToList();
            if (reads.Count != 1 || reads[0].Parent is AssignmentExpression && ((AssignmentExpression)reads[0].Parent).Left == reads[0]) continue;
            if (loop.EmbeddedStatement.Descendants.OfType<IdentifierExpression>().Count(x => x.Identifier == array || x.Identifier == index) != 2) continue;
            var entity = loop.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (entity == null) continue;
            RawIL raw = RawIL.Read(context.TypeSystem, a.Function.MoveNextMethod ?? a.Function.Method);
            if (raw == null) continue;
            int shaped = 0, stores = 0;
            for (int k = 0; k < raw.Ops.Count; k++)
            {
                if (!RawIL.IsStLoc(raw.Ops[k].Code) || raw.Ops[k].Operand != a.Index.Value) continue;
                stores++;
                if (k + 6 >= raw.Ops.Count || raw.Ops[k + 1].Code != ILOpCode.Ldc_i4_0 || !RawIL.IsStLoc(raw.Ops[k + 2].Code) || raw.Ops[k + 2].Operand != i.Index.Value) continue;
                if (raw.Ops[k + 3].Code != ILOpCode.Br && raw.Ops[k + 3].Code != ILOpCode.Br_s) continue;
                if (!RawIL.IsLdLoc(raw.Ops[k + 4].Code) || raw.Ops[k + 4].Operand != a.Index.Value || !RawIL.IsLdLoc(raw.Ops[k + 5].Code) || raw.Ops[k + 5].Operand != i.Index.Value) continue;
                ILOpCode element = raw.Ops[k + 6].Code;
                if (element != ILOpCode.Ldelem && !(element >= ILOpCode.Ldelem_i1 && element <= ILOpCode.Ldelem_ref)) continue;
                shaped++;
            }
            if (stores < 2 || shaped != stores) continue;
            var names = new HashSet<string>(entity.Descendants.OfType<Identifier>().Select(n => n.Name));
            string name = "element";
            for (int n = 2; names.Contains(name); n++) name = "element" + n;
            reads[0].ReplaceWith(new IdentifierExpression(name));
            var replacement = new ForeachStatement
            {
                VariableType = context.TypeSystemAstBuilder.ConvertType(((ArrayType)a.Type).ElementType),
                VariableDesignation = new SingleVariableDesignation { Identifier = name },
                InExpression = copy.Variables.Single().Initializer.Detach(),
                EmbeddedStatement = loop.EmbeddedStatement.Detach()
            };
            loop.ReplaceWith(replacement);
            copy.Remove();
        }
    }

    // ---- 4. ForeachCollectionLocal: a lambda in the collection of a 'foreach' is "in a
    //    loop" for the compiler (the statement is among its syntax parents), so its delegate
    //    is cached in the closure. No cache in the IL: the collection was computed before,
    //    'var list = e.FindAll(x => ...); foreach (T x in list)'.
    private static void ForeachCollectionLocal(AstNode rootNode, TransformContext context)
    {
        foreach (var loop in rootNode.Descendants.OfType<ForeachStatement>().ToList())
        {
            if (!(loop.Parent is BlockStatement)) continue;
            var lambdas = loop.InExpression.DescendantsAndSelf.Where(n => n is LambdaExpression || n is AnonymousMethodExpression).ToList();
            if (lambdas.Count != 1) continue;
            ILFunction inner = lambdas[0].Annotation<ILFunction>();
            if (inner == null || inner.Method == null || inner.Method.DeclaringTypeDefinition == null) continue;
            if (!inner.Method.DeclaringTypeDefinition.Name.StartsWith("<>c__DisplayClass")) continue;
            var outer = inner.Parent == null ? null : inner.Parent.Ancestors.OfType<ILFunction>().FirstOrDefault();
            if (outer == null || outer.Method == null) continue;
            RawIL raw = RawIL.Read(context.TypeSystem, outer.Method);
            if (raw == null) continue;
            int created = 0, cached = 0;
            for (int i = 0; i + 2 < raw.Ops.Count; i++)
            {
                if (raw.Ops[i].Code != ILOpCode.Ldftn) continue;
                IMethod target = raw.Method(raw.Ops[i]);
                if (target == null || !target.MetadataToken.Equals(inner.Method.MetadataToken)) continue;
                created++;
                if (raw.Ops[i + 2].Code == ILOpCode.Dup) cached++;
            }
            if (created != 1 || cached != 0) continue;
            var type = loop.InExpression.GetResolveResult();
            var entity = loop.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (type == null || type.Type.Kind == TypeKind.Unknown || entity == null) continue;
            var names = new HashSet<string>(entity.Descendants.OfType<Identifier>().Select(n => n.Name));
            string name = "collection";
            for (int n = 2; names.Contains(name); n++) name = "collection" + n;
            Expression value = loop.InExpression;
            value.ReplaceWith(new IdentifierExpression(name));
            var declaration = new VariableDeclarationStatement(context.TypeSystemAstBuilder.ConvertType(type.Type), name, value);
            ((BlockStatement)loop.Parent).Statements.InsertBefore(loop, declaration);
        }
    }

    // ---- 1

    private static void LambdaSyntax(AstNode rootNode, TransformContext context)
    {
        foreach (var node in rootNode.Descendants.OfType<AnonymousMethodExpression>().ToList())
        {
            ILFunction f = node.Annotation<ILFunction>();
            if (f == null || f.Method == null || node.Parent == null) continue;
            RawIL raw = RawIL.Read(context.TypeSystem, f.Method);
            if (raw == null || !CachesInClosure(raw, context.TypeSystem)) continue;
            var lambda = new LambdaExpression { IsAsync = node.IsAsync };
            if (node.HasParameterList)
            {
                foreach (ParameterDeclaration p in node.Parameters.ToList()) lambda.Parameters.Add(p.Detach());
            }
            else
            {
                bool named = true;
                var parameters = new List<ParameterDeclaration>();
                for (int i = 0; i < f.Method.Parameters.Count; i++)
                {
                    ILVariable v = f.Variables.FirstOrDefault(x => x.Kind == VariableKind.Parameter && x.Index == i);
                    if (v == null || string.IsNullOrEmpty(v.Name)) { named = false; break; }
                    ParameterDeclaration p = context.TypeSystemAstBuilder.ConvertParameter(f.Method.Parameters[i]);
                    p.Name = v.Name;
                    parameters.Add(p);
                }
                if (!named) continue;
                foreach (ParameterDeclaration p in parameters) lambda.Parameters.Add(p);
            }
            lambda.Body = node.Body.Detach();
            lambda.CopyAnnotationsFrom(node);
            node.ReplaceWith(lambda);
        }
    }

    private static bool CachesInClosure(RawIL raw, IDecompilerTypeSystem typeSystem)
    {
        bool cached = false;
        for (int i = 0; i < raw.Ops.Count; i++)
        {
            RawIL.Op op = raw.Ops[i];
            // A loop: lambdas in it are cached whatever the syntax around.
            if ((RawIL.IsCondBranch(op.Code) || op.Code == ILOpCode.Br || op.Code == ILOpCode.Br_s) && op.Operand <= op.Offset) return false;
            if (op.Code != ILOpCode.Ldfld || i + 3 >= raw.Ops.Count) continue;
            if (raw.Ops[i + 1].Code != ILOpCode.Dup || raw.Ops[i + 3].Code != ILOpCode.Pop) continue;
            ILOpCode jump = raw.Ops[i + 2].Code;
            if (jump != ILOpCode.Brtrue && jump != ILOpCode.Brtrue_s) continue;
            IField field = null;
            try { field = typeSystem.MainModule.ResolveEntity(MetadataTokens.EntityHandle(op.Operand)) as IField; }
            catch (System.BadImageFormatException) { }
            if (field == null || field.IsStatic || field.Type.Kind != TypeKind.Delegate) continue;
            if (field.DeclaringTypeDefinition == null || !field.DeclaringTypeDefinition.Name.StartsWith("<>c__DisplayClass")) continue;
            cached = true;
        }
        return cached;
    }

    // ---- 2

    private static void CapturedUsing(AstNode rootNode)
    {
        foreach (var block in rootNode.Descendants.OfType<TryCatchStatement>().ToList())
        {
            if (block.Parent == null || block.CatchClauses.Count != 0 || block.FinallyBlock.IsNull || block.FinallyBlock.Statements.Count != 1) continue;
            var declaration = block.PrevSibling as VariableDeclarationStatement;
            if (declaration == null || declaration.Variables.Count != 1 || !(block.Parent is BlockStatement)) continue;
            VariableInitializer variable = declaration.Variables.Single();
            ILVariable v = VariableOf(variable);
            ClosureScope closure;
            if (v == null || variable.Initializer.IsNull || !TrackClosures.Closures.TryGetValue(v, out closure)) continue;
            if (!DisposesIfNotNull(block.FinallyBlock.Statements.Single(), variable.Name)) continue;
            // A 'using' variable is read-only, and its scope ends with the statement.
            bool other = false;
            foreach (var id in block.Parent.Descendants.OfType<IdentifierExpression>())
            {
                if (VariableOf(id) != v) continue;
                if (!id.Ancestors.Contains(block)) { other = true; break; }
                var assign = id.Parent as AssignmentExpression;
                if ((assign != null && assign.Left == id) || id.Parent is DirectionExpression || id.Parent is UnaryOperatorExpression) { other = true; break; }
            }
            if (other) continue;
            var statement = new UsingStatement();
            block.ReplaceWith(statement);
            statement.ResourceAcquisition = declaration.Detach();
            statement.EmbeddedStatement = block.TryBlock.Detach();
        }
    }

    // if (x != null) { ((IDisposable)x).Dispose(); }
    private static bool DisposesIfNotNull(Statement s, string name)
    {
        var test = s as IfElseStatement;
        if (test == null || !test.FalseStatement.IsNull) return false;
        var condition = test.Condition as BinaryOperatorExpression;
        if (condition == null || condition.Operator != BinaryOperatorType.InEquality || !(condition.Right is NullReferenceExpression) || !IsName(condition.Left, name)) return false;
        Statement body = test.TrueStatement;
        var braces = body as BlockStatement;
        if (braces != null) { if (braces.Statements.Count != 1) return false; body = braces.Statements.Single(); }
        var call = body is ExpressionStatement ? ((ExpressionStatement)body).Expression as InvocationExpression : null;
        var member = call == null ? null : call.Target as MemberReferenceExpression;
        if (member == null || member.MemberName != "Dispose" || call.Arguments.Count != 0) return false;
        Expression target = member.Target;
        while (target is ParenthesizedExpression) target = ((ParenthesizedExpression)target).Expression;
        var cast = target as CastExpression;
        if (cast != null)
        {
            if (cast.Type.ToString() != "IDisposable") return false;
            target = cast.Expression;
        }
        return IsName(target, name);
    }

    private static bool IsName(Expression e, string name)
    {
        var id = e as IdentifierExpression;
        return id != null && id.Identifier == name;
    }

    private static ILVariable VariableOf(AstNode node)
    {
        var rr = node.Annotation<ILVariableResolveResult>();
        return rr == null ? null : rr.Variable;
    }
}

// An empty static constructor ('.cctor' made of one 'ret') was written: the compiler makes
// none for static fields without initializers. The decompiler drops it.
internal sealed class RestoreMembers : IAstTransform
{
    public void Run(AstNode rootNode, TransformContext context)
    {
        foreach (var type in rootNode.DescendantsAndSelf.OfType<TypeDeclaration>().ToList())
        {
            var definition = type.GetSymbol() as ITypeDefinition;
            if (definition == null || type.ClassType == ClassType.Enum || type.ClassType == ClassType.Interface) continue;
            IMethod cctor = definition.Methods.FirstOrDefault(m => m.IsStatic && m.Name == ".cctor");
            if (cctor == null || type.Members.OfType<ConstructorDeclaration>().Any(c => (c.Modifiers & Modifiers.Static) != 0)) continue;
            RawIL raw = RawIL.Read(context.TypeSystem, cctor);
            if (raw == null || raw.Ops.Count != 1 || raw.Ops[0].Code != ILOpCode.Ret) continue;
            var declaration = new ConstructorDeclaration { Name = type.Name, Modifiers = Modifiers.Static, Body = new BlockStatement() };
            type.Members.Add(declaration);
        }
        ImplicitConversions(rootNode, context);
        IteratorLoopVariables(rootNode, context);
        IteratorForLoops(rootNode, context);
        IfChains(rootNode, context);
        IteratorSteps(rootNode);
        CapturedThisCalls(rootNode);
    }

    // In an iterator '++v;' on a hoisted variable is 'ldfld; ldc.i4.1; add; stloc t; ldarg.0;
    // ldloc t; stfld' ('v++' stores the old value instead, 'v += 1' has no temporary). The
    // decompiler prints the temporary: 'int t = v + 1; v = t;', a declared local whose slot
    // the compiler then never shares.
    private static void IteratorSteps(AstNode rootNode)
    {
        foreach (var declaration in rootNode.Descendants.OfType<VariableDeclarationStatement>().ToList())
        {
            if (declaration.Variables.Count != 1 || !(declaration.Parent is BlockStatement)) continue;
            VariableInitializer temp = declaration.Variables.Single();
            var sum = temp.Initializer as BinaryOperatorExpression;
            var next = declaration.NextSibling as ExpressionStatement;
            var assign = next == null ? null : next.Expression as AssignmentExpression;
            if (sum == null || assign == null || assign.Operator != AssignmentOperatorType.Assign) continue;
            if (sum.Operator != BinaryOperatorType.Add && sum.Operator != BinaryOperatorType.Subtract) continue;
            var one = sum.Right as PrimitiveExpression;
            var target = assign.Left as IdentifierExpression;
            var source = sum.Left as IdentifierExpression;
            var value = assign.Right as IdentifierExpression;
            if (one == null || !(one.Value is int) || (int)one.Value != 1 || target == null || source == null || value == null) continue;
            if (target.Identifier != source.Identifier || value.Identifier != temp.Name) continue;
            var t = temp.Annotation<ILVariableResolveResult>();
            var entity = declaration.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (t == null || entity == null || !entity.Descendants.OfType<YieldReturnStatement>().Any()) continue;
            if (entity.Descendants.OfType<IdentifierExpression>().Count(i => { var r = i.Annotation<ILVariableResolveResult>(); return r != null && r.Variable == t.Variable; }) != 1) continue;
            var step = new UnaryOperatorExpression(sum.Operator == BinaryOperatorType.Add ? UnaryOperatorType.Increment : UnaryOperatorType.Decrement, target.Detach());
            next.Remove();
            declaration.ReplaceWith(new ExpressionStatement(step));
        }
    }

    // 'base.M()' in a lambda of a closure class is a call to a generated method ('<>n__0').
    // A direct 'call' there is 'this.M()' on the captured 'this', M virtual but sealed: the
    // compiler knows the receiver is not null and that nothing overrides M.
    private static void CapturedThisCalls(AstNode rootNode)
    {
        foreach (var node in rootNode.Descendants.OfType<BaseReferenceExpression>().ToList())
        {
            var access = node.Parent as MemberReferenceExpression;
            var member = access == null ? null : access.GetSymbol() as IMember;
            if (member == null && access != null && access.Parent is InvocationExpression) member = access.Parent.GetSymbol() as IMember;
            if (member == null || member.IsOverridable) continue;
            bool closure = false;
            foreach (AstNode a in node.Ancestors)
            {
                if (!(a is LambdaExpression) && !(a is AnonymousMethodExpression)) continue;
                ILFunction f = a.Annotation<ILFunction>();
                closure = f != null && f.Method != null && f.Method.DeclaringTypeDefinition != null && f.Method.DeclaringTypeDefinition.Name.StartsWith("<>c__DisplayClass");
                break;
            }
            if (closure) node.ReplaceWith(new ThisReferenceExpression());
        }
    }

    // 'if (x == 0) return a; if (x == 1) return b; ...' is printed as a 'switch'. A compiled
    // 'switch' jumps to its sections ('switch' table, or 'beq' to each one); in a chain of
    // 'if' every body is entered by falling through the test that skips it ('bne.un', 'brtrue').
    // The statements of a section, which the decompiler still keeps in a block at this stage.
    private static List<Statement> Content(SwitchSection section)
    {
        var list = section.Statements.ToList();
        if (list.Count == 1 && list[0] is BlockStatement) list = ((BlockStatement)list[0]).Statements.ToList();
        return list;
    }

    private static void IfChains(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var node in rootNode.Descendants.OfType<SwitchStatement>().ToList())
        {
            if (node.Parent == null || !(node.Parent is BlockStatement || node.Parent is SwitchSection) || !(node.Expression is IdentifierExpression) || node.SwitchSections.Count < 2) continue;
            var starts = new List<KeyValuePair<int, SwitchSection>>();
            bool chain = true;
            foreach (SwitchSection section in node.SwitchSections)
            {
                var content = Content(section);
                Statement last = content.LastOrDefault();
                if (!(last is ReturnStatement || last is ThrowStatement) || section.Descendants.OfType<BreakStatement>().Any()
                    || section.CaseLabels.Any(l => l.Expression.IsNull) || section.Descendants.OfType<VariableDeclarationStatement>().Any()) { chain = false; break; }
                int start = -1;
                RawIL raw = null;
                foreach (AstNode n in content.First().DescendantsAndSelf)
                {
                    foreach (var inst in n.Annotations.OfType<ILInstruction>())
                    {
                        ILFunction f = inst.Ancestors.OfType<ILFunction>().FirstOrDefault();
                        if (f == null || f.Method == null) continue;
                        if (raw == null && !bodies.TryGetValue(f.Method, out raw)) { raw = RawIL.Read(context.TypeSystem, f.Method); bodies[f.Method] = raw; }
                        int s = RawIL.StartOf(inst);
                        if (s >= 0 && (start < 0 || s < start)) start = s;
                    }
                }
                RawIL.Op before;
                if (raw == null || start < 0 || !raw.Near(start, -1, out before)) { chain = false; break; }
                bool skips = before.Code == ILOpCode.Bne_un || before.Code == ILOpCode.Bne_un_s || before.Code == ILOpCode.Brtrue || before.Code == ILOpCode.Brtrue_s;
                // With several labels ('x == a || x == b') the first tests jump to the body ('beq').
                if (!skips || before.Operand == start || (raw.Targets.Contains(start) && section.CaseLabels.Count < 2)) { chain = false; break; }
                starts.Add(new KeyValuePair<int, SwitchSection>(start, section));
            }
            if (!chain) continue;
            foreach (var entry in starts.OrderBy(e => e.Key))
            {
                Expression condition = null;
                foreach (CaseLabel label in entry.Value.CaseLabels)
                {
                    var test = new BinaryOperatorExpression(node.Expression.Clone(), BinaryOperatorType.Equality, label.Expression.Clone());
                    condition = condition == null ? (Expression)test : new BinaryOperatorExpression(condition, BinaryOperatorType.ConditionalOr, test);
                }
                var then = new BlockStatement();
                foreach (Statement s in Content(entry.Value)) then.Statements.Add(s.Detach());
                var check = new IfElseStatement(condition, then);
                node.Parent.InsertChildBefore(node, check, (ICSharpCode.Decompiler.CSharp.Syntax.Role<Statement>)node.Role);
            }
            node.Remove();
        }
    }

    // In an iterator, a hoisted variable of a nested scope is cleared when the scope ends
    // ('ldarg.0; ldnull; stfld'): 'for (T v = e; ...)' is such a scope, 'T v = e; for (; ...)'
    // in the outermost block of the method is not. No 'ldnull; stfld' on a field of the
    // iterator class in MoveNext: the variable was declared before the loop.
    // The reverse: 'for (T v = e; c; v = s) { ... }' in an iterator clears the field of v
    // when the loop ends ('ldarg.0; ldnull; stfld <v>' right after its test). The decompiler
    // prints 'T v = e; while (c) { ...; v = s; }', a variable of the outermost block, which
    // is never cleared.
    private static void IteratorForLoops(AstNode rootNode, TransformContext context)
    {
        foreach (var method in rootNode.Descendants.OfType<MethodDeclaration>().ToList())
        {
            if (method.Body.IsNull || !method.Body.Descendants.OfType<YieldReturnStatement>().Any()) continue;
            var symbol = method.GetSymbol() as IMethod;
            ITypeDefinition owner = symbol == null ? null : symbol.DeclaringTypeDefinition;
            if (owner == null) continue;
            string prefix = "<" + symbol.Name.Replace('<', '_').Replace('>', '_') + ">d__";
            var machines = owner.NestedTypes.Where(t => t.Name.StartsWith(prefix)).ToList();
            if (machines.Count != 1) continue;
            IMethod moveNext = machines[0].Methods.FirstOrDefault(m => m.Name == "MoveNext" || m.Name.EndsWith(".MoveNext"));
            RawIL raw = moveNext == null ? null : RawIL.Read(context.TypeSystem, moveNext);
            if (raw == null) continue;
            foreach (var loop in method.Body.Descendants.OfType<WhileStatement>().ToList())
            {
                var declaration = loop.PrevSibling as VariableDeclarationStatement;
                var body = loop.EmbeddedStatement as BlockStatement;
                if (declaration == null || body == null || declaration.Variables.Count != 1 || declaration.Variables.Single().Initializer.IsNull || body.Statements.Count < 2) continue;
                string name = declaration.Variables.Single().Name;
                var rr = declaration.Variables.Single().Annotation<ILVariableResolveResult>();
                IField field = rr == null ? null : rr.Variable.StateMachineField;
                if (field == null || field.MetadataToken.IsNil || rr.Variable.Type.IsReferenceType != true) continue;
                var step = body.Statements.Last() as ExpressionStatement;
                var assign = step == null ? null : step.Expression as AssignmentExpression;
                if (assign == null || !(assign.Left is IdentifierExpression) || ((IdentifierExpression)assign.Left).Identifier != name) continue;
                if (!loop.Condition.Descendants.OfType<IdentifierExpression>().Any(i => i.Identifier == name)) continue;
                // 'continue;' would skip the step; the variable lives in the loop only.
                if (body.Descendants.OfType<ContinueStatement>().Any()) continue;
                bool later = false;
                for (AstNode next = loop.NextSibling; next != null; next = next.NextSibling)
                    if (next.DescendantsAndSelf.OfType<IdentifierExpression>().Any(i => i.Identifier == name)) later = true;
                if (later) continue;
                int token = System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(field.MetadataToken);
                bool cleared = false;
                for (int i = 0; i + 2 < raw.Ops.Count; i++)
                    if (raw.Ops[i].Code == ILOpCode.Ldarg_0 && raw.Ops[i + 1].Code == ILOpCode.Ldnull && raw.Ops[i + 2].Code == ILOpCode.Stfld && raw.Ops[i + 2].Operand == token) cleared = true;
                if (!cleared) continue;
                var replacement = new ForStatement();
                loop.ReplaceWith(replacement);
                replacement.Initializers.Add(declaration.Detach());
                replacement.Condition = loop.Condition.Detach();
                replacement.Iterators.Add(step.Detach());
                replacement.EmbeddedStatement = body.Detach();
            }
        }
    }

    private static void IteratorLoopVariables(AstNode rootNode, TransformContext context)
    {
        foreach (var method in rootNode.Descendants.OfType<MethodDeclaration>().ToList())
        {
            if (method.Body.IsNull) continue;
            var loops = method.Body.Statements.OfType<ForStatement>().Where(f => f.Initializers.Count == 1 && f.Initializers.Single() is VariableDeclarationStatement).ToList();
            if (loops.Count == 0 || !method.Body.Descendants.OfType<YieldReturnStatement>().Any()) continue;
            var symbol = method.GetSymbol() as IMethod;
            ITypeDefinition owner = symbol == null ? null : symbol.DeclaringTypeDefinition;
            if (owner == null) continue;
            string prefix = "<" + symbol.Name.Replace('<', '_').Replace('>', '_') + ">d__";
            var machines = owner.NestedTypes.Where(t => t.Name.StartsWith(prefix)).ToList();
            if (machines.Count != 1) continue;
            IMethod moveNext = machines[0].Methods.FirstOrDefault(m => m.Name == "MoveNext" || m.Name.EndsWith(".MoveNext"));
            RawIL raw = moveNext == null ? null : RawIL.Read(context.TypeSystem, moveNext);
            if (raw == null) continue;
            bool clears = false;
            for (int i = 0; i + 1 < raw.Ops.Count; i++)
                if (raw.Ops[i].Code == ILOpCode.Ldnull && raw.Ops[i + 1].Code == ILOpCode.Stfld) clears = true;
            if (clears) continue;
            foreach (ForStatement loop in loops)
            {
                var declaration = (VariableDeclarationStatement)loop.Initializers.Single();
                if (declaration.Variables.Count != 1) continue;
                var rr = declaration.Variables.Single().Annotation<ILVariableResolveResult>();
                if (rr == null || rr.Variable.Type.IsReferenceType != true) continue;
                declaration.Remove();
                method.Body.Statements.InsertBefore(loop, declaration);
            }
        }
    }

    // 'b != 1.0' with b converted by a user-defined implicit operator to double: the
    // decompiler prints '(double)b != 1.0', and the compiler adds a 'conv.r8' to an explicit
    // cast to double (as it does for '(double)d'). No 'conv' after the call in the IL: no cast.
    private static void ImplicitConversions(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var cast in rootNode.Descendants.OfType<CastExpression>().ToList())
        {
            string to = cast.Type.ToString();
            if (to != "double" && to != "float") continue;
            AstNode top = cast;
            while (top.Parent is ParenthesizedExpression) top = top.Parent;
            var binary = top.Parent as BinaryOperatorExpression;
            if (binary == null) continue;
            Expression other = binary.Left == top ? binary.Right : binary.Left;
            var otherType = other.GetResolveResult();
            if (otherType == null || !otherType.Type.IsKnownType(to == "double" ? KnownTypeCode.Double : KnownTypeCode.Single)) continue;
            bool bare = false;
            foreach (var call in cast.Annotations.OfType<CallInstruction>())
            {
                if (call.Method.Name != "op_Implicit" || !call.Method.ReturnType.IsKnownType(to == "double" ? KnownTypeCode.Double : KnownTypeCode.Single)) continue;
                ILFunction f = call.Ancestors.OfType<ILFunction>().FirstOrDefault();
                if (f == null || f.Method == null) continue;
                RawIL raw;
                if (!bodies.TryGetValue(f.Method, out raw)) { raw = RawIL.Read(context.TypeSystem, f.Method); bodies[f.Method] = raw; }
                int end = RawIL.EndOf(call);
                RawIL.Op next;
                if (raw == null || end < 0 || !raw.IsAt(end, out next)) continue;
                RawIL.Op self;
                // The range of the call may include the conversion that follows it.
                if (raw.EndsAt(end, out self) && (self.Code == ILOpCode.Conv_r8 || self.Code == ILOpCode.Conv_r4)) continue;
                if (next.Code != ILOpCode.Conv_r8 && next.Code != ILOpCode.Conv_r4) bare = true;
            }
            if (!bare) continue;
            Expression inner = cast.Expression.Detach();
            top.ReplaceWith(inner);
        }
    }
}

// 'x--;' on a captured variable reads the field into a temporary first ('ldfld; stloc;
// ldloc; ldc.i4.1; sub; stfld'); 'x -= 1;' does not ('ldfld; ldc.i4.1; sub; stfld'). The
// decompiler prints 'x--' for both. Per function and per closure field: when no step in the
// IL goes through a temporary, the statements are printed 'x -= 1'.
internal sealed class RestoreCapturedSteps : IAstTransform
{
    public void Run(AstNode rootNode, TransformContext context)
    {
        PatternVariables(rootNode, context);
        EnumNegations(rootNode, context);
        NarrowedConditionals(rootNode, context);
        var verdicts = new Dictionary<string, bool>();
        foreach (var node in rootNode.Descendants.OfType<UnaryOperatorExpression>().ToList())
        {
            if (node.Operator != UnaryOperatorType.PostIncrement && node.Operator != UnaryOperatorType.PostDecrement) continue;
            if (!(node.Parent is ExpressionStatement) || !(node.Expression is IdentifierExpression)) continue;
            var rr = node.Expression.Annotation<ILVariableResolveResult>();
            IField field;
            if (rr == null || !TrackClosures.Fields.TryGetValue(rr.Variable, out field) || field.MetadataToken.IsNil) continue;
            IMethod method = null;
            foreach (var inst in node.Annotations.OfType<ILInstruction>())
            {
                ILFunction f = inst.Ancestors.OfType<ILFunction>().FirstOrDefault();
                if (f != null && f.Method != null) method = f.Method;
            }
            if (method == null) continue;
            string key = MetadataTokens.GetToken(method.MetadataToken) + ":" + MetadataTokens.GetToken(field.MetadataToken);
            bool direct;
            if (!verdicts.TryGetValue(key, out direct))
            {
                direct = Direct(RawIL.Read(context.TypeSystem, method), MetadataTokens.GetToken(field.MetadataToken));
                verdicts[key] = direct;
            }
            if (!direct) continue;
            var assign = new AssignmentExpression(node.Expression.Detach(),
                node.Operator == UnaryOperatorType.PostIncrement ? AssignmentOperatorType.Add : AssignmentOperatorType.Subtract, new PrimitiveExpression(1));
            assign.CopyAnnotationsFrom(node);
            node.ReplaceWith(assign);
        }
    }

    // 'if (!(e is T v && v == c)) return;' stores e and reads it back at once ('stloc v; ldloc
    // v', v read nowhere else): a declared local read once stays on the stack, a pattern
    // variable does not. The decompiler prints 'T v = e; if (v != c) return;'.
    private static void PatternVariables(AstNode rootNode, TransformContext context)
    {
        foreach (var declaration in rootNode.Descendants.OfType<VariableDeclarationStatement>().ToList())
        {
            if (declaration.Variables.Count != 1 || !(declaration.Parent is BlockStatement)) continue;
            VariableInitializer variable = declaration.Variables.Single();
            var test = declaration.NextSibling as IfElseStatement;
            var rr = variable.Annotation<ILVariableResolveResult>();
            if (test == null || rr == null || variable.Initializer.IsNull) continue;
            ILVariable v = rr.Variable;
            if (v.Kind != VariableKind.Local || !v.Index.HasValue || v.Function == null || v.Function.Method == null) continue;
            if (v.Type.IsReferenceType != false || v.Type.IsKnownType(KnownTypeCode.NullableOfT) || v.Type.Kind == TypeKind.TypeParameter) continue;
            if (v.Type.GetDefinition() != null && v.Type.GetDefinition().KnownTypeCode == KnownTypeCode.NullableOfT) continue;
            var entity = declaration.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            if (entity == null) continue;
            var uses = entity.Descendants.OfType<IdentifierExpression>().Where(i => { var r = i.Annotation<ILVariableResolveResult>(); return r != null && r.Variable == v; }).ToList();
            if (uses.Count != 1 || !uses[0].Ancestors.Contains(test.Condition) && uses[0] != test.Condition) continue;
            var comparison = uses[0].Parent as BinaryOperatorExpression;
            if (comparison == null || comparison.Left != uses[0]) continue;
            switch (comparison.Operator)
            {
                case BinaryOperatorType.Equality: case BinaryOperatorType.InEquality: case BinaryOperatorType.GreaterThan:
                case BinaryOperatorType.GreaterThanOrEqual: case BinaryOperatorType.LessThan: case BinaryOperatorType.LessThanOrEqual: break;
                default: continue;
            }
            // The first thing the condition evaluates.
            bool first = true;
            for (AstNode n = comparison; n != test.Condition; n = n.Parent)
            {
                AstNode parent = n.Parent;
                if (parent is ParenthesizedExpression) continue;
                var not = parent as UnaryOperatorExpression;
                if (not != null && not.Operator == UnaryOperatorType.Not) continue;
                var logic = parent as BinaryOperatorExpression;
                if (logic != null && (logic.Operator == BinaryOperatorType.ConditionalAnd || logic.Operator == BinaryOperatorType.ConditionalOr) && logic.Left == n) continue;
                first = false;
                break;
            }
            if (!first) continue;
            RawIL raw = RawIL.Read(context.TypeSystem, v.Function.Method);
            if (raw == null) continue;
            int stores = 0, loads = 0, at = -1;
            for (int i = 0; i < raw.Ops.Count; i++)
            {
                RawIL.Op op = raw.Ops[i];
                if (RawIL.IsStLoc(op.Code) && op.Operand == v.Index.Value) { stores++; at = i; }
                else if ((RawIL.IsLdLoc(op.Code) || op.Code == ILOpCode.Ldloca || op.Code == ILOpCode.Ldloca_s) && op.Operand == v.Index.Value) loads++;
            }
            if (stores != 1 || loads != 1 || at + 1 >= raw.Ops.Count || !RawIL.IsLdLoc(raw.Ops[at + 1].Code) || raw.Ops[at + 1].Operand != v.Index.Value) continue;
            if (at > 0 && raw.Ops[at - 1].Code == ILOpCode.Dup) continue;

            var pattern = new DeclarationExpression { Type = declaration.Type.Clone(), Designation = new SingleVariableDesignation { Identifier = variable.Name } };
            var match = new BinaryOperatorExpression(variable.Initializer.Detach(), BinaryOperatorType.IsPattern, pattern);
            var holder = new NullReferenceExpression();
            comparison.ReplaceWith(holder);
            holder.ReplaceWith(new ParenthesizedExpression(new BinaryOperatorExpression(match, BinaryOperatorType.ConditionalAnd, comparison)));
            declaration.Remove();
        }
    }
    private static RawIL BodyOf(ILInstruction inst, TransformContext context, Dictionary<IMethod, RawIL> bodies)
    {
        ILFunction f = inst.Ancestors.OfType<ILFunction>().FirstOrDefault();
        if (f == null || f.Method == null) return null;
        RawIL raw;
        if (!bodies.TryGetValue(f.Method, out raw)) { raw = RawIL.Read(context.TypeSystem, f.Method); bodies[f.Method] = raw; }
        return raw;
    }

    // '-(int)e' on an enum is a 'neg'. The decompiler prints '0 - e', a 'sub'.
    private static void EnumNegations(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var node in rootNode.Descendants.OfType<BinaryOperatorExpression>().ToList())
        {
            if (node.Operator != BinaryOperatorType.Subtract) continue;
            var zero = node.Left as PrimitiveExpression;
            if (zero == null || !(zero.Value is int) || (int)zero.Value != 0) continue;
            var type = node.Right.GetResolveResult();
            if (type == null || type.Type.Kind != TypeKind.Enum) continue;
            bool negated = false;
            foreach (AstNode n in node.Right.DescendantsAndSelf)
                foreach (var inst in n.Annotations.OfType<ILInstruction>())
                {
                    RawIL raw = BodyOf(inst, context, bodies);
                    int end = RawIL.EndOf(inst);
                    RawIL.Op op;
                    if (raw != null && end >= 0 && raw.IsAt(end, out op) && op.Code == ILOpCode.Neg) negated = true;
                }
            if (!negated) continue;
            IType underlying = type.Type.GetDefinition() == null ? null : type.Type.GetDefinition().EnumUnderlyingType;
            if (underlying == null) continue;
            Expression operand = node.Right.Detach();
            node.ReplaceWith(new UnaryOperatorExpression(UnaryOperatorType.Minus, new CastExpression(context.TypeSystemAstBuilder.ConvertType(underlying), operand)));
        }
    }

    // '(byte)(c ? 255 : b)': the choice is an int and the IL converts it ('conv.u1' after the
    // two branches). The decompiler gives both branches the small type and drops the cast.
    private static void NarrowedConditionals(AstNode rootNode, TransformContext context)
    {
        var bodies = new Dictionary<IMethod, RawIL>();
        foreach (var node in rootNode.Descendants.OfType<ConditionalExpression>().ToList())
        {
            AstNode top = node;
            while (top.Parent is ParenthesizedExpression) top = top.Parent;
            if (top.Parent is CastExpression || top.Parent == null) continue;
            string to = null;
            foreach (var inst in node.Annotations.OfType<IfInstruction>())
            {
                RawIL raw = BodyOf(inst, context, bodies);
                int end = RawIL.EndOf(inst);
                RawIL.Op op;
                if (raw == null || end < 0 || !raw.IsAt(end, out op)) continue;
                if (op.Code == ILOpCode.Conv_u1) to = "byte";
                else if (op.Code == ILOpCode.Conv_i1) to = "sbyte";
                else if (op.Code == ILOpCode.Conv_u2) to = "ushort";
                else if (op.Code == ILOpCode.Conv_i2) to = "short";
            }
            if (to == null) continue;
            var whole = node.GetResolveResult();
            if (whole == null || whole.Type.GetDefinition() == null || whole.Type.GetDefinition().ReflectionName != ReflectionNameOf(to)) continue;
            // One branch read as an int makes the choice an int.
            Expression branch = IsConstant(node.FalseExpression) ? node.TrueExpression : node.FalseExpression;
            if (IsConstant(branch)) continue;
            var holder = new NullReferenceExpression();
            branch.ReplaceWith(holder);
            holder.ReplaceWith(new CastExpression(new ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveType("int"), branch));
            var cast = new CastExpression();
            top.ReplaceWith(cast);
            cast.Type = new ICSharpCode.Decompiler.CSharp.Syntax.PrimitiveType(to);
            cast.Expression = (Expression)top;
        }
    }

    private static string ReflectionNameOf(string keyword)
    {
        switch (keyword)
        {
            case "byte": return "System.Byte";
            case "sbyte": return "System.SByte";
            case "ushort": return "System.UInt16";
            default: return "System.Int16";
        }
    }

    private static bool IsConstant(Expression e)
    {
        while (e is ParenthesizedExpression) e = ((ParenthesizedExpression)e).Expression;
        if (e is PrimitiveExpression) return true;
        var rr = e.GetResolveResult();
        return rr != null && rr.IsCompileTimeConstant;
    }
    // Every 'ldfld F ... stfld F' step by one in the body is made without a temporary.
    private static bool Direct(RawIL raw, int field)
    {
        if (raw == null) return false;
        int direct = 0, other = 0;
        for (int i = 0; i + 3 < raw.Ops.Count; i++)
        {
            if (raw.Ops[i].Code != ILOpCode.Ldfld || raw.Ops[i].Operand != field) continue;
            int j = i + 1;
            bool temp = false;
            if (RawIL.IsStLoc(raw.Ops[j].Code) && j + 1 < raw.Ops.Count && RawIL.IsLdLoc(raw.Ops[j + 1].Code) && raw.Ops[j + 1].Operand == raw.Ops[j].Operand) { temp = true; j += 2; }
            // The closure may be loaded between the two ('ldloc c; ldfld; stloc t; ldloc c; ldloc t').
            if (RawIL.IsStLoc(raw.Ops[j].Code)) { temp = true; while (j < raw.Ops.Count && (RawIL.IsStLoc(raw.Ops[j].Code) || RawIL.IsLdLoc(raw.Ops[j].Code) || RawIL.IsLdArg(raw.Ops[j].Code))) j++; }
            if (j + 2 >= raw.Ops.Count || raw.Ops[j].Code != ILOpCode.Ldc_i4_1) continue;
            if (raw.Ops[j + 1].Code != ILOpCode.Add && raw.Ops[j + 1].Code != ILOpCode.Sub) continue;
            RawIL.Op store = raw.Ops[j + 2];
            if (store.Code == ILOpCode.Stfld && store.Operand == field) { if (temp) other++; else direct++; }
            else if (RawIL.IsStLoc(store.Code) || store.Code == ILOpCode.Dup) other++;
        }
        return direct > 0 && other == 0;
    }
}
