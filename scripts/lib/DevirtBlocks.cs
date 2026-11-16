// Decrypts the encrypted blocks of the virtual code (docs/pipeline.md).
// Every step below is the one the decompiled obfuscator does:
//   VirtualMachine.ExecuteVmDecryptBlock / PopBlockKey    the key is the boxed value kept by the code,
//                                              as bytes (SignatureEncoder.GetBytes: big-endian)
//   CascadeCipher (constructor)                      five ciphers, key (10 bytes) and vector (4 bytes)
//                                              of each taken in turn from FallbackPbkdf2DeriveBytes = PBKDF2,
//                                              HMAC-SHA1, one iteration, salt = the 64-bit
//                                              constant, little-endian
//   CascadeCipher.Transform(decrypt)                  ciphers 4 to 0: decrypt, encrypt, decrypt, ...
//   CascadeCbcAlgorithm.CascadeCbcTransform                            chaining of 4-byte blocks (CBC)
//   Skip32Algorithm.Skip32Transform                            the 4-byte block cipher (24 rounds)
//   CascadeDecryptor.DecryptStream                           first four bytes decrypted: length of the code
// C# 5: compiled by Add-Type under Windows PowerShell 5.1.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

public static class VmBlockCipher
{
    // Skip32Algorithm.fTable.
    private static readonly byte[] F = {
        163, 215, 9, 131, 248, 72, 246, 244, 179, 33, 21, 120, 153, 177, 175, 249,
        231, 45, 77, 138, 206, 76, 202, 46, 82, 149, 217, 30, 78, 56, 68, 40,
        10, 223, 2, 160, 23, 241, 96, 104, 18, 183, 122, 195, 233, 250, 61, 83,
        150, 132, 107, 186, 242, 99, 154, 25, 124, 174, 229, 245, 247, 22, 106, 162,
        57, 182, 123, 15, 193, 147, 129, 27, 238, 180, 26, 234, 208, 145, 47, 184,
        85, 185, 218, 133, 63, 65, 191, 224, 90, 88, 128, 95, 102, 11, 216, 144,
        53, 213, 192, 167, 51, 6, 101, 105, 69, 0, 148, 86, 109, 152, 155, 118,
        151, 252, 178, 194, 176, 254, 219, 32, 225, 235, 214, 228, 221, 71, 74, 29,
        66, 237, 158, 110, 73, 60, 205, 67, 39, 210, 7, 212, 222, 199, 103, 24,
        137, 203, 48, 31, 141, 198, 143, 170, 200, 116, 220, 201, 93, 92, 49, 164,
        112, 136, 97, 44, 159, 13, 43, 135, 80, 130, 84, 100, 38, 125, 3, 64,
        52, 75, 28, 115, 209, 196, 253, 59, 204, 251, 127, 171, 230, 62, 91, 165,
        173, 4, 35, 156, 20, 81, 34, 240, 41, 121, 113, 126, 255, 140, 14, 226,
        12, 239, 188, 114, 117, 111, 55, 161, 236, 211, 142, 98, 139, 134, 16, 232,
        8, 119, 17, 190, 146, 79, 36, 197, 50, 54, 157, 207, 243, 166, 187, 172,
        94, 108, 169, 19, 87, 37, 181, 227, 189, 168, 58, 1, 5, 89, 42, 70 };

    // Skip32Algorithm.G: the key is ten bytes at k + ko.
    private static int G(byte[] k, int ko, int round, int w)
    {
        int b = (w >> 8) & 0xFF, b2 = w & 0xFF;
        int r = (4 * round) % 10;
        int b3 = F[b2 ^ k[ko + r]] ^ b;
        int b4 = F[b3 ^ k[ko + (r + 1) % 10]] ^ b2;
        int b5 = F[b4 ^ k[ko + (r + 2) % 10]] ^ b3;
        int b6 = F[b5 ^ k[ko + (r + 3) % 10]] ^ b4;
        return (b5 << 8) + b6;
    }

    // Skip32Algorithm.ProcessBlock on a 32-bit value read big-endian; returns it written the same way.
    private static uint Block(byte[] k, int ko, uint v, bool encrypt)
    {
        int step, round;
        if (encrypt) { step = 1; round = 0; } else { step = -1; round = 23; }
        int a = (int)(v >> 16), b = (int)(v & 0xFFFF);
        for (int i = 0; i < 12; i++)
        {
            b ^= (G(k, ko, round, a) ^ round) & 0xFFFF;
            round += step;
            a ^= (G(k, ko, round, b) ^ round) & 0xFFFF;
            round += step;
        }
        return ((uint)b << 16) | (uint)a;
    }

