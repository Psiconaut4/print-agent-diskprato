# Testes manuais — DiskPrato Print Agent

Este documento reúne **todos** os testes que não dá para automatizar num
agente/CI: exigem uma sessão de desktop real do Windows, hardware físico
(impressora térmica), ou um backend de verdade rodando. Tudo que pode ser
coberto por `dotnet test` já está nos projetos `tests/*` — este arquivo é
só para o que sobra.

Fonte da lista: seção "Pendências manuais" e critérios de aceite de cada
fase em `docs/plan/PRINT-AGENT-REPO.md §8`/`§10`. Atualize os dois
documentos juntos quando um teste passar a ser automatizável ou quando uma
fase nova adicionar pendência própria.

Itens já validados e fechados (convivência do spooler com outro PDV, fase
2; SSE contra backend real — reconexão e revogação, fase 3) saíram deste
arquivo. O histórico completo desses dois — bugs encontrados, causa raiz,
correção — continua em `docs/plan/PRINT-AGENT-REPO.md §0`, não precisa
duplicar aqui.

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
   - Para os testes de múltiplas estações (§3), é útil ter uma **segunda**
     fila `FILE:` com outro nome (ex. `Generic / Text Only (Cozinha)`), pra
     confirmar que cada seção do Tray de fato manda pra uma fila diferente.
3. **Para os testes que envolvem pedido de verdade (fila local, Tray):**
   um código de pareamento válido gerado no dashboard de dev do DiskPrato,
   e o backend de dev acessível pela máquina de teste.

---

## 1. Fila local em arquivo e recuperação de pedidos (Fase 4 — `JobStore`)

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

1. Suba o Host pareado com o backend de dev (ver §3, comando `pair`, ou
   pareie pelo Tray).
