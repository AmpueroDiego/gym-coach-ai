using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GymCoach.Api.Functions;
using Microsoft.Azure.Cosmos;

namespace GymCoach.Api.Tests;

[Collection(ColeccionCosmos.Nombre)]
public class RutinasIntegracionTests(CosmosFixture cosmos)
{
    readonly Tokens _tokens = new();
    static readonly DateTimeOffset Ahora = DateTimeOffset.UtcNow;
    static readonly RelojFalso Reloj = new(Ahora);
    readonly Limites _limites = new(Reloj);
    static readonly string Hoy = Ahora.UtcDateTime.ToString("yyyy-MM-dd");

    Container Contenedor()
    {
        Assert.SkipWhen(cosmos.Container is null, cosmos.MotivoSkip ?? "");
        return cosmos.Container!;
    }

    Rutinas Handlers() => new(_tokens.Autenticador(), Contenedor(), _limites, Reloj);
    Series HandlersSeries() => new(_tokens.Autenticador(), Contenedor(), _limites, Reloj);
    Ejercicios HandlersEjercicios() => new(_tokens.Autenticador(), Contenedor(), _limites);

    string Bearer(string sub) => "Bearer " + _tokens.Crear(sub: sub);

    static string Body(string nombre = "Pierna", string ejercicio = "sentadilla", decimal? peso = 80) =>
        JsonSerializer.Serialize(new
        {
            nombre,
            ejercicios = new[] { new { id = ejercicio, series = 4, reps = 8, pesoKg = peso, descansoS = 120 } },
        });

    async Task<ResponseFalsa> Put(string sub, string id, string body) =>
        (ResponseFalsa)await Handlers().Guardar(new RequestFalso("PUT", "/api/rutinas/" + id, Bearer(sub), body), id);

    async Task<ResponseFalsa> Borrar(string sub, string id) =>
        (ResponseFalsa)await Handlers().Borrar(new RequestFalso("DELETE", "/api/rutinas/" + id, Bearer(sub)), id);

