// Captured variables are declared where the closure class of the IL says.
//
// The compiler makes one closure class per scope that declares captured variables and
// creates it where the scope starts. It then merges the closure of a scope into the one of
// an enclosing scope when exactly the same lambdas capture both and no loop lies between
// (Roslyn, ClosureConversion.Analysis.MergeEnvironments). The decompiler declares a variable
// at its first use, in the innermost block that holds all its uses:
//
//     Menu menu = ...;                         one closure in the IL: menu, image, scale
//     if (menu != null) {
//         MenuImage image = ...;               captured by other lambdas than 'menu':
//         if (image != null) {                 recompiled, a second closure here
//             float scale = 1f;                and a third one here
//             sprite.OnHover += delegate { menu.Scale(scale); };
//
// Where the IL creates a closure ('newobj' of the closure class) tells its scope: the
// instructions that follow the 'newobj' in its basic block are in it.
//
//   TrackClosures (in place of TransformDisplayClassUsage, same result) notes for each
//   variable the decompiler makes out of a closure field: the closure, and the instructions
//   that followed its creation.
//   HoistCapturedDeclarations (C#, after DeclareVariables) applies the compiler's merging
//   rule to the declarations as decompiled. A variable whose closure would not end up in
//   the scope where the IL creates it is declared there instead: 'T x;' moves out, 'x = e;'
//   stays. A closure created just before a 'switch' is the scope of the switch sections:
//   the declaration goes in the section, outside the braces the decompiler adds.
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;
using SwitchSection = ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection;

internal sealed class ClosureScope
{
    public ITypeDefinition Type;
    public Block Block; // where the closure is created
    public int Index;   // and at which instruction
    // The instructions that followed in that block, in order, each with what it contains.
    public readonly List<HashSet<ILInstruction>> Following = new List<HashSet<ILInstruction>>();
    public readonly HashSet<ILInstruction> Anchors = new HashSet<ILInstruction>();
    // Closures created later in the same block.
    public readonly List<ClosureScope> Later = new List<ClosureScope>();
    public readonly List<ClosureScope> Earlier = new List<ClosureScope>();
}

internal sealed class TrackClosures : IILTransform
{
    internal static readonly ConditionalWeakTable<ILVariable, ClosureScope> Closures = new ConditionalWeakTable<ILVariable, ClosureScope>();
    // The closure field each of these variables was.
    internal static readonly ConditionalWeakTable<ILVariable, IField> Fields = new ConditionalWeakTable<ILVariable, IField>();

    private sealed class Tracker : TransformDisplayClassUsage
    {
        public readonly Dictionary<ITypeDefinition, ClosureScope> Scopes = new Dictionary<ITypeDefinition, ClosureScope>();
        public readonly List<KeyValuePair<ILVariable, IField>> Uses = new List<KeyValuePair<ILVariable, IField>>();

        protected override void VisitStLoc(StLoc inst)
        {
            // stloc V(newobj Closure()), or the same under an object initializer block
            var created = inst.Value as NewObj;
            var init = inst.Value as Block;
            if (created == null && init != null && init.Kind == BlockKind.ObjectInitializer && init.Instructions.Count > 0 && init.Instructions[0] is StLoc)
                created = ((StLoc)init.Instructions[0]).Value as NewObj;
            var block = inst.Parent as Block;
            ITypeDefinition type = created == null ? null : created.Method.DeclaringTypeDefinition;
            if (type != null && block != null && type.Name.StartsWith("<>c__DisplayClass") && !Scopes.ContainsKey(type))
            {
                var scope = new ClosureScope { Type = type, Block = block, Index = inst.ChildIndex };
                for (int i = inst.ChildIndex + 1; i < block.Instructions.Count; i++)
                {
                    var set = new HashSet<ILInstruction>();
                    Collect(block.Instructions[i], set);
                    scope.Following.Add(set);
                    scope.Anchors.UnionWith(set);
                }
                foreach (ClosureScope other in Scopes.Values)
                {
                    if (other.Block != block) continue;
                    if (other.Index < scope.Index) { other.Later.Add(scope); scope.Earlier.Add(other); } else { scope.Later.Add(other); other.Earlier.Add(scope); }
                }
                Scopes[type] = scope;
            }
            base.VisitStLoc(inst);
        }

