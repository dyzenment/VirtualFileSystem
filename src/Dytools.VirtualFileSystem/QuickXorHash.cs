namespace Dytools.VirtualFileSystem;

/// <summary>
/// Microsoft's QuickXorHash - the only content hash SharePoint and OneDrive for Business report.
/// <para>
/// It lives in core rather than the SharePoint package because the point of computing it is to compare
/// a SharePoint item against something that is not SharePoint. If only that package could produce it,
/// a local file could never be compared with one, which is the whole reason to want it. It is pure
/// arithmetic with no Microsoft dependency - an algorithm like any other, and
/// <see cref="VfsHashAlgorithms.QuickXor"/> already named it here.
/// </para>
/// <para>
/// A 160-bit XOR of the input, each byte folded in at a rotating bit offset, with the length mixed
/// into the tail. Cheap, and a checksum rather than a content identity - it tells two files apart, it
/// does not stand up to anyone constructing a collision on purpose.
/// </para>
/// </summary>
public sealed class QuickXorHash
{
    private const int  WidthInBits    = 160;
    private const int  BitsInLastCell = 32;
    private const byte Shift          = 11;

    private readonly ulong[] _data = new ulong[(WidthInBits - 1) / 64 + 1];
    private long _lengthSoFar;
    private int  _shiftSoFar;

    /// <summary>Folds another span of input into the hash. Call as often as needed, then <see cref="Finish"/>.</summary>
    public void Append(ReadOnlySpan<byte> input)
    {
        if (input.IsEmpty) return;

        var currentShift    = _shiftSoFar;
        var vectorArrayIndex = currentShift / 64;
        var vectorOffset     = currentShift % 64;
        var iterations       = Math.Min(input.Length, WidthInBits);

        for (var i = 0; i < iterations; i++)
        {
            var isLastCell       = vectorArrayIndex == _data.Length - 1;
            var bitsInVectorCell = isLastCell ? BitsInLastCell : 64;

            if (vectorOffset <= bitsInVectorCell - 8)
            {
                for (var j = i; j < input.Length; j += WidthInBits)
                    _data[vectorArrayIndex] ^= (ulong)input[j] << vectorOffset;
            }
            else
            {
                var index1 = vectorArrayIndex;
                var index2 = isLastCell ? 0 : vectorArrayIndex + 1;
                var low    = bitsInVectorCell - vectorOffset;

                byte xoredByte = 0;
                for (var j = i; j < input.Length; j += WidthInBits)
                    xoredByte ^= input[j];

                _data[index1] ^= (ulong)xoredByte << vectorOffset;
                _data[index2] ^= (ulong)xoredByte >> low;
            }

            vectorOffset += Shift;
            while (vectorOffset >= bitsInVectorCell)
            {
                vectorArrayIndex = isLastCell ? 0 : vectorArrayIndex + 1;
                vectorOffset    -= bitsInVectorCell;
            }
        }

        _shiftSoFar  = (int)((_shiftSoFar + Shift * (input.Length % WidthInBits)) % WidthInBits);
        _lengthSoFar += input.Length;
    }

    /// <summary>The finished hash, base64 encoded - the form Graph reports it in.</summary>
    public string Finish()
    {
        var bytes = new byte[(WidthInBits - 1) / 8 + 1];
        Buffer.BlockCopy(_data, 0, bytes, 0, bytes.Length);

        // The length is mixed into the tail so that inputs differing only in trailing zero bytes do
        // not collide - an XOR alone would not notice them.
        var length = BitConverter.GetBytes(_lengthSoFar);
        for (var i = 0; i < length.Length; i++)
            bytes[WidthInBits / 8 - length.Length + i] ^= length[i];

        return Convert.ToBase64String(bytes);
    }

    /// <summary>Hashes a whole stream, reading it to the end.</summary>
    public static async Task<string> ComputeAsync(Stream content, CancellationToken ct = default)
    {
        var hash   = new QuickXorHash();
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            int read;
            while ((read = await content.ReadAsync(buffer, ct)) > 0)
                hash.Append(buffer.AsSpan(0, read));
            return hash.Finish();
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }
}
