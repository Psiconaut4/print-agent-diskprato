using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using PrintAgent.Contracts;
using PrintAgent.Core;
using PrintAgent.Host.Config;
using PrintAgent.Host.Diagnostics;
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
    AgentController controller,
    EscPosFormatter formatter,
    DiagnosticsExporter diagnosticsExporter,
    ILogger<NamedPipeIpcServer> logger) : BackgroundService
{
    public const string PipeName = "diskprato-printagent";

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

        var response = await DispatchAsync(server, line, ct).ConfigureAwait(false);
        await writer.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
    }

    private async Task<IpcResponse> DispatchAsync(NamedPipeServerStream server, string line, CancellationToken ct)
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
                "get-config" => IpcResponse.Success(await controller.GetStatusAsync(ct).ConfigureAwait(false), controller.Config.Printers),
                "pair" => await HandlePairAsync(request, ct).ConfigureAwait(false),
                "unpair" => await HandleUnpairAsync(ct).ConfigureAwait(false),
                "set-printer" => await HandleSetPrinterAsync(request, ct).ConfigureAwait(false),
                "remove-printer" => await HandleRemovePrinterAsync(request, ct).ConfigureAwait(false),
                "test-print" => await HandleTestPrintAsync(request, ct).ConfigureAwait(false),
                "export-diagnostics" => await HandleExportDiagnosticsAsync(server, request, ct).ConfigureAwait(false),
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
            request.Code, request.DeviceName, AgentVersion.Current, "win-x64 / Windows", ct).ConfigureAwait(false);

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

    private async Task<IpcResponse> HandleRemovePrinterAsync(IpcRequest request, CancellationToken ct)
    {
        controller.RemovePrinterConfig(request.Station);
        return IpcResponse.Success(await controller.GetStatusAsync(ct).ConfigureAwait(false));
    }

    private async Task<IpcResponse> HandleTestPrintAsync(IpcRequest request, CancellationToken ct)
    {
        // request.Station ausente testa a impressora "padrão" (plano §10) —
        // mesmo comportamento de antes da tela de setup ganhar seções por
        // estação (Fase 3), preservado pro caso de instalação de estação única.
        var printer = request.Station is PrintJobTarget station
            ? controller.ResolvePrinter(station)
            : controller.ResolveDefaultPrinter();
        var transport = PrinterTransportFactory.Create(printer);
        var bytes = formatter.Format(BuildSyntheticJob(), printer.ToProfile());

        var result = await transport.SendAsync(bytes, ct).ConfigureAwait(false);
        return result.Success
            ? IpcResponse.Success(await controller.GetStatusAsync(ct).ConfigureAwait(false))
            : IpcResponse.Failure(result.Detail ?? result.ErrorCode?.ToString() ?? "Falha desconhecida na impressao de teste.");
    }

    /// <summary>
    /// Grava o pacote de diagnóstico (plano §8, Fase 8) onde o cliente pediu —
    /// mas escrevendo com o token do cliente, não com o do serviço.
    ///
    /// A ACL do pipe libera <c>BUILTIN\Users</c> para o Tray funcionar sem
    /// elevação, e o serviço roda como <c>LocalSystem</c>. Se o
    /// <c>File.WriteAllBytes</c> rodasse com a identidade do serviço, qualquer
    /// usuário local mandaria uma linha de JSON neste pipe e faria o SYSTEM
    /// gravar um arquivo em qualquer lugar do disco — inclusive
    /// <c>%SystemRoot%\System32</c>. Seria elevação de privilégio de verdade,
    /// exposta pelo agente. <see cref="NamedPipeServerStream.RunAsClient"/>
    /// impersona quem está do outro lado do pipe, então a gravação passa pelas
    /// permissões do próprio usuário: ele só consegue escrever onde já podia.
    ///
    /// O zip é montado antes, como serviço (a pasta de dados é ACL-restrita e
    /// o usuário não a lê), e só a escrita final é impersonada.
    /// </summary>
    private async Task<IpcResponse> HandleExportDiagnosticsAsync(
        NamedPipeServerStream server, IpcRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DestinationPath))
        {
            return IpcResponse.Failure("export-diagnostics exige destinationPath.");
        }

        var destination = request.DestinationPath;
        var bytes = await diagnosticsExporter.BuildAsync(ct).ConfigureAwait(false);

        Exception? writeFailure = null;
        server.RunAsClient(() =>
        {
            try
            {
                File.WriteAllBytes(destination, bytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                writeFailure = ex;
            }
        });

        if (writeFailure is not null)
        {
            logger.LogError(writeFailure, "Falha ao gravar o pacote de diagnostico em {Destination}.", destination);
            return IpcResponse.Failure($"Nao foi possivel gravar em {destination}: {writeFailure.Message}");
        }

        logger.LogInformation("Pacote de diagnostico exportado para {Destination} ({Bytes} bytes).", destination, bytes.Length);
        var response = IpcResponse.Success(await controller.GetStatusAsync(ct).ConfigureAwait(false));
        response.Path = destination;
        return response;
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
        Restaurant = new Restaurant2 { Name = "DiskPrato", Phone = "+55 11 99999-0000", AddressLine = "Rua de Teste, 123" },
        Order = new PrintOrder
        {
            Number = "TESTE-001",
            CreatedAt = DateTimeOffset.Now,
            Timezone = "America/Sao_Paulo",
            FulfillmentType = PrintOrderFulfillmentType.Delivery,
            Notes = "Cupom de teste completo - configuração do PrintAgent\nEste cupom verifica formatação, acentos, espaçamento e negrito",
            Customer = new Customer { Name = "João da Silva Teste", Phone = "+55 11 98888-7777" },
            Delivery = new Delivery
            {
                Address = "Av. Paulista, 1000",
                Street = "Av. Paulista",
                StreetNumber = "1000",
                Neighborhood = "Bela Vista",
                Complement = "Andar 5, Sala 501",
                DistanceKm = 5.5,
            },
            Payment = new PrintPayment
            {
                Method = PrintPaymentMethod.Cash,
                Status = PrintPaymentStatus.Pending,
                Label = "Dinheiro",
                ChangeForCents = 10000,
                ChangeDueCents = 3750,
            },
            Items =
            [
                new PrintItem
                {
                    Quantity = 2,
                    Name = "X-Bacon com Queijo Extra (ç ã õ é)",
                    UnitPriceCents = 2850,
                    TotalPriceCents = 5700,
                    Modifiers =
                    [
                        new Modifiers { GroupName = "Adicionais", Name = "Bacon", PriceCents = 300 },
                        new Modifiers { GroupName = "Adicionais", Name = "Cheddar", PriceCents = 250 },
                        new Modifiers { GroupName = "Molhos", Name = "Maionese Caseira", PriceCents = null },
                    ],
                    ComboItems = [],
                },
                new PrintItem
                {
                    Quantity = 1,
                    Name = "Batata Frita Grande",
                    UnitPriceCents = 1500,
                    TotalPriceCents = 1500,
                    Modifiers =
                    [
                        new Modifiers { GroupName = "Temperos", Name = "Alecrim", PriceCents = 0 },
                        new Modifiers { GroupName = "Temperos", Name = "Alho", PriceCents = 0 },
                    ],
                    ComboItems = [],
                },
                new PrintItem
                {
                    Quantity = 1,
                    Name = "Combo Família Completo",
                    UnitPriceCents = 4500,
                    TotalPriceCents = 4500,
                    Modifiers = [],
                    ComboItems =
                    [
                        new ComboItems { Name = "Coca-Cola 350ml", Quantity = 2 },
                        new ComboItems { Name = "Batata Frita Média", Quantity = 1 },
                        new ComboItems { Name = "Nuggets 10 unidades", Quantity = 1 },
                    ],
                },
                new PrintItem
                {
                    Quantity = 3,
                    Name = "Hambúrguer Artesanal",
                    UnitPriceCents = 2200,
                    TotalPriceCents = 6600,
                    Modifiers =
                    [
                        new Modifiers { GroupName = "Ponto", Name = "Mal Passado", PriceCents = null },
                        new Modifiers { GroupName = "Queijo", Name = "Prato", PriceCents = 150 },
                    ],
                    ComboItems = [],
                },
            ],
            SubtotalCents = 18300,
            DeliveryFeeCents = 750,
            TotalCents = 19050,
            Currency = PrintOrderCurrency.BRL,
        },
    };
}
