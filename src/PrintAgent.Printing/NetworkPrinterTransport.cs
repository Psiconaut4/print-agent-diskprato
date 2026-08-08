using System.Net.Sockets;
using PrintAgent.Contracts;

namespace PrintAgent.Printing;

/// <summary>
/// Transporte de rede para impressoras RAW/JetDirect na porta 9100 (§4.2 do
/// plano). Essas impressoras aceitam apenas uma conexão TCP por vez e não
/// têm um árbitro como o spooler do Windows — a convivência com outro PDV
/// vem inteiramente do comportamento deste transporte:
///
/// <list type="bullet">
/// <item>Conectar → enviar → fechar, por job. Nunca mantém socket aberto
/// entre jobs (o socket persistente é com o backend, nunca com a
/// impressora).</item>
/// <item>Falha de conexão (recusada, timeout, host inalcançável) vira
/// <see cref="PrinterSendResult.IsRetryable"/> = true — é provavelmente
/// outro PDV usando a impressora naquele instante, não um erro terminal.
/// Quem chama decide a política de backoff.</item>
/// </list>
///
/// Quando a mesma impressora de rede também estiver instalada como fila do
/// Windows (porta TCP/IP padrão), <see cref="SpoolerPrinterTransport"/> é
/// preferível — volta a ter um árbitro real.
/// </summary>
public sealed class NetworkPrinterTransport : IPrinterTransport, IPrinterStatusQuery
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    private readonly string _host;
    private readonly int _port;

    public NetworkPrinterTransport(string host, int port = 9100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));

        _host = host;
        _port = port;
    }

    public async Task<PrinterSendResult> SendAsync(byte[] payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);

        try
        {
            using var client = new TcpClient();

            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connectCts.CancelAfter(ConnectTimeout);
                try
                {
                    await client.ConnectAsync(_host, _port, connectCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return PrinterSendResult.Fail(
                        PrinterErrorCode.Printer_offline,
                        isRetryable: true,
                        $"Timeout (>{ConnectTimeout.TotalSeconds}s) ao conectar em {_host}:{_port}.");
                }
            }

            using var stream = client.GetStream();
            using (var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                sendCts.CancelAfter(SendTimeout);
                try
                {
                    await stream.WriteAsync(payload, sendCts.Token).ConfigureAwait(false);
                    await stream.FlushAsync(sendCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return PrinterSendResult.Fail(
                        PrinterErrorCode.Transport_error,
                        isRetryable: true,
                        $"Timeout (>{SendTimeout.TotalSeconds}s) ao enviar para {_host}:{_port}.");
                }
            }

            // client/stream são fechados pelo `using` ao sair do método —
            // conectar → enviar → fechar, sempre, por job.
            return PrinterSendResult.Ok();
        }
        catch (SocketException ex)
        {
            return MapSocketException(ex);
        }
        catch (IOException ex) when (ex.InnerException is SocketException socketEx)
        {
            return MapSocketException(socketEx);
        }
    }

    private static PrinterSendResult MapSocketException(SocketException ex) => ex.SocketErrorCode switch
    {
        // Recusada geralmente significa que a impressora já tem a única
        // conexão que aceita ocupada por outro PDV — vale tentar de novo.
        SocketError.ConnectionRefused => PrinterSendResult.Fail(PrinterErrorCode.Printer_busy, isRetryable: true, ex.Message),
        SocketError.ConnectionReset => PrinterSendResult.Fail(PrinterErrorCode.Printer_busy, isRetryable: true, ex.Message),

        // Sem resposta nenhuma / rede inalcançável: mais provável que a
        // impressora esteja desligada ou fora da rede do que ocupada.
        SocketError.TimedOut => PrinterSendResult.Fail(PrinterErrorCode.Printer_offline, isRetryable: true, ex.Message),
        SocketError.HostUnreachable => PrinterSendResult.Fail(PrinterErrorCode.Printer_offline, isRetryable: true, ex.Message),
        SocketError.NetworkUnreachable => PrinterSendResult.Fail(PrinterErrorCode.Printer_offline, isRetryable: true, ex.Message),

        // Não resolve DNS / IP configurado errado: retry não ajuda sem o
        // lojista corrigir a configuração.
        SocketError.HostNotFound => PrinterSendResult.Fail(PrinterErrorCode.Not_configured, isRetryable: false, ex.Message),

        _ => PrinterSendResult.Fail(PrinterErrorCode.Transport_error, isRetryable: true, ex.Message),
    };

    /// <summary>
    /// Consulta <c>DLE EOT n</c> (§5.3 do plano) — só é possível aqui porque
    /// o canal TCP é bidirecional (dá para ler a resposta do socket antes de
    /// fechar). A interpretação dos bits de status segue a convenção Epson
    /// (a mais seguida pelo mercado); outros fabricantes podem divergir —
    /// por isso qualquer ambiguidade cai em <see cref="PrinterStatus.Unknown"/>
    /// em vez de adivinhar.
    /// </summary>
    public async Task<PrinterStatus> QueryStatusAsync(CancellationToken ct)
    {
        try
        {
            using var client = new TcpClient();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(ConnectTimeout);
            await client.ConnectAsync(_host, _port, connectCts.Token).ConfigureAwait(false);

            using var stream = client.GetStream();
            using var ioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ioCts.CancelAfter(SendTimeout);

            // n=1: status geral. Bit 3 (0x08) ligado = offline.
            var general = await QueryDleEotAsync(stream, n: 1, ioCts.Token).ConfigureAwait(false);
            if (general is null)
                return PrinterStatus.Unknown;
            if ((general.Value & 0x08) != 0)
                return PrinterStatus.Offline;

            // n=4: sensor de papel. Bits 5/7 (0x60) ligados = fim de papel.
            var paper = await QueryDleEotAsync(stream, n: 4, ioCts.Token).ConfigureAwait(false);
            if (paper is not null && (paper.Value & 0x60) != 0)
                return PrinterStatus.PaperOut;

            return PrinterStatus.Ready;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
            _ = ex;
            return PrinterStatus.Unknown;
        }
    }

    private static async Task<byte?> QueryDleEotAsync(NetworkStream stream, byte n, CancellationToken ct)
    {
        byte[] command = [0x10, 0x04, n];
        await stream.WriteAsync(command, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);

        var buffer = new byte[1];
        var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
        return read == 1 ? buffer[0] : null;
    }
}
