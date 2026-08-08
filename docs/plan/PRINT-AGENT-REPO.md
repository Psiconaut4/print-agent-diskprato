# DiskPrato Print Agent — plano de construção do repositório

Documento de planejamento do repositório `diskprato-print-agent`. Cobre a
construção do agente .NET, fase a fase, e como ele conversa com a API do
DiskPrato.

**Fonte da verdade do contrato:** `contracts/print-agent/v1.openapi.json`,
no repositório do DiskPrato. Este documento **não** redefine o contrato —
descreve como o agente o consome. Quando os dois divergirem, o OpenAPI vence.

---

## 0. Status atual (atualizado em 2026-08-08)

Progresso frente às fases descritas em §8. Cada commit corresponde a um
bloco fechado; `dotnet build`/`dotnet test` na raiz do repo passam limpos
(0 avisos, 0 erros) a cada etapa concluída.

| Fase | Projeto | Status |
|---|---|---|
| 0 — bootstrap | solution, CI | ✅ feito (`feat(scaffold)`) |
| 1 — Contracts | `PrintAgent.Contracts` | ✅ feito (`feat(contracts)`, `fix(contracts)`) |
| 2 — ESC/POS | `PrintAgent.Core` (`EscPosFormatter`) | ✅ feito (`feat(core)`) |
| 2/5 — transportes | `PrintAgent.Printing` (Spooler + Network) | ✅ feito (`feat(printing)`) |
| 3 — API do backend | `PrintAgent.Transport` (HTTP + SSE) | ✅ feito (`feat(transport)`) |
| 4 — Worker Service | `PrintAgent.Host` | ✅ feito (`feat(host)`) — fila local em arquivo desde o refactor de 2026-08-08 |
| 6 — tray/setup | `PrintAgent.Tray` | ✅ feito (`feat(tray)`) — pendente validação manual (ver abaixo) |
| 7 — instalador | WiX | ⏳ não iniciado |
| 8 — endurecimento | Serilog, diagnóstico | ⏳ não iniciado |

**O que foi decidido/ajustado em relação ao texto original do plano:**
- A divisão real entre `Core` e `Printing` segue a árvore de `tests/` do
  §3 (que já estava certa), não a frase solta de `PrintAgent.Core/` em §3
  que mencionava "formatação ESC/POS" — o formatador (`EscPosFormatter`)
  é puro e mora em `PrintAgent.Core`; `PrintAgent.Printing` só transporta
  `byte[]` já formatado até a impressora (Spooler/Network), sem saber nada
  de `PrintJob`.
- Corrigido um bug real do codegen do NSwag: o `JsonStringEnumConverter`
  padrão ignora o `[EnumMember]` gerado e serializava enums pelo nome do
  membro C# (`"Cash"` em vez de `"cash"`), o que quebraria toda escrita
  para a API real. Ver `patch-enum-handling.ps1` e
  `TolerantEnumConverterFactory` em `PrintAgent.Contracts`.
- Propriedades opcionais de tipo valor (`ApiError.Code`,
  `AckRequest.PrintedAt`) agora geram como nuláveis
  (`/GenerateOptionalPropertiesAsNullable:true`) — sem isso, um campo
  ausente no JSON virava silenciosamente o membro `0` do enum ou
  `0001-01-01`, em vez de `null`.
- `PrintOrder.Timezone` é opcional no schema (não está na lista
  `required`). Quando ausente, `EscPosFormatter` usa o offset já embutido
  no `DateTimeOffset` em vez de cair no fuso da máquina — nunca o
  comportamento que o plano §5.4 proíbe.
- `PrintOrchestrator` (Fase 4) nunca fala com a API diretamente, nem para
  mandar ack: grava em `pending_acks` (síncrono, sem rede) e devolve. Quem
  efetivamente tenta enviar pela rede é o `AckFlusher`, num loop separado.
  Motivo: `JobsApiClient.AckJobAsync` já re-tenta indefinidamente para
  5xx/rede (por design, §6.6) — se essa espera acontecesse dentro do
  caminho de impressão, um backend fora do ar travaria a impressão de
  pedidos novos também. `AckFlusher` limita cada tentativa a um timeout
  curto e deixa o resto pra próxima rodada.
- Named pipe (`AgentController`/`NamedPipeIpcServer`, §7.4) implementado já
  na Fase 4, adiantado — o Tray (Fase 6) ainda não existe, mas o protocolo
  (`get-status`/`pair`/`unpair`/`set-printer`/`test-print`, JSON por linha)
  já está pronto para ele consumir.
