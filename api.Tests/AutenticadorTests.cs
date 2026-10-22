using GymCoach.Api.Auth;

namespace GymCoach.Api.Tests;

public class AutenticadorTests
{
    const string N = SesionFirmada.NombreCookie;
    readonly Tokens _tokens = new();
    readonly SesionFirmada _sesion;
    readonly Autenticador _auth;

    public AutenticadorTests()
    {
        _sesion = _tokens.Sesion();
        _auth = _tokens.Autenticador(_sesion);
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    string Cookie(string sub = "cookie-1") => _sesion.Emitir(sub, null, null);
    string Bearer(string sub = "header-1") => "Bearer " + _tokens.Crear(sub: sub);

    static (string, string) Xrw => ("X-Requested-With", "gym-coach");

    [Fact]
    public async Task SinCookie_BearerEnCustomAuthorization_UsaElHeader() =>
        Assert.Equal("google:header-1", await _auth.UserIdAsync(new RequestFalso("GET", "/api/ejercicios", customAuthorization: Bearer()), Ct));

    [Fact]
    public async Task SinCookieNiHeader_EsNull() =>
        Assert.Null(await _auth.UserIdAsync(new RequestFalso("GET", "/api/ejercicios"), Ct));

    [Fact]
    public async Task CookieValidaSola() =>
        Assert.Equal("google:cookie-1", await _auth.UserIdAsync(new RequestFalso("GET", "/api/ejercicios", cookie: $"{N}={Cookie()}"), Ct));

    [Fact]
    public async Task CookieInvalida_ConHeaderValido_EsNull() =>
        Assert.Null(await _auth.UserIdAsync(
            new RequestFalso("GET", "/api/ejercicios", cookie: $"{N}=v1.basura.basura", customAuthorization: Bearer()), Ct));

    [Fact]
    public async Task CookieDeOtroSecreto_ConHeaderValido_EsNull()
    {
        var ajena = new SesionFirmada(Tokens.NuevoSecreto(), null, TimeProvider.System).Emitir("x", null, null);
        Assert.Null(await _auth.UserIdAsync(
            new RequestFalso("GET", "/api/ejercicios", cookie: $"{N}={ajena}", authorization: Bearer()), Ct));
    }

    [Fact]
    public async Task DosCookies_EnUnHeader_EsNull() =>
        Assert.Null(await _auth.UserIdAsync(
            new RequestFalso("GET", "/api/ejercicios", cookie: $"{N}={Cookie()}; {N}={Cookie("otra")}", customAuthorization: Bearer()), Ct));

    [Fact]
    public async Task DosCookies_EnDosHeaders_EsNull() =>
        Assert.Null(await _auth.UserIdAsync(new RequestFalso("GET", "/api/ejercicios", customAuthorization: Bearer(),
            headers: [("Cookie", $"{N}={Cookie()}"), ("Cookie", $"{N}={Cookie()}")]), Ct));

    [Fact]
    public async Task CookieEntreOtras_Vale() =>
        Assert.Equal("google:cookie-1", await _auth.UserIdAsync(
            new RequestFalso("GET", "/api/ejercicios", cookie: $"a=1; {N}={Cookie()}; b=2"), Ct));

    [Fact]
    public async Task CookieEnSegundoHeader_Vale() =>
        Assert.Equal("google:cookie-1", await _auth.UserIdAsync(new RequestFalso("GET", "/api/ejercicios",
            headers: [("Cookie", "a=1"), ("Cookie", $"{N}={Cookie()}")]), Ct));

    [Fact]
    public async Task NombreParecido_SeIgnoraYCaeAlHeader() =>
        Assert.Equal("google:header-1", await _auth.UserIdAsync(
            new RequestFalso("GET", "/api/ejercicios", cookie: $"x{N}={Cookie()}; {N}x=basura", customAuthorization: Bearer()), Ct));

    [Fact]
    public void LeerCookie_Estados()
    {
        Assert.Equal((EstadoCookie.Ninguna, null), Autenticador.LeerCookie(new RequestFalso("GET", "/"), N));
        Assert.Equal((EstadoCookie.Ninguna, null), Autenticador.LeerCookie(new RequestFalso("GET", "/", cookie: "a=1"), N));
        Assert.Equal((EstadoCookie.Presente, "v"), Autenticador.LeerCookie(new RequestFalso("GET", "/", cookie: $"a=1;{N}=v"), N));
        Assert.Equal((EstadoCookie.Presente, ""), Autenticador.LeerCookie(new RequestFalso("GET", "/", cookie: $"{N}="), N));
        Assert.Equal((EstadoCookie.Duplicada, null), Autenticador.LeerCookie(new RequestFalso("GET", "/", cookie: $"{N}=a; {N}=a"), N));
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("post")]
    [InlineData("put")]
    [InlineData("patch")]
    [InlineData("delete")]
    public async Task EscrituraConCookie_SinXRequestedWith_EsNull(string metodo)
    {
        Assert.Null(await _auth.UserIdAsync(new RequestFalso(metodo, "/api/series", cookie: $"{N}={Cookie()}"), Ct));
        Assert.Null(await _auth.UserIdAsync(new RequestFalso(metodo, "/api/series", cookie: $"{N}={Cookie()}",
            headers: [("X-Requested-With", "  ")]), Ct));
        Assert.Equal("google:cookie-1", await _auth.UserIdAsync(
            new RequestFalso(metodo, "/api/series", cookie: $"{N}={Cookie()}", headers: [Xrw]), Ct));
    }

    [Fact]
    public async Task GetConCookie_SinXRequestedWith_Vale() =>
        Assert.Equal("google:cookie-1", await _auth.UserIdAsync(new RequestFalso("GET", "/api/series", cookie: $"{N}={Cookie()}"), Ct));

    [Fact]
    public async Task EscrituraConBearer_SinXRequestedWith_SigueIgual() =>
        Assert.Equal("google:header-1", await _auth.UserIdAsync(new RequestFalso("PUT", "/api/series", customAuthorization: Bearer()), Ct));

    [Fact]
    public async Task XGcSubDistinto_EsNull() =>
        Assert.Null(await _auth.UserIdAsync(new RequestFalso("GET", "/api/series", cookie: $"{N}={Cookie()}",
            headers: [("X-Gc-Sub", "otra-cuenta")]), Ct));

    [Fact]
    public async Task XGcSubIgual_Vale() =>
        Assert.Equal("google:cookie-1", await _auth.UserIdAsync(new RequestFalso("PUT", "/api/series", cookie: $"{N}={Cookie()}",
            headers: [Xrw, ("X-Gc-Sub", "cookie-1")]), Ct));

    [Fact]
    public async Task Rechazos_NuncaLogueanCookieNiSub()
    {
        var log = new LoggerFalso<Autenticador>();
        var auth = _tokens.Autenticador(_sesion, log);
        var buena = Cookie("sub-secreto");
        var ajena = new SesionFirmada(Tokens.NuevoSecreto(), null, TimeProvider.System).Emitir("sub-secreto", null, null);
        RequestFalso[] reqs =
        [
            new("GET", "/", cookie: $"{N}={ajena}"),
            new("GET", "/", cookie: $"{N}=v1.basura-secreta.x"),
            new("GET", "/", cookie: $"{N}={buena}; {N}={buena}"),
            new("PUT", "/", cookie: $"{N}={buena}"),
            new("GET", "/", cookie: $"{N}={buena}", headers: [("X-Gc-Sub", "sub-ajeno")]),
        ];
        foreach (var r in reqs) Assert.Null(await auth.UserIdAsync(r, Ct));

        Assert.Equal(reqs.Length, log.Mensajes.Count);
        foreach (var etiqueta in new[] { "cookie_firma", "cookie_formato", "cookie_duplicada", "csrf", "otra_cuenta" })
            Assert.Contains(log.Mensajes, m => m.Texto.Contains(etiqueta));
        foreach (var (_, texto) in log.Mensajes)
        {
            Assert.DoesNotContain("sub-secreto", texto);
            Assert.DoesNotContain("sub-ajeno", texto);
            Assert.DoesNotContain("basura-secreta", texto);
            Assert.DoesNotContain(buena, texto);
            Assert.DoesNotContain(ajena, texto);
        }
    }
}
