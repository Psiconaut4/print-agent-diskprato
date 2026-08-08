using System.Net;
using System.Net.Sockets;
using PrintAgent.Contracts;
using PrintAgent.Printing;

namespace PrintAgent.Printing.Tests;

/// <summary>
/// Testa <see cref="NetworkPrinterTransport"/> contra um <see cref="TcpListener"/>
/// falso rodando em 127.0.0.1 numa porta aleatória, no próprio processo de
/// teste. Não requer nenhuma impressora real nem fila do Windows.
/// </summary>
public sealed class NetworkPrinterTransportTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SendAsync_DeliversExactBytes()
    {
        using var fake = FakePrinterServer.Start();
        var transport = new NetworkPrinterTransport("127.0.0.1", fake.Port);

        var payload = new byte[] { 0x1B, 0x40, (byte)'ç', (byte)'ã', (byte)'o', 0x0A };
        var result = await transport.SendAsync(payload, TimeoutToken());

        Assert.True(result.Success);
        var received = await fake.WaitForNextJobAsync(TestTimeout);
        Assert.Equal(payload, received);
    }

    [Fact]
    public async Task SendAsync_ClosesConnectionAfterEachJob()
    {
        using var fake = FakePrinterServer.Start();
        var transport = new NetworkPrinterTransport("127.0.0.1", fake.Port);

        var payload = new byte[] { 0x1B, 0x40 };
        var result = await transport.SendAsync(payload, TimeoutToken());

        Assert.True(result.Success);
        // O servidor fake só marca o job como "concluído" (WaitForNextJobAsync)
        // depois de ler até EOF, ou seja, depois que o cliente fechou o socket.
        await fake.WaitForNextJobAsync(TestTimeout);
        Assert.True(fake.LastConnectionClosedByClient);
    }

    [Fact]
    public async Task SendAsync_SendsAnotherJobOnANewConnection()
    {
        using var fake = FakePrinterServer.Start();
        var transport = new NetworkPrinterTransport("127.0.0.1", fake.Port);

        var first = new byte[] { 0x01 };
        var second = new byte[] { 0x02, 0x03 };

        Assert.True((await transport.SendAsync(first, TimeoutToken())).Success);
        Assert.Equal(first, await fake.WaitForNextJobAsync(TestTimeout));

        Assert.True((await transport.SendAsync(second, TimeoutToken())).Success);
        Assert.Equal(second, await fake.WaitForNextJobAsync(TestTimeout));

        Assert.Equal(2, fake.TotalConnectionsAccepted);
    }

    [Fact]
    public async Task SendAsync_NothingListening_ReturnsRetryableWithoutThrowing()
    {
        var port = GetFreeLoopbackPort();
        var transport = new NetworkPrinterTransport("127.0.0.1", port);

        var result = await transport.SendAsync([0x1B, 0x40], TimeoutToken());

        Assert.False(result.Success);
        Assert.True(result.IsRetryable);
        Assert.NotNull(result.ErrorCode);
        Assert.True(
            result.ErrorCode is PrinterErrorCode.Printer_busy or PrinterErrorCode.Printer_offline,
            $"Esperado printer_busy ou printer_offline, veio {result.ErrorCode}.");
    }

    [Fact]
    public async Task SendAsync_SecondSimultaneousConnectionRefused_ReturnsRetryableBusy()
    {
        // Simula uma impressora RAW/JetDirect: aceita uma conexão (o "outro
        // PDV" imprimindo), então para de escutar enquanto aquele job está
        // em andamento — qualquer tentativa de conexão nesse intervalo é
        // recusada pelo SO, exatamente como uma impressora ocupada.
        using var busyListener = new TcpListener(IPAddress.Loopback, 0);
        busyListener.Start();
        var port = ((IPEndPoint)busyListener.LocalEndpoint).Port;

        using var occupyingClient = new TcpClient();
        var acceptTask = busyListener.AcceptTcpClientAsync();
        await occupyingClient.ConnectAsync(IPAddress.Loopback, port);
        using var occupyingServerSide = await acceptTask.WaitAsync(TestTimeout);

        // Ninguém mais vai aceitar conexões nesta porta a partir de agora.
        busyListener.Stop();

        var transport = new NetworkPrinterTransport("127.0.0.1", port);
        var result = await transport.SendAsync([0x1B, 0x40], TimeoutToken());

        Assert.False(result.Success);
        Assert.True(result.IsRetryable);
        Assert.Equal(PrinterErrorCode.Printer_busy, result.ErrorCode);
    }

    private static CancellationToken TimeoutToken() => new CancellationTokenSource(TestTimeout).Token;

    private static int GetFreeLoopbackPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// Impressora de rede falsa: aceita conexões em loop, lê cada uma até o
    /// cliente fechar (EOF) e expõe o payload recebido.
    /// </summary>
    private sealed class FakePrinterServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly System.Collections.Concurrent.BlockingCollection<byte[]> _jobs = new();
        private readonly Task _acceptLoop;

        public int Port { get; }
        public int TotalConnectionsAccepted;
        public bool LastConnectionClosedByClient { get; private set; }

        private FakePrinterServer(TcpListener listener)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(AcceptLoopAsync);
        }

        public static FakePrinterServer Start()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return new FakePrinterServer(listener);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref TotalConnectionsAccepted);

                    using var stream = client.GetStream();
                    using var ms = new MemoryStream();
                    await stream.CopyToAsync(ms, _cts.Token).ConfigureAwait(false);

                    LastConnectionClosedByClient = true;
                    _jobs.Add(ms.ToArray());
                }
            }
            catch (OperationCanceledException)
            {
                // Encerramento normal do teste.
            }
            catch (ObjectDisposedException)
            {
                // Listener foi parado (Dispose) durante um accept pendente.
            }
        }

        public async Task<byte[]> WaitForNextJobAsync(TimeSpan timeout)
        {
            return await Task.Run(() => _jobs.Take(new CancellationTokenSource(timeout).Token));
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try { _acceptLoop.Wait(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
            _cts.Dispose();
            _jobs.Dispose();
        }
    }
}