2. Configure a impressora "padrão" pra fila `Generic / Text Only` (§3,
   `set-printer`, ou pelo Tray).
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
5. **Teste de recuperação:** faça outro pedido e mate o processo
   (`Ctrl+C`) bem no instante em que `pending\<jobId>.json` aparece, antes
   de virar `printed\`. Suba o Host de novo — o pedido deve sair impresso
   **uma única vez**.
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

### Status

Os três passos (fluxo normal, recuperação, retry/falha) **passaram** contra
o backend de dev real em 2026-08-09, depois de duas rodadas de bugs
encontrados e corrigidos no formato/serialização do `ack` (`printedAt` sem
`Z`, depois campos opcionais nulos explícitos no JSON) — detalhe completo
em `docs/plan/PRINT-AGENT-REPO.md §0`.

### O que ainda falta validar

- **`AckFlusher.SendAsync` com job órfão na fila.** Um bug secundário foi
  encontrado na rodada 2 (uma exceção inesperada de ack — ex. `400` de um
  job que não existe mais no backend — abortava a rodada de flush inteira,
  deixando jobs válidos sem tentar) e corrigido em 2026-08-09 (catch
  genérico que loga e segue pro próximo). **Nunca foi revalidado contra o
  backend real** depois da correção. Passos: deixe um job órfão em
  `printed\<jobId>.json` com `acked: false` de uma sessão anterior (ou crie
  um manualmente com um `jobId` que o backend não reconhece), faça um
  pedido novo válido, e confirme que o pedido novo é confirmado (`acked:
  true`) mesmo com o órfão continuando a falhar a cada rodada — sem travar
  o restante da fila.
- **Fluxo ponta a ponta depois do refactor de roteamento por estação**
  (Fase 2 do plano §10, 2026-08-10). `PrintOrchestrator` trocou os
  parâmetros `PrinterProfile`/`IPrinterTransport` pré-calculados por um
  delegate resolvido só depois de desserializar o job, e a criação do
  transporte concreto (`PrinterTransportFactory.Create`) entrou pro mesmo
  `try` da formatação/envio. É uma mudança de implementação sem mudança de
  contrato externo, coberta por unit test (`PrintOrchestratorTests`,
  `AgentControllerTests`) — mas nunca foi exercitada com um pedido real de
  ponta a ponta desde o refactor. Repita os passos 1–4 acima (fluxo normal)
  numa instalação de estação única (topologia 1 do §10, sem nenhuma
  impressora dedicada configurada) só para confirmar que o caminho mais
  comum continua idêntico ao de antes do refactor.

---

## 2. Impressora de rede real (Fase 5) — opcional, precisa de hardware

`NetworkPrinterTransport` (IP:9100) já tem teste automatizado contra uma
impressora de rede falsa que recusa a segunda conexão simultânea
(`PrintAgent.Printing.Tests`). Com uma impressora térmica de rede de
verdade na mesma LAN, dá pra confirmar comportamento que o fake não cobre.
**Nunca executado** — depende de hardware que ainda não foi providenciado.

- Configure `transport: network` apontando pro IP:porta real e faça um
  pedido — deve imprimir normalmente.
- Enquanto o "PDV fake" do restaurante também manda direto pra mesma
  impressora, confirme que o agente trata a porta ocupada como
  `printer_busy` (retry), não como falha terminal.
- Puxe o cabo de rede da impressora — o agente deve reportar
  `printer_offline`/`printer_busy` e continuar tentando, nunca travar.

---

## 3. Tray — ícone da bandeja e tela de configuração, com múltiplas estações (Fase 6 + Fase 3 do §10)

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
  DiskPrato), laranja (conectado, mas a impressora "padrão" sinalizou
  problema: offline / sem papel / tampa aberta / estado desconhecido) ou
  verde (tudo certo);
- abre uma **tela de configuração** com pareamento e uma **seção de
  impressora por estação** (Fase 3 do §10): cada seção tem seu combo de
  Estação (Padrão/Cozinha/Bar/Balcão/Cliente), fila do Windows ou
  IP:porta, papel, code page, remover acentos, cópias, e botões
  Salvar/Imprimir teste/Remover independentes; um botão "+ Adicionar
  impressora" no rodapé cria uma seção em branco. A tela também tem um log
  das últimas ações;
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

### Roteiro base (já validado uma vez, revalidar se algo aqui quebrar)

- [ ] **Sem pareamento:** o tooltip do ícone mostra algo como "Aguardando
  pareamento". Clique duplo (ou botão direito → "Configurar...") abre a
  tela de setup, com o resumo no topo dizendo "Não pareado".
- [ ] **Pareamento:** digite um código válido do dashboard de dev + um
  nome de dispositivo, clique "Parear". O log de atividade deve mostrar
  "Pareado com sucesso" e o resumo atualizar. Em até 5s (intervalo de
  polling do Tray) o ícone deve refletir se o `Worker` já conectou o SSE.
- [ ] **Persistência da tela:** feche e reabra a tela de setup — as seções
  de impressora devem vir reconstruídas a partir do que foi salvo (valida
  `get-config` devolvendo a lista).
- [ ] **Despareamento:** clique "Desparear", confirme o diálogo — o
  resumo volta a "Não pareado".
- [ ] **Encerramento:** botão direito no ícone → "Sair" — o ícone some da
  bandeja e o processo do Tray termina; o `Host` continua rodando à parte
  (são processos independentes).

### Roteiro novo — múltiplas estações (Fase 3 do §10, **nunca validado numa sessão desktop real**)

- [ ] **Impressora padrão:** com a tela recém-aberta (uma seção em
  branco, Estação = "Padrão"), configure a fila `Generic / Text Only`,
  clique "Salvar" — log deve mostrar `Configuração da impressora "Padrão"
  salva.`. "Imprimir teste" nessa seção deve sair um cupom sintético com
  acentos (`ç ã õ é`).
- [ ] **Adicionar uma segunda estação:** clique "+ Adicionar impressora",
  na seção nova selecione Estação = "Cozinha", configure a segunda fila
  `FILE:` (ou a mesma, se só tiver uma disponível), "Salvar" — log deve
  mostrar `Configuração da impressora "Cozinha" salva.`, e a seção
  "Padrão" configurada antes deve continuar intacta (não foi
  sobrescrita).
- [ ] **Duplicar estação (validação client-side):** adicione uma terceira
  seção, selecione Estação = "Cozinha" de novo (mesma da seção anterior) e
  clique "Salvar" — deve **bloquear no cliente**, sem chamar o pipe:
  log mostra algo como `Já existe outra impressora configurada para
  "Cozinha"...`, e a config da seção "Cozinha" original não muda.
- [ ] **Imprimir teste por estação:** com "Padrão" e "Cozinha" salvas,
  clique "Imprimir teste" em cada seção separadamente — cada uma deve
  imprimir na fila daquela seção especificamente (confirme comparando os
  arquivos de saída das duas filas `FILE:`, se configuradas com nomes
  diferentes).
- [ ] **Persistência com múltiplas seções:** feche e reabra a tela — devem
  aparecer duas seções (Padrão + Cozinha), cada uma com os campos certos
  pré-preenchidos, na mesma ordem/config salva.
- [ ] **Remover uma estação:** na seção "Cozinha", clique "Remover",
  confirme o diálogo — a seção desaparece da tela e o log confirma
  `Impressora "Cozinha" removida.`. Reabra a tela: só "Padrão" deve
  restar.
- [ ] **Remover a última seção:** com só "Padrão" configurada, remova-a
  também — a tela nunca deve ficar sem nenhuma seção editável (uma seção
  em branco, Estação = "Padrão", deve aparecer no lugar automaticamente).
- [ ] **Limitação conhecida a confirmar (não é bug):** configure só uma
  impressora "Cozinha" (sem nenhuma "Padrão"). O resumo "Estado" no topo
  da tela deve mostrar a impressora como "não configurada" mesmo com a
  seção "Cozinha" funcionando normalmente — o resumo hoje só lê o status
  da impressora "padrão" (`Station == null`), não uma agregação de todas
  as estações (`get-status` não ganhou granularidade por estação nesta
  fase, só `get-config`). Confirme que é exatamente esse o comportamento
  (visualmente confuso, mas esperado) e não algo pior (crash, texto
  incoerente).

### Roteiro — bugs corrigidos em 2026-08-09 que nunca foram revalidados

Estes dois foram corrigidos em código (`dotnet test` cobre a unidade
correspondente) mas **nunca confirmados de novo numa sessão desktop
real**:

- [ ] **Ícone laranja em estado "Unknown".** Aponte a impressora "padrão"
  pra um nome de fila inexistente (erro de digitação proposital) — o
  resumo deve mostrar "estado desconhecido" e o **ícone deve ficar
  laranja**, não verde. (`TrayIcons.StateFor()` mapeando `"Unknown"` pra
  `PrinterProblem`.)
- [ ] **Reparear sem restart.** Com o Host pareado e a sessão SSE já
  conectada (ícone verde/vermelho), desparear e parear de novo **pela
  própria tela** (não pelo endpoint admin) — o resumo deve voltar a
  "conectado ao DiskPrato" sozinho, sem precisar reiniciar o processo do
  `PrintAgent.Host`. (`Worker.RunPairedAsync` reagindo a
  `AgentController.TokenChanged`.)

### Testando o protocolo do pipe sem abrir o Tray

Útil para depurar sem esperar a UI:

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
Send-Ipc '{"Command":"set-printer","Printer":{"Station":null,"Transport":0,"SpoolerName":"Generic / Text Only","PaperWidthMm":80,"CodePage":850,"EscTIndex":2}}'
Send-Ipc '{"Command":"set-printer","Printer":{"Station":0,"Transport":0,"SpoolerName":"Generic / Text Only (Cozinha)","PaperWidthMm":80,"CodePage":850,"EscTIndex":2}}'
Send-Ipc '{"Command":"test-print"}'
Send-Ipc '{"Command":"test-print","Station":0}'
Send-Ipc '{"Command":"remove-printer","Station":0}'
Send-Ipc '{"Command":"pair","Code":"XXXX-XXXX","DeviceName":"balcao-teste"}'
Send-Ipc '{"Command":"unpair"}'
```

