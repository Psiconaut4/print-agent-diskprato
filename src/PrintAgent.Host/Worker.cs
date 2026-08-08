using PrintAgent.Host.Storage;
using PrintAgent.Transport;
using PrintAgent.Transport.Sse;

namespace PrintAgent.Host;

/// <summary>
/// Ciclo de vida principal do agente (plano §6.2): sem token, espera
/// pareamento (via named pipe, plano §7.4); com token, mantém o stream SSE
/// aberto, processa <c>jobs/pending</c> a cada (re)conexão, imprime jobs
/// recebidos, e roda em paralelo um loop de retry local + flush de acks
/// pendentes (plano §6.5/§7.1).
/// </summary>
public sealed class Worker(
    AgentController controller,
    JobStore jobStore,
    PrintOrchestrator orchestrator,
    ILogger<Worker> logger,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private static readonly TimeSpan PairingPollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan LocalRetryInterval = TimeSpan.FromSeconds(15);
    private const int StatusReportEveryNTicks = 20; // ~5 min com LocalRetryInterval=15s (plano §6: no maximo a cada 5 min)

    private const string AgentVersion = "1.0.0"; // TODO(Fase 8): ler da versao do assembly/instalador.

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        jobStore.CleanupOldPrinted(DateTimeOffset.UtcNow.AddDays(-7));

        while (!stoppingToken.IsCancellationRequested)
        {
            if (!controller.IsPaired)
            {
                logger.LogInformation("Sem token de dispositivo — aguardando pareamento.");
                await WaitForPairingAsync(stoppingToken).ConfigureAwait(false);
                continue;
            }

            await RunPairedAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task WaitForPairingAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !controller.IsPaired)
        {
            await Task.Delay(PairingPollInterval, ct).ConfigureAwait(false);
        }
    }

    private async Task RunPairedAsync(CancellationToken stoppingToken)
    {
        var apiBaseUrl = new Uri(controller.Config.ApiBaseUrl);
        using var apiHttp = PrintAgentHttpClientFactory.CreateApiClient(apiBaseUrl, () => controller.Token, AgentVersion);
        using var streamHttp = PrintAgentHttpClientFactory.CreateStreamClient(apiBaseUrl, () => controller.Token, AgentVersion);

        var jobsApi = new JobsApiClient(apiHttp);
        var ackFlusher = new AckFlusher(jobStore, jobsApi, loggerFactory.CreateLogger<AckFlusher>());
        var sse = new SseStreamClient(streamHttp);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        sse.Connected += deviceId =>
        {
            controller.StreamConnected = true;
            logger.LogInformation("Stream conectado (deviceId={DeviceId}).", deviceId);
            _ = RunSafelyAsync(() => HandlePendingJobsAsync(jobsApi, linkedCts.Token), "jobs/pending");
            _ = RunSafelyAsync(() => ackFlusher.FlushAsync(linkedCts.Token), "flush de acks pendentes (connected)");
        };

        sse.JobReceived += e => _ = RunSafelyAsync(() => HandleSseJobAsync(sse, e, linkedCts.Token), $"print:job {e.Job.JobId}");

        sse.JobCancelled += e =>
        {
            if (!jobStore.IsAlreadyPrinted(e.JobId))
            {
                jobStore.RemoveFromQueue(e.JobId);
                logger.LogInformation("Job {JobId} cancelado antes de imprimir — removido da fila local.", e.JobId);
            }
        };

        sse.TokenInvalidated += reason =>
        {
            logger.LogWarning("Token invalidado ({Reason}) — apagando e parando de reconectar.", reason);
            controller.InvalidateToken();
            linkedCts.Cancel();
        };

        sse.VersionUnsupported += () =>
        {
            logger.LogCritical("Versao do agente nao suportada pelo backend (PRINT_AGENT_VERSION_UNSUPPORTED) — atualize o PrintAgent.");
            linkedCts.Cancel();
        };

        var retryLoopTask = RunLocalRetryLoopAsync(jobsApi, ackFlusher, linkedCts.Token);

        try
        {
            await sse.RunAsync(linkedCts.Token).ConfigureAwait(false);
        }
        finally
        {
            controller.StreamConnected = false;
            linkedCts.Cancel();
            await RunSafelyAsync(() => retryLoopTask, "loop de retry local (encerramento)").ConfigureAwait(false);
        }
    }

    private async Task HandleSseJobAsync(SseStreamClient sse, SseJobEvent e, CancellationToken ct)
    {
        var profile = controller.Config.Printer.ToProfile();
        var transport = PrinterTransportFactory.Create(controller.Config.Printer);

        await orchestrator.HandleNewJobAsync(e.Job, profile, transport, ct).ConfigureAwait(false);

        // Só avança depois que HandleNewJobAsync retornou sem lançar: a essa
        // altura o job já está persistido em `jobs` (plano §7.1), então não
        // há mais risco de perdê-lo mesmo que o processo morra em seguida —
        // não é preciso esperar ele efetivamente sair impresso.
        if (e.EventId is not null)
        {
            sse.MarkProcessed(e.EventId);
        }
    }

    private async Task HandlePendingJobsAsync(JobsApiClient jobsApi, CancellationToken ct)
    {
        var jobs = await jobsApi.GetPendingJobsAsync(ct: ct).ConfigureAwait(false);
        var profile = controller.Config.Printer.ToProfile();

        foreach (var job in jobs)
        {
            ct.ThrowIfCancellationRequested();
            var transport = PrinterTransportFactory.Create(controller.Config.Printer);
            await orchestrator.HandleNewJobAsync(job, profile, transport, ct).ConfigureAwait(false);
        }
    }

    private async Task RunLocalRetryLoopAsync(JobsApiClient jobsApi, AckFlusher ackFlusher, CancellationToken ct)
    {
        var tick = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(LocalRetryInterval, ct).ConfigureAwait(false);

                var profile = controller.Config.Printer.ToProfile();
                foreach (var due in jobStore.GetDueJobs(DateTimeOffset.UtcNow))
                {
                    ct.ThrowIfCancellationRequested();
                    var transport = PrinterTransportFactory.Create(controller.Config.Printer);
                    await orchestrator.RetryAsync(due, profile, transport, ct).ConfigureAwait(false);
                }

                await ackFlusher.FlushAsync(ct).ConfigureAwait(false);

                tick++;
                if (tick % StatusReportEveryNTicks == 0)
                {
                    await RunSafelyAsync(() => ReportStatusAsync(jobsApi, ct), "status best-effort").ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // encerramento normal do loop pareado.
        }
    }

    private Task ReportStatusAsync(JobsApiClient jobsApi, CancellationToken ct)
    {
        var printer = controller.Config.Printer;
        var report = new Contracts.StatusReport
        {
            PrinterState = Contracts.StatusReportPrinterState.Unknown, // TODO(Fase 8): ligar a QueryStatusAsync dos transportes.
            Transport = printer.Transport == Config.PrinterTransportKind.Spooler
                ? Contracts.StatusReportTransport.Spooler
                : Contracts.StatusReportTransport.Network,
            PrinterName = printer.Transport == Config.PrinterTransportKind.Spooler ? printer.SpoolerName : printer.Host,
            QueuedJobs = jobStore.GetQueueLength(),
            AgentVersion = AgentVersion,
        };

        return jobsApi.ReportStatusAsync(report, ct); // best-effort: nunca lança.
    }

    private async Task RunSafelyAsync(Func<Task> action, string what)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Falha em: {What}.", what);
        }
    }
}
