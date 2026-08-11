# DiskPrato Print Agent

Agente Windows (.NET 10) que roda como Worker Service no balcão do
restaurante: conecta na API do DiskPrato via SSE, recebe pedidos e imprime
em impressora térmica ESC/POS convivendo com o PDV que já está instalado.

**Fonte da verdade do plano e das decisões de arquitetura:**
`docs/plan/PRINT-AGENT-REPO.md`. Antes de mexer em qualquer coisa não óbvia
pelo código, leia a seção `0. Status atual` (progresso por fase) e a seção
correspondente ao componente que vai mudar — o documento explica o "porquê"
por trás de quase toda decisão não trivial (formatos de arquivo, ordem de
gravação, timeouts, etc.).

> `docs/plan/` **não é versionado** (está no `.gitignore`): o repositório é
> público e o plano descreve a arquitetura interna do backend. Ele vive na
> máquina de quem desenvolve. Num clone novo o arquivo não existe — peça a
> cópia atual antes de mexer em algo que dependa dele. Os comentários do
> código citam seções dele ("plano §7.2") de propósito; a referência
> continua válida mesmo com o arquivo fora do git.

## Estrutura

```
src/
  PrintAgent.Contracts/   DTOs gerados do OpenAPI via NSwag — nunca editar à mão
  PrintAgent.Core/        EscPosFormatter (PrintJob -> byte[]), retry policy — puro, sem HTTP/Win32
  PrintAgent.Printing/    IPrinterTransport: SpoolerPrinterTransport (RAW/winspool), NetworkPrinterTransport (IP:9100)
  PrintAgent.Transport/   Cliente HTTP + SSE da API do DiskPrato, pareamento
  PrintAgent.Host/        Worker Service: fila local em arquivo, PrintOrchestrator, AckFlusher, named pipe IPC
  PrintAgent.Tray/        Tray icon + tela de setup (WinForms) — fala com Host só pelo JSON do named pipe, sem referenciar o projeto Host
tests/                    um projeto de teste por projeto de src/ acima (PrintAgent.Tray não tem — é UI, validação manual)
installer/                WiX v5 -> .msi (Package.wxs). Fora da .slnx: publica ~190 MB self-contained por build
resources/                ícones; icon-16-256.ico é gerado de icon-256.ico por build-icon.ps1
```

Regra de dependência: `Host` → `Transport`/`Printing`/`Core` → `Contracts`.

## Build e testes

```powershell
dotnet build      # deve sair com 0 avisos, 0 erros (Directory.Build.props: warnings as errors)
dotnet test       # todos os projetos de teste, sem mocks de filesystem/HTTP — usam diretório temp real e servidor fake

dotnet build installer/PrintAgent.Installer.wixproj -c Release   # -> installer/bin/Release/DiskPratoPrintAgent.msi
```

O `.msi` **não** sai de `dotnet build` na raiz: o `installer/` está fora da
`PrintAgent.slnx` de propósito, porque cada build dele publica os dois
executáveis self-contained (~190 MB). Um job separado do `ci.yml` monta o
pacote para que quebra de `.wxs` apareça mesmo assim.

O que `dotnet test` não cobre (Tray, convivência do spooler com hardware
real, fila local ponta a ponta contra backend real) está roteirizado em
`docs/testes-manuais.md`.

## Estado atual (2026-08-11)

**Todas as fases do plano estão implementadas** (0–8: scaffold, Contracts,
EscPosFormatter, os dois transportes de impressão, cliente HTTP/SSE, Worker
Service, Tray/setup, instalador WiX, endurecimento), mais as três fases de
roteamento por estação do §10 (contrato v1.1.0 consumido —
`stationLabel`/`printMode`, `AgentConfig.Printers` como lista por estação, e
o Tray editando N impressoras).

Da Fase 5, `NetworkPrinterTransport` está implementado e o critério de aceite
do plano §8 (impressora de rede falsa que recusa a segunda conexão simultânea
→ retry com backoff, sem falha terminal) é coberto por teste automatizado em
`PrintAgent.Printing.Tests`.

**O que resta é validação manual, não código:** o aceite da Fase 8 é em
hardware térmico real (`docs/testes-manuais.md` §2/§4 — corte de papel, fim
de papel e impressora desligada produzindo os `errorCode` certos) e o da
Fase 7 é o `.msi` numa VM Windows limpa (§5). O checklist do Tray foi inteiro
validado em 2026-08-10, incluindo múltiplas estações. Ver tabela e histórico
completo em `docs/plan/PRINT-AGENT-REPO.md §0`.

