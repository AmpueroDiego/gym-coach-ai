using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace GymCoach.Api.Tests;

/// <summary>Conversaciones de Foundry falsas: ids conv_1, conv_2...; anota lo creado y lo borrado.</summary>
public sealed class ConversacionesAgenteFalsas : IConversacionesAgente
{
    int _n;
    public ConcurrentQueue<string> Creadas { get; } = new();
    public ConcurrentQueue<string> Borradas { get; } = new();
    /// <summary>Lo que lanza CrearAsync (null: crea bien).</summary>
    public Exception? FallarCrear { get; set; }
    /// <summary>Si no es null, CrearAsync espera a que se complete (para forzar una carrera).</summary>
    public Func<Task>? AntesDeCrear { get; set; }

    public async Task<string> CrearAsync(CancellationToken ct)
    {
        if (FallarCrear is { } e) throw e;
        if (AntesDeCrear is { } esperar) await esperar();
        var id = "conv_" + Interlocked.Increment(ref _n);
        Creadas.Enqueue(id);
        return id;
    }

    /// <summary>Ids cuyo borrado no termina (BorrarAsync devuelve false).</summary>
    public ConcurrentDictionary<string, byte> NoSeBorran { get; } = new();

    /// <summary>Borradas anota cada intento, termine o no.</summary>
    public Task<bool> BorrarAsync(string conversacionId, CancellationToken ct)
    {
        Borradas.Enqueue(conversacionId);
        return Task.FromResult(!NoSeBorran.ContainsKey(conversacionId));
    }
}

