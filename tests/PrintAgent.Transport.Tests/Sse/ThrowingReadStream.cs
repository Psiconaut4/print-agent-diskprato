using System.Text;

namespace PrintAgent.Transport.Tests.Sse;

/// <summary>
/// Stream que devolve um texto inicial e depois lança <see cref="IOException"/>
/// na próxima leitura — simula a conexão sendo resetada no meio da leitura
/// (ex.: erro de socket 10054), o cenário que derrubava o <c>Worker</c>
/// inteiro antes de <see cref="Transport.Sse.SseStreamClient"/> aprender a
/// tratar esse erro como qualquer outra queda de conexão.
/// </summary>
internal sealed class ThrowingReadStream : Stream
{
    private readonly byte[] _initial;
    private int _offset;

    public ThrowingReadStream(string initialText)
    {
        _initial = Encoding.UTF8.GetBytes(initialText);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset < _initial.Length)
        {
            var n = Math.Min(buffer.Length, _initial.Length - _offset);
            _initial.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return ValueTask.FromResult(n);
        }

        throw new IOException("Simulated connection reset (10054).", new System.Net.Sockets.SocketException(10054));
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
