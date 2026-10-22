using System.Net;
using System.Text.Json;
using GymCoach.Api.Functions;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker.Http;

namespace GymCoach.Api.Tests;

/// <summary>
/// Contenedor de pruebas en el emulador de Cosmos. Si COSMOS_ENDPOINT no está definido o el
/// emulador no responde, Container queda null y las pruebas se saltan.
/// </summary>
public sealed class CosmosFixture : IAsyncLifetime
{
    // Clave pública y documentada del emulador, no es un secreto.
    const string ClaveEmulador = "C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";
    CosmosClient? _client;
    Database? _db;
    string? _endpoint;
    readonly List<CosmosClient> _otros = [];

    public Container? Container { get; private set; }
    public string? MotivoSkip { get; private set; }

    public async ValueTask InitializeAsync()
    {
        var endpoint = Environment.GetEnvironmentVariable("COSMOS_ENDPOINT");
        if (string.IsNullOrEmpty(endpoint))
        {
            MotivoSkip = "COSMOS_ENDPOINT no definido; pruebas de integración omitidas.";
            return;
        }

        _endpoint = endpoint;
        try
        {
            _client = Cliente();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _db = (await _client.CreateDatabaseIfNotExistsAsync("gymcoach-pruebas-" + Guid.NewGuid().ToString("N")[..8],
                cancellationToken: cts.Token)).Database;
            Container = (await _db.CreateContainerIfNotExistsAsync("series", "/userId", cancellationToken: cts.Token)).Container;
        }
        catch (Exception e)
        {
            MotivoSkip = $"Emulador de Cosmos no responde ({e.GetType().Name} {(e as CosmosException)?.StatusCode}); pruebas de integración omitidas.";
            Container = null;
        }
    }

    CosmosClient Cliente(RequestHandler? handler = null)
    {
        var opciones = new CosmosClientOptions
        {
            UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web),
            ConnectionMode = ConnectionMode.Gateway,
        };
        if (handler is not null) opciones.CustomHandlers.Add(handler);
        return new CosmosClient(_endpoint, Environment.GetEnvironmentVariable("COSMOS_KEY") ?? ClaveEmulador, opciones);
    }

    /// <summary>El mismo contenedor visto por un cliente con un handler propio (para inyectar fallas).</summary>
    public Container ConHandler(RequestHandler handler, string? contenedor = null)
    {
        var cliente = Cliente(handler);
        lock (_otros) _otros.Add(cliente);
        return cliente.GetContainer(Container!.Database.Id, contenedor ?? Container.Id);
    }

    public async ValueTask DisposeAsync()
    {
        if (_db is not null) await _db.DeleteAsync();
        _client?.Dispose();
        foreach (var c in _otros) c.Dispose();
    }
}

/// <summary>
/// Una sola base para todas las clases de integración: con una por clase, crearlas a la vez hacía que el
/// emulador rechazara alguna (CosmosException) y se saltaba la clase entera. Las clases de la colección corren en serie.
/// </summary>
[CollectionDefinition(Nombre)]
public sealed class ColeccionCosmos : ICollectionFixture<CosmosFixture>
{
    public const string Nombre = "Cosmos";
}

[Collection(ColeccionCosmos.Nombre)]
public class SeriesIntegracionTests(CosmosFixture cosmos)
{
    readonly Tokens _tokens = new();

    // Una sola fuente de "ahora" para fecha, ts y la validación del handler: sin sorpresas a medianoche UTC.
    static readonly DateTimeOffset Ahora = DateTimeOffset.UtcNow;
    static readonly RelojFalso Reloj = new(Ahora);
    // Límites holgados para estas pruebas: cada instancia de prueba tiene los suyos.
    readonly Limites _limites = new(Reloj);

    Series Handlers() => new(_tokens.Autenticador(), Contenedor(), _limites, Reloj);
    Ejercicios HandlersEjercicios() => new(_tokens.Autenticador(), Contenedor(), _limites);

    Container Contenedor()
    {
        Assert.SkipWhen(cosmos.Container is null, cosmos.MotivoSkip ?? "");
        return cosmos.Container!;
    }

    string Bearer(string sub) => "Bearer " + _tokens.Crear(sub: sub);

    static readonly string Hoy = Ahora.UtcDateTime.ToString("yyyy-MM-dd");

    static string Body(string ejercicio = "press-banca", decimal peso = 60, int reps = 8, string? fecha = null, string? ts = null) =>
        JsonSerializer.Serialize(new { ejercicioId = ejercicio, pesoKg = peso, reps, fecha = fecha ?? Hoy, ts = ts ?? Ahora.ToString("o") });

