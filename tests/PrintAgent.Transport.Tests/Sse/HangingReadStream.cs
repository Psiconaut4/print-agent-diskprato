using System.Text;

namespace PrintAgent.Transport.Tests.Sse;

/// <summary>
/// Stream que devolve um texto inicial e depois "pendura" (nunca completa)
/// até o CancellationToken passado a <c>ReadAsync</c> ser cancelado —
/// simula uma conexão SSE aberta e ociosa (sem frames novos), o cenário que
/// o watchdog de 90s existe para detectar.
/// </summary>
internal sealed class HangingReadStream : Stream
{
    private readonly byte[] _initial;
    private int _offset;

    public HangingReadStream(string initialText)
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

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_offset < _initial.Length)
        {
            var n = Math.Min(buffer.Length, _initial.Length - _offset);
            _initial.AsSpan(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return n;
        }

        var tcs = new TaskCompletionSource<int>();
        await using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
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