[Collection(ColeccionCosmos.Nombre)]
public class ConversacionesCoachIntegracionTests(CosmosFixture cosmos)
{
    static readonly DateOnly Hoy = new(2026, 10, 20);
    readonly RelojFalso _reloj = new(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
    readonly ConversacionesAgenteFalsas _foundry = new();
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    Container Contenedor()
    {
        Assert.SkipWhen(cosmos.Container is null, cosmos.MotivoSkip ?? "");
        return cosmos.Container!;
    }

    ConversacionesCoach Conversaciones() => new(Contenedor(), _foundry, _reloj);

    static string Usuario() => "google:" + Guid.NewGuid();

    async Task<JsonElement?> Doc(string userId)
    {
        using var res = await Contenedor().ReadItemStreamAsync(ConversacionAgenteDoc.IdDoc, new PartitionKey(userId), cancellationToken: Ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        using var doc = await JsonDocument.ParseAsync(res.Content, cancellationToken: Ct);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task MismoDia_Reusa_YElDocLlevaTipo()
    {
        var u = Usuario();
        var c = Conversaciones();
        var id = await c.ObtenerAsync(u, Hoy, nueva: true, Ct);
        Assert.Equal(id, await c.ObtenerAsync(u, Hoy, nueva: false, Ct));
        Assert.Single(_foundry.Creadas);
        Assert.Empty(_foundry.Borradas);

        var doc = (await Doc(u))!.Value;
        Assert.Equal(ConversacionAgenteDoc.TipoDoc, doc.GetProperty("tipo").GetString());
        Assert.Equal(u, doc.GetProperty("userId").GetString());
        Assert.Equal(id, doc.GetProperty("conversacionId").GetString());
        Assert.Equal("2026-10-20", doc.GetProperty("dia").GetString());
        Assert.Equal("2026-10-20T12:00:00.000Z", doc.GetProperty("creadaEl").GetString());
    }

    [Fact]
    public async Task OtroDia_Renueva_YBorraLaAnterior()
    {
        var u = Usuario();
        var c = Conversaciones();
        var ayer = await c.ObtenerAsync(u, Hoy.AddDays(-1), nueva: false, Ct);
        var hoy = await c.ObtenerAsync(u, Hoy, nueva: false, Ct);
        Assert.NotEqual(ayer, hoy);
        Assert.Equal([ayer], _foundry.Borradas);
        Assert.Equal(hoy, (await Doc(u))!.Value.GetProperty("conversacionId").GetString());
    }

    [Fact]
    public async Task Nueva_Renueva_AunqueSeaDeHoy()
    {
        var u = Usuario();
        var c = Conversaciones();
        var primera = await c.ObtenerAsync(u, Hoy, nueva: false, Ct);
        var segunda = await c.ObtenerAsync(u, Hoy, nueva: true, Ct);
        Assert.NotEqual(primera, segunda);
        Assert.Equal([primera], _foundry.Borradas);
        Assert.Equal(segunda, await c.ObtenerAsync(u, Hoy, nueva: false, Ct));
    }

    [Fact]
    public async Task Aislamiento_BNoObtieneLaDeA()
    {
        var a = Usuario();
        var b = Usuario();
        var c = Conversaciones();
        var deA = await c.ObtenerAsync(a, Hoy, nueva: false, Ct);
        var deB = await c.ObtenerAsync(b, Hoy, nueva: false, Ct);
        Assert.NotEqual(deA, deB);
        Assert.Equal(deA, await c.ObtenerAsync(a, Hoy, nueva: false, Ct));
        Assert.Equal(deB, await c.ObtenerAsync(b, Hoy, nueva: false, Ct));
        // Invalidar la de B no toca el doc de A.
        await c.InvalidarAsync(b, deB, Ct);
        Assert.Equal(deA, (await Doc(a))!.Value.GetProperty("conversacionId").GetString());
        Assert.Null(await Doc(b));
    }

    static List<string?> Pendientes(JsonElement doc) =>
        doc.TryGetProperty("pendientes", out var p) && p.ValueKind == JsonValueKind.Array ? [.. p.EnumerateArray().Select(x => x.GetString())] : [];

    [Fact]
    public async Task Renovar_LaViejaQueNoSeBorra_QuedaPendiente_YSeReintenta()
    {
        var u = Usuario();
        var c = Conversaciones();
        var vieja = await c.ObtenerAsync(u, Hoy, nueva: false, Ct);
        _foundry.NoSeBorran[vieja] = 0;
        var segunda = await c.ObtenerAsync(u, Hoy, nueva: true, Ct);
        Assert.Equal([vieja], Pendientes((await Doc(u))!.Value));

        // Al renovar otra vez se reintentan la anterior y los pendientes; los que se borran salen del doc.
        _foundry.NoSeBorran.Clear();
        var tercera = await c.ObtenerAsync(u, Hoy.AddDays(1), nueva: false, Ct);
        var doc = (await Doc(u))!.Value;
        Assert.Equal(tercera, doc.GetProperty("conversacionId").GetString());
        Assert.Empty(Pendientes(doc));
        Assert.Equal([vieja, vieja, segunda], _foundry.Borradas.Order());
    }

    [Fact]
    public async Task Pendientes_ComoMuchoCinco_LasMasNuevas()
    {
        var u = Usuario();
        var c = Conversaciones();
        var ids = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            var id = await c.ObtenerAsync(u, Hoy, nueva: true, Ct);
            _foundry.NoSeBorran[id] = 0;
            ids.Add(id);
        }
        // Pendientes: las 5 anteriores a la vigente, de la más vieja a la más nueva.
        Assert.Equal(ids.Skip(2).Take(5), Pendientes((await Doc(u))!.Value));
    }

    [Fact]
    public async Task Invalidar_QueNoSeBorra_DejaElDocInvalidadoConPendiente_YLaSiguienteRenueva()
    {
        var u = Usuario();
        var c = Conversaciones();
        var rota = await c.ObtenerAsync(u, Hoy, nueva: false, Ct);
        _foundry.NoSeBorran[rota] = 0;
        await c.InvalidarAsync(u, rota, Ct);
        var doc = (await Doc(u))!.Value;
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("conversacionId").ValueKind);
        Assert.Equal([rota], Pendientes(doc));

        _foundry.NoSeBorran.Clear();
        var nueva = await c.ObtenerAsync(u, Hoy, nueva: false, Ct);
        Assert.NotEqual(rota, nueva);
        doc = (await Doc(u))!.Value;
        Assert.Equal(nueva, doc.GetProperty("conversacionId").GetString());
        Assert.Empty(Pendientes(doc));
    }

    [Fact]
    public async Task Invalidar_DeUnaVieja_ReintentaPendientesYConservaLaVigente()
    {
        var u = Usuario();
        var c = Conversaciones();
        var vieja = await c.ObtenerAsync(u, Hoy, nueva: false, Ct);
        _foundry.NoSeBorran[vieja] = 0;
        var vigente = await c.ObtenerAsync(u, Hoy, nueva: true, Ct);
        _foundry.NoSeBorran.Clear();
        await c.InvalidarAsync(u, vieja, Ct);
        var doc = (await Doc(u))!.Value;
        Assert.Equal(vigente, doc.GetProperty("conversacionId").GetString());
        Assert.Empty(Pendientes(doc));
    }

    [Fact]
    public async Task Invalidar_BorraDocYConversacion_ElSiguienteTurnoRenueva()
    {
        var u = Usuario();
        var c = Conversaciones();
        var id = await c.ObtenerAsync(u, Hoy, nueva: false, Ct);
        await c.InvalidarAsync(u, id, Ct);
        Assert.Null(await Doc(u));
        Assert.Equal([id], _foundry.Borradas);
        Assert.NotEqual(id, await c.ObtenerAsync(u, Hoy, nueva: false, Ct));
    }

    [Fact]
    public async Task Invalidar_DeUnaVieja_NoBorraElDocVigente()
    {
        var u = Usuario();
        var c = Conversaciones();
        var vieja = await c.ObtenerAsync(u, Hoy, nueva: false, Ct);
        var vigente = await c.ObtenerAsync(u, Hoy, nueva: true, Ct);
        await c.InvalidarAsync(u, vieja, Ct);
        Assert.Equal(vigente, (await Doc(u))!.Value.GetProperty("conversacionId").GetString());
    }

    [Fact]
    public async Task Invalidar_SinDocNiId_NoLanza()
    {
        var c = Conversaciones();
        await c.InvalidarAsync(Usuario(), "conv_x", Ct);
        await c.InvalidarAsync(Usuario(), null, Ct);
        Assert.Equal(["conv_x"], _foundry.Borradas);
    }

    [Fact]
    public async Task DocCorrupto_SeRenueva()
    {
        var u = Usuario();
        await Contenedor().UpsertItemAsync(new { id = ConversacionAgenteDoc.IdDoc, userId = u, tipo = ConversacionAgenteDoc.TipoDoc, dia = 5 },
            new PartitionKey(u), cancellationToken: Ct);
        var id = await Conversaciones().ObtenerAsync(u, Hoy, nueva: false, Ct);
        Assert.Equal(id, (await Doc(u))!.Value.GetProperty("conversacionId").GetString());
    }

    [Fact]
    public async Task Carrera_DosMensajesALaVez_UsanLaMismaYSeBorraLaPerdedora()
    {
        var u = Usuario();
        var c = Conversaciones();
        // Los dos leen "sin doc" antes de que alguno escriba: ambos crean una conversación en Foundry.
        var llegaron = 0;
        var ambos = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _foundry.AntesDeCrear = () =>
        {
            if (Interlocked.Increment(ref llegaron) == 2) ambos.SetResult();
            return ambos.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        };

        var ids = await Task.WhenAll(c.ObtenerAsync(u, Hoy, nueva: false, Ct), c.ObtenerAsync(u, Hoy, nueva: false, Ct));

        Assert.Equal(ids[0], ids[1]);
        Assert.Equal(2, _foundry.Creadas.Count);
        var perdedora = Assert.Single(_foundry.Borradas);
        Assert.NotEqual(ids[0], perdedora);
        Assert.Equal(ids[0], (await Doc(u))!.Value.GetProperty("conversacionId").GetString());
    }

    [Fact]
    public async Task ConsultaDeSeries_NoLevantaElDoc()
    {
        var u = Usuario();
        await Conversaciones().ObtenerAsync(u, Hoy, nueva: false, Ct);
        // La misma forma de filtro que usan Series, HerramientasCoach y PerfilesEntrenamiento.
        var consulta = new QueryDefinition("SELECT VALUE COUNT(1) FROM c WHERE c.userId = @userId AND c.tipo = 'serie'")
            .WithParameter("@userId", u);
        using var it = Contenedor().GetItemQueryIterator<int>(consulta,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(u) });
        Assert.Equal(0, (await it.ReadNextAsync(Ct)).Single());
    }
}
