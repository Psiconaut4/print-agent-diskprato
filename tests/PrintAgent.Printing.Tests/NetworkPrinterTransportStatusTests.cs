using System.Net;
using System.Net.Sockets;
using PrintAgent.Printing;

namespace PrintAgent.Printing.Tests;

/// <summary>
/// Testa <see cref="NetworkPrinterTransport.QueryStatusAsync"/> (DLE EOT,
/// §5.3 do plano) contra um servidor TCP fake que responde às consultas
/// como uma impressora ESC/POS responderia.
/// </summary>
public sealed class NetworkPrinterTransportStatusTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task QueryStatusAsync_GeneralStatusOnlineAndPaperOk_ReturnsReady()
    {
        using var server = DleEotServer.Start(generalStatusByte: 0x12, paperStatusByte: 0x12);
        var transport = new NetworkPrinterTransport("127.0.0.1", server.Port);

        var status = await transport.QueryStatusAsync(TimeoutCt());

        Assert.Equal(PrinterStatus.Ready, status);
    }

    [Fact]
    public async Task QueryStatusAsync_OfflineBitSet_ReturnsOffline()
    {
        // bit 3 (0x08) ligado = offline.
        using var server = DleEotServer.Start(generalStatusByte: 0x1A, paperStatusByte: 0x12);
        var transport = new NetworkPrinterTransport("127.0.0.1", server.Port);

        var status = await transport.QueryStatusAsync(TimeoutCt());

        Assert.Equal(PrinterStatus.Offline, status);
    }

    [Fact]
    public async Task QueryStatusAsync_PaperOutBitSet_ReturnsPaperOut()
    {
        // bits 5/7 (0x60) ligados no status de papel = fim de papel.
        using var server = DleEotServer.Start(generalStatusByte: 0x12, paperStatusByte: 0x72);
        var transport = new NetworkPrinterTransport("127.0.0.1", server.Port);

        var status = await transport.QueryStatusAsync(TimeoutCt());

        Assert.Equal(PrinterStatus.PaperOut, status);
    }

    [Fact]
    public async Task QueryStatusAsync_NothingListening_ReturnsUnknownWithoutThrowing()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var transport = new NetworkPrinterTransport("127.0.0.1", port);

        var status = await transport.QueryStatusAsync(TimeoutCt());

        Assert.Equal(PrinterStatus.Unknown, status);
    }

    private static CancellationToken TimeoutCt() => new CancellationTokenSource(TestTimeout).Token;

    /// <summary>Servidor fake que responde a `DLE EOT n` com um byte fixo por n.</summary>
    private sealed class DleEotServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        private readonly byte _generalStatusByte;
        private readonly byte _paperStatusByte;

        public int Port { get; }

        private DleEotServer(TcpListener listener, byte generalStatusByte, byte paperStatusByte)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _generalStatusByte = generalStatusByte;
            _paperStatusByte = paperStatusByte;
            _loop = Task.Run(RunAsync);
        }

        public static DleEotServer Start(byte generalStatusByte, byte paperStatusByte)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new DleEotServer(listener, generalStatusByte, paperStatusByte);
        }

        private async Task RunAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                using var stream = client.GetStream();
                var command = new byte[3];

                while (!_cts.IsCancellationRequested)
                {
                    var totalRead = 0;
                    while (totalRead < 3)
                    {
                        var read = await stream.ReadAsync(command.AsMemory(totalRead, 3 - totalRead), _cts.Token).ConfigureAwait(false);
                        if (read == 0) return; // cliente fechou
                        totalRead += read;
                    }

                    if (command[0] == 0x10 && command[1] == 0x04)
                    {
                        var response = command[2] switch
                        {
                            1 => _generalStatusByte,
                            4 => _paperStatusByte,
                            _ => (byte)0x12,
                        };
                        await stream.WriteAsync(new[] { response }, _cts.Token).ConfigureAwait(false);
                        await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { /* encerramento do teste */ }
            catch (ObjectDisposedException) { /* listener parado */ }
            catch (IOException) { /* cliente fechou abruptamente */ }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
            _cts.Dispose();
        }
    }
}
