# Agente de Impressão DiskPrato

Agente Windows que imprime as comandas do DiskPrato na impressora térmica do
balcão. Roda como serviço, convive com o PDV já instalado e continua
funcionando se a internet cair — os pedidos ficam numa fila local e saem
quando a conexão volta.

## Instalar

**[⬇ Baixar a versão mais recente](https://github.com/Psiconaut4/print-agent-diskprato/releases/latest/download/DiskPratoPrintAgent.msi)**

Esse link sempre aponta para a última versão publicada. Para uma versão
específica, veja a [página de releases](https://github.com/Psiconaut4/print-agent-diskprato/releases).

1. Baixe e execute o `.msi`. É preciso ser administrador da máquina.
2. O Windows vai avisar **"Editor desconhecido"** — o instalador ainda não é
   assinado digitalmente. Clique em *Mais informações* → *Executar assim
   mesmo*.
3. Ao terminar, deixe marcada a opção de abrir a configuração. O ícone do
   agente fica ao lado do relógio, na bandeja do sistema.
4. Na tela de configuração: informe o **código de pareamento** que aparece no
   painel do lojista e escolha a impressora. Dá para imprimir um cupom de
   teste ali mesmo.

Requisitos: Windows 10 ou 11, 64 bits. Não é preciso instalar .NET — o
pacote já vem com tudo.

O serviço sobe sozinho no boot e o ícone da bandeja aparece no login de
qualquer usuário da máquina, não só de quem instalou.

## Atualizar

Baixe o `.msi` novo e instale por cima. O pareamento e a impressora
configurada são preservados.

## Quando algo não imprime

No menu do ícone da bandeja, **"Exportar diagnóstico..."** gera um `.zip` na
Área de Trabalho com os logs, a configuração e o estado da fila. É o que o
suporte precisa. O pacote **não** inclui o token do dispositivo.
