// C# transform: sections of a 'while (true) switch' that stand before the 'switch' in the IL.
//
//     while (true) switch (mode) {                 IL: br DISPATCH
//         case 0: A; if (c) { B; break; }              TAIL: T; ONE: C; ...; br DISPATCH
//                 T; goto case 1;                      THREE: D; ...
//         case 1: C; break;                            DISPATCH: mode; switch (ZERO, ONE, TWO, THREE)
//         case 2: E; goto case 3;                      ZERO: A; c; brfalse TAIL; B; br DISPATCH
//         case 3: D; break; }                          TWO: E; br THREE
//
// Three methods of the inflater of Ionic.Zlib have this layout: every section a 'goto case'
// jumps to stands before the 'switch', with the end of a section that falls into one of them,
// and the method starts with a jump over them. No compiler tried gives it from the 'switch'
// (the dispatch always comes first); written with labels the pinned compiler gives it back
// exactly, down to a 'while' at the head of such a section that has lost the jump to its
// condition (nothing falls into it any more, and the table goes straight to the condition):
//
//     goto dispatch; tail: T; one: C; goto dispatch; three: D; goto dispatch;
//     dispatch: while (true) switch (mode) { case 0: A; if (c) { B; break; } goto tail;
//         case 1: goto one; case 2: E; goto three; case 3: goto three; }
//
// Decided by the raw body: a 'switch' with targets before it, and a 'br' to its dispatch
// before all of them. The statements of a section whose code stands between the two move.
//
// Second form, in the loops of the same methods: an 'else' that leaves, in the middle.
//
//     while (k < j) {                               IL: n; brfalse ELSE; r = 0; br REST
//         if (n != 0) { r = 0; }                        ELSE: X; ret
//         else { X; return f(r); }                      REST: n--; S
//         n--; S; }
//
// is printed 'if (n != 0) { r = 0; n--; S; continue; } X; return f(r);', which puts X after
// S. Decided by the raw body: the code of X stands between two statements of the 'if', right
// after an unconditional jump to the second.
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.TypeSystem;
using SwitchSection = ICSharpCode.Decompiler.CSharp.Syntax.SwitchSection;

internal sealed class RestoreSwitchLayout : IAstTransform
{
    private sealed class Chunk
    {
        public SwitchSection Section;
        public List<Statement> Statements = new List<Statement>();
        public bool Whole;
        public int Offset;
        public string Label;
    }

    public void Run(AstNode rootNode, TransformContext context)
    {
        foreach (var declaration in rootNode.Descendants.OfType<MethodDeclaration>().ToList())
        {
            var method = declaration.GetSymbol() as IMethod;
            if (method == null || declaration.Body.IsNull) continue;
            RawIL raw = null;
            foreach (var test in declaration.Body.Descendants.OfType<IfElseStatement>().ToList())
            {
                if (!MayHaveElseInside(test)) continue;
                if (raw == null) raw = RawIL.Read(context.TypeSystem, method);
                if (raw == null) break;
                ElseInside(test, raw);
            }
            foreach (var loop in declaration.Body.Statements.OfType<WhileStatement>().ToList())
            {
                var condition = loop.Condition as PrimitiveExpression;
                var body = loop.EmbeddedStatement as BlockStatement;
                if (condition == null || !(condition.Value is bool) || !(bool)condition.Value || body == null) continue;
                var sw = body.Statements.LastOrDefault() as SwitchStatement;
                if (sw == null || body.Statements.Any(s => s != sw && !(s is VariableDeclarationStatement))) continue;
                if (raw == null) raw = RawIL.Read(context.TypeSystem, method);
                if (raw == null) break;
                Hoist(declaration.Body, loop, sw, raw);
            }
        }
    }

