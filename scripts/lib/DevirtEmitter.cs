// Writes the CIL body of a virtualized method from its virtual instructions.
// docs/vm-opcodes.tsv says which CIL instruction each handler of the VM stands for; operands
// are converted by the operand kind of that instruction (docs/pipeline.md).
// C# 5: compiled by Add-Type together with Devirt.cs and DevirtResolver.cs.
using System;
using System.Collections.Generic;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

public sealed class VmEmitter
{
    private static readonly Dictionary<string, OpCode> byName = Names();
    private readonly VmResolver resolver;
    private readonly Dictionary<string, string> cilOfHandler;
    // The string decryptor and what it returns for each id: the virtual code calls it as the
    // rest of the program did, 'ldc.i4 id; call', which is written back as 'ldstr'
    // (scripts/decrypt-strings.ps1 does the same for the methods that kept their IL).
    public uint DecryptorToken;
    public Dictionary<int, string> Strings = new Dictionary<int, string>();
    // The virtualized methods by their position in the stream: one of them calls another
    // with an instruction of the VM (ExecuteVmCall), which stands for a plain call.
    public Dictionary<long, MethodDef> Virtualized = new Dictionary<long, MethodDef>();
    // Encrypted blocks that have no key (VmStreamFile.BlockKeys): each is written as a call to
    // a method without a body, created in BlockHost under BlockName. EncryptedBlocks counts
    // them. The ones that have a key are in the instructions already (VmMethod.Blocks).
    public TypeDef BlockHost;
    public string BlockName = "EncryptedBlock";
    public int EncryptedBlocks;
    private MethodDef blockMethod;

    public VmEmitter(VmResolver resolver, Dictionary<string, string> cilOfHandler)
    {
        this.resolver = resolver;
        this.cilOfHandler = cilOfHandler;
    }

    private static Dictionary<string, OpCode> Names()
    {
        Dictionary<string, OpCode> names = new Dictionary<string, OpCode>(StringComparer.Ordinal);
        foreach (FieldInfo f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            OpCode op = f.GetValue(null) as OpCode;
            if (op != null && !names.ContainsKey(op.Name)) names[op.Name] = op;
        }
        return names;
    }

