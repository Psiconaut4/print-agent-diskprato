using System.ComponentModel;
using System.Runtime.InteropServices;
using PrintAgent.Contracts;

namespace PrintAgent.Printing;

/// <summary>
/// Caminho padrão de impressão no Windows (§4.1 do plano): envia ESC/POS
/// cru por RAW pass-through pela fila de impressão que o cliente já
/// configurou. O spooler do Windows é o árbitro — ele serializa jobs entre
/// aplicações, então outro PDV imprimindo ao mesmo tempo simplesmente vira
/// outro job na fila; os dois saem íntegros. Também dispensa instalar
/// driver: usa a fila existente.
///
/// Trade-off honesto (documentado também no plano, §4.1 e §5.3): por este
/// caminho não é possível ler status em tempo real via `DLE EOT` — o
/// spooler não expõe um canal bidirecional com o dispositivo físico para
/// quem só manda jobs. O que dá para saber vem de <c>GetPrinter</c> nível 2,
/// checando as flags de status que o driver publicou — e isso é
/// best-effort: muitos drivers genéricos simplesmente não populam essas
/// flags. Ver <see cref="QueryStatusAsync"/>.
/// </summary>
public sealed class SpoolerPrinterTransport : IPrinterTransport, IPrinterStatusQuery
{
    private readonly string _printerName;

    public SpoolerPrinterTransport(string printerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        _printerName = printerName;
    }

    public Task<PrinterSendResult> SendAsync(byte[] payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ct.ThrowIfCancellationRequested();

        // As APIs de winspool.drv são síncronas (bloqueantes); roda em
        // thread de pool para não travar quem chamou SendAsync.
        return Task.Run(() => SendCore(payload), CancellationToken.None);
    }

    private PrinterSendResult SendCore(byte[] payload)
    {
        var hPrinter = IntPtr.Zero;
        var docStarted = false;
        var pageStarted = false;
        try
        {
            if (!NativeMethods.OpenPrinter(_printerName, out hPrinter, IntPtr.Zero))
            {
                var win32Error = Marshal.GetLastWin32Error();
                return PrinterSendResult.Fail(
                    PrinterErrorCode.Not_configured,
                    isRetryable: false,
                    $"OpenPrinter('{_printerName}') falhou: {new Win32Exception(win32Error).Message} (0x{win32Error:X}).");
            }

            var docInfo = new NativeMethods.DOCINFOW
            {
                pDocName = "DiskPrato",
                pOutputFile = null,
                // Crítico: sem RAW, o driver reinterpreta os bytes como texto/EMF
                // e o ESC/POS vira lixo impresso.
                pDatatype = "RAW",
            };

            var jobId = NativeMethods.StartDocPrinter(hPrinter, 1, ref docInfo);
            if (jobId == 0)
            {
                var win32Error = Marshal.GetLastWin32Error();
                return PrinterSendResult.Fail(
                    PrinterErrorCode.Transport_error,
                    isRetryable: true,
                    $"StartDocPrinter falhou: {new Win32Exception(win32Error).Message} (0x{win32Error:X}).");
            }
            docStarted = true;

            if (!NativeMethods.StartPagePrinter(hPrinter))
            {
                var win32Error = Marshal.GetLastWin32Error();
                return PrinterSendResult.Fail(
                    PrinterErrorCode.Transport_error,
                    isRetryable: true,
                    $"StartPagePrinter falhou: {new Win32Exception(win32Error).Message} (0x{win32Error:X}).");
            }
            pageStarted = true;

            var pinned = GCHandle.Alloc(payload, GCHandleType.Pinned);
            try
            {
                var ok = NativeMethods.WritePrinter(hPrinter, pinned.AddrOfPinnedObject(), payload.Length, out var written);
                if (!ok || written != payload.Length)
                {
                    var win32Error = Marshal.GetLastWin32Error();
                    return PrinterSendResult.Fail(
                        PrinterErrorCode.Transport_error,
                        isRetryable: true,
                        $"WritePrinter escreveu {written}/{payload.Length} bytes: {new Win32Exception(win32Error).Message} (0x{win32Error:X}).");
                }
            }
            finally
            {
                pinned.Free();
            }

            return PrinterSendResult.Ok();
        }
        finally
        {
            // Sempre em try/finally: vazar handle de impressora trava a
            // fila para todo mundo, inclusive o outro PDV.
            if (pageStarted) NativeMethods.EndPagePrinter(hPrinter);
            if (docStarted) NativeMethods.EndDocPrinter(hPrinter);
            if (hPrinter != IntPtr.Zero) NativeMethods.ClosePrinter(hPrinter);
        }
    }

