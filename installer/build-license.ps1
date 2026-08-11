<#
.SYNOPSIS
    Gera installer/License.rtf a partir de installer/License.txt e verifica que
    o resultado realmente renderiza.

.DESCRIPTION
    A tela de termos de uso do MSI (WixUILicenseRtf) e desenhada por um controle
    RichEdit, que aborta em silencio no primeiro escape RTF malformado e mostra
    o resto da licenca em branco -- foi exatamente o que aconteceu ate
    2026-08-11, com "\'e" no lugar de "\'e9": so o titulo aparecia, e nada no
    build acusou. Dai as duas metades deste script:

      1. Escrever o RTF a partir de texto puro UTF-8, escapando por tabela em
         vez de a mao (o erro original foi de digitacao, nao de conteudo).
      2. Carregar o RTF gerado num RichTextBox de verdade e comparar o texto
         renderizado com o texto de origem. Divergiu, o build falha.

    Mesma divisao do resources/build-icon.ps1: License.txt e a fonte editavel,
    License.rtf e artefato gerado (versionado, para o .msi nao depender de
    rodar o script). Chamado pelo PrintAgent.Installer.wixproj a cada build.

    Convencoes do License.txt:
      "# titulo"     linha de titulo (negrito)
      "**trecho**"   negrito no meio do paragrafo
      linha em branco separa paragrafos; dentro de um paragrafo, as quebras de
      linha do arquivo sao so formatacao e viram espaco.
#>
[CmdletBinding()]
param(
    [string]$SourcePath,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

# Resolvido aqui, nao no default do param(): chamado com "powershell.exe -File"
# (que e como o wixproj chama), $PSScriptRoot ainda esta vazio na hora de
# avaliar os defaults, e o Join-Path falha antes da primeira linha do corpo.
if (-not $SourcePath) { $SourcePath = Join-Path $PSScriptRoot 'License.txt' }
if (-not $OutputPath) { $OutputPath = Join-Path $PSScriptRoot 'License.rtf' }
Add-Type -AssemblyName System.Windows.Forms

# cp1252 e o codepage declarado no cabecalho do RTF (\ansicpg1252). No .NET
# Core (pwsh) ele so existe depois de registrar o provider; no .NET Framework
# (Windows PowerShell 5.1) o tipo do provider nem existe, e o codepage ja esta
# disponivel -- por isso o try, que cobre os dois hosts.
try {
    [System.Text.Encoding]::RegisterProvider([System.Text.CodePagesEncodingProvider]::Instance)
}
catch [System.Management.Automation.RuntimeException] {
}
$cp1252 = [System.Text.Encoding]::GetEncoding(1252)

function ConvertTo-RtfText {
    param([string]$Text)

    # if/elseif, nao switch: dentro de um switch o "continue" do PowerShell sai
    # do switch e nao do foreach, e cada caractere escapado sairia escapado e
    # repetido (o "\" de %ProgramData%\DiskPrato virava "\\\", e o RichEdit lia
    # "\DiskPrato" como comando desconhecido e sumia com o resto da linha).
    $sb = [System.Text.StringBuilder]::new()
    foreach ($ch in $Text.ToCharArray()) {
        $code = [int]$ch

        if ($ch -eq '\' -or $ch -eq '{' -or $ch -eq '}') {
            [void]$sb.Append('\').Append($ch)
        }
        elseif ($code -lt 128) {
            [void]$sb.Append($ch)
        }
        else {
            # Fora do ASCII: se cp1252 representa o caractere, sai como \'xx
            # (sempre dois digitos hex -- a origem do bug original); senao, como
            # \uNNNN? , que o RichEdit entende e cujo "?" e o fallback de
            # leitores antigos.
            $bytes = $cp1252.GetBytes([string]$ch)
            if ($bytes.Length -eq 1 -and $bytes[0] -ne 0x3F) {
                [void]$sb.AppendFormat("\'{0:x2}", $bytes[0])
            }
            else {
                [void]$sb.AppendFormat('\u{0}?', [int][short]$code)
            }
        }
    }

    return $sb.ToString()
}

function ConvertTo-RtfParagraph {
    param([string]$Paragraph)

    # **negrito** vira \b ... \b0 ; o resto do paragrafo passa pelo escape.
    $sb = [System.Text.StringBuilder]::new()
    $bold = $false
    foreach ($piece in [regex]::Split($Paragraph, '\*\*')) {
        if ($bold) {
            [void]$sb.Append('\b ').Append((ConvertTo-RtfText $piece)).Append('\b0 ')
        }
        else {
            [void]$sb.Append((ConvertTo-RtfText $piece))
        }
        $bold = -not $bold
    }

    return $sb.ToString()
}

if (-not (Test-Path -LiteralPath $SourcePath)) {
    throw "Fonte da licenca nao encontrada: $SourcePath"
}

$source = Get-Content -LiteralPath $SourcePath -Raw -Encoding UTF8
$blocks = [regex]::Split(($source -replace "`r`n", "`n").Trim(), "`n{2,}")

$body = [System.Text.StringBuilder]::new()
$expected = [System.Text.StringBuilder]::new()

foreach ($block in $blocks) {
    # Quebras de linha internas sao formatacao do .txt, nao do documento.
    $paragraph = ($block -replace "`n", ' ').Trim()
    if ($paragraph.Length -eq 0) { continue }

    if ($paragraph.StartsWith('# ')) {
        $paragraph = $paragraph.Substring(2).Trim()
        [void]$body.AppendLine("\b " + (ConvertTo-RtfText $paragraph) + "\b0\par")
    }
    else {
        [void]$body.AppendLine((ConvertTo-RtfParagraph $paragraph) + '\par')
    }

    [void]$body.AppendLine('\par')
    [void]$expected.AppendLine(($paragraph -replace '\*\*', ''))
    [void]$expected.AppendLine()
}

$rtf = @"
{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss\fcharset0 Segoe UI;}}
\viewkind4\uc1\pard\f0\fs18
$($body.ToString().TrimEnd())
}
"@