    public CilBody Emit(VmMethod vm, MethodDef target)
    {
        if (vm.Error != null) throw new FormatException(vm.Error);
        CilBody body = new CilBody();
        foreach (int local in vm.Locals) body.Variables.Add(new Local(resolver.ResolveType(local)));
        body.InitLocals = body.Variables.Count > 0;

        Dictionary<int, Instruction> at = new Dictionary<int, Instruction>();
        List<KeyValuePair<Instruction, object>> jumps = new List<KeyValuePair<Instruction, object>>();
        List<int> offsets = new List<int>();
        List<Instruction[]> blocks = new List<Instruction[]>();
        HashSet<uint> blockStarts = new HashSet<uint>();
        for (int k = 0; k < vm.Instructions.Count; k++)
        {
            VmInstruction vi = vm.Instructions[k];
            if (vi.Opcode == VmStreamFile.EncryptedBlock) throw new FormatException(string.Format("{0:X4}: encrypted bytes without the instruction that opens a block", vi.Offset));
            string name;
            if (!cilOfHandler.TryGetValue(vi.Opcode.Handler, out name)) throw new FormatException(string.Format("{0:X4}: no CIL instruction for {1}", vi.Offset, vi.Opcode.Handler));
            OpCode op;
            if (name == "vm:decrypt-block")
            {
                // An encrypted block that has no key (docs/pipeline.md).
                // The VM pops a block id, a key and a salt, runs the
                // block, and its 'finally' closes it: three instructions here, the opening
                // one, the bytes, 'endfinally'. They are stood for by a call that takes the
                // same three values and a jump to somewhere this reading cannot see: one of
                // the places of the body nothing else reaches (filled in below).
                VmInstruction bytes = k + 1 < vm.Instructions.Count ? vm.Instructions[k + 1] : null;
                VmInstruction close = k + 2 < vm.Instructions.Count ? vm.Instructions[k + 2] : null;
                string closeName;
                if (bytes == null || bytes.Opcode != VmStreamFile.EncryptedBlock || close == null
                    || !cilOfHandler.TryGetValue(close.Opcode.Handler, out closeName) || closeName != "endfinally")
                    throw new FormatException(string.Format("{0:X4}: a block that is not 'open, bytes, endfinally'", vi.Offset));
                Instruction[] stand = { new Instruction(OpCodes.Call, BlockMethod()), new Instruction(OpCodes.Switch), new Instruction(OpCodes.Br) };
                VmInstruction[] stood = { vi, bytes, close };
                for (int s = 0; s < 3; s++)
                {
                    at[stood[s].Offset] = stand[s];
                    offsets.Add(stood[s].Offset);
                    body.Instructions.Add(stand[s]);
                }
                blocks.Add(stand);
                blockStarts.Add((uint)bytes.Offset);
                k += 2;
                continue;
            }
            if (name == "vm:call")
            {
                // Bit 31: the low 30 bits are the position of the callee; bit 30: virtual call.
                int word = Convert.ToInt32(vi.Operand);
                MethodDef callee;
                if (word >= 0 || !Virtualized.TryGetValue(word & 0x3FFFFFFF, out callee)) throw new FormatException(string.Format("{0:X4}: call of a virtualized method that is not one of the replacement bodies ({1:X8})", vi.Offset, word));
                Instruction direct = new Instruction((word & 0x40000000) != 0 ? OpCodes.Callvirt : OpCodes.Call, callee);
                at[vi.Offset] = direct;
                offsets.Add(vi.Offset);
                body.Instructions.Add(direct);
                continue;
            }
            // The stream has one 'starg' for both forms: the compiler writes the short one.
            if (name == "starg" && Convert.ToInt32(vi.Operand) < 256) name = "starg.s";
            if (!byName.TryGetValue(name, out op)) throw new FormatException(string.Format("{0:X4}: {1} ({2}) is not a CIL instruction", vi.Offset, name, vi.Opcode.Handler));
            Instruction ins = new Instruction(op);
            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    break;
                case OperandType.InlineBrTarget:
                case OperandType.ShortInlineBrTarget:
                    jumps.Add(new KeyValuePair<Instruction, object>(ins, Convert.ToInt32(vi.Operand)));
                    break;
                case OperandType.InlineSwitch:
                    jumps.Add(new KeyValuePair<Instruction, object>(ins, (int[])vi.Operand));
                    break;
                case OperandType.InlineVar:
                case OperandType.ShortInlineVar:
                    int index = Convert.ToInt32(vi.Operand);
                    if (name.StartsWith("ldarg", StringComparison.Ordinal) || name.StartsWith("starg", StringComparison.Ordinal)) ins.Operand = target.Parameters[index];
                    else ins.Operand = body.Variables[index];
                    break;
                case OperandType.ShortInlineI:
                    if (op.Code == Code.Ldc_I4_S) ins.Operand = Convert.ToSByte(vi.Operand);
                    else ins.Operand = Convert.ToByte(vi.Operand);
                    break;
                case OperandType.InlineI:
                    ins.Operand = Convert.ToInt32(vi.Operand);
                    break;
                case OperandType.InlineI8:
                    ins.Operand = Convert.ToInt64(vi.Operand);
                    break;
                case OperandType.ShortInlineR:
                    ins.Operand = Convert.ToSingle(vi.Operand);
                    break;
                case OperandType.InlineR:
                    ins.Operand = Convert.ToDouble(vi.Operand);
                    break;
                case OperandType.InlineString:
                    ins.Operand = (string)resolver.Resolve(Convert.ToInt32(vi.Operand));
                    break;
                case OperandType.InlineField:
                case OperandType.InlineMethod:
                case OperandType.InlineType:
                case OperandType.InlineTok:
                    object reference = resolver.Resolve(Convert.ToInt32(vi.Operand));
                    TypeSig sig = reference as TypeSig;
                    ins.Operand = sig != null ? resolver.Existing(sig.ToTypeDefOrRef()) : reference;
                    if (ins.Operand == null || ins.Operand is string) throw new FormatException(string.Format("{0:X4}: {1} with operand {2}", vi.Offset, name, reference));
                    break;
                default:
                    throw new NotSupportedException(string.Format("{0:X4}: operand of {1}", vi.Offset, name));
            }
            MethodDef called = ins.Operand as MethodDef;
            if (op.Code == Code.Call && called != null && called.MDToken.Raw == DecryptorToken && body.Instructions.Count > 0)
            {
                Instruction constant = body.Instructions[body.Instructions.Count - 1];
                string value;
                if (constant.IsLdcI4() && Strings.TryGetValue(constant.GetLdcI4Value(), out value))
                {
                    constant.OpCode = OpCodes.Ldstr;
                    constant.Operand = value;
                    at[vi.Offset] = constant;
                    continue;
                }
                throw new FormatException(string.Format("{0:X4}: string decryptor called without a known id", vi.Offset));
            }
            at[vi.Offset] = ins;
            offsets.Add(vi.Offset);
            body.Instructions.Add(ins);
        }