    // SHA-1 compression of one block of sixteen words.
    private static void Compress(uint[] h, int ho, uint[] w, uint[] into, int io)
    {
        for (int t = 16; t < 80; t++)
        {
            uint x = w[t - 3] ^ w[t - 8] ^ w[t - 14] ^ w[t - 16];
            w[t] = (x << 1) | (x >> 31);
        }
        uint a = h[ho], b = h[ho + 1], c = h[ho + 2], d = h[ho + 3], e = h[ho + 4];
        for (int t = 0; t < 20; t++)
        {
            uint tmp = ((a << 5) | (a >> 27)) + ((b & c) | (~b & d)) + e + 0x5A827999u + w[t];
            e = d; d = c; c = (b << 30) | (b >> 2); b = a; a = tmp;
        }
        for (int t = 20; t < 40; t++)
        {
            uint tmp = ((a << 5) | (a >> 27)) + (b ^ c ^ d) + e + 0x6ED9EBA1u + w[t];
            e = d; d = c; c = (b << 30) | (b >> 2); b = a; a = tmp;
        }
        for (int t = 40; t < 60; t++)
        {
            uint tmp = ((a << 5) | (a >> 27)) + ((b & c) | (b & d) | (c & d)) + e + 0x8F1BBCDCu + w[t];
            e = d; d = c; c = (b << 30) | (b >> 2); b = a; a = tmp;
        }
        for (int t = 60; t < 80; t++)
        {
            uint tmp = ((a << 5) | (a >> 27)) + (b ^ c ^ d) + e + 0xCA62C1D6u + w[t];
            e = d; d = c; c = (b << 30) | (b >> 2); b = a; a = tmp;
        }
        into[io] = h[ho] + a; into[io + 1] = h[ho + 1] + b; into[io + 2] = h[ho + 2] + c;
        into[io + 3] = h[ho + 3] + d; into[io + 4] = h[ho + 4] + e;
    }

    private static readonly uint[] Init = { 0x67452301u, 0xEFCDAB89u, 0x98BADCFEu, 0x10325476u, 0xC3D2E1F0u };

    // Working memory of one thread.
    public sealed class Scratch
    {
        internal readonly uint[] W = new uint[80];
        internal readonly uint[] Pads = new uint[10];      // inner and outer state after the key block
        internal readonly uint[] T = new uint[5];
        internal readonly uint[] Out = new uint[5];
        public readonly byte[] Keys = new byte[80];        // PBKDF2 output; 70 bytes are used
    }

    // HMAC-SHA1 states after the key block. The key is the 32-bit value, big-endian.
    private static void Prepare(uint key, Scratch s)
    {
        uint[] w = s.W;
        w[0] = key ^ 0x36363636u;
        for (int i = 1; i < 16; i++) w[i] = 0x36363636u;
        Compress(Init, 0, w, s.Pads, 0);
        w[0] = key ^ 0x5C5C5C5Cu;
        for (int i = 1; i < 16; i++) w[i] = 0x5C5C5C5Cu;
        Compress(Init, 0, w, s.Pads, 5);
    }

    // PBKDF2 with one iteration: block i = HMAC(key, salt | i). saltHigh, saltLow: the eight
    // bytes of the salt read as two big-endian words.
    private static void Derive(uint saltHigh, uint saltLow, Scratch s)
    {
        uint[] w = s.W;
        for (uint i = 1; i <= 4; i++)
        {
            w[0] = saltHigh; w[1] = saltLow; w[2] = i; w[3] = 0x80000000u;
            for (int j = 4; j < 15; j++) w[j] = 0;
            w[15] = (64 + 12) * 8;
            Compress(s.Pads, 0, w, s.T, 0);
            w[0] = s.T[0]; w[1] = s.T[1]; w[2] = s.T[2]; w[3] = s.T[3]; w[4] = s.T[4]; w[5] = 0x80000000u;
            for (int j = 6; j < 15; j++) w[j] = 0;
            w[15] = (64 + 20) * 8;
            Compress(s.Pads, 5, w, s.Out, 0);
            int o = (int)(i - 1) * 20;
            for (int j = 0; j < 5; j++)
            {
                uint v = s.Out[j];
                s.Keys[o + 4 * j] = (byte)(v >> 24); s.Keys[o + 4 * j + 1] = (byte)(v >> 16);
                s.Keys[o + 4 * j + 2] = (byte)(v >> 8); s.Keys[o + 4 * j + 3] = (byte)v;
            }
        }
    }

    private static uint Vector(byte[] k, int o)
    {
        return ((uint)k[o] << 24) | ((uint)k[o + 1] << 16) | ((uint)k[o + 2] << 8) | k[o + 3];
    }

    private static void SaltWords(long salt, out uint high, out uint low)
    {
        // CascadeCipher.WriteInt64 writes the salt little-endian; SHA-1 reads words big-endian.
        uint a = (uint)salt, b = (uint)((ulong)salt >> 32);
        high = (a << 24) | ((a & 0xFF00) << 8) | ((a >> 8) & 0xFF00) | (a >> 24);
        low = (b << 24) | ((b & 0xFF00) << 8) | ((b >> 8) & 0xFF00) | (b >> 24);
    }