    /// <summary>
    /// Lê o estado best-effort via <c>GetPrinter</c> nível 2. Depende do
    /// driver popular <c>PRINTER_STATUS_*</c> corretamente — muitos drivers
    /// genéricos não o fazem, então <see cref="PrinterStatus.Unknown"/> é um
    /// resultado esperado e não um bug. Nunca lança.
    /// </summary>
    public Task<PrinterStatus> QueryStatusAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.Run(QueryStatusCore, CancellationToken.None);
    }

    private PrinterStatus QueryStatusCore()
    {
        var hPrinter = IntPtr.Zero;
        var buffer = IntPtr.Zero;
        try
        {
            if (!NativeMethods.OpenPrinter(_printerName, out hPrinter, IntPtr.Zero))
                return PrinterStatus.Unknown;

            NativeMethods.GetPrinter(hPrinter, 2, IntPtr.Zero, 0, out var needed);
            if (needed == 0)
                return PrinterStatus.Unknown;

            buffer = Marshal.AllocHGlobal((int)needed);
            if (!NativeMethods.GetPrinter(hPrinter, 2, buffer, needed, out _))
                return PrinterStatus.Unknown;

            var info = Marshal.PtrToStructure<NativeMethods.PRINTER_INFO_2>(buffer);
            var status = info.Status;

            // Prioridade: condições mais específicas primeiro. O driver pode
            // marcar mais de uma flag ao mesmo tempo.
            if ((status & NativeMethods.PRINTER_STATUS_DOOR_OPEN) != 0) return PrinterStatus.CoverOpen;
            if ((status & NativeMethods.PRINTER_STATUS_PAPER_OUT) != 0) return PrinterStatus.PaperOut;
            if ((status & NativeMethods.PRINTER_STATUS_OFFLINE) != 0) return PrinterStatus.Offline;
            if (status == 0) return PrinterStatus.Ready;

            // Outras flags (busy, printing, warming up, etc.) não mapeiam
            // para um PrinterErrorCode nosso e não indicam problema real.
            return PrinterStatus.Ready;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (hPrinter != IntPtr.Zero) NativeMethods.ClosePrinter(hPrinter);
        }
    }

    /// <summary>
    /// Lista as filas de impressão do Windows disponíveis, para a tela de
    /// setup do Tray escolher uma. Não requer privilégio administrativo.
    /// </summary>
    public static IReadOnlyList<string> EnumPrinterQueues()
    {
        const uint PRINTER_ENUM_LOCAL = 0x00000002;
        const uint PRINTER_ENUM_CONNECTIONS = 0x00000004;
        const uint flags = PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS;
        const uint level = 4;

        NativeMethods.EnumPrinters(flags, null, level, IntPtr.Zero, 0, out var needed, out _);
        if (needed == 0)
            return [];

        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!NativeMethods.EnumPrinters(flags, null, level, buffer, needed, out _, out var returned))
                return [];

            var names = new List<string>((int)returned);
            var entrySize = Marshal.SizeOf<NativeMethods.PRINTER_INFO_4>();
            for (var i = 0; i < returned; i++)
            {
                var entryPtr = buffer + i * entrySize;
                var entry = Marshal.PtrToStructure<NativeMethods.PRINTER_INFO_4>(entryPtr);
                if (!string.IsNullOrEmpty(entry.pPrinterName))
                    names.Add(entry.pPrinterName);
            }

            return names;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static class NativeMethods
    {
        // Flags de PRINTER_INFO_2.Status usadas pelo GetPrinter best-effort acima.
        public const int PRINTER_STATUS_PAPER_OUT = 0x00000010;
        public const int PRINTER_STATUS_OFFLINE = 0x00000080;
        public const int PRINTER_STATUS_DOOR_OPEN = 0x00400000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DOCINFOW
        {
            public string pDocName;
            public string? pOutputFile;
            public string pDatatype;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PRINTER_INFO_2
        {
            public string? pServerName;
            public string? pPrinterName;
            public string? pShareName;
            public string? pPortName;
            public string? pDriverName;
            public string? pComment;
            public string? pLocation;
            public IntPtr pDevMode;
            public string? pSepFile;
            public string? pPrintProcessor;
            public string? pDatatype;
            public string? pParameters;
            public IntPtr pSecurityDescriptor;
            public int Attributes;
            public int Priority;
            public int DefaultPriority;
            public int StartTime;
            public int UntilTime;
            public int Status;
            public int cJobs;
            public int AveragePPM;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct PRINTER_INFO_4
        {
            public string? pPrinterName;
            public string? pServerName;
            public int Attributes;
        }

        [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

        [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "StartDocPrinterW")]
        public static extern int StartDocPrinter(IntPtr hPrinter, int level, ref DOCINFOW pDocInfo);

        [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

        [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool GetPrinter(IntPtr hPrinter, int level, IntPtr pPrinter, uint cbBuf, out uint pcbNeeded);

        [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool EnumPrinters(uint flags, string? name, uint level, IntPtr pPrinterEnum, uint cbBuf, out uint pcbNeeded, out uint pcReturned);
    }
}
