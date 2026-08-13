using System.Text;
using PrintAgent.Contracts;
using PrintAgent.Core;

namespace PrintAgent.Core.Tests;

/// <summary>
/// Golden tests em hex (plano §5, "o bug mais provável do projeto"). Cada
/// hex abaixo foi obtido rodando o formatter de verdade, decodificado à mão
/// byte a byte (comandos ESC/POS, alinhamento/ênfase, texto em CP850) e só
/// então fixado como constante — não é hex inventado de cabeça.
/// </summary>
public class EscPosFormatterTests
{
    private static PrintJob BuildMinimalPickupJob() => new()
    {
        JobId = "job_1",
        OrderId = "order_1",
        RestaurantId = "rest_1",
        Kind = PrintJobKind.Order,
        Target = PrintJobTarget.Kitchen,
        Copies = 1,
        IssuedAt = DateTimeOffset.Parse("2026-08-08T13:00:00-03:00"),
        Restaurant = new Restaurant2
        {
            Name = "Café Açaí", // ç
            Phone = null,
            AddressLine = null,
        },
        Order = new PrintOrder
        {
            Number = "77",
            CreatedAt = DateTimeOffset.Parse("2026-08-08T12:58:30-03:00"),
            Timezone = "America/Sao_Paulo",
            FulfillmentType = PrintOrderFulfillmentType.Pickup,
            Notes = null,
            Customer = new Customer { Name = "Ana", Phone = "18999990000" },
            Delivery = null,
            Payment = new PrintPayment
            {
                Method = PrintPaymentMethod.Cash,
                Status = PrintPaymentStatus.Pending,
                Label = "Dinheiro",
                ChangeForCents = null,
                ChangeDueCents = null,
            },
            Items = new List<PrintItem>
            {
                new()
                {
                    Quantity = 1,
                    Name = "Açaí 300ml",
                    UnitPriceCents = 1200,
                    TotalPriceCents = 1200,
                    Modifiers = new List<Modifiers>(),
                    ComboItems = new List<ComboItems>(),
                },
            },
            SubtotalCents = 1200,
            DeliveryFeeCents = 0,
            TotalCents = 1200,
            Currency = PrintOrderCurrency.BRL,
        },
    };

    private static PrintJob BuildFullDeliveryJob() => new()
    {
        JobId = "job_1",
        OrderId = "order_1",
        RestaurantId = "rest_1",
        Kind = PrintJobKind.Order,
        Target = PrintJobTarget.Kitchen,
        Copies = 1,
        IssuedAt = DateTimeOffset.Parse("2026-08-08T13:00:00-03:00"),
        Restaurant = new Restaurant2
        {
            Name = "Cantina do Zé", // é
            Phone = "1899990000",
            AddressLine = null,
        },
        Order = new PrintOrder
        {
            Number = "1042",
            CreatedAt = DateTimeOffset.Parse("2026-08-08T12:58:30-03:00"),
            Timezone = "America/Sao_Paulo",
            FulfillmentType = PrintOrderFulfillmentType.Delivery,
            Notes = "Sem cebola, com limões", // õ
            Customer = new Customer { Name = "João", Phone = "18988881111" }, // ã
            Delivery = new Delivery { Address = "Av. Brasil, 456", DistanceKm = 3.2 },
            Payment = new PrintPayment
            {
                Method = PrintPaymentMethod.Cash,
                Status = PrintPaymentStatus.Pending,
                Label = "Dinheiro",
                ChangeForCents = 10000,
                ChangeDueCents = 4300,
            },
            Items = new List<PrintItem>
            {
                new()
                {
                    Quantity = 2,
                    Name = "X-Salada",
                    UnitPriceCents = 2500,
                    TotalPriceCents = 5000,
                    Modifiers = new List<Modifiers>
                    {
                        new() { GroupName = "Adicionais", Name = "Bacon", PriceCents = 300 },
                        new() { GroupName = "Adicionais", Name = "Cheddar", PriceCents = null },
                    },
                    ComboItems = new List<ComboItems>(),
                },
                new()
                {
                    Quantity = 1,
                    Name = "Combo Família", // í (marca também que acentuação não se limita às 4 letras citadas no plano)
                    UnitPriceCents = 4000,
                    TotalPriceCents = 4000,
                    Modifiers = new List<Modifiers>(),
                    ComboItems = new List<ComboItems>
                    {
                        new() { Name = "Coca 350ml", Quantity = 1 },
                        new() { Name = "Batata Frita", Quantity = 1 },
                    },
                },
            },
            SubtotalCents = 5700,
            DeliveryFeeCents = 700,
            TotalCents = 6400,
            Currency = PrintOrderCurrency.BRL,
        },
    };

