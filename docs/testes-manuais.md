# Testes manuais — DiskPrato Print Agent

Este documento reúne **todos** os testes que não dá para automatizar num
agente/CI: exigem uma sessão de desktop real do Windows, hardware físico
(impressora térmica), ou um backend de verdade rodando. Tudo que pode ser
coberto por `dotnet test` já está nos projetos `tests/*` — este arquivo é
só para o que sobra.

Fonte da lista: seção "Pendências manuais" e critérios de aceite de cada
fase em `docs/plan/PRINT-AGENT-REPO.md §8`. Atualize os dois documentos
juntos quando um teste passar a ser automatizável ou quando uma fase nova
adicionar pendência própria.

---

## Antes de começar

Pré-requisitos comuns a quase todos os testes abaixo:

1. **Build limpo:**
   ```powershell
   dotnet build
   ```
2. **Uma fila de impressão Windows apontada para `FILE:`** — simula uma
   impressora térmica sem precisar de hardware, e é o mesmo caminho
   (`SpoolerPrinterTransport`, RAW pass-through) que uma Epson/Elgin real
   usaria:
   - Painel de Controle → Dispositivos e Impressoras → Adicionar impressora
   - "A impressora que eu quero não está na lista" → "Adicionar impressora
     local usando configurações manuais"
   - Porta: `FILE:` (já existe por padrão)
   - Fabricante `Generic` → driver `Generic / Text Only`
   - Nomeie como quiser (os exemplos abaixo usam `Generic / Text Only`)
3. **Para os testes que envolvem pedido de verdade (fila local, Tray):**
   um código de pareamento válido gerado no dashboard de dev do DiskPrato,
   e o backend de dev acessível pela máquina de teste.

---

## 1. Convivência do spooler com outro PDV (Fase 2)

### O que está sendo testado

`SpoolerPrinterTransport` manda ESC/POS cru (`pDatatype = "RAW"`) direto
pela fila de impressão do Windows, sem tomar posse do dispositivo — o
requisito que define todo o desenho do agente (plano §4): o restaurante já
tem um PDV usando aquela mesma impressora, e o agente não pode atrapalhar.
O spooler do Windows é quem serializa os jobs entre os dois programas; este
teste prova que essa serialização realmente acontece e que nenhum job sai
com bytes de dois pedidos misturados.

### Passos

1. Rode o Host (ou dispare via Tray → "Imprimir teste") apontado para a
   fila `Generic / Text Only`.