        private static void Collect(ILInstruction inst, HashSet<ILInstruction> into)
        {
            into.Add(inst);
            foreach (ILInstruction c in inst.Children)
                if (!(c is ILFunction) && !(c is Block) && !(c is BlockContainer)) Collect(c, into);
        }

        protected override void VisitLdFlda(LdFlda inst)
        {
            ILInstruction parent = inst.Parent;
            int index = inst.ChildIndex;
            IField field = inst.Field;
            base.VisitLdFlda(inst);
            // The base class has put 'ldloca v' in its place when the field is a captured variable.
            if (parent == null || index >= parent.Children.Count) return;
            var address = parent.Children[index] as LdLoca;
            if (address != null && !ReferenceEquals(address, inst)) Uses.Add(new KeyValuePair<ILVariable, IField>(address.Variable, field));
        }
    }

    public void Run(ILFunction function, ILTransformContext context)
    {
        var tracker = new Tracker();
        ((IILTransform)tracker).Run(function, context);
        if (function.Method == null || !Program.Enabled("RestoreCaptureScope", function.Method.MetadataToken)) return;
        foreach (var use in tracker.Uses)
        {
            ILVariable v = use.Key;
            ITypeDefinition type = use.Value.DeclaringTypeDefinition;
            ClosureScope scope, known;
            if (v.Kind != VariableKind.Local || type == null) continue;
            if (!tracker.Scopes.TryGetValue(type, out scope) || Closures.TryGetValue(v, out known)) continue;
            Closures.Add(v, scope);
        }
        foreach (var use in tracker.Uses)
        {
            IField seen;
            if (use.Key.Kind == VariableKind.Local && !Fields.TryGetValue(use.Key, out seen)) Fields.Add(use.Key, use.Value);
        }
    }
}

internal sealed class HoistCapturedDeclarations : IAstTransform
{
    private sealed class Declared
    {
        public ILVariable Variable;
        public ClosureScope Closure; // null: not a variable of a tracked closure
        public AstNode Node;         // VariableDeclarationStatement or OutVarDeclarationExpression; null: cannot move
        public AstNode Scope;
        public string Name;
    }

    // What the compiler would make closures of: per scope, the captured variables it declares;
    // per variable, the lambdas that capture it.
    private readonly Dictionary<AstNode, HashSet<object>> env = new Dictionary<AstNode, HashSet<object>>();
    private readonly Dictionary<object, HashSet<AstNode>> capturedBy = new Dictionary<object, HashSet<AstNode>>();

