using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Azure.Cosmos;

namespace GymCoach.Api.Tests;

/// <summary>Doc del perfil de entrenamiento en memoria: cuenta lecturas, consultas, escrituras y marcas.</summary>
public sealed class PerfilesEntrenamientoFalsos
{
    public PerfilEntrenamientoDoc? Doc;
    public string? ETag;
    /// <summary>El doc existe pero no pasa la revalidación (llega como null con ETag).</summary>
    public bool Corrupto;
    public List<Serie> Series = [];
    public int Consultas;
    public readonly List<(PerfilEntrenamientoDoc Doc, string? ETag)> Guardados = [];
    public readonly List<string> Leidos = [];
    /// <summary>Cuántas escrituras más fallan como si otro hubiera escrito primero (412).</summary>
    public int ConflictosPendientes;
    public Exception? FallaLeer, FallaConsulta, FallaGuardar, FallaMarcar;
    public bool HayCupo = true;
    public int CuposPedidos;
    public int Marcas;
    int _version;

    public PerfilesEntrenamiento Crear(TimeProvider reloj) => new(
        (userId, _) =>
        {
            Leidos.Add(userId);
            if (FallaLeer is not null) throw FallaLeer;
            return Task.FromResult(Corrupto ? ((PerfilEntrenamientoDoc?)null, (string?)"e-corrupto") : (Doc, ETag));
        },
        (_, _) =>
        {
            Consultas++;
            if (FallaConsulta is not null) throw FallaConsulta;
            return Task.FromResult<IReadOnlyList<Serie>>(Series);
        },
        (doc, etag, _) =>
        {
            Guardados.Add((doc, etag));
            if (FallaGuardar is not null) throw FallaGuardar;
            if (ConflictosPendientes > 0)
            {
                ConflictosPendientes--;
                return Task.FromResult(false);
            }
            (Doc, ETag, Corrupto) = (doc, "e" + ++_version, false);
            return Task.FromResult(true);
        },
        (_, _) =>
        {
            Marcas++;
            return FallaMarcar is null ? Task.CompletedTask : Task.FromException(FallaMarcar);
        },
        _ =>
        {
            CuposPedidos++;
            return HayCupo;
        },
        reloj);
}

public class PerfilesEntrenamientoTests
{
    const string Usuario = "google:123";
    static readonly DateOnly Hoy = new(2026, 10, 14);
    readonly RelojFalso _reloj = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    readonly PerfilesEntrenamientoFalsos _falsos = new();
    readonly PerfilesEntrenamiento _perfiles;

    public PerfilesEntrenamientoTests() => _perfiles = _falsos.Crear(_reloj);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Una serie por día, n días hacia atrás desde hoy (13:05Z).</summary>
    static List<Serie> Series(int dias, string ejercicio = "press-banca") =>
        [.. Enumerable.Range(0, dias).Select(i =>
        {
            var t = Hoy.AddDays(-i).ToDateTime(new TimeOnly(13, 5), DateTimeKind.Utc);
            return new Serie(Guid.NewGuid().ToString("D"), ejercicio, 50, 8, t.ToString("yyyy-MM-dd"),
                t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        })];

    PerfilEntrenamientoDoc DocDe(DateOnly hoy, int? offsetMin = null, bool desactualizado = false, int version = CalculosPerfil.Version) =>
        new(PerfilEntrenamientoDoc.IdDoc, Usuario, PerfilEntrenamientoDoc.TipoDoc, offsetMin,
            CalculosPerfil.Calcular(Series(1), hoy, offsetMin, _reloj.GetUtcNow(), false) with { Version = version }, desactualizado);

    void Sembrar(PerfilEntrenamientoDoc doc)
    {
        _falsos.Doc = doc;
        _falsos.ETag = "e-viejo";
    }

    [Fact]
    public async Task SinDoc_CalculaYCrea()
    {
        _falsos.Series = Series(3);
        var p = await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct);

        Assert.Equal(3, p!.SeriesAnalizadas);
        Assert.Equal(1, _falsos.Consultas);
        var (doc, etag) = Assert.Single(_falsos.Guardados);
        Assert.Null(etag);
        Assert.Equal((PerfilEntrenamientoDoc.IdDoc, Usuario, PerfilEntrenamientoDoc.TipoDoc), (doc.Id, doc.UserId, doc.Tipo));
        Assert.Same(p, doc.Perfil);
        Assert.False(doc.Desactualizado);
        Assert.Equal([Usuario], _falsos.Leidos);
    }