    [Fact]
    public void CP850_encodes_accented_letters_as_expected_by_the_golden_tests()
    {
        // Pin test: se o runtime do CI ou o provider de code pages mudar de
        // comportamento, este teste falha primeiro e aponta exatamente para
        // a causa (plano §5.2 — a armadilha mais provável do projeto).
        var encoding = Encoding.GetEncoding(850);

        Assert.Equal("87", Convert.ToHexString(encoding.GetBytes("ç")));
        Assert.Equal("C6", Convert.ToHexString(encoding.GetBytes("ã")));
        Assert.Equal("E4", Convert.ToHexString(encoding.GetBytes("õ")));
        Assert.Equal("82", Convert.ToHexString(encoding.GetBytes("é")));
    }

    [Fact]
    public void Format_58mm_pickup_order_matches_golden_bytes()
    {
        var formatter = new EscPosFormatter();
        var profile = new PrinterProfile(PaperWidthMm: 58, CodePage: 850, EscTIndex: 2, StripAccents: false, Copies: 1);

        var bytes = formatter.Format(BuildMinimalPickupJob(), profile);

        const string expectedHex =
            "1B401B74021B61011D21111B45012D2D2D2D2050454449444F203737202D2D2D2D2D0A1B45001D21001B450152455449524144410A" +
            "1B45004D6F6D656E746F20646F2070656469646F3A2030382F30382F323032362031323A35380A" +
            "1D21012D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A" +
            "494E464F524D4180E5455320444F20434C49454E54450A2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A" +
            "1B61004E6F6D653A20416E610A0A4EA36D65726F3A2031383939393939303030300A" +
            "1B61012D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A" +
            "444554414C48455320444F2050454449444F0A2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A" +
            "1B6100317820418761A1203330306D6C202E2E2E2E2E2E2E2E2E2E2E2E2031322C30300A0A" +
            "2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A" +
            "537562746F74616C202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2031322C30300A" +
            "544F54414C202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2031322C30300A" +
            "2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A" +
            "44696E686569726F0A1D21002D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A1B64031D564203";

        Assert.Equal(expectedHex, Convert.ToHexString(bytes));

        // Sanidades de estrutura, para quando o teste falhar dar um sinal mais direto:
        Assert.StartsWith("1B40", Convert.ToHexString(bytes)); // ESC @ sempre primeiro
        Assert.EndsWith("1D564203", Convert.ToHexString(bytes)); // corte parcial sempre por último
        Assert.Equal(32, profile.Columns); // 58mm => 32 colunas
    }

    [Fact]
    public void Format_80mm_delivery_order_with_combo_modifiers_and_change_matches_golden_bytes()
    {
        var formatter = new EscPosFormatter();
        var profile = new PrinterProfile(PaperWidthMm: 80, CodePage: 850, EscTIndex: 2, StripAccents: false, Copies: 1);

        var bytes = formatter.Format(BuildFullDeliveryJob(), profile);
        var hex = Convert.ToHexString(bytes);

        const string expectedHex =
            "1B401B74021B61011D21111B45012D2D2D2D2050454449444F2031303432202D2D2D2D2D0A1B45001D21001B4501454E54524547410A" +
            "1B45004D6F6D656E746F20646F2070656469646F3A2030382F30382F323032362031323A35380A" +
            "1D21012D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A" +
            "494E464F524D4180E5455320444F20434C49454E54450A2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A" +
            "1B61004E6F6D653A204A6FC66F0A0A4EA36D65726F3A2031383938383838313131310A0A" +
            "456E64657265876F3A2041762E2042726173696C2C203435360A0A44697374836E6369613A20332C32206B6D0A" +
            "1B61012D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A" +
            "444554414C48455320444F2050454449444F0A2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A" +
            "1B6100327820582D53616C616461202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2035302C30300A" +
            "2020202B204261636F6E202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E20332C30300A" +
            "2020202B20436865646461720A0A" +
            "317820436F6D626F2046616DA16C6961202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2034302C30300A" +
            "202020FA20436F6361203335306D6C202831290A202020FA20426174617461204672697461202831290A0A" +
            "6F62733A2053656D206365626F6C612C20636F6D206C696DE465730A" +
            "2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A" +
            "5461786120646520656E7472656761202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E20372C30300A" +
            "537562746F74616C202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2035372C30300A" +
            "544F54414C202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2036342C30300A" +
            "2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A" +
            "44696E686569726F0A54726F636F2070617261203130302C3030202D3E2034332C30300A" +
            "1D21002D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A" +
            "1B64031D564203";

        Assert.Equal(expectedHex, hex);

        // 0xFA (middle dot, CP850) marca comboItems — não 0x07 (BEL), que é o
        // que o fallback padrão do .NET produziria silenciosamente para o
        // caractere '•' (U+2022), inexistente em CP850.
        Assert.Contains("FA20436F6361", hex);

        // "->" no lugar de "→" (U+2192 também não existe em CP850).
        Assert.Contains("202D3E20", hex);

        // Modificador sem preço (Cheddar) não deve ter nenhum valor monetário
        // colado ao nome — a linha termina em LF logo após o nome.
        Assert.Contains("436865646461720A", hex);
    }

