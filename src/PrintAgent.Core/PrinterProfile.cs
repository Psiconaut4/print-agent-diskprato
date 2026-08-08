namespace PrintAgent.Core;

/// <summary>
/// Configuração local do dispositivo de impressão (persistida em
/// <c>agent.json</c>, plano §7.3). Não vem do contrato OpenAPI — é
/// exclusivamente configuração da máquina do balcão.
/// </summary>
/// <param name="PaperWidthMm">Largura do papel em milímetros. Tipicamente 80 ou 58.</param>
/// <param name="CodePage">Code page ANSI usada para codificar o texto (ex.: 850 para CP850).</param>
/// <param name="EscTIndex">
/// Índice enviado em <c>ESC t n</c> para selecionar a code page na impressora.
/// A tabela Epson (mais seguida pelo mercado) usa <c>n=2</c> para CP850 e
/// <c>n=3</c> para CP860 — ver plano §5.2. Configurável porque varia por fabricante.
/// </param>
/// <param name="StripAccents">
/// Quando <c>true</c>, normaliza o texto para <c>FormD</c> e descarta as marcas
/// diacríticas antes de codificar, em vez de depender da code page para acentos.
/// </param>
/// <param name="Copies">Número de vias a imprimir.</param>
public sealed record PrinterProfile(
    int PaperWidthMm,
    int CodePage,
    int EscTIndex,
    bool StripAccents,
    int Copies)
{
    /// <summary>
    /// Perfil padrão: 80mm, CP850, ESC t 2, sem remover acentos, 1 via.
    /// </summary>
    public static PrinterProfile Default { get; } = new(
        PaperWidthMm: 80,
        CodePage: 850,
        EscTIndex: 2,
        StripAccents: false,
        Copies: 1);

    /// <summary>
    /// Colunas úteis da fonte A (12x24) para a largura de papel configurada.
    /// 80mm = 48 colunas, 58mm = 32 colunas (plano §5.1). Qualquer outra largura
    /// cai no default de 48 colunas — mais seguro que estourar a linha numa
    /// impressora não catalogada.
    /// </summary>
    public int Columns => PaperWidthMm switch
    {
        80 => 48,
        58 => 32,
        _ => 48,
    };
}
