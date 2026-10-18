using IdentityModel.OidcClient;
using System.Net;

namespace BusinessERP.Desktop.Services;

/// <summary>
/// Provider-neutral OpenID Connect client for a native Windows application.
/// No client secret is supported or stored in the desktop app.
/// </summary>
public sealed class OidcAuthenticationService
{
    private readonly OidcClient? _client;

    public string? ConfigurationError { get; }
    public bool IsConfigured => _client is not null;

    public OidcAuthenticationService(string? authority, string? clientId, string? scope, string? redirectUri)
    {
        authority = authority?.Trim().TrimEnd('/');
        clientId = clientId?.Trim();
        scope = string.IsNullOrWhiteSpace(scope) ? "openid profile email offline_access" : scope.Trim();

        if (string.IsNullOrWhiteSpace(authority) ||
            !Uri.TryCreate(authority, UriKind.Absolute, out var authorityUri) ||
            (authorityUri.Scheme != Uri.UriSchemeHttps &&
             !(authorityUri.Scheme == Uri.UriSchemeHttp && authorityUri.IsLoopback)))
        {
            ConfigurationError = "يجب ضبط Oidc:Authority بعنوان HTTPS لموفر الهوية (يسمح بـ HTTP للمضيف المحلي فقط).";
            return;
        }

        if (string.IsNullOrWhiteSpace(clientId) || clientId.Length > 300)
        {
            ConfigurationError = "يجب ضبط Oidc:ClientId من إعداد تطبيق Native/Public Client في موفر الهوية.";
            return;
        }

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var callback) ||
            callback.Scheme != Uri.UriSchemeHttp ||
            !IPAddress.TryParse(callback.Host, out var callbackAddress) ||
            !IPAddress.IsLoopback(callbackAddress) ||
            !callback.AbsolutePath.EndsWith('/', StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(callback.Query) ||
            !string.IsNullOrEmpty(callback.Fragment))
        {
            ConfigurationError = "يجب أن يكون Oidc:RedirectUri عنوان HTTP محليًا على loopback وينتهي المسار بشرطة مائلة، مثل http://127.0.0.1:7890/callback/.";
            return;
        }

        _client = new OidcClient(new OidcClientOptions
        {
            Authority = authority,
            ClientId = clientId,
            Scope = scope,
            RedirectUri = callback.AbsoluteUri,
            Browser = new LoopbackSystemBrowser(callback)
        });
    }

    public Task<LoginResult> LoginAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null)
            throw new InvalidOperationException(ConfigurationError ?? "OIDC is not configured.");

        return _client.LoginAsync(cancellationToken: cancellationToken);
    }
}