    public void Run(AstNode rootNode, TransformContext context)
    {
        env.Clear();
        capturedBy.Clear();
        firstCapture.Clear();
        var declared = new Dictionary<ILVariable, Declared>();
        foreach (AstNode n in rootNode.Descendants)
        {
            var vi = n as VariableInitializer;
            var ov = n as OutVarDeclarationExpression;
            if (vi == null && ov == null) continue;
            ILVariable v = VariableOf(vi != null ? (AstNode)vi : ov.Variable);
            if (v == null || declared.ContainsKey(v)) continue;
            var d = new Declared { Variable = v, Name = vi != null ? vi.Name : ov.Variable.Name };
            TrackClosures.Closures.TryGetValue(v, out d.Closure);
            if (ov != null) { d.Node = ov; d.Scope = ScopeOf(ov); }
            else
            {
                var vds = vi.Parent as VariableDeclarationStatement;
                if (vds == null) continue;
                if (vds.Parent is ForStatement) d.Scope = vds.Parent;
                else d.Scope = ScopeOf(vds);
                if (vds.Variables.Count == 1 && (vds.Parent is BlockStatement || vds.Parent is SwitchSection)) d.Node = vds;
            }
            if (d.Scope != null) declared[v] = d;
        }
        var closures = declared.Values.Where(d => d.Closure != null && d.Node != null).Select(d => d.Closure).Distinct().ToList();
        if (closures.Count == 0) return;
        var own = closures.ToList();
        foreach (ClosureScope c in own) foreach (ClosureScope l in c.Later) if (!closures.Contains(l)) closures.Add(l);
        foreach (ClosureScope c in own) foreach (ClosureScope l in c.Earlier) if (!closures.Contains(l)) closures.Add(l);

        // One pass: statements made from the instructions that follow a closure, and captures.
        var anchorBlocks = new Dictionary<ClosureScope, HashSet<AstNode>>();
        var first = new Dictionary<ClosureScope, KeyValuePair<int, AstNode>>();
        foreach (ClosureScope c in closures) anchorBlocks[c] = new HashSet<AstNode>();
        foreach (AstNode n in rootNode.Descendants)
        {
            foreach (ILInstruction inst in n.Annotations.OfType<ILInstruction>())
                foreach (ClosureScope c in closures)
                {
                    if (!c.Anchors.Contains(inst)) continue;
                    AstNode statement = StatementOf(n);
                    if (statement == null) continue;
                    anchorBlocks[c].Add(statement.Parent);
                    int rank = c.Following.FindIndex(set => set.Contains(inst));
                    KeyValuePair<int, AstNode> best;
                    if (!first.TryGetValue(c, out best) || rank < best.Key) first[c] = new KeyValuePair<int, AstNode>(rank, statement);
                }
            Capture(n, declared);
        }

        foreach (ClosureScope c in own)
        {
            // A closure created last before a 'switch': the scope of the sections.
            SwitchStatement sections = null;
            KeyValuePair<int, AstNode> start;
            if (first.TryGetValue(c, out start) && start.Value is SwitchStatement
                && !c.Later.Any(l => first.ContainsKey(l) && first[l].Value == start.Value)) sections = (SwitchStatement)start.Value;

            // A closure created last before a 'for': the loop variable, declared in the initializer.
            if (first.TryGetValue(c, out start) && start.Value is ForStatement
                && !c.Later.Any(l => first.ContainsKey(l) && first[l].Value == start.Value))
            {
                foreach (Declared d in declared.Values.Where(x => x.Closure == c && x.Node != null).ToList())
                    if (LoopVariable(d, (ForStatement)start.Value))
                    {
                        env[d.Scope].Remove(d.Variable);
                        d.Scope = start.Value;
                        d.Node = null;
                        Add(env, d.Scope, d.Variable);
                    }
            }

            foreach (Declared d in declared.Values.Where(x => x.Closure == c && x.Node != null).ToList())
            {
                AstNode target = null, at = null;
                if (sections != null)
                {
                    target = sections;
                    SwitchSection section = d.Node.Ancestors.OfType<SwitchSection>().FirstOrDefault(s => s.Parent == sections);
                    if (section != null) { at = d.Node; while (at.Parent != section) at = at.Parent; }
                    else if (d.Node is VariableDeclarationStatement && ((VariableDeclarationStatement)d.Node).Variables.Single().Initializer.IsNull)
                    {
                        section = sections.SwitchSections.FirstOrDefault();
                        at = section == null ? null : section.Statements.FirstOrDefault();
                    }
                    if (at == null) continue;
                }
                else
                {
                    // The outermost block around the declaration with a statement of the closure's scope.
                    for (AstNode a = d.Node.Parent; a != null; a = a.Parent)
                        if (a is BlockStatement && anchorBlocks[c].Contains(a)) target = a;
                    if (target == null) continue;
                    at = d.Node;
                    while (at.Parent != target) at = at.Parent;
                }
                if (d.Scope == target || at == d.Node) continue;
                if (MergesInto(d.Scope, target) || Collides(target, d)) continue;
                Split(d, at, context);
                env[d.Scope].Remove(d.Variable);
                d.Scope = target;
                Add(env, target, d.Variable);
            }
        }
        NestedScopes(own, first, declared);
        LoopVariablesByFieldOrder(declared);
    }