`"Transport":0` = spooler, `"Transport":1` = network — o pipe serializa o
enum como número, não como texto. `"Station"` segue o mesmo esquema:
`0`=Kitchen, `1`=Bar, `2`=Counter, `3`=Customer; ausente/`null` = "padrão".
`get-config` agora devolve `"Printers"` (lista), não mais `"Printer"`
(objeto único).

### Histórico

Layout quebrado (`GroupBox`/`AutoSize`), ícone não refletindo estado
"Unknown", e pareamento local não reconectando sem restart — todos
encontrados e corrigidos entre 2026-08-09 e a introdução das seções por
estação. Detalhe completo (causa raiz, diffs, datas) em
`docs/plan/PRINT-AGENT-REPO.md §0`; não duplicado aqui.

---

## 4. Hardware físico real (antecipável antes da Fase 8)

A Fase 8 formaliza isso ("corte de papel, fim de papel e impressora
desligada produzem os `errorCode` certos no dashboard"), mas dá pra
adiantar uma verificação com uma impressora térmica de verdade
(Elgin i9 ou Epson TM-T20) na rede, já que `PrinterStatus`
(`Ready`/`Offline`/`PaperOut`/`CoverOpen`) já é lido pelo Tray desde a
Fase 6. **Nunca executado** — depende de hardware que ainda não foi
providenciado.

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
"Pendências manuais"/checklist pra fora da lista (ou anote o resultado), e
ajuste o "Próximo passo" se a validação destravar a fase seguinte.