- **Refatorado em 2026-08-08: fila local trocada de SQLite para arquivos.**
  A Fase 4 tinha sido entregue com `Microsoft.Data.Sqlite` (schema em
  `jobs`/`printed`/`pending_acks`). Decisão revertida ainda dentro da
  janela de retrabalho aceitável — nenhum outro componente depende do
  formato de persistência, e o volume real (dezenas de jobs simultâneos,
  no máximo) não justifica um banco relacional embutido. Motivos:
  arquivo corrompido perde um job; banco corrompido perde a fila inteira.
  E o suporte consegue abrir a pasta pelo Explorer sem instalar ferramenta
  nenhuma. `JobStore` foi reescrito sobre `queue/pending/`, `queue/printed/`
  e `queue/failed/` (§7.1); `Microsoft.Data.Sqlite` saiu do projeto e do
  instalador. `PrintOrchestrator` e `AckFlusher` não mudaram de
  responsabilidade, só a implementação de `JobStore` por baixo. A tabela
  `pending_acks` também saiu: o estado de ack (`acked`/`lastAckAttemptAt`/
  `lastAckError`) agora mora dentro do próprio arquivo `printed/<jobId>.json`
  ou `failed/<jobId>.json` — os dois esquemas também ganharam `attempts` (e
  `failed/` ganhou `errorCode`/`errorMessage`) para o `AckFlusher` conseguir
  remontar o `AckRequest` sem um blob de ack separado.
- **Fase 6 (`PrintAgent.Tray`) feita em 2026-08-08.** `ApplicationContext`
  com `NotifyIcon` (sem janela principal — ícone gerado em memória, sem
  arquivo `.ico`) e uma `SetupForm` em WinForms puro (sem designer
  serializado) consumindo o named pipe pelo protocolo já existente. Tray
  não referencia `PrintAgent.Host`: os dois falam só pelo JSON do pipe,
  com DTOs espelhados no lado do Tray (`Ipc/IpcContracts.cs`) — Tray e
  serviço continuam deployables/processos independentes de propósito
  (Session 0 isolation). Duas extensões pequenas no protocolo do pipe para
  a tela de setup funcionar de verdade:
  - Novo comando `get-config`, devolve o `PrinterConfig` atual (nenhum
    outro comando expunha isso) — necessário pra tela pré-preencher os
    campos ao abrir, em vez de sempre partir de valores em branco.
  - `AgentStatusSnapshot` ganhou `PrinterStatus` (texto:
    `Ready`/`Offline`/`PaperOut`/`CoverOpen`/`Unknown`), lido via
    `IPrinterStatusQuery.QueryStatusAsync` com teto de 2s em
    `AgentController.GetStatusAsync` — separado do `StatusReport` que o
    `Worker` manda pro backend (esse continua com o TODO da Fase 8 em
    aberto, §6 não mudou). É só o que faz o ícone da bandeja/tela de setup
    mostrarem "impressora ok" de verdade, e nunca inventa "pronta" sem
    saber (§5.3).