    // 'T i; for (i = e; ...)' -> 'for (T i = e; ...)' when the fields of the closure say so. A
    // loop variable is in a scope of its own: merged into the closure of the enclosing scope,
    // its field comes after those of that scope. Declared in the enclosing scope, the fields
    // are in the order the lambdas meet the variables.
    private void LoopVariablesByFieldOrder(Dictionary<ILVariable, Declared> declared)
    {
        foreach (Declared d in declared.Values.Where(x => x.Node is VariableDeclarationStatement).ToList())
        {
            int rank, row = FieldRow(d.Variable);
            if (row < 0 || !firstCapture.TryGetValue(d.Variable, out rank) || d.Node.Parent == null) continue;
            ForStatement loop = d.Node.Parent.Children.OfType<ForStatement>().FirstOrDefault(f => f.Initializers.Count == 1
                && f.Initializers.Single() is ExpressionStatement && ((ExpressionStatement)f.Initializers.Single()).Expression is AssignmentExpression
                && VariableOf(((AssignmentExpression)((ExpressionStatement)f.Initializers.Single()).Expression).Left) == d.Variable);
            if (loop == null) continue;
            var others = declared.Values.Where(x => x != d && ClosureType(x.Variable) == ClosureType(d.Variable) && x.Scope == d.Scope && firstCapture.ContainsKey(x.Variable)).ToList();
            if (others.Count == 0)
            {
                // Alone in its closure class while the scope has other captured variables, in
                // other classes: a scope of its own (struct closures, which nothing creates).
                var beside = declared.Values.Where(x => x != d && x.Scope == d.Scope && FieldRow(x.Variable) >= 0).ToList();
                if (d.Closure == null && beside.Count > 0 && beside.All(x => ClosureType(x.Variable) != ClosureType(d.Variable)))
                {
                    AstNode outer = d.Scope;
                    if (LoopVariable(d, loop))
                    {
                        env[outer].Remove(d.Variable);
                        d.Scope = loop;
                        d.Node = null;
                        Add(env, loop, d.Variable);
                    }
                }
                continue;
            }
            if (others.Any(x => FieldRow(x.Variable) < 0 || FieldRow(x.Variable) > row)) continue;
            if (!others.Any(x => firstCapture[x.Variable] > rank)) continue;
            // Merged only when the same lambdas capture the loop variable and the enclosing scope.
            HashSet<AstNode> mine, rest = new HashSet<AstNode>();
            HashSet<object> variables;
            if (!capturedBy.TryGetValue(d.Variable, out mine) || !env.TryGetValue(d.Scope, out variables)) continue;
            foreach (object o in variables) { HashSet<AstNode> by; if (!ReferenceEquals(o, d.Variable) && capturedBy.TryGetValue(o, out by)) rest.UnionWith(by); }
            if (!rest.SetEquals(mine)) continue;
            AstNode scope = d.Scope;
            if (!LoopVariable(d, loop)) continue;
            env[scope].Remove(d.Variable);
            d.Scope = loop;
            d.Node = null;
            Add(env, loop, d.Variable);
        }
    }

    private static ITypeDefinition ClosureType(ILVariable v)
    {
        IField field;
        return TrackClosures.Fields.TryGetValue(v, out field) ? field.DeclaringTypeDefinition : null;
    }

