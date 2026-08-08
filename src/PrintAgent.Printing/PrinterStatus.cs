namespace PrintAgent.Printing;

/// <summary>
/// Estado da impressora física lido em tempo real via `DLE EOT n` (§5.3 do
/// plano). Só faz sentido em transportes com canal bidirecional
/// (<see cref="NetworkPrinterTransport"/>); no caminho do spooler
/// (<see cref="SpoolerPrinterTransport"/>) não há como perguntar à
/// impressora, então o único valor honesto é <see cref="Unknown"/> —
/// reportar "pronta" sem saber é pior do que admitir que não se sabe.
/// </summary>
public enum PrinterStatus
{
    /// <summary>Não foi possível determinar o estado (canal sem suporte, sem resposta, timeout).</summary>
    Unknown = 0,

    /// <summary>Impressora respondeu e não sinalizou nenhuma condição de erro.</summary>
    Ready,

    /// <summary>Impressora offline (desligada, cabo removido, etc.).</summary>
    Offline,

    /// <summary>Sem papel.</summary>
    PaperOut,

    /// <summary>Tampa/cover aberta.</summary>
    CoverOpen,
}
