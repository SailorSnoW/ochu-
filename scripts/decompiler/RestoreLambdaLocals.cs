// Two places where the decompiler declares in a method what belongs to a lambda or to the
// class, which recompiles to one more closure class (or one more field) than the IL has.
//
// 1. A local of an expression-bodied lambda. There is no statement in the lambda to declare
//    it, so the decompiler declares it in the enclosing method:
//
//        int snap;
//        report = new Report(() => !IsSnapped(time, out snap));
//
//    and 'snap' becomes a captured variable. The variable belongs to the lambda's function
//    in the IL; when its first use is an 'out' argument it is declared there:
//    '() => !IsSnapped(time, out int snap)'.
//
// 2. 'this' captured in a constructor together with a parameter. The closure is created
//    before the base constructor call and 'this' is stored in it after: the decompiler does
//    not see the usual prologue and writes 'T copy = this;', a second captured variable in
//    another scope. The copy is replaced with 'this'.
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;

internal sealed class RestoreLambdaLocals : IAstTransform
{
    public void Run(AstNode rootNode, TransformContext context)
    {
        var uses = new Dictionary<ILVariable, List<IdentifierExpression>>();
        var declarations = new List<VariableDeclarationStatement>();
        foreach (AstNode n in rootNode.Descendants)
        {
            var id = n as IdentifierExpression;
            if (id != null)
            {
                ILVariable v = VariableOf(id);
                if (v == null) continue;
                List<IdentifierExpression> l;
                if (!uses.TryGetValue(v, out l)) uses[v] = l = new List<IdentifierExpression>();
                l.Add(id);
            }
            var vds = n as VariableDeclarationStatement;
            if (vds != null && vds.Variables.Count == 1 && vds.Parent is BlockStatement) declarations.Add(vds);
        }
        foreach (VariableDeclarationStatement vds in declarations)
        {
            VariableInitializer vi = vds.Variables.Single();
            ILVariable v = VariableOf(vi);
            List<IdentifierExpression> l;
            if (v == null || !uses.TryGetValue(v, out l)) continue;
            if (vi.Initializer.IsNull) OutVariable(vds, v, l);
            else if (vi.Initializer is ThisReferenceExpression) ThisCopy(vds, v, l);
        }
    }

    private static void OutVariable(VariableDeclarationStatement vds, ILVariable v, List<IdentifierExpression> uses)
    {
        if (v.Function == null || v.Function.Parent == null) return; // a local of the method itself
        var direction = uses[0].Parent as DirectionExpression;
        if (direction == null || direction.FieldDirection != FieldDirection.Out) return;
        LambdaExpression lambda = null;
        foreach (AstNode a in uses[0].Ancestors)
        {
            if (a == vds.Parent) break;
            if (a is LambdaExpression && a.Annotation<ILFunction>() == v.Function) { lambda = (LambdaExpression)a; break; }
        }
        if (lambda == null || !(lambda.Body is Expression)) return;
        foreach (IdentifierExpression use in uses) if (!use.Ancestors.Contains(lambda)) return;
        var declared = new OutVarDeclarationExpression(vds.Type.Clone(), vds.Variables.Single().Name);
        declared.Variable.AddAnnotation(new ILVariableResolveResult(v, v.Type));
        declared.CopyAnnotationsFrom(direction);
        direction.ReplaceWith(declared);
        vds.Remove();
    }

    private static void ThisCopy(VariableDeclarationStatement vds, ILVariable v, List<IdentifierExpression> uses)
    {
        ClosureScope closure;
        if (!(vds.Parent.Parent is ConstructorDeclaration) || !v.IsSingleDefinition) return;
        if (!TrackClosures.Closures.TryGetValue(v, out closure)) return;
        foreach (IdentifierExpression use in uses)
        {
            var assigned = use.Parent as AssignmentExpression;
            if (assigned != null && assigned.Left == use) return;
            if (use.Parent is DirectionExpression) return;
        }
        Expression self = vds.Variables.Single().Initializer;
        foreach (IdentifierExpression use in uses) use.ReplaceWith(self.Clone());
        vds.Remove();
    }

    private static ILVariable VariableOf(AstNode node)
    {
        var rr = node.Annotation<ILVariableResolveResult>();
        return rr == null ? null : rr.Variable;
    }
}