    [Fact]
    public void Format_with_StripAccents_removes_diacritics_but_keeps_line_lengths()
    {
        var formatter = new EscPosFormatter();
        var profileWithAccents = new PrinterProfile(PaperWidthMm: 80, CodePage: 850, EscTIndex: 2, StripAccents: false, Copies: 1);
        var profileStripped = profileWithAccents with { StripAccents = true };

        var withAccents = formatter.Format(BuildFullDeliveryJob(), profileWithAccents);
        var stripped = formatter.Format(BuildFullDeliveryJob(), profileStripped);

        var strippedHex = Convert.ToHexString(stripped);

        // Mesmo comprimento: toda letra acentuada em CP850 ocupa 1 byte, então
        // remover o acento não muda a paginação/alinhamento do cupom.
        Assert.Equal(withAccents.Length, stripped.Length);
        Assert.NotEqual(Convert.ToHexString(withAccents), strippedHex);

        // "João" -> "Joao" (0xC6 vira 0x61), "Família" -> "Familia" (0xA1 vira
        // 0x69), "limões" -> "limoes" (0xE4 vira 0x6F).
        Assert.Contains("4A6F616F0A", strippedHex); // Joao
        Assert.Contains("46616D696C696120", strippedHex); // Familia
        Assert.Contains("6C696D6F6573", strippedHex); // limoes
    }

    [Fact]
    public void Format_uses_configured_EscTIndex_for_the_code_page_command()
    {
        var formatter = new EscPosFormatter();
        var profile = new PrinterProfile(PaperWidthMm: 80, CodePage: 850, EscTIndex: 3, StripAccents: false, Copies: 1);

        var bytes = formatter.Format(BuildMinimalPickupJob(), profile);

        // ESC t 3 logo após o ESC @ inicial.
        Assert.Equal([0x1B, 0x40, 0x1B, 0x74, 0x03], bytes[..5]);
    }

    [Fact]
    public void Format_production_mode_omits_prices_and_uses_bigger_item_font()
    {
        var formatter = new EscPosFormatter();
        var profile = new PrinterProfile(PaperWidthMm: 80, CodePage: 850, EscTIndex: 2, StripAccents: false, Copies: 1);

        var job = BuildFullDeliveryJob();
        job.PrintMode = PrintJobPrintMode.Production;
        job.StationLabel = "Cozinha";

        var hex = Convert.ToHexString(formatter.Format(job, profile));

        // Nenhum valor monetário do pedido aparece: nem preço de item, nem
        // modificador com preço, nem subtotal/taxa/total/pagamento/troco.
        Assert.DoesNotContain("35302C3030", hex); // "50,00" (X-Salada)
        Assert.DoesNotContain("332C3030", hex); // "3,00" (Bacon)
        Assert.DoesNotContain("537562746F74616C", hex); // "Subtotal"
        Assert.DoesNotContain("544F54414C", hex); // "TOTAL"
        Assert.DoesNotContain("44696E686569726F", hex); // "Dinheiro" (payment.Label)
        Assert.DoesNotContain("54726F636F", hex); // "Troco"

        // stationLabel aparece no cabeçalho, com ênfase (ESC E 1 ... LF ... ESC E 0).
        Assert.Contains("1B4501436F7A696E68610A1B4500", hex);

        // Nome do item ainda aparece, sem preço colado — o corpo inteiro do
        // cupom (não só o item) já está em GS ! 01 (dobro de altura) desde
        // antes da seção de cliente, então não há mais um toggle local por item.
        Assert.Contains("327820582D53616C6164610A", hex);
    }

    [Fact]
    public void Format_without_stationLabel_or_printMode_matches_receipt_behavior()
    {
        var formatter = new EscPosFormatter();
        var profile = new PrinterProfile(PaperWidthMm: 80, CodePage: 850, EscTIndex: 2, StripAccents: false, Copies: 1);

        // Job sem os campos novos (x-since 1.1.0) — cobre agente/pedido
        // antigo que não passou por roteamento (plano §10).
        var job = BuildFullDeliveryJob();
        Assert.Null(job.PrintMode);
        Assert.Null(job.StationLabel);

        var withoutFields = Convert.ToHexString(formatter.Format(job, profile));

        job.PrintMode = PrintJobPrintMode.Receipt;
        var explicitReceipt = Convert.ToHexString(formatter.Format(job, profile));

        Assert.Equal(explicitReceipt, withoutFields);
    }

    [Fact]
    public void Format_multiplies_output_by_copies()
    {
        var formatter = new EscPosFormatter();
        var profile = new PrinterProfile(PaperWidthMm: 80, CodePage: 850, EscTIndex: 2, StripAccents: false, Copies: 1);

        var single = formatter.Format(BuildMinimalPickupJob(), profile);
        var triple = formatter.Format(BuildMinimalPickupJob(), profile with { Copies = 3 });

        Assert.Equal(single.Length * 3, triple.Length);
        Assert.Equal(Convert.ToHexString(single), Convert.ToHexString(triple[..single.Length]));
    }
}