2. Simule um "PDV fake" concorrente escrevendo RAW na mesma fila, em loop,
   ao mesmo tempo. Um jeito rápido em PowerShell (mesmo padrão de
   `OpenPrinter`/`WritePrinter` que o `SpoolerPrinterTransport` usa):

   ```powershell
   Add-Type -Namespace Raw -Name Spool -MemberDefinition @'
   [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
   public static extern bool OpenPrinter(string name, out IntPtr h, IntPtr d);
   [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
   public struct DOCINFO { public string pDocName, pOutputFile, pDatatype; }
   [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint="StartDocPrinterW")]
   public static extern int StartDocPrinter(IntPtr h, int level, ref DOCINFO di);
   [DllImport("winspool.drv")] public static extern bool StartPagePrinter(IntPtr h);
   [DllImport("winspool.drv")] public static extern bool WritePrinter(IntPtr h, byte[] buf, int count, out int written);
   [DllImport("winspool.drv")] public static extern bool EndPagePrinter(IntPtr h);
   [DllImport("winspool.drv")] public static extern bool EndDocPrinter(IntPtr h);
   [DllImport("winspool.drv")] public static extern bool ClosePrinter(IntPtr h);
   '@

   for ($i = 0; $i -lt 200; $i++) {
     [IntPtr]$h = [IntPtr]::Zero
     [Raw.Spool]::OpenPrinter("Generic / Text Only", [ref]$h, [IntPtr]::Zero) | Out-Null
     $di = New-Object Raw.Spool+DOCINFO
     $di.pDocName = "PDV-FAKE"; $di.pDatatype = "RAW"
     [Raw.Spool]::StartDocPrinter($h, 1, [ref]$di) | Out-Null
     [Raw.Spool]::StartPagePrinter($h) | Out-Null
     $bytes = [System.Text.Encoding]::ASCII.GetBytes("PDV-FAKE-JOB-$i`n")
     [Raw.Spool]::WritePrinter($h, $bytes, $bytes.Length, [ref]0) | Out-Null
     [Raw.Spool]::EndPagePrinter($h) | Out-Null
     [Raw.Spool]::EndDocPrinter($h) | Out-Null
     [Raw.Spool]::ClosePrinter($h) | Out-Null
     Start-Sleep -Milliseconds 50
   }
   ```
3. Enquanto o loop acima roda, dispare vários "Imprimir teste" pelo Tray
   (ou `test-print` pelo pipe, repetidamente) — ver §5 abaixo para o
   comando exato.

### Critério de sucesso

- Nenhum "acesso negado" / handle inválido em nenhum dos dois lados.
- Cada arquivo de saída (a porta `FILE:` pede o caminho na primeira
  impressão de cada job — ou configure a porta pra sempre escrever no
  mesmo arquivo e inspecione com `Get-Content -Raw`) contém **um** cupom
  ESC/POS completo (começa com `1B 40`) ou **um** bloco `PDV-FAKE-JOB-N`
  inteiro — nunca os dois entrelaçados.
- (Opcional, recomendado) Renderize os bytes capturados do cupom do
  DiskPrato com `receiptline` ou `python-escpos` para confirmar que saiu
  legível, com acentos (`ç ã õ é`) corretos.

---

## 2. Fila local em arquivo e recuperação de pedidos (Fase 4 — `JobStore`)

### O que está sendo testado

A fila local (`%ProgramData%\DiskPrato\PrintAgent\queue\{pending,printed,failed}\`,
um `.json` por job) é o que garante que um pedido não se perde se o
processo morrer no meio do caminho, e que ele não é impresso duas vezes
quando o mesmo job chega tanto pelo SSE quanto por `jobs/pending` (plano
§6.2/§7.1). `JobStore`/`PrintOrchestrator`/`AckFlusher` têm teste
automatizado contra diretório temporário real — o que falta é a integração
de ponta a ponta contra SSE + HTTP + IPC reais, que só existe com o
processo rodando de verdade.

### Passos

1. Suba o Host pareado com o backend de dev (ver §5, comando `pair`, ou
   pareie pelo Tray).
2. Configure a impressora pra fila `Generic / Text Only` (§5, `set-printer`,
   ou pelo Tray).
3. Faça um pedido de teste no cardápio associado ao restaurante pareado.
4. Acompanhe a pasta da fila:
   ```powershell
   explorer "$env:ProgramData\DiskPrato\PrintAgent\queue"
   ```
   - O pedido deve aparecer em `pending\<jobId>.json` assim que chega.
   - Depois de impresso, o arquivo sai de `pending\` e aparece em
     `printed\<jobId>.json` com `"acked": false`.
   - Em até ~15s (intervalo do `AckFlusher`), `acked` deve virar `true` no
     mesmo arquivo.
5. **Teste de recuperação (o critério de aceite oficial da Fase 4):** faça
   outro pedido e mate o processo (`Ctrl+C`) bem no instante em que
   `pending\<jobId>.json` aparece, antes de virar `printed\`. Suba o Host
   de novo — o pedido deve sair impresso **uma única vez** (o Host busca
   `jobs/pending` na reconexão, e `RecordReceived` é idempotente).
6. **Teste de falha/retry:** aponte a config da impressora para algo
   inválido (ex: fila inexistente) e faça um pedido — confira em
   `pending\<jobId>.json` que `attempts` cresce e `nextAttemptAt` avança a
   cada ~15s, até esgotar as 5 tentativas (~10 min) e o arquivo migrar para
   `failed\<jobId>.json` com `errorCode` preenchido.

### Critério de sucesso

- Pedido sai impresso exatamente uma vez mesmo com o processo morrendo no
  meio.
- Nenhum job fica "grudado" em `pending\` além do tempo de retry esperado.
- `printed\`/`failed\` acabam com `acked: true` (confirmação chega ao
  backend mesmo que atrasada).

### Resultado (2026-08-09)

**Falhou** no critério de ack. Pedido real (`Pedido-0006-HJB5`) chegou via
SSE e imprimiu localmente sem erro (`printed\<jobId>.json` gravado,
`attempts: 1`), mas `acked` nunca virou `true` — mesmo esperando vários
minutos e reiniciando o Host duas vezes. Causa: o backend rejeita todo
`ack` com `400 Bad Request` porque `printedAt` sai serializado como
`2026-08-09T19:41:47.2275009+00:00` (offset explícito, formato padrão do
`DateTimeOffset` do .NET) e `ackJobSchema` (`print-agents.schema.ts`) usa
`z.iso.datetime()`, que exige sufixo `Z` e rejeita offset — confirmado
reproduzindo a validação Zod isolada. Falha é determinística, não
intermitente. Ver `docs/plan/PRINT-AGENT-REPO.md §0` para detalhe
completo, incluindo um bug secundário (o loop periódico de ack engole essa
falha sem logar nada). Os testes de recuperação (matar o processo em
`pending\`) e de retry/falha (fila inválida) não chegaram a ser executados
por causa deste bloqueio.

**Corrigido em 2026-08-09** (ver `docs/plan/PRINT-AGENT-REPO.md §0` para o
detalhe): `printedAt` agora sempre serializa em UTC com sufixo `Z`, e o
flush de ack no loop de retry local não morre mais em silêncio quando falha.
`dotnet test` cobre a serialização (`JobsApiClientTests`); **este teste
manual (§2) precisa ser refeito do zero** — incluindo os passos de
recuperação e retry/falha que não chegaram a rodar da vez passada — para
confirmar contra o backend real.

---

## 3. Cliente HTTP/SSE contra o backend real (Fase 3) — smoke opcional

Já coberto por teste automatizado contra um servidor SSE fake
(`PrintAgent.Transport.Tests`: reconexão, `Last-Event-ID`, watchdog, `401`,
`device:revoked`). Este item é só um smoke test opcional contra o backend
de verdade, útil se algo parecer errado em produção e os testes automáticos
não reproduzirem:

- Derrube a rede da máquina de teste por alguns segundos com o Host
  pareado e rodando — confira no log que ele detecta a queda (watchdog de
  90s ou erro de socket) e reconecta sozinho com backoff.
- Revogue o dispositivo pelo dashboard — confira que o Host apaga o token
  e **para** de tentar reconectar (não deve entrar em loop de erro 401).

### Resultado (2026-08-09)

**Falhou** — não intencional, reproduzido organicamente (o backend de dev
resetou a conexão durante a sessão). Um `IOException`/`SocketException`
(reset de conexão, 10054) dentro de `SseStreamClient.ConnectAndPumpAsync`
só é capturado se for `OperationCanceledException` — qualquer outra
exceção de rede sobe até `Worker.ExecuteAsync` e derruba o **processo
inteiro** do `PrintAgent.Host` (`BackgroundServiceExceptionBehavior =
StopHost`), em vez de acionar o reconnect-com-backoff que este teste
deveria confirmar. Reproduzido 2x seguidas na mesma sessão (mesmo
stacktrace as duas vezes). Ver `docs/plan/PRINT-AGENT-REPO.md §0` para o
detalhe completo.

**Corrigido em 2026-08-09**: `IOException`/`HttpRequestException` durante a
leitura do stream agora acionam reconexão com backoff normal, e
`Worker.ExecuteAsync` ganhou uma segunda camada de proteção (qualquer
exceção inesperada na sessão pareada loga e reabre em vez de derrubar o
host). `dotnet test` cobre o caso de reset de conexão
(`SseStreamClientTests.ConnectionReset_DuringRead_ReconnectsInsteadOfThrowing`).
**Este teste manual (§3) precisa ser refeito** — derrubar a rede com o Host
pareado e rodando — para confirmar contra condições de rede reais.

---

## 4. Impressora de rede real (Fase 5) — opcional, precisa de hardware

`NetworkPrinterTransport` (IP:9100) já tem teste automatizado contra uma
impressora de rede falsa que recusa a segunda conexão simultânea
(`PrintAgent.Printing.Tests`). Com uma impressora térmica de rede de
verdade na mesma LAN, dá pra confirmar comportamento que o fake não cobre:

- Configure `transport: network` apontando pro IP:porta real e faça um
  pedido — deve imprimir normalmente.
- Enquanto o "PDV fake" do restaurante também manda direto pra mesma
  impressora, confirme que o agente trata a porta ocupada como
  `printer_busy` (retry), não como falha terminal.
- Puxe o cabo de rede da impressora — o agente deve reportar
  `printer_offline`/`printer_busy` e continuar tentando, nunca travar.

---

## 5. Tray — ícone da bandeja e tela de configuração (Fase 6)

### O que é o Tray

`PrintAgent.Tray` é um segundo executável, **um processo Windows completamente
separado** do serviço (`PrintAgent.Host`). A razão é técnica, não estética:
um serviço do Windows roda na "Session 0", que não tem área de trabalho —
ele não consegue desenhar uma janela ou colocar um ícone na bandeja, ponto.
Por isso existe este segundo programa, que:

- roda na sessão do usuário logado no balcão (no instalador da Fase 7, vai
  iniciar sozinho com o Windows, na pasta de startup do usuário);
- mostra um **ícone na bandeja** cuja cor reflete o estado do agente —
  cinza (sem pareamento), vermelho (pareado mas sem conexão com o
  DiskPrato), laranja (conectado, mas a impressora sinalizou problema:
  offline / sem papel / tampa aberta) ou verde (tudo certo);
- abre uma **tela de configuração** (pareamento, escolha de fila do
  Windows ou IP:porta da impressora, largura do papel, code page, opção de
  remover acentos, número de cópias, botão de impressão de teste, e um log
  das últimas ações);
- **nunca lê o `device.dat` (token) nem o `agent.json` diretamente** — tudo
  passa pelo named pipe `\\.\pipe\diskprato-printagent`, conversando em
  JSON com o serviço, que é quem de fato guarda e protege essas
  informações (plano §7.2/§7.4). Por isso o Tray nem referencia o projeto
  `PrintAgent.Host` no código — os dois só compartilham o contrato JSON do
  pipe.

Ou seja: o serviço (`Host`) é quem imprime de verdade e fala com a API do
DiskPrato; o Tray é só o "painel de controle visual" que um humano usa pra
configurar e acompanhar esse serviço, porque o serviço sozinho é invisível.

### Passos

**Suba as duas pontas**, em terminais separados:
```powershell
cd src\PrintAgent.Host
dotnet run
```
```powershell
cd src\PrintAgent.Tray
dotnet run
```

Um ícone cinza deve aparecer na bandeja do sistema (pode estar escondido
no "^" de ícones ocultos do Windows).

Roteiro, na ordem:

- [ ] **Sem pareamento:** o tooltip do ícone mostra algo como "Aguardando
  pareamento". Clique duplo (ou botão direito → "Configurar...") abre a
  tela de setup, com o resumo no topo dizendo "Não pareado".
- [ ] **Pareamento:** digite um código válido do dashboard de dev + um
  nome de dispositivo, clique "Parear". O log de atividade deve mostrar
  "Pareado com sucesso" e o resumo atualizar. Em até 5s (intervalo de
  polling do Tray) o ícone deve refletir se o `Worker` já conectou o SSE.
- [ ] **Configurar impressora:** escolha "Fila do Windows (spooler)",
  selecione (ou digite) `Generic / Text Only`, clique "Salvar" — deve
  logar "Configuração da impressora salva".
- [ ] **Teste de impressão:** clique "Imprimir teste" (no menu de contexto
  do ícone e também dentro da tela de setup) — deve sair um cupom
  sintético com acentos (`ç ã õ é`) e logar sucesso/falha.
- [ ] **Persistência da tela:** feche e reabra a tela de setup — os campos
  devem vir pré-preenchidos com o que foi salvo (valida o comando
  `get-config`).
- [ ] **Estado real da impressora:** desligue/desconecte a impressora (ou
  aponte pra um host de rede inválido) — o resumo deve mostrar
  "offline"/"estado desconhecido" em vez de fingir que está pronta, e o
  ícone deve virar laranja quando pareado + conectado mas com a impressora
  com problema.
- [ ] **Despareamento:** clique "Desparear", confirme o diálogo — o
  resumo volta a "Não pareado".
- [ ] **Encerramento:** botão direito no ícone → "Sair" — o ícone some da
  bandeja e o processo do Tray termina; o `Host` continua rodando à parte
  (são processos independentes).

Se preferir testar o protocolo do pipe sem abrir o Tray (útil para
depurar), dá pra falar com o serviço direto por PowerShell:

```powershell
function Send-Ipc($json) {
  $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", "diskprato-printagent", [System.IO.Pipes.PipeDirection]::InOut)
  $pipe.Connect(2000)
  $writer = New-Object System.IO.StreamWriter($pipe); $writer.AutoFlush = $true
  $reader = New-Object System.IO.StreamReader($pipe)
  $writer.WriteLine($json)
  $reader.ReadLine()
  $pipe.Dispose()
}

