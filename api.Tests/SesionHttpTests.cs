using System.Net;
using System.Text.Json;
using GymCoach.Api.Auth;
using GymCoach.Api.Functions;

namespace GymCoach.Api.Tests;

public class SesionHttpTests
{
    const string N = SesionFirmada.NombreCookie;
    const string Foto = "https://lh3.googleusercontent.com/a/foto=s96-c";
    readonly Tokens _tokens = new();
    readonly RelojFalso _reloj = new(DateTimeOffset.UtcNow);
    readonly SesionFirmada _sesion;
    readonly Sesion _fn;

    public SesionHttpTests()
    {
        _sesion = _tokens.Sesion(_reloj);
        _fn = new Sesion(_tokens.Validador(), _sesion, _reloj);
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static (string, string) Xrw => ("X-Requested-With", "gym-coach");

    static RequestFalso Post(string? body, bool conXrw = true, string? cookie = null) =>
        new("POST", "/api/sesion", body: body, cookie: cookie, headers: conXrw ? [Xrw] : null);

    static string Body(string idToken) => JsonSerializer.Serialize(new { idToken });

    static JsonElement Json(ResponseFalsa res) => JsonDocument.Parse(res.Texto()).RootElement;

    static void SinCache(ResponseFalsa res) =>
        Assert.Equal("no-store", Assert.Single(res.Headers.GetValues("Cache-Control")));

    // Valor de la cookie dentro de un Set-Cookie de emisión.
    static string ValorDe(string setCookie)
    {
        Assert.StartsWith(N + "=", setCookie);
        return setCookie[(N.Length + 1)..setCookie.IndexOf(';')];
    }

    [Fact]
    public async Task Post_SinXRequestedWith_403SinCookie()
    {
        var res = (ResponseFalsa)await _fn.Crear(Post(Body(_tokens.Crear()), conXrw: false), Ct);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("csrf", Json(res).GetProperty("error").GetString());
        Assert.Empty(res.SetCookies());
        SinCache(res);
    }

    [Fact]
    public async Task Post_TokenInvalido_401SinCookie()
    {
        foreach (var token in new[] { "basura", Tokens.SinFirma(), _tokens.Crear(expira: DateTime.UtcNow.AddMinutes(-5)), _tokens.Crear(aud: "otro") })
        {
            var res = (ResponseFalsa)await _fn.Crear(Post(Body(token)), Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            Assert.Equal("no_autenticado", Json(res).GetProperty("error").GetString());
            Assert.Empty(res.SetCookies());
            SinCache(res);
        }
    }

    [Fact]
    public async Task Post_TokenViejoOSinIat_401SinCookie()
    {
        foreach (var token in new[] { _tokens.Crear(emitido: DateTime.UtcNow.AddMinutes(-11)), _tokens.Crear(sinIat: true) })
        {
            var res = (ResponseFalsa)await _fn.Crear(Post(Body(token)), Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            Assert.Equal("no_autenticado", Json(res).GetProperty("error").GetString());
            Assert.Empty(res.SetCookies());
        }
    }

    [Fact]
    public async Task Post_TokenDe9Minutos_200() =>
        Assert.Equal(HttpStatusCode.OK,
            ((ResponseFalsa)await _fn.Crear(Post(Body(_tokens.Crear(emitido: DateTime.UtcNow.AddMinutes(-9)))), Ct)).StatusCode);

    [Fact]
    public async Task Post_PeorCaso_NuncaDa500()
    {
        var foto = "https://lh3.googleusercontent.com/" + new string('a', 512 - "https://lh3.googleusercontent.com/".Length);
        var token = _tokens.Crear(sub: new string('9', 255), nombre: string.Concat(Enumerable.Repeat("\U0001F4AA", 60)), foto: foto);
        var res = (ResponseFalsa)await _fn.Crear(Post(Body(token)), Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var p = _sesion.Verificar(ValorDe(Assert.Single(res.SetCookies())));
        Assert.NotNull(p);
        Assert.Equal(foto, p.Foto);
        Assert.Equal(Json(res).GetProperty("nombre").GetString(), p.Nombre);
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("""{"idToken":""}""")]
    [InlineData("""{"idToken":123}""")]
    [InlineData("""{"token":"abc"}""")]
    public async Task Post_BodyInvalido_400(string body)
    {
        var res = (ResponseFalsa)await _fn.Crear(Post(body), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var json = Json(res);
        Assert.Equal("validacion", json.GetProperty("error").GetString());
        Assert.Equal("idToken", json.GetProperty("detalles")[0].GetProperty("campo").GetString());
        Assert.Empty(res.SetCookies());
        SinCache(res);
    }

    [Fact]
    public async Task Post_BodyMayorA8KB_400()
    {
        var res = (ResponseFalsa)await _fn.Crear(Post(Body(new string('a', 9000))), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("idToken", Json(res).GetProperty("detalles")[0].GetProperty("campo").GetString());
    }

    [Fact]
    public async Task Post_Valido_200ConCookieVerificable()
    {
        var token = _tokens.Crear(sub: "abc-42", nombre: "Diego", foto: Foto);
        // Una cookie previa (aunque sea basura) no estorba: el login la sobrescribe.
        var res = (ResponseFalsa)await _fn.Crear(Post(Body(token), cookie: $"{N}=basura"), Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = Json(res);
        Assert.Equal("abc-42", json.GetProperty("sub").GetString());
        Assert.Equal("Diego", json.GetProperty("nombre").GetString());
        Assert.Equal(Foto, json.GetProperty("foto").GetString());
        SinCache(res);

        var setCookie = Assert.Single(res.SetCookies());
        var valor = ValorDe(setCookie);
        Assert.Equal($"{N}={valor}; Max-Age=7776000; Path=/; Secure; HttpOnly; SameSite=Lax", setCookie);
        var p = _sesion.Verificar(valor);
        Assert.NotNull(p);
        Assert.Equal("abc-42", p.Sub);
        Assert.Equal("Diego", p.Nombre);
        Assert.Equal(Foto, p.Foto);
        Assert.DoesNotContain(token, res.Texto());
    }

    [Fact]
    public async Task Post_SinNombreNiFoto_NullExplicitos()
    {
        var res = (ResponseFalsa)await _fn.Crear(Post(Body(_tokens.Crear(sub: "abc"))), Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = Json(res);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("nombre").ValueKind);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("foto").ValueKind);
    }

    [Fact]
    public async Task Post_NombreConControlesYFotoAjena_Saneados()
    {
        var token = _tokens.Crear(sub: "abc", nombre: "  Die\u0000go\u001b  ", foto: "https://evil.example.com/x.png");
        var res = (ResponseFalsa)await _fn.Crear(Post(Body(token)), Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = Json(res);
        Assert.Equal("Diego", json.GetProperty("nombre").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("foto").ValueKind);
        var p = _sesion.Verificar(ValorDe(Assert.Single(res.SetCookies())))!;
        Assert.Equal("Diego", p.Nombre);
        Assert.Null(p.Foto);
    }

    [Fact]
    public async Task Get_SinCookie_401SinSesion()
    {
        // Un Bearer válido no cuenta: GET /api/sesion es solo cookie.
        var res = (ResponseFalsa)await _fn.Leer(new RequestFalso("GET", "/api/sesion", customAuthorization: "Bearer " + _tokens.Crear()));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("sin_sesion", Json(res).GetProperty("error").GetString());
        Assert.Empty(res.SetCookies());
        SinCache(res);
    }

    [Theory]
    [InlineData("basura")]
    [InlineData("v1.abc.def")]
    [InlineData("")]
    public async Task Get_CookieBasura_401YBorra(string valor)
    {
        var res = (ResponseFalsa)await _fn.Leer(new RequestFalso("GET", "/api/sesion", cookie: $"{N}={valor}"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("sesion_invalida", Json(res).GetProperty("error").GetString());
        Assert.Equal($"{N}=; Max-Age=0; Path=/; Secure; HttpOnly; SameSite=Lax", Assert.Single(res.SetCookies()));
        SinCache(res);
    }

    [Fact]
    public async Task Get_CookieDuplicada_401YBorra()
    {
        var v = _sesion.Emitir("abc", null, null);
        var res = (ResponseFalsa)await _fn.Leer(new RequestFalso("GET", "/api/sesion", cookie: $"{N}={v}; {N}={v}"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("sesion_invalida", Json(res).GetProperty("error").GetString());
        Assert.Contains("Max-Age=0", Assert.Single(res.SetCookies()));
    }

    [Fact]
    public async Task Get_RecienEmitida_200SinRenovar()
    {
        var v = _sesion.Emitir("abc", "Diego", Foto);
        var res = (ResponseFalsa)await _fn.Leer(new RequestFalso("GET", "/api/sesion", cookie: $"{N}={v}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var json = Json(res);
        Assert.Equal("abc", json.GetProperty("sub").GetString());
        Assert.Equal("Diego", json.GetProperty("nombre").GetString());
        Assert.Equal(Foto, json.GetProperty("foto").GetString());
        Assert.Empty(res.SetCookies());
        SinCache(res);
    }

    [Fact]
    public async Task Get_A46Dias_RenuevaPor90Dias()
    {
        var v = _sesion.Emitir("abc", "Diego", null);
        var iat = _sesion.Verificar(v)!.Iat;
        _reloj.Avanzar(TimeSpan.FromDays(46));
        var res = (ResponseFalsa)await _fn.Leer(new RequestFalso("GET", "/api/sesion", cookie: $"{N}={v}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var nueva = _sesion.Verificar(ValorDe(Assert.Single(res.SetCookies())));
        Assert.NotNull(nueva);
        Assert.Equal("abc", nueva.Sub);
        Assert.Equal("Diego", nueva.Nombre);
        // La renovación conserva el login original: el tope de 365 días no se corre.
        Assert.Equal(iat, nueva.Iat);
        Assert.InRange(nueva.Exp - _reloj.Ahora.ToUnixTimeSeconds(), (long)SesionFirmada.Vida.TotalSeconds - 5, (long)SesionFirmada.Vida.TotalSeconds);
    }

    [Fact]
    public async Task Get_ConSecretoAnterior_ReemiteConElActual()
    {
        var vieja = new SesionFirmada(_tokens.SecretoSesionAnterior, null, _reloj).Emitir("abc", null, null);
        var rotada = new SesionFirmada(_tokens.SecretoSesion, _tokens.SecretoSesionAnterior, _reloj);
        var res = (ResponseFalsa)await new Sesion(_tokens.Validador(), rotada, _reloj).Leer(
            new RequestFalso("GET", "/api/sesion", cookie: $"{N}={vieja}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var valor = ValorDe(Assert.Single(res.SetCookies()));
        // Firmada con la actual: la valida un verificador que ya no conoce la anterior.
        var p = _sesion.Verificar(valor);
        Assert.NotNull(p);
        Assert.False(p.ConAnterior);
    }

    [Fact]
    public async Task Delete_SinXRequestedWith_403()
    {
        var res = (ResponseFalsa)await _fn.Borrar(new RequestFalso("DELETE", "/api/sesion"));
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Equal("csrf", Json(res).GetProperty("error").GetString());
        Assert.Empty(res.SetCookies());
        SinCache(res);
    }

    [Fact]
    public async Task Delete_ConHeader_204YBorraAunSinCookie()
    {
        foreach (var cookie in new[] { null, $"{N}={_sesion.Emitir("abc", null, null)}" })
        {
            var res = (ResponseFalsa)await _fn.Borrar(new RequestFalso("DELETE", "/api/sesion", cookie: cookie, headers: [Xrw]));
            Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
            Assert.Equal($"{N}=; Max-Age=0; Path=/; Secure; HttpOnly; SameSite=Lax", Assert.Single(res.SetCookies()));
            SinCache(res);
        }
    }
}
