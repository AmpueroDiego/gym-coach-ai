using System.Net;
using System.Text.Json;
using GymCoach.Api.Functions;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker.Http;

namespace GymCoach.Api.Tests;

/// <summary>GET y POST /api/coach/plan-dia con modelo guionado y Cosmos en memoria.</summary>
public class PlanDiaHttpTests
{
    const string Usuario = "google:123";
    static readonly DateOnly Hoy = new(2026, 10, 9);
    readonly Tokens _tokens = new();
    readonly RelojFalso _reloj = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    readonly PlanesFalsos _planes = new();
    readonly PerfilesFalsos _perfiles = new();
    readonly Limites _limites;

    public PlanDiaHttpTests() => _limites = new Limites(_reloj);

    string Bearer => "Bearer " + _tokens.Crear();

    PlanDiaHttp Handler(IModeloCoach modelo)
    {
        var p = _perfiles.Crear();
        var herramientas = new HerramientasCoach((QueryDefinition _, string _) => Task.FromResult(new List<Serie>()), p);
        var planes = _planes.Crear(new ConversacionCoach(modelo, herramientas), p, _limites, _reloj);
        return new PlanDiaHttp(_tokens.Autenticador(), _limites, _reloj, modelo, planes);
    }

    static RequestFalso Get(string? authorization, string query = "?hoy=2026-10-09") =>
        new("GET", "/api/coach/plan-dia" + query, authorization);

    static RequestFalso Post(string? authorization, string body = """{"hoy":"2026-10-09"}""") =>
        new("POST", "/api/coach/plan-dia", authorization, body);

    static string Esperar(HttpResponseData res, HttpStatusCode codigo)
    {
        var texto = ((ResponseFalsa)res).Texto();
        Assert.True(codigo == res.StatusCode, $"Esperaba {codigo}, llegó {res.StatusCode}: {texto}");
        return texto;
    }