Send-Ipc '{"Command":"get-status"}'
Send-Ipc '{"Command":"get-config"}'
Send-Ipc '{"Command":"set-printer","Printer":{"Transport":0,"SpoolerName":"Generic / Text Only","PaperWidthMm":80,"CodePage":850,"EscTIndex":2}}'
Send-Ipc '{"Command":"test-print"}'
Send-Ipc '{"Command":"pair","Code":"XXXX-XXXX","DeviceName":"balcao-teste"}'
Send-Ipc '{"Command":"unpair"}'
```

(`"Transport":0` = spooler, `"Transport":1` = network — o pipe serializa o
enum como número, não como texto.)

### Resultado (2026-08-09)

**Bloqueado** logo no primeiro item visual do roteiro — a tela de setup
abre completamente ilegível (screenshot arquivado nesta sessão): cada
`GroupBox` ("Estado", "Pareamento", "Impressora") renderiza como uma
coluna estreitíssima com o título quebrado uma letra por linha, e nenhum
controle (textbox/combo/botão) fica visível. Causa raiz em
`SetupForm.Section()`: `GroupBox` com `AutoSize = true` **e**
`Width = 440` ao mesmo tempo, combinado com o `FlowLayoutPanel` interno
também `AutoSize = true` + `Dock = DockStyle.Top` — o layout engine
colapsa a caixa a uma largura quase zero. Não é problema de DPI. O
pareamento em si (via pipe, fora da UI) funcionou normalmente — só a tela
visual do Tray está quebrada. Nenhum item do checklist abaixo foi validado
por causa deste bloqueio. Pedido de melhoria observado à parte (não é
bug): trocar o ícone atual da bandeja (cor sólida genérica) por algo mais
estilizado, como uma cabeça de robô ou motivo de impressão. Ver
`docs/plan/PRINT-AGENT-REPO.md §0` para o detalhe completo.

**Corrigido em 2026-08-09**: `SetupForm.Section()` não doca mais o
`FlowLayoutPanel` interno (`Dock = DockStyle.Top` removido) e o `GroupBox`
usa `MinimumSize = new Size(440, 0)` em vez de `Width = 440` fixo — as duas
mudanças eliminam o conflito com `AutoSize`. `dotnet build` limpo, mas
como é UI WinForms sem cobertura de teste automatizado (natureza da Fase
6), **todo o checklist desta seção §5 precisa ser refeito do zero** numa
sessão desktop real para confirmar visualmente. Pedido de melhoria do
ícone não foi endereçado (fora de escopo desta correção).

---

## 6. Hardware físico real (antecipável antes da Fase 8)

A Fase 8 formaliza isso ("corte de papel, fim de papel e impressora
desligada produzem os `errorCode` certos no dashboard"), mas dá pra
adiantar uma verificação com uma impressora térmica de verdade
(Elgin i9 ou Epson TM-T20) na rede, já que `PrinterStatus`
(`Ready`/`Offline`/`PaperOut`/`CoverOpen`) já é lido pelo Tray desde a
Fase 6:

- Com a impressora configurada como `network`, tire o papel e confirme que
  o resumo do Tray muda pra "sem papel" em até 5s.
- Abra a tampa e confirme "tampa aberta".
- Desligue a impressora e confirme "offline".

Isso não passa pelo `StatusReport` que o `Worker` manda pro backend (esse
ainda é `Unknown` sempre — TODO explícito da Fase 8), só valida a leitura
local via `DLE EOT` que o Tray já expõe.

---

## Depois de validar

Atualize `docs/plan/PRINT-AGENT-REPO.md §0`: mova o item correspondente de
"Pendências manuais" pra fora da lista (ou anote o resultado), e ajuste o
"Próximo passo" se a validação destravar a fase seguinte.