    private static void Hoist(BlockStatement methodBody, WhileStatement loop, SwitchStatement sw, RawIL raw)
    {
        // The 'switch' of the raw body, its dispatch, and the jump over what stands before it.
        int at = FirstOffset(sw.Expression, 0);
        if (at < 0) return;
        int dispatch = -1, start = -1;
        foreach (var entry in raw.Switches)
        {
            if (entry.Key < at || entry.Key > at + 16) continue;
            int[] back = entry.Value.Where(t => t < entry.Key).ToArray();
            if (back.Length == 0) continue;
            foreach (RawIL.Op op in raw.Ops)
            {
                if (op.Offset >= back.Min()) break;
                if ((op.Code == ILOpCode.Br || op.Code == ILOpCode.Br_s) && op.Operand <= at && op.Operand > at - 4 && op.Operand > back.Max()) { dispatch = op.Operand; start = op.End; break; }
            }
        }
        if (dispatch < 0) return;

        // The end of each section that stands in between.
        var chunks = new List<Chunk>();
        foreach (SwitchSection section in sw.SwitchSections)
        {
            List<Statement> statements = StatementsOf(section);
            int first = statements.Count;
            bool any = false;
            for (int i = statements.Count - 1; i >= 0; i--)
            {
                int offset = FirstOffset(statements[i], start);
                if (offset >= dispatch) break;
                if (offset >= 0) any = true;
                else if (!(statements[i] is BreakStatement || statements[i] is GotoCaseStatement || statements[i] is ContinueStatement || statements[i] is ReturnStatement)) break;
                first = i;
            }
            if (!any) continue;
            // Statements without code at the head of the moved part stay with what precedes them.
            while (first < statements.Count && FirstOffset(statements[first], start) < 0) first++;
            var chunk = new Chunk { Section = section, Whole = first == 0 };
            for (int i = first; i < statements.Count; i++) chunk.Statements.Add(statements[i]);
            chunk.Offset = chunk.Statements.Select(s => FirstOffset(s, start)).Where(o => o >= 0).Min();
            chunk.Label = "hoisted_" + chunk.Offset.ToString("x4");
            chunks.Add(chunk);
        }
        if (chunks.Count == 0) return;
        chunks.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        string dispatchLabel = "dispatch_" + dispatch.ToString("x4");

        // Where each 'case' is, once moved.
        var labelOf = new Dictionary<string, string>();
        foreach (Chunk chunk in chunks)
            if (chunk.Whole)
                foreach (CaseLabel label in chunk.Section.CaseLabels)
                    if (!label.Expression.IsNull) labelOf[label.Expression.ToString()] = chunk.Label;
        var moved = new HashSet<AstNode>(chunks.SelectMany(c => c.Statements));

        // Jumps of the moved statements that are bound to the 'switch' or to the loop: all of
        // them must have somewhere to go from outside the 'switch'.
        var rewrites = new List<KeyValuePair<Statement, string>>();
        foreach (SwitchSection section in sw.SwitchSections)
            foreach (Statement top in StatementsOf(section))
                foreach (AstNode node in top.DescendantsAndSelf.ToList())
                {
                    bool inside = moved.Contains(top);
                    if (node is BreakStatement && Bound(node, top, true, true))
                    {
                        if (inside) rewrites.Add(new KeyValuePair<Statement, string>((Statement)node, dispatchLabel));
                    }
                    else if (node is ContinueStatement && Bound(node, top, true, false))
                    {
                        if (inside) rewrites.Add(new KeyValuePair<Statement, string>((Statement)node, dispatchLabel));
                    }
                    else if (node is GotoCaseStatement && Bound(node, top, false, true))
                    {
                        string target;
                        if (labelOf.TryGetValue(((GotoCaseStatement)node).LabelExpression.ToString(), out target)) rewrites.Add(new KeyValuePair<Statement, string>((Statement)node, target));
                        else if (inside) return;
                    }
                    else if (node is GotoDefaultStatement && Bound(node, top, false, true))
                    {
                        if (inside) return;
                    }
                }

        // Variables the moved statements use and that are declared in the loop: declared
        // before it (a declared variable never shares its slot, wherever it is declared in
        // the method block).
        var used = new HashSet<ILVariable>();
        var usedNames = new HashSet<string>();
        foreach (Statement s in moved)
            foreach (var id in s.DescendantsAndSelf.OfType<IdentifierExpression>())
            {
                var rr = id.GetResolveResult() as ILVariableResolveResult;
                if (rr != null) used.Add(rr.Variable);
                usedNames.Add(id.Identifier);
            }
        var declarations = new List<VariableDeclarationStatement>();
        foreach (var declared in loop.Descendants.OfType<VariableDeclarationStatement>().ToList())
        {
            if (declared.AncestorsAndSelf.Any(a => moved.Contains(a))) continue;
            ILVariable v = null;
            foreach (VariableInitializer init in declared.Variables)
                foreach (var rr in init.Annotations.OfType<ILVariableResolveResult>()) v = rr.Variable;
            if (v != null ? !used.Contains(v) : !declared.Variables.Any(i => usedNames.Contains(i.Name))) continue;
            if (declared.Variables.Count != 1) return;
            declarations.Add(declared);
        }

        // From here on the tree changes.
        foreach (VariableDeclarationStatement declared in declarations)
        {
            VariableInitializer init = declared.Variables.Single();
            var hoisted = new VariableDeclarationStatement(declared.Type.Clone(), init.Name);
            if (init.Initializer.IsNull) declared.Remove();
            else declared.ReplaceWith(new ExpressionStatement(new AssignmentExpression(new IdentifierExpression(init.Name), init.Initializer.Detach())));
            methodBody.Statements.InsertBefore(loop, hoisted);
        }
        foreach (var rewrite in rewrites)
        {
            var jump = new GotoStatement(rewrite.Value);
            // A jump that is itself a moved statement: the new one moves in its place.
            foreach (Chunk chunk in chunks)
            {
                int i = chunk.Statements.IndexOf(rewrite.Key);
                if (i >= 0) chunk.Statements[i] = jump;
            }
            rewrite.Key.ReplaceWith(jump);
        }
        methodBody.Statements.InsertBefore(loop, new GotoStatement(dispatchLabel));
        foreach (Chunk chunk in chunks)
        {
            methodBody.Statements.InsertBefore(loop, new LabelStatement { Label = chunk.Label });
            var block = chunk.Section.Statements.Count == 1 ? chunk.Section.Statements.First() as BlockStatement : null;
            if (chunk.Whole && block != null)
            {
                // A section with declarations of its own keeps its block.
                block.Remove();
                methodBody.Statements.InsertBefore(loop, block);
                chunk.Section.Statements.Add(new GotoStatement(chunk.Label));
                continue;
            }
            foreach (Statement s in chunk.Statements)
            {
                s.Remove();
                methodBody.Statements.InsertBefore(loop, s);
            }
            if (block != null) block.Statements.Add(new GotoStatement(chunk.Label));
            else chunk.Section.Statements.Add(new GotoStatement(chunk.Label));
        }
        methodBody.Statements.InsertBefore(loop, new LabelStatement { Label = dispatchLabel });
    }

