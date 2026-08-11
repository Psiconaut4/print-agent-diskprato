namespace PrintAgent.Host.Diagnostics;

/// <summary>
/// Onde o agente guarda o que é dele em disco (plano §7): tudo pendurado em
/// <c>%ProgramData%\DiskPrato\PrintAgent</c>, cuja ACL o instalador restringe
/// a SYSTEM + Administradores porque o <c>device.dat</c> ao lado é protegido
/// com <c>DataProtectionScope.LocalMachine</c> (plano §7.2).
///
/// Existe para os componentes da Fase 8 (log em arquivo, exportar diagnóstico)
/// não repetirem a montagem do caminho nem, pior, divergirem dela.
/// <see cref="Config.AgentConfigStore.DefaultDirectory"/> aponta para cá.
/// </summary>
public static class AgentPaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "DiskPrato",
        "PrintAgent");

    /// <summary>Serilog escreve <c>printagent-YYYYMMDD.log</c> aqui (plano §8, Fase 8 — rotação diária, 7 dias).</summary>
    public static string LogsDirectory { get; } = Path.Combine(RootDirectory, "logs");

    /// <summary>Fila local em arquivo (plano §7.1) — <c>pending/</c>, <c>printed/</c>, <c>failed/</c>.</summary>
    public static string QueueDirectory { get; } = Path.Combine(RootDirectory, "queue");
}
