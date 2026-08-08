using PrintAgent.Contracts;
using PrintAgent.Printing;

namespace PrintAgent.Printing.Tests;

/// <summary>
/// Testes automatizados de <see cref="SpoolerPrinterTransport"/> ficam
/// limitados ao que é seguro rodar sem provisionar uma fila de impressão
/// real do Windows (o que exige uma máquina/VM configurada manualmente,
/// tipicamente uma fila "Generic / Text Only" apontada para a porta
/// <c>FILE:</c> — ver o plano, Fase 2). Os testes aqui cobrem apenas o
/// caminho de erro (fila inexistente) e o helper de enumeração, que rodam
/// em qualquer Windows sem privilégio elevado.
/// </summary>
public sealed class SpoolerPrinterTransportTests
{
    [Fact]
    public async Task SendAsync_UnknownQueueName_ReturnsNotConfiguredWithoutThrowing()
    {
        var transport = new SpoolerPrinterTransport($"DiskPrato-Fila-Que-Nao-Existe-{Guid.NewGuid():N}");

        var result = await transport.SendAsync([0x1B, 0x40], CancellationToken.None);

        Assert.False(result.Success);
        Assert.False(result.IsRetryable);
        Assert.Equal(PrinterErrorCode.Not_configured, result.ErrorCode);
    }

    [Fact]
    public async Task QueryStatusAsync_UnknownQueueName_ReturnsUnknownWithoutThrowing()
    {
        var transport = new SpoolerPrinterTransport($"DiskPrato-Fila-Que-Nao-Existe-{Guid.NewGuid():N}");

        var status = await transport.QueryStatusAsync(CancellationToken.None);

        Assert.Equal(PrinterStatus.Unknown, status);
    }

    [Fact]
    public void EnumPrinterQueues_DoesNotThrow_AndReturnsAList()
    {
        var queues = SpoolerPrinterTransport.EnumPrinterQueues();

        Assert.NotNull(queues);
        // Não afirmamos nada sobre o conteúdo: a máquina de CI pode não ter
        // nenhuma fila instalada. O importante é não lançar e devolver uma
        // lista (mesmo vazia) com nomes não-nulos/não-vazios quando houver.
        Assert.All(queues, name => Assert.False(string.IsNullOrWhiteSpace(name)));
    }
}