    // The first four bytes of a block, decrypted: the length of its code (little-endian).
    private static int FirstWord(uint first, Scratch s)
    {
        byte[] k = s.Keys;
        // Cipher n has its key at 14 n and its vector at 14 n + 10.
        uint v = Block(k, 56, first, false) ^ Vector(k, 66);
        v = Block(k, 42, v ^ Vector(k, 52), true);
        v = Block(k, 28, v, false) ^ Vector(k, 38);
        v = Block(k, 14, v ^ Vector(k, 24), true);
        v = Block(k, 0, v, false) ^ Vector(k, 10);
        return (int)((v >> 24) | ((v >> 8) & 0xFF00) | ((v << 8) & 0xFF0000) | (v << 24));
    }

    // CascadeDecryptor.DecryptStream on the bytes of a block: null when the key is not the one of the block
    // (the length it reads does not give the size of the block).
    public static byte[] Decrypt(byte[] block, uint key, long salt)
    {
        Scratch s = new Scratch();
        Prepare(key, s);
        uint high, low;
        SaltWords(salt, out high, out low);
        Derive(high, low, s);
        int words = block.Length / 4;
        uint[] data = new uint[words];
        for (int i = 0; i < words; i++) data[i] = Vector(block, 4 * i);
        for (int n = 4; n >= 0; n--)
        {
            uint previous = Vector(s.Keys, 14 * n + 10);
            bool encrypt = n % 2 == 1;
            for (int i = 0; i < words; i++)
            {
                if (encrypt)
                {
                    data[i] = Block(s.Keys, 14 * n, data[i] ^ previous, true);
                    previous = data[i];
                }
                else
                {
                    uint saved = data[i];
                    data[i] = Block(s.Keys, 14 * n, data[i], false) ^ previous;
                    previous = saved;
                }
            }
        }
        byte[] plain = new byte[block.Length];
        for (int i = 0; i < words; i++)
        {
            plain[4 * i] = (byte)(data[i] >> 24); plain[4 * i + 1] = (byte)(data[i] >> 16);
            plain[4 * i + 2] = (byte)(data[i] >> 8); plain[4 * i + 3] = (byte)data[i];
        }
        int length = plain[0] | (plain[1] << 8) | (plain[2] << 16) | (plain[3] << 24);
        if (length < 0 || length > block.Length - 4 || (length + 4 + 3) / 4 * 4 != block.Length) return null;
        byte[] code = new byte[length];
        Buffer.BlockCopy(plain, 4, code, 0, length);
        return code;
    }

    // Every 32-bit key in [from, to] whose first decrypted word gives the size of a block:
    // (block index, key) pairs. About four false keys per block are expected over the whole
    // range (four lengths give the same rounded size); Decrypt and the reading of the code
    // tell them apart. progress is called with the number of keys done, from any thread.
    public static List<KeyValuePair<int, uint>> Search(byte[][] blocks, long[] salts, uint from, uint to, Action<long> progress)
    {
        int n = blocks.Length;
        uint[] first = new uint[n], high = new uint[n], low = new uint[n];
        for (int i = 0; i < n; i++)
        {
            first[i] = Vector(blocks[i], 0);
            SaltWords(salts[i], out high[i], out low[i]);
        }
        List<KeyValuePair<int, uint>> found = new List<KeyValuePair<int, uint>>();
        long done = 0;
        const int Chunk = 1 << 16;
        long chunks = ((long)to - from) / Chunk + 1;
        Parallel.For(0L, chunks, () => new Scratch(), (c, state, s) =>
        {
            long start = from + c * Chunk;
            long end = Math.Min(start + Chunk - 1, to);
            for (long key = start; key <= end; key++)
            {
                Prepare((uint)key, s);
                for (int i = 0; i < n; i++)
                {
                    Derive(high[i], low[i], s);
                    int length = FirstWord(first[i], s);
                    if (length >= 0 && length <= blocks[i].Length - 4 && (length + 4 + 3) / 4 * 4 == blocks[i].Length)
                        lock (found) found.Add(new KeyValuePair<int, uint>(i, (uint)key));
                }
            }
            long total = Interlocked.Add(ref done, end - start + 1);
            if (progress != null && (total >> 16) % 512 == 0) progress(total);
            return s;
        }, s => { });
        return found;
    }

    // The hash the compiler gives a 'switch' on a string (ComputeStringHash): FNV-1a, 32 bits,
    // on the UTF-16 code units.
    public static uint StringHash(string text)
    {
        uint h = 2166136261u;
        foreach (char c in text) h = unchecked((h ^ c) * 16777619u);
        return h;
    }
}
