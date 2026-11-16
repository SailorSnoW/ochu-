// IL transform that runs early, right after the first inlining of the decompiler.
//
// Temporary of the compiler under a call on a converted value.
//        ((int)e).ToString()           IL: e; stloc t; ldloca t; call Int32::ToString
// The slot is an 'int', the value an enum: the decompiler does not inline a value of
// another type and keeps 'stloc t(e); ... call ToString(ldloca t)' as two statements. Whatever
// holds them cannot become an expression any more: a conditional expression is printed as
// 'if / else' on a variable, an array initializer stops at that element. The slot is only
// ever written right before such a call: it is the copy the compiler makes to call a method
// on a value, never a variable of the source read elsewhere.
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

internal sealed class RestoreEarlyTemps : IILTransform
{
    public void Run(ILFunction function, ILTransformContext context)
    {
        var functions = function.Descendants.OfType<ILFunction>().ToList();
        functions.Add(function);
        foreach (ILFunction f in functions)
        {
            if (f.Method == null || !Program.Enabled("RestoreOrder", f.Method.MetadataToken)) continue;
            string debug = System.Environment.GetEnvironmentVariable("DECOMP_DEBUG_EARLY");
            if (debug != null && debug.Split(';').Contains(f.Method.FullName)) System.Console.Error.WriteLine("== early == " + f.Method.FullName + " " + f.Body);
            foreach (ILVariable v in f.Variables.ToList())
            {
                if (v.Kind != VariableKind.Local || !v.Index.HasValue || v.LoadCount != 0 || v.StoreCount < 1 || v.StoreCount != v.AddressCount) continue;
                if (!IsPrimitive(v.Type)) continue;
                var pairs = new List<KeyValuePair<StLoc, LdLoca>>();
                bool all = true, converted = false;
                foreach (StLoc store in v.StoreInstructions.OfType<StLoc>().ToList())
                {
                    var block = store.Parent as Block;
                    if (block == null || store.SlotInfo != Block.InstructionSlot || store.ChildIndex + 1 >= block.Instructions.Count) { all = false; break; }
                    ILInstruction next = block.Instructions[store.ChildIndex + 1];
                    var addresses = next.Descendants.OfType<LdLoca>().Where(a => a.Variable == v).ToList();
                    LdLoca address = addresses.Count == 1 ? addresses[0] : null;
                    var call = address == null ? null : address.Parent as CallInstruction;
                    if (call == null || call.Arguments[0] != address || call.Method.IsStatic || !call.Method.DeclaringType.Equals(v.Type) || call is NewObj) { all = false; break; }
                    // What the statement evaluates before the call was pushed before the value: loads only.
                    bool pure = true;
                    foreach (ILInstruction d in next.Descendants)
                    {
                        if (d == address || address.IsDescendantOf(d) || !Before(d, address, next)) continue;
                        if (!(d is LdLoc || d is LdStr || d is LdcI4 || d is LdNull || d is LdElema || d is LdFlda || d is LdsFlda || d is LdcI8 || d is LdcF4 || d is LdcF8)) pure = false;
                    }
                    if (!pure) { all = false; break; }
                    // Only what the decompiler leaves: a value of another type than the slot.
                    IType inferred = TypeOf(store.Value);
                    if (inferred != null && !inferred.Equals(v.Type)) converted = true;
                    pairs.Add(new KeyValuePair<StLoc, LdLoca>(store, address));
                }
                if (!all || !converted || pairs.Count != v.StoreCount || v.StoreInstructions.Count != v.StoreCount) continue;
                foreach (var pair in pairs)
                {
                    StLoc store = pair.Key;
                    var block = (Block)store.Parent;
                    ILInstruction value = store.Value;
                    store.Value = new Nop();
                    pair.Value.ReplaceWith(new AddressOf(value, v.Type));
                    block.Instructions.RemoveAt(store.ChildIndex);
                }
            }
        }
    }

    // True when d is evaluated before 'address' in 'root' (children are evaluated in order).
    private static bool Before(ILInstruction d, ILInstruction address, ILInstruction root)
    {
        var path = new List<ILInstruction>();
        for (ILInstruction p = address; p != null && p != root.Parent; p = p.Parent) path.Add(p);
        ILInstruction child = d;
        for (ILInstruction p = d.Parent; p != null && p != root.Parent; child = p, p = p.Parent)
        {
            int at = path.IndexOf(p);
            if (at <= 0) continue;
            return child.ChildIndex < path[at - 1].ChildIndex;
        }
        return false;
    }

    private static IType TypeOf(ILInstruction value)
    {
        if (value is LdObj) return ((LdObj)value).Type;
        if (value is LdLoc) return ((LdLoc)value).Variable.Kind == VariableKind.StackSlot ? null : ((LdLoc)value).Variable.Type;
        if (value is CallInstruction) return ((CallInstruction)value).Method.ReturnType;
        if (value is UnboxAny) return ((UnboxAny)value).Type;
        return null;
    }

    private static bool IsPrimitive(IType type)
    {
        switch (type.GetDefinition() == null ? KnownTypeCode.None : type.GetDefinition().KnownTypeCode)
        {
            case KnownTypeCode.Int32: case KnownTypeCode.UInt32: case KnownTypeCode.Int64: case KnownTypeCode.UInt64:
            case KnownTypeCode.Int16: case KnownTypeCode.UInt16: case KnownTypeCode.Byte: case KnownTypeCode.SByte:
                return true;
        }
        return false;
    }
}
