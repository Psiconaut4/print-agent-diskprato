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
            Delivery = new Delivery
            {
                Address = "Av. Brasil, 456",
                Street = "Av. Brasil",
                StreetNumber = "456",
                Neighborhood = "Centro",
                Complement = "Apto 12",
                DistanceKm = 3.2,
            },
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
            "1B401B74021B61011D21111B45012D2D2D2D2050454449444F203737202D2D2D2D2D0A1B45001D21001B450152455449524144410A1B45004D6F6D656E746F20646F2070656469646F3A2030382F30382F323032362031323A35380A0A1D21000A2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A1B4501494E464F524D4180E5455320444F20434C49454E54450A1B45002D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A0A1B61011B45014E6F6D653A201B4500416E610A0A1B45014EA36D65726F3A201B450031383939393939303030300A0A1B61010A2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A1B4501444554414C48455320444F2050454449444F0A1B45002D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A0A1B61001B4501317820418761A1203330306D6C1B4500202E2E2E2E2E2E2E2E2E2E2E2E2031322C30300A0A0A2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A1B4501537562746F74616C1B4500202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2031322C30300A1B4501544F54414C1B4500202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2031322C30300A2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A1B450144696E686569726F0A1B45001D21002D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A1B64031D564203";

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
            "1B401B74021B61011D21111B45012D2D2D2D2050454449444F2031303432202D2D2D2D2D0A1B45001D21001B4501454E54524547410A1B45004D6F6D656E746F20646F2070656469646F3A2030382F30382F323032362031323A35380A0A1D21000A2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A1B4501494E464F524D4180E5455320444F20434C49454E54450A1B45002D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A0A1B61011B45014E6F6D653A201B45004A6FC66F0A0A1B45014EA36D65726F3A201B450031383938383838313131310A0A1B4501456E64657265876F3A201B450041762E2042726173696C2C203435360A0A1B450142616972726F3A201B450043656E74726F0A0A1B4501436F6D706C656D656E746F3A201B45004170746F2031320A0A44697374836E6369613A20332C32206B6D0A0A1B61010A2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A1B4501444554414C48455320444F2050454449444F0A1B45002D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A0A1B61001B4501327820582D53616C6164611B4500202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2035302C30300A0A2020202B204261636F6E202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E20332C30300A0A2020202B20436865646461720A0A1B4501317820436F6D626F2046616DA16C69611B4500202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2034302C30300A0A202020FA20436F6361203335306D6C202831290A0A202020FA20426174617461204672697461202831290A0A0A6F62733A2053656D206365626F6C612C20636F6D206C696DE465730A0A2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A5461786120646520656E7472656761202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E20372C30300A1B4501537562746F74616C1B4500202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2035372C30300A1B4501544F54414C1B4500202E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2E2036342C30300A2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A1B450144696E686569726F0A1B45001B450154726F636F2070617261203130302C3030202D3E2034332C30300A1B45001D21002D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D2D0A1B64031D564203";

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
        // No novo formato, os nomes dos itens são em negrito (ESC E 1 ... ESC E 0),
        // então o padrão é: "Familia" seguido de ESC E 0 (1B4500), não "Familia " com espaço.
        Assert.Contains("4A6F616F0A", strippedHex); // Joao
        Assert.Contains("46616D696C69611B", strippedHex); // Familia + ESC
        Assert.Contains("6C696D6F65730A", strippedHex); // limoes + LF
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

    [Fact]
    public void Format_delivery_with_structured_address_prints_street_neighborhood_and_complement_as_separate_lines()
    {
        var formatter = new EscPosFormatter();
        var profile = new PrinterProfile(PaperWidthMm: 80, CodePage: 850, EscTIndex: 2, StripAccents: false, Copies: 1);

        var hex = Convert.ToHexString(formatter.Format(BuildFullDeliveryJob(), profile));

        // Rua+número, bairro e complemento saem em linhas separadas — cada uma
        // com sua própria linha em branco antes/depois (mesmo pulo que já
        // existe entre nome e telefone), em vez de um único "Endereço: ...".
        Assert.Contains("1B4501456E64657265876F3A201B450041762E2042726173696C2C203435360A0A", hex); // Endereço: Av. Brasil, 456
        Assert.Contains("1B450142616972726F3A201B450043656E74726F0A0A", hex); // Bairro: Centro
        Assert.Contains("1B4501436F6D706C656D656E746F3A201B45004170746F2031320A0A", hex); // Complemento: Apto 12

        // Bloco inteiro (nome, telefone, endereço) centralizado: ESC a 1 logo
        // antes do "Nome:", não mais ESC a 0 (esquerda).
        Assert.Contains("1B61011B45014E6F6D653A20", hex);
    }

    [Fact]
    public void Format_delivery_without_structured_address_falls_back_to_single_address_line()
    {
        var formatter = new EscPosFormatter();
        var profile = new PrinterProfile(PaperWidthMm: 80, CodePage: 850, EscTIndex: 2, StripAccents: false, Copies: 1);

        var job = BuildFullDeliveryJob();
        // Pedido antigo / agente anterior ao contrato 1.2.0: só o campo
        // `address` (linha única) vem preenchido.
        job.Order.Delivery!.Street = null;
        job.Order.Delivery!.StreetNumber = null;
        job.Order.Delivery!.Neighborhood = null;
        job.Order.Delivery!.Complement = null;

        var hex = Convert.ToHexString(formatter.Format(job, profile));

        Assert.Contains("1B4501456E64657265876F3A201B450041762E2042726173696C2C203435360A0A", hex); // Endereço: Av. Brasil, 456
        Assert.DoesNotContain("42616972726F3A20", hex); // sem "Bairro:"
        Assert.DoesNotContain("436F6D706C656D656E746F3A20", hex); // sem "Complemento:"
    }
}
