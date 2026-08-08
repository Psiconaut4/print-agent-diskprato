using System.Net.Http.Headers;

namespace PrintAgent.Transport;

/// <summary>
/// Adiciona `Authorization: Bearer &lt;deviceToken&gt;` e
/// `X-Print-Agent-Version: &lt;semver&gt;` a toda requisição, para as rotas
/// de dispositivo (§6). O token é obtido via delegate em vez de guardado
/// diretamente aqui: quem instancia isto (o Host) é quem sabe ler o token
/// protegido por DPAPI. Esta camada nunca vê onde/como o token é persistido.
/// </summary>
public sealed class AuthHeaderHandler : DelegatingHandler
{
    private readonly Func<string?> _tokenAccessor;
    private readonly string _agentVersion;

    public AuthHeaderHandler(Func<string?> tokenAccessor, string agentVersion, HttpMessageHandler? innerHandler = null)
        : base(innerHandler ?? new HttpClientHandler())
    {
        _tokenAccessor = tokenAccessor;
        _agentVersion = agentVersion;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _tokenAccessor();
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (!request.Headers.Contains("X-Print-Agent-Version"))
        {
            request.Headers.TryAddWithoutValidation("X-Print-Agent-Version", _agentVersion);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
