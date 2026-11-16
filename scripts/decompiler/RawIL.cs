// The raw body of a method, as a list of instructions: what the IL-faithful transforms read
// to decide between two source forms the decompiler cannot tell apart.
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RawIL
{
    internal struct Op
    {
        public int Offset;
        public int End;
        public ILOpCode Code;
        public int Operand;    // local or argument index, metadata token, or branch target; -1 when none
    }

    public readonly List<Op> Ops = new List<Op>();
    public readonly Dictionary<int, int> IndexAt = new Dictionary<int, int>();   // offset -> index in Ops
    public readonly HashSet<int> Targets = new HashSet<int>();                    // offsets a branch jumps to
    public readonly HashSet<int> CondTargets = new HashSet<int>();                // same, conditional branches and switches only
    public readonly Dictionary<int, int[]> Switches = new Dictionary<int, int[]>();  // offset of a 'switch' -> its targets
    public ImmutableArray<IType> Locals = ImmutableArray<IType>.Empty;
    public readonly Dictionary<int, long> Wide = new Dictionary<int, long>();                   // offset of an 'ldc.i8' -> its value
    // Offsets where a protected block or a handler ends: the 'leave' just before one is the
    // end of the block itself, not a statement.
    public readonly HashSet<int> RegionEnds = new HashSet<int>();
    // Protected regions: try start, try end, handler start, handler end, 1 for finally / fault.
    public readonly List<int[]> Regions = new List<int[]>();
    private IMethod method;
    private IDecompilerTypeSystem typeSystem;

    public static RawIL Read(ILFunction f, ILTransformContext context)
    {
        // An iterator is decompiled from its MoveNext: the ranges of its nodes are offsets there.
        if (f.Method == null) return null;
        if (f.MoveNextMethod != null)
        {
            // There 'this' has the range of the copy MoveNext makes of it at its start, which
            // says nothing about where an expression that reads it stands: no range.
            foreach (LdLoc load in Own(f).OfType<LdLoc>().ToList())
                if (load.Variable.Kind == VariableKind.Parameter && load.Variable.Index == -1 && load.ILRanges.Any()) load.ReplaceWith(new LdLoc(load.Variable));
        }
        return Read(context.TypeSystem, f.MoveNextMethod ?? f.Method);
    }

    public static RawIL Read(IDecompilerTypeSystem typeSystem, IMethod method)
    {
        if (method.MetadataToken.IsNil || method.MetadataToken.Kind != HandleKind.MethodDefinition) return null;
        var module = typeSystem.MainModule.MetadataFile;
        var def = module.Metadata.GetMethodDefinition((MethodDefinitionHandle)method.MetadataToken);
        if (def.RelativeVirtualAddress == 0) return null;
        MethodBodyBlock body = module.GetMethodBody(def.RelativeVirtualAddress);
        BlobReader reader = body.GetILReader();
        var raw = new RawIL { method = method, typeSystem = typeSystem };
        while (reader.RemainingBytes > 0)
        {
            var op = new Op { Offset = reader.Offset, Operand = -1 };
            op.Code = ILParser.DecodeOpCode(ref reader);
            switch (op.Code.GetOperandType())
            {
                case OperandType.BrTarget:
                case OperandType.ShortBrTarget:
                    op.Operand = ILParser.DecodeBranchTarget(ref reader, op.Code);
                    raw.Targets.Add(op.Operand);
                    if (op.Code != ILOpCode.Br && op.Code != ILOpCode.Br_s && op.Code != ILOpCode.Leave && op.Code != ILOpCode.Leave_s) raw.CondTargets.Add(op.Operand);
                    break;
                case OperandType.Switch:
                    int[] table = ILParser.DecodeSwitchTargets(ref reader);
                    raw.Switches[op.Offset] = table;
                    foreach (int t in table) { raw.Targets.Add(t); raw.CondTargets.Add(t); }
                    break;
                case OperandType.Variable:
                case OperandType.ShortVariable:
                    op.Operand = ILParser.DecodeIndex(ref reader, op.Code);
                    break;
                case OperandType.I:
                    op.Operand = reader.ReadInt32();
                    break;
                case OperandType.ShortI:
                    op.Operand = op.Code == ILOpCode.Ldc_i4_s ? reader.ReadSByte() : reader.ReadByte();
                    break;
                case OperandType.I8:
                    raw.Wide[op.Offset] = reader.ReadInt64();
                    break;
                case OperandType.String:
                case OperandType.Field:
                case OperandType.Method:
                case OperandType.Sig:
                case OperandType.Tok:
                case OperandType.Type:
                    op.Operand = reader.ReadInt32();
                    break;
                default:
                    ILParser.SkipOperand(ref reader, op.Code);
                    break;
            }
            switch (op.Code)
            {
                case ILOpCode.Ldc_i4_m1: op.Operand = -1; break;
                case ILOpCode.Ldc_i4_0: op.Operand = 0; break;
                case ILOpCode.Ldc_i4_4: op.Operand = 4; break;
                case ILOpCode.Ldc_i4_5: op.Operand = 5; break;
                case ILOpCode.Ldc_i4_6: op.Operand = 6; break;
                case ILOpCode.Ldc_i4_7: op.Operand = 7; break;
                case ILOpCode.Ldc_i4_8: op.Operand = 8; break;
                case ILOpCode.Ldloc_0: case ILOpCode.Stloc_0: case ILOpCode.Ldarg_0: op.Operand = 0; break;
                case ILOpCode.Ldloc_1: case ILOpCode.Stloc_1: case ILOpCode.Ldarg_1: case ILOpCode.Ldc_i4_1: op.Operand = 1; break;
                case ILOpCode.Ldloc_2: case ILOpCode.Stloc_2: case ILOpCode.Ldarg_2: case ILOpCode.Ldc_i4_2: op.Operand = 2; break;
                case ILOpCode.Ldloc_3: case ILOpCode.Stloc_3: case ILOpCode.Ldarg_3: case ILOpCode.Ldc_i4_3: op.Operand = 3; break;
            }
            op.End = reader.Offset;
            raw.IndexAt[op.Offset] = raw.Ops.Count;
            raw.Ops.Add(op);
        }
        foreach (var region in body.ExceptionRegions)
        {
            raw.RegionEnds.Add(region.TryOffset + region.TryLength);
            raw.RegionEnds.Add(region.HandlerOffset + region.HandlerLength);
            raw.Regions.Add(new[] { region.TryOffset, region.TryOffset + region.TryLength, region.HandlerOffset, region.HandlerOffset + region.HandlerLength, region.Kind == ExceptionRegionKind.Finally || region.Kind == ExceptionRegionKind.Fault ? 1 : 0 });
        }
        if (!body.LocalSignature.IsNil)
        {
            try { raw.Locals = typeSystem.MainModule.DecodeLocalSignature(body.LocalSignature, Generic(method)); }
            catch (System.BadImageFormatException) { }
        }
        return raw;
    }

    private static ICSharpCode.Decompiler.TypeSystem.GenericContext Generic(IMethod m)
    {
        return new ICSharpCode.Decompiler.TypeSystem.GenericContext(m.DeclaringTypeDefinition != null ? m.DeclaringTypeDefinition.TypeParameters : null, m.TypeParameters);
    }

    // Types of the local slots of a method, as its signature declares them.
    public static ImmutableArray<IType> LocalTypes(IDecompilerTypeSystem typeSystem, IMethod method)
    {
        if (method.MetadataToken.IsNil || method.MetadataToken.Kind != HandleKind.MethodDefinition) return ImmutableArray<IType>.Empty;
        var module = typeSystem.MainModule.MetadataFile;
        var def = module.Metadata.GetMethodDefinition((MethodDefinitionHandle)method.MetadataToken);
        if (def.RelativeVirtualAddress == 0) return ImmutableArray<IType>.Empty;
        MethodBodyBlock body = module.GetMethodBody(def.RelativeVirtualAddress);
        if (body.LocalSignature.IsNil) return ImmutableArray<IType>.Empty;
        try { return typeSystem.MainModule.DecodeLocalSignature(body.LocalSignature, Generic(method)); }
        catch (System.BadImageFormatException) { return ImmutableArray<IType>.Empty; }
    }

    public bool IsAt(int offset, out Op op)
    {
        int i;
        if (IndexAt.TryGetValue(offset, out i)) { op = Ops[i]; return true; }
        op = default(Op);
        return false;
    }

    // The instruction that ends at 'offset'.
    public bool EndsAt(int offset, out Op op)
    {
        for (int i = 0; i < Ops.Count; i++)
            if (Ops[i].End == offset) { op = Ops[i]; return true; }
        op = default(Op);
        return false;
    }

    // The instruction 'delta' places after (or before) the one at 'offset'.
    public bool Near(int offset, int delta, out Op op)
    {
        int i;
        op = default(Op);
        if (!IndexAt.TryGetValue(offset, out i) || i + delta < 0 || i + delta >= Ops.Count) return false;
        op = Ops[i + delta];
        return true;
    }

    public static bool IsStLoc(ILOpCode c)
    {
        return c == ILOpCode.Stloc || c == ILOpCode.Stloc_s || c == ILOpCode.Stloc_0 || c == ILOpCode.Stloc_1 || c == ILOpCode.Stloc_2 || c == ILOpCode.Stloc_3;
    }

    public static bool IsLdLoc(ILOpCode c)
    {
        return c == ILOpCode.Ldloc || c == ILOpCode.Ldloc_s || c == ILOpCode.Ldloc_0 || c == ILOpCode.Ldloc_1 || c == ILOpCode.Ldloc_2 || c == ILOpCode.Ldloc_3;
    }

    public static bool IsLdcI4(ILOpCode c)
    {
        return c == ILOpCode.Ldc_i4 || c == ILOpCode.Ldc_i4_s || (c >= ILOpCode.Ldc_i4_m1 && c <= ILOpCode.Ldc_i4_8);
    }

    public static bool IsLdArg(ILOpCode c)
    {
        return c == ILOpCode.Ldarg || c == ILOpCode.Ldarg_s || c == ILOpCode.Ldarg_0 || c == ILOpCode.Ldarg_1 || c == ILOpCode.Ldarg_2 || c == ILOpCode.Ldarg_3;
    }

    // Conditional branch with one target (not 'switch').
    public static bool IsCondBranch(ILOpCode c)
    {
        switch (c)
        {
            case ILOpCode.Brfalse: case ILOpCode.Brfalse_s: case ILOpCode.Brtrue: case ILOpCode.Brtrue_s:
            case ILOpCode.Beq: case ILOpCode.Beq_s: case ILOpCode.Bne_un: case ILOpCode.Bne_un_s:
            case ILOpCode.Bge: case ILOpCode.Bge_s: case ILOpCode.Bge_un: case ILOpCode.Bge_un_s:
            case ILOpCode.Bgt: case ILOpCode.Bgt_s: case ILOpCode.Bgt_un: case ILOpCode.Bgt_un_s:
            case ILOpCode.Ble: case ILOpCode.Ble_s: case ILOpCode.Ble_un: case ILOpCode.Ble_un_s:
            case ILOpCode.Blt: case ILOpCode.Blt_s: case ILOpCode.Blt_un: case ILOpCode.Blt_un_s:
                return true;
        }
        return false;
    }

    public static bool IsCall(ILOpCode c)
    {
        return c == ILOpCode.Call || c == ILOpCode.Callvirt;
    }

    // The literal of an 'ldstr'; null for another instruction.
    public string String(Op op)
    {
        if (op.Code != ILOpCode.Ldstr) return null;
        try { return typeSystem.MainModule.MetadataFile.Metadata.GetUserString(MetadataTokens.UserStringHandle(op.Operand & 0xFFFFFF)); }
        catch (System.BadImageFormatException) { return null; }
    }

    public IType LocalType(int index)
    {
        return index >= 0 && index < Locals.Length ? Locals[index] : null;
    }

    public IMethod Method(Op op)
    {
        if (!IsCall(op.Code) && op.Code != ILOpCode.Newobj && op.Code != ILOpCode.Ldftn) return null;
        try { return typeSystem.MainModule.ResolveMethod(MetadataTokens.EntityHandle(op.Operand), Generic(method)); }
        catch (System.BadImageFormatException) { return null; }
    }

    // Instructions of f, without those of nested lambdas: their offsets are in another body.
    public static IEnumerable<ILInstruction> Own(ILFunction f)
    {
        var stack = new Stack<ILInstruction>();
        stack.Push(f.Body);
        while (stack.Count > 0)
        {
            ILInstruction i = stack.Pop();
            yield return i;
            foreach (ILInstruction c in i.Children) if (!(c is ILFunction)) stack.Push(c);
        }
    }

    // Highest end offset among the instructions of an expression: where the next instruction
    // of the body starts, as long as the expression is evaluated in IL order. -1 when unknown.
    public static int EndOf(ILInstruction inst)
    {
        int max = -1;
        var stack = new Stack<ILInstruction>();
        stack.Push(inst);
        while (stack.Count > 0)
        {
            ILInstruction i = stack.Pop();
            if (i is ILFunction) continue;
            foreach (var r in i.ILRanges)
                if (r.Start != r.End && r.End > max) max = r.End;
            foreach (ILInstruction c in i.Children) stack.Push(c);
        }
        return max;
    }

    public static int StartOf(ILInstruction inst)
    {
        int min = -1;
        var stack = new Stack<ILInstruction>();
        stack.Push(inst);
        while (stack.Count > 0)
        {
            ILInstruction i = stack.Pop();
            if (i is ILFunction) continue;
            foreach (var r in i.ILRanges)
                if (r.Start != r.End && (min < 0 || r.Start < min)) min = r.Start;
            foreach (ILInstruction c in i.Children) stack.Push(c);
        }
        return min;
    }

    // The same without the exits: every 'return;' of a void method is one 'leave' for the
    // decompiler, which carries the place of another 'ret'.
    public static int StartOfCode(ILInstruction inst)
    {
        int min = -1;
        var stack = new Stack<ILInstruction>();
        stack.Push(inst);
        while (stack.Count > 0)
        {
            ILInstruction i = stack.Pop();
            if (i is ILFunction) continue;
            if (!(i is Leave) && !(i is Branch))
                foreach (var r in i.ILRanges)
                    if (r.Start != r.End && (min < 0 || r.Start < min)) min = r.Start;
            foreach (ILInstruction c in i.Children) stack.Push(c);
        }
        return min;
    }
}