using PrintAgent.Core;
using PrintAgent.Host;
using PrintAgent.Host.Config;
using PrintAgent.Host.Diagnostics;
using PrintAgent.Host.Ipc;
using PrintAgent.Host.Security;
using PrintAgent.Host.Storage;
using PrintAgent.Transport;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// Sessão 0: serviço do Windows não tem UI. UseWindowsService() é no-op
// quando roda como console/dev (plano §7.4/§8 Fase 7).
builder.Services.AddWindowsService(options => options.ServiceName = "DiskPratoPrintAgent");

// Serilog em arquivo com rotação (plano §8, Fase 8). ClearProviders porque o
// EventLog do Worker Service continuaria recebendo tudo em duplicata.
builder.Logging.ClearProviders();
builder.Services.AddSerilog(AgentLogging.CreateLogger(builder.Configuration), dispose: true);

builder.Services.AddSingleton(new AgentConfigStore());
builder.Services.AddSingleton<DeviceTokenStore>();
builder.Services.AddSingleton<JobStore>();
builder.Services.AddSingleton<EscPosFormatter>();
builder.Services.AddSingleton<PrintOrchestrator>();
builder.Services.AddSingleton<StartupSelfTest>();
builder.Services.AddSingleton<DiagnosticsExporter>();

builder.Services.AddSingleton(sp =>
{
    var configStore = sp.GetRequiredService<AgentConfigStore>();
    var apiBaseUrl = new Uri(configStore.Load().ApiBaseUrl);
    var tokenStore = sp.GetRequiredService<DeviceTokenStore>();
    var http = PrintAgentHttpClientFactory.CreateApiClient(apiBaseUrl, tokenStore.TryLoad, AgentVersion.Current);
    return new PairingApiClient(http);
});

builder.Services.AddSingleton<AgentController>();
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<NamedPipeIpcServer>();

var host = builder.Build();
host.Run();