**Pendências manuais (não automatizáveis por um agente):**
- O teste de convivência do plano §8 Fase 2 exige uma fila de impressão
  Windows real apontada para a porta `FILE:` (impressora "Generic / Text
  Only"), provisionada manualmente numa máquina com sessão desktop.
  `SpoolerPrinterTransport` está implementado conforme a spec e tem o
  caminho de erro coberto por teste automatizado, mas o golden-bytes test e
  o teste de "PDV fake concorrente" contra impressora real ficam para
  depois.
- O loop principal do `Worker` e o servidor do named pipe (Fase 4) não têm
  teste automatizado — são integração pura (SSE + HTTP + IPC reais contra
  backend de verdade). `JobStore` e `PrintOrchestrator`, que concentram a
  lógica de decisão, estão cobertos por teste contra um diretório temporário
  real (sem mock de filesystem) e um `IPrinterTransport` fake.
- `PrintAgent.Tray` (Fase 6) não tem teste automatizado — é UI WinForms
  falando com um processo real pelo named pipe, o critério de aceite da
  Fase 6 (§8) é manual por natureza: "instalação limpa vai de zero a cupom
  de teste impresso sem abrir terminal nem editar arquivo". Validação
  pendente antes de embalar a Fase 7.

**Próximo passo:** Fase 7 (instalador WiX) — depois da validação manual do
Tray. `ServiceInstall`/`ServiceControl` do serviço, tray no startup do
usuário, ACL do `%ProgramData%`, desinstalação limpa.

---

## 1. O que o agente é

Um serviço do Windows que fica na máquina do balcão, mantém uma conexão com
a API do DiskPrato e imprime cupons de pedido em impressora térmica ESC/POS.

Três coisas que ele **não** é, e que definem o desenho:

- **Não é o dono da impressora.** Precisa conviver com o PDV que o
  restaurante já usa. Isso elimina acesso USB direto como caminho padrão
  (§4).
- **Não faz conta.** Todo valor chega em centavos inteiros já calculado pelo
  servidor. O agente formata e imprime. Ele nunca soma preços de
  modificadores, nunca calcula troco, nunca recalcula total.
- **Não é uma fonte de verdade.** Se ele perder um pedido, a API sabe: o job
  continua pendente do lado do servidor até um `ack` chegar.

---

## 2. Stack e decisões de base

| Item | Escolha | Por quê |
|---|---|---|
| Runtime | .NET 10 (LTS) | suporte até nov/2028 |
| Deploy | self-contained, `win-x64`, single-file | a máquina da loja não precisa ter runtime instalado; instalar runtime na loja é suporte técnico que não escala |
| Trimming | **desligado** | codegen do NSwag e serialização por reflexão quebram com trim; o ganho de tamanho não paga o risco de falha só em produção |
| Serviço | `Microsoft.Extensions.Hosting.WindowsServices` | start automático no boot, lifecycle, logging |
| UI | WinForms (`net10.0-windows`), processo separado | tray icon + tela de setup; separado do serviço porque serviço do Windows não tem sessão de desktop (Session 0 isolation) |
| Fila local | arquivo — um `.json` por job em `%ProgramData%\...\queue\` | precisa sobreviver a reboot; volume é dezenas de jobs, não milhares — `File.Move` atômico no NTFS supre a durabilidade sem o custo de um banco embutido (ver §7.1) |
| Instalador | WiX v5 → `.msi` | `ServiceInstall`/`ServiceControl` nativos |

Pacotes NuGet principais:

```
Microsoft.Extensions.Hosting
Microsoft.Extensions.Hosting.WindowsServices
Microsoft.Extensions.Http.Resilience     # retry/backoff nas chamadas HTTP
System.Security.Cryptography.ProtectedData   # DPAPI para o token
System.Text.Encoding.CodePages           # CP850/CP860 (ver §5.2)
Serilog.Extensions.Hosting + Serilog.Sinks.File
NSwag.MSBuild                            # codegen do contrato (dev)
```

Sem `ESCPOS.NET` nem equivalente. As libs prontas do ecossistema assumem
posse do dispositivo (USB direto) — exatamente o que o requisito de
convivência proíbe. A formatação ESC/POS que precisamos é umas 200 linhas e
fica sob nosso controle.

---

## 3. Estrutura da solution

```
diskprato-print-agent/
  PrintAgent.sln
  contracts/
    v1.openapi.json            # cópia sincronizada do repo DiskPrato (§9)
  src/
    PrintAgent.Contracts/      # DTOs gerados do OpenAPI — nunca editado à mão
    PrintAgent.Core/           # formatação ESC/POS, dedup, política de retry
    PrintAgent.Printing/       # IPrinterTransport + implementações
    PrintAgent.Transport/      # cliente SSE, cliente HTTP da API, pareamento
    PrintAgent.Host/           # Worker Service + servidor do named pipe
    PrintAgent.Tray/           # tray icon + tela de setup (WinForms)
  tests/
    PrintAgent.Core.Tests/     # golden bytes do ESC/POS
    PrintAgent.Transport.Tests/# SSE contra servidor fake
    PrintAgent.Printing.Tests/ # impressora de rede fake, porta FILE:
  installer/
    PrintAgent.Installer.wixproj
  .github/workflows/
    ci.yml                     # build + test em cada push
    release.yml                # tag → .msi assinado no GitHub Release
```

Regra de dependência: `Host` → `Transport`/`Printing`/`Core` → `Contracts`.
`Core` não conhece HTTP nem Win32; é onde ficam os testes puros.

---

## 4. Convivência com outros PDVs (o requisito que define o desenho)

O agente nunca reivindica o dispositivo. Ele usa o árbitro que o SO já
oferece. `IPrinterTransport` tem três implementações, nessa ordem de
preferência:

### 4.1 `SpoolerPrinterTransport` — padrão no Windows

Envia ESC/POS cru por RAW pass-through pela fila de impressão do Windows.
O spooler serializa jobs entre aplicações: outro PDV imprimindo ao mesmo
tempo vira outro job na fila, os dois saem íntegros. Também dispensa
instalar driver — usa a fila que o cliente já configurou.

P/Invoke em `winspool.drv`:

```csharp
[DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool OpenPrinter(string pPrinterName, out IntPtr hPrinter, IntPtr pDefault);

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
struct DOCINFO { public string pDocName, pOutputFile, pDatatype; }

[DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
static extern bool StartDocPrinter(IntPtr hPrinter, int level, ref DOCINFO di);
// ... StartPagePrinter, WritePrinter, EndPagePrinter, EndDocPrinter, ClosePrinter
```

Sequência por job: `OpenPrinter` → `StartDocPrinter` com
`pDatatype = "RAW"` → `StartPagePrinter` → `WritePrinter(bytes)` →
`EndPagePrinter` → `EndDocPrinter` → `ClosePrinter`. Sempre em `try/finally`
com `ClosePrinter` no finally — vazar handle de impressora trava a fila para
todo mundo, inclusive o outro PDV.

`pDatatype = "RAW"` é o ponto crítico: sem isso o driver reinterpreta os
bytes e o ESC/POS vira lixo impresso.

Listar filas disponíveis para a tela de setup: `EnumPrinters` nível 4.
Não requer privilégio administrativo — nem para listar, nem para imprimir.

**Trade-off honesto:** por esse caminho não conseguimos ler status em tempo
real da impressora (`DLE EOT`, §5.3). O que dá para saber vem de
`GetPrinter` nível 2, checando `PRINTER_STATUS_PAPER_OUT`,
`PRINTER_STATUS_OFFLINE`, `PRINTER_STATUS_DOOR_OPEN` — e isso depende do
driver popular esses flags, o que muitos drivers genéricos não fazem. É
best-effort. Detecção confiável de "sem papel" só existe no caminho de rede
(§4.2). Esse é o preço da convivência, e é um preço que vale.

### 4.2 `NetworkPrinterTransport` — IP:9100

Impressoras RAW/JetDirect aceitam uma conexão TCP por vez. Não há árbitro,
então a convivência vem do comportamento:

- Conectar → enviar → **fechar**, por job. Nunca manter socket aberto.
  (O socket persistente é com o backend, nunca com a impressora.)
- `ConnectTimeout` 3s, `SendTimeout` 5s.
- `SocketError.ConnectionRefused`, `TimedOut` ou `HostUnreachable` →
  `printer_busy` / `printer_offline`, que são **retry**, não falha terminal.
  Provavelmente é o outro PDV usando a impressora naquele instante.
- Backoff entre tentativas: 1s, 3s, 10s, 30s, teto 60s.

Quando a impressora de rede também estiver instalada como fila do Windows
(porta TCP/IP padrão), preferir o caminho 4.1 — volta a ter árbitro.

### 4.3 `RawDevicePrinterTransport` — USB direta

Escape hatch, escondido atrás de um aviso explícito na tela de setup. Abre
handle direto no dispositivo e é exclusivo enquanto o handle estiver aberto.

**Proibido:** WinUSB/libusb via Zadig. Trocar o driver do dispositivo faz o
driver do fabricante sumir e o PDV do cliente para de enxergar a impressora.
É exatamente o cenário que o requisito proíbe. Se um dia parecer necessário,
a resposta é orientar o lojista a compartilhar a impressora como fila do
Windows, não trocar driver.

O transporte em uso vai em `POST /status` (`transport`:
`spooler` | `network` | `raw_device`) e o dashboard destaca `raw_device`
como configuração de risco.

---

## 5. Formatação ESC/POS

### 5.1 Comandos usados

| Comando | Bytes | Uso |
|---|---|---|
| Inicializar | `1B 40` | primeiro byte de todo job; limpa estado deixado pelo job do outro PDV |
| Code page | `1B 74 n` | `n=2` CP850, `n=3` CP860 — ver 5.2 |
| Alinhamento | `1B 61 n` | 0 esq, 1 centro, 2 dir |
| Ênfase | `1B 45 n` | 1 liga, 0 desliga |
| Tamanho | `1D 21 n` | nibble alto = largura, baixo = altura (`0x11` = dobro dos dois) |
| Avanço | `1B 64 n` | n linhas |
| Corte parcial | `1D 56 42 03` | corta após avançar 3 linhas |

Largura útil por papel, fonte A (12×24): **80mm = 48 colunas**,
**58mm = 32 colunas**. Fonte B (9×17): 64 / 42. A largura é configuração do
dispositivo e todo o layout deriva dela — nada de constante 48 espalhada
pelo código.

O `1B 40` inicial não é decorativo: se o PDV do cliente imprimiu antes com
fonte dobrada e não resetou, o nosso cupom sai errado. Sempre inicializar,
nunca assumir estado.

### 5.2 Acentuação — o bug mais provável do projeto

Duas armadilhas encadeadas:

1. **.NET Core não traz CP850/CP860.** É preciso
   `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` no
   startup, senão `Encoding.GetEncoding(850)` lança em runtime — e só na
   máquina do cliente, porque em dev ninguém testa acento.
2. **O número da code page em `ESC t n` varia por fabricante.** A tabela
   Epson (`n=2` → CP850, `n=3` → CP860) é seguida pela maioria, mas não por
   todos. `codePage` e `escTIndex` são **configuráveis** por dispositivo,
   com CP850/`n=2` como padrão.

Fallback obrigatório: opção "remover acentos" que normaliza para
`FormD` e descarta as marcas diacríticas. Cupom sem acento é feio; cupom com
`Ã§` no lugar de `ç` é um chamado de suporte.

Isso entra nos golden tests desde o primeiro dia, com nomes de produto
contendo `ç`, `ã`, `õ`, `é`.

### 5.3 Status da impressora

`DLE EOT n` (`10 04 n`) devolve status em tempo real: `n=1` estado geral,
`n=2` causa de offline, `n=4` sensor de papel. Requer canal bidirecional —
funciona no `NetworkPrinterTransport` (ler resposta do socket antes de
fechar), **não** no `SpoolerPrinterTransport` (§4.1).

Onde não houver `DLE EOT`, o estado reportado em `POST /status` é
`unknown`, e não uma adivinhação. Reportar `ready` sem saber é pior que
reportar `unknown`.

### 5.4 Layout do cupom

Derivado inteiramente do `PrintJob` do contrato:

```
        [restaurant.name]              centro, dobro de altura
        [restaurant.phone]             centro
────────────────────────────
PEDIDO #[order.number]                 esquerda, ênfase
[order.createdAt no order.timezone]
[DELIVERY | RETIRADA]                  destaque
────────────────────────────
[customer.name]
[customer.phone]
[delivery.address]                     se delivery
[delivery.distanceKm] km               se presente
────────────────────────────
2x X-Salada                    50,00
   + Bacon                      3,00   modifiers[].priceCents != null
   + Cheddar                           modifiers[].priceCents == null
   • Coca 350ml (1)                    comboItems
   obs: sem cebola
────────────────────────────
Subtotal                       50,00
Taxa de entrega                 7,00
TOTAL                          57,00   dobro de altura
────────────────────────────
[payment.label]
Troco para 100,00 → 43,00              se changeForCents != null
────────────────────────────
[order.notes]
```

Regras que o layout **tem** que respeitar, e que vêm do contrato:

- `unitPriceCents` já inclui os modificadores. Os preços dos modificadores
  **não somam** até ele quando o grupo usa `pricingMode` `max`/`average` —
  nesses casos o backend manda `priceCents: null` e o modificador é impresso
  **sem preço**. Nunca somar esses valores para conferir nada.
- `subtotalCents`, `totalCents` e `changeDueCents` vêm prontos. O agente não
  os recalcula, nem para validar.
- A hora é formatada em `order.timezone` (IANA), não no fuso da máquina. PC
  de balcão com relógio errado não pode virar hora errada no cupom.
  `TimeZoneInfo.FindSystemTimeZoneById` aceita ID IANA no .NET moderno,
  inclusive no Windows.
- `payment.label` vem pronto em pt-BR. Não montar esse texto a partir de
  `payment.method`.
- Valores em centavos → `(cents / 100m).ToString("N2", ptBR)`. Sempre
  `decimal`, nunca `double`.

---

## 6. Como o agente conversa com a API

Base: `https://api.diskprato.com`. Todas as rotas de dispositivo levam
`Authorization: Bearer <deviceToken>` e o header
`X-Print-Agent-Version: <semver>`.

### 6.1 Pareamento (uma vez por instalação)

```
lojista gera código no dashboard  ──►  digita na tela de setup do agente
                                          │
POST /api/print-agents/v1/pair            │
{ code, deviceName, agentVersion, platform }
                                          ▼
{ deviceId, deviceToken, restaurant }  ◄──┘
```

O `deviceToken` (`dpa_v1_<43 chars base64url>`) trafega em claro **uma única
vez**. Persistir imediatamente (§7.2) e nunca logar, nem em `Debug`.

Normalizar o código antes de enviar: maiúsculo, sem espaços e sem hífens. O
alfabeto é Crockford base32 sem `I/L/O/U` — se o lojista digitar `O` ou `I`,
mapear para `0` e `1` antes de mandar, em vez de deixar a API recusar.

Código inválido, expirado e já usado retornam todos o mesmo
`PRINT_AGENT_PAIRING_CODE_INVALID`. A UI mostra uma mensagem só, sem tentar
inferir qual dos três foi.

### 6.2 Ciclo de vida da conexão

```
  ┌─ boot / retomada ──────────────────────────────────────────┐
  │                                                             │
  │  1. carrega token                                           │
  │       sem token → tela de pareamento, fim                   │
  │  2. abre GET /v1/stream (Last-Event-ID, se houver)          │
  │  3. GET /v1/jobs/pending  ← SEMPRE, a cada (re)conexão      │
  │  4. imprime o que veio, deduplicando por jobId              │
  │  5. loop: recebe print:job → imprime → POST ack             │
  │  6. conexão cai → backoff → volta ao passo 2                │
  └─────────────────────────────────────────────────────────────┘
```

O passo 3 é a garantia real de não perder pedido. O replay do SSE via
`Last-Event-ID` tem TTL de 5 minutos no servidor: cobre blip de rede e
deploy, **não** cobre reboot da máquina da loja. `jobs/pending` cobre.

Consequência: o mesmo job chega pelo stream **e** por `jobs/pending`. A
deduplicação por `jobId` (§7.1) não é otimização, é correção.

### 6.3 Cliente SSE

```csharp
using var req = new HttpRequestMessage(HttpMethod.Get, "/api/print-agents/v1/stream");
if (lastEventId is not null) req.Headers.Add("Last-Event-ID", lastEventId);

using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
res.EnsureSuccessStatusCode();

using var reader = new StreamReader(await res.Content.ReadAsStreamAsync(ct));
string? id = null, evt = null; var data = new StringBuilder();

while (!reader.EndOfStream && !ct.IsCancellationRequested)
{
    var line = await reader.ReadLineAsync(ct);
    if (string.IsNullOrEmpty(line)) { Dispatch(id, evt, data.ToString()); /* reset */ continue; }
    if (line.StartsWith("id: "))    id  = line[4..];
    if (line.StartsWith("event: ")) evt = line[7..];
    if (line.StartsWith("data: "))  data.Append(line[6..]);
}
```

Detalhes que fazem diferença:

- **`HttpClient.Timeout = Timeout.InfiniteTimeSpan`** para o cliente do
  stream. O default de 100s mata a conexão persistente. Use um `HttpClient`
  separado para as chamadas normais (`ack`, `status`, `pending`), com
  timeout curto.
- **Watchdog de 90s**: o backend manda `ping` a cada 30s. Sem nada recebido
  por 90s, cancelar e reconectar. Sem isso, uma conexão TCP meio-morta
  (NAT que expirou, cabo removido) fica pendurada para sempre e nenhum
  pedido é impresso, sem erro nenhum aparecer.
- **`lastEventId` só avança depois do job processado com sucesso** (impresso
  ou enfileirado localmente com durabilidade). Avançar ao receber perde o
  pedido se o processo morrer no meio.
- **Backoff de reconexão**: 1s, 2s, 4s, 8s… teto 60s, com jitter de ±20%.
  Jitter importa: sem ele, uma queda da API faz todas as lojas reconectarem
  no mesmo milissegundo.

### 6.4 Eventos

| `event` | `data` | Ação do agente |
|---|---|---|
| `connected` | `{deviceId}` | log; dispara `jobs/pending` |
| `ping` | `{}` | reseta o watchdog |
| `print:job` | `PrintJob` | dedup por `jobId` → enfileira → imprime |
| `print:job:cancelled` | `{jobId, orderId}` | remove da fila local **se ainda não imprimiu**; se já imprimiu, ignora |
| `device:revoked` | `{deviceId}` | apaga o token, para o loop, mostra tela de pareamento. **Não reconectar** |
| `shutdown` | `{}` | reconecta imediatamente, sem backoff (instância drenando) |

### 6.5 Ack

`POST /v1/jobs/{jobId}/ack` só depois que os bytes saíram com sucesso pelo
transporte. É idempotente do lado do servidor: reenviar dá 200, e `failed`
depois de `printed` é ignorado (estado terminal vence). O agente nunca
precisa tratar conflito.

Falhas intermediárias **não** viram ack — viram `POST /status` e retry
local. `status: failed` significa "desisti depois de esgotar o retry local"
(padrão: 5 tentativas ao longo de ~10 min): o arquivo sai de `pending/` e
vai para `failed/` (§7.1), e a chamada leva `errorCode` do vocabulário
fechado do contrato.

Se o backend estiver fora do ar na hora do ack: o arquivo em `printed/`
fica com `acked: false` e o `AckFlusher` reenvia sozinho. O job continua
aparecendo em `jobs/pending` até o ack chegar, e a checagem em `printed/`
(§7.1) impede impressão dobrada.

### 6.6 Tratamento de HTTP

| Situação | Ação |
|---|---|
| `401` em qualquer rota | token inválido ou revogado: apagar token, **parar de tentar**, pedir novo pareamento na UI. Nunca entrar em loop de reconexão com 401 |
| `429` | respeitar `Retry-After`; se ausente, backoff |
| `5xx` / rede | retry com backoff + jitter, indefinidamente |
| `404` no ack | job não existe mais (pedido apagado): descartar da fila local, sem retry |
| `400` com `PRINT_AGENT_VERSION_UNSUPPORTED` | versão do agente velha demais: parar, avisar na UI que precisa atualizar |

Ramificar sempre por `code` do `ApiError`, nunca pelo texto de `message` —
`message` é texto livre e muda de redação sem aviso.

---

## 7. Estado local

### 7.1 Fila em arquivo — `%ProgramData%\DiskPrato\PrintAgent\queue\`

SQLite foi cogitado e descartado. Não é a complexidade de operar um banco
que pesa — é que ele resolve um problema de concorrência que este processo
não tem (só o `Worker` escreve; o Tray só lê, via named pipe) e cobra em
troca um raio de estrago maior: banco corrompido perde a fila inteira,
arquivo corrompido perde um job. Com dezenas de jobs pendentes no pior caso,
não milhares, a estrutura certa é a mais simples que sobrevive a reboot —
um arquivo por job, com escrita atômica.

```
queue/
  pending/    <jobId>.json     -- recebido, ainda não impresso (ou em retry)
  printed/    <jobId>.json     -- impresso; carrega o próprio estado de ack
  failed/     <jobId>.json     -- retry local esgotado (5 tentativas, ~10min)
```

O nome do arquivo é sempre `<jobId>.json` — dedup vira `File.Exists`, O(1),
sem índice.

**`pending/<jobId>.json`** — equivalente à antiga tabela `jobs`:

```json
{
  "jobId": "job_9f2a...",
  "payload": { /* PrintJob completo, do jeito que chegou */ },
  "receivedAt": "2026-08-08T19:42:00-03:00",
  "attempts": 2,
  "nextAttemptAt": "2026-08-08T19:42:15-03:00",
  "lastError": "printer_busy"
}
```

**`printed/<jobId>.json`** — equivalente às antigas `printed` +
`pending_acks` fundidas num arquivo só, porque as duas descreviam o mesmo
job em momentos diferentes do mesmo ciclo de vida:

```json
{
  "jobId": "job_9f2a...",
  "printedAt": "2026-08-08T19:42:16-03:00",
  "acked": false,
  "lastAckAttemptAt": "2026-08-08T19:42:20-03:00",
  "lastAckError": "network"
}
```

`acked: false` é o que faz o `AckFlusher` (§0) considerar o arquivo
pendente de reenvio — sem tabela `pending_acks` separada, é o mesmo arquivo
que muda de estado.

**Escrita sempre atômica.** Toda gravação, seja criar ou atualizar, segue:

```csharp
var tmp = Path.Combine(dir, $"{jobId}.json.tmp-{Guid.NewGuid():N}");
await File.WriteAllTextAsync(tmp, json, ct);
File.Move(tmp, Path.Combine(dir, $"{jobId}.json"), overwrite: true);
```

`File.Move` no mesmo volume NTFS é atômico — nunca existe um estado em que
o arquivo final está pela metade. Nome de `.tmp` único por escrita evita
colisão se duas gravações se sobrepuserem por engano.

**Transição de estado é mover entre pastas**, não editar um campo:
`pending/<jobId>.json` some e `printed/<jobId>.json` aparece na mesma
operação lógica (grava em `printed/`, depois apaga de `pending/` — nessa
ordem, para nunca haver uma janela em que o job não existe em lugar
nenhum). Falha de energia no meio deixa o job em `printed/`, o que é seguro:
na pior hipótese ele é reportado impresso de novo ao backend, que já trata
ack duplicado como idempotente (§6.5).

**Dedup na recepção:** antes de enfileirar um job recém-chegado, checar
`File.Exists` em `printed/` **e** em `failed/` — um job que já foi impresso
ou já falhou definitivamente não deve reentrar em `pending/` só porque
`jobs/pending` o devolveu de novo antes do ack chegar ao servidor. Essa
checagem substitui o que a antiga tabela `printed` garantia.

**Ordem de processamento:** a fila é montada em memória no boot lendo todo
`pending/*.json` e ordenando pelo campo `receivedAt` do conteúdo — nunca
pela ordem de listagem do diretório (não é garantida) nem pelo timestamp do
arquivo no filesystem (antivírus e backup podem alterá-lo). Como o volume é
pequeno, ler todos os arquivos no boot é barato.

**Limpeza:** um timer horário remove de `printed/` e `failed/` tudo com mais
de 7 dias — a mesma janela que a tabela `printed` usava, folgada sobre as
24h que `jobs/pending` cobre. `pending/` nunca é limpo por idade: um job ali
significa trabalho em aberto, e só sai por sucesso (vira `printed/`) ou por
esgotar retry (vira `failed/`).

Ordem obrigatória de gravação ao receber um job: **escrever em `pending/`
antes de responder qualquer coisa**. Se o processo morrer entre receber e
persistir, `jobs/pending` recupera; mas se `lastEventId` já tiver avançado,
não.

### 7.2 Token — `%ProgramData%\DiskPrato\PrintAgent\device.dat`

`ProtectedData.Protect` com **`DataProtectionScope.LocalMachine`**, não
`CurrentUser`: o serviço roda como `LocalSystem` e o tray roda como o
usuário logado — os dois precisam ler o mesmo arquivo. `CurrentUser`
quebraria essa divisão de forma silenciosa e só na máquina do cliente.

`LocalMachine` significa que qualquer usuário local que consiga ler o
arquivo consegue decifrá-lo. Por isso a ACL importa: o instalador restringe
o arquivo a `SYSTEM` + `Administrators`, e o tray pede o status ao serviço
pelo named pipe em vez de ler o token.

### 7.3 Configuração — `%ProgramData%\DiskPrato\PrintAgent\agent.json`

```json
{
  "apiBaseUrl": "https://api.diskprato.com",
  "deviceId": "clx...",
  "printer": {
    "transport": "spooler",
    "spoolerName": "EPSON TM-T20",
    "host": null, "port": 9100,
    "paperWidthMm": 80,
    "codePage": 850,
    "escTIndex": 2,
    "stripAccents": false,
    "copies": 1
  }
}
```

Arquivo, não registry: dá para o suporte pedir print do arquivo por
WhatsApp e diagnosticar em 10 segundos.

### 7.4 IPC serviço ↔ tray

Named pipe `\\.\pipe\diskprato-printagent`, JSON por linha. Comandos:
`get-status`, `test-print`, `set-printer`, `pair`, `unpair`. ACL do pipe
permite `Users` (a tela de setup roda sem elevação).

Serviço do Windows roda na Session 0 e não pode desenhar UI — por isso a
separação. Tentar mostrar janela a partir do serviço não funciona no Windows
moderno.

---

## 8. Fases de construção

Cada fase termina com critério de aceite verificável. Não avançar sem ele.

### Fase 0 — bootstrap

Solution, os 6 projetos, `Directory.Build.props` (nullable enable, warnings
as errors), CI de build+test no push. Sincronizar `contracts/v1.openapi.json`.

**Aceite:** `dotnet build` e `dotnet test` verdes no CI.

### Fase 1 — `PrintAgent.Contracts`

Codegen NSwag a partir do OpenAPI, via target MSBuild. Projeto marcado como
gerado; edição manual é erro de build.

**Aceite:** um `PrintJob` de exemplo do OpenAPI desserializa sem perda e
round-trip de JSON bate.

### Fase 2 — `PrintAgent.Printing` + ESC/POS ⚠️ maior risco técnico

Começa aqui, isolada, antes de qualquer rede. Se algo neste projeto for
inviável, é melhor descobrir na semana 1.

Entrega: `EscPosFormatter` (`PrintJob` → `byte[]`),
`SpoolerPrinterTransport`, `EnumPrinters` para listar filas.

**Aceite:**
- Golden tests em hex, incluindo `ç ã õ é` nos nomes de produto, cupom de
  58mm e de 80mm, item com combo, item com modificador `priceCents: null`,
  pedido de retirada (sem endereço), pagamento em dinheiro com troco.
- Impressora "Generic / Text Only" apontada para porta `FILE:` recebe o
  caminho RAW completo e os bytes batem com o golden.
- Bytes capturados renderizados por parser ESC/POS (`receiptline` ou
  `python-escpos`) produzem cupom legível — conferência visual.
- **Teste de convivência:** processo "PDV fake" mandando RAW para a mesma
  fila em loop, junto com o agente. Nenhum acesso negado, nenhum documento
  com bytes intercalados. *Este é o teste que prova o requisito do cliente.*

### Fase 3 — `PrintAgent.Transport`

Cliente HTTP da API, pareamento, cliente SSE com backoff + watchdog,
`jobs/pending`, `ack`, `status`.

**Aceite:** contra um servidor SSE fake em teste —
- reconecta após queda e reenvia `Last-Event-ID`;
- watchdog dispara quando o `ping` some;
- `401` **não** vira loop de reconexão;
- `device:revoked` apaga o token.

### Fase 4 — `PrintAgent.Host`

Worker Service ligando tudo: fila local em arquivo (`queue/`, §7.1), dedup,
fila de retry, acks pendentes, named pipe.

**Aceite:** rodando contra a API real do DiskPrato em dev, um pedido feito
no cardápio sai impresso (porta `FILE:`) e aparece como `printed` no
dashboard. Matar o processo entre receber e imprimir e reiniciar → o pedido
sai uma vez, não duas.

### Fase 5 — `NetworkPrinterTransport`

Conexão efêmera, timeouts, `printer_busy` como retry, `DLE EOT` para status.

**Aceite:** contra a impressora de rede falsa que recusa a segunda conexão
simultânea, o agente faz retry com backoff e não reporta falha terminal.

### Fase 6 — `PrintAgent.Tray`

Tray icon com estado (conectado / impressora ok / N na fila) e tela de
setup: pareamento, escolha de fila ou IP:porta, papel/code page, teste de
impressão, log recente.

**Aceite:** instalação limpa vai de zero a cupom de teste impresso sem
abrir terminal nem editar arquivo.

### Fase 7 — instalador

WiX: `ServiceInstall` + `ServiceControl` (start automático, recuperação em
caso de crash), tray no startup do usuário, ACL do `%ProgramData%`,
desinstalação limpa. Assinatura de código no `release.yml`.

**Aceite:** `.msi` instala numa VM Windows limpa, serviço sobe no boot,
desinstalar não deixa serviço órfão nem arquivo em `%ProgramData%` (exceto
logs, por opção).

### Fase 8 — endurecimento

Serilog em arquivo com rotação (7 dias) em `%ProgramData%`, botão "exportar
diagnóstico" (config + logs + últimos jobs, **sem o token**), auto-teste na
inicialização.

**Aceite:** hardware real (Elgin i9 ou Epson TM-T20). Corte de papel, fim de
papel e impressora desligada produzem os `errorCode` certos no dashboard.

---

## 9. Sincronização do contrato

O OpenAPI vive no repo do DiskPrato (`contracts/print-agent/v1.openapi.json`).
Aqui fica uma cópia em `contracts/v1.openapi.json`, atualizada manualmente:
quando o contrato mudar do lado do backend, a cópia é transferida por PR
como qualquer outra mudança — sem automação de sync entre os dois repos.

Compatibilidade, do lado do agente:

- **Ignorar campos desconhecidos** (comportamento padrão do
  `System.Text.Json`; não ligar `JsonUnmappedMemberHandling.Disallow`).
- **Aceitar valores novos de enum** sem quebrar — `target` é declarado no
  contrato como extensível. Desserializar enum desconhecido para um valor
  `Unknown`, não lançar.
- A versão major está na URL (`/v1/`). O backend serve N e N-1 por pelo
  menos um ciclo de release do agente, o que dá tempo para o parque
  instalado atualizar.

---

## 10. Fora de escopo na v1

- Múltiplas impressoras por loja (cozinha + balcão). O contrato já tem
  `target` reservado para isso; a implementação fica para depois.
- Auto-update. Primeira versão é instalação manual pelo `.msi`.
- Gaveta de dinheiro (`ESC p`) e impressão de QR code.
- Linux/ARM (Raspberry Pi na cozinha). O deployment self-contained do .NET
  cobre isso quando for a hora; a abstração `IPrinterTransport` já isola o
  que muda (lá o caminho seria CUPS ou socket direto, não o spooler).
- Comprovante de pagamento com cartão — o fluxo é 100% Stripe, sem
  maquininha local.