// Reads the stream of the obfuscator's virtual machine and writes back the CIL of the methods it
// holds (docs/pipeline.md). Every reading below is the one the decompiled VM does
// (src/Obfuscator/VirtualMachine.cs, VmBinaryReader.cs, VmXorStream.cs, VmAscii85.cs).
// C# 5: compiled by Add-Type under Windows PowerShell 5.1.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

public sealed class VmReader
{
    private readonly byte[] data;
    private readonly uint key;
    private readonly bool masked;
    public int Position;

    // The stream: every byte is xored with (key ^ position) (VmXorStream).
    public VmReader(byte[] data, int key)
    {
        this.data = data;
        this.key = unchecked((uint)(key ^ -559030707));
        this.masked = true;
    }

    // A body: plain bytes (VmMemoryStream).
    public VmReader(byte[] data)
    {
        this.data = data;
        this.masked = false;
    }

    public int Length { get { return data.Length; } }

    public byte ReadByte()
    {
        byte b = data[Position];
        if (masked) b = (byte)(b ^ (byte)(key ^ (uint)Position));
        Position++;
        return b;
    }

    public bool ReadBoolean() { return ReadByte() != 0; }

    // VmBinaryReader.ReadInt32 and VmMemoryStream.ReadScrambledInt32: b0 | b2 << 8 | b3 << 16 | b1 << 24.
    public int ReadInt32()
    {
        int b0 = ReadByte(), b1 = ReadByte(), b2 = ReadByte(), b3 = ReadByte();
        return b0 | (b2 << 8) | (b3 << 16) | (b1 << 24);
    }

    // ReadUInt32: little-endian.
    public uint ReadUInt32()
    {
        uint b0 = ReadByte(), b1 = ReadByte(), b2 = ReadByte(), b3 = ReadByte();
        return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
    }

    // ReadInt16 / ReadUInt16: big-endian.
    public ushort ReadUInt16()
    {
        int b0 = ReadByte(), b1 = ReadByte();
        return (ushort)((b0 << 8) | b1);
    }

    public short ReadInt16() { return unchecked((short)ReadUInt16()); }

    // ReadInt64.
    public long ReadInt64()
    {
        byte[] a = new byte[8];
        for (int i = 0; i < 8; i++) a[i] = ReadByte();
        uint low = (uint)(a[3] | (a[4] << 8) | (a[5] << 16) | (a[7] << 24));
        uint high = (uint)(a[2] | (a[0] << 8) | (a[6] << 16) | (a[1] << 24));
        return (long)(((ulong)high << 32) | low);
    }

    // ReadSingle: the four bytes as they are.
    public float ReadSingle()
    {
        byte[] a = new byte[4];
        for (int i = 0; i < 4; i++) a[i] = ReadByte();
        return BitConverter.ToSingle(a, 0);
    }

    // ReadDouble.
    public double ReadDouble()
    {
        byte[] a = new byte[8];
        for (int i = 0; i < 8; i++) a[i] = ReadByte();
        byte[] b = new byte[8];
        b[2] = a[0]; b[4] = a[2]; b[7] = a[4]; b[1] = a[7]; b[5] = a[1]; b[6] = a[6]; b[0] = a[5]; b[3] = a[3];
        return BitConverter.ToDouble(b, 0);
    }