    [Fact]
    public async Task Vigente_CeroConsultas()
    {
        _falsos.Series = Series(3);
        await _perfiles.ObtenerAsync(Usuario, Hoy, -360, Ct);
        var p = await _perfiles.ObtenerAsync(Usuario, Hoy, -360, Ct);

        Assert.Same(_falsos.Doc!.Perfil, p);
        Assert.Equal(1, _falsos.Consultas);
        Assert.Single(_falsos.Guardados);
        // Ya vigente: ni siquiera se pide cupo.
        Assert.Equal(1, _falsos.CuposPedidos);
    }

    [Fact]
    public async Task OtroHoy_Recalcula()
    {
        Sembrar(DocDe(Hoy.AddDays(-1)));
        var p = await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct);
        Assert.Equal("2026-10-14", p!.Hoy);
        Assert.Equal(1, _falsos.Consultas);
        Assert.Equal("e-viejo", Assert.Single(_falsos.Guardados).ETag);
    }

    [Fact]
    public async Task Desactualizado_Recalcula_YElReplaceBorraLaMarca()
    {
        Sembrar(DocDe(Hoy, desactualizado: true));
        _falsos.Series = Series(5);
        Assert.Equal(5, (await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct))!.SeriesAnalizadas);
        var (doc, etag) = Assert.Single(_falsos.Guardados);
        Assert.Equal("e-viejo", etag);
        Assert.False(doc.Desactualizado);
    }

    [Fact]
    public async Task OtraVersion_Recalcula()
    {
        Sembrar(DocDe(Hoy, version: CalculosPerfil.Version - 1));
        Assert.Equal(CalculosPerfil.Version, (await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct))!.Version);
        Assert.Equal(1, _falsos.Consultas);
    }

    [Fact]
    public async Task Corrupto_Recalcula_YReemplazaConSuETag()
    {
        _falsos.Corrupto = true;
        _falsos.Series = Series(2);
        Assert.Equal(2, (await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct))!.SeriesAnalizadas);
        Assert.Equal("e-corrupto", Assert.Single(_falsos.Guardados).ETag);
    }

    [Fact]
    public async Task Conflicto412AlGuardar_DevuelveLoCalculado_SinReintentar()
    {
        Sembrar(DocDe(Hoy, desactualizado: true));
        _falsos.ConflictosPendientes = 1;
        _falsos.Series = Series(4);

        Assert.Equal(4, (await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct))!.SeriesAnalizadas);
        Assert.Single(_falsos.Guardados);
        Assert.Equal(1, _falsos.Consultas);
        // El doc del otro escritor quedó (sigue marcado): el siguiente read recalcula.
        Assert.True(_falsos.Doc!.Desactualizado);
    }

    [Fact]
    public async Task ErrorTransitorioAlGuardar_DevuelveLoCalculado()
    {
        _falsos.FallaGuardar = new CosmosException("saturado", HttpStatusCode.TooManyRequests, 0, "", 0);
        _falsos.Series = Series(2);
        Assert.Equal(2, (await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct))!.SeriesAnalizadas);
    }

    [Fact]
    public async Task SinCupo_DevuelveElViejoSinConsultar()
    {
        var viejo = DocDe(Hoy.AddDays(-1));
        Sembrar(viejo);
        _falsos.HayCupo = false;

        Assert.Same(viejo.Perfil, await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct));
        Assert.Equal(0, _falsos.Consultas);
        Assert.Empty(_falsos.Guardados);
    }

    [Fact]
    public async Task SinCupo_SinDocOCorrupto_Null()
    {
        _falsos.HayCupo = false;
        Assert.Null(await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct));
        _falsos.Corrupto = true;
        Assert.Null(await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct));
        Assert.Equal(0, _falsos.Consultas);
    }

    [Fact]
    public async Task ConsultaSaturada_ConDocViejo_LoDevuelve_SinDoc_Propaga()
    {
        _falsos.FallaConsulta = new CosmosException("saturado", HttpStatusCode.TooManyRequests, 0, "", 0);
        await Assert.ThrowsAsync<CosmosException>(() => _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct));

        var viejo = DocDe(Hoy.AddDays(-1));
        Sembrar(viejo);
        Assert.Same(viejo.Perfil, await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct));
        Assert.Empty(_falsos.Guardados);
    }

    [Fact]
    public async Task LecturaSaturada_Propaga()
    {
        _falsos.FallaLeer = new CosmosException("saturado", HttpStatusCode.ServiceUnavailable, 0, "", 0);
        await Assert.ThrowsAsync<CosmosException>(() => _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct));
    }

    [Fact]
    public async Task OffsetMin_ElDelRequestGana_SinElSeUsaElGuardado()
    {
        _falsos.Series = Series(3);
        var p = await _perfiles.ObtenerAsync(Usuario, Hoy, -360, Ct);
        Assert.Equal("07:00", p!.Habitos!.Horario!.HoraTipica);
        Assert.Equal(-360, _falsos.Doc!.OffsetMin);

        // Al otro día, sin offset en el request: se recalcula con el guardado.
        p = await _perfiles.ObtenerAsync(Usuario, Hoy.AddDays(1), null, Ct);
        Assert.Equal("07:00", p!.Habitos!.Horario!.HoraTipica);
        Assert.Equal(-360, _falsos.Doc!.OffsetMin);

        // Con otro offset en el request (otro día): ese gana y queda guardado.
        p = await _perfiles.ObtenerAsync(Usuario, Hoy.AddDays(2), 60, Ct);
        Assert.Equal("14:00", p!.Habitos!.Horario!.HoraTipica);
        Assert.Equal(60, _falsos.Doc!.OffsetMin);
    }

    [Fact]
    public async Task OffsetMinDistintoDelGuardado_Recalcula_SinOffsetNo()
    {
        _falsos.Series = Series(3);
        // El primero sin offset (p. ej. un turno del coach): horario null.
        Assert.Null((await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct))!.Habitos!.Horario);
        // Llega el offset: no espera al día siguiente.
        Assert.Equal("07:00", (await _perfiles.ObtenerAsync(Usuario, Hoy, -360, Ct))!.Habitos!.Horario!.HoraTipica);
        Assert.Equal(2, _falsos.Consultas);

        // Mismo offset, o sin offset: vigente.
        await _perfiles.ObtenerAsync(Usuario, Hoy, -360, Ct);
        await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct);
        Assert.Equal(2, _falsos.Consultas);

        // Otro offset: recalcula (sujeto al cupo; sin cupo, el guardado).
        _falsos.HayCupo = false;
        Assert.Equal("07:00", (await _perfiles.ObtenerAsync(Usuario, Hoy, 60, Ct))!.Habitos!.Horario!.HoraTipica);
        _falsos.HayCupo = true;
        Assert.Equal("14:00", (await _perfiles.ObtenerAsync(Usuario, Hoy, 60, Ct))!.Habitos!.Horario!.HoraTipica);
        Assert.Equal(3, _falsos.Consultas);
    }

    [Fact]
    public async Task DocDeManana_Vigente_DeAyerOPasadoManana_Recalcula()
    {
        // Otro dispositivo adelantado a UTC ya calculó con mañana: sin ping-pong cerca de medianoche.
        Sembrar(DocDe(Hoy.AddDays(1)));
        await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct);
        Assert.Equal(0, _falsos.Consultas);

        Sembrar(DocDe(Hoy.AddDays(2)));
        await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct);
        Assert.Equal(1, _falsos.Consultas);

        Sembrar(DocDe(Hoy.AddDays(-1)));
        await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct);
        Assert.Equal(2, _falsos.Consultas);
    }

    [Fact]
    public async Task MasDe3000Series_Truncado_SeQuedanLasMasRecientes()
    {
        _falsos.Series = Series(PerfilesEntrenamiento.MaxSeries + 1);
        // Como las devuelve Cosmos (ts DESC), pero desordenadas: el corte no depende del orden de llegada.
        _falsos.Series.Reverse();
        var p = await _perfiles.ObtenerAsync(Usuario, Hoy, null, Ct);

        Assert.True(p!.Truncado);
        Assert.Equal(PerfilesEntrenamiento.MaxSeries, p.SeriesAnalizadas);
        Assert.Equal(Hoy.AddDays(-(PerfilesEntrenamiento.MaxSeries - 1)).ToString("yyyy-MM-dd"), p.PrimerEntreno);

        _falsos.Series = Series(PerfilesEntrenamiento.MaxSeries);
        Assert.False((await _perfiles.ObtenerAsync(Usuario, Hoy.AddDays(1), null, Ct))!.Truncado);
    }

    [Fact]
    public async Task Marcar_404Y412SeIgnoran_ElRestoSePropaga()
    {
        _falsos.FallaMarcar = new CosmosException("no existe", HttpStatusCode.NotFound, 0, "", 0);
        await _perfiles.MarcarDesactualizadoAsync(Usuario, Ct);
        _falsos.FallaMarcar = new CosmosException("ya marcado", HttpStatusCode.PreconditionFailed, 0, "", 0);
        await _perfiles.MarcarDesactualizadoAsync(Usuario, Ct);
        Assert.Equal(2, _falsos.Marcas);

        _falsos.FallaMarcar = new CosmosException("saturado", HttpStatusCode.TooManyRequests, 0, "", 0);
        await Assert.ThrowsAsync<CosmosException>(() => _perfiles.MarcarDesactualizadoAsync(Usuario, Ct));
    }

    // --- Revalidación del doc leído (lo que haría el delegado real con el JSON de Cosmos) ---

    /// <summary>El doc como lo escribe el cliente de Cosmos (Web, sin ignorar nulls) más los campos de sistema.</summary>
    JsonObject Json(PerfilEntrenamientoDoc? doc = null)
    {
        var json = JsonSerializer.SerializeToNode(doc ?? DocDe(Hoy, -360), new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        json["_etag"] = "\"e1\"";
        json["_ts"] = 1;
        return json;
    }

    [Fact]
    public void Interpretar_DocValido_IdaYVuelta()
    {
        var original = DocDe(Hoy, -360);
        var doc = PerfilesEntrenamiento.Interpretar(Json(original).ToJsonString(), Usuario);
        Assert.NotNull(doc);
        Assert.Equal(-360, doc.OffsetMin);
        Assert.False(doc.Desactualizado);
        Assert.Equal(JsonSerializer.Serialize(original.Perfil), JsonSerializer.Serialize(doc.Perfil));

        var marcado = Json(original);
        marcado["desactualizado"] = true;
        Assert.True(PerfilesEntrenamiento.Interpretar(marcado.ToJsonString(), Usuario)!.Desactualizado);
    }

    [Fact]
    public void Interpretar_DocCorruptoODeOtroEsquema_Null()
    {
        Assert.Null(PerfilesEntrenamiento.Interpretar("{no es json", Usuario));
        Assert.Null(PerfilesEntrenamiento.Interpretar("[]", Usuario));
        Assert.Null(PerfilesEntrenamiento.Interpretar(Json().ToJsonString(), "google:otro"));

        void Nulo(Action<JsonObject> cambiar)
        {
            var json = Json();
            cambiar(json);
            Assert.Null(PerfilesEntrenamiento.Interpretar(json.ToJsonString(), Usuario));
        }
        Nulo(j => j["tipo"] = "serie");
        Nulo(j => j["id"] = "otro");
        Nulo(j => j["perfil"] = null);
        Nulo(j => j.Remove("perfil"));
        Nulo(j => j.Remove("offsetMin"));
        Nulo(j => j["offsetMin"] = 9999);
        Nulo(j => j["offsetMin"] = "60");
        Nulo(j => j["perfil"]!["hoy"] = null);
        Nulo(j => j["perfil"]!["equipo"] = null);
        Nulo(j => j["perfil"]!.AsObject().Remove("ejercicios"));
        Nulo(j => j["perfil"]!["ejercicios"]!.AsArray().Add(null));
        Nulo(j => j["perfil"]!["principales"]!.AsArray().Add(null));
        Nulo(j => j["perfil"]!["ejercicios"]![0]!["ultimaSerie"]!["fecha"] = null);
        Nulo(j => j["perfil"]!["habitos"]!["division"]!.AsArray().Add(null));
        Nulo(j => j["perfil"]!["habitos"]!["rutinasFrecuentes"]!.AsArray().Add(null));
        Nulo(j => j["perfil"]!["seriesAnalizadas"] = "3");
    }
}
