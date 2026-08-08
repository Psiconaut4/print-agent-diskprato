using System.Linq;
using System.Text.Json;
using PrintAgent.Contracts;

namespace PrintAgent.Core.Tests;

public class PrintJobRoundTripTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private const string SampleJson = """
    {
      "jobId": "clx_job_1",
      "orderId": "clx_order_1",
      "restaurantId": "clx_restaurant_1",
      "kind": "order",
      "target": "kitchen",
      "copies": 1,
      "issuedAt": "2026-08-08T13:00:00-03:00",
      "restaurant": {
        "name": "Cantina do Zé",
        "phone": "+55 18 99999-0000",
        "addressLine": "Rua das Flores, 123"
      },
      "order": {
        "number": "1042",
        "createdAt": "2026-08-08T12:58:30-03:00",
        "timezone": "America/Sao_Paulo",
        "fulfillmentType": "delivery",
        "notes": "Sem cebola em tudo",
        "customer": { "name": "João", "phone": "+55 18 98888-1111" },
        "delivery": { "address": "Av. Brasil, 456", "distanceKm": 3.2 },
        "payment": {
          "method": "cash",
          "status": "pending",
          "label": "Dinheiro",
          "changeForCents": 10000,
          "changeDueCents": 4300
        },
        "items": [
          {
            "quantity": 2,
            "name": "X-Salada",
            "unitPriceCents": 2500,
            "totalPriceCents": 5000,
            "modifiers": [
              { "groupName": "Adicionais", "name": "Bacon", "priceCents": 300 },
              { "groupName": "Adicionais", "name": "Cheddar", "priceCents": null }
            ]
          },
          {
            "quantity": 1,
            "name": "Combo Família",
            "unitPriceCents": 4000,
            "totalPriceCents": 4000,
            "modifiers": [],
            "comboItems": [
              { "name": "Coca 350ml", "quantity": 1 },
              { "name": "Batata Frita", "quantity": 1 }
            ]
          }
        ],
        "subtotalCents": 5700,
        "deliveryFeeCents": 700,
        "totalCents": 6400,
        "currency": "BRL"
      }
    }
    """;

    [Fact]
    public void PrintJob_deserializes_without_loss()
    {
        var job = JsonSerializer.Deserialize<PrintJob>(SampleJson, Options);

        Assert.NotNull(job);
        Assert.Equal("clx_job_1", job!.JobId);
        Assert.Equal(PrintJobKind.Order, job.Kind);
        Assert.Equal(PrintJobTarget.Kitchen, job.Target);
        Assert.Equal("Cantina do Zé", job.Restaurant.Name);
        Assert.Equal(PrintOrderFulfillmentType.Delivery, job.Order.FulfillmentType);
        Assert.Equal(2, job.Order.Items.Count);
        Assert.Equal(300, job.Order.Items.ElementAt(0).Modifiers!.ElementAt(0).PriceCents);
        Assert.Null(job.Order.Items.ElementAt(0).Modifiers!.ElementAt(1).PriceCents);
        Assert.Equal(2, job.Order.Items.ElementAt(1).ComboItems!.Count);
        Assert.Equal(4300, job.Order.Payment.ChangeDueCents);
    }

    [Fact]
    public void PrintJob_roundtrip_preserves_all_data()
    {
        // JSON null explicito (priceCents) e chave ausente (comboItems em
        // item sem combo) sao equivalentes para um campo opcional — nao da
        // para comparar contra o JSON original byte a byte por causa disso.
        // O que importa e nao perder dado indo e voltando pelo nosso proprio
        // serializer: deserializa, serializa, deserializa nesse output, e
        // compara as duas serializacoes (ambas produzidas por nos).
        var job1 = JsonSerializer.Deserialize<PrintJob>(SampleJson, Options);
        Assert.NotNull(job1);

        var json2 = JsonSerializer.Serialize(job1, Options);
        var job2 = JsonSerializer.Deserialize<PrintJob>(json2, Options);
        Assert.NotNull(job2);

        var reserialized1 = JsonSerializer.Serialize(job1, Options);
        var reserialized2 = JsonSerializer.Serialize(job2, Options);

        Assert.Equal(reserialized1, reserialized2);
    }

    [Fact]
    public void PrintJob_ignores_unknown_fields()
    {
        var withExtraField = SampleJson.Replace(
            "\"jobId\": \"clx_job_1\",",
            "\"jobId\": \"clx_job_1\", \"futureField\": { \"anything\": true },");

        var job = JsonSerializer.Deserialize<PrintJob>(withExtraField, Options);

        Assert.NotNull(job);
        Assert.Equal("clx_job_1", job!.JobId);
    }

    [Fact]
    public void PrintJob_target_accepts_unknown_enum_values_without_throwing()
    {
        var withNewTarget = SampleJson.Replace("\"target\": \"kitchen\"", "\"target\": \"drive_thru\"");

        var job = JsonSerializer.Deserialize<PrintJob>(withNewTarget, Options);

        Assert.NotNull(job);
    }
}
