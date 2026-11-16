// C# transform for the types of a unit compiled with /debug+ (docs/units.tsv).
//
// There every local declared in the source has a slot, and what the compiler adds has its own:
//
//     foreach (T x in e)                 IL: e; stloc tmp; ...             (the loop's copy alone)
//     T[] a = e;  foreach (T x in a)     IL: e; stloc a; ldloc a; stloc tmp; ...
//
// The decompiler prints the first as 'T[] array = e; foreach (T x in array)': it names the
// loop's own copy. A variable declared right before a 'foreach' and read by nothing but the
// loop is that copy: its value goes back into the loop.
using System.Linq;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreUnitSyntax : IAstTransform
{
    public void Run(AstNode rootNode, TransformContext context)
    {
        if (Program.Units.Count == 0) return;
        foreach (var loop in rootNode.Descendants.OfType<ForeachStatement>().ToList())
        {
            var member = loop.Ancestors.OfType<EntityDeclaration>().FirstOrDefault();
            var method = member == null ? null : member.GetSymbol() as IMethod;
            if (method == null || !Program.KeepsLocals(method)) continue;
            var use = loop.InExpression as IdentifierExpression;
            var declaration = loop.GetPrevSibling(n => n is Statement) as VariableDeclarationStatement;
            if (use == null || declaration == null || declaration.Variables.Count != 1) continue;
            VariableInitializer variable = declaration.Variables.Single();
            if (variable.Name != use.Identifier || variable.Initializer.IsNull) continue;
            if (member.Descendants.OfType<IdentifierExpression>().Count(i => i.Identifier == use.Identifier) != 1) continue;
            Expression value = variable.Initializer;
            value.Remove();
            use.ReplaceWith(value);
            declaration.Remove();
        }
    }
}