    private static bool IsLoop(AstNode n)
    {
        return n is ForStatement || n is ForeachStatement || n is WhileStatement || n is DoWhileStatement;
    }

    // 'if (c) { ...; continue; }' without 'else' in the block of a loop, before statements
    // that leave the method.
    private static bool MayHaveElseInside(IfElseStatement test)
    {
        var then = test.TrueStatement as BlockStatement;
        var parent = test.Parent as BlockStatement;
        if (!test.FalseStatement.IsNull || then == null || parent == null || !IsLoop(parent.Parent)) return false;
        if (then.Statements.Count < 3 || !(then.Statements.Last() is ContinueStatement)) return false;
        Statement last = parent.Statements.Last();
        return last != test && (last is ReturnStatement || last is ThrowStatement);
    }

    private static void ElseInside(IfElseStatement test, RawIL raw)
    {
        var then = (BlockStatement)test.TrueStatement;
        var parent = (BlockStatement)test.Parent;
        List<Statement> body = then.Statements.ToList();
        List<Statement> rest = parent.Statements.SkipWhile(s => s != test).Skip(1).ToList();
        int restAt = rest.Select(s => FirstOffset(s, 0)).Where(o => o >= 0).DefaultIfEmpty(-1).Min();
        int index;
        if (restAt < 0 || !raw.IndexAt.TryGetValue(restAt, out index) || index == 0) return;
        // What the 'if' keeps: the statements before the code of the rest.
        int keep = 0;
        while (keep < body.Count - 1)
        {
            int offset = FirstOffset(body[keep], 0);
            if (offset < 0 || offset > restAt) break;
            keep++;
        }
        if (keep == 0 || keep == body.Count - 1) return;
        int next = FirstOffset(body[keep], 0);
        if (next < 0) return;
        for (int i = keep; i < body.Count - 1; i++)
        {
            int offset = FirstOffset(body[i], 0);
            if (offset >= 0 && offset < restAt) return;
        }
        // The jump over the rest, from the last statement kept to the first one moved.
        RawIL.Op jump = raw.Ops[index - 1];
        if ((jump.Code != ILOpCode.Br && jump.Code != ILOpCode.Br_s) || jump.Operand != next) return;
        // A variable declared in what is kept would no longer be in scope.
        for (int i = 0; i < keep; i++)
            if (body[i] is VariableDeclarationStatement) return;
        var otherwise = new BlockStatement();
        foreach (Statement s in rest) { s.Remove(); otherwise.Statements.Add(s); }
        test.FalseStatement = otherwise;
        body[body.Count - 1].Remove();
        for (int i = body.Count - 2; i >= keep; i--) { body[i].Remove(); parent.Statements.InsertAfter(test, body[i]); }
    }

    private static List<Statement> StatementsOf(SwitchSection section)
    {
        var block = section.Statements.Count == 1 ? section.Statements.First() as BlockStatement : null;
        return (block != null ? block.Statements : section.Statements).ToList();
    }

    // Whether a jump inside 'top' leaves it: no loop (or 'switch') of its own around it.
    private static bool Bound(AstNode jump, AstNode top, bool loops, bool switches)
    {
        if (jump == top) return true;
        for (AstNode p = jump.Parent; p != null; p = p.Parent)
        {
            if (loops && IsLoop(p)) return false;
            if (switches && p is SwitchStatement) return false;
            if (p is LambdaExpression || p is AnonymousMethodExpression || p is LocalFunctionDeclarationStatement) return false;
            if (p == top) break;
        }
        return true;
    }

    // Lowest offset of the code of a node at or after 'from', -1 when it has none.
    private static int FirstOffset(AstNode node, int from)
    {
        int min = -1;
        var stack = new Stack<ILInstruction>();
        foreach (AstNode n in node.DescendantsAndSelf)
            foreach (var inst in n.Annotations.OfType<ILInstruction>())
            {
                stack.Push(inst);
                while (stack.Count > 0)
                {
                    ILInstruction i = stack.Pop();
                    if (i is ILFunction) continue;
                    if (!(i is Leave) && !(i is Branch))
                        foreach (var r in i.ILRanges)
                            if (r.Start != r.End && r.Start >= from && (min < 0 || r.Start < min)) min = r.Start;
                    foreach (ILInstruction c in i.Children) stack.Push(c);
                }
            }
        return min;
    }
}
