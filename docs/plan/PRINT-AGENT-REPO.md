# DiskPrato Print Agent — plano de construção do repositório

Documento de planejamento do repositório `diskprato-print-agent`. Cobre a
construção do agente .NET, fase a fase, e como ele conversa com a API do
DiskPrato.

**Fonte da verdade do contrato:** `contracts/print-agent/v1.openapi.json`,
no repositório do DiskPrato. Este documento **não** redefine o contrato —
descreve como o agente o consome. Quando os dois divergirem, o OpenAPI vence.

---

## 0. Status atual (atualizado em 2026-08-10)

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
| 7 — instalador | WiX | ✅ feito (`feat(installer)`) — pendente validação em VM limpa |
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

**Validação manual de 2026-08-09 (parcial — 3 bugs reais encontrados, ver
`docs/testes-manuais.md` §2/§3/§5 para o passo a passo reproduzido):**

- **Teste 1** (convivência do spooler): validado antes desta sessão, sem
  pendência.
- **Teste 2** (fila local/recuperação): pedido real chegou via SSE e
  imprimiu localmente com sucesso (`printed/<jobId>.json` gravado), mas o
  `ack` para o backend nunca é aceito — falha **determinística**, não
  intermitente. Causa raiz: `AckFlusher`/`JobsApiClient` serializa
  `printedAt` no formato round-trip padrão do `DateTimeOffset` do .NET
  (ex.: `2026-08-09T19:41:47.2275009+00:00`, offset explícito), mas
  `ackJobSchema` no backend (`src/modules/print-agents/print-agents.schema.ts`)
  usa `z.iso.datetime()`, que por padrão exige sufixo `Z` e rejeita offset
  `+00:00` — confirmado reproduzindo a validação Zod isoladamente. Todo ack
  real volta `400 Bad Request` para sempre, então o pedido fica com
  `status: "pending"` no backend indefinidamente mesmo já impresso. Bug
  secundário encontrado no caminho: o loop periódico de ack
  (`Worker.RunLocalRetryLoopAsync`) não envolve `ackFlusher.FlushAsync` em
  `RunSafelyAsync`, então esse erro mata o loop silenciosamente sem log
  nenhum — só o flush disparado no evento `Connected` está protegido e loga
  o erro.
  **Corrigido em 2026-08-09:** novo `UtcZDateTimeOffsetConverter`/
  `UtcZNullableDateTimeOffsetConverter` (`PrintAgent.Contracts`), registrados
  no `JsonSerializerOptions` de `JobsApiClient`, normalizam todo
  `DateTimeOffset` de saída para UTC com sufixo `Z` antes de serializar —
  não mexe no `Contracts.g.cs` gerado (regra do repo é nunca editar esse
  arquivo à mão). Teste de regressão:
  `JobsApiClientTests.AckJobAsync_SerializesPrintedAt_WithUtcZSuffix`. O bug
  secundário também foi corrigido: `RunLocalRetryLoopAsync` agora chama
  `ackFlusher.FlushAsync` dentro de `RunSafelyAsync`, então uma falha de ack
  loga e o loop continua na próxima rodada em vez de morrer em silêncio.
  Ainda falta reabrir o teste manual (pedido real + ack aceito) para
  confirmar contra o backend de verdade.
- **Teste 3** (SSE contra backend real): reproduzido organicamente (reset
  de conexão do backend de dev, não provocado de propósito). Um
  `IOException`/`SocketException` (reset de conexão, 10054) dentro do loop
  de leitura em `SseStreamClient.ConnectAndPumpAsync` (linha do
  `reader.ReadLineAsync`) só tem `catch` para `OperationCanceledException`
  — a exceção sobe até `Worker.ExecuteAsync` e derruba o processo inteiro
  do `PrintAgent.Host` (`BackgroundServiceExceptionBehavior = StopHost`) em
  vez de acionar o reconnect-com-backoff documentado. Reproduzido 2x
  seguidas na mesma sessão.
  **Corrigido em 2026-08-09:** a leitura do stream em
  `ConnectAndPumpAsync` agora está dentro de um `try/catch` que trata
  `IOException` e `HttpRequestException` como qualquer outra queda de
  conexão (`ConnectionOutcome.WaitAndReconnect()`), em vez de deixar subir.
  Como segunda camada de proteção, `Worker.ExecuteAsync` também passou a
  envolver `RunPairedAsync` num `try/catch` que loga qualquer exceção
  inesperada não-`OperationCanceledException` e volta pro topo do loop
  (reabrindo a sessão pareada do zero) em vez de deixar o
  `BackgroundService` derrubar o host inteiro. Teste de regressão:
  `SseStreamClientTests.ConnectionReset_DuringRead_ReconnectsInsteadOfThrowing`.
  Falta reabrir o teste manual (derrubar a rede/matar a conexão com o Host
  pareado rodando) para confirmar contra o backend de verdade.
- **Teste 5** (Tray): tela de setup abre ilegível — bloqueante, impede
  validar o resto do checklist (configurar impressora, teste de impressão,
  persistência, estado da impressora, despareamento). Causa raiz em
  `SetupForm.Section()` (`SetupForm.cs`): cada `GroupBox` é criado com
  `AutoSize = true` **e** `Width = 440` ao mesmo tempo, e o
  `FlowLayoutPanel` interno também é `AutoSize = true` com
  `Dock = DockStyle.Top` — a combinação faz o engine de layout colapsar a
  caixa para uma largura quase zero: o título vira uma coluna de um
  caractere por linha e nenhum controle (textbox/combo/botão) fica visível
  ou clicável.
  **Corrigido em 2026-08-09:** removido `Dock = DockStyle.Top` do
  `FlowLayoutPanel` interno (o que forçava o cálculo de tamanho preferido a
  zero durante o `AutoSize` do `GroupBox` pai) e trocado `Width = 440` fixo
  no `GroupBox` por `MinimumSize = new Size(440, 0)`, que não briga com
  `AutoSize` do jeito que uma largura exata briga. `dotnet build` limpo;
  como é UI WinForms sem teste automatizado (natureza da Fase 6), falta
  reabrir o checklist visual completo do Tray (`docs/testes-manuais.md` §5)
  pra confirmar que o layout renderiza corretamente numa sessão desktop de
  verdade.
  Pedido de melhoria observado junto (não é bug, não endereçado): trocar o
  ícone atual da bandeja por algo mais estilizado (ex. cabeça de robô ou
  motivo de impressão), hoje é só um ícone genérico de cor sólida.

