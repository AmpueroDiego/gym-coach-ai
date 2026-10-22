using System.Net;
using GymCoach.Api.Functions;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker.Http;

namespace GymCoach.Api.Tests;

/// <summary>Handlers del perfil del coach sobre perfiles en memoria (sin Cosmos).</summary>
public class PerfilCoachHttpTests
{
    readonly Tokens _tokens = new();
    readonly RelojFalso _reloj = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    readonly PerfilesFalsos _perfiles = new();
    readonly Limites _limites;
    readonly PerfilCoachHttp _http;

    public PerfilCoachHttpTests()
    {
        _limites = new Limites(_reloj);
        _http = new PerfilCoachHttp(_tokens.Autenticador(), _perfiles.Crear(), _limites);
    }

    string Bearer(string sub = "123") => "Bearer " + _tokens.Crear(sub: sub);

    const string BodyValido = """{"minutosDisponibles":45,"objetivo":"Fuerza","notas":["Hombro derecho"]}""";

    static string Esperar(HttpResponseData res, HttpStatusCode codigo)
    {
        var texto = ((ResponseFalsa)res).Texto();
        Assert.True(codigo == res.StatusCode, $"Esperaba {codigo}, llegó {res.StatusCode}: {texto}");
        return texto;
    }

    Task<HttpResponseData> Get(string? auth) => _http.Leer(new RequestFalso("GET", "/api/coach/perfil", auth));

    Task<HttpResponseData> Put(string? auth, string body, long? contentLength = null) =>
        _http.Guardar(new RequestFalso("PUT", "/api/coach/perfil", auth, body, contentLength));

    [Fact]
    public async Task SinAuth_401_SinTocarPerfiles()
    {
        const string no = """{"error":"no_autenticado"}""";
        Assert.Equal(no, Esperar(await Get(null), HttpStatusCode.Unauthorized));
        Assert.Equal(no, Esperar(await Get("Bearer x"), HttpStatusCode.Unauthorized));
        Assert.Equal(no, Esperar(await Put(null, BodyValido), HttpStatusCode.Unauthorized));
        Assert.Empty(_perfiles.Leidos);
        Assert.Empty(_perfiles.Escritos);
    }

    [Fact]
    public async Task SoloCustomAuthorization_Autenticado() =>
        Esperar(await _http.Leer(new RequestFalso("GET", "/api/coach/perfil", customAuthorization: Bearer())), HttpStatusCode.OK);

    [Fact]
    public async Task Get_SinDoc_PorDefectoConNulls() =>
        Assert.Equal("""{"minutosDisponibles":null,"objetivo":null,"notas":[],"alturaCm":null,"pesoCorporalKg":null,"equipo":null,"experiencia":null,"diasPorSemana":null}""", Esperar(await Get(Bearer()), HttpStatusCode.OK));

    [Fact]
    public async Task Put_GuardaEnElUserIdDelToken_AunqueElBodyTraigaOtro()
    {
        const string body = """{"userId":"google:otro","minutosDisponibles":45,"objetivo":" Fuerza ","notas":["Hombro derecho"]}""";
        Assert.Equal("""{"minutosDisponibles":45,"objetivo":"Fuerza","notas":["Hombro derecho"],"alturaCm":null,"pesoCorporalKg":null,"equipo":null,"experiencia":null,"diasPorSemana":null}""",
            Esperar(await Put(Bearer("a"), body), HttpStatusCode.OK));

        Assert.Equal(["google:a"], _perfiles.Docs.Keys);
        Assert.Equal("""{"minutosDisponibles":45,"objetivo":"Fuerza","notas":["Hombro derecho"],"alturaCm":null,"pesoCorporalKg":null,"equipo":null,"experiencia":null,"diasPorSemana":null}""",
            Esperar(await Get(Bearer("a")), HttpStatusCode.OK));
        Assert.Equal("""{"minutosDisponibles":null,"objetivo":null,"notas":[],"alturaCm":null,"pesoCorporalKg":null,"equipo":null,"experiencia":null,"diasPorSemana":null}""", Esperar(await Get(Bearer("otro")), HttpStatusCode.OK));
    }