    // Read7BitEncodedInt.
    public int Read7Bit()
    {
        int n = 0, shift = 0;
        byte b;
        do
        {
            b = ReadByte();
            n |= (b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);
        return n;
    }

    // ReadString: length, then UTF-8.
    public string ReadString()
    {
        int n = Read7Bit();
        byte[] bytes = new byte[n];
        for (int i = 0; i < n; i++) bytes[i] = ReadByte();
        return Encoding.UTF8.GetString(bytes);
    }

    public byte[] ReadBytes(int n)
    {
        byte[] bytes = new byte[n];
        for (int i = 0; i < n; i++) bytes[i] = ReadByte();
        return bytes;
    }
}

public sealed class VmOpcode
{
    public string Field;       // VmOpCodes field
    public int Id;
    public int OperandType;
    public string Handler;     // VirtualMachine method, null when the opcode has none
}

public sealed class VmHandler
{
    public int Kind;           // 0 catch, 1 fault, 2 filter, 4 finally
    public int CatchType;      // reference id
    public uint FilterStart, HandlerStart, TryLength, TryStart;
}

public sealed class VmInstruction
{
    public int Offset;
    public VmOpcode Opcode;
    public object Operand;     // null, sbyte, byte, ushort, int, uint, long, float, double, int[]
    public override string ToString()
    {
        string text = "";
        int[] table = Operand as int[];
        if (table != null) text = "[" + string.Join(", ", Array.ConvertAll(table, delegate(int t) { return t.ToString("X4"); })) + "]";
        else if (Operand != null) text = Convert.ToString(Operand, CultureInfo.InvariantCulture);
        return string.Format("{0:X4}: {1,-14} {2}", Offset, Opcode.Handler, text).TrimEnd();
    }
}

// An encrypted block that was decrypted: its code is in the instructions of the method, at the
// offsets the VM gives it (the offset of the encrypted bytes, then on).
public sealed class VmBlock
{
    public int Offset;             // of the encrypted bytes, and of the first instruction
    public int End;                // where the obfuscator's handler started: end of the code
    public int Entry;              // offset of the instruction that opened it: jumps go there
    public uint Key;
    public long Salt;
    public VmInstruction IdLoad;   // the three values the opening instruction popped: block id,
    public VmInstruction KeyLoad;  // key (a local the obfuscator added),
    public VmInstruction SaltLoad; // salt
}

public sealed class VmMethod
{
    public string Id;
    public long Position;
    public byte Flags;
    public int[] Locals;
    public int ReturnType, DeclaringType;
    public int[] ParameterTypes;
    public bool[] ParameterFlags;
    public string Name;
    public VmHandler[] Handlers;
    public byte[] Body;
    public List<VmInstruction> Instructions = new List<VmInstruction>();
    public List<VmBlock> Blocks = new List<VmBlock>();   // the decrypted ones
    public string Error;       // set when the body does not split to its end
    public bool IsStatic { get { return (Flags & 0x10) != 0; } }
}

public sealed class VmStreamFile
{
    public const int StreamKey = -693692741;      // VirtualMachine.GetStreamKey
    public const int IdKey = 1851064669;          // VirtualMachine.GetIdKey
    public readonly byte[] Data;
    public readonly Dictionary<int, VmOpcode> Opcodes = new Dictionary<int, VmOpcode>();
    // Not an opcode of the VM: the bytes of an encrypted block, kept as one instruction.
    public static readonly VmOpcode EncryptedBlock = new VmOpcode { Field = "-", Id = 0, OperandType = -1, Handler = "vm:encrypted" };
    // Keys of the encrypted blocks (docs/vm-block-keys.tsv): position of the method in the
    // stream -> offset of the encrypted bytes in its body -> key. A block that has its key
    // is read decrypted; one that has none is kept as its bytes.
    public readonly Dictionary<long, Dictionary<int, uint>> BlockKeys = new Dictionary<long, Dictionary<int, uint>>();

    public VmStreamFile(byte[] data) { Data = data; }

    public void AddBlockKey(string methodId, int offset, uint key)
    {
        long position = PositionOf(methodId);
        Dictionary<int, uint> keys;
        if (!BlockKeys.TryGetValue(position, out keys)) BlockKeys[position] = keys = new Dictionary<int, uint>();
        keys[offset] = key;
    }

    // The lines of docs/vm-block-keys.tsv (method token, offset, key, string). idOfToken:
    // the string each replacement body gives the VM, by method token. The string of a line
    // is checked against its key (the hash the compiler gives a 'switch' on a string).
    public int ReadBlockKeys(string[] lines, Dictionary<uint, string> idOfToken)
    {
        int count = 0;
        foreach (string line in lines)
        {
            if (line.Length == 0 || line[0] == '#') continue;
            string[] c = line.Split('\t');
            if (c[0] == "method") continue;
            if (c.Length < 4) throw new FormatException("block key: " + line);
            uint token = Convert.ToUInt32(c[0], 16);
            uint key = Convert.ToUInt32(c[2], 16);
            if (c[3] != "" && VmBlockCipher.StringHash(c[3]) != key) throw new FormatException("block key: " + c[2] + " is not the hash of '" + c[3] + "'");
            string id;
            if (!idOfToken.TryGetValue(token, out id)) throw new FormatException("block key: " + c[0] + " is not a virtualized method");
            AddBlockKey(id, Convert.ToInt32(c[1], 16), key);
            count++;
        }
        return count;
    }

    public VmReader At(long position)
    {
        VmReader r = new VmReader(Data, StreamKey);
        r.Position = checked((int)position);
        return r;
    }