**Validação manual de 2026-08-09 (segunda rodada — sessão desktop real,
backend de dev + Postgres/Redis reais, restaurante "Forno di Napoli"):**

- **Teste 2** (fila local/recuperação): a correção do formato de
  `printedAt` (rodada anterior) não era suficiente — o `ack` de um pedido
  real continuava voltando `400 Bad Request`. Causa raiz real: `AckRequest`/
  `ReportStatusDto` têm vários campos opcionais (`errorCode`,
  `errorMessage`, ...) e o `JsonSerializerOptions` de `JobsApiClient` não
  tinha `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull` — o
  `System.Text.Json` escreve `"errorCode":null` explícito pra todo campo
  nulo do DTO, mas o schema Zod do backend (`ackJobSchema`/
  `reportStatusSchema`) usa `.optional()`, não `.nullable()`: aceita a
  chave ausente, rejeita `null` explícito. Confirmado isolando a validação
  Zod com/sem os campos nulos via `curl` direto no endpoint. Afetava tanto
  o ack de job impresso com sucesso (sempre manda `errorCode`/
  `errorMessage` nulos) quanto `POST /status`.
  **Corrigido em 2026-08-09:** `JobsApiClient.CreateJsonOptions()` agora
  seta `DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull`.
  `dotnet test` (68 testes) segue verde. Confirmado contra o backend real:
  pedido novo imprime e é confirmado (`acked: true`) em ~15-20s, sem editar
  nada manualmente.
  Bug secundário encontrado (não corrigido — fora do escopo desta rodada):
  `AckFlusher.SendAsync` só trata `OperationCanceledException`,
  `PrintAgentUnauthorizedException` e `PrintAgentVersionUnsupportedException`
  — qualquer outra exceção (ex.: `HttpRequestException` de um 400
  inesperado, como um job órfão que não existe mais no backend) escapa do
  `foreach` de `AckFlusher.FlushAsync` e aborta a rodada inteira, deixando
  todo o resto da fila de acks pendente sem tentar. Reproduzido com jobs
  órfãos deixados por uma sessão de teste anterior bloqueando o ack de um
  job novo e válido até os órfãos serem removidos manualmente.
  **Teste de recuperação** (matar o Host com o job em `pending\`, antes de
  imprimir): passou — apontei a impressora pra uma fila inexistente pra
  segurar o job em `pending\` com retry agendado, matei o processo, corrigi
  a impressora e subi o Host de novo; o job saiu de `pending\` e foi
  impresso **uma única vez**, confirmado depois em `printed\` com
  `acked: true`.
  **Teste de retry/falha** (impressora inválida do início ao fim): passou
  — `attempts` cresceu a cada tentativa (schedule de 60/90/120/150/180s), o
  job migrou pra `failed\<jobId>.json` com `attempts: 5` e
  `errorCode: "Not_configured"` preenchidos, dentro da janela de ~10 min
  esperada.
- **Teste 3** (SSE contra backend real): reaberto de propósito desta vez
  (rodada anterior foi reprodução orgânica). Reiniciei o processo do
  backend de dev com o Host pareado e rodando: o Host detectou a queda e
  reconectou sozinho sem log de erro nem derrubar o processo — confirma a
  correção da rodada anterior. Revoguei o dispositivo pelo endpoint admin
  (`DELETE /api/print-agents/:deviceId`): o Host apagou o token
  (`Token invalidado (DeviceRevoked) — apagando e parando de reconectar.`)
  e voltou pro estado "aguardando pareamento", sem loop de erro 401.
  Nenhum bug novo encontrado — sem alterações de código.
- **Teste 5** (Tray): a correção de layout do `GroupBox` (rodada anterior)
  confirmada — a tela de setup abre totalmente legível numa sessão desktop
  real, com screenshot capturado. Checklist completo:
  - Sem pareamento: texto "Não pareado — digite o código do lojista
    abaixo." e ícone cinza, ok.
  - Pareamento pela própria tela (digitando o código + "Parear"): ok, log
    de atividade e ícone corretos.
  - Configurar impressora / Teste de impressão: ok, log de atividade
    mostra "Configuração da impressora salva." e "Cupom de teste enviado."
  - Persistência da tela: ok, fechar/reabrir traz os campos pré-preenchidos
    (`get-config`).
  - Despareamento: ok, diálogo de confirmação, resumo volta a "Não
    pareado", ícone volta a cinza.
  - Encerramento: ok, "Sair" remove o ícone e finaliza só o processo do
    Tray — o Host continua rodando à parte.
  - **Estado real da impressora — 2 gaps novos encontrados:**
    1. Apontar a fila do Windows pra um nome inexistente deixa
       `PrinterStatus = "Unknown"`. O texto do resumo reflete isso
       corretamente ("estado desconhecido", não finge "pronta"), mas
       `TrayIcons.StateFor()` (`PrintAgent.Tray/TrayIcons.cs`) só mapeia
       `"Offline"`/`"PaperOut"`/`"CoverOpen"` para o ícone laranja
       (`PrinterProblem`) — `"Unknown"` cai no `default` e o ícone continua
       **verde**, dando falsa confiança visual numa configuração quebrada
       (cenário realista: erro de digitação no nome da fila). Não
       corrigido nesta rodada.
    2. Desparear/parear pela tela (ou pelo pipe) **enquanto o Worker já
       tem uma sessão SSE pareada ativa** não tem efeito imediato: o loop
       `RunPairedAsync` (`Worker.cs`) só é reiniciado por uma exceção ou
       por `TokenInvalidated` (evento `device:revoked` vindo do próprio
       SSE) — um `unpair`/`pair` local via named pipe não cancela a tarefa
       em execução. Resultado: depois de reparear pela tela, o resumo
       fica preso em "Pareado, sem conexão com o DiskPrato." (vermelho)
       indefinidamente (esperei mais de 1 min), e só um restart do
       `PrintAgent.Host` reconecta de fato com o novo pareamento —
       confirmado revertendo com restart, que resolveu na hora. Diferente
       da revogação remota (testada no Teste 3, que funciona porque o
       backend manda `device:revoked` pelo próprio SSE). Não corrigido
       nesta rodada — precisa de um jeito do `Worker` observar troca de
       pareamento local (ex.: token/deviceId mudou) e cancelar a sessão
       atual, não só reagir a eventos vindos do servidor.
  Pedido de melhoria do ícone (trocar por algo mais estilizado) segue em
  aberto, fora de escopo.

**Próximo passo:** os bugs de formato de `printedAt`/reconexão SSE/layout
do Tray (rodada anterior) e o de serialização de campos nulos no ack
(rodada atual) estão corrigidos e confirmados contra backend/desktop reais.
Os três problemas restantes da rodada atual (`AckFlusher` abortando a fila
inteira num erro inesperado de ack; ícone não ficando laranja em estado
"Unknown"; pareamento local não reconectando sem restart do Host) também
foram corrigidos em 2026-08-09 — `AckFlusher.SendAsync` ganhou um
`catch (Exception)` genérico que segue pro próximo job em vez de abortar a
rodada; `TrayIcons.StateFor()` passou a mapear `"Unknown"` pro ícone
laranja; `Worker.RunPairedAsync` passou a assinar
`AgentController.TokenChanged` (evento que já existia mas não tinha
listener) e cancela a sessão SSE atual quando o token/pareamento muda
localmente, forçando reconexão sem restart. `dotnet build`/`dotnet test`
(60 testes) verdes — **falta revalidação manual** dos três cenários contra
backend/desktop reais antes de considerar fechado (ver
`docs/testes-manuais.md` §2/§5).

**Múltiplas impressoras — Fase 1 de §10 feita em 2026-08-10.** Contrato
sincronizado para v1.1.0 (`contracts/v1.openapi.json`, `feat(contracts)` em
2026-08-09): `target` ganhou o valor `bar`, e `PrintJob` ganhou
`stationLabel`/`printMode` (ambos `x-since: 1.1.0`, opcionais).
`PrintAgent.Contracts` regenerado via NSwag sem editar `Contracts.g.cs` à
mão (`dotnet build` regrava a partir do JSON, como sempre). `EscPosFormatter`
(`PrintAgent.Core`) passou a consumir os dois campos novos:
- `job.StationLabel`, se presente, imprime como linha centralizada com
  ênfase logo abaixo do nome/telefone do restaurante — nenhuma tabela local
  de tradução `target` → texto.
- `job.PrintMode == Production` corta a seção de preços/pagamento/totais
  inteira e imprime `{qty}x {nome}` do item em dobro de altura (sem preço),
  sem somar nada que o backend não mandou pronto. Modificadores nunca
  imprimem preço nesse modo, mesmo quando `priceCents` existe.
- Ausência de `printMode` (agente/pedido antigo, sem roteamento) continua
  bit-a-bit idêntica ao comportamento de hoje — coberto por teste dedicado
  (`Format_without_stationLabel_or_printMode_matches_receipt_behavior`).
Dois golden tests novos em `EscPosFormatterTests` (hex de trechos-chave, não
o cupom inteiro como os dois testes mais antigos — o cupom de production é
uma variação pequena o bastante do golden de receipt que testar por trecho
já prova a diferença sem duplicar um segundo golden gigante). `dotnet
build`/`dotnet test` (62 testes) verdes na solution inteira.
**Não fazem parte desta fase** (ficam para a Fase 2 de §10, ainda não
iniciada): `AgentConfig.Printer` → `Printers` (lista), escolha de
impressora por `target` no `Worker`, e o comando de named pipe
`set-printers`/`get-config` devolvendo lista. Topologia "um agente por
estação" (§10, opção 1) já funciona hoje com só a mudança desta fase —
zero mudança em `AgentConfig`/`Worker`/Tray.

**Múltiplas impressoras — Fase 2 de §10 feita em 2026-08-10.** Topologia 2
(um agente, várias impressoras) agora tem o caminho de dados completo:
- `PrinterConfig` ganhou `Station` (`PrintJobTarget?`, mesmo enum do
  contrato). `AgentConfig.Printer` (singular) virou `AgentConfig.Printers`
  (lista).
- **Migração automática** em `AgentConfigStore.Load()`: um `agent.json`
  com o campo singular antigo e sem `printers` é envolvido numa lista de
  um elemento (`Station = null`, "recebe tudo") e **regravado em disco na
  hora** — a migração acontece uma única vez, não a cada boot. Coberto por
  `AgentConfigStoreTests` (inclusive o caso de arquivo ausente e o de
  `printers` já no formato novo, que não deve ser tocado).
- **Escolha de impressora por job** em `AgentController.ResolvePrinter(target)`:
  entrada dedicada à estação → entrada padrão (`Station == null`) →
  primeira da lista → `PrinterConfig` vazia (nunca lança; um target sem
  impressora configurada vira retry local "não configurado", nunca perde o
  job). `UpdatePrinterConfig` virou upsert por `Station` em vez de
  substituir a única impressora. Coberto por `AgentControllerTests`.
- **`PrintOrchestrator`** trocou os parâmetros `PrinterProfile`/
  `IPrinterTransport` (escolhidos pelo `Worker` antes de saber o `target`
  do job) por um delegate `ResolvePrinter(PrintJobTarget) => (Profile,
  Transport)`, resolvido depois que o job já foi desserializado — a
  criação do transporte concreto entrou pra dentro do mesmo `try` da
  formatação/envio, então um `PrinterTransportFactory.Create` que lança
  (config incompleta) também vira retry em vez de exceção não tratada
  (ganho colateral: antes esse `Create` acontecia fora de qualquer
  `try` no `Worker`, então uma estação mal configurada podia derrubar a
  sessão pareada inteira — não tinha teste cobrindo esse caminho
  especificamente, então não dá pra confirmar que era exercitado em
  produção, mas o código permitia).
- **Named pipe não mudou de formato**, desvio deliberado do texto original
  desta seção (que previa `get-config`/`get-status` devolvendo a lista
  inteira já nesta fase): `set-printer`/`get-config`/`get-status`
  continuam falando de uma `PrinterConfig` só, resolvida por
  `AgentController.ResolveDefaultPrinter()` (entrada `Station == null`, ou
  a primeira, ou uma vazia). Como o Tray de hoje (Fase 6) nunca preenche
  `Station`, `set-printer` vindo da tela sempre faz upsert da entrada
  padrão — o Tray continua funcionando sem nenhuma mudança de código, e o
  checklist manual validado em 2026-08-09 não precisa ser reaberto por
  causa desta fase. Trocar o formato do pipe agora forçaria mudar o Tray
  sem nenhum ganho de UI ainda (isso só vem na Fase 3, que é quando a tela
  aprende a editar mais de uma estação) — melhor adiar a mudança de
  contrato do pipe pra quando ela vier empacotada com a UI que a
  justifica.
`dotnet build`/`dotnet test` (72 testes) verdes na solution inteira.

**Múltiplas impressoras — Fase 3 de §10 feita em 2026-08-10** (adiantada:
o texto original condicionava esta fase à validação do dashboard no outro
repo, mas as mudanças de backend estavam sendo feitas em paralelo pelo
mesmo autor, então fazia sentido preparar o Tray junto em vez de esperar
uma rodada de coordenação entre repos). `SetupForm` trocou a única seção
"Impressora" por N seções dinâmicas, uma por estação configurada:
- Protocolo do pipe finalmente ganhou o formato de lista que a Fase 2
  tinha adiado por falta de UI que o justificasse: `get-config` devolve
  `Printers` (lista) em vez de `Printer` (objeto único). `IpcRequest`/
  `IpcResponseDto` ganharam o campo `Station`, usado por dois comandos
  novos: `remove-printer` (remove a entrada daquela estação) e
  `test-print` (agora aceita uma estação opcional — ausente continua
  testando a impressora "padrão", mesmo comportamento de antes).
- Cada seção tem um combo "Estação" (as 4 do contrato + "Padrão"), os
  mesmos campos de sempre (transporte, fila/IP:porta, papel, code page,
  cópias) e botões Salvar/Imprimir teste/Remover — cada ação fala só com
  a própria estação daquela seção. Botão "+ Adicionar impressora" no
  rodapé da lista cria uma seção em branco.
- Salvar barra duas seções apontando pra mesma estação antes de mandar
  pro serviço (`set-printer` faz upsert por `Station` — deixar isso
  acontecer em silêncio faria uma seção sobrescrever a outra sem avisar o
  lojista, e ele só descobriria quando o cupom saísse na impressora
  errada). Remover a última seção sempre deixa uma em branco no lugar —
  nunca uma tela sem nenhum jeito óbvio de configurar de novo.
- **Limitação conhecida, não corrigida nesta rodada:** o resumo no topo
  ("Estado") continua mostrando só o status da impressora "padrão"
  (`Station == null`), não uma leitura agregada de todas as estações — o
  `AgentStatusSnapshot`/`get-status` não foi estendido para status por
  estação, só `get-config` virou lista. Numa loja com só "Cozinha"
  configurada (sem impressora "padrão"), o resumo mostra "não
  configurada" mesmo com a seção de Cozinha funcionando normalmente. Como
  a Fase 6 (validação manual do Tray) foi feita antes de existir mais de
  uma estação, esse cenário específico não foi validado numa sessão
  desktop real ainda.
- Tray ainda não tem teste automatizado (natureza da Fase 6/UI) — o
  checklist manual (`docs/testes-manuais.md` §5) precisa ser reaberto
  cobrindo múltiplas estações antes de considerar esta fase fechada de
  verdade: adicionar/salvar/testar/remover mais de uma seção, e o cenário
  de limitação acima.
`dotnet build`/`dotnet test` (74 testes) verdes na solution inteira.

**As três fases de §10 estão implementadas no código do agente.** O que
falta é validação manual (checklist do Tray com múltiplas estações) e a
integração ponta a ponta contra o backend real, uma vez que o roteamento
esteja de pé do lado do dashboard/API (mudanças em andamento em paralelo,
no outro repo).

**Validação manual de 2026-08-10 (terceira rodada — sessão desktop real,
backend de dev + Postgres/Redis reais, restaurante "Forno di Napoli"):**

- **Teste 1 — fluxo ponta a ponta pós-refactor de roteamento (topologia de
  estação única, §10 Fase 2):** repareado o Host (token antigo tinha sido
  revogado desde a sessão anterior), pedido real feito pelo cardápio
  público e aceito pelo dashboard — job apareceu em `queue/pending`,
  imprimiu e foi confirmado (`acked: true`) sem intervenção manual, dentro
  do ciclo normal do `AckFlusher`. Confirma que o delegate
  `ResolvePrinter(target)` introduzido na Fase 2 de §10 não quebrou o
  caminho mais comum (sem nenhuma estação dedicada configurada).
- **Teste 1 — `AckFlusher` com job órfão na fila:** revalidação pendente
  desde a correção de 2026-08-09. Plantei manualmente um
  `printed/<jobId>.json` com `acked: false` e um `jobId` inexistente no
  backend, depois fiz um segundo pedido real. Resultado parcial:
  - **Confirmado:** o job novo e válido foi confirmado (`acked: true`)
    normalmente mesmo com o órfão presente — a correção de 2026-08-09
    (`catch (Exception)` genérico em `AckFlusher.SendAsync` que loga e
    segue pro próximo job) continua funcionando, a rodada de flush não foi
    abortada.
  - **Bug novo encontrado (não corrigido):** `AckOutcome.JobNotFound`
    (backend responde `404` no ack) é **ignorado silenciosamente** em
    `AckFlusher.SendAsync` — o `switch`/`if` só trata
    `AckOutcome.Acknowledged`; não há nenhum branch para `JobNotFound` em
    lugar nenhum do código (`grep` confirma que o valor do enum nunca é
    lido fora de onde é produzido em `JobsApiClient.AckJobAsync`). Isso
    contradiz o comentário XML do próprio `AckOutcome`
    (`JobsApiClient.cs:10-12`): "`NotFound` cobre job não existe mais...
    404 no ack → descartar da fila local, sem retry". Na prática, o job
    órfão nunca é descartado nem marcado como falho — `lastAckAttemptAt`/
    `lastAckError` continuaram `null` depois de mais de 40s (≥ 2 ciclos do
    `AckFlusher`, que roda a cada 15s), então ele vai ser re-tentado pra
    sempre, silenciosamente, sem nunca aparecer como erro em lugar nenhum.
    Corrigir exige um método tipo `JobStore.Discard(jobId)` chamado
    quando `AckJobAsync` retorna `JobNotFound`, dentro de
    `AckFlusher.SendAsync`.
- **Teste 3 (Tray) — checklist base com uma única estação:** revalidado, ok
  — tela de setup abre legível (layout de 2026-08-09 continua correto),
  resumo mostra "Pareado, conectado ao DiskPrato. Impressora padrão
  (Spooler — Generic / Text Only): pronta. 0 pedido(s) na fila local.", e
  o ícone da bandeja aparece verde, consistente com o estado saudável.
- **Teste 3 (Tray) — checklist de múltiplas estações e revalidação dos
  dois bugs de 2026-08-09 (ícone `Unknown`→laranja, reparear sem
  restart): não completado nesta rodada.** A automação de UI via
  `SendInput`/`mouse_event` (não havia outra forma de dirigir uma janela
  WinForms nesta sessão) se mostrou pouco confiável porque outra janela
  do desktop (uma sessão Claude Code/VS Code diferente, ativa em paralelo)
  ficava roubando o foreground repetidamente, fazendo cliques/scroll
  caírem no lugar errado — cheguei a mudar sem querer os combos
  "Estação"/"Transporte"/"Papel" da seção Padrão sem clicar em "Salvar"
  (config real em `agent.json` nunca foi tocada, confirmado por leitura
  direta do arquivo antes e depois). Reiniciei o processo do Tray pra
  descartar o estado sujo em memória e não fui adiante para não arriscar
  salvar um valor incorreto sem querer. **Pendente**: adicionar uma
  segunda estação ("Cozinha"), testar duplicidade bloqueada no cliente,
  imprimir teste por estação (fila `Generic / Text Only (Cozinha)` já
  provisionada nesta máquina), persistência com múltiplas seções, remoção,
  e os dois cenários de bug de 2026-08-09 — precisa de uma sessão desktop
  sem outra janela concorrendo pelo foreground, ou de um driver de UI
  Automation em vez de coordenadas de mouse cegas.
- **Pedido de melhoria novo (Tray, fora de escopo desta correção):** a
  janela de configuração só tem botão de fechar — não tem minimizar, e não
  é redimensionável arrastando a borda (uma tentativa de `SetWindowPos`
  pedindo uma janela mais alta foi ignorada/revertida). Hoje o único jeito
  de alcançar as seções mais abaixo (estações extras, log de atividade) é
  rolar dentro do painel interno. Vale trocar `FormBorderStyle` para algo
  redimensionável e adicionar `MinimizeBox = true`.

**Correções de 2026-08-10 (feitas em resposta à terceira rodada acima):**

- **`AckOutcome.JobNotFound` silencioso — corrigido.** O `if` de
  `AckFlusher.SendAsync` virou `switch` com um branch para `JobNotFound`,
  que loga um warning e chama o novo `JobStore.Discard(jobId)` (apaga o
  `.json` de `printed/` e de `failed/`). É exatamente o que §6.6 e o
  comentário XML de `AckOutcome` já mandavam fazer: 404 no ack significa
  que o backend não conhece mais o job, não há nada a confirmar e re-tentar
  só gera tráfego eterno em silêncio. Optou-se por apagar em vez de mover
  para `failed/` porque `failed/` é a fila de acks pendentes de job que
  falhou na impressão — um órfão colocado ali voltaria a ser re-tentado
  pelo próprio flusher, recriando o loop. Coberto por
  `tests/PrintAgent.Host.Tests/AckFlusherTests.cs` (primeiro teste
  automatizado do `AckFlusher`): job órfão em `printed/` e em `failed/`
  some da fila na primeira rodada, e a segunda rodada não chega a fazer
  requisição nenhuma.
- **Janela do Tray fixa/sem minimizar — corrigido.** `SetupForm` passou de
  `FormBorderStyle.FixedDialog` para `Sizable`, com `MinimizeBox` e
  `MaximizeBox` habilitados e `MinimumSize = 500x400`. A altura inicial
  agora é `min(860, altura útil do monitor − 80)` em vez dos 860 fixos —
  com 860 cravado numa tela menor a janela nascia com a borda inferior
  fora do monitor, ou seja, impossível de redimensionar pela borda mesmo
  depois de virar `Sizable`.

**Validação manual de 2026-08-10 (quarta rodada — sessão desktop real,
backend de dev + Postgres/Redis reais):** a rodada que fechou o checklist
de múltiplas estações que a terceira não conseguiu completar.

**Método (o que destravou):** em vez de `SendInput`/`mouse_event` com
coordenadas — que na terceira rodada caía no lugar errado toda vez que
outra janela roubava o foreground — a tela foi dirigida por **UI
Automation** (`InvokePattern`/`ValuePattern`/`SelectionItemPattern`), que
não depende de foreground nem de posição na tela e alcança inclusive
controles fora da área visível do painel rolável. Duas descobertas de
método que valem para a próxima rodada:
- O Windows 11 **não expõe os ícones da bandeja via UIA** (o `Shell_TrayWnd`
  não devolve descendente nenhum), então não dá para abrir a tela clicando
  no ícone por UIA. A tela foi aberta mandando `WM_TRAYMOUSEMESSAGE`
  (`WM_USER+1024`) com `lParam = WM_LBUTTONDBLCLK` para a janela oculta do
  `NotifyIcon` — equivale ao duplo-clique do usuário, sem depender do shell.
- Janela **minimizada some da árvore UIA** (`BoundingRectangle` vira
  `-32000,-32000` e os controles não aparecem). Se um passo falhar com
  "botão não encontrado", checar `WindowVisualState` antes de suspeitar do
  código.

**Resultados — roteiro de múltiplas estações (§10 Fase 3), todos passaram:**
- Seção em branco criada pelo "+ Adicionar impressora" nasce com Estação
  "Padrão" e campos vazios; salvar uma segunda estação ("Cozinha") não
  tocou na seção "Padrão" já gravada.
- **Duplicidade barrada no cliente:** com "Cozinha" já salva, uma segunda
  seção apontando para "Cozinha" logou o aviso e **não chamou o pipe** —
  confirmado por hash do `agent.json` idêntico byte a byte antes e depois.
- **Impressão de teste por estação:** provada de forma direta. A estação
  "Cozinha" foi configurada como `network` apontando para um listener TCP
  local em `127.0.0.1:9100`; o teste da "Cozinha" chegou lá (604 bytes,
  ESC/POS bem formado — `1B 40`, `1B 74 02` = CP850, acentos `ç ã õ é`
  íntegros) e o teste da "Padrão", disparado logo depois, **não** chegou
  (foi para o spooler). Desvio deliberado do roteiro, que pedia duas filas
  `FILE:`: ver "observabilidade do spooler" abaixo.
- Persistência com duas seções, remoção de uma estação, e remoção da
  última seção (deixa uma seção em branco no lugar) — todos conforme.
- **Limitação conhecida do resumo: o comportamento real é diferente do
  previsto no roteiro.** Com só "Cozinha" configurada (sem "Padrão"), o
  resumo não mostra "não configurada": mostra os dados da impressora da
  Cozinha rotulados como "Impressora padrão" (`Impressora padrão (Network
  — 127.0.0.1): estado desconhecido`), porque `ResolveDefaultPrinter()`
  cai no "primeira da lista" quando não existe entrada `Station == null`.
  É mais enganoso do que o roteiro supunha, mas não é crash nem texto
  incoerente. Corrigir de verdade exige status por estação no `get-status`
  (fora do escopo da Fase 3).

**Resultados — bugs de 2026-08-09 que faltava revalidar:**
- **Ícone laranja em estado "Unknown": confirmado visualmente.** Com a
  impressora padrão apontada para `Fila-Inexistente-XYZ`, o resumo mostrou
  "estado desconhecido" **em laranja** (captura via `PrintWindow`), e verde
  no estado saudável. `TrayIcons.For()` (ícone) e `ColorFor()` (label do
  resumo) chamam o mesmo `StateFor()`, então a cor do texto é prova direta
  da cor do ícone.
- **Reparear sem restart: confirmado.** Despareado e pareado de novo pela
  própria tela (código gerado no dashboard, mesmo restaurante — "Forno di
  Napoli" — para o balcão não trocar de vínculo), com o `PrintAgent.Host`
  no mesmo processo o tempo todo (pid inalterado). O resumo voltou sozinho
  a "Pareado, conectado ao DiskPrato", e o log do Host registra a
  sequência inteira: `Stream conectado (deviceId=<antigo>)` → `Sem token
  de dispositivo — aguardando pareamento.` → `Stream conectado
  (deviceId=<novo>)`. `Worker.RunPairedAsync` de fato reage a
  `AgentController.TokenChanged`. Efeito colateral esperado do fluxo: cada
  pareamento cria uma linha nova em `print_agent_devices` (o device antigo
  não é reaproveitado nem apagado).

**Bug novo encontrado nesta rodada (corrigido):** `Remover` numa seção
**recém-criada e nunca salva** apagava a impressora já gravada. A seção
nova nasce com Estação "Padrão"; `OnRemovePrinterAsync` mandava
`remove-printer` pela estação **do combo**, então desistir de uma seção
recém-adicionada (o gesto natural de desfazer) levava junto a impressora
"Padrão" que estava funcionando — silenciosamente, com o log dizendo
"removida" como se tivesse removido a seção nova. Reproduzido na tela real
(`agent.json` foi de 1 impressora para `[]`). Correção: `PrinterSectionView`
ganhou `IsPersisted`/`PersistedStation`; seção não persistida é só
descartada da tela (sem tocar no serviço), e a persistida é removida pela
**estação gravada**, não pela do combo — o que também conserta a variante
"trocar o combo sem salvar e remover", que antes removia a estação errada
e deixava a gravada órfã na config. Ambas as variantes revalidadas na tela.

**Achado de encoding (corrigido só no ponto óbvio):** o cupom de teste
saía com `?` no meio de "Cupom de teste — configuração do PrintAgent" — o
travessão `—` (U+2014) não existe na CP850/CP860 e o encoder cai no
fallback `?`. Trocado por hífen em `NamedPipeIpcServer.cs`. **O problema
maior continua aberto:** `EscPosFormatter` não normaliza pontuação Unicode
antes de codificar, então qualquer `—`, `–`, `"` ou `…` vindo de uma
observação de pedido real (digitada no celular) imprime como `?`. Vale um
passe de transliteração de pontuação antes do `Encoding.GetBytes`.

**Observabilidade do spooler nesta máquina (para a próxima rodada não
perder tempo):** não dá para conferir o que saiu numa fila `FILE:` por
aqui. `Get-PrintJob` não mostra nada nem com a fila pausada — nem para um
`Out-Printer` de controle, o que descarta o agente como causa — e o log
`Microsoft-Windows-PrintService/Operational`, que registraria job e
impressora de destino, **exige admin para ser habilitado** (`wevtutil sl`
devolveu "Acesso negado"). Daí a troca por listener TCP para provar
roteamento.

**Fase 3 de §10 e Fase 6 fechadas.** Com esta rodada o checklist manual do
Tray está inteiro validado — não sobra nenhum item de UI pendente. O que
ainda não tem validação é só o que depende de hardware térmico real
(`docs/testes-manuais.md` §2 e §4), que continua sem equipamento.

**Fase 7 (instalador WiX) feita em 2026-08-10.** `installer/Package.wxs` +
`installer/PrintAgent.Installer.wixproj` (WiX v5, `.msi` perMachine x64) e
`.github/workflows/release.yml` (tag `v*` → `.msi` assinado no GitHub
Release). O que o pacote entrega:

- **Serviço** `DiskPratoPrintAgent`, `LocalSystem`, start automático, com
  `util:ServiceConfig` reiniciando o processo em qualquer uma das três
  primeiras falhas (60 s de espera, contador zerado a cada dia) —
  o serviço vive de uma conexão SSE longa e ninguém no balcão vai
  reiniciá-lo à mão.
- **Tray** no `Run` do **HKLM**, não HKCU: a instalação é perMachine e o
  operador que loga no balcão não é necessariamente quem instalou. Mesmo
  motivo para o atalho ir no menu Iniciar de Todos os Usuários — o que
  dispara ICE43/ICE57 (regras escritas para pacote perUser) e obriga o
  `SuppressIces` no `.wixproj`; o `ALLUSERS=1` gravado no `.msi` confirma
  que é falso positivo.
- **ACL do `%ProgramData%\DiskPrato\PrintAgent`** restrita a `SYSTEM` +
  `Administrators` via `util:PermissionEx`, que substitui a DACL, corta a
  herança e resolve os nomes por SID — o `<Permission>` nativo do MSI faria
  `LookupAccountName` e quebraria em Windows pt-BR ("Administradores").
  Sem isso o `device.dat` protegido com `DataProtectionScope.LocalMachine`
  seria decifrável por qualquer usuário local (§7.2).
- **Desinstalação limpa:** `ServiceControl` remove o serviço, e
  `util:RemoveFolderEx` apaga a fila/`agent.json`/`device.dat`, que nascem
  em runtime e o MSI não conhece. A propriedade com o caminho é montada a
  partir de `[CommonAppDataFolder]`, não do ID `DATAFOLDER`: a ação roda
  antes do `CostFinalize`, quando diretórios customizados ainda não foram
  resolvidos.
- `util:CloseApplication` fecha o tray antes de mexer nos arquivos; sem
  isso, upgrade e desinstalação caem em "arquivo em uso"/pedido de reboot.
- Publish self-contained single-file dos dois `.exe` (plano §2). O
  `installer/` fica **fora** da `PrintAgent.slnx` de propósito — são ~190 MB
  publicados a cada build, que tornariam `dotnet build`/`dotnet test` na
  raiz inviáveis no loop de desenvolvimento. Um job separado no `ci.yml`
  monta o `.msi` para que quebra de `.wxs` apareça mesmo assim.
- `resources/icon-256.ico` (arte de origem, só o frame 256) gerou
  `resources/icon-16-256.ico` via `resources/build-icon.ps1` — a ARP e o
  atalho do menu Iniciar precisam dos tamanhos pequenos. É esse que vira
  `ARPPRODUCTICON` e `ApplicationIcon` dos dois `.exe`.
- A versão passou a viver em `Directory.Build.props` (`<Version>`), lida
  tanto pelos `.exe` quanto pelo `ProductVersion` do `.msi`.
- Assinatura de código no `release.yml` é condicional aos secrets
  `SIGNING_CERT_PFX_BASE64`/`SIGNING_CERT_PASSWORD`: sem eles o workflow
  gera o `.msi` sem assinar, em vez de falhar. Assina os dois `.exe`
  **antes** de empacotar (daí o `SkipAgentPublish` no `.wixproj`, que
  impede o republish de sobrescrever as assinaturas) e o `.msi` depois.

Ainda não validado: instalação/desinstalação numa VM Windows limpa
(critério de aceite do §8 Fase 7) — roteirizado em
`docs/testes-manuais.md` §5.

**Também em 2026-08-10:** o default de `AgentConfig.ApiBaseUrl` passou de
`https://api.diskprato.com` para `https://api.psiconaut4.com.br` (túnel
Cloudflare) enquanto o backend está em fase de teste. O default só vale
para `agent.json` recém-criado; assim que o arquivo existe, o valor gravado
nele manda.

**Próximo passo:** Fase 8 (Serilog com rotação, exportar diagnóstico,
auto-teste na inicialização).

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
  resources/
    icon-256.ico               # arte de origem (só o frame 256x256)
    icon-16-256.ico            # gerado por build-icon.ps1 — ARP, atalho, .exe
    build-icon.ps1
  installer/                   # fora da .slnx: publica ~190 MB por build
    PrintAgent.Installer.wixproj
    Package.wxs
    License.rtf
  .github/workflows/
    ci.yml                     # build + test em cada push (+ job do .msi)
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

Base: `http://localhost:5000`. Todas as rotas de dispositivo levam
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

## 10. Múltiplas impressoras (roteamento de comandas) — planejado

**Planejado, não implementado.** Desenho completo do lado
backend/dashboard (regra de roteamento por categoria/produto, campo
`station` no dispositivo, contrato) está em
`docs/planejamento-features/PRINT-AGENT.md` no repo do DiskPrato — esta
seção cobre só a parte que muda **aqui**, no agente.

### Duas topologias, dois níveis de esforço

1. **Um agente por estação** (PC da cozinha pareado como dispositivo
   "Cozinha", PC do balcão pareado como "Balcão", cada um com sua própria
   impressora) — **zero mudança de código no agente**. O backend já manda
   um `PrintJob` por dispositivo hoje (`createPrintJobsForOrder` itera os
   devices); o roteamento vira, do ponto de vista do agente, só "o job que
   chegou tem menos itens e um `target` diferente" — o `EscPosFormatter`
   já recebe `PrintOrder.items` pronto, não sabe nem precisa saber que foi
   filtrado no backend. O único trabalho é passar a **consumir** os campos
   novos do contrato (abaixo) em vez de ignorá-los.
2. **Um agente, várias impressoras** (uma máquina só no balcão com
   impressora de comanda de cozinha e impressora de recibo lado a lado) —
   exige as mudanças descritas no resto desta seção.

### Contrato: parar de ignorar `target`, consumir campos novos

Hoje `PrintJobTarget` já é desserializado (`PrintJob.Target`) mas nunca
lido em lugar nenhum do `Host` — só o job sintético de teste hardcoda
`Target = PrintJobTarget.Kitchen` (`NamedPipeIpcServer.cs`). Passa a
importar pra valer:

- `stationLabel` (novo campo string opcional, nome amigável tipo
  "Cozinha"/"Bar" pronto em pt-BR) — usado no cabeçalho do cupom em vez de
  traduzir o enum `target` localmente. Evita o agente carregar uma tabela
  de tradução que fica desatualizada sem reinstalar quando o backend
  adicionar uma estação nova.
- `printMode: "production" | "receipt"` (novo campo string opcional,
  default `"receipt"` se ausente — compatível com o formato de cupom atual
  quando o campo não vier, o que cobre qualquer agente antigo ou pedido
  que não passou por roteamento). `EscPosFormatter` ganha um branch: em
  `production`, omite a seção de preços/pagamento/totais e aumenta a fonte
  do nome do item — é uma comanda de produção, não um recibo fiscal.
- **Enum desconhecido em `target` continua não-fatal** (já é a regra hoje,
  §9) — uma estação nova que o backend inventou e este agente ainda não
  conhece cai no branch padrão, imprime como cupom genérico em vez de
  falhar.

### `AgentConfig.Printer` (singular) → `AgentConfig.Printers` (por estação)

Mudança de maior risco desta seção, só necessária pra topologia 2:

- `PrinterConfig` ganha um campo `Station` (mesmo enum do contrato).
  `AgentConfig.Printer` (uma instância) vira `AgentConfig.Printers`
  (lista). **Migração automática e silenciosa** em
  `AgentConfigStore.Load()`: se o `agent.json` existente tem o campo
  singular antigo (formato de hoje) e não tem o plural novo, envolve numa
  lista de um elemento com `Station = null` ("estação padrão, recebe
  tudo") — nenhuma instalação em produção quebra ao atualizar, e nenhuma
  delas *precisa* reconfigurar nada pra continuar funcionando exatamente
  como hoje.
- Escolha da impressora por job, em `Worker.HandleSseJobAsync`: procura em
  `Printers` uma entrada com `Station == job.Target`; se não achar, cai na
  entrada com `Station == null` (a "padrão"); se não existir nem essa,
  usa a primeira da lista. Nunca descarta um job por falta de impressora
  configurada pra aquela estação especificamente — mesma filosofia de
  "nunca finge que está tudo bem, mas também nunca perde o pedido" que já
  rege o resto do agente (§5.3 do plano original).
- Named pipe (`NamedPipeIpcServer`): `set-printer` (singular, um
  `PrinterConfig`) vira `set-printers` (lista) — ou mantém `set-printer`
  mas com um campo `Station` no payload que faz upsert só daquela entrada
  da lista, sem precisar remandar as outras (API mais ergonômica pro Tray,
  que edita uma estação de cada vez). `get-config`/`get-status` passam a
  devolver a lista inteira, não um objeto só.
- `PrintAgent.Tray` / `SetupForm`: a seção "Impressora" (hoje uma só) vira
  uma lista de seções, uma por estação configurada, com um jeito de
  adicionar/remover estação. É o maior redesenho de UI desta feature —
  cada seção repete os mesmos campos de hoje (transporte, fila/IP:porta,
  papel, code page, cópias) mais o seletor de `Station`. Fila local
  (`JobStore`) não muda: continua um `.json` por job, indiferente a quantas
  impressoras existem — a escolha de impressora acontece só no momento de
  tentar imprimir, não na hora de gravar o job na fila.

### Por que não dividir o job no agente

Alternativa descartada: mandar o pedido inteiro num job só e deixar o
agente decidir localmente quais itens saem em qual impressora (agente
replica a regra de roteamento). Rejeitada porque duplica no cliente uma
regra de negócio que já vive no backend (categoria/produto → estação), e
quebra o modelo de ack por job: se a impressora da cozinha estiver sem
papel mas a do balcão imprimir com sucesso, o contrato de hoje (`ack`
idempotente por `jobId`, retry local por job) não tem como expressar
"metade do job deu certo". Splitting no backend (um `PrintJob`/`jobId` por
combinação dispositivo×estação-com-item) mantém o mesmo modelo de
ack/retry que já existe, só com granularidade menor — o agente continua
sem saber nada sobre categorias, produtos ou regras de roteamento.

### Fases sugeridas (espelha as do outro repo)

1. ✅ **Feito em 2026-08-10.** Consumir `stationLabel`/`printMode` no
   `EscPosFormatter` — funciona pra topologia 1 sem tocar em
   `AgentConfig`/Tray. Cabe inteiro em `PrintAgent.Core`, com golden-bytes
   test cobrindo os dois modos (ver §0).
2. ✅ **Feito em 2026-08-10.** `AgentConfig.Printers` (lista) + migração
   automática do formato antigo + escolha de impressora por `target` no
   `Worker`. Sem UI nova no Tray, como planejado — configurável só via
   named pipe/suporte por enquanto (protocolo do pipe ficou
   deliberadamente igual ao de antes; ver §0).
3. `SetupForm` com seção por estação. Só vale a pena depois que a Fase 2
   do outro repo (UI de roteamento no dashboard) já validou que lojistas
   reais usam a feature — não faz sentido redesenhar o Tray pra um cenário
   que ninguém configurou ainda.

---

## 11. Fora de escopo na v1

- Nome de estação customizado pelo lojista (texto livre além do enum
  fixo `kitchen`/`bar`/`counter`/`customer`) — ver §10.
- Auto-update. Primeira versão é instalação manual pelo `.msi`.
- Gaveta de dinheiro (`ESC p`) e impressão de QR code.
- Linux/ARM (Raspberry Pi na cozinha). O deployment self-contained do .NET
  cobre isso quando for a hora; a abstração `IPrinterTransport` já isola o
  que muda (lá o caminho seria CUPS ou socket direto, não o spooler).