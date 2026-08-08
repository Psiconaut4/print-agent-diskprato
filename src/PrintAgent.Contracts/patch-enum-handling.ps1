<#
Passo automatizado da geracao de contratos (chamado pelo target
NSwagPatchEnumHandling em PrintAgent.Contracts.csproj a cada build).

NSwag emite [System.Runtime.Serialization.EnumMember(Value = "x")] para o
nome no wire, mas o JsonStringEnumConverter do System.Text.Json ignora esse
atributo e serializa pelo nome do membro C# (PascalCase) — o que quebraria
toda escrita para a API real (backend espera "cash", nao "Cash"). Este
script troca:

  1. EnumMemberAttribute -> JsonStringEnumMemberNameAttribute, que o
     JsonStringEnumConverter do .NET 9+ realmente honra.
  2. JsonStringEnumConverter -> TolerantEnumConverterFactory, para nao
     lancar em valor de enum desconhecido (contrato, secao "Regras de
     compatibilidade").
#>
param(
    [Parameter(Mandatory = $true)][string]$Path
)

$content = Get-Content -Raw -LiteralPath $Path

$content = $content -replace 'System\.Runtime\.Serialization\.EnumMember\(Value = (@"[^"]*")\)', 'System.Text.Json.Serialization.JsonStringEnumMemberName($1)'
$content = $content -replace 'typeof\(System\.Text\.Json\.Serialization\.JsonStringEnumConverter\)', 'typeof(PrintAgent.Contracts.TolerantEnumConverterFactory)'

Set-Content -LiteralPath $Path -NoNewline -Value $content