    // VmAscii85.Decode: base 85, big-endian groups, 'z' for four zero bytes.
    public static byte[] DecodeId(string id)
    {
        uint[] weights = { 52200625u, 614125u, 7225u, 85u, 1u };
        List<byte> bytes = new List<byte>();
        int n = 0;
        uint acc = 0;
        foreach (char c in id)
        {
            if (c == 'z' && n == 0) { bytes.AddRange(new byte[4]); continue; }
            if (c < '!' || c > 'u') throw new FormatException("not a method id: " + id);
            acc = checked(acc + weights[n] * (uint)(c - 33));
            n++;
            if (n == 5)
            {
                bytes.Add((byte)(acc >> 24)); bytes.Add((byte)(acc >> 16)); bytes.Add((byte)(acc >> 8)); bytes.Add((byte)acc);
                n = 0; acc = 0;
            }
        }
        if (n == 1) throw new FormatException("not a method id: " + id);
        if (n > 1)
        {
            for (int j = n; j < 5; j++) acc = checked(acc + 84 * weights[j]);
            int drop = 5 - n;
            bytes.Add((byte)(acc >> 24));
            if (drop != 3) { bytes.Add((byte)(acc >> 16)); if (drop != 2) { bytes.Add((byte)(acc >> 8)); if (drop != 1) bytes.Add((byte)acc); } }
        }
        return bytes.ToArray();
    }

    // VirtualMachine.DecodeMethodPosition.
    public static long PositionOf(string id)
    {
        return new VmReader(DecodeId(id), IdKey).ReadInt64();
    }

    // VirtualMachine.LoadMethod: header (ReadMethodHeader), handlers (ReadExceptionHandlers), body (ReadMethodBody).
    public VmMethod ReadMethod(string id)
    {
        VmMethod m = new VmMethod();
        m.Id = id;
        m.Position = PositionOf(id);
        VmReader r = At(m.Position);
        m.Flags = r.ReadByte();
        m.Locals = new int[r.ReadInt16()];
        for (int i = 0; i < m.Locals.Length; i++) m.Locals[i] = r.ReadInt32();
        m.ReturnType = r.ReadInt32();
        m.DeclaringType = r.ReadInt32();
        int count = r.ReadInt16();
        m.ParameterTypes = new int[count];
        m.ParameterFlags = new bool[count];
        for (int i = 0; i < count; i++) { m.ParameterTypes[i] = r.ReadInt32(); m.ParameterFlags[i] = r.ReadBoolean(); }
        m.Name = r.ReadString();
        m.Handlers = new VmHandler[r.ReadInt16()];
        for (int i = 0; i < m.Handlers.Length; i++)
        {
            VmHandler h = new VmHandler();
            h.Kind = r.ReadByte();
            h.CatchType = r.ReadInt32();
            h.FilterStart = r.ReadUInt32();
            h.HandlerStart = r.ReadUInt32();
            h.TryLength = r.ReadUInt32();
            h.TryStart = r.ReadUInt32();
            m.Handlers[i] = h;
        }
        m.Body = r.ReadBytes(r.ReadInt32());
        Split(m, m.Body, 0, m.Instructions);
        return m;
    }

