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

O fluxo normal foi reconfirmado numa terceira rodada em 2026-08-10, depois
do refactor de roteamento por estação (Fase 2 do §10): pedido real
imprimiu e confirmou sozinho, sem nenhuma estação dedicada configurada
(topologia de agente único). Nessa mesma rodada também foi testado (pela
primeira vez) o cenário de job órfão na fila de acks — achou um bug novo,
ver abaixo.

### O que ainda falta validar

Nada neste teste. O bug do ack órfão encontrado na terceira rodada
(`AckOutcome.JobNotFound` ignorado em silêncio, job re-tentado pra sempre)
foi corrigido — 404 no ack agora loga e chama `JobStore.Discard(jobId)`,
com cobertura em `AckFlusherTests` — e **revalidado contra o backend real
em 2026-08-10 (quarta rodada)**: um `printed\<jobId>.json` plantado à mão
com um `jobId` inexistente sumiu da pasta no primeiro ciclo do
`AckFlusher`, com o warning correspondente no log do Host. Na mesma rodada
a correção ainda limpou sozinha um job que estava travado de verdade desde
09/08 (`cmsmb9b7d…` em `failed\`, `acked: false`, `lastAckError:
"timeout"`), que o backend também já não reconhecia.

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
- [x] **Persistência da tela** (revalidado em 2026-08-10): feche e reabra a
  tela de setup — as seções de impressora vêm reconstruídas a partir do que
  foi salvo (valida `get-config` devolvendo a lista).
- [x] **Despareamento** (revalidado em 2026-08-10): clique "Desparear",
  confirme o diálogo — o resumo volta a "Não pareado" e o `device.dat`
  some do `%ProgramData%`. A configuração de impressoras sobrevive.
- [x] **Janela redimensionável** (mudança de 2026-08-10, validada na mesma
  data): a janela expõe minimizar/maximizar/redimensionar
  (`WindowPattern.CanResize`/`CanMinimize`), mínimo 500x400, e nasce
  dentro da área útil — nesta tela de 768px de altura ela abriu com 727px
  em vez dos 860 pedidos, com a borda de baixo visível. Minimizar e
  restaurar pela barra de título funciona.
- [ ] **Encerramento:** botão direito no ícone → "Sair" — o ícone some da
  bandeja e o processo do Tray termina; o `Host` continua rodando à parte
  (são processos independentes).

### Roteiro — múltiplas estações (Fase 3 do §10) — **validado em 2026-08-10**

Todos os itens abaixo passaram numa sessão desktop real (quarta rodada),
dirigindo a tela por UI Automation. Ficam aqui como roteiro de regressão —
refaça se mexer em `SetupForm`/`AgentController`.

- [x] **Impressora padrão:** seção em branco → fila `Generic / Text Only`
  → "Salvar" loga `Configuração da impressora "Padrão" salva.`.
  "Imprimir teste" sai um cupom sintético com acentos (`ç ã õ é`).
- [x] **Adicionar uma segunda estação** ("Cozinha"): loga `Configuração da
  impressora "Cozinha" salva.` e a seção "Padrão" continua intacta.
- [x] **Duplicar estação (validação client-side):** uma segunda seção
  apontando pra "Cozinha" **bloqueia no cliente** e não chama o pipe —
  confirmado por hash do `agent.json` idêntico antes e depois.
- [x] **Imprimir teste por estação:** cada botão imprime no destino da sua
  própria seção. Como não dá pra ver a saída de uma fila `FILE:` nesta
  máquina (ver "Observabilidade" abaixo), a prova foi apontar a "Cozinha"
  pra um listener TCP local (`network`, `127.0.0.1:9100`): o teste da
  Cozinha chegou lá (604 bytes de ESC/POS válido) e o da Padrão não.
- [x] **Persistência com múltiplas seções:** fechar e reabrir reconstrói
  as duas seções na ordem salva, com os campos certos.
- [x] **Remover uma estação** e **remover a última seção** (deixa uma
  seção em branco no lugar, nunca uma tela sem nada editável).
- [x] **Limitação conhecida — o comportamento real é *diferente* do que
  este roteiro previa.** Com só "Cozinha" configurada, o resumo **não**
  diz "não configurada": mostra os dados da impressora da Cozinha
  rotulados como "Impressora padrão" (`Impressora padrão (Network —
  127.0.0.1): estado desconhecido`), porque `ResolveDefaultPrinter()` cai
  no "primeira da lista". Mais enganoso do que se supunha, mas sem crash
  nem texto incoerente. Só some de vez com status por estação no
  `get-status`.

### Roteiro — bugs corrigidos em 2026-08-09

- [x] **Ícone laranja em estado "Unknown"** — revalidado em 2026-08-10.
  Com a impressora padrão apontada pra `Fila-Inexistente-XYZ`, o resumo
  mostrou "estado desconhecido" **em laranja** (verde no estado
  saudável). `TrayIcons.For()` (ícone) e `ColorFor()` (resumo) chamam o
  mesmo `StateFor()`, então a cor do texto prova a cor do ícone.
- [x] **Reparear sem restart** — revalidado em 2026-08-10. Despareado e
  pareado de novo pela própria tela, com o `PrintAgent.Host` no mesmo
  processo o tempo todo: o resumo voltou sozinho a "Pareado, conectado ao
  DiskPrato". O log do Host mostra a sequência inteira —
  `Stream conectado (deviceId=<antigo>)` → `Sem token de dispositivo —
  aguardando pareamento.` → `Stream conectado (deviceId=<novo>)` —,
  confirmando `Worker.RunPairedAsync` reagindo a
  `AgentController.TokenChanged`. Ao refazer: **tenha o código do
  dashboard em mãos antes de desparear** (desparear sem ele deixa o
  agente sem imprimir), e gere o código no **mesmo restaurante** em que o
  device já está, senão o balcão troca de vínculo. Cada pareamento cria
  uma linha nova em `print_agent_devices` — as antigas ficam para trás.

### Observabilidade e automação (o que economiza tempo na próxima rodada)

- **Não dá pra conferir a saída de uma fila `FILE:` nesta máquina.**
  `Get-PrintJob` não mostra job nenhum nem com a fila pausada — nem para
  um `Out-Printer` de controle, o que descarta o agente como causa — e o
  log `Microsoft-Windows-PrintService/Operational`, que registraria job e
  impressora de destino, **precisa de admin** pra ser habilitado
  (`wevtutil sl` devolve "Acesso negado"). Para provar roteamento, aponte
  uma das estações pra um listener TCP em `127.0.0.1:9100` e leia os bytes.
- **Dirija a tela por UI Automation, não por coordenadas de mouse.**
  `InvokePattern`/`ValuePattern`/`SelectionItemPattern` não dependem de
  foreground (o problema que travou a terceira rodada) e alcançam
  controles fora da área visível do painel rolável.
- **O Windows 11 não expõe os ícones da bandeja via UIA**, então não dá
  pra abrir a tela clicando no ícone por esse caminho. Mande
  `WM_TRAYMOUSEMESSAGE` (`WM_USER+1024`, `lParam = WM_LBUTTONDBLCLK`) pra
  janela oculta do `NotifyIcon` — é o equivalente ao duplo-clique.
- **Janela minimizada some da árvore UIA** (rect vira `-32000,-32000`). Se
  um passo falhar com "botão não encontrado", cheque `WindowVisualState`
  antes de suspeitar do código.

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

## 5. Instalador `.msi` em VM Windows limpa (Fase 7)

Critério de aceite do plano §8 Fase 7: "`.msi` instala numa VM Windows
limpa, serviço sobe no boot, desinstalar não deixa serviço órfão nem
arquivo em `%ProgramData%`". **Nunca executado** — precisa de uma VM, não
da máquina de desenvolvimento (que já tem .NET, já tem a pasta
`%ProgramData%\DiskPrato` populada por rodadas anteriores e cujo estado
mascararia justamente o que o teste quer provar).

### Gerar o pacote

```powershell
dotnet build installer/PrintAgent.Installer.wixproj -c Release
# -> installer/bin/Release/DiskPratoPrintAgent.msi
```

O `installer/` está fora da `PrintAgent.slnx` de propósito (publica ~190 MB
self-contained a cada build), então `dotnet build` na raiz **não** gera o
`.msi`.

### Passos

1. VM Windows 11 x64 limpa, **sem** .NET instalado — o pacote é
   self-contained justamente para não depender disso.
2. Instalar com duplo clique. Aceitar a licença, concluir com o checkbox
   "Abrir a configuração do Print Agent" marcado.
3. Conferir logo após instalar:
   - `Get-Service DiskPratoPrintAgent` → `Running`, `StartType Automatic`.
   - `sc.exe qfailure DiskPratoPrintAgent` → três ações `RESTART -- 60000`
     e `RESET_PERIOD 86400`.
   - `Get-Acl C:\ProgramData\DiskPrato\PrintAgent | Format-List` → só
     `SYSTEM` e `BUILTIN\Administradores`, herança desativada
     (`AreAccessRulesProtected` = `True`).
   - A janela de configuração do Tray abriu sozinha e o ícone está na
     bandeja.
   - Atalho "DiskPrato Print Agent" no menu Iniciar, com o ícone certo.
4. **Reiniciar a VM.** O serviço tem que voltar sozinho e o ícone da
   bandeja tem que aparecer no login (entrada em
   `HKLM\...\CurrentVersion\Run`). Testar com um segundo usuário local
   também — o `Run` é HKLM justamente para o operador do balcão, que não é
   quem instalou.
5. Parear, configurar impressora e imprimir cupom de teste — a partir daqui
   é o roteiro do §3, mas agora saindo de uma instalação de verdade.
6. **Upgrade in-place:** buildar com `-p:Version=1.0.1`, instalar por cima
   e conferir que o `deviceId` e a impressora configurada sobreviveram
   (`agent.json` não é gravado pelo MSI de propósito) e que não pediu
   reboot — o `util:CloseApplication` fecha o tray antes.
7. **Desinstalar** pelo Painel de Controle / Aplicativos e Recursos e
   conferir:
   - `Get-Service DiskPratoPrintAgent` → não existe mais.
   - `C:\Program Files\DiskPrato` → não existe mais.
   - `C:\ProgramData\DiskPrato` → não existe mais (o `util:RemoveFolderEx`
     apaga a fila, o `agent.json` e o `device.dat`, que nascem em runtime e
     o MSI não conhece).
   - A entrada em `HKLM\...\CurrentVersion\Run` sumiu.
   - Nenhum atalho sobrando no menu Iniciar.

### Critério de sucesso

Todos os itens dos passos 3, 4, 6 e 7 conferem, e o passo 5 chega ao cupom
impresso sem abrir terminal nem editar arquivo.

### O que este teste não cobre

Assinatura de código: o `.msi` local sai sem assinar. Quem assina é o
`release.yml`, e só quando os secrets `SIGNING_CERT_PFX_BASE64` e
`SIGNING_CERT_PASSWORD` existirem no repositório. Enquanto não existirem, o
SmartScreen vai avisar "editor desconhecido" na VM — esperado, não é falha
do pacote.

---

## Depois de validar

Atualize `docs/plan/PRINT-AGENT-REPO.md §0`: mova o item correspondente de
"Pendências manuais"/checklist pra fora da lista (ou anote o resultado), e
ajuste o "Próximo passo" se a validação destravar a fase seguinte.