    // A closure created in the middle of a block, after another one of the same block: the
    // compiler creates a closure where its scope starts, so a scope starts there. Nothing
    // says which statement it was ('unchecked', a constant condition...): bare braces from
    // there to the end of the block give the same code.
    private void NestedScopes(List<ClosureScope> own, Dictionary<ClosureScope, KeyValuePair<int, AstNode>> first, Dictionary<ILVariable, Declared> declared)
    {
        foreach (ClosureScope c in own.OrderBy(x => x.Index))
        {
            KeyValuePair<int, AstNode> start;
            if (c.Earlier.Count == 0 || !first.TryGetValue(c, out start)) continue;
            var at = start.Value as Statement;
            var outer = at == null ? null : at.Parent as BlockStatement;
            if (outer == null || at.PrevSibling == null || !(at.PrevSibling is Statement)) continue;
            if (at is ForStatement || at is ForeachStatement || at is SwitchStatement || at is UsingStatement || at is TryCatchStatement
                || at is WhileStatement || at is DoWhileStatement || at is LockStatement || at is FixedStatement || at is LabelStatement) continue;
            var mine = declared.Values.Where(x => x.Closure == c).ToList();
            if (mine.Count == 0 || mine.Any(x => x.Scope != outer || x.Node == null)) continue;
            // An earlier closure of the same block that the source keeps in this scope.
            if (!c.Earlier.Any(e => first.ContainsKey(e) && first[e].Value.Parent == outer && first[e].Value != at)) continue;
            var moved = new List<Statement>();
            for (AstNode n = at; n != null; n = n.NextSibling) if (n is Statement) moved.Add((Statement)n);
            if (moved.Any(s => s is LabelStatement || s is LocalFunctionDeclarationStatement)) continue;
            // Declarations of its variables that stand before: they come along when nothing before uses them.
            var early = new List<Statement>();
            bool movable = true;
            foreach (Declared d in mine)
            {
                if (d.Node.Ancestors.Any(a => moved.Contains(a)) || moved.Contains(d.Node)) continue;
                var vds = d.Node as VariableDeclarationStatement;
                if (vds == null || vds.Parent != outer || !vds.Variables.Single().Initializer.IsNull) { movable = false; break; }
                for (AstNode n = vds.NextSibling; n != null && n != at; n = n.NextSibling)
                    if (n.DescendantsAndSelf.Any(x => x is IdentifierExpression && VariableOf(x) == d.Variable)) movable = false;
                early.Add(vds);
            }
            if (!movable) continue;
            var block = new BlockStatement();
            foreach (Statement s in early) { s.Remove(); block.Statements.Add(s); }
            foreach (Statement s in moved) { s.Remove(); block.Statements.Add(s); }
            outer.Statements.Add(block);
            foreach (Declared d in declared.Values.Where(x => x.Scope == outer && x.Node != null && x.Node.Ancestors.Contains(block)).ToList())
            {
                env[outer].Remove(d.Variable);
                d.Scope = block;
                Add(env, block, d.Variable);
            }
        }
    }

    private static int FieldRow(ILVariable v)
    {
        IField field;
        if (!TrackClosures.Fields.TryGetValue(v, out field) || field.MetadataToken.IsNil) return -1;
        return System.Reflection.Metadata.Ecma335.MetadataTokens.GetRowNumber(field.MetadataToken);
    }

    private readonly Dictionary<object, int> firstCapture = new Dictionary<object, int>();

    // ---- what the compiler will do with the declarations as they are

    private void Capture(AstNode n, Dictionary<ILVariable, Declared> declared)
    {
        object key = null;
        AstNode scope = null;
        ILVariable v = n is IdentifierExpression ? VariableOf(n) : null;
        if (v != null && v.Kind != VariableKind.Parameter)
        {
            Declared d;
            if (!declared.TryGetValue(v, out d)) return;
            key = v; scope = d.Scope;
        }
        else if (v != null || n is ThisReferenceExpression || n is BaseReferenceExpression || ImplicitThis(n))
        {
            // 'this' and parameters: the body of their function. 'this' is the outermost one's.
            ILFunction function = v != null && !(v.Index < 0) ? v.Function : null;
            AstNode body = null;
            foreach (AstNode a in n.Ancestors)
            {
                if (IsLambda(a) && function != null && FunctionOf(a) == function) { body = BodyOf(a); break; }
                if (a is EntityDeclaration && !(a.Parent is LocalFunctionDeclarationStatement))
                {
                    if (function != null && function.Parent != null) return; // a lambda's parameter, lambda not found
                    body = a.GetChildByRole(Roles.Body);
                    break;
                }
            }
            if (body == null || body.IsNull) return;
            scope = body;
            key = v != null && function != null ? (object)v : body;
        }
        else return;

        List<AstNode> lambdas = null;
        AstNode p = n;
        for (; p != null && p != scope; p = p.Parent)
            if (IsLambda(p)) { if (lambdas == null) lambdas = new List<AstNode>(); lambdas.Add(p); }
        if (p == null || lambdas == null) return;
        Add(env, scope, key);
        if (!firstCapture.ContainsKey(key)) firstCapture[key] = firstCapture.Count;
        HashSet<AstNode> by;
        if (!capturedBy.TryGetValue(key, out by)) capturedBy[key] = by = new HashSet<AstNode>();
        by.UnionWith(lambdas);
    }