    // VirtualMachine.ExecuteNextInstruction and ReadOperand: an opcode id, then its operand.
    public void Split(VmMethod m, byte[] body, int baseOffset, List<VmInstruction> into)
    {
        VmReader r = new VmReader(body);
        while (r.Position < body.Length)
        {
            VmInstruction ins = new VmInstruction();
            ins.Offset = baseOffset + r.Position;
            int id = r.ReadInt32();
            if (!Opcodes.TryGetValue(id, out ins.Opcode))
            {
                // An encrypted block (docs/pipeline.md): the protected region of a
                // 'finally' the obfuscator added, and the start of that handler. Its length
                // is inside it (CascadeDecryptor.DecryptStream: four bytes of length, then the code, the
                // whole rounded up to four bytes): the block is taken to end at the first
                // position past the start of the handler, a multiple of four bytes from the
                // start, where an opcode of the VM is read again. The bytes are kept as they
                // are, as one instruction.
                VmHandler around = null;
                foreach (VmHandler h in m.Handlers)
                    if (h.TryStart == (uint)ins.Offset && h.Kind == 4 && h.HandlerStart > h.TryStart) around = h;
                int end = -1;
                if (around != null)
                    for (int e = (int)around.HandlerStart; e + 4 <= body.Length && end < 0; e++)
                    {
                        if ((e - ins.Offset + baseOffset) % 4 != 0) continue;
                        VmReader probe = new VmReader(body);
                        probe.Position = e - baseOffset;
                        if (Opcodes.ContainsKey(probe.ReadInt32())) end = e;
                    }
                if (end >= 0)
                {
                    r.Position -= 4;
                    ins.Opcode = EncryptedBlock;
                    ins.Operand = r.ReadBytes(end - ins.Offset);
                    Dictionary<int, uint> keys;
                    uint key;
                    if (BlockKeys.TryGetValue(m.Position, out keys) && keys.TryGetValue(ins.Offset, out key))
                    {
                        Open(m, around, ins, key, into);
                        if (m.Error != null) return;
                        // What is left in clear of the obfuscator's handler: 'endfinally'.
                        VmOpcode close;
                        if (r.Position + 4 > body.Length || !Opcodes.TryGetValue(r.ReadInt32(), out close) || close.OperandType != 2)
                            throw new FormatException(string.Format("{0:X4}: a decrypted block that is not followed by one instruction without operand", ins.Offset));
                        continue;
                    }
                    into.Add(ins);
                    continue;
                }
                m.Error = string.Format("unknown opcode {0} at {1:X4}", id, ins.Offset);
                return;
            }
            switch (ins.Opcode.OperandType)
            {
                case 2: break;
                case -1: break;
                case 10: ins.Operand = unchecked((sbyte)r.ReadByte()); break;
                case 11: case 12: ins.Operand = r.ReadInt32(); break;
                case 0: ins.Operand = r.ReadInt64(); break;
                case 6: case 9: ins.Operand = r.ReadByte(); break;
                case 3: case 8: ins.Operand = r.ReadUInt16(); break;
                case 7: ins.Operand = r.ReadSingle(); break;
                case 5: ins.Operand = r.ReadDouble(); break;
                case 4: ins.Operand = r.ReadUInt32(); break;
                case 1:
                    int[] table = new int[r.ReadInt32()];
                    for (int i = 0; i < table.Length; i++) table[i] = r.ReadInt32();
                    ins.Operand = table;
                    break;
                default:
                    throw new FormatException("operand type " + ins.Opcode.OperandType);
            }
            into.Add(ins);
        }
    }

    // What the VM does at the instruction that opens a block (VirtualMachine.ExecuteVmDecryptBlock): it
    // pops a salt, a key and a block id, decrypts the bytes that follow with them and runs the
    // result, whose offsets go on from the offset of the bytes. The code of the block takes
    // the place of the four opening instructions and of the bytes; the end of the block
    // (two instructions: the block id, and the instruction that closes it) and the 'finally'
    // of the obfuscator around it are dropped.
    private void Open(VmMethod m, VmHandler around, VmInstruction bytes, uint key, List<VmInstruction> into)
    {
        int n = into.Count;
        if (n < 4 || !(into[n - 2].Operand is long) || into[n - 1].Operand != null)
            throw new FormatException(string.Format("{0:X4}: encrypted bytes that do not follow 'id, key, salt, open'", bytes.Offset));
        VmBlock block = new VmBlock();
        block.Offset = bytes.Offset;
        block.End = (int)around.HandlerStart;
        block.Entry = into[n - 4].Offset;
        block.Key = key;
        block.Salt = (long)into[n - 2].Operand;
        block.IdLoad = into[n - 4];
        block.KeyLoad = into[n - 3];
        block.SaltLoad = into[n - 2];
        byte[] code = VmBlockCipher.Decrypt((byte[])bytes.Operand, key, block.Salt);
        if (code == null) throw new FormatException(string.Format("{0:X4}: {1:X8} is not the key of this block", bytes.Offset, key));
        List<VmInstruction> inner = new List<VmInstruction>();
        Split(m, code, bytes.Offset, inner);
        if (m.Error != null) return;
        into.RemoveRange(n - 4, 4);
        int tail = 0;
        foreach (VmInstruction i in inner)
        {
            if (i.Offset < block.End) into.Add(i);
            else tail++;
        }
        if (tail != 2 || inner.Count < 3) throw new FormatException(string.Format("{0:X4}: a decrypted block that does not end with 'id, close'", bytes.Offset));
        List<VmHandler> left = new List<VmHandler>(m.Handlers);
        left.Remove(around);
        m.Handlers = left.ToArray();
        m.Blocks.Add(block);
    }
}
