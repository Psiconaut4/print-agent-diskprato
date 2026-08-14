using System.Globalization;
using System.Text;
using PrintAgent.Contracts;

namespace PrintAgent.Core;

/// <summary>
/// Converte um <see cref="PrintJob"/> em bytes ESC/POS crus
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

        // Cabeçalho: número do pedido centralizado, negrito, tamanho dobrado.
        // Nome/telefone do restaurante não aparecem mais no cupom (plano
        // COMANDA-E-NUMERO-PEDIDO.md §Parte B — decisão confirmada).
        SetAlign(buffer, Align.Center);
        SetSize(buffer, doubleWidth: true, doubleHeight: true);
        SetEmphasis(buffer, on: true);
        WriteLine(buffer, encoding, profile, $"---- PEDIDO {order.Number} -----", columns);
        SetEmphasis(buffer, on: false);
        SetSize(buffer, doubleWidth: false, doubleHeight: false);

        // stationLabel já vem pronto em pt-BR do backend (ex. "Cozinha") —
        // o agente não mantém tabela de tradução de target -> texto (plano §10).
        if (!string.IsNullOrWhiteSpace(job.StationLabel))
        {
            SetEmphasis(buffer, on: true);
            WriteLine(buffer, encoding, profile, job.StationLabel!, columns);
            SetEmphasis(buffer, on: false);
        }

        SetEmphasis(buffer, on: true);
        WriteLine(
            buffer,
            encoding,
            profile,
            order.FulfillmentType == PrintOrderFulfillmentType.Delivery ? "ENTREGA" : "RETIRADA",
            columns);
        SetEmphasis(buffer, on: false);

        WriteLine(buffer, encoding, profile, $"Momento do pedido: {FormatOrderDateTime(order)}", columns);

        // Corpo do cupom (cliente, itens, totais) em altura normal — dobrar a
        // altura aqui deixava a fonte grande demais; largura nunca dobra,
        // porque quebraria a conta de dotCount/centralização baseada em
        // profile.Columns.
        SetSize(buffer, doubleWidth: false, doubleHeight: false);

        // Cliente e endereço.
        WriteSectionTitle(buffer, encoding, profile, "INFORMAÇÕES DO CLIENTE", columns);
        SetAlign(buffer, Align.Left);

        // Chave em negrito, valor em fonte normal, cada uma na linha final.
        var clientLines = new List<(string Key, string Value)> { ("Nome: ", order.Customer.Name), ("Número: ", order.Customer.Phone) };
        string? distanceLine = null;
        if (order.FulfillmentType == PrintOrderFulfillmentType.Delivery && order.Delivery is not null)
        {
            if (!string.IsNullOrWhiteSpace(order.Delivery.Address))
            {
                clientLines.Add(("Endereço: ", order.Delivery.Address));
            }

            if (order.Delivery.DistanceKm is double distanceKm)
            {
                distanceLine = string.Format(PtBr, "Distância: {0:0.0} km", distanceKm);
            }
        }

        for (var i = 0; i < clientLines.Count; i++)
        {
            WriteKeyValueLine(buffer, encoding, profile, clientLines[i].Key, clientLines[i].Value, columns);
            if (i < clientLines.Count - 1 || distanceLine is not null)
            {
                WriteBlankLine(buffer);
            }
        }

        if (distanceLine is not null)
        {
            WriteLine(buffer, encoding, profile, distanceLine, columns);
        }

        // Itens. Em production (comanda de cozinha/bar), sem preço — não é
        // recibo fiscal (plano §10). Uma linha em branco entre itens.
        SetAlign(buffer, Align.Center);
        WriteSectionTitle(buffer, encoding, profile, "DETALHES DO PEDIDO", columns);
        SetAlign(buffer, Align.Left);

        // Cada subitem (item, modificador, componente de combo) pula uma
        // linha depois de si — sem isso os complementos ficam grudados uns
        // nos outros.
        foreach (var item in order.Items)
        {
            if (isProduction)
            {
                WriteLine(buffer, encoding, profile, $"{item.Quantity}x {item.Name}", columns);
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

            WriteBlankLine(buffer);

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

                WriteBlankLine(buffer);
            }

            foreach (var comboItem in item.ComboItems ?? Array.Empty<ComboItems>())
            {
                // U+2022 (•) não existe em CP850/CP860 e o fallback padrão do
                // .NET o substitui silenciosamente por 0x07 (BEL) — a
                // impressora bipa em vez de imprimir. U+00B7 (middle dot)
                // existe em CP850 (0xFA), então usamos ele como marcador.
                WriteLine(buffer, encoding, profile, $"   · {comboItem.Name} ({comboItem.Quantity})", columns);
                WriteBlankLine(buffer);
            }
        }

        if (!string.IsNullOrWhiteSpace(order.Notes))
        {
            WriteLine(buffer, encoding, profile, $"obs: {order.Notes}", columns);
        }

        // Preços/pagamento/totais: comanda de produção não é recibo fiscal.
        // Ordem: Taxa de entrega -> Subtotal -> TOTAL.
        if (!isProduction)
        {
            WriteSeparator(buffer, encoding, columns);

            if (order.DeliveryFeeCents > 0)
            {
                WriteMoneyLine(buffer, encoding, profile, "Taxa de entrega", FormatMoney(order.DeliveryFeeCents), columns);
            }

            WriteMoneyLine(buffer, encoding, profile, "Subtotal", FormatMoney(order.SubtotalCents), columns, boldLabel: true);

            WriteMoneyLine(buffer, encoding, profile, "TOTAL", FormatMoney(order.TotalCents), columns, boldLabel: true);

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

        SetSize(buffer, doubleWidth: false, doubleHeight: false);
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

    private static void WriteBlankLine(List<byte> buffer) => buffer.Add(Lf);

    /// <summary>Separador, título centralizado em negrito, separador — abre uma seção do cupom.</summary>
    private static void WriteSectionTitle(List<byte> buffer, Encoding encoding, PrinterProfile profile, string title, int columns)
    {
        WriteSeparator(buffer, encoding, columns);
        SetEmphasis(buffer, on: true);
        WriteLine(buffer, encoding, profile, title, columns);
        SetEmphasis(buffer, on: false);
        WriteSeparator(buffer, encoding, columns);
    }

    /// <summary>Linha "chave: valor" — chave em negrito, valor em fonte normal.</summary>
    private static void WriteKeyValueLine(List<byte> buffer, Encoding encoding, PrinterProfile profile, string key, string value, int columns)
    {
        var keyStripped = ApplyAccentStripping(key, profile.StripAccents);
        var valueStripped = ApplyAccentStripping(value, profile.StripAccents);

        SetEmphasis(buffer, on: true);
        buffer.AddRange(encoding.GetBytes(keyStripped));
        SetEmphasis(buffer, on: false);
        buffer.AddRange(encoding.GetBytes(valueStripped));
        buffer.Add(Lf);
    }

    /// <summary>Linha com texto à esquerda e valor monetário alinhado à direita, ligados por pontilhado (dot leaders).</summary>
    private static void WriteMoneyLine(
        List<byte> buffer,
        Encoding encoding,
        PrinterProfile profile,
        string left,
        string amount,
        int columns,
        bool boldLabel = false)
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

        // Margem de um espaço de cada lado dos pontos.
        var dotCount = columns - leftStripped.Length - amountStripped.Length - 2;

        string rest;
        if (dotCount < 3)
        {
            // Não cabe pontilhado com folga: cai no preenchimento por
            // espaço em branco de sempre (leftStripped já foi truncado acima).
            var padding = columns - leftStripped.Length - amountStripped.Length;
            if (padding < 1)
            {
                padding = 1;
            }

            rest = new string(' ', padding) + amountStripped;
        }
        else
        {
            rest = " " + new string('.', dotCount) + " " + amountStripped;
        }

        if (boldLabel)
        {
            SetEmphasis(buffer, on: true);
            buffer.AddRange(encoding.GetBytes(leftStripped));
            SetEmphasis(buffer, on: false);
            buffer.AddRange(encoding.GetBytes(rest));
        }
        else
        {
            buffer.AddRange(encoding.GetBytes(leftStripped + rest));
        }

        buffer.Add(Lf);
    }

    private enum Align : byte
    {
        Left = 0,
        Center = 1,
        Right = 2,
    }
}