**Diagnóstico (Fase 8):** tudo em `src/PrintAgent.Host/Diagnostics/`.
`AgentPaths` centraliza os caminhos sob `%ProgramData%\DiskPrato\PrintAgent`;
`AgentVersion` lê a versão do assembly (não repita `"1.0.0"` em lugar
nenhum); `AgentLogging` configura o Serilog **em código** (o publish
single-file quebra a descoberta de sinks por configuração);
`StartupSelfTest` roda na subida do serviço e **nunca imprime**;
`DiagnosticsExporter` monta o `.zip` de suporte e **nunca inclui o
`device.dat`** — ele lista o que quer incluir, em vez de excluir o que não
deve ir.

**Backend em fase de teste:** o default de `AgentConfig.ApiBaseUrl` aponta
para o túnel Cloudflare (`https://app.psiconaut4.com.br`), não para
`api.diskprato.com`. O default só vale para `agent.json` recém-criado —
depois disso o valor no arquivo manda. O `ApiBaseUrl` é **só a origem, sem
caminho**: o prefixo `/api` já está nos caminhos dos clientes
(`/api/print-agents/v1/...`), que são root-relative, então um caminho posto
ali é descartado em silêncio na resolução da URI — `StartupSelfTest` avisa.

**Fila local:** arquivo, não banco. `%ProgramData%\DiskPrato\PrintAgent\queue\`
com `pending/`, `printed/`, `failed/` — um `.json` por job, escrita atômica
(temp file + `File.Move`), transição de estado é mover entre pastas. Não usa
SQLite (decisão revertida em 2026-08-08; ver plano §0 e §7.1 para o
raciocínio). `JobStore` (`src/PrintAgent.Host/Storage/JobStore.cs`) é o único
ponto de acesso a essa fila.

**Named pipe** `\\.\pipe\diskprato-printagent` (`NamedPipeIpcServer`), JSON
por linha, uma conexão por comando:
`get-status`/`get-config`/`pair`/`unpair`/`set-printer`/`remove-printer`/`test-print`/`export-diagnostics`.
A ACL do pipe permite `Users` porque o Tray roda sem elevação — por isso
`export-diagnostics` grava o arquivo com `RunAsClient` (impersonando quem
pediu) em vez de escrever como `LocalSystem`, que seria escrita arbitrária
com privilégio de SYSTEM. Ao adicionar comando que escreva em caminho vindo
do cliente, siga o mesmo padrão.

## Convenções deste repo

- Commits seguem Conventional Commits (`feat(host): ...`, `fix(contracts): ...`, `docs(plan): ...`).
- `PrintAgent.Core` é o único projeto sem dependência de Win32/HTTP — mantenha assim; é onde ficam os golden tests de ESC/POS.
- Nunca somar/recalcular valores em centavos que vêm do backend (`unitPriceCents`, `subtotalCents`, `totalCents` etc.) — o agente formata, não calcula (plano §1, §5.4).
- Hora do cupom sempre em `order.timezone` (IANA), nunca no fuso da máquina local (plano §5.4).
- Ramificar tratamento de erro HTTP sempre por `ApiError.code`, nunca pelo texto de `message` (plano §6.6).
- `installer/License.rtf` é **gerado**, não editado à mão: edite `installer/License.txt` e o build do `.wixproj` regenera e valida o RTF (`installer/build-license.ps1`). Editar o `.rtf` direto foi como a tela de termos do `.msi` ficou em branco por um release inteiro.
- Arquivo gerado durante o build **nunca** pode depender do glob padrão `**/*.cs` para ser compilado: o glob é expandido na *avaliação* do projeto, antes de qualquer target rodar, então em clone limpo o arquivo ainda não existe e some da compilação sem erro nenhum. Inclua explicitamente no `Compile` a partir de um target (ver `IncludeGeneratedContracts` em `PrintAgent.Contracts.csproj`). Foi assim que o CI ficou vermelho enquanto todo build local passava.
- Antes de mexer em build/instalador, valide em **worktree limpo** (`git worktree add`), não no diretório de trabalho: `obj/`, `bin/` e `Generated/` mascaram exatamente a classe de bug que só o CI vê.
