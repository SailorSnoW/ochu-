// C# transform: 'return;' at the end of the last 'if' of a void method.
//
//     ...  if (c) { A; return; }             IL: c; brfalse END; A; ret; END: ret
// The method ends on 'ret; ret' and a conditional branch jumps to the last one: the first is
// a statement of the source. The decompiler drops it (falling off the end of the 'if' leaves
// the method as well), and the recompiled method ends on one 'ret'. Inside a loop or a switch
// the decompiler prints 'break;', which RestoreEarlyReturn gives back as 'return;' in the IL
// tree; a 'return;' that ends an 'if' has nothing left there to carry it, hence the syntax.
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreTrailingReturn : IAstTransform
{
    public void Run(AstNode rootNode, TransformContext context)
    {
        if (!Program.With.Contains("RestoreEarlyReturn")) return;
        foreach (var declaration in rootNode.Descendants.OfType<MethodDeclaration>().ToList())
        {
            var method = declaration.GetSymbol() as IMethod;
            if (method == null || declaration.Body.IsNull || method.ReturnType.Kind != TypeKind.Void) continue;
            // The last statement, through 'else' branches, is an 'if' without 'else' whose
            // body ends on a plain statement.
            Statement last = declaration.Body.Statements.LastOrDefault();
            IfElseStatement tail = null;
            while (last is IfElseStatement)
            {
                tail = (IfElseStatement)last;
                if (tail.FalseStatement.IsNull) break;
                last = tail.FalseStatement is BlockStatement ? ((BlockStatement)tail.FalseStatement).Statements.LastOrDefault() : tail.FalseStatement;
                tail = null;
            }
            if (tail == null || !tail.FalseStatement.IsNull) continue;
            var body = tail.TrueStatement as BlockStatement;
            if (body == null || !(body.Statements.LastOrDefault() is ExpressionStatement)) continue;
            RawIL raw = RawIL.Read(context.TypeSystem, method);
            if (raw == null || raw.Ops.Count < 3) continue;
            RawIL.Op end = raw.Ops[raw.Ops.Count - 1], before = raw.Ops[raw.Ops.Count - 2];
            if (end.Code != ILOpCode.Ret || before.Code != ILOpCode.Ret || !raw.CondTargets.Contains(end.Offset) || raw.Targets.Contains(before.Offset)) continue;
            // No protected block: there 'return;' is a 'leave'.
            if (raw.RegionEnds.Count > 0 && raw.RegionEnds.Max() >= before.Offset) continue;
            body.Statements.Add(new ReturnStatement());
        }
    }
}