    [Fact]
    public async Task Put_ConAlturaYPeso_LosGuardaYElGetLosDevuelve()
    {
        const string body = """{"minutosDisponibles":null,"objetivo":null,"notas":[],"alturaCm":175,"pesoCorporalKg":78.50}""";
        const string esperado = """{"minutosDisponibles":null,"objetivo":null,"notas":[],"alturaCm":175,"pesoCorporalKg":78.5,"equipo":null,"experiencia":null,"diasPorSemana":null}""";
        Assert.Equal(esperado, Esperar(await Put(Bearer(), body), HttpStatusCode.OK));
        Assert.Equal(esperado, Esperar(await Get(Bearer()), HttpStatusCode.OK));

        // Sin las claves (PWA vieja): se conservan, y la respuesta es el perfil guardado, no el del body.
        const string conservado = """{"minutosDisponibles":30,"objetivo":null,"notas":[],"alturaCm":175,"pesoCorporalKg":78.5,"equipo":null,"experiencia":null,"diasPorSemana":null}""";
        Assert.Equal(conservado, Esperar(await Put(Bearer(), """{"minutosDisponibles":30,"notas":[]}"""), HttpStatusCode.OK));
        Assert.Equal(conservado, Esperar(await Get(Bearer()), HttpStatusCode.OK));

        // Con valor la cambia; null explícito la borra (cada una por separado).
        Assert.Equal("""{"minutosDisponibles":null,"objetivo":null,"notas":[],"alturaCm":180,"pesoCorporalKg":null,"equipo":null,"experiencia":null,"diasPorSemana":null}""",
            Esperar(await Put(Bearer(), """{"notas":[],"alturaCm":180,"pesoCorporalKg":null}"""), HttpStatusCode.OK));
        Assert.Equal("""{"minutosDisponibles":null,"objetivo":null,"notas":[],"alturaCm":null,"pesoCorporalKg":null,"equipo":null,"experiencia":null,"diasPorSemana":null}""",
            Esperar(await Put(Bearer(), """{"notas":[],"alturaCm":null}"""), HttpStatusCode.OK));
        Assert.Equal("""{"minutosDisponibles":null,"objetivo":null,"notas":[],"alturaCm":null,"pesoCorporalKg":null,"equipo":null,"experiencia":null,"diasPorSemana":null}""",
            Esperar(await Get(Bearer()), HttpStatusCode.OK));
    }

    [Fact]
    public async Task Put_AlturaOPesoInvalidos_400_SinEscribir()
    {
        var texto = Esperar(await Put(Bearer(), """{"notas":[],"alturaCm":"175","pesoCorporalKg":78.55}"""), HttpStatusCode.BadRequest);
        Assert.Contains("\"campo\":\"alturaCm\"", texto);
        Assert.Contains("\"campo\":\"pesoCorporalKg\"", texto);
        Assert.Empty(_perfiles.Escritos);
    }

    [Fact]
    public async Task Put_Equipo_SinClaveSeConserva_NullLoBorra_RaroDa400()
    {
        Assert.Contains("\"equipo\":\"maquina\"", Esperar(await Put(Bearer(), """{"notas":[],"equipo":"maquina"}"""), HttpStatusCode.OK));
        // PWA vieja sin la clave: no lo borra.
        Assert.Contains("\"equipo\":\"maquina\"", Esperar(await Put(Bearer(), """{"minutosDisponibles":30,"notas":[]}"""), HttpStatusCode.OK));

        var escritos = _perfiles.Escritos.Count;
        Assert.Contains("\"campo\":\"equipo\"", Esperar(await Put(Bearer(), """{"notas":[],"equipo":"Maquina"}"""), HttpStatusCode.BadRequest));
        Assert.Contains("\"campo\":\"equipo\"", Esperar(await Put(Bearer(), """{"notas":[],"equipo":1}"""), HttpStatusCode.BadRequest));
        Assert.Equal(escritos, _perfiles.Escritos.Count);

        Assert.Contains("\"equipo\":null", Esperar(await Put(Bearer(), """{"notas":[],"equipo":null,"experiencia":null,"diasPorSemana":null}"""), HttpStatusCode.OK));
        Assert.Contains("\"equipo\":null", Esperar(await Get(Bearer()), HttpStatusCode.OK));
    }

