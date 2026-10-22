using System.Net;
using GymCoach.Api.Auth;
using GymCoach.Api.Functions;
using Microsoft.Azure.Functions.Worker.Http;

namespace GymCoach.Api.Tests;

/// <summary>
/// Respuestas de los handlers que salen antes de tocar Cosmos (por eso el Container es null):
/// si alguno llegara a Cosmos, la prueba revienta con NullReferenceException.
/// </summary>
public class HandlersTests
{
    readonly Tokens _tokens = new();
    readonly RelojFalso _reloj = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    readonly Ejercicios _ejercicios;
    readonly Series _series;
    readonly Records _records;
    readonly Rutinas _rutinas;
    readonly Limites _limites;

    public HandlersTests()
    {
        var limites = _limites = new Limites(_reloj);
        _ejercicios = new Ejercicios(_tokens.Autenticador(), null!, limites);
        _series = new Series(_tokens.Autenticador(), null!, limites, _reloj);
        _records = new Records(_tokens.Autenticador(), null!, limites, _reloj);
        _rutinas = new Rutinas(_tokens.Autenticador(), null!, limites, _reloj);
    }

    string Bearer => "Bearer " + _tokens.Crear();
    static readonly string Id = Guid.NewGuid().ToString();

    static string Esperar(HttpResponseData res, HttpStatusCode codigo)
    {
        var texto = ((ResponseFalsa)res).Texto();
        Assert.True(codigo == res.StatusCode, $"Esperaba {codigo}, llegó {res.StatusCode}: {texto}");
        return texto;
    }

    [Fact]
    public async Task SinAuth_Todos401()
    {
        static void Es401(HttpResponseData res) =>
            Assert.Equal("""{"error":"no_autenticado"}""", Esperar(res, HttpStatusCode.Unauthorized));

        Es401(await _ejercicios.Listar(new RequestFalso("GET", "/api/ejercicios")));
        Es401(await _ejercicios.Historial(new RequestFalso("GET", "/api/ejercicios/sentadilla/historial", "Bearer x"), "sentadilla"));
        Es401(await _series.Guardar(new RequestFalso("PUT", "/api/series/" + Id, body: "{}"), Id));
        Es401(await _series.Borrar(new RequestFalso("DELETE", "/api/series/" + Id), Id));
        Es401(await _series.Listar(new RequestFalso("GET", "/api/series?desde=2026-01-01&hasta=2026-01-02")));
        Es401(await _records.Listar(new RequestFalso("GET", "/api/records")));
    }

    // En Azure, SWA pisa Authorization: el token llega en X-Custom-Authorization.
    [Fact]
    public async Task SoloCustomAuthorization_Autenticado() =>
        Esperar(await _ejercicios.Listar(new RequestFalso("GET", "/api/ejercicios", customAuthorization: Bearer)), HttpStatusCode.OK);

    [Fact]
    public async Task AmbasCabeceras_GanaCustom() =>
        Esperar(await _ejercicios.Listar(new RequestFalso("GET", "/api/ejercicios", "Bearer x", customAuthorization: Bearer)),
            HttpStatusCode.OK);

    // Si la custom trae valor, es la única que cuenta: no hay segunda oportunidad con Authorization.
    [Fact]
    public async Task CustomInvalida_NoCaeAAuthorization() =>
        Esperar(await _ejercicios.Listar(new RequestFalso("GET", "/api/ejercicios", Bearer, customAuthorization: "Bearer x")),
            HttpStatusCode.Unauthorized);

    [Fact]
    public async Task SoloAuthorization_SigueFuncionando() =>
        Esperar(await _ejercicios.Listar(new RequestFalso("GET", "/api/ejercicios", Bearer)), HttpStatusCode.OK);

    [Fact]
    public async Task CustomEnBlanco_UsaAuthorization() =>
        Esperar(await _ejercicios.Listar(new RequestFalso("GET", "/api/ejercicios", Bearer, customAuthorization: "   ")), HttpStatusCode.OK);

    // Cookie de sesión: el detalle vive en AutenticadorTests; aquí, que los handlers la usen de punta a punta.
    string Cookie => $"{SesionFirmada.NombreCookie}={_tokens.Sesion().Emitir("123", null, null)}";
    static readonly (string, string)[] ConXrw = [("X-Requested-With", "gym-coach")];

    [Fact]
    public async Task Cookie_GetEjercicios_200() =>
        Esperar(await _ejercicios.Listar(new RequestFalso("GET", "/api/ejercicios", cookie: Cookie)), HttpStatusCode.OK);

