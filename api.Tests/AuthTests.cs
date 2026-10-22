using System.Security.Cryptography;
using GymCoach.Api.Auth;
using Microsoft.IdentityModel.Tokens;

namespace GymCoach.Api.Tests;

public class AuthTests
{
    readonly Tokens _tokens = new();
    readonly ValidadorGoogle _validador;

    public AuthTests() => _validador = _tokens.Validador();

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TokenValido_DevuelveUserIdConPrefijoGoogle() =>
        Assert.Equal("google:abc-42", await _validador.UserIdAsync("Bearer " + _tokens.Crear(sub: "abc-42"), Ct));

    [Fact]
    public async Task IssSinEsquema_TambienEsValido() =>
        Assert.Equal("google:123", await _validador.UserIdAsync("Bearer " + _tokens.Crear(iss: "accounts.google.com"), Ct));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Basic dXN1YXJpbzpjbGF2ZQ==")]
    [InlineData("Bearer ")]
    [InlineData("Bearer esto.no.es-un-jwt")]
    [InlineData("Bearer abc")]
    public async Task HeaderAusenteOMalformado_EsNull(string? header) =>
        Assert.Null(await _validador.UserIdAsync(header, Ct));

    [Fact]
    public async Task FirmaDeOtraLlave_EsNull()
    {
        var otra = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "kid-prueba" };
        Assert.Null(await _validador.UserIdAsync("Bearer " + _tokens.Crear(llave: otra), Ct));
    }

    [Fact]
    public async Task AudienciaIncorrecta_EsNull() =>
        Assert.Null(await _validador.UserIdAsync("Bearer " + _tokens.Crear(aud: "otro-cliente"), Ct));

    [Fact]
    public async Task EmisorIncorrecto_EsNull() =>
        Assert.Null(await _validador.UserIdAsync("Bearer " + _tokens.Crear(iss: "https://evil.example.com"), Ct));

    [Fact]
    public async Task Expirado_EsNull() =>
        Assert.Null(await _validador.UserIdAsync("Bearer " + _tokens.Crear(expira: DateTime.UtcNow.AddMinutes(-5)), Ct));

    [Fact]
    public void SinAudiencia_NoArranca() =>
        Assert.Throws<InvalidOperationException>(() => new ValidadorGoogle("", Tokens.Config(new RsaSecurityKey(RSA.Create(2048)))));

    [Fact]
    public async Task AlgNone_EsNull() =>
        Assert.Null(await _validador.UserIdAsync("Bearer " + Tokens.SinFirma(), Ct));

    // Confusión de algoritmo: firmar HS256 usando la llave pública RSA como secreto.
    [Fact]
    public async Task Hs256ConLlavePublica_EsNull() =>
        Assert.Null(await _validador.UserIdAsync("Bearer " + _tokens.Crear(
            llave: new SymmetricSecurityKey(_tokens.LlavePublicaDer) { KeyId = "kid-prueba" },
            algoritmo: SecurityAlgorithms.HmacSha256), Ct));

    [Fact]
    public async Task SinExp_EsNull() =>
        Assert.Null(await _validador.UserIdAsync("Bearer " + _tokens.Crear(sinExp: true), Ct));

    [Fact]
    public async Task Rechazo_LogueaMotivoSinTokenNiClaims()
    {
        var log = new LoggerFalso();
        var validador = _tokens.Validador(log);
        var otra = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "kid-prueba" };
        var tokens = new[]
        {
            _tokens.Crear(sub: "sub-secreto", llave: otra),
            _tokens.Crear(sub: "sub-secreto", expira: DateTime.UtcNow.AddMinutes(-5)),
            _tokens.Crear(sub: "sub-secreto", aud: "aud-secreta"),
        };
        foreach (var t in tokens) Assert.Null(await validador.UserIdAsync("Bearer " + t, Ct));
        Assert.Null(await validador.UserIdAsync("Basic abc", Ct));

        Assert.Equal(4, log.Mensajes.Count(m => m.Nivel == Microsoft.Extensions.Logging.LogLevel.Warning));
        Assert.Contains(log.Mensajes, m => m.Texto.Contains("formato_invalido"));
        foreach (var (_, texto) in log.Mensajes)
        {
            Assert.DoesNotContain("sub-secreto", texto);
            Assert.DoesNotContain("aud-secreta", texto);
            Assert.DoesNotContain(Tokens.Audiencia, texto);
            foreach (var t in tokens) Assert.DoesNotContain(t, texto);
        }
    }

    [Fact]
    public async Task SubEnBlanco_EsNull() =>
        Assert.Null(await _validador.UserIdAsync("Bearer " + _tokens.Crear(sub: "  "), Ct));
}
