using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace GymCoach.Api.Auth;

/// <summary>
/// Identidad de un ID token de Google válido; Nombre y Foto ya saneados (null si no pasan).
/// Iat (unix s) es null si el token no lo trae.
/// </summary>
public sealed record IdentidadGoogle(string Sub, string? Nombre, string? Foto, long? Iat = null);

/// <summary>
/// Valida el ID token de Google Sign-In que la PWA manda como Bearer y devuelve el userId.
/// Es la única fuente del userId: nunca se toma del body ni del query.
/// </summary>
public sealed class ValidadorGoogle
{
    const string Discovery = "https://accounts.google.com/.well-known/openid-configuration";

    // Google emite ambos formatos de iss.
    static readonly string[] Emisores = ["https://accounts.google.com", "accounts.google.com"];

    readonly IConfigurationManager<OpenIdConnectConfiguration> _config;
    readonly string _audiencia;
    readonly ILogger? _log;
    readonly JsonWebTokenHandler _handler = new();

    /// <param name="config">Fuente de las llaves de firma; en pruebas, una estática con una RSA local.</param>
    public ValidadorGoogle(string audiencia, IConfigurationManager<OpenIdConnectConfiguration> config, ILogger? log = null)
    {
        // Sin audiencia no hay validación posible: fallar al arrancar, no aceptar cualquier token.
        if (string.IsNullOrWhiteSpace(audiencia))
            throw new InvalidOperationException("Falta GOOGLE_CLIENT_ID.");
        _audiencia = audiencia;
        _config = config;
        _log = log;
    }

    /// <summary>Validador real: llaves de Google por OIDC discovery (ConfigurationManager las cachea).</summary>
    public static ValidadorGoogle ParaGoogle(string clientId, ILogger? log = null) =>
        new(clientId, new ConfigurationManager<OpenIdConnectConfiguration>(
            Discovery, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true }), log);

    /// <summary>Devuelve "google:&lt;sub&gt;" o null si el header no trae un token válido.</summary>
    public async Task<string?> UserIdAsync(string? authorization, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(authorization))
        {
            Rechazo("sin_cabecera", LogLevel.Debug);
            return null;
        }
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            Rechazo("formato_invalido");
            return null;
        }

        var token = authorization["Bearer ".Length..].Trim();
        if (token.Length == 0)
        {
            Rechazo("formato_invalido");
            return null;
        }

        return await IdentidadAsync(token, ct) is { } id ? "google:" + id.Sub : null;
    }

    /// <summary>
    /// Valida el ID token crudo (sin "Bearer") y devuelve sub, más name/picture ya saneados; null si no es válido.
    /// </summary>
    public async Task<IdentidadGoogle?> IdentidadAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            Rechazo("formato_invalido");
            return null;
        }

        try
        {
            var resultado = await ValidarAsync(token, ct);
            // Google rota llaves: si el kid no está en cache, refrescar una vez y reintentar.
            if (resultado.Exception is SecurityTokenSignatureKeyNotFoundException)
            {
                _config.RequestRefresh();
                resultado = await ValidarAsync(token, ct);
            }

            if (!resultado.IsValid)
            {
                Rechazo(resultado.Exception?.GetType().Name ?? "token_invalido");
                return null;
            }
            var claims = resultado.ClaimsIdentity;
            var sub = claims.FindFirst("sub")?.Value;
            if (string.IsNullOrWhiteSpace(sub))
            {
                Rechazo("sin_sub");
                return null;
            }
            return new IdentidadGoogle(sub, SesionFirmada.NombreSeguro(claims.FindFirst("name")?.Value),
                SesionFirmada.FotoSegura(claims.FindFirst("picture")?.Value),
                long.TryParse(claims.FindFirst("iat")?.Value, out var iat) ? iat : null);
        }
        catch (Exception ex)
        {
            // Cualquier fallo (incluido no poder bajar las llaves) es 401, nunca acceso.
            Rechazo(ex.GetType().Name);
            return null;
        }
    }

    // Solo el motivo (tipo de excepción o etiqueta): nunca el token, claims ni ex.Message,
    // que en IdentityModel puede traer aud/iss. El log no puede cambiar el resultado.
    void Rechazo(string motivo, LogLevel nivel = LogLevel.Warning)
    {
        try { _log?.Log(nivel, "Token rechazado: {Motivo}", motivo); }
        catch { }
    }

    async Task<TokenValidationResult> ValidarAsync(string token, CancellationToken ct)
    {
        var config = await _config.GetConfigurationAsync(ct);
        return await _handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuers = Emisores,
            ValidAudience = _audiencia,
            IssuerSigningKeys = config.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        });
    }
}
