using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using PrintAgent.Host.Config;

namespace PrintAgent.Host.Security;

/// <summary>
/// Persiste o <c>deviceToken</c> (plano §7.2) em
/// <c>%ProgramData%\DiskPrato\PrintAgent\device.dat</c>, protegido com DPAPI
/// em <see cref="DataProtectionScope.LocalMachine"/> — não
/// <see cref="DataProtectionScope.CurrentUser"/>, porque o serviço roda como
/// <c>LocalSystem</c> e o tray roda como o usuário logado: os dois precisam
/// ler o mesmo arquivo, e <c>CurrentUser</c> quebraria essa divisão em
/// silêncio, só na máquina do cliente.
///
/// <c>LocalMachine</c> significa que qualquer usuário local que consiga ler
/// o arquivo consegue decifrá-lo — por isso a ACL do arquivo importa (o
/// instalador restringe a SYSTEM + Administrators, plano §7.2/Fase 7). Este
/// tipo não define ACL nenhuma; isso é responsabilidade do instalador.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceTokenStore
{
    private readonly string _path;

    public DeviceTokenStore(string? path = null)
    {
        _path = path ?? Path.Combine(AgentConfigStore.DefaultDirectory, "device.dat");
    }

    /// <summary>
    /// Null quando não há token (nunca pareado) ou quando o arquivo está
    /// corrompido/protegido por outra máquina — nos dois casos o chamador
    /// deve tratar como "não pareado" e pedir novo pareamento, nunca lançar.
    /// </summary>
    public string? TryLoad()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            var protectedBytes = File.ReadAllBytes(_path);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Save(string token)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var plainBytes = Encoding.UTF8.GetBytes(token);
        var protectedBytes = ProtectedData.Protect(plainBytes, optionalEntropy: null, DataProtectionScope.LocalMachine);
        File.WriteAllBytes(_path, protectedBytes);
    }

    /// <summary>Chamado em <c>device:revoked</c> ou 401 terminal (plano §6.4/§6.6) — nunca reconectar depois.</summary>
    public void Clear()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
