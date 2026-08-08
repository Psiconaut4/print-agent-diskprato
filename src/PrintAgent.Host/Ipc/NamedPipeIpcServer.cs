using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PrintAgent.Contracts;
using PrintAgent.Core;
using PrintAgent.Host.Config;
using PrintAgent.Printing;
using PrintAgent.Transport;

namespace PrintAgent.Host.Ipc;

/// <summary>
/// Servidor do named pipe <c>\\.\pipe\diskprato-printagent</c> (plano §7.4):
/// JSON por linha, um comando por conexão. ACL permite <c>Users</c> — a tela
/// de setup do Tray roda sem elevação, e o serviço roda como
/// <c>LocalSystem</c>; os dois precisam falar um com o outro sem admin.
///
/// O pipe nunca lê o token diretamente (plano §7.2): toda ação passa pelo
/// <see cref="AgentController"/>, que é quem sabe onde/como o token vive.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NamedPipeIpcServer(
    AgentController controller, EscPosFormatter formatter, ILogger<NamedPipeIpcServer> logger) : BackgroundService
{
    public const string PipeName = "diskprato-printagent";
    private const string AgentVersion = "1.0.0";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var server = CreatePipeServer();
                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
                await HandleConnectionAsync(server, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // encerramento normal do serviço.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro no servidor do named pipe; reabrindo a fila de conexão.");
            }
        }
    }

    private static NamedPipeServerStream CreatePipeServer()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            pipeSecurity: security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server, leaveOpen: true);
        using var writer = new StreamWriter(server, leaveOpen: true) { AutoFlush = true };

        var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        if (line is null)
        {
            return;
        }

        var response = await DispatchAsync(line, ct).ConfigureAwait(false);
        await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
    }

    private async Task<IpcResponse> DispatchAsync(string line, CancellationToken ct)
    {
        IpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<IpcRequest>(line);
        }
        catch (JsonException)
        {
            return IpcResponse.Failure("Requisicao JSON invalida.");
        }

        if (request is null)
        {
            return IpcResponse.Failure("Requisicao vazia.");
        }

        try
        {
            return request.Command switch
            {
                "get-status" => IpcResponse.Success(await controller.GetStatusAsync(ct).ConfigureAwait(false)),
                "get-config" => IpcResponse.Success(await controller.GetStatusAsync(ct).ConfigureAwait(false), controller.Config.Printer),
                "pair" => await HandlePairAsync(request, ct).ConfigureAwait(false),
                "unpair" => await HandleUnpairAsync(ct).ConfigureAwait(false),
                "set-printer" => await HandleSetPrinterAsync(request, ct).ConfigureAwait(false),
                "test-print" => await HandleTestPrintAsync(ct).ConfigureAwait(false),
                _ => IpcResponse.Failure($"Comando desconhecido: {request.Command}"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falha processando comando IPC {Command}.", request.Command);
            return IpcResponse.Failure(ex.Message);
        }
    }

    private async Task<IpcResponse> HandlePairAsync(IpcRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || string.IsNullOrWhiteSpace(request.DeviceName))
        {
            return IpcResponse.Failure("pair exige code e deviceName.");
        }

        var outcome = await controller.PairAsync(
            request.Code, request.DeviceName, AgentVersion, "win-x64 / Windows", ct).ConfigureAwait(false);

        return outcome switch
        {
            PairOutcome.Success => IpcResponse.Success(await controller.GetStatusAsync(ct).ConfigureAwait(false)),
            PairOutcome.Failure failure => IpcResponse.Failure(failure.Message),
            _ => IpcResponse.Failure("Falha desconhecida no pareamento."),
        };
    }

    private async Task<IpcResponse> HandleUnpairAsync(CancellationToken ct)
    {
        controller.Unpair();
        return IpcResponse.Success(await controller.GetStatusAsync(ct).ConfigureAwait(false));
    }

    private async Task<IpcResponse> HandleSetPrinterAsync(IpcRequest request, CancellationToken ct)
    {
        if (request.Printer is null)
        {
            return IpcResponse.Failure("set-printer exige printer.");
        }

        controller.UpdatePrinterConfig(request.Printer);
        return IpcResponse.Success(await controller.GetStatusAsync(ct).ConfigureAwait(false));
    }

    private async Task<IpcResponse> HandleTestPrintAsync(CancellationToken ct)
    {
        var printer = controller.Config.Printer;
        var transport = PrinterTransportFactory.Create(printer);
        var bytes = formatter.Format(BuildSyntheticJob(), printer.ToProfile());

        var result = await transport.SendAsync(bytes, ct).ConfigureAwait(false);
        return result.Success
            ? IpcResponse.Success(await controller.GetStatusAsync(ct).ConfigureAwait(false))
            : IpcResponse.Failure(result.Detail ?? result.ErrorCode?.ToString() ?? "Falha desconhecida na impressao de teste.");
    }

    private static PrintJob BuildSyntheticJob() => new()
    {
        JobId = $"test_{Guid.NewGuid():N}",
        OrderId = "test",
        RestaurantId = "test",
        Kind = PrintJobKind.Test,
        Target = PrintJobTarget.Kitchen,
        Copies = 1,
        IssuedAt = DateTimeOffset.Now,
        Restaurant = new Restaurant2 { Name = "Teste de impressão", Phone = null, AddressLine = null },
        Order = new PrintOrder
        {
            Number = "TESTE",
            CreatedAt = DateTimeOffset.Now,
            Timezone = null,
            FulfillmentType = PrintOrderFulfillmentType.Pickup,
            Notes = "Cupom de teste — configuração do PrintAgent",
            Customer = new Customer { Name = "Teste", Phone = "" },
            Delivery = null,
            Payment = new PrintPayment
            {
                Method = PrintPaymentMethod.Cash,
                Status = PrintPaymentStatus.Paid,
                Label = "Teste",
                ChangeForCents = null,
                ChangeDueCents = null,
            },
            Items =
            [
                new PrintItem
                {
                    Quantity = 1,
                    Name = "Item de teste (ç ã õ é)",
                    UnitPriceCents = 0,
                    TotalPriceCents = 0,
                    Modifiers = [],
                    ComboItems = [],
                },
            ],
            SubtotalCents = 0,
            DeliveryFeeCents = 0,
            TotalCents = 0,
            Currency = PrintOrderCurrency.BRL,
        },
    };
}
