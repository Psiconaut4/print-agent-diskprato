using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace PrintAgent.Tray.Ipc;

/// <summary>
/// Cliente do named pipe <c>\\.\pipe\diskprato-printagent</c> (plano §7.4).
/// Uma conexão por comando, como o servidor espera (<c>NamedPipeIpcServer</c>
/// lê uma linha e responde uma linha por conexão) — sem estado entre
/// chamadas, então uma falha de comunicação num comando não afeta o
/// próximo.
/// </summary>
public sealed class AgentIpcClient
{
    private const string PipeName = "diskprato-printagent";
    private const int ConnectTimeoutMs = 2000;

    public Task<IpcResponseDto> GetStatusAsync(CancellationToken ct = default) =>
        SendAsync(new IpcRequestDto { Command = "get-status" }, ct);

    public Task<IpcResponseDto> GetConfigAsync(CancellationToken ct = default) =>
        SendAsync(new IpcRequestDto { Command = "get-config" }, ct);

    public Task<IpcResponseDto> PairAsync(string code, string deviceName, CancellationToken ct = default) =>
        SendAsync(new IpcRequestDto { Command = "pair", Code = code, DeviceName = deviceName }, ct);

    public Task<IpcResponseDto> UnpairAsync(CancellationToken ct = default) =>
        SendAsync(new IpcRequestDto { Command = "unpair" }, ct);

    public Task<IpcResponseDto> SetPrinterAsync(PrinterConfigDto printer, CancellationToken ct = default) =>
        SendAsync(new IpcRequestDto { Command = "set-printer", Printer = printer }, ct);

    public Task<IpcResponseDto> RemovePrinterAsync(StationDto? station, CancellationToken ct = default) =>
        SendAsync(new IpcRequestDto { Command = "remove-printer", Station = station }, ct);

    public Task<IpcResponseDto> TestPrintAsync(StationDto? station = null, CancellationToken ct = default) =>
        SendAsync(new IpcRequestDto { Command = "test-print", Station = station }, ct);

    /// <summary>
    /// Pede ao serviço o pacote de diagnóstico (plano §8, Fase 8). Quem monta e
    /// grava é o serviço: os arquivos moram em <c>%ProgramData%</c>, cuja ACL o
    /// instalador restringe a SYSTEM + Administradores, e este processo roda
    /// como o operador do balcão — ele não consegue nem listar aquela pasta. A
    /// gravação em <paramref name="destinationPath"/> é feita pelo serviço
    /// impersonando este processo, então vale a permissão do usuário.
    /// </summary>
    public Task<IpcResponseDto> ExportDiagnosticsAsync(string destinationPath, CancellationToken ct = default) =>
        SendAsync(new IpcRequestDto { Command = "export-diagnostics", DestinationPath = destinationPath }, ct);

    private static async Task<IpcResponseDto> SendAsync(IpcRequestDto request, CancellationToken ct)
    {
        try
        {
            // TokenImpersonationLevel.Impersonation: sem isso o RunAsClient do
            // lado do serviço falha, e é dele que depende o export-diagnostics
            // gravar com a identidade do usuário em vez da do LocalSystem.
            using var pipe = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
            await pipe.ConnectAsync(ConnectTimeoutMs, ct).ConfigureAwait(false);

            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, leaveOpen: true);

            await writer.WriteLineAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);

            if (line is null)
            {
                return new IpcResponseDto { Ok = false, Error = "O serviço fechou a conexão sem responder." };
            }

            return JsonSerializer.Deserialize<IpcResponseDto>(line)
                ?? new IpcResponseDto { Ok = false, Error = "Resposta do serviço não pôde ser entendida." };
        }
        catch (TimeoutException)
        {
            return new IpcResponseDto { Ok = false, Error = "Serviço Gerente de Impressão DiskPrato não está rodando." };
        }
        catch (IOException ex)
        {
            return new IpcResponseDto { Ok = false, Error = $"Falha de comunicação com o serviço: {ex.Message}" };
        }
        catch (JsonException)
        {
            return new IpcResponseDto { Ok = false, Error = "Resposta do serviço não pôde ser entendida." };
        }
    }
}