    [Fact]
    public async Task Cookie_PutSinXRequestedWith_401() =>
        Assert.Equal("""{"error":"no_autenticado"}""", Esperar(await _series.Guardar(
            new RequestFalso("PUT", "/api/series/" + Id, body: "{}", cookie: Cookie), Id), HttpStatusCode.Unauthorized));

    // Pasa la auth y llega a validar el body: 400, no 401.
    [Fact]
    public async Task Cookie_PutConXRequestedWith_PasaAuth() =>
        Assert.Contains("\"error\":\"validacion\"", Esperar(await _series.Guardar(
            new RequestFalso("PUT", "/api/series/" + Id, body: "{}", cookie: Cookie, headers: ConXrw), Id), HttpStatusCode.BadRequest));

    // Cookie presente pero mala: no hay segunda oportunidad con el token del header.
    [Fact]
    public async Task CookieInvalida_ConCustomValida_401() =>
        Esperar(await _ejercicios.Listar(new RequestFalso("GET", "/api/ejercicios", customAuthorization: Bearer,
            cookie: $"{SesionFirmada.NombreCookie}=v1.basura.basura")), HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Ejercicios_DevuelveCatalogo()
    {
        var texto = Esperar(await _ejercicios.Listar(new RequestFalso("GET", "/api/ejercicios", Bearer)), HttpStatusCode.OK);
        // Forma exacta que consume la PWA; cargaCorporal solo aparece cuando aplica.
        Assert.Contains("""{"id":"fondos","nombre":"Fondos en paralelas","nombreEn":"Dips","grupo":"pecho","equipo":"libre","cargaCorporal":true}""", texto);
        Assert.Contains("""{"id":"press-banca","nombre":"Press de banca","nombreEn":"Bench press","grupo":"pecho","equipo":"libre"}""", texto);
    }

    [Fact]
    public async Task Historial_EjercicioInexistente_404() =>
        Esperar(await _ejercicios.Historial(
            new RequestFalso("GET", "/api/ejercicios/no-existe/historial", Bearer), "no-existe"), HttpStatusCode.NotFound);

    [Fact]
    public async Task Historial_LimiteInvalido_400() =>
        Esperar(await _ejercicios.Historial(
            new RequestFalso("GET", "/api/ejercicios/sentadilla/historial?limite=51", Bearer), "sentadilla"), HttpStatusCode.BadRequest);

    [Fact]
    public async Task Put_Invalido_400ConDetalles()
    {
        var texto = Esperar(await _series.Guardar(
            new RequestFalso("PUT", "/api/series/" + Id, Bearer, """{"ejercicioId":"press-banca","pesoKg":"60"}"""), Id),
            HttpStatusCode.BadRequest);
        Assert.Contains("\"error\":\"validacion\"", texto);
        Assert.Contains("\"campo\":\"pesoKg\"", texto);
    }

    [Fact]
    public async Task Put_BodyMayorA4KB_400()
    {
        var grande = "{\"x\":\"" + new string('a', 5000) + "\"}";
        var texto = Esperar(await _series.Guardar(new RequestFalso("PUT", "/api/series/" + Id, Bearer, grande), Id),
            HttpStatusCode.BadRequest);
        Assert.Contains("\"campo\":\"body\"", texto);
    }

    // Content-Length declarado grande: se rechaza sin leer el body (aquí el body es corto a propósito).
    [Fact]
    public async Task Put_ContentLengthMayorA4KB_400() =>
        Assert.Contains("\"campo\":\"body\"", Esperar(await _series.Guardar(
            new RequestFalso("PUT", "/api/series/" + Id, Bearer, "{}", contentLength: 5000), Id), HttpStatusCode.BadRequest));

    [Fact]
    public async Task Delete_IdNoUuid_400() =>
        Assert.Contains("\"campo\":\"id\"", Esperar(await _series.Borrar(
            new RequestFalso("DELETE", "/api/series/abc", Bearer), "abc"), HttpStatusCode.BadRequest));

    [Theory]
    [InlineData("desde=2026-01-01&hasta=2026-06-01")]
    [InlineData("desde=2026-02-01&hasta=2026-01-01")]
    [InlineData("hasta=2026-01-01")]
    [InlineData("")]
    public async Task Rango_Invalido_400(string query) =>
        Esperar(await _series.Listar(new RequestFalso("GET", "/api/series?" + query, Bearer)), HttpStatusCode.BadRequest);

    [Fact]
    public async Task Escrituras_PasadoElLimite_429ConRetryAfter()
    {
        var bearer = Bearer;
        // Las escrituras inválidas también cuentan: el límite va antes de validar.
        for (var i = 0; i < Limites.EscriturasPorMinuto; i++)
            Esperar(await _series.Guardar(new RequestFalso("PUT", "/api/series/" + Id, bearer, "{}"), Id), HttpStatusCode.BadRequest);

        var res = await _series.Borrar(new RequestFalso("DELETE", "/api/series/" + Id, bearer), Id);
        Assert.Equal("""{"error":"demasiadas_peticiones"}""", Esperar(res, HttpStatusCode.TooManyRequests));
        Assert.True(res.Headers.TryGetValues("Retry-After", out var ra));
        Assert.Equal("60", ra.Single());

        // Las lecturas tienen su propio cupo.
        Esperar(await Historial404(bearer), HttpStatusCode.NotFound);

        // Pasada la ventana, vuelve a dejar.
        _reloj.Avanzar(TimeSpan.FromMinutes(1));
        Esperar(await _series.Borrar(new RequestFalso("DELETE", "/api/series/abc", bearer), "abc"), HttpStatusCode.BadRequest);
    }

    // Ejercicio inexistente: pasa por el cupo de lecturas y responde 404 sin tocar Cosmos.
    Task<HttpResponseData> Historial404(string bearer) =>
        _ejercicios.Historial(new RequestFalso("GET", "/api/ejercicios/no-existe/historial", bearer), "no-existe");

    Task<HttpResponseData> RangoInvalido(string bearer) =>
        _series.Listar(new RequestFalso("GET", "/api/series", bearer));

    [Fact]
    public async Task Lecturas_PasadoElLimite_429_SinTocarRangoNiCatalogo()
    {
        var bearer = Bearer;
        for (var i = 0; i < Limites.LecturasPorMinuto; i++)
            Esperar(await Historial404(bearer), HttpStatusCode.NotFound);
        Esperar(await Historial404(bearer), HttpStatusCode.TooManyRequests);

        // El rango tiene su cupo y el catálogo no tiene: siguen respondiendo.
        Esperar(await RangoInvalido(bearer), HttpStatusCode.BadRequest);
        Esperar(await _ejercicios.Listar(new RequestFalso("GET", "/api/ejercicios", bearer)), HttpStatusCode.OK);
    }

    // Récords tiene cupo propio (60/min, puede recalcular docs marcados): no comparte el de lecturas.
    [Fact]
    public async Task Records_CupoPropio_429()
    {
        var bearer = Bearer;
        var userId = "google:123"; // el sub por defecto de Tokens.Crear
        for (var i = 0; i < Limites.RecordsPorMinuto; i++)
            Assert.True(_limites.Records.Permitir(userId, out _));

        var res = await _records.Listar(new RequestFalso("GET", "/api/records", bearer));
        Assert.Equal("""{"error":"demasiadas_peticiones"}""", Esperar(res, HttpStatusCode.TooManyRequests));
        Assert.True(res.Headers.TryGetValues("Retry-After", out _));
        // Las lecturas de historial siguen.
        Esperar(await Historial404(bearer), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Records_SinCupoDeLecturas_NoSeFrena()
    {
        var bearer = Bearer;
        for (var i = 0; i < Limites.LecturasPorMinuto; i++)
            Esperar(await Historial404(bearer), HttpStatusCode.NotFound);
        // Pasa el limitador y llega a Cosmos (null aquí): prueba que no usa el cupo de lecturas.
        await Assert.ThrowsAsync<NullReferenceException>(() => _records.Listar(new RequestFalso("GET", "/api/records", bearer)));
    }

    // GET /rutinas trae todas las rutinas completas: cupo propio de 30/min, no el de lecturas.
    [Fact]
    public async Task Rutinas_CupoPropio_429()
    {
        var bearer = Bearer;
        for (var i = 0; i < Limites.RutinasPorMinuto; i++)
            Assert.True(_limites.Rutinas.Permitir("google:123", out _));
        Esperar(await _rutinas.Listar(new RequestFalso("GET", "/api/rutinas", bearer)), HttpStatusCode.TooManyRequests);
        Esperar(await Historial404(bearer), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Rutinas_SinCupoDeLecturas_NoSeFrena()
    {
        var bearer = Bearer;
        for (var i = 0; i < Limites.LecturasPorMinuto; i++)
            Esperar(await Historial404(bearer), HttpStatusCode.NotFound);
        // Pasa el limitador y llega a Cosmos (null aquí).
        await Assert.ThrowsAsync<NullReferenceException>(() => _rutinas.Listar(new RequestFalso("GET", "/api/rutinas", bearer)));
    }

    // G4: si el candado del usuario quedó tomado (algo colgado), 503 a los 5 s sin escribir nada (el Container es null).
    [Fact]
    public async Task CandadoRetenido_503SinEscribir()
    {
        var bearer = Bearer;
        var candado = await _limites.BloquearAsync("google:123");
        Assert.NotNull(candado);
        try
        {
            const string body = """{"ejercicioId":"press-banca","pesoKg":60,"reps":8,"fecha":"2026-10-06","ts":"2026-10-06T10:00:00Z"}""";
            var put = await _series.Guardar(new RequestFalso("PUT", "/api/series/" + Id, bearer, body), Id);
            Assert.Equal("""{"error":"no_disponible"}""", Esperar(put, HttpStatusCode.ServiceUnavailable));
            Assert.True(put.Headers.TryGetValues("Retry-After", out var ra));
            Assert.Equal("2", ra.Single());
            Esperar(await _series.Borrar(new RequestFalso("DELETE", "/api/series/" + Id, bearer), Id), HttpStatusCode.ServiceUnavailable);
        }
        finally
        {
            candado!.Release();
        }
        // Sin tomarlo, el 503 no lo liberó de más: sigue siendo de a uno.
        var otra = await _limites.BloquearAsync("google:123");
        Assert.NotNull(otra);
        Assert.Equal(0, otra!.CurrentCount);
        otra.Release();
    }

    // Tope diario: 120/min sostenido serían ~170 mil escrituras al día; 1500 alcanza para entrenar.
    [Fact]
    public async Task Escrituras_TopeDiario_429()
    {
        var bearer = Bearer;
        for (var i = 0; i < Limites.EscriturasPorDia; i++)
        {
            if (i > 0 && i % Limites.EscriturasPorMinuto == 0) _reloj.Avanzar(TimeSpan.FromMinutes(1));
            Esperar(await _series.Guardar(new RequestFalso("PUT", "/api/series/" + Id, bearer, "{}"), Id), HttpStatusCode.BadRequest);
        }

        // Queda cupo en el minuto, pero no en el día: PUT y DELETE de series y de rutinas.
        var res = await _series.Guardar(new RequestFalso("PUT", "/api/series/" + Id, bearer, "{}"), Id);
        Esperar(res, HttpStatusCode.TooManyRequests);
        Assert.True(res.Headers.TryGetValues("Retry-After", out var ra));
        Assert.True(int.Parse(ra.Single()) > 23 * 3600, ra.Single());
        Esperar(await _series.Borrar(new RequestFalso("DELETE", "/api/series/abc", bearer), "abc"), HttpStatusCode.TooManyRequests);
        Esperar(await _rutinas.Guardar(new RequestFalso("PUT", "/api/rutinas/" + Id, bearer, "{}"), Id), HttpStatusCode.TooManyRequests);
        Esperar(await _rutinas.Borrar(new RequestFalso("DELETE", "/api/rutinas/abc", bearer), "abc"), HttpStatusCode.TooManyRequests);

        // Otro usuario no comparte el tope; pasado un día, vuelve a dejar.
        Esperar(await _series.Borrar(new RequestFalso("DELETE", "/api/series/abc", "Bearer " + _tokens.Crear(sub: "otro")), "abc"),
            HttpStatusCode.BadRequest);
        _reloj.Avanzar(TimeSpan.FromDays(1));
        Esperar(await _series.Borrar(new RequestFalso("DELETE", "/api/series/abc", bearer), "abc"), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rango_PasadoElLimite_429_SinTocarHistorialNiCatalogo()
    {
        var bearer = Bearer;
        // Los rangos inválidos también cuentan: el límite va antes de validar.
        for (var i = 0; i < Limites.RangosPorMinuto; i++)
            Esperar(await RangoInvalido(bearer), HttpStatusCode.BadRequest);

        var res = await RangoInvalido(bearer);
        Assert.Equal("""{"error":"demasiadas_peticiones"}""", Esperar(res, HttpStatusCode.TooManyRequests));
        Assert.True(res.Headers.TryGetValues("Retry-After", out var ra));
        Assert.Equal("60", ra.Single());

        Esperar(await Historial404(bearer), HttpStatusCode.NotFound);
        Esperar(await _ejercicios.Listar(new RequestFalso("GET", "/api/ejercicios", bearer)), HttpStatusCode.OK);

        // Otro usuario no comparte el cupo.
        Esperar(await RangoInvalido("Bearer " + _tokens.Crear(sub: "otro")), HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Catalogo_SinCupo()
    {
        var bearer = Bearer;
        for (var i = 0; i < Limites.LecturasPorMinuto + Limites.RangosPorMinuto + 1; i++)
            Esperar(await _ejercicios.Listar(new RequestFalso("GET", "/api/ejercicios", bearer)), HttpStatusCode.OK);
    }
}