    [Fact]
    public async Task Put_ExperienciaYDias_LosGuarda_DiasFueraDeRango400()
    {
        var texto = Esperar(await Put(Bearer(), """{"notas":[],"experiencia":"menos6m","diasPorSemana":4}"""), HttpStatusCode.OK);
        Assert.Contains("\"experiencia\":\"menos6m\",\"diasPorSemana\":4", texto);
        Assert.Contains("\"experiencia\":\"menos6m\",\"diasPorSemana\":4", Esperar(await Get(Bearer()), HttpStatusCode.OK));

        var escritos = _perfiles.Escritos.Count;
        Assert.Contains("\"campo\":\"diasPorSemana\"",
            Esperar(await Put(Bearer(), """{"notas":[],"diasPorSemana":8}"""), HttpStatusCode.BadRequest));
        Assert.Equal(escritos, _perfiles.Escritos.Count);
    }

    [Fact]
    public async Task Put_Invalido_400_SinEscribir()
    {
        var texto = Esperar(await Put(Bearer(), """{"minutosDisponibles":5,"notas":["a\nb"]}"""), HttpStatusCode.BadRequest);
        Assert.Contains("\"error\":\"validacion\"", texto);
        Assert.Contains("\"campo\":\"minutosDisponibles\"", texto);
        Assert.Contains("\"campo\":\"notas[0]\"", texto);
        Assert.Empty(_perfiles.Escritos);
    }

    [Fact]
    public async Task Put_BodyMayorA8KB_400()
    {
        var grande = "{\"objetivo\":\"" + new string('a', 9000) + "\"}";
        Assert.Contains("\"campo\":\"body\"", Esperar(await Put(Bearer(), grande), HttpStatusCode.BadRequest));
        Assert.Contains("\"campo\":\"body\"", Esperar(await Put(Bearer(), "{}", contentLength: 9000), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Put_ConflictoDosVeces_503()
    {
        _perfiles.ConflictosPendientes = 2;
        var res = await Put(Bearer(), BodyValido);
        Assert.Equal("""{"error":"no_disponible"}""", Esperar(res, HttpStatusCode.ServiceUnavailable));
        Assert.True(res.Headers.TryGetValues("Retry-After", out _));
    }

    [Fact]
    public async Task Cosmos429_503()
    {
        var saturado = new PerfilesCoach(
            (_, _) => throw new CosmosException("saturado", HttpStatusCode.TooManyRequests, 0, "", 0),
            (_, _, _, _) => throw new CosmosException("saturado", HttpStatusCode.TooManyRequests, 0, "", 0));
        var http = new PerfilCoachHttp(_tokens.Autenticador(), saturado, new Limites(_reloj));
        Esperar(await http.Leer(new RequestFalso("GET", "/api/coach/perfil", Bearer())), HttpStatusCode.ServiceUnavailable);
        Esperar(await http.Guardar(new RequestFalso("PUT", "/api/coach/perfil", Bearer(), BodyValido)), HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Put_PasadoElLimiteDeLecturas_429_SinGastarEscriturasDeSeries()
    {
        var bearer = Bearer();
        // Las inválidas también cuentan: el límite va antes de validar.
        for (var i = 0; i < Limites.LecturasPorMinuto; i++) Esperar(await Put(bearer, "{}"), HttpStatusCode.BadRequest);
        // El cupo de escrituras (series) sigue entero.
        Assert.True(_limites.PermitirEscritura("google:123", out _));

        var res = await Put(bearer, BodyValido);
        Assert.Equal("""{"error":"demasiadas_peticiones"}""", Esperar(res, HttpStatusCode.TooManyRequests));
        Assert.True(res.Headers.TryGetValues("Retry-After", out var ra));
        Assert.Equal("60", ra.Single());
        Assert.Empty(_perfiles.Escritos);
    }

    [Fact]
    public async Task Get_PasadoElLimiteDeLecturas_429()
    {
        var bearer = Bearer();
        for (var i = 0; i < Limites.LecturasPorMinuto; i++) Esperar(await Get(bearer), HttpStatusCode.OK);
        Esperar(await Get(bearer), HttpStatusCode.TooManyRequests);

        _reloj.Avanzar(TimeSpan.FromMinutes(1));
        Esperar(await Get(bearer), HttpStatusCode.OK);
    }
}
