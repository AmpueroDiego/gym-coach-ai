using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using GymCoach.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using IConfigurationManagerOidc = Microsoft.IdentityModel.Protocols.IConfigurationManager<Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration>;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace GymCoach.Api.Tests;

/// <summary>Firma tokens "de Google" con una RSA local y arma un validador que confía en ella.</summary>
public sealed class Tokens
{
    public const string Audiencia = "cliente-prueba.apps.googleusercontent.com";
    readonly RsaSecurityKey _llave = new(RSA.Create(2048)) { KeyId = "kid-prueba" };

    /// <summary>La llave pública en DER: lo que un atacante podría usar como secreto HMAC.</summary>
    public byte[] LlavePublicaDer => _llave.Rsa.ExportSubjectPublicKeyInfo();

    public ValidadorGoogle Validador(ILogger? log = null) => new(Audiencia, Config(_llave), log);

    // Secretos de sesión generados en cada corrida: nunca una constante versionada.
    public string SecretoSesion { get; } = NuevoSecreto();
    public string SecretoSesionAnterior { get; } = NuevoSecreto();

    public static string NuevoSecreto() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    public SesionFirmada Sesion(TimeProvider? reloj = null) => new(SecretoSesion, null, reloj ?? TimeProvider.System);

    public Autenticador Autenticador(SesionFirmada? sesion = null, ILogger<Autenticador>? log = null) =>
        new(Validador(), sesion ?? Sesion(), log);

    public static IConfigurationManagerOidc Config(SecurityKey llave)
    {
        var config = new OpenIdConnectConfiguration();
        config.SigningKeys.Add(llave);
        return new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(config);
    }

    public string Crear(string sub = "123", string iss = "https://accounts.google.com", string aud = Audiencia,
        DateTime? expira = null, SecurityKey? llave = null, string algoritmo = SecurityAlgorithms.RsaSha256, bool sinExp = false,
        string? nombre = null, string? foto = null, DateTime? emitido = null, bool sinIat = false)
    {
        var exp = expira ?? DateTime.UtcNow.AddHours(1);
        var claims = new List<Claim> { new("sub", sub) };
        if (nombre is not null) claims.Add(new("name", nombre));
        if (foto is not null) claims.Add(new("picture", foto));
        // Con tiempos por defecto el handler agregaría iat solo: sin iat hay que apagarlos.
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = !sinExp && !sinIat }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = iss,
            Audience = aud,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = sinExp || sinIat ? null : emitido ?? exp.AddHours(-1),
            NotBefore = sinExp ? null : exp.AddHours(-1),
            Expires = sinExp ? null : exp,
            SigningCredentials = new SigningCredentials(llave ?? _llave, algoritmo),
        });
    }

    /// <summary>Token sin firma (alg "none") con claims que por lo demás serían válidos.</summary>
    public static string SinFirma(string sub = "123")
    {
        static string B64(string json) => Base64UrlEncoder.Encode(json);
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        return B64("""{"alg":"none","typ":"JWT"}""") + "." +
            B64(System.Text.Json.JsonSerializer.Serialize(new { iss = "https://accounts.google.com", aud = Audiencia, sub, exp })) + ".";
    }
}

/// <summary>HttpRequestData mínimo para invocar los handlers sin el host de Functions.</summary>
public sealed class RequestFalso : HttpRequestData
{
    public RequestFalso(string metodo, string url, string? authorization = null, string? body = null, long? contentLength = null,
        string? customAuthorization = null, string? cookie = null, IEnumerable<(string Nombre, string Valor)>? headers = null)
        : base(new ContextoFalso())
    {
        Method = metodo;
        Url = new Uri("http://localhost" + url);
        Body = new MemoryStream(Encoding.UTF8.GetBytes(body ?? ""));
        if (authorization is not null) Headers.Add("Authorization", authorization);
        if (customAuthorization is not null) Headers.Add("X-Custom-Authorization", customAuthorization);
        if (contentLength is not null) Headers.Add("Content-Length", contentLength.Value.ToString());
        if (cookie is not null) Headers.Add("Cookie", cookie);
        foreach (var (nombre, valor) in headers ?? []) Headers.Add(nombre, valor);
    }

    public override Stream Body { get; }
    public override HttpHeadersCollection Headers { get; } = [];
    public override IReadOnlyCollection<IHttpCookie> Cookies { get; } = [];
    public override Uri Url { get; }
    public override IEnumerable<ClaimsIdentity> Identities { get; } = [];
    public override string Method { get; }
    public override HttpResponseData CreateResponse() => new ResponseFalsa(FunctionContext);
}

public sealed class ResponseFalsa(FunctionContext contexto) : HttpResponseData(contexto)
{
    public override System.Net.HttpStatusCode StatusCode { get; set; }
    public override HttpHeadersCollection Headers { get; set; } = [];
    public override Stream Body { get; set; } = new MemoryStream();
    public override HttpCookies Cookies => throw new NotSupportedException();

    public string Texto()
    {
        Body.Position = 0;
        return new StreamReader(Body).ReadToEnd();
    }

    public string[] SetCookies() => Headers.TryGetValues("Set-Cookie", out var v) ? [.. v] : [];
}

public sealed class ContextoFalso : FunctionContext
{
    public override string InvocationId => "prueba";
    public override string FunctionId => "prueba";
    public override TraceContext TraceContext => throw new NotSupportedException();
    public override BindingContext BindingContext => throw new NotSupportedException();
    public override RetryContext RetryContext => throw new NotSupportedException();
    public override IServiceProvider InstanceServices { get; set; } = null!;
    public override FunctionDefinition FunctionDefinition => throw new NotSupportedException();
    public override IDictionary<object, object> Items { get; set; } = new Dictionary<object, object>();
    public override IInvocationFeatures Features => throw new NotSupportedException();
}

/// <summary>Guarda los mensajes ya formateados para revisar qué se loguea.</summary>
public class LoggerFalso : ILogger
{
    public List<(LogLevel Nivel, string Texto)> Mensajes { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter) => Mensajes.Add((logLevel, formatter(state, exception)));
}

/// <summary>El mismo logger falso con categoría, para los handlers que reciben ILogger&lt;T&gt;.</summary>
public sealed class LoggerFalso<T> : LoggerFalso, ILogger<T>;

/// <summary>Reloj controlable para el limitador y la validación de fecha/ts.</summary>
public sealed class RelojFalso(DateTimeOffset inicio) : TimeProvider
{
    public DateTimeOffset Ahora { get; set; } = inicio;
    public override DateTimeOffset GetUtcNow() => Ahora;
    public void Avanzar(TimeSpan t) => Ahora += t;
}