    async Task<ResponseFalsa> Put(string sub, string id, string body) =>
        (ResponseFalsa)await Handlers().Guardar(new RequestFalso("PUT", "/api/series/" + id, Bearer(sub), body), id);

    async Task<List<Serie>> Rango(string sub, string desde, string hasta)
    {
        var res = (ResponseFalsa)await Handlers().Listar(new RequestFalso("GET", $"/api/series?desde={desde}&hasta={hasta}", Bearer(sub)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonSerializer.Deserialize<List<Serie>>(res.Texto(), Http.Json)!;
    }

    /// <summary>Handlers con la marca del perfil de entrenamiento falsa: cuenta las llamadas y puede fallar.</summary>
    Series ConPerfil(List<string> marcados, Exception? falla = null) => new(_tokens.Autenticador(), Contenedor(), _limites, Reloj,
        perfilesEntrenamiento: new PerfilesEntrenamiento(
            (_, _) => throw new InvalidOperationException("Series no lee el perfil."),
            (_, _) => throw new InvalidOperationException("Series no recalcula el perfil."),
            (_, _, _) => throw new InvalidOperationException("Series no guarda el perfil."),
            (userId, _) =>
            {
                marcados.Add(userId);
                return falla is null ? Task.CompletedTask : Task.FromException(falla);
            },
            _ => true, Reloj));

    [Fact]
    public async Task PerfilEntrenamiento_SeMarcaAlEditarYBorrar_NoConSerieNuevaNiReintento()
    {
        var sub = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        var marcados = new List<string>();
        var h = ConPerfil(marcados);
        Task<HttpResponseData> PutCon(string body) => h.Guardar(new RequestFalso("PUT", "/api/series/" + id, Bearer(sub), body), id);

        Assert.Equal(HttpStatusCode.OK, (await PutCon(Body(peso: 60))).StatusCode);
        // Reintento idéntico (UI optimista): nada cambió.
        Assert.Equal(HttpStatusCode.OK, (await PutCon(Body(peso: 60))).StatusCode);
        Assert.Empty(marcados);

        Assert.Equal(HttpStatusCode.OK, (await PutCon(Body(peso: 65))).StatusCode);
        Assert.Equal(["google:" + sub], marcados);

        var borrar = await h.Borrar(new RequestFalso("DELETE", "/api/series/" + id, Bearer(sub)), id);
        Assert.Equal(HttpStatusCode.NoContent, borrar.StatusCode);
        Assert.Equal(2, marcados.Count);
    }

    [Fact]
    public async Task PerfilEntrenamiento_SiMarcarFalla_LaSerieSeGuardaYSeBorraIgual()
    {
        var sub = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        var marcados = new List<string>();
        var h = ConPerfil(marcados, new CosmosException("saturado", HttpStatusCode.ServiceUnavailable, 0, "", 0));

        await Put(sub, id, Body(peso: 60));
        Assert.Equal(HttpStatusCode.OK,
            (await h.Guardar(new RequestFalso("PUT", "/api/series/" + id, Bearer(sub), Body(peso: 70)), id)).StatusCode);
        Assert.Equal(70, Assert.Single(await Rango(sub, Hoy, Hoy)).PesoKg);
        Assert.Equal(HttpStatusCode.NoContent,
            (await h.Borrar(new RequestFalso("DELETE", "/api/series/" + id, Bearer(sub)), id)).StatusCode);
        Assert.Equal(2, marcados.Count);
    }

    [Fact]
    public async Task Put_EsIdempotente()
    {
        var sub = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        var body = Body();

        Assert.Equal(HttpStatusCode.OK, (await Put(sub, id, body)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, id, body)).StatusCode);

        Assert.Single(await Rango(sub, Hoy, Hoy));
    }

    [Fact]
    public async Task Put_EditaLaSerie()
    {
        var sub = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        await Put(sub, id, Body(peso: 60, reps: 8));
        await Put(sub, id, Body(peso: 62.5m, reps: 6));

        var serie = Assert.Single(await Rango(sub, Hoy, Hoy));
        Assert.Equal(62.5m, serie.PesoKg);
        Assert.Equal(6, serie.Reps);
    }

    [Fact]
    public async Task Historial_OrdenDescYLimite()
    {
        var sub = Guid.NewGuid().ToString();
        var t0 = Ahora.AddMinutes(-10);
        for (var i = 0; i < 3; i++)
            await Put(sub, Guid.NewGuid().ToString(), Body("sentadilla", 100 + i, ts: t0.AddMinutes(i).ToString("o")));
        await Put(sub, Guid.NewGuid().ToString(), Body("press-banca"));

        var res = (ResponseFalsa)await HandlersEjercicios().Historial(
            new RequestFalso("GET", "/api/ejercicios/sentadilla/historial?limite=2", Bearer(sub)), "sentadilla");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var historial = JsonSerializer.Deserialize<List<Serie>>(res.Texto(), Http.Json)!;
        Assert.Equal([102m, 101m], historial.Select(s => s.PesoKg));
    }

    [Fact]
    public async Task Rango_OrdenAscPorTs()
    {
        var sub = Guid.NewGuid().ToString();
        var t0 = Ahora.AddMinutes(-10);
        await Put(sub, Guid.NewGuid().ToString(), Body(peso: 2, ts: t0.AddMinutes(2).ToString("o")));
        await Put(sub, Guid.NewGuid().ToString(), Body(peso: 1, ts: t0.AddMinutes(1).ToString("o")));

        Assert.Equal([1m, 2m], (await Rango(sub, Hoy, Hoy)).Select(s => s.PesoKg));
    }

    // Cosmos guarda números como double: 47.35 debe volver exacto como decimal.
    [Fact]
    public async Task Decimal_IdaYVueltaExacto()
    {
        var sub = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        await Put(sub, id, Body(peso: 47.35m));

        Assert.Equal(47.35m, Assert.Single(await Rango(sub, Hoy, Hoy)).PesoKg);

        var res = (ResponseFalsa)await HandlersEjercicios().Historial(
            new RequestFalso("GET", "/api/ejercicios/press-banca/historial", Bearer(sub)), "press-banca");
        Assert.Contains("\"pesoKg\":47.35,", res.Texto());
    }

    [Fact]
    public async Task Aislamiento_EntreUsuarios()
    {
        string a = Guid.NewGuid().ToString(), b = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        await Put(a, id, Body(peso: 80));

        // B no ve la serie de A en su rango.
        Assert.Empty(await Rango(b, Hoy, Hoy));

        // B no puede borrarla.
        var borrar = await Handlers().Borrar(new RequestFalso("DELETE", "/api/series/" + id, Bearer(b)), id);
        Assert.Equal(HttpStatusCode.NotFound, borrar.StatusCode);

        // Un PUT de B con el id de A crea un documento en la partición de B; el de A queda intacto.
        await Put(b, id, Body(peso: 1));
        Assert.Equal(80m, Assert.Single(await Rango(a, Hoy, Hoy)).PesoKg);
        Assert.Equal(1m, Assert.Single(await Rango(b, Hoy, Hoy)).PesoKg);
    }

    [Fact]
    public async Task Delete_BorraY404SiNoExiste()
    {
        var sub = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        await Put(sub, id, Body());

        var req = () => new RequestFalso("DELETE", "/api/series/" + id, Bearer(sub));
        Assert.Equal(HttpStatusCode.NoContent, (await Handlers().Borrar(req(), id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Handlers().Borrar(req(), id)).StatusCode);
    }

    // Inserta directo en Cosmos (sin pasar por el cupo de escrituras) y en paralelo, en un contenedor
    // aparte con más RU/s: con los 400 por defecto del emulador las inserciones se throttlean (429).
    [Fact]
    [Trait("Categoria", "Lenta")]
    public async Task Rango_ConMasDelTope_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var contenedor = (await Contenedor().Database.CreateContainerAsync(
            "tope-" + Guid.NewGuid().ToString("N")[..8], "/userId", throughput: 10000, cancellationToken: ct)).Container;
        try
        {
            await RangoConMasDelTope(contenedor, ct);
        }
        finally
        {
            await contenedor.DeleteContainerAsync(cancellationToken: CancellationToken.None);
        }
    }

    async Task RangoConMasDelTope(Container contenedor, CancellationToken ct)
    {
        var sub = Guid.NewGuid().ToString();
        var userId = "google:" + sub; // el mismo userId que saca ValidadorGoogle del token
        var ts = Ahora.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        using var cupo = new SemaphoreSlim(50);
        await Task.WhenAll(Enumerable.Range(0, Validacion.MaxSeriesRango + 1).Select(async _ =>
        {
            await cupo.WaitAsync(ct);
            try
            {
                var serie = new Serie(Guid.NewGuid().ToString(), "press-banca", 60, 8, Hoy, ts);
                await contenedor.CreateItemAsync(SerieDoc.De(serie, userId), new PartitionKey(userId), cancellationToken: ct);
            }
            finally { cupo.Release(); }
        }));

        var handlers = new Series(_tokens.Autenticador(), contenedor, _limites, Reloj);
        var res = (ResponseFalsa)await handlers.Listar(new RequestFalso("GET", $"/api/series?desde={Hoy}&hasta={Hoy}", Bearer(sub)));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("\"campo\":\"hasta\"", res.Texto());
    }
}
