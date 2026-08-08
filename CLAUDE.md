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

## Estrutura

```
src/
  PrintAgent.Contracts/   DTOs gerados do OpenAPI via NSwag — nunca editar à mão
  PrintAgent.Core/        EscPosFormatter (PrintJob -> byte[]), retry policy — puro, sem HTTP/Win32
  PrintAgent.Printing/    IPrinterTransport: SpoolerPrinterTransport (RAW/winspool), NetworkPrinterTransport (IP:9100)
  PrintAgent.Transport/   Cliente HTTP + SSE da API do DiskPrato, pareamento
  PrintAgent.Host/        Worker Service: fila local em arquivo, PrintOrchestrator, AckFlusher, named pipe IPC
  PrintAgent.Tray/        Tray icon + tela de setup (WinForms) — não iniciado
tests/                    um projeto de teste por projeto de src/ acima
installer/                WiX — vazio, não iniciado
```

Regra de dependência: `Host` → `Transport`/`Printing`/`Core` → `Contracts`.

## Build e testes

```powershell
dotnet build      # deve sair com 0 avisos, 0 erros (Directory.Build.props: warnings as errors)
dotnet test       # todos os projetos de teste, sem mocks de filesystem/HTTP — usam diretório temp real e servidor fake
```

## Estado atual (2026-08-08)

Fases 0–4 completas (scaffold, Contracts, EscPosFormatter, transportes de
impressão, cliente HTTP/SSE, Worker Service). Fases 5–8 (network transport
como caminho separado testado, Tray, instalador, endurecimento) não
iniciadas — ver tabela em `docs/plan/PRINT-AGENT-REPO.md §0`.

**Fila local:** arquivo, não banco. `%ProgramData%\DiskPrato\PrintAgent\queue\`
com `pending/`, `printed/`, `failed/` — um `.json` por job, escrita atômica
(temp file + `File.Move`), transição de estado é mover entre pastas. Não usa
SQLite (decisão revertida em 2026-08-08; ver plano §0 e §7.1 para o
raciocínio). `JobStore` (`src/PrintAgent.Host/Storage/JobStore.cs`) é o único
ponto de acesso a essa fila.

**Named pipe** `\\.\pipe\diskprato-printagent` (`NamedPipeIpcServer`) já
implementado desde a Fase 4, adiantado — o Tray (Fase 6) ainda não existe,
mas o protocolo (`get-status`/`pair`/`unpair`/`set-printer`/`test-print`,
JSON por linha) já está pronto pra ele consumir.

## Convenções deste repo

- Commits seguem Conventional Commits (`feat(host): ...`, `fix(contracts): ...`, `docs(plan): ...`).
- `PrintAgent.Core` é o único projeto sem dependência de Win32/HTTP — mantenha assim; é onde ficam os golden tests de ESC/POS.
- Nunca somar/recalcular valores em centavos que vêm do backend (`unitPriceCents`, `subtotalCents`, `totalCents` etc.) — o agente formata, não calcula (plano §1, §5.4).
- Hora do cupom sempre em `order.timezone` (IANA), nunca no fuso da máquina local (plano §5.4).
- Ramificar tratamento de erro HTTP sempre por `ApiError.code`, nunca pelo texto de `message` (plano §6.6).
