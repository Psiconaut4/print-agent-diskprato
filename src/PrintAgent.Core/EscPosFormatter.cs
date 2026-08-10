using System.Globalization;
using System.Text;
using PrintAgent.Contracts;

namespace PrintAgent.Core;

/// <summary>
/// Converte um <see cref="PrintJob"/> em bytes ESC/POS crus (plano §5).
/// Puramente formatação: não conhece HTTP, Win32 nem a impressora física —
/// quem manda os bytes é <c>PrintAgent.Printing</c>.
/// </summary>
public sealed class EscPosFormatter
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    // .NET Core não traz CP850/CP860 embutidas; sem registrar o provider,
    // Encoding.GetEncoding(850) lança em runtime (plano §5.2). Construtor
    // estático registra uma única vez por processo (CA2255 proíbe
    // [ModuleInitializer] em código de biblioteca).
    static EscPosFormatter()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private const byte Esc = 0x1B;
    private const byte Gs = 0x1D;
    private const byte Lf = 0x0A;

    /// <summary>Formata um <see cref="PrintJob"/> completo, pronto para envio RAW à impressora.</summary>
    public byte[] Format(PrintJob job, PrinterProfile profile)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(profile);

        // Fallback explícito para '?' em vez do best-fit padrão do .NET:
        // caracteres sem representação na code page (ex.: '•', '→', emoji)
        // caem em best-fit imprevisível — no caso de '•' em CP850, vira 0x07
        // (BEL), que faz a impressora bipar em vez de imprimir algo legível.
        // Preferimos um '?' visível a um efeito colateral silencioso.
        var encoding = Encoding.GetEncoding(
            profile.CodePage,
            new EncoderReplacementFallback("?"),
            new DecoderReplacementFallback("?"));
        var buffer = new List<byte>();

        Init(buffer);
        SetCodePage(buffer, profile.EscTIndex);

        var order = job.Order;
        var columns = profile.Columns;

        // printMode ausente == "receipt" (comportamento de hoje, plano §10):
        // agentes/pedidos que não passaram por roteamento não têm o campo.
        var isProduction = job.PrintMode == PrintJobPrintMode.Production;

        // Cabeçalho: nome do restaurante centralizado, dobro de altura.
        SetAlign(buffer, Align.Center);
        SetSize(buffer, doubleWidth: true, doubleHeight: true);
        WriteLine(buffer, encoding, profile, job.Restaurant.Name, columns);
        SetSize(buffer, doubleWidth: false, doubleHeight: false);

        if (!string.IsNullOrWhiteSpace(job.Restaurant.Phone))
        {
            WriteLine(buffer, encoding, profile, job.Restaurant.Phone!, columns);
        }

        // stationLabel já vem pronto em pt-BR do backend (ex. "Cozinha") —
        // o agente não mantém tabela de tradução de target -> texto (plano §10).
        if (!string.IsNullOrWhiteSpace(job.StationLabel))
        {
            SetEmphasis(buffer, on: true);
            WriteLine(buffer, encoding, profile, job.StationLabel!, columns);
            SetEmphasis(buffer, on: false);
        }

        WriteSeparator(buffer, encoding, columns);

        // Pedido: número em esquerda com ênfase, data/hora, tipo de entrega.
        SetAlign(buffer, Align.Left);
        SetEmphasis(buffer, on: true);
        WriteLine(buffer, encoding, profile, $"PEDIDO #{order.Number}", columns);
        SetEmphasis(buffer, on: false);

        WriteLine(buffer, encoding, profile, FormatOrderDateTime(order), columns);

        SetEmphasis(buffer, on: true);
        WriteLine(
            buffer,
            encoding,
            profile,
            order.FulfillmentType == PrintOrderFulfillmentType.Delivery ? "DELIVERY" : "RETIRADA",
            columns);
        SetEmphasis(buffer, on: false);

        WriteSeparator(buffer, encoding, columns);

        // Cliente e endereço.
        WriteLine(buffer, encoding, profile, order.Customer.Name, columns);
        WriteLine(buffer, encoding, profile, order.Customer.Phone, columns);

        if (order.FulfillmentType == PrintOrderFulfillmentType.Delivery && order.Delivery is not null)
        {
            if (!string.IsNullOrWhiteSpace(order.Delivery.Address))
            {
                WriteLine(buffer, encoding, profile, order.Delivery.Address!, columns);
            }

            if (order.Delivery.DistanceKm is double distanceKm)
            {
                WriteLine(
                    buffer,
                    encoding,
                    profile,
                    string.Format(PtBr, "{0:0.0} km", distanceKm),
                    columns);
            }
        }

        WriteSeparator(buffer, encoding, columns);

        // Itens. Em production (comanda de cozinha/bar), nome do item em fonte
        // maior e sem preço — não é recibo fiscal (plano §10).
        foreach (var item in order.Items)
        {
            if (isProduction)
            {
                SetSize(buffer, doubleWidth: false, doubleHeight: true);
                WriteLine(buffer, encoding, profile, $"{item.Quantity}x {item.Name}", columns);
                SetSize(buffer, doubleWidth: false, doubleHeight: false);
            }
            else
            {
                WriteMoneyLine(
                    buffer,
                    encoding,
                    profile,
                    $"{item.Quantity}x {item.Name}",
                    FormatMoney(item.TotalPriceCents),
                    columns);
            }

            foreach (var modifier in item.Modifiers ?? Array.Empty<Modifiers>())
            {
                if (!isProduction && modifier.PriceCents is int priceCents)
                {
                    WriteMoneyLine(buffer, encoding, profile, $"   + {modifier.Name}", FormatMoney(priceCents), columns);
                }
                else
                {
                    // priceCents == null: modificador entra em pricingMode max/average,
                    // o preço não é atribuível a ele. Imprime sem preço, nunca soma nada.
                    // Em production, preço nunca é impresso mesmo quando existe.
                    WriteLine(buffer, encoding, profile, $"   + {modifier.Name}", columns);
                }
            }

            foreach (var comboItem in item.ComboItems ?? Array.Empty<ComboItems>())
            {
                // U+2022 (•) não existe em CP850/CP860 e o fallback padrão do
                // .NET o substitui silenciosamente por 0x07 (BEL) — a
                // impressora bipa em vez de imprimir. U+00B7 (middle dot)
                // existe em CP850 (0xFA), então usamos ele como marcador.
                WriteLine(buffer, encoding, profile, $"   · {comboItem.Name} ({comboItem.Quantity})", columns);
            }
        }

        if (!string.IsNullOrWhiteSpace(order.Notes))
        {
            WriteLine(buffer, encoding, profile, $"obs: {order.Notes}", columns);
        }

        // Preços/pagamento/totais: comanda de produção não é recibo fiscal,
        // essa seção inteira não existe nela (plano §10).
        if (!isProduction)
        {
            WriteSeparator(buffer, encoding, columns);

            // Totais.
            WriteMoneyLine(buffer, encoding, profile, "Subtotal", FormatMoney(order.SubtotalCents), columns);
            if (order.DeliveryFeeCents > 0)
            {
                WriteMoneyLine(buffer, encoding, profile, "Taxa de entrega", FormatMoney(order.DeliveryFeeCents), columns);
            }

            SetSize(buffer, doubleWidth: false, doubleHeight: true);
            WriteMoneyLine(buffer, encoding, profile, "TOTAL", FormatMoney(order.TotalCents), columns);
            SetSize(buffer, doubleWidth: false, doubleHeight: false);

            WriteSeparator(buffer, encoding, columns);

            // Pagamento.
            var payment = order.Payment;
            WriteLine(buffer, encoding, profile, payment.Label, columns);
            if (payment.ChangeForCents is int changeForCents)
            {
                var changeDue = payment.ChangeDueCents is int due ? FormatMoney(due) : "?";
                // "->" em vez de U+2192 (→): a seta não existe em CP850/CP860 e o
                // fallback padrão do .NET a substitui silenciosamente por 0x1A
                // (SUB) em vez de lançar — mesma armadilha do marcador de combo.
                WriteLine(
                    buffer,
                    encoding,
                    profile,
                    $"Troco para {FormatMoney(changeForCents)} -> {changeDue}",
                    columns);
            }
        }

        WriteSeparator(buffer, encoding, columns);

        // Corte parcial, avançando 3 linhas antes.
        Feed(buffer, 3);
        PartialCut(buffer);

        var singleCopy = buffer.ToArray();
        var copies = Math.Max(1, profile.Copies);
        if (copies == 1)
        {
            return singleCopy;
        }

        var result = new byte[singleCopy.Length * copies];
        for (var i = 0; i < copies; i++)
        {
            Array.Copy(singleCopy, 0, result, i * singleCopy.Length, singleCopy.Length);
        }

        return result;
    }

    /// <summary>
    /// Aplica o modo StripAccents (se ligado): normaliza para FormD e descarta
    /// as marcas diacríticas (categoria Unicode NonSpacingMark) antes de codificar.
    /// </summary>
    internal static string ApplyAccentStripping(string text, bool stripAccents)
    {
        if (!stripAccents)
        {
            return text;
        }

        var normalized = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string FormatOrderDateTime(PrintOrder order)
    {
        // order.timezone e opcional no contrato. Se vier ausente, NAO cai no
        // fuso da maquina (e exatamente o que o plano §5.4 proibe) — usa o
        // offset que ja vem embutido no DateTimeOffset, que foi setado pelo
        // servidor.
        var local = string.IsNullOrWhiteSpace(order.Timezone)
            ? order.CreatedAt
            : TimeZoneInfo.ConvertTime(order.CreatedAt, TimeZoneInfo.FindSystemTimeZoneById(order.Timezone));
        return local.ToString("dd/MM/yyyy HH:mm", PtBr);
    }

    internal static string FormatMoney(int cents) => (cents / 100m).ToString("N2", PtBr);

    private static void Init(List<byte> buffer) => buffer.AddRange([Esc, 0x40]);

    private static void SetCodePage(List<byte> buffer, int escTIndex) => buffer.AddRange([Esc, 0x74, (byte)escTIndex]);

    private static void SetAlign(List<byte> buffer, Align align) => buffer.AddRange([Esc, 0x61, (byte)align]);

    private static void SetEmphasis(List<byte> buffer, bool on) => buffer.AddRange([Esc, 0x45, (byte)(on ? 1 : 0)]);

    private static void SetSize(List<byte> buffer, bool doubleWidth, bool doubleHeight)
    {
        byte n = 0;
        if (doubleWidth)
        {
            n |= 0x10;
        }

        if (doubleHeight)
        {
            n |= 0x01;
        }

        buffer.AddRange([Gs, 0x21, n]);
    }

    private static void Feed(List<byte> buffer, int lines) => buffer.AddRange([Esc, 0x64, (byte)lines]);

    private static void PartialCut(List<byte> buffer) => buffer.AddRange([Gs, 0x56, 0x42, 0x03]);

    private static void WriteLine(List<byte> buffer, Encoding encoding, PrinterProfile profile, string text, int columns)
    {
        var toEncode = ApplyAccentStripping(text, profile.StripAccents);
        buffer.AddRange(encoding.GetBytes(toEncode));
        buffer.Add(Lf);
    }

    private static void WriteSeparator(List<byte> buffer, Encoding encoding, int columns)
    {
        buffer.AddRange(encoding.GetBytes(new string('-', columns)));
        buffer.Add(Lf);
    }

    /// <summary>Linha com texto à esquerda e valor monetário alinhado à direita, dentro da largura configurada.</summary>
    private static void WriteMoneyLine(
        List<byte> buffer,
        Encoding encoding,
        PrinterProfile profile,
        string left,
        string amount,
        int columns)
    {
        var leftStripped = ApplyAccentStripping(left, profile.StripAccents);
        var amountStripped = ApplyAccentStripping(amount, profile.StripAccents);

        var availableForLeft = columns - amountStripped.Length - 1;
        if (availableForLeft < 0)
        {
            availableForLeft = 0;
        }

        if (leftStripped.Length > availableForLeft)
        {
            leftStripped = leftStripped[..availableForLeft];
        }

        var padding = columns - leftStripped.Length - amountStripped.Length;
        if (padding < 1)
        {
            padding = 1;
        }

        var line = leftStripped + new string(' ', padding) + amountStripped;
        buffer.AddRange(encoding.GetBytes(line));
        buffer.Add(Lf);
    }

    private enum Align : byte
    {
        Left = 0,
        Center = 1,
        Right = 2,
    }
}
