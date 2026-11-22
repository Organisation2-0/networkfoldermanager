using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NetworkFolderManager.Server;

/// <summary>
/// Verschlüsseltes Dateiformat (eine Datei = ein "Blob"):
///   "NFB1" | Nonce-Präfix (8 Byte) | Block 0 | Block 1 | ...
/// Jeder Block = AES-256-GCM(max. 64 KiB Klartext) | Tag (16 Byte).
/// Nonce = Präfix | Blocknummer (4 Byte); AAD = Blocknummer (8 Byte) | "letzter Block" (1 Byte).
/// Dadurch kann beliebig gesprungen werden (Video-Streaming, Range-Anfragen) und Abschneiden/Vertauschen fällt auf.
/// </summary>
public static class BlobFormat
{
    public const int ChunkSize = 64 * 1024;
    public const int TagSize = 16;
    public const int HeaderSize = 12;
    private static ReadOnlySpan<byte> Magic => "NFB1"u8;

    public static long ChunkCount(long plainLength) =>
        plainLength == 0 ? 1 : (plainLength + ChunkSize - 1) / ChunkSize;

    internal static void Nonce(Span<byte> nonce, ReadOnlySpan<byte> prefix, long index)
    {
        prefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt32BigEndian(nonce[8..], checked((uint)index));
    }

    internal static void Aad(Span<byte> aad, long index, bool final)
    {
        BinaryPrimitives.WriteInt64BigEndian(aad, index);
        aad[8] = final ? (byte)1 : (byte)0;
    }

    /// <summary>
    /// Liest <paramref name="input"/> vollständig, schreibt ihn verschlüsselt nach <paramref name="path"/>
    /// und gibt die Klartextlänge zurück.
    /// </summary>
    public static async Task<long> WriteAsync(Stream input, string path, byte[] key, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);

        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        RandomNumberGenerator.Fill(header.AsSpan(4, 8));
        var prefix = header.AsSpan(4, 8).ToArray();
        await fs.WriteAsync(header, ct);

        using var gcm = new AesGcm(key, TagSize);
        var current = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var output = new byte[ChunkSize + TagSize];

        var currentLength = await ReadFullAsync(input, current, ct);
        long total = currentLength;
        long index = 0;
        while (true)
        {
            var nextLength = currentLength == ChunkSize ? await ReadFullAsync(input, next, ct) : 0;
            var final = nextLength == 0;
            EncryptChunk(gcm, prefix, index, final, current, currentLength, output);
            await fs.WriteAsync(output.AsMemory(0, currentLength + TagSize), ct);
            if (final)
                break;

            (current, next) = (next, current);
            currentLength = nextLength;
            total += currentLength;
            index++;
        }

        await fs.FlushAsync(ct);
        return total;
    }

    private static void EncryptChunk(AesGcm gcm, byte[] prefix, long index, bool final, byte[] plain, int length, byte[] output)
    {
        Span<byte> nonce = stackalloc byte[12];
        Span<byte> aad = stackalloc byte[9];
        Nonce(nonce, prefix, index);
        Aad(aad, index, final);
        gcm.Encrypt(nonce, plain.AsSpan(0, length), output.AsSpan(0, length), output.AsSpan(length, TagSize), aad);
    }

    private static async Task<int> ReadFullAsync(Stream input, byte[] buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    public static bool HasValidHeader(FileStream fs, out byte[] prefix)
    {
        prefix = new byte[8];
        Span<byte> header = stackalloc byte[HeaderSize];
        fs.Position = 0;
        if (fs.Read(header) != HeaderSize || !header[..4].SequenceEqual(Magic))
            return false;
        header.Slice(4, 8).CopyTo(prefix);
        return true;
    }
}

/// <summary>Entschlüsselnder, frei positionierbarer Lese-Stream für einen Blob.</summary>
public sealed class BlobReadStream : Stream
{
    private readonly FileStream _fs;
    private readonly AesGcm _gcm;
    private readonly byte[] _prefix;
    private readonly long _length;
    private readonly long _chunkCount;
    private readonly byte[] _plain = new byte[BlobFormat.ChunkSize];
    private readonly byte[] _cipher = new byte[BlobFormat.ChunkSize + BlobFormat.TagSize];
    private long _cachedChunk = -1;
    private int _plainLength;
    private long _position;

    public BlobReadStream(string path, byte[] key, long length)
    {
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.RandomAccess);
        if (!BlobFormat.HasValidHeader(_fs, out _prefix))
        {
            _fs.Dispose();
            throw new InvalidDataException("Ungültige verschlüsselte Datei.");
        }
        _gcm = new AesGcm(key, BlobFormat.TagSize);
        _length = length;
        _chunkCount = BlobFormat.ChunkCount(length);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_position >= _length || buffer.Length == 0)
            return 0;

        var chunk = _position / BlobFormat.ChunkSize;
        LoadChunk(chunk);
        var offset = (int)(_position - chunk * BlobFormat.ChunkSize);
        var n = Math.Min(buffer.Length, _plainLength - offset);
        _plain.AsSpan(offset, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Read(buffer.Span));

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Task.FromResult(Read(buffer.AsSpan(offset, count)));

    private void LoadChunk(long index)
    {
        if (index == _cachedChunk)
            return;

        var final = index == _chunkCount - 1;
        var plainLength = final ? (int)(_length - index * BlobFormat.ChunkSize) : BlobFormat.ChunkSize;
        _fs.Position = BlobFormat.HeaderSize + index * (BlobFormat.ChunkSize + BlobFormat.TagSize);
        _fs.ReadExactly(_cipher, 0, plainLength + BlobFormat.TagSize);

        Span<byte> nonce = stackalloc byte[12];
        Span<byte> aad = stackalloc byte[9];
        BlobFormat.Nonce(nonce, _prefix, index);
        BlobFormat.Aad(aad, index, final);
        _gcm.Decrypt(nonce, _cipher.AsSpan(0, plainLength), _cipher.AsSpan(plainLength, BlobFormat.TagSize),
            _plain.AsSpan(0, plainLength), aad);

        _cachedChunk = index;
        _plainLength = plainLength;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fs.Dispose();
            _gcm.Dispose();
        }
        base.Dispose(disposing);
    }
}