        // A decrypted block: what jumped to the instruction that opened it goes to its code.
        foreach (VmBlock block in vm.Blocks) at[block.Entry] = Target(at, block.Offset);
        foreach (KeyValuePair<Instruction, object> jump in jumps)
        {
            int[] table = jump.Value as int[];
            if (table != null)
            {
                Instruction[] targets = new Instruction[table.Length];
                for (int i = 0; i < table.Length; i++) targets[i] = Target(at, table[i]);
                jump.Key.Operand = targets;
            }
            else jump.Key.Operand = Target(at, (int)jump.Value);
        }

        // Protected regions. The stream gives the start of the block, the position of its
        // last instruction, and the start of the handler; a handler ends where its code does.
        foreach (VmHandler h in vm.Handlers)
        {
            // The 'finally' of an encrypted block is the obfuscator's: it went with the block.
            if (blockStarts.Contains(h.TryStart)) continue;
            ExceptionHandler eh = new ExceptionHandler(h.Kind == 0 ? ExceptionHandlerType.Catch : h.Kind == 2 ? ExceptionHandlerType.Filter : h.Kind == 4 ? ExceptionHandlerType.Finally : ExceptionHandlerType.Fault);
            eh.TryStart = Target(at, (int)h.TryStart);
            eh.TryEnd = After(at, offsets, body, (int)(h.TryStart + h.TryLength));
            eh.HandlerStart = Target(at, (int)h.HandlerStart);
            int end = HandlerEnd(vm, at, offsets, (int)h.HandlerStart);
            eh.HandlerEnd = end < 0 ? null : Target(at, end);
            if (h.Kind == 0) eh.CatchType = (ITypeDefOrRef)resolver.Existing(resolver.ResolveType(h.CatchType).ToTypeDefOrRef());
            if (h.Kind == 2) eh.FilterStart = Target(at, (int)h.FilterStart);
            body.ExceptionHandlers.Add(eh);
        }
        // The stream lists the handlers last block first; the compiler lists them as their
        // blocks close (docs/matching.md).
        List<ExceptionHandler> sorted = new List<ExceptionHandler>();
        foreach (ExceptionHandler h in body.ExceptionHandlers)
        {
            int end = h.HandlerEnd == null ? int.MaxValue : body.Instructions.IndexOf(h.HandlerEnd);
            int k = sorted.Count;
            while (k > 0 && (sorted[k - 1].HandlerEnd == null ? int.MaxValue : body.Instructions.IndexOf(sorted[k - 1].HandlerEnd)) > end) k--;
            sorted.Insert(k, h);
        }
        body.ExceptionHandlers.Clear();
        foreach (ExceptionHandler h in sorted) body.ExceptionHandlers.Add(h);
        // The locals of the keys first: removing one renumbers the others.
        Local[] keyLocals = new Local[vm.Blocks.Count];
        for (int i = 0; i < keyLocals.Length; i++) keyLocals[i] = KeyLocal(vm.Blocks[i], body);
        for (int i = 0; i < keyLocals.Length; i++) Restore(vm.Blocks[i], keyLocals[i], body, at);
        if (blocks.Count > 0)
        {
            List<Instruction> orphans = Orphans(body);
            if (orphans.Count == 0) throw new FormatException("encrypted blocks, and no code that only they could reach");
            Instruction[] targets = new Instruction[orphans.Count - 1];
            for (int i = 0; i < targets.Length; i++) targets[i] = orphans[i];
            foreach (Instruction[] stand in blocks)
            {
                stand[1].Operand = targets;
                stand[2].Operand = orphans[orphans.Count - 1];
            }
            EncryptedBlocks += blocks.Count;
        }
        body.OptimizeBranches();
        // The stream does not hold it; what follows (scripts/lib/UndoInlining.cs) keeps the
        // value of the body it edits.
        body.MaxStack = (ushort)dnlib.DotNet.Writer.MaxStackCalculator.GetMaxStack(body.Instructions, body.ExceptionHandlers);
        return body;
    }

    // Decrypted blocks restored (DecryptedBlocks), and the fields that held the encrypted
    // image of their keys: nothing reads them once the tests are written back.
    public int DecryptedBlocks;
    public List<FieldDef> KeyFields = new List<FieldDef>();

    // Undoes what the obfuscator wrote around a decrypted block (docs/pipeline.md).
    // The code of the block is in the body already.
    //  - Exits. The block was the protected region of a 'finally' of the obfuscator: its code
    //    leaves it with 'leave', and a conditional jump out of it goes through a 'leave'
    //    placed at the end of the block ('brtrue L; leave default; L: leave case'). The jump
    //    is given its target back and the relay, which nothing else reaches, is removed; a
    //    'leave' that leaves no other protected region is the 'br' it was.
    //  - The test that led to the block. The value tested (a hash) was boxed and kept in a
    //    local, the key of the block, and compared through its encrypted image:
    //        ldloc h; box; dup; stloc K; ldc.i4 256; newarr; dup; ldtoken F;
    //        call InitializeArray; ldc.i8 salt; call bool (object, byte[], uint64); brtrue block
    //    The block only decrypts when K is its key: the test is 'h == key', written as the
    //    compiler does, 'ldloc h; ldc.i4 key; beq block'. K goes with it.
    private Local KeyLocal(VmBlock block, CilBody body)
    {
        string load;
        if (!cilOfHandler.TryGetValue(block.KeyLoad.Opcode.Handler, out load) || !load.StartsWith("ldloc", StringComparison.Ordinal) || load.StartsWith("ldloca", StringComparison.Ordinal))
            throw new FormatException(string.Format("{0:X4}: the key of the block is not read from a local", block.Offset));
        int index = block.KeyLoad.Operand != null ? Convert.ToInt32(block.KeyLoad.Operand) : int.Parse(load.Substring(load.Length - 1));
        return body.Variables[index];
    }

    private void Restore(VmBlock block, Local key, CilBody body, Dictionary<int, Instruction> at)
    {
        IList<Instruction> code = body.Instructions;
        Instruction first = Target(at, block.Offset);
        HashSet<Instruction> inside = new HashSet<Instruction>();
        foreach (KeyValuePair<int, Instruction> pair in at)
            if (pair.Key >= block.Offset && pair.Key < block.End) inside.Add(pair.Value);
        for (int i = code.Count - 1; i > 0; i--)
        {
            Instruction relay = code[i];
            if (!inside.Contains(relay) || relay.OpCode.Code != Code.Leave || code[i - 1].OpCode.FlowControl != FlowControl.Branch) continue;
            bool any = false, onlyInside = true;
            foreach (Instruction j in code)
                if (Jumps(j, relay)) { any = true; if (!inside.Contains(j) || j.OpCode.FlowControl != FlowControl.Cond_Branch) onlyInside = false; }
            if (!any || !onlyInside || Bounds(body, relay) || !SameRegions(body, relay, (Instruction)relay.Operand)) continue;
            foreach (Instruction j in code) Retarget(j, relay, (Instruction)relay.Operand);
            code.RemoveAt(i);
            inside.Remove(relay);
        }
        foreach (Instruction leave in inside)
            if (leave.OpCode.Code == Code.Leave && SameRegions(body, leave, (Instruction)leave.Operand)) leave.OpCode = OpCodes.Br;

        int store = -1;
        for (int i = 0; i < code.Count; i++)
        {
            if (code[i].GetLocal(body.Variables) != key) continue;
            if (store >= 0 || !code[i].IsStloc()) throw new FormatException(string.Format("{0:X4}: the local of the key has other uses", block.Offset));
            store = i;
        }
        bool shape = store >= 3 && store + 8 < code.Count
            && code[store - 3].IsLdloc() && code[store - 2].OpCode.Code == Code.Box && code[store - 1].OpCode.Code == Code.Dup
            && code[store + 1].IsLdcI4() && code[store + 2].OpCode.Code == Code.Newarr && code[store + 3].OpCode.Code == Code.Dup
            && code[store + 4].OpCode.Code == Code.Ldtoken && code[store + 4].Operand is FieldDef && code[store + 5].OpCode.Code == Code.Call
            && code[store + 6].OpCode.Code == Code.Ldc_I8 && code[store + 7].OpCode.Code == Code.Call
            && (code[store + 8].OpCode.Code == Code.Brtrue || code[store + 8].OpCode.Code == Code.Brtrue_S)
            && code[store + 8].Operand == first;
        IMethod test = shape ? code[store + 7].Operand as IMethod : null;
        if (test == null || test.MethodSig.Params.Count != 3 || test.MethodSig.RetType.ElementType != ElementType.Boolean)
            throw new FormatException(string.Format("{0:X4}: the key of the block is not set by a test that leads to it", block.Offset));
        for (int i = store - 2; i <= store + 7; i++)
        {
            foreach (Instruction j in code)
                if (Jumps(j, code[i])) throw new FormatException(string.Format("{0:X4}: a jump into the test of the block", block.Offset));
            if (Bounds(body, code[i])) throw new FormatException(string.Format("{0:X4}: a protected region starts in the test of the block", block.Offset));
        }
        KeyFields.Add((FieldDef)code[store + 4].Operand);
        code[store + 8].OpCode = OpCodes.Beq;
        for (int i = store + 7; i >= store - 2; i--) code.RemoveAt(i);
        code.Insert(store - 2, Instruction.CreateLdcI4(unchecked((int)block.Key)));
        body.Variables.Remove(key);
        DecryptedBlocks++;
    }

    private static bool Jumps(Instruction from, Instruction to)
    {
        if (from.Operand == to) return true;
        Instruction[] table = from.Operand as Instruction[];
        return table != null && Array.IndexOf(table, to) >= 0;
    }

    private static void Retarget(Instruction from, Instruction old, Instruction target)
    {
        if (from.Operand == old) from.Operand = target;
        Instruction[] table = from.Operand as Instruction[];
        if (table != null)
            for (int i = 0; i < table.Length; i++) if (table[i] == old) table[i] = target;
    }

    // Both instructions are in the same protected regions and handlers.
    private static bool SameRegions(CilBody body, Instruction a, Instruction b)
    {
        IList<Instruction> code = body.Instructions;
        int ia = code.IndexOf(a), ib = code.IndexOf(b);
        foreach (ExceptionHandler h in body.ExceptionHandlers)
        {
            int tryStart = code.IndexOf(h.TryStart), tryEnd = h.TryEnd == null ? code.Count : code.IndexOf(h.TryEnd);
            int start = code.IndexOf(h.FilterStart ?? h.HandlerStart), end = h.HandlerEnd == null ? code.Count : code.IndexOf(h.HandlerEnd);
            if ((ia >= tryStart && ia < tryEnd) != (ib >= tryStart && ib < tryEnd)) return false;
            if ((ia >= start && ia < end) != (ib >= start && ib < end)) return false;
        }
        return true;
    }

    private static bool Bounds(CilBody body, Instruction ins)
    {
        foreach (ExceptionHandler h in body.ExceptionHandlers)
            if (h.TryStart == ins || h.TryEnd == ins || h.HandlerStart == ins || h.HandlerEnd == ins || h.FilterStart == ins) return true;
        return false;
    }

    // The method that stands for an encrypted block: no body ('extern', internal call), since
    // what it does is in the VM. Created once, in BlockHost.
    private MethodDef BlockMethod()
    {
        if (blockMethod != null) return blockMethod;
        if (BlockHost == null) throw new FormatException("encrypted block: no type to hold " + BlockName);
        ModuleDef mod = BlockHost.Module;
        foreach (MethodDef existing in BlockHost.Methods)
            if (existing.Name == BlockName) throw new FormatException(BlockHost.FullName + " already has a method " + BlockName);
        MethodSig sig = MethodSig.CreateStatic(mod.CorLibTypes.Int32, mod.CorLibTypes.Int32, mod.CorLibTypes.Object, mod.CorLibTypes.Int64);
        MethodDefUser m = new MethodDefUser(BlockName, sig,
            dnlib.DotNet.MethodImplAttributes.IL | dnlib.DotNet.MethodImplAttributes.Managed | dnlib.DotNet.MethodImplAttributes.InternalCall,
            dnlib.DotNet.MethodAttributes.Assembly | dnlib.DotNet.MethodAttributes.HideBySig | dnlib.DotNet.MethodAttributes.Static);
        m.ParamDefs.Add(new ParamDefUser("id", 1));
        m.ParamDefs.Add(new ParamDefUser("key", 2));
        m.ParamDefs.Add(new ParamDefUser("salt", 3));
        BlockHost.Methods.Add(m);
        blockMethod = m;
        return m;
    }

    // The first instruction of every stretch of code that nothing reaches: neither the entry,
    // nor a handler, nor a jump. A block still encrypted jumps nowhere here (its 'switch' and
    // its 'br' have no target yet): what only the blocks reach is what this returns.
    private static List<Instruction> Orphans(CilBody body)
    {
        IList<Instruction> code = body.Instructions;
        Dictionary<Instruction, int> indexOf = new Dictionary<Instruction, int>();
        for (int i = 0; i < code.Count; i++) indexOf[code[i]] = i;
        bool[] seen = new bool[code.Count];
        Stack<int> todo = new Stack<int>();
        todo.Push(0);
        foreach (ExceptionHandler h in body.ExceptionHandlers)
        {
            todo.Push(indexOf[h.HandlerStart]);
            if (h.FilterStart != null) todo.Push(indexOf[h.FilterStart]);
        }
        List<Instruction> orphans = new List<Instruction>();
        int next = 0;
        while (true)
        {
            while (todo.Count > 0)
            {
                int i = todo.Pop();
                while (i < code.Count && !seen[i])
                {
                    seen[i] = true;
                    Instruction ins = code[i];
                    Instruction target = ins.Operand as Instruction;
                    if (target != null) todo.Push(indexOf[target]);
                    Instruction[] table = ins.Operand as Instruction[];
                    if (table != null) foreach (Instruction t in table) todo.Push(indexOf[t]);
                    FlowControl flow = ins.OpCode.FlowControl;
                    if (flow == FlowControl.Branch || flow == FlowControl.Return || flow == FlowControl.Throw) break;
                    i++;
                }
            }
            while (next < code.Count && seen[next]) next++;
            if (next == code.Count) return orphans;
            orphans.Add(code[next]);
            todo.Push(next);
        }
    }

    private static Instruction Target(Dictionary<int, Instruction> at, int offset)
    {
        Instruction ins;
        if (!at.TryGetValue(offset, out ins)) throw new FormatException(string.Format("no instruction at {0:X4}", offset));
        return ins;
    }

    // The instruction after the one at this offset; null at the end of the body.
    private static Instruction After(Dictionary<int, Instruction> at, List<int> offsets, CilBody body, int offset)
    {
        int index = offsets.IndexOf(offset);
        if (index < 0) throw new FormatException(string.Format("no instruction at {0:X4}", offset));
        return index + 1 < body.Instructions.Count ? body.Instructions[index + 1] : null;
    }

    // Where the handler that starts at 'start' ends: after the first instruction that leaves
    // (leave, endfinally, endfilter, throw, rethrow, ret, br) while no jump of the handler
    // goes further on. A protected region inside the handler is passed over with its own
    // handlers. Returns the offset of the instruction that follows, -1 at the end of the body.
    private int HandlerEnd(VmMethod vm, Dictionary<int, Instruction> at, List<int> offsets, int start)
    {
        int index = offsets.IndexOf(start);
        int furthest = start;
        while (index < offsets.Count)
        {
            int offset = offsets[index];
            // A region nested here: continue after its last handler.
            int skipTo = -2;
            foreach (VmHandler inner in vm.Handlers)
                if (inner.TryStart == offset && inner.HandlerStart > (uint)start)
                {
                    int innerEnd = HandlerEnd(vm, at, offsets, (int)inner.HandlerStart);
                    if (innerEnd == -1) return -1;
                    if (innerEnd > skipTo) skipTo = innerEnd;
                }
            if (skipTo >= 0)
            {
                // The handler goes on after the region when something comes there: a jump
                // of the handler seen so far, or one out of the region ('leave' at the end
                // of its blocks). When nothing does, the region does not complete inside
                // the handler: the handler ends with it.
                bool reached = furthest >= skipTo;
                int stop = offsets.IndexOf(skipTo);
                for (int j = index; j < stop && !reached; j++)
                {
                    Instruction inside = at[offsets[j]].Operand as Instruction;
                    if (inside != null && OffsetOf(at, inside) == skipTo) reached = true;
                }
                if (!reached) return skipTo;
                if (skipTo > furthest) furthest = skipTo;
                index = stop;
                continue;
            }
            Instruction ins = at[offset];
            Instruction target = ins.Operand as Instruction;
            FlowControl flow = ins.OpCode.FlowControl;
            bool leaves = ins.OpCode.Code == Code.Leave || ins.OpCode.Code == Code.Leave_S;
            if (target != null && !leaves)
            {
                int to = OffsetOf(at, target);
                if (to > furthest) furthest = to;
            }
            Instruction[] targets = ins.Operand as Instruction[];
            if (targets != null)
                foreach (Instruction t in targets)
                {
                    int to = OffsetOf(at, t);
                    if (to > furthest) furthest = to;
                }
            bool ends = leaves || flow == FlowControl.Return || flow == FlowControl.Throw || ins.OpCode.Code == Code.Br || ins.OpCode.Code == Code.Br_S;
            if (ends && offset >= furthest) return index + 1 < offsets.Count ? offsets[index + 1] : -1;
            index++;
        }
        return -1;
    }

    private static int OffsetOf(Dictionary<int, Instruction> at, Instruction ins)
    {
        foreach (KeyValuePair<int, Instruction> pair in at)
            if (pair.Value == ins) return pair.Key;
        throw new FormatException("instruction without offset");
    }
}