    sealed class ModeloQueFalla : IModeloCoach
    {
        public Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
            bool permitirHerramientas, CancellationToken ct) => throw new ModeloNoDisponibleException("429");
    }

    PlanDiaDoc Doc(int variaciones) => new(PlanDiaDoc.IdDoc(Hoy), Usuario, PlanDiaDoc.TipoDoc, "2026-10-09", null,
        [.. Enumerable.Range(1, variaciones).Select(i => new VariacionPlan("plan " + i, ["ver_entrenos_recientes"], null,
            "2026-10-09T11:00:00.000Z"))], "2026-10-09T11:00:00.000Z");

    [Fact]
    public async Task Get_SinToken_401() =>
        Esperar(await Handler(new ModeloGuionado(ModeloGuionado.ConRutina("x"))).Leer(Get(null)), HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Get_SinDoc()
    {
        var texto = Esperar(await Handler(new ModeloGuionado(ModeloGuionado.ConRutina("x"))).Leer(Get(Bearer)), HttpStatusCode.OK);
        Assert.Equal("""{"fecha":"2026-10-09","actual":null,"numero":0,"restantes":3,"generando":false}""", texto);
    }

    [Fact]
    public async Task Get_ConDoc()
    {
        _planes.Sembrar(Doc(1));
        var texto = Esperar(await Handler(new ModeloGuionado(ModeloGuionado.ConRutina("x"))).Leer(Get(Bearer)), HttpStatusCode.OK);
        Assert.Equal("""{"fecha":"2026-10-09","actual":{"texto":"plan 1","herramientas":["ver_entrenos_recientes"],"rutina":null,"creado":"2026-10-09T11:00:00.000Z"},"numero":1,"restantes":2,"generando":false}""",
            texto);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?hoy=2026-10-11")]
    [InlineData("?hoy=2026-10-07")]
    [InlineData("?hoy=9-10-2026")]
    public async Task Get_HoyInvalido_400(string query) =>
        Assert.Contains("\"campo\":\"hoy\"", Esperar(await Handler(new ModeloGuionado(ModeloGuionado.ConRutina("x")))
            .Leer(Get(Bearer, query)), HttpStatusCode.BadRequest));

    [Fact]
    public async Task Post_SinToken_401() =>
        Esperar(await Handler(new ModeloGuionado(ModeloGuionado.ConRutina("x"))).Generar(Post(null)), HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Post_CoachSinConfigurar_503SinGastarCupo()
    {
        Assert.Equal("""{"error":"coach_no_configurado"}""",
            Esperar(await Handler(new ModeloSinConfigurar()).Generar(Post(Bearer)), HttpStatusCode.ServiceUnavailable));
        Assert.Equal(0, _limites.Coach.Claves);
        Assert.Empty(_planes.Docs);
    }

    [Fact]
    public async Task Post_GeneraYGuarda()
    {
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("Hoy: pierna."));
        var texto = Esperar(await Handler(modelo).Generar(Post(Bearer)), HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(texto);
        var raiz = doc.RootElement;
        Assert.Equal("Hoy: pierna.", raiz.GetProperty("actual").GetProperty("texto").GetString());
        Assert.Equal("Pecho", raiz.GetProperty("actual").GetProperty("rutina").GetProperty("nombre").GetString());
        Assert.Equal(1, raiz.GetProperty("numero").GetInt32());
        Assert.Equal(2, raiz.GetProperty("restantes").GetInt32());
        // Un turno: proponer_rutina y luego el texto.
        Assert.Equal(2, modelo.Llamadas.Count);
    }

    [Fact]
    public async Task Post_UserIdDelBodyIgnorado()
    {
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("ok"));
        Esperar(await Handler(modelo).Generar(Post(Bearer, """{"hoy":"2026-10-09","userId":"google:otro"}""")), HttpStatusCode.OK);
        Assert.Equal([Usuario], _planes.Docs.Keys.Select(k => k.UserId));
    }

    [Theory]
    [InlineData("""{"hoy":"2026-10-11"}""", "hoy")]
    [InlineData("""{"hoy":20261009}""", "hoy")]
    [InlineData("""{"hoy":null}""", "hoy")]
    [InlineData("""{"offsetMin":60}""", "hoy")]
    [InlineData("""{"hoy":"2026-10-09","otraOpcion":"true"}""", "otraOpcion")]
    [InlineData("""{"hoy":"2026-10-09","otraOpcion":1}""", "otraOpcion")]
    [InlineData("""{"hoy":"2026-10-09","otraOpcion":null}""", "otraOpcion")]
    [InlineData("""{"hoy":"2026-10-09","offsetMin":1.5}""", "offsetMin")]
    [InlineData("""{"hoy":"2026-10-09","offsetMin":841}""", "offsetMin")]
    [InlineData("""{"hoy":"2026-10-09","offsetMin":-721}""", "offsetMin")]
    [InlineData("""{"hoy":"2026-10-09","offsetMin":"60"}""", "offsetMin")]
    [InlineData("""{"hoy":"2026-10-09","numero":-1}""", "numero")]
    [InlineData("""{"hoy":"2026-10-09","numero":1.5}""", "numero")]
    [InlineData("""{"hoy":"2026-10-09","numero":"1"}""", "numero")]
    [InlineData("""{"hoy":"2026-10-09","idioma":"EN"}""", "idioma")]
    [InlineData("""{"hoy":"2026-10-09","idioma":"fr"}""", "idioma")]
    [InlineData("""{"hoy":"2026-10-09","idioma":1}""", "idioma")]
    [InlineData("""[]""", "body")]
    [InlineData("""no""", "body")]
    [InlineData("""{"hoy":"2026-10-09","hoy":"2026-10-08"}""", "body")]
    public async Task Post_BodyInvalido_400SinModelo(string body, string campo)
    {
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("x"));
        Assert.Contains($"\"campo\":\"{campo}\"", Esperar(await Handler(modelo).Generar(Post(Bearer, body)), HttpStatusCode.BadRequest));
        Assert.Empty(modelo.Llamadas);
        Assert.Equal(0, _limites.CoachDiario.Claves);
    }

    [Theory]
    [InlineData("""{"hoy":"2026-10-09","offsetMin":840,"otraOpcion":false}""")]
    [InlineData("""{"hoy":"2026-10-08","offsetMin":-720}""")]
    [InlineData("""{"hoy":"2026-10-10","offsetMin":null}""")]
    public async Task Post_BordesValidos_200(string body) =>
        Esperar(await Handler(new ModeloGuionado(ModeloGuionado.ConRutina("ok"))).Generar(Post(Bearer, body)), HttpStatusCode.OK);

    [Fact]
    public async Task Post_SinCupoDeLecturas_429()
    {
        for (var i = 0; i < Limites.LecturasPorMinuto; i++) _limites.Lecturas.Permitir(Usuario, out _);
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("x"));
        Esperar(await Handler(modelo).Generar(Post(Bearer)), HttpStatusCode.TooManyRequests);
        Assert.Empty(modelo.Llamadas);
        Assert.Empty(_planes.Docs);
    }

    [Fact]
    public async Task Post_ReintentoConNumeroViejo_200SinModelo()
    {
        _planes.Sembrar(Doc(2));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("x"));
        var texto = Esperar(await Handler(modelo).Generar(Post(Bearer, """{"hoy":"2026-10-09","otraOpcion":true,"numero":1}""")),
            HttpStatusCode.OK);
        Assert.Contains("\"numero\":2", texto);
        Assert.Empty(modelo.Llamadas);
    }

    [Fact]
    public async Task Post_BodyMayorAlTope_400() =>
        Assert.Contains("\"campo\":\"body\"", Esperar(await Handler(new ModeloGuionado(ModeloGuionado.ConRutina("x")))
            .Generar(Post(Bearer, "{\"hoy\":\"2026-10-09\",\"x\":\"" + new string('a', PlanDiaHttp.MaxBodyBytes) + "\"}")),
            HttpStatusCode.BadRequest));

    [Fact]
    public async Task Post_CuartaGeneracion_409()
    {
        _planes.Sembrar(Doc(PlanesDia.MaxGeneraciones));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("x"));
        Assert.Equal("""{"error":"tope_variaciones"}""",
            Esperar(await Handler(modelo).Generar(Post(Bearer, """{"hoy":"2026-10-09","otraOpcion":true}""")), HttpStatusCode.Conflict));
        Assert.Empty(modelo.Llamadas);
    }

    [Fact]
    public async Task Post_SinCupoCoach_429ConRetryAfter()
    {
        for (var i = 0; i < Limites.CoachPorMinuto; i++) _limites.Coach.Permitir(Usuario, out _);
        var res = await Handler(new ModeloGuionado(ModeloGuionado.ConRutina("x"))).Generar(Post(Bearer));
        Assert.Equal("""{"error":"demasiadas_peticiones"}""", Esperar(res, HttpStatusCode.TooManyRequests));
        Assert.InRange(int.Parse(res.Headers.GetValues("Retry-After").Single()), 1, 60);
        Assert.Empty(_planes.Docs);
    }

    [Fact]
    public async Task Post_FallaElModelo_503()
    {
        var res = await Handler(new ModeloQueFalla()).Generar(Post(Bearer));
        Assert.Equal("""{"error":"no_disponible"}""", Esperar(res, HttpStatusCode.ServiceUnavailable));
        Assert.Empty(_planes.Docs);
    }

    [Fact]
    public async Task Post_EnIngles_GeneraConPromptEnInglesYGuardaElIdioma()
    {
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("Today: legs."));
        Esperar(await Handler(modelo).Generar(Post(Bearer, """{"hoy":"2026-10-09","idioma":"en"}""")), HttpStatusCode.OK);
        Assert.StartsWith(PromptCoach.Reglas(Hoy, "en"), modelo.Llamadas[0].Mensajes[0].Texto);
        Assert.Equal("en", _planes.Doc(Usuario, Hoy)!.Idioma);
    }

    [Theory]
    [InlineData("""{"hoy":"2026-10-09"}""", "es")]
    [InlineData("""{"hoy":"2026-10-09","idioma":null}""", "es")]
    [InlineData("""{"hoy":"2026-10-09","idioma":"en"}""", "en")]
    public void Peticion_Idioma(string body, string esperado) =>
        Assert.Equal(esperado, PlanDiaHttp.Peticion(body, _reloj.GetUtcNow()).Peticion!.Idioma);

    [Fact]
    public async Task Post_ConPlan_200SinModelo()
    {
        _planes.Sembrar(Doc(1));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("x"));
        Assert.Contains("\"texto\":\"plan 1\"", Esperar(await Handler(modelo).Generar(Post(Bearer)), HttpStatusCode.OK));
        Assert.Empty(modelo.Llamadas);
    }
}
