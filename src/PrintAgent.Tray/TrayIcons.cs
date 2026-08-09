using System.Drawing.Drawing2D;
using PrintAgent.Tray.Ipc;

namespace PrintAgent.Tray;

/// <summary>
/// Ícones da bandeja gerados em memória — evita empacotar arquivos
/// <c>.ico</c> separados no instalador (Fase 7). Ficam vivos pelo tempo de
/// vida do processo (armazenados como campos estáticos), então o handle
/// nativo do <see cref="Icon"/> nunca precisa ser destruído em runtime.
///
/// A silhueta (uma impressorinha com um cupom saindo) é sempre a mesma —
/// só o selo circular no canto muda de cor. Colorir o ícone inteiro (como
/// na versão anterior, um círculo sólido) fica ilegível em 16px; um selo de
/// status pequeno sobre uma forma fixa é o padrão mais comum de ícone de
/// bandeja com estado (ex. sincronização de nuvem, chat) e lê melhor nesse
/// tamanho.
/// </summary>
internal static class TrayIcons
{
    public static readonly Color NotPairedColor = Color.Gray;
    public static readonly Color DisconnectedColor = Color.Firebrick;
    public static readonly Color PrinterProblemColor = Color.Orange;
    public static readonly Color ConnectedColor = Color.SeaGreen;

    private static readonly Icon UnknownIcon = Build(Color.Gray);
    private static readonly Icon NotPairedIcon = Build(NotPairedColor);
    private static readonly Icon DisconnectedIcon = Build(DisconnectedColor);
    private static readonly Icon PrinterProblemIcon = Build(PrinterProblemColor);
    private static readonly Icon ConnectedIcon = Build(ConnectedColor);

    public static Icon Unknown => UnknownIcon;

    public static Icon For(AgentStatusDto? status) => StateFor(status) switch
    {
        AgentVisualState.NotPaired => NotPairedIcon,
        AgentVisualState.Disconnected => DisconnectedIcon,
        AgentVisualState.PrinterProblem => PrinterProblemIcon,
        AgentVisualState.Connected => ConnectedIcon,
        _ => UnknownIcon,
    };

    /// <summary>Mesma cor usada no selo do ícone — para a tela de setup refletir o mesmo estado sem duplicar a lógica de prioridade.</summary>
    public static Color ColorFor(AgentStatusDto? status) => StateFor(status) switch
    {
        AgentVisualState.NotPaired => NotPairedColor,
        AgentVisualState.Disconnected => DisconnectedColor,
        AgentVisualState.PrinterProblem => PrinterProblemColor,
        AgentVisualState.Connected => ConnectedColor,
        _ => Color.Gray,
    };

    /// <summary>Prioriza o que precisa de atenção do lojista: sem pareamento, depois sem conexão, depois problema físico da impressora — só então "tudo certo".</summary>
    private static AgentVisualState StateFor(AgentStatusDto? status)
    {
        if (status is null)
        {
            return AgentVisualState.Disconnected;
        }

        if (!status.Paired)
        {
            return AgentVisualState.NotPaired;
        }

        if (!status.StreamConnected)
        {
            return AgentVisualState.Disconnected;
        }

        return status.PrinterStatus is "Offline" or "PaperOut" or "CoverOpen"
            ? AgentVisualState.PrinterProblem
            : AgentVisualState.Connected;
    }

    private enum AgentVisualState
    {
        NotPaired,
        Disconnected,
        PrinterProblem,
        Connected,
    }

    // Desenhado num canvas 32x32 (não 16x16): o shell do Windows escala pra
    // baixo quando o slot da bandeja é menor, e fica mais nítido em telas de
    // alta densidade do que desenhar direto em 16x16 e depender de upscale.
    private static Icon Build(Color statusColor)
    {
        const int size = 32;
        var bodyColor = Color.FromArgb(80, 80, 80);

        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // corpo da impressora
            using (var bodyBrush = new SolidBrush(bodyColor))
            {
                var body = new Rectangle(3, 13, 24, 12);
                DrawRoundedRectangle(g, bodyBrush, body, 3);
            }

            // fresta de saida do papel
            using (var slotBrush = new SolidBrush(Color.FromArgb(45, 45, 45)))
            {
                g.FillRectangle(slotBrush, 8, 16, 14, 3);
            }

            // pezinhos da base
            using (var footBrush = new SolidBrush(bodyColor))
            {
                g.FillRectangle(footBrush, 6, 24, 3, 2);
                g.FillRectangle(footBrush, 21, 24, 3, 2);
            }

            // cupom saindo por cima
            var paper = new Rectangle(7, 2, 16, 12);
            using (var paperBrush = new SolidBrush(Color.White))
            {
                g.FillRectangle(paperBrush, paper);
            }
            using (var paperPen = new Pen(bodyColor, 1.4f))
            {
                g.DrawRectangle(paperPen, paper);
                g.DrawLine(paperPen, paper.Left + 3, paper.Top + 4, paper.Right - 3, paper.Top + 4);
                g.DrawLine(paperPen, paper.Left + 3, paper.Top + 7, paper.Right - 6, paper.Top + 7);
            }

            // selo de status: unico elemento colorido, canto inferior direito.
            var badge = new Rectangle(19, 18, 12, 12);
            using (var badgeBrush = new SolidBrush(statusColor))
            {
                g.FillEllipse(badgeBrush, badge);
            }
            using (var badgePen = new Pen(Color.White, 2f))
            {
                g.DrawEllipse(badgePen, badge);
            }
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    private static void DrawRoundedRectangle(Graphics g, Brush brush, Rectangle rect, int radius)
    {
        using var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