    private static bool ImplicitThis(AstNode n)
    {
        if (!(n is IdentifierExpression) && !(n is InvocationExpression)) return false;
        foreach (object a in n.Annotations)
        {
            var member = a as MemberResolveResult;
            if (member != null && member.Member != null && !member.Member.IsStatic && IsThis(member.TargetResult)) return true;
            var group = a as ICSharpCode.Decompiler.CSharp.Resolver.MethodGroupResolveResult;
            if (group != null && IsThis(group.TargetResult)) return true;
        }
        return false;
    }

    private static bool IsThis(ResolveResult target)
    {
        if (target is ThisResolveResult) return true;
        var local = target as ILVariableResolveResult;
        return local != null && local.Variable.Kind == VariableKind.Parameter && local.Variable.Index < 0;
    }

    private HashSet<AstNode> Capturing(AstNode scope)
    {
        var all = new HashSet<AstNode>();
        HashSet<object> variables;
        if (!env.TryGetValue(scope, out variables)) return all;
        foreach (object v in variables) { HashSet<AstNode> by; if (capturedBy.TryGetValue(v, out by)) all.UnionWith(by); }
        return all;
    }

    // MergeEnvironments: from the scope outwards, past scopes without captured variables, as
    // long as the same lambdas capture both. Not out of a lambda, not out of a loop.
    private bool MergesInto(AstNode scope, AstNode target)
    {
        HashSet<AstNode> mine = Capturing(scope);
        AstNode best = scope;
        for (AstNode n = scope.Parent; n != null; n = n.Parent)
        {
            if (IsLambda(n) || n is WhileStatement || n is DoWhileStatement || n is ForStatement || n is ForeachStatement) return true; // cannot move there either
            if (!(n is BlockStatement) && !(n is SwitchStatement)) continue;
            HashSet<object> variables;
            if (env.TryGetValue(n, out variables) && variables.Count > 0)
            {
                if (!Capturing(n).SetEquals(mine)) break;
                best = n;
            }
            if (n == target) break;
        }
        return best == target;
    }

    // ---- syntax

    private static void Add(Dictionary<AstNode, HashSet<object>> env, AstNode scope, object variable)
    {
        HashSet<object> set;
        if (!env.TryGetValue(scope, out set)) env[scope] = set = new HashSet<object>();
        set.Add(variable);
    }

    private static bool IsLambda(AstNode n)
    {
        return n is AnonymousMethodExpression || n is LambdaExpression || n is LocalFunctionDeclarationStatement;
    }

    private static ILFunction FunctionOf(AstNode lambda)
    {
        ILFunction f = lambda.Annotation<ILFunction>();
        if (f == null && lambda is LocalFunctionDeclarationStatement) f = ((LocalFunctionDeclarationStatement)lambda).Declaration.Annotation<ILFunction>();
        return f;
    }

    private static AstNode BodyOf(AstNode lambda)
    {
        if (lambda is LambdaExpression) return ((LambdaExpression)lambda).Body;
        if (lambda is LocalFunctionDeclarationStatement) return ((LocalFunctionDeclarationStatement)lambda).Declaration.Body;
        return lambda.GetChildByRole(Roles.Body);
    }

    private static ILVariable VariableOf(AstNode node)
    {
        var rr = node.Annotation<ILVariableResolveResult>();
        return rr == null ? null : rr.Variable;
    }

    // The statement of a block or of a switch section that holds the node.
    private static AstNode StatementOf(AstNode n)
    {
        for (; n != null && n.Parent != null; n = n.Parent)
            if (n is Statement && (n.Parent is BlockStatement || n.Parent is SwitchSection)) return n;
        return null;
    }

