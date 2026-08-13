using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// A reader for compiled Lua 5.0 chunks, enough of one to answer which
/// globals a script reads and which it defines.
/// </summary>
/// <remarks>
/// Scanning a chunk for identifier-shaped strings finds every API name a
/// script calls, but it also finds table keys, sound names and the string
/// literals passed to <c>SetMemoryPoolSize</c> - and in the stock missions the
/// literals outnumber the calls by an order of magnitude. The distinction is
/// not in the constant table, it is in the code: a call to an engine function
/// is a GETGLOBAL against a constant, and a function the scripts define
/// themselves is a SETGLOBAL against one.
///
/// So the interesting set is "read by some script, written by none" - that is
/// precisely the surface the engine is expected to provide, and it cannot be
/// obtained without decoding instructions.
/// </remarks>
internal static class LuaBytecode
{
    // Lua 5.0 opcode numbering; only the two that touch globals matter here.
    const int OpGetGlobal = 5;
    const int OpSetGlobal = 7;

    const byte TypeNil = 0;
    const byte TypeNumber = 3;
    const byte TypeString = 4;

    sealed class Reader
    {
        public byte[] Data;
        public int At;
        public int End;
        public int IntSize = 4;
        public int SizeTSize = 4;
        public int NumberSize = 8;
        public bool BigEndian;

        public byte Byte()
        {
            if (At >= End) throw new FormatException("truncated");
            return Data[At++];
        }

        public void Skip(long count)
        {
            if (count < 0 || At + count > End) throw new FormatException("truncated");
            At += (int)count;
        }

        public long Integer(int width)
        {
            if (At + width > End) throw new FormatException("truncated");

            long value = 0;
            for (int i = 0; i < width; ++i)
            {
                int shift = BigEndian ? (width - 1 - i) * 8 : i * 8;
                value |= (long)Data[At + i] << shift;
            }
            At += width;
            return value;
        }

        public int Int() => (int)Integer(IntSize);
        public long Size() => Integer(SizeTSize);

        /// <summary>A length-prefixed string; the length counts the terminator.</summary>
        public string String()
        {
            long length = Size();
            if (length == 0) return null;
            if (length < 0 || At + length > End) throw new FormatException("truncated");

            string text = Encoding.ASCII.GetString(Data, At, (int)length - 1);
            At += (int)length;
            return text;
        }
    }

    /// <summary>
    /// Decode one chunk, adding every global it reads to <paramref name="reads"/>
    /// and every global it defines to <paramref name="writes"/>.
    /// </summary>
    /// <returns>Whether the chunk decoded as Lua 5.0 bytecode at all.</returns>
    public static bool Read(byte[] data, int at, int size, ISet<string> reads, ISet<string> writes)
    {
        if (size < 12) return false;
        if (data[at] != 0x1B || data[at + 1] != 'L' || data[at + 2] != 'u' || data[at + 3] != 'a') return false;
        if (data[at + 4] != 0x50) return false;

        var reader = new Reader { Data = data, At = at + 5, End = at + size };

        // Sizes come out of the header rather than being assumed: the format
        // is defined by whatever compiler produced it, and guessing wrong
        // fails silently by decoding garbage rather than by throwing.
        reader.BigEndian = reader.Byte() == 0;
        reader.IntSize = reader.Byte();
        reader.SizeTSize = reader.Byte();
        int instructionSize = reader.Byte();

        // SIZE_OP, SIZE_A, SIZE_B, SIZE_C - the instruction field widths.
        int sizeOp = reader.Byte();
        int sizeA = reader.Byte();
        int sizeB = reader.Byte();
        int sizeC = reader.Byte();

        reader.NumberSize = reader.Byte();
        reader.Skip(reader.NumberSize); // the format's sample number

        if (instructionSize != 4 || sizeOp != 6) return false;

        try
        {
            ReadFunction(reader, instructionSize, sizeOp, sizeA, sizeB, sizeC, reads, writes, 0);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    static void ReadFunction(Reader r, int instructionSize, int sizeOp, int sizeA, int sizeB, int sizeC,
                             ISet<string> reads, ISet<string> writes, int depth)
    {
        if (depth > 64) throw new FormatException("nested too deep");

        r.String();      // source
        r.Int();         // line defined
        r.Byte();        // upvalue count
        r.Byte();        // parameter count
        r.Byte();        // is vararg
        r.Byte();        // max stack size

        // Line info.
        r.Skip((long)r.Int() * r.IntSize);

        // Local variables: name, start pc, end pc.
        int locals = r.Int();
        for (int i = 0; i < locals; ++i)
        {
            r.String();
            r.Int();
            r.Int();
        }

        // Upvalue names.
        int upvalues = r.Int();
        for (int i = 0; i < upvalues; ++i) r.String();

        // Constants. Only the strings are kept - they are what the global
        // instructions index into.
        int constantCount = r.Int();
        var constants = new string[constantCount];
        for (int i = 0; i < constantCount; ++i)
        {
            byte type = r.Byte();
            switch (type)
            {
                case TypeNil: break;
                case TypeNumber: r.Skip(r.NumberSize); break;
                case TypeString: constants[i] = r.String(); break;
                default: throw new FormatException($"constant type {type}");
            }
        }

        // Nested functions come before the code in Lua 5.0's layout.
        int protos = r.Int();
        for (int i = 0; i < protos; ++i)
        {
            ReadFunction(r, instructionSize, sizeOp, sizeA, sizeB, sizeC, reads, writes, depth + 1);
        }

        // Code. GETGLOBAL and SETGLOBAL are iABx: the constant index is the
        // Bx field, which sits above the opcode and the A field.
        int code = r.Int();
        int bxShift = sizeOp + sizeA;
        int bxMask = (1 << (sizeB + sizeC)) - 1;
        int opMask = (1 << sizeOp) - 1;

        for (int i = 0; i < code; ++i)
        {
            uint instruction = (uint)r.Integer(instructionSize);
            int op = (int)(instruction & opMask);
            if (op != OpGetGlobal && op != OpSetGlobal) continue;

            int index = (int)((instruction >> bxShift) & bxMask);
            if (index < 0 || index >= constantCount) continue;

            string name = constants[index];
            if (name == null) continue;

            if (op == OpGetGlobal) reads.Add(name);
            else writes.Add(name);
        }
    }
}
