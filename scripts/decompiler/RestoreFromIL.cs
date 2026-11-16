// Two smaller IL-faithful corrections, both decided by reading the raw method body.
//
// 1. Negation. The decompiler reads 'neg' as '0 - x' and prints it back as '-x' for
//    integers only. For float and double it prints '0f - x', which compiles to
//    'ldc.r4 0; sub'. MarkNegations finds the subtractions that were a 'neg' in the IL and
//    NegationToUnaryMinus prints them as '-x'.
//
// 2. Struct locals assigned field by field. For
//        BoundingBox result;  result.Min = a;  result.Max = b;
//    the IL takes the address of an unassigned local, so the decompiler declares it as
//    'BoundingBox result = default(BoundingBox);', which compiles to an extra
//    'ldloca; initobj'. When the IL has no 'initobj' for a struct local, the source did not
//    initialize it: DropStructInit clears the flag that makes the decompiler add one.
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RawBody
{
    public readonly HashSet<int> Negations = new HashSet<int>();     // offsets of 'neg'
    public readonly HashSet<int> InitObjLocals = new HashSet<int>(); // locals with 'ldloca n; initobj'

    public static RawBody Read(ILFunction f, ILTransformContext context)
    {
        if (f.Method == null || f.Method.MetadataToken.IsNil || f.Method.MetadataToken.Kind != HandleKind.MethodDefinition) return null;
        var module = context.PEFile;
        var def = module.Metadata.GetMethodDefinition((MethodDefinitionHandle)f.Method.MetadataToken);
        if (def.RelativeVirtualAddress == 0) return null;
        BlobReader reader = module.GetMethodBody(def.RelativeVirtualAddress).GetILReader();
        var body = new RawBody();
        int pendingLocal = -1;
        while (reader.RemainingBytes > 0)
        {
            int offset = reader.Offset;
            ILOpCode op = ILParser.DecodeOpCode(ref reader);
            int local = -1;
            if (op == ILOpCode.Ldloca_s) local = reader.ReadByte();
            else if (op == ILOpCode.Ldloca) local = reader.ReadUInt16();
            else
            {
                if (op == ILOpCode.Neg) body.Negations.Add(offset);
                if (op == ILOpCode.Initobj && pendingLocal >= 0) body.InitObjLocals.Add(pendingLocal);
                ILParser.SkipOperand(ref reader, op);
            }
            pendingLocal = local;
        }
        return body;
    }
}

internal sealed class RestoreFromIL : IILTransform
{
    internal static readonly ConditionalWeakTable<ILInstruction, object> Negations = new ConditionalWeakTable<ILInstruction, object>();
    private static readonly object Marker = new object();

    public void Run(ILFunction function, ILTransformContext context)
    {
        var functions = function.Descendants.OfType<ILFunction>().ToList();
        functions.Add(function);
        if (function.Method != null && !function.Method.MetadataToken.IsNil)
        {
            int owner = System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(function.Method.MetadataToken);
            foreach (ILFunction f in functions)
            {
                if (f != function && f.Method != null && !f.Method.MetadataToken.IsNil)
                    Program.Family[System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(f.Method.MetadataToken)] = owner;
                // An iterator's code is in MoveNext, decompiled as the body of the iterator method:
                // options are read under the iterator method's token.
                if (f.MoveNextMethod != null && !f.MoveNextMethod.MetadataToken.IsNil)
                    Program.Family[System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(f.MoveNextMethod.MetadataToken)] = owner;
            }
        }
        foreach (ILFunction f in functions)
        {
            RawBody raw = RawBody.Read(f, context);
            if (raw == null) continue;
            if (raw.Negations.Count > 0) MarkNegations(f, raw);
            DropStructInit(f, raw);
        }
    }

    private static IEnumerable<ILInstruction> Own(ILFunction f)
    {
        // Instructions of f, without those of nested lambdas: their offsets are in another body.
        var stack = new Stack<ILInstruction>();
        stack.Push(f.Body);
        while (stack.Count > 0)
        {
            ILInstruction i = stack.Pop();
            yield return i;
            foreach (ILInstruction c in i.Children) if (!(c is ILFunction)) stack.Push(c);
        }
    }

    private static void MarkNegations(ILFunction f, RawBody raw)
    {
        foreach (ILInstruction i in Own(f))
        {
            var sub = i as BinaryNumericInstruction;
            if (sub == null || sub.Operator != BinaryNumericOperator.Sub) continue;
            if (!(sub.Left is LdcF4) && !(sub.Left is LdcF8)) continue;
            // A zero that was in the IL has its own range. The one the IL reader makes up for
            // 'neg' has none, or the range of the 'neg' itself.
            bool own = false, atNeg = false;
            foreach (var r in sub.Left.ILRanges)
            {
                if (r.Start == r.End) continue;
                if (raw.Negations.Contains(r.Start)) atNeg = true; else own = true;
            }
            bool covers = false;
            foreach (var r in sub.ILRanges)
                foreach (int n in raw.Negations) if (n >= r.Start && n < r.End) covers = true;
            if (atNeg || (!own && covers)) Negations.GetValue(sub, _ => Marker);
        }
    }

    private static void DropStructInit(ILFunction f, RawBody raw)
    {
        foreach (ILVariable v in f.Variables)
        {
            if (v.Kind != VariableKind.Local || !v.Index.HasValue || !v.UsesInitialValue) continue;
            if (raw.InitObjLocals.Contains(v.Index.Value)) continue;
            ITypeDefinition def = v.Type.GetDefinition();
            if (v.Type.Kind != TypeKind.Struct || def == null || def.KnownTypeCode != KnownTypeCode.None) continue;
            v.UsesInitialValue = false;
        }
    }
}

internal sealed class NegationToUnaryMinus : IAstTransform
{
    public void Run(AstNode rootNode, TransformContext context)
    {
        foreach (var node in rootNode.Descendants.OfType<BinaryOperatorExpression>().ToList())
        {
            if (node.Operator != BinaryOperatorType.Subtract) continue;
            object marker;
            bool marked = false;
            foreach (var inst in node.Annotations.OfType<ILInstruction>())
                if (RestoreFromIL.Negations.TryGetValue(inst, out marker)) marked = true;
            if (!marked) continue;
            Expression operand = node.Right.Detach();
            node.ReplaceWith(new UnaryOperatorExpression(UnaryOperatorType.Minus, operand).CopyAnnotationsFrom(node));
        }
    }
}
