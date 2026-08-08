using System.Drawing.Drawing2D;
using PrintAgent.Tray.Ipc;

namespace PrintAgent.Tray;

/// <summary>
/// Ícones da bandeja gerados em memória — evita empacotar arquivos
/// <c>.ico</c> separados no instalador (Fase 7) só para cinco círculos
/// coloridos. Ficam vivos pelo tempo de vida do processo (armazenados como
/// campos estáticos), então o handle nativo do <see cref="Icon"/> nunca
/// precisa ser destruído em runtime.
/// </summary>
internal static class TrayIcons
{
    private static readonly Icon UnknownIcon = Build(Color.Gray);
    private static readonly Icon NotPairedIcon = Build(Color.Gray);
    private static readonly Icon ConnectedIcon = Build(Color.SeaGreen);
    private static readonly Icon DisconnectedIcon = Build(Color.Firebrick);
    private static readonly Icon PrinterProblemIcon = Build(Color.Orange);

    public static Icon Unknown => UnknownIcon;

    /// <summary>Prioriza o que precisa de atenção do lojista: sem pareamento, depois sem conexão, depois problema físico da impressora — só então "tudo certo".</summary>
    public static Icon For(AgentStatusDto? status)
    {
        if (status is null)
        {
            return DisconnectedIcon;
        }

        if (!status.Paired)
        {
            return NotPairedIcon;
        }

        if (!status.StreamConnected)
        {
            return DisconnectedIcon;
        }

        return status.PrinterStatus is "Offline" or "PaperOut" or "CoverOpen"
            ? PrinterProblemIcon
            : ConnectedIcon;
    }

    private static Icon Build(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 13, 13);
            using var pen = new Pen(Color.FromArgb(90, 0, 0, 0));
            g.DrawEllipse(pen, 1, 1, 13, 13);
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }
}