    async Task<JsonArray> Listar(string sub)
    {
        var res = (ResponseFalsa)await Handlers().Listar(new RequestFalso("GET", "/api/rutinas", Bearer(sub)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonNode.Parse(res.Texto())!.AsArray();
    }

    async Task<string> SeriesDeHoy(string sub)
    {
        var res = (ResponseFalsa)await HandlersSeries().Listar(new RequestFalso("GET", $"/api/series?desde={Hoy}&hasta={Hoy}", Bearer(sub)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return res.Texto();
    }

    static string BodySerie(string? rutina = null) =>
        JsonSerializer.Serialize(new { ejercicioId = "press-banca", pesoKg = 60, reps = 8, fecha = Hoy, ts = Ahora.ToString("o"), rutina });

    [Fact]
    public async Task PutLuegoGet_DevuelveDtoSinUserIdNiTipo()
    {
        var sub = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        var put = await Put(sub, id.ToUpperInvariant(), Body(peso: 47.35m));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var r = Assert.Single(await Listar(sub))!.AsObject();
        Assert.Equal(["id", "nombre", "ejercicios", "actualizada"], r.Select(p => p.Key));
        Assert.Equal(id, (string)r["id"]!);
        Assert.Equal("Pierna", (string)r["nombre"]!);
        Assert.Equal(Ahora.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), (string)r["actualizada"]!);
        Assert.Equal("""[{"id":"sentadilla","series":4,"reps":8,"pesoKg":47.35,"descansoS":120}]""", r["ejercicios"]!.ToJsonString());
        Assert.Equal(JsonNode.Parse(put.Texto())!.ToJsonString(), r.ToJsonString());
    }

    [Fact]
    public async Task PesoNull_VuelveComoNull()
    {
        var sub = Guid.NewGuid().ToString();
        await Put(sub, Guid.NewGuid().ToString(), Body(peso: null));
        Assert.Contains("\"pesoKg\":null", (await Listar(sub)).ToJsonString());
    }

    [Fact]
    public async Task Put_EsIdempotenteYEdita()
    {
        var sub = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, id, Body())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, id, Body())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, id, Body("Torso", "press-banca"))).StatusCode);

        var r = Assert.Single(await Listar(sub))!;
        Assert.Equal("Torso", (string)r["nombre"]!);
        Assert.Equal("press-banca", (string)r["ejercicios"]![0]!["id"]!);
    }

    [Fact]
    public async Task Aislamiento_EntreUsuarios()
    {
        string a = Guid.NewGuid().ToString(), b = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        await Put(a, id, Body("De A"));

        Assert.Empty(await Listar(b));

        var borrar = await Borrar(b, id);
        Assert.Equal(HttpStatusCode.NotFound, borrar.StatusCode);
        Assert.Contains("no_encontrado", borrar.Texto());
        Assert.Single(await Listar(a));

        // Mismo uuid en la partición de B: crea la suya, la de A no cambia.
        Assert.Equal(HttpStatusCode.OK, (await Put(b, id, Body("De B"))).StatusCode);
        Assert.Equal("De A", (string)Assert.Single(await Listar(a))!["nombre"]!);
        Assert.Equal("De B", (string)Assert.Single(await Listar(b))!["nombre"]!);
    }

    [Fact]
    public async Task Delete_BorraY404SiNoExiste()
    {
        var sub = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        await Put(sub, id, Body());

        Assert.Equal(HttpStatusCode.NoContent, (await Borrar(sub, id)).StatusCode);
        Assert.Empty(await Listar(sub));
        Assert.Equal(HttpStatusCode.NotFound, (await Borrar(sub, id)).StatusCode);
    }

    [Fact]
    public async Task Tope_LaVeintiunaFallaYEditarLaVeinteNo()
    {
        var sub = Guid.NewGuid().ToString();
        var ids = Enumerable.Range(0, Validacion.MaxRutinas).Select(_ => Guid.NewGuid().ToString()).ToList();
        foreach (var id in ids)
            Assert.Equal(HttpStatusCode.OK, (await Put(sub, id, Body())).StatusCode);

        var extra = await Put(sub, Guid.NewGuid().ToString(), Body());
        Assert.Equal(HttpStatusCode.BadRequest, extra.StatusCode);
        Assert.Contains("""{"campo":"rutinas","mensaje":"Máximo 20 rutinas."}""", JsonNode.Parse(extra.Texto())!["detalles"]!.ToJsonString(
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));

        Assert.Equal(HttpStatusCode.OK, (await Put(sub, ids[^1], Body("Editada"))).StatusCode);
        Assert.Equal(Validacion.MaxRutinas, (await Listar(sub)).Count);
    }

    [Fact]
    public async Task SeriesEHistorial_NoDevuelvenRutinas()
    {
        var sub = Guid.NewGuid().ToString();
        await Put(sub, Guid.NewGuid().ToString(), Body(ejercicio: "press-banca"));
        var idSerie = Guid.NewGuid().ToString();
        await HandlersSeries().Guardar(new RequestFalso("PUT", "/api/series/" + idSerie, Bearer(sub), BodySerie()), idSerie);

        var series = JsonNode.Parse(await SeriesDeHoy(sub))!.AsArray();
        Assert.Single(series);
        Assert.DoesNotContain("rutina:", series.ToJsonString());

        var hist = (ResponseFalsa)await HandlersEjercicios().Historial(
            new RequestFalso("GET", "/api/ejercicios/press-banca/historial", Bearer(sub)), "press-banca");
        Assert.Single(JsonNode.Parse(hist.Texto())!.AsArray());
    }

    [Fact]
    public async Task SeriesConElUuidDeUnaRutina_NoLaTocan()
    {
        var sub = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        await Put(sub, id, Body());

        var borrar = await HandlersSeries().Borrar(new RequestFalso("DELETE", "/api/series/" + id, Bearer(sub)), id);
        Assert.Equal(HttpStatusCode.NotFound, borrar.StatusCode);
        Assert.Single(await Listar(sub));

        var put = await HandlersSeries().Guardar(new RequestFalso("PUT", "/api/series/" + id, Bearer(sub), BodySerie()), id);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("Pierna", (string)Assert.Single(await Listar(sub))!["nombre"]!);
        Assert.Single(JsonNode.Parse(await SeriesDeHoy(sub))!.AsArray());

        // Y DELETE /series/{id} ahora borra la serie, no la rutina.
        Assert.Equal(HttpStatusCode.NoContent,
            (await HandlersSeries().Borrar(new RequestFalso("DELETE", "/api/series/" + id, Bearer(sub)), id)).StatusCode);
        Assert.Single(await Listar(sub));
    }

    [Fact]
    public async Task SerieConRutina_SeGuardaYSeDevuelve()
    {
        var sub = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        var put = (ResponseFalsa)await HandlersSeries().Guardar(new RequestFalso("PUT", "/api/series/" + id, Bearer(sub), BodySerie(" Pierna ")), id);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("Pierna", (string)JsonNode.Parse(put.Texto())!["rutina"]!);

        Assert.Equal("Pierna", (string)JsonNode.Parse(await SeriesDeHoy(sub))![0]!["rutina"]!);
        var hist = (ResponseFalsa)await HandlersEjercicios().Historial(
            new RequestFalso("GET", "/api/ejercicios/press-banca/historial", Bearer(sub)), "press-banca");
        Assert.Equal("Pierna", (string)JsonNode.Parse(hist.Texto())![0]!["rutina"]!);

        // Reintento sin rutina: el replace no deja la rutina vieja.
        await HandlersSeries().Guardar(new RequestFalso("PUT", "/api/series/" + id, Bearer(sub), BodySerie()), id);
        Assert.Null(JsonNode.Parse(await SeriesDeHoy(sub))![0]!["rutina"]);
        var doc = await Contenedor().ReadItemAsync<JsonObject>(id, new PartitionKey("google:" + sub),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(doc.Resource.ContainsKey("rutina"));
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("5")]
    [InlineData("\"a\\u0007b\"")]
    public async Task SerieConRutinaMala_400(string rutina)
    {
        var id = Guid.NewGuid().ToString();
        var body = JsonNode.Parse(BodySerie())!;
        body["rutina"] = JsonNode.Parse(rutina);
        var put = (ResponseFalsa)await HandlersSeries().Guardar(
            new RequestFalso("PUT", "/api/series/" + id, Bearer("x"), body.ToJsonString()), id);
        Assert.Equal(HttpStatusCode.BadRequest, put.StatusCode);
        Assert.Contains("\"campo\":\"rutina\"", put.Texto());
    }

    // Documento escrito antes de que existiera el campo rutina.
    [Fact]
    public async Task SerieVieja_SinCampo_SigueEnElGet()
    {
        var sub = Guid.NewGuid().ToString();
        var userId = "google:" + sub;
        var id = Guid.NewGuid().ToString();
        var viejo = new JsonObject
        {
            ["id"] = id, ["userId"] = userId, ["tipo"] = "serie", ["ejercicioId"] = "press-banca", ["pesoKg"] = 60, ["reps"] = 8,
            ["fecha"] = Hoy, ["ts"] = Ahora.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        };
        await Contenedor().CreateItemAsync(viejo, new PartitionKey(userId), cancellationToken: TestContext.Current.CancellationToken);

        var s = Assert.Single(JsonNode.Parse(await SeriesDeHoy(sub))!.AsArray())!.AsObject();
        Assert.Equal(id, (string)s["id"]!);
        Assert.False(s.ContainsKey("rutina"));
    }
}
