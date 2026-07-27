using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DataTray.Tools.UniversalBackup;

/// <summary>
/// Streaming authenticated encryption for the <c>.lbak</c> v2 payload. <see cref="System.Security.Cryptography.AesGcm"/>
/// is one-shot (it needs the whole message at once), so a multi-gigabyte payload can't go through it directly.
/// This splits the stream into fixed-size chunks, each sealed with its own nonce + GCM tag — the age/miniLock
/// pattern. Each chunk's counter and an <c>isFinal</c> flag are authenticated as associated data, so reordering,
/// dropping or truncating chunks fails authentication (you can't cut a backup short unnoticed).
///
/// Frame per chunk on the underlying stream: <c>[isFinal:1][plaintextLen:int32][ciphertext:len][tag:16]</c>
/// (GCM ciphertext length equals plaintext length). Nonce = <c>baseNonce(4) ‖ counter(8, big-endian)</c>.
/// </summary>
public static class ChunkedGcm
{
    public const int ChunkSize = 64 * 1024;
    public const int BaseNonceBytes = 4;
    private const int CounterBytes = 8;
    private const int TagBytes = 16;

    public static Stream CreateWriter(Stream inner, byte[] key, byte[] baseNonce) =>
        new WriteStream(inner, key, baseNonce);

    public static Stream CreateReader(Stream inner, byte[] key, byte[] baseNonce) =>
        new ReadStream(inner, key, baseNonce);

    private static void BuildNonce(byte[] baseNonce, ulong counter, Span<byte> nonce)
    {
        baseNonce.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[BaseNonceBytes..], counter);
    }

    private static void BuildAad(ulong counter, bool isFinal, Span<byte> aad)
    {
        BinaryPrimitives.WriteUInt64BigEndian(aad, counter);
        aad[CounterBytes] = (byte)(isFinal ? 1 : 0);
    }

    private sealed class WriteStream(Stream inner, byte[] key, byte[] baseNonce) : Stream
    {
        private readonly AesGcm _aes = new(key, TagBytes);
        private readonly byte[] _buffer = new byte[ChunkSize];
        private int _pos;
        private ulong _counter;
        private bool _finalized;

        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;

        public override void Write(byte[] buffer, int offset, int count) =>
            Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (!buffer.IsEmpty)
            {
                var n = Math.Min(ChunkSize - _pos, buffer.Length);
                buffer[..n].CopyTo(_buffer.AsSpan(_pos));
                _pos += n;
                buffer = buffer[n..];
                if (_pos == ChunkSize)
                {
                    FlushChunk(isFinal: false);
                }
            }
        }

        private void FlushChunk(bool isFinal)
        {
            Span<byte> nonce = stackalloc byte[BaseNonceBytes + CounterBytes];
            Span<byte> aad = stackalloc byte[CounterBytes + 1];
            Span<byte> tag = stackalloc byte[TagBytes];
            BuildNonce(baseNonce, _counter, nonce);
            BuildAad(_counter, isFinal, aad);

            var cipher = new byte[_pos];
            _aes.Encrypt(nonce, _buffer.AsSpan(0, _pos), cipher, tag, aad);

            inner.WriteByte((byte)(isFinal ? 1 : 0));
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(len, _pos);
            inner.Write(len);
            inner.Write(cipher);
            inner.Write(tag);

            _counter++;
            _pos = 0;
        }

        // The final (possibly empty) chunk carries the isFinal marker, so a truncated file is detectable.
        private void WriteFinalChunk()
        {
            if (_finalized)
            {
                return;
            }

            FlushChunk(isFinal: true);
            inner.Flush();
            _finalized = true;
        }

        public override void Flush() { }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                WriteFinalChunk();
                _aes.Dispose();
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class ReadStream(Stream inner, byte[] key, byte[] baseNonce) : Stream
    {
        private readonly AesGcm _aes = new(key, TagBytes);
        private byte[] _plain = [];
        private int _plainPos;
        private ulong _counter;
        private bool _sawFinal;

        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_plainPos >= _plain.Length)
            {
                if (!NextChunk())
                {
                    return 0;
                }
            }

            var n = Math.Min(count, _plain.Length - _plainPos);
            Array.Copy(_plain, _plainPos, buffer, offset, n);
            _plainPos += n;
            return n;
        }

        private bool NextChunk()
        {
            if (_sawFinal)
            {
                return false;
            }

            var header = ReadExact(1 + 4);
            if (header is null)
            {
                // EOF before an isFinal chunk => the file was truncated (or corrupt).
                throw new CryptographicException("Backup payload ended unexpectedly (truncated or corrupt).");
            }

            var isFinal = header[0] == 1;
            var len = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1, 4));
            if (len < 0 || len > ChunkSize)
            {
                throw new CryptographicException("Backup payload is corrupt (bad chunk length).");
            }

            var cipher = ReadExact(len) ?? throw new CryptographicException("Backup payload is corrupt (short chunk).");
            var tag = ReadExact(TagBytes) ?? throw new CryptographicException("Backup payload is corrupt (missing tag).");

            Span<byte> nonce = stackalloc byte[BaseNonceBytes + CounterBytes];
            Span<byte> aad = stackalloc byte[CounterBytes + 1];
            BuildNonce(baseNonce, _counter, nonce);
            BuildAad(_counter, isFinal, aad);

            var plain = new byte[len];
            // Throws AuthenticationTagMismatchException on a wrong passphrase or any tampering/reordering.
            _aes.Decrypt(nonce, cipher, tag, plain, aad);

            _plain = plain;
            _plainPos = 0;
            _counter++;
            _sawFinal = isFinal;

            if (len > 0)
            {
                return true;
            }

            // Empty chunk: the final marker means EOF; an empty non-final chunk just advances.
            return _sawFinal ? false : NextChunk();
        }

        private byte[]? ReadExact(int count)
        {
            var buf = new byte[count];
            var read = 0;
            while (read < count)
            {
                var r = inner.Read(buf, read, count - read);
                if (r == 0)
                {
                    return read == 0 ? null : throw new CryptographicException("Backup payload is corrupt (short read).");
                }

                read += r;
            }

            return buf;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _aes.Dispose();
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override void Flush() { }
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
