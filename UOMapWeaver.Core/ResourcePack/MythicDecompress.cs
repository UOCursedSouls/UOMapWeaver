namespace UOMapWeaver.Core.ResourcePack;

/// <summary>
/// Decompresses data in UO's "Mythic" format (BWT + Move-To-Front coding).
/// Used for gumpartLegacyMUL.uop entries with compression flag 3.
/// Ported from UOFiddler/Ultima/Helpers/MythicDecompress.cs + MoveToFront.cs
/// </summary>
public static class MythicDecompress
{
    public static byte[] Decompress(byte[] buffer)
    {
        using var reader = new BinaryReader(new MemoryStream(buffer));
        reader.ReadUInt32(); // header XOR 0x8E2C9A3D = data length

        var list = reader.ReadBytes((int)(reader.BaseStream.Length - 4));
        return BwtDecompress(MtfDecode(list));
    }

    private static byte[] MtfDecode(byte[] input)
    {
        Span<byte> symbols = stackalloc byte[256];
        var output = new byte[input.Length];

        for (int i = 0; i < 256; i++)
            symbols[i] = (byte)i;

        for (int i = 0; i < input.Length; i++)
        {
            int ind = input[i];
            output[i] = symbols[ind];

            // Move element at ind to front
            var element = symbols[ind];
            for (int j = ind; j > 0; j--)
                symbols[j] = symbols[j - 1];
            symbols[0] = element;
        }

        return output;
    }

    private static byte[] BwtDecompress(Span<byte> input)
    {
        Span<byte> symbolTable = stackalloc byte[256];
        Span<byte> frequency = stackalloc byte[256];
        Span<int> partialInput = stackalloc int[256 * 3];
        partialInput.Clear();

        for (int i = 0; i < 256; i++)
            symbolTable[i] = (byte)i;

        input.Slice(0, 1024).CopyTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(partialInput));

        int sum = 0;
        for (int i = 0; i < 256; i++)
            sum += partialInput[i];

        if (sum == 0)
            return [];

        var output = new byte[sum];
        int nonZeroCount = 0;

        for (int i = 0; i < 256; i++)
        {
            if (partialInput[i] != 0)
                nonZeroCount++;
        }

        // Build frequency table
        {
            Span<int> tmp = stackalloc int[256];
            partialInput.Slice(0, 256).CopyTo(tmp);

            for (int i = 0; i < 256; i++)
            {
                uint value = 0;
                byte index = 0;

                for (int j = 0; j < 256; j++)
                {
                    if (tmp[j] > value)
                    {
                        index = (byte)j;
                        value = (uint)tmp[j];
                    }
                }

                if (value == 0) break;
                frequency[i] = index;
                tmp[index] = 0;
            }
        }

        for (int i = 0, m = 0; i < nonZeroCount; ++i)
        {
            var freq = frequency[i];
            symbolTable[input[m + 1024]] = freq;
            partialInput[freq + 256] = m + 1;
            m += partialInput[freq];
            partialInput[freq + 512] = m;
        }

        var val = symbolTable[0];
        int count = 0;

        do
        {
            ref var firstValRef = ref partialInput[val + 256];
            output[count] = val;

            if (firstValRef < partialInput[val + 512])
            {
                var idx = input[firstValRef + 1024];
                firstValRef++;

                if (idx != 0)
                {
                    // ShiftLeft
                    for (int ii = 0; ii < idx; ii++)
                        symbolTable[ii] = symbolTable[ii + 1];

                    symbolTable[idx] = val;
                    val = symbolTable[0];
                }
            }
            else if (nonZeroCount-- > 0)
            {
                for (int ii = 0; ii < nonZeroCount; ii++)
                    symbolTable[ii] = symbolTable[ii + 1];

                val = symbolTable[0];
            }

            count++;
        } while (count < sum);

        return output;
    }
}