# RTF e ASCII puro por construcao (tudo fora do ASCII virou escape acima).
[System.IO.File]::WriteAllText($OutputPath, $rtf, [System.Text.Encoding]::ASCII)

# Verificacao: o RichEdit e quem decide o que aparece no instalador, entao a
# unica checagem que vale e carregar o arquivo num RichEdit de verdade. STA
# explicito porque o controle exige apartment single-threaded e o host que
# chama este script nem sempre roda em STA. Nao da para so criar uma Thread STA
# aqui: um scriptblock convertido em delegate roda sem runspace na thread nova e
# derruba o processo inteiro (exit 2, sem mensagem). O jeito estavel e relancar
# o script num powershell.exe -STA.
if ([System.Threading.Thread]::CurrentThread.GetApartmentState() -ne [System.Threading.ApartmentState]::STA) {
    $child = Start-Process -FilePath 'powershell.exe' -ArgumentList @(
        '-NoProfile', '-STA', '-ExecutionPolicy', 'Bypass',
        '-File', $PSCommandPath, '-SourcePath', $SourcePath, '-OutputPath', $OutputPath
    ) -NoNewWindow -Wait -PassThru
    exit $child.ExitCode
}

try {
    $box = [System.Windows.Forms.RichTextBox]::new()
    $box.Rtf = [System.IO.File]::ReadAllText($OutputPath)
    $rendered = $box.Text
    $box.Dispose()
}
catch {
    throw "License.rtf nao pode ser lido por um RichEdit (a tela de termos do .msi ficaria em branco): $($_.Exception.Message)"
}

# Normaliza espaco em branco dos dois lados: o RichEdit devolve \r\n e come a
# linha em branco final, diferencas que nao dizem nada sobre o conteudo.
function Get-Normalized {
    param([string]$Text)
    return (($Text -replace '\s+', ' ')).Trim()
}

$renderedText = Get-Normalized $rendered
$expectedText = Get-Normalized $expected.ToString()

if ($renderedText -ne $expectedText) {
    $preview = if ($renderedText.Length -gt 120) { $renderedText.Substring(0, 120) + '...' } else { $renderedText }
    throw @"
O texto renderizado de License.rtf nao confere com License.txt -- a tela de
termos do .msi mostraria conteudo truncado ou em branco.
  esperado: $($expectedText.Length) caracteres
  obtido:   $($renderedText.Length) caracteres
  inicio do que o RichEdit leu: $preview
"@
}

Write-Host "License.rtf gerado e verificado ($($renderedText.Length) caracteres renderizados)."