    // Statements of a section are in the scope of the switch.
    private static AstNode ScopeOf(AstNode n)
    {
        AstNode statement = StatementOf(n);
        if (statement == null) return null;
        return statement.Parent is SwitchSection ? statement.Parent.Parent : statement.Parent;
    }

    // Another variable of that name somewhere in the wider scope.
    private static bool Collides(AstNode target, Declared d)
    {
        foreach (AstNode n in target.Descendants)
        {
            var id = n as IdentifierExpression;
            if (id != null && id.Identifier == d.Name && VariableOf(id) != d.Variable && VariableOf(id) != null) return true;
            var vi = n as VariableInitializer;
            if (vi != null && vi.Name == d.Name && VariableOf(vi) != d.Variable) return true;
            var p = n as ParameterDeclaration;
            if (p != null && p.Name == d.Name) return true;
            var fe = n as ForeachStatement;
            if (fe != null && fe.VariableDesignation is SingleVariableDesignation && ((SingleVariableDesignation)fe.VariableDesignation).Identifier == d.Name) return true;
            var cc = n as CatchClause;
            if (cc != null && cc.VariableName == d.Name) return true;
        }
        return false;
    }

    // 'T x; for (x = e; ...)' -> 'for (T x = e; ...)' when nothing outside the loop uses x.
    private static bool LoopVariable(Declared d, ForStatement loop)
    {
        var vds = d.Node as VariableDeclarationStatement;
        if (vds == null || vds.Parent != loop.Parent || !vds.Variables.Single().Initializer.IsNull || loop.Initializers.Count != 1) return false;
        var init = loop.Initializers.Single() as ExpressionStatement;
        var assign = init == null ? null : init.Expression as AssignmentExpression;
        if (assign == null || assign.Operator != AssignmentOperatorType.Assign || VariableOf(assign.Left) != d.Variable) return false;
        foreach (AstNode n in loop.Parent.Descendants)
            if (n is IdentifierExpression && VariableOf(n) == d.Variable && !n.Ancestors.Contains(loop)) return false;
        var declaration = new VariableDeclarationStatement(vds.Type.Clone(), d.Name, assign.Right.Detach());
        declaration.Variables.Single().AddAnnotation(new ILVariableResolveResult(d.Variable, d.Variable.Type));
        foreach (object a in assign.Annotations) if (!(a is ResolveResult)) declaration.Variables.Single().AddAnnotation(a);
        init.ReplaceWith(declaration);
        vds.Remove();
        return true;
    }

    // 'T x = e;' -> 'T x;' before the statement 'at', and 'x = e;' where the declaration was.
    private static void Split(Declared d, AstNode at, TransformContext context)
    {
        var rr = new ILVariableResolveResult(d.Variable, d.Variable.Type);
        AstType type = context.TypeSystemAstBuilder.ConvertType(d.Variable.Type);
        var declaration = new VariableDeclarationStatement(type, d.Name);
        declaration.Variables.Single().AddAnnotation(rr);
        at.Parent.InsertChildBefore(at, declaration, (ICSharpCode.Decompiler.CSharp.Syntax.Role<Statement>)at.Role);

        var vds = d.Node as VariableDeclarationStatement;
        if (vds != null)
        {
            VariableInitializer vi = vds.Variables.Single();
            if (vi.Initializer.IsNull) vds.Remove();
            else
            {
                Expression value = vi.Initializer.Detach();
                var left = new IdentifierExpression(d.Name);
                left.AddAnnotation(rr);
                var assign = new AssignmentExpression(left, value);
                foreach (object a in vi.Annotations) if (!(a is ResolveResult)) assign.AddAnnotation(a);
                vds.ReplaceWith(new ExpressionStatement(assign));
            }
        }
        else
        {
            var left = new IdentifierExpression(d.Name);
            left.AddAnnotation(rr);
            var direction = new DirectionExpression(FieldDirection.Out, left);
            direction.CopyAnnotationsFrom(d.Node);
            d.Node.ReplaceWith(direction);
        }
        d.Node = declaration;
    }
}
