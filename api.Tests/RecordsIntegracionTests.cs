using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GymCoach.Api.Functions;
using Microsoft.Azure.Cosmos;

namespace GymCoach.Api.Tests;

/// <summary>
/// Handler del SDK que responde 503 a las peticiones que cumplan el predicado. Va antes de los reintentos del SDK,
/// así la falla llega directo al código. El mensaje lleva un marcador para comprobar que nunca se loguea.
/// </summary>
public sealed class Fallas : RequestHandler
{
    public const string Marcador = "MENSAJE-INTERNO-DE-COSMOS";
    public Func<RequestMessage, bool> Fallar { get; set; } = _ => false;

    public override async Task<ResponseMessage> SendAsync(RequestMessage request, CancellationToken cancellationToken) =>
        Fallar(request) ? new ResponseMessage(HttpStatusCode.ServiceUnavailable, request, Marcador)
            : await base.SendAsync(request, cancellationToken);

    public static bool EsRecord(RequestMessage r) =>
        Uri.UnescapeDataString(r.RequestUri.OriginalString).Contains(RecordDoc.Prefijo, StringComparison.Ordinal);
}

[Collection(ColeccionCosmos.Nombre)]
public class RecordsIntegracionTests(CosmosFixture cosmos)
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

    readonly LoggerFalso<Series> _logSeries = new();
    readonly LoggerFalso<Records> _logRecords = new();

    Series HandlersSeries(Container? c = null) => new(_tokens.Autenticador(), c ?? Contenedor(), _limites, Reloj, _logSeries);
    Records Handlers() => new(_tokens.Autenticador(), Contenedor(), _limites, Reloj, _logRecords);
    Ejercicios HandlersEjercicios() => new(_tokens.Autenticador(), Contenedor(), _limites);

    string Bearer(string sub) => "Bearer " + _tokens.Crear(sub: sub);

    // ts distintos y crecientes por minuto: el recálculo ordena por ts.
    static string Body(decimal peso, int reps = 5, string? tipoSerie = null, string ejercicio = "press-banca", int minuto = 0,
        string? fecha = null) =>
        JsonSerializer.Serialize(new
        {
            ejercicioId = ejercicio, pesoKg = peso, reps, fecha = fecha ?? Hoy, ts = Ahora.AddMinutes(-30 + minuto).ToString("o"), tipoSerie,
        });

    async Task<ResponseFalsa> Put(string sub, string id, string body, Container? c = null) =>
        (ResponseFalsa)await HandlersSeries(c).Guardar(new RequestFalso("PUT", "/api/series/" + id, Bearer(sub), body), id);

    async Task<ResponseFalsa> Borrar(string sub, string id, Container? c = null) =>
        (ResponseFalsa)await HandlersSeries(c).Borrar(new RequestFalso("DELETE", "/api/series/" + id, Bearer(sub)), id);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>El doc de récord tal cual está en Cosmos (null si no existe).</summary>
    async Task<JsonObject?> DocRecord(string sub, string ejercicio = "press-banca")
    {
        using var res = await Contenedor().ReadItemStreamAsync(RecordDoc.IdDoc(ejercicio), new PartitionKey("google:" + sub),
            cancellationToken: Ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        return JsonNode.Parse(res.Content)!.AsObject();
    }

    async Task InsertarSerie(string sub, decimal peso, int reps, int minuto = -40, string ejercicio = "press-banca")
    {
        var userId = "google:" + sub;
        var s = new Serie(Nuevo(), ejercicio, peso, reps, Hoy,
            Ahora.AddMinutes(minuto).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        await Contenedor().CreateItemAsync(SerieDoc.De(s, userId), new PartitionKey(userId), cancellationToken: Ct);
    }

    void AgotarRecalculos(string sub)
    {
        while (_limites.Recalculos.Permitir("google:" + sub, out _)) { }
    }

    // Ningún log lleva el mensaje de Cosmos (trae la partición), el sub ni el userId.
    void LogsLimpios(string sub)
    {
        var textos = _logSeries.Mensajes.Concat(_logRecords.Mensajes).Select(m => m.Texto).ToList();
        Assert.All(textos, t =>
        {
            Assert.DoesNotContain(Fallas.Marcador, t);
            Assert.DoesNotContain(sub, t);
            Assert.DoesNotContain("google:", t);
        });
    }

    async Task<List<RecordEjercicio>> Listar(string sub)
    {
        var res = (ResponseFalsa)await Handlers().Listar(new RequestFalso("GET", "/api/records", Bearer(sub)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonSerializer.Deserialize<List<RecordEjercicio>>(res.Texto(), Http.Json)!;
    }

    async Task<RecordEjercicio?> Record(string sub, string ejercicio = "press-banca") =>
        (await Listar(sub)).SingleOrDefault(r => r.EjercicioId == ejercicio);

    static string Nuevo() => Guid.NewGuid().ToString();

    [Fact]
    public async Task SubirPeso_ActualizaElRecord_YGetDevuelveElContrato()
    {
        var sub = Nuevo();
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, Nuevo(), Body(80, 5))).StatusCode);
        Assert.Equal(80m, (await Record(sub))!.PesoMax.PesoKg);

        await Put(sub, Nuevo(), Body(85, 3, minuto: 1));
        var r = (await Record(sub))!;
        Assert.Equal(new MarcaPeso(85, 3, Hoy), r.PesoMax);
        // 85x3 = 93.5 bate al e1rm de 80x5 = 93.33; repsMax sigue en la de 80.
        Assert.Equal(new MarcaE1rm(93.5m, 85, 3, Hoy), r.E1rm);
        Assert.Equal(new MarcaReps(5, 80, Hoy), r.RepsMax);

        var res = (ResponseFalsa)await Handlers().Listar(new RequestFalso("GET", "/api/records", Bearer(sub)));
        var obj = Assert.Single(JsonNode.Parse(res.Texto())!.AsArray())!.AsObject();
        Assert.Equal(["ejercicioId", "pesoMax", "e1rm", "repsMax"], obj.Select(p => p.Key));
    }

    // Carga corporal sin lastre: e1rm null y el campo viaja igual (contrato objeto | null).
    [Fact]
    public async Task CargaCorporal_E1rmNullVisible()
    {
        var sub = Nuevo();
        await Put(sub, Nuevo(), Body(0, 10, ejercicio: "dominadas"));
        var res = (ResponseFalsa)await Handlers().Listar(new RequestFalso("GET", "/api/records", Bearer(sub)));
        Assert.Contains("\"e1rm\":null", res.Texto());
        Assert.Equal(10, (await Record(sub, "dominadas"))!.RepsMax.Reps);
    }

    [Fact]
    public async Task CalentamientoMasPesado_NoCambiaElRecord()
    {
        var sub = Nuevo();
        await Put(sub, Nuevo(), Body(80, 5));
        var antes = await Record(sub);
        await Put(sub, Nuevo(), Body(120, 8, "calentamiento", minuto: 1));
        Assert.Equal(antes, await Record(sub));
    }

    // Un documento previo a tipoSerie (sin el campo) cuenta como normal en el recálculo.
    [Fact]
    public async Task DocViejoSinTipoSerie_CuentaComoNormal()
    {
        var sub = Nuevo();
        var userId = "google:" + sub; // el mismo userId que saca ValidadorGoogle del token
        var viejo = new JsonObject
        {
            ["id"] = Nuevo(), ["userId"] = userId, ["tipo"] = "serie", ["ejercicioId"] = "press-banca", ["pesoKg"] = 70,
            ["reps"] = 5, ["fecha"] = Hoy, ["ts"] = Ahora.AddMinutes(-40).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
        };
        await Contenedor().CreateItemAsync(viejo, new PartitionKey(userId),
            cancellationToken: TestContext.Current.CancellationToken);

        var dueña = Nuevo();
        await Put(sub, dueña, Body(80, 5));
        Assert.Equal(HttpStatusCode.NoContent, (await Borrar(sub, dueña)).StatusCode);
        Assert.Equal(70m, (await Record(sub))!.PesoMax.PesoKg);
    }

    [Fact]
    public async Task BorrarLaDueña_BajaAlSegundo_YBorrarLaUltimaBorraElDoc()
    {
        var sub = Nuevo();
        string a = Nuevo(), b = Nuevo(), c = Nuevo();
        await Put(sub, a, Body(70, 5));
        await Put(sub, b, Body(80, 5, minuto: 1));
        await Put(sub, c, Body(90, 5, "calentamiento", minuto: 2));

        await Borrar(sub, b);
        var r = (await Record(sub))!;
        Assert.Equal(70m, r.PesoMax.PesoKg);
        Assert.Equal(new MarcaE1rm(81.67m, 70, 5, Hoy), r.E1rm);

        // Borrar un calentamiento no toca el récord; borrar la última serie que cuenta borra el doc.
        await Borrar(sub, c);
        Assert.Equal(r, await Record(sub));
        await Borrar(sub, a);
        Assert.Empty(await Listar(sub));
    }

    [Fact]
    public async Task EditarLaDueña_Recalcula()
    {
        var sub = Nuevo();
        string a = Nuevo(), b = Nuevo();
        await Put(sub, a, Body(70, 5));
        await Put(sub, b, Body(80, 5, minuto: 1));

        // A calentamiento: el récord baja a 70.
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, b, Body(80, 5, "calentamiento", minuto: 1))).StatusCode);
        Assert.Equal(70m, (await Record(sub))!.PesoMax.PesoKg);

        // De vuelta a normal pero con menos peso: queda 70 (el empate no aplica, 65 < 70).
        await Put(sub, b, Body(65, 5, minuto: 1));
        Assert.Equal(70m, (await Record(sub))!.PesoMax.PesoKg);

        // Cambiar de ejercicio: el récord se va con la serie.
        await Put(sub, a, Body(70, 5, ejercicio: "sentadilla"));
        Assert.Equal(65m, (await Record(sub))!.PesoMax.PesoKg);
        Assert.Equal(70m, (await Record(sub, "sentadilla"))!.PesoMax.PesoKg);
    }

    // Series de antes de los récords: no hay doc, y el primer PUT lo arma con todo el historial.
    [Fact]
    public async Task HistorialPrevioSinDoc_ElPrimerPutUsaElHistorial()
    {
        var sub = Nuevo();
        var userId = "google:" + sub;
        var previa = new Serie(Nuevo(), "press-banca", 100, 5, Hoy, Ahora.AddMinutes(-40).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        await Contenedor().CreateItemAsync(SerieDoc.De(previa, userId), new PartitionKey(userId),
            cancellationToken: TestContext.Current.CancellationToken);

        await Put(sub, Nuevo(), Body(60, 8));
        var r = (await Record(sub))!;
        Assert.Equal(new MarcaPeso(100, 5, Hoy), r.PesoMax);
        Assert.Equal(new MarcaReps(8, 60, Hoy), r.RepsMax);
    }

    [Fact]
    public async Task SinHistorial_ElPrimerPutCreaElRecordConEsaSerie()
    {
        var sub = Nuevo();
        await Put(sub, Nuevo(), Body(60, 8));
        Assert.Equal(new RecordEjercicio("press-banca", new(60, 8, Hoy), new(76, 60, 8, Hoy), new(8, 60, Hoy)), await Record(sub));
    }

    // El cliente reenvía series con Promise.all: sobre un ejercicio sin doc, los recálculos y fusiones
    // en paralelo (ETag + reintentos) deben dejar el máximo.
    [Fact]
    public async Task PutsConcurrentes_DejanElMaximo()
    {
        var sub = Nuevo();
        var res = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Put(sub, Nuevo(), Body(60 + i, 5, minuto: i))));
        Assert.All(res, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var record = (await Record(sub))!;
        Assert.Equal(69m, record.PesoMax.PesoKg);
        Assert.Equal(69m, record.E1rm!.PesoKg);
    }

    [Fact]
    public async Task RepetirPut_EsIdempotente()
    {
        var sub = Nuevo();
        var id = Nuevo();
        var body = Body(80, 5);
        await Put(sub, id, body);
        var etag = (await Contenedor().ReadItemAsync<RecordDoc>(RecordDoc.IdDoc("press-banca"), new PartitionKey("google:" + sub),
            cancellationToken: TestContext.Current.CancellationToken)).ETag;

        await Put(sub, id, body);
        var despues = await Contenedor().ReadItemAsync<RecordDoc>(RecordDoc.IdDoc("press-banca"), new PartitionKey("google:" + sub),
            cancellationToken: TestContext.Current.CancellationToken);
        // Mismo ETag: el segundo PUT no reescribió el récord.
        Assert.Equal(etag, despues.ETag);
        Assert.Equal(80m, despues.Resource.PesoMax.PesoKg);
    }

    [Fact]
    public async Task ListarYHistorial_TipoSerieSoloSiNoEsNormal()
    {
        var sub = Nuevo();
        await Put(sub, Nuevo(), Body(40, 10, "calentamiento"));
        await Put(sub, Nuevo(), Body(80, 5, minuto: 1));

        var res = (ResponseFalsa)await HandlersSeries().Listar(new RequestFalso("GET", $"/api/series?desde={Hoy}&hasta={Hoy}", Bearer(sub)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var series = JsonNode.Parse(res.Texto())!.AsArray();
        Assert.Equal("calentamiento", (string)series[0]!["tipoSerie"]!);
        Assert.DoesNotContain("tipoSerie", series[1]!.AsObject().Select(p => p.Key));

        var hist = (ResponseFalsa)await HandlersEjercicios().Historial(
            new RequestFalso("GET", "/api/ejercicios/press-banca/historial", Bearer(sub)), "press-banca");
        var historial = JsonNode.Parse(hist.Texto())!.AsArray();
        Assert.Equal("calentamiento", (string)historial[1]!["tipoSerie"]!);
        Assert.DoesNotContain("tipoSerie", historial[0]!.AsObject().Select(p => p.Key));
    }

    // Recalcular cuesta una consulta: sin cupo de recálculos, 429 y la serie no se borra.
    [Fact]
    public async Task BorrarLaDueña_SinCupoDeRecalculos_429YLaSerieSigue()
    {
        var sub = Nuevo();
        var id = Nuevo();
        await Put(sub, id, Body(80, 5));
        AgotarRecalculos(sub);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Borrar(sub, id)).StatusCode);

        var hist = (ResponseFalsa)await HandlersEjercicios().Historial(
            new RequestFalso("GET", "/api/ejercicios/press-banca/historial", Bearer(sub)), "press-banca");
        Assert.Equal(id, (string)Assert.Single(JsonNode.Parse(hist.Texto())!.AsArray())!["id"]!);
        Assert.Equal(80m, (await Record(sub))!.PesoMax.PesoKg);
    }

    [Fact]
    public async Task EditarLaDueña_SinCupoDeRecalculos_429SinModificar()
    {
        var sub = Nuevo();
        var id = Nuevo();
        await Put(sub, id, Body(80, 5));
        AgotarRecalculos(sub);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Put(sub, id, Body(60, 5))).StatusCode);
        // Subir peso no necesita recálculo: no pide cupo.
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, Nuevo(), Body(90, 5, minuto: 1))).StatusCode);

        var hist = (ResponseFalsa)await HandlersEjercicios().Historial(
            new RequestFalso("GET", "/api/ejercicios/press-banca/historial", Bearer(sub)), "press-banca");
        Assert.Equal([90m, 80m], JsonSerializer.Deserialize<List<Serie>>(hist.Texto(), Http.Json)!.Select(s => s.PesoKg));
    }

    [Fact]
    public async Task Aislamiento_RecordsPorUsuario()
    {
        string a = Nuevo(), b = Nuevo();
        await Put(a, Nuevo(), Body(80, 5));
        Assert.Empty(await Listar(b));
    }

    // G2: armar el récord desde el historial es una consulta: sin cupo de recálculos, 429 y la serie no se guarda.
    [Fact]
    public async Task SinDoc_SinCupoDeRangos_429SinEscribir()
    {
        var sub = Nuevo();
        AgotarRecalculos(sub);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Put(sub, Nuevo(), Body(80, 5))).StatusCode);

        // Un calentamiento no arma récord: no necesita cupo.
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, Nuevo(), Body(40, 10, "calentamiento"))).StatusCode);
        var hist = (ResponseFalsa)await HandlersEjercicios().Historial(
            new RequestFalso("GET", "/api/ejercicios/press-banca/historial", Bearer(sub)), "press-banca");
        Assert.Equal([40m], JsonSerializer.Deserialize<List<Serie>>(hist.Texto(), Http.Json)!.Select(s => s.PesoKg));
        Assert.Null(await DocRecord(sub));
    }

    // Con el doc ya creado, sumar series no gasta cupo de recálculos.
    [Fact]
    public async Task ConDoc_SubirPesoNoGastaRecalculos()
    {
        var sub = Nuevo();
        await Put(sub, Nuevo(), Body(80, 5));
        AgotarRecalculos(sub);
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, Nuevo(), Body(90, 5, minuto: 1))).StatusCode);
        Assert.Equal(90m, (decimal)(await DocRecord(sub))!["pesoMax"]!["pesoKg"]!);
    }

    // R4: la marca guarda la fecha; editar solo la fecha de la dueña la mueve.
    [Fact]
    public async Task EditarFechaDeLaDueña_ActualizaLaFecha()
    {
        var sub = Nuevo();
        var id = Nuevo();
        await Put(sub, id, Body(80, 5));
        var ayer = Ahora.UtcDateTime.AddDays(-1).ToString("yyyy-MM-dd");
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, id, Body(80, 5, fecha: ayer))).StatusCode);
        var r = (await Record(sub))!;
        Assert.Equal(ayer, r.PesoMax.Fecha);
        Assert.Equal(ayer, r.RepsMax.Fecha);
    }

    public static TheoryData<string> DocsCorruptos => new()
    {
        """{"pesoMax":null,"repsMax":{"reps":5,"pesoKg":100,"fecha":"2026-01-01"}}""",
        """{"pesoMax":"x","repsMax":{"reps":5,"pesoKg":100,"fecha":"2026-01-01"}}""",
        """{"pesoMax":{"pesoKg":100,"reps":5,"fecha":"2026-01-01"}}""",
        """{"pesoMax":{"pesoKg":100,"reps":5},"repsMax":{"reps":5,"pesoKg":100,"fecha":"2026-01-01"}}""",
    };

    async Task InsertarCorrupto(string sub, string campos)
    {
        var userId = "google:" + sub;
        var doc = JsonNode.Parse(campos)!.AsObject();
        doc["id"] = RecordDoc.IdDoc("press-banca");
        doc["userId"] = userId;
        doc["tipo"] = "record";
        doc["ejercicioId"] = "press-banca";
        await Contenedor().CreateItemAsync(doc, new PartitionKey(userId), cancellationToken: Ct);
    }

    // R3: un doc inválido se trata como "hay que recalcular": ni PUT ni DELETE ni GET revientan.
    [Theory]
    [MemberData(nameof(DocsCorruptos))]
    public async Task DocCorrupto_SeRecalculaSinFallar(string campos)
    {
        var sub = Nuevo();
        await InsertarSerie(sub, 100, 5);
        await InsertarCorrupto(sub, campos);

        // GET: lo recalcula.
        Assert.Equal(new MarcaPeso(100, 5, Hoy), (await Record(sub))!.PesoMax);

        // PUT y DELETE con el doc corrupto otra vez.
        await Contenedor().DeleteItemAsync<JsonObject>(RecordDoc.IdDoc("press-banca"), new PartitionKey("google:" + sub), cancellationToken: Ct);
        await InsertarCorrupto(sub, campos);
        var id = Nuevo();
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, id, Body(60, 8))).StatusCode);
        Assert.Equal(100m, (decimal)(await DocRecord(sub))!["pesoMax"]!["pesoKg"]!);

        await Contenedor().DeleteItemAsync<JsonObject>(RecordDoc.IdDoc("press-banca"), new PartitionKey("google:" + sub), cancellationToken: Ct);
        await InsertarCorrupto(sub, campos);
        Assert.Equal(HttpStatusCode.NoContent, (await Borrar(sub, id)).StatusCode);
        Assert.Equal(new MarcaReps(5, 100, Hoy), (await Record(sub))!.RepsMax);
    }

    // R1 + R5: falla el reemplazo del récord. La serie se guarda (200), el doc queda marcado y el GET lo repara.
    [Fact]
    public async Task FallaElReemplazo_MarcaYElGetLoRepara()
    {
        var sub = Nuevo();
        await Put(sub, Nuevo(), Body(80, 5));
        var fallas = new Fallas { Fallar = r => r.Method == HttpMethod.Put && Fallas.EsRecord(r) };
        var conFallas = cosmos.ConHandler(fallas);

        Assert.Equal(HttpStatusCode.OK, (await Put(sub, Nuevo(), Body(90, 5, minuto: 1), conFallas)).StatusCode);
        var doc = (await DocRecord(sub))!;
        Assert.True((bool)doc["desactualizado"]!);
        Assert.Equal(80m, (decimal)doc["pesoMax"]!["pesoKg"]!);
        Assert.NotEmpty(_logSeries.Mensajes);
        LogsLimpios(sub);

        Assert.Equal(90m, (await Record(sub))!.PesoMax.PesoKg);
        Assert.False((await DocRecord(sub))!.ContainsKey("desactualizado"));
    }

    // R1: la siguiente operación sobre el ejercicio (aunque sea un calentamiento o un DELETE) repara la marca.
    [Fact]
    public async Task DocMarcado_LoReparaElSiguientePutODelete()
    {
        var sub = Nuevo();
        var fallas = new Fallas { Fallar = r => r.Method == HttpMethod.Put && Fallas.EsRecord(r) };
        var conFallas = cosmos.ConHandler(fallas);
        await Put(sub, Nuevo(), Body(80, 5));
        await Put(sub, Nuevo(), Body(90, 5, minuto: 1), conFallas);

        await Put(sub, Nuevo(), Body(40, 10, "calentamiento", minuto: 2));
        var doc = (await DocRecord(sub))!;
        Assert.False(doc.ContainsKey("desactualizado"));
        Assert.Equal(90m, (decimal)doc["pesoMax"]!["pesoKg"]!);

        var otra = Nuevo();
        await Put(sub, otra, Body(30, 10, "calentamiento", minuto: 3));
        await Put(sub, Nuevo(), Body(95, 5, minuto: 4), conFallas);
        Assert.True((bool)(await DocRecord(sub))!["desactualizado"]!);
        Assert.Equal(HttpStatusCode.NoContent, (await Borrar(sub, otra)).StatusCode);
        doc = (await DocRecord(sub))!;
        Assert.False(doc.ContainsKey("desactualizado"));
        Assert.Equal(95m, (decimal)doc["pesoMax"]!["pesoKg"]!);
    }

    // R1: un doc marcado nunca se muestra con valores viejos; sin cupo para recalcularlo, el GET lo omite.
    [Fact]
    public async Task DocMarcado_SinCupo_ElGetLoOmite()
    {
        var sub = Nuevo();
        var conFallas = cosmos.ConHandler(new Fallas { Fallar = r => r.Method == HttpMethod.Put && Fallas.EsRecord(r) });
        await Put(sub, Nuevo(), Body(80, 5));
        await Put(sub, Nuevo(), Body(70, 5, ejercicio: "sentadilla"));
        await Put(sub, Nuevo(), Body(90, 5, minuto: 1), conFallas);

        AgotarRecalculos(sub);
        var records = await Listar(sub);
        Assert.Equal(["sentadilla"], records.Select(r => r.EjercicioId));
    }

    // R2 + R5: falla la lectura del doc de récord. PUT y DELETE responden igual y el récord queda marcado.
    [Fact]
    public async Task FallaLaLecturaDelRecord_GuardaYMarca()
    {
        var sub = Nuevo();
        await Put(sub, Nuevo(), Body(80, 5));
        var conFallas = cosmos.ConHandler(new Fallas { Fallar = r => r.Method == HttpMethod.Get && Fallas.EsRecord(r) });

        var id = Nuevo();
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, id, Body(90, 5, minuto: 1), conFallas)).StatusCode);
        Assert.True((bool)(await DocRecord(sub))!["desactualizado"]!);
        Assert.Equal(90m, (await Record(sub))!.PesoMax.PesoKg);

        Assert.Equal(HttpStatusCode.NoContent, (await Borrar(sub, id, conFallas)).StatusCode);
        Assert.True((bool)(await DocRecord(sub))!["desactualizado"]!);
        Assert.Equal(80m, (await Record(sub))!.PesoMax.PesoKg);
        LogsLimpios(sub);
    }

    // R5: todo lo del récord falla (también marcar): la serie es el dato que importa, 200/204 igual.
    [Fact]
    public async Task FallaTodoElRecord_SerieGuardadaY200()
    {
        var sub = Nuevo();
        var conFallas = cosmos.ConHandler(new Fallas
        {
            Fallar = r => Fallas.EsRecord(r) || r.Headers["x-ms-documentdb-isquery"] is not null,
        });

        var id = Nuevo();
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, id, Body(80, 5), conFallas)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, id, Body(70, 5), conFallas)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Borrar(sub, id, conFallas)).StatusCode);
        Assert.NotEmpty(_logSeries.Mensajes);
        LogsLimpios(sub);
    }

    // G1: con más de MaxFilasRecalculo series el recálculo no corre (el doc queda igual) y solo se loguea el tope.
    // También mide el RU real de la consulta de recálculo. Contenedor aparte con más RU/s, como Rango_ConMasDelTope.
    [Fact]
    [Trait("Categoria", "Lenta")]
    public async Task Recalculo_TopeDeFilas_YCostoPorFila()
    {
        var contenedor = (await Contenedor().Database.CreateContainerAsync(
            "tope-rec-" + Guid.NewGuid().ToString("N")[..8], "/userId", throughput: 10000, cancellationToken: Ct)).Container;
        try
        {
            await TopeYCosto(contenedor);
        }
        finally
        {
            await contenedor.DeleteContainerAsync(cancellationToken: CancellationToken.None);
        }
    }

    async Task TopeYCosto(Container contenedor)
    {
        var sub = Nuevo();
        var userId = "google:" + sub;
        var ts = Ahora.AddMinutes(-50).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        using var cupo = new SemaphoreSlim(50);
        async Task Insertar(int n) => await Task.WhenAll(Enumerable.Range(0, n).Select(async _ =>
        {
            await cupo.WaitAsync(Ct);
            try
            {
                var s = new Serie(Nuevo(), "press-banca", 60, 5, Hoy, ts);
                await contenedor.CreateItemAsync(SerieDoc.De(s, userId), new PartitionKey(userId), cancellationToken: Ct);
            }
            finally { cupo.Release(); }
        }));

        async Task<double> Cargo()
        {
            double ru = 0;
            using var it = contenedor.GetItemQueryIterator<JsonElement>(Records.ConsultaRecalculo(userId, "press-banca"),
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(userId) });
            while (it.HasMoreResults) ru += (await it.ReadNextAsync(Ct)).RequestCharge;
            return ru;
        }

        await Insertar(500);
        var ru500 = await Cargo();
        await Insertar(500);
        var ru1000 = await Cargo();
        var porFila = (ru1000 - ru500) / 500;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"RU recálculo: 500 filas = {ru500:F2}, 1000 filas = {ru1000:F2}, por fila = {porFila:F4}, fijo = {ru500 - 500 * porFila:F2}");

        // Pasado el tope aun sin la dueña de 100 kg (que se borra): relleno hasta MaxFilasRecalculo + 1.
        await Insertar(Records.MaxFilasRecalculo - 1000 + 1);
        var duena = new Serie(Nuevo(), "press-banca", 100, 5, Hoy, ts);
        await contenedor.CreateItemAsync(SerieDoc.De(duena, userId), new PartitionKey(userId), cancellationToken: Ct);
        var record = CalculoRecords.Fusionar(null, duena)!;
        await contenedor.CreateItemAsync(RecordDoc.De(record, userId, ts), new PartitionKey(userId), cancellationToken: Ct);
        TestContext.Current.TestOutputHelper?.WriteLine($"RU recálculo con {Records.MaxFilasRecalculo + 1} filas (TOP) = {await Cargo():F2}");

        var handlers = new Series(_tokens.Autenticador(), contenedor, _limites, Reloj, _logSeries);
        var res = await handlers.Borrar(new RequestFalso("DELETE", "/api/series/" + duena.Id, Bearer(sub)), duena.Id);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        // Los valores quedan como estaban (marcado antes de borrar) y el tope queda anotado en el doc.
        var despues = (await contenedor.ReadItemAsync<RecordDoc>(RecordDoc.IdDoc("press-banca"), new PartitionKey(userId),
            cancellationToken: Ct)).Resource;
        Assert.Equal(record.PesoMax, despues.PesoMax);
        Assert.True(despues.ExcedeTope);
        Assert.True(despues.Desactualizado);
        Assert.Contains(_logSeries.Mensajes, m => m.Texto.Contains($"más de {Records.MaxFilasRecalculo} series"));

        // M1: ya anotado, ni PUT ni DELETE ni GET repiten la consulta de ~81 RU ni gastan cupo de recálculos.
        var consultas = 0;
        var contados = cosmos.ConHandler(new Fallas
        {
            Fallar = r =>
            {
                if (r.Headers["x-ms-documentdb-isquery"] is not null && !Fallas.EsRecord(r)) Interlocked.Increment(ref consultas);
                return false;
            },
        }, contenedor.Id);
        var conContador = new Series(_tokens.Autenticador(), contados, _limites, Reloj, _logSeries);
        var otra = Nuevo();
        Assert.Equal(HttpStatusCode.OK, (await conContador.Guardar(
            new RequestFalso("PUT", "/api/series/" + otra, Bearer(sub), Body(110, 5)), otra)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await conContador.Guardar(
            new RequestFalso("PUT", "/api/series/" + otra, Bearer(sub), Body(50, 5)), otra)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await conContador.Borrar(
            new RequestFalso("DELETE", "/api/series/" + otra, Bearer(sub)), otra)).StatusCode);

        // GET: devuelve el guardado (110: la mejora sí se sumó) aunque esté marcado, mejor que ocultarlo.
        var get = (ResponseFalsa)await new Records(_tokens.Autenticador(), contados, _limites, Reloj, _logRecords)
            .Listar(new RequestFalso("GET", "/api/records", Bearer(sub)));
        var lista = JsonSerializer.Deserialize<List<RecordEjercicio>>(get.Texto(), Http.Json)!;
        Assert.Equal(110m, Assert.Single(lista).PesoMax.PesoKg);
        // Solo la consulta del GET (la de records); ninguna de recálculo.
        Assert.Equal(1, consultas);
        Assert.True(_limites.Recalculos.Permitir(userId, out _));
        LogsLimpios(sub);
    }

    // G3: si no se puede marcar antes de un cambio que dejaría el récord alto, 503 sin tocar la serie.
    [Fact]
    public async Task MarcaPreviaFalla_503YLaSerieSigueIntacta()
    {
        var sub = Nuevo();
        var id = Nuevo();
        await Put(sub, id, Body(80, 5));
        var conFallas = cosmos.ConHandler(new Fallas { Fallar = r => r.Method == HttpMethod.Patch && Fallas.EsRecord(r) });

        var borrar = await Borrar(sub, id, conFallas);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, borrar.StatusCode);
        Assert.True(borrar.Headers.TryGetValues("Retry-After", out _));
        var bajar = await Put(sub, id, Body(60, 5), conFallas);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, bajar.StatusCode);

        var hist = (ResponseFalsa)await HandlersEjercicios().Historial(
            new RequestFalso("GET", "/api/ejercicios/press-banca/historial", Bearer(sub)), "press-banca");
        var serie = Assert.Single(JsonSerializer.Deserialize<List<Serie>>(hist.Texto(), Http.Json)!);
        Assert.Equal((id, 80m), (serie.Id, serie.PesoKg));
        Assert.Equal(80m, (await Record(sub))!.PesoMax.PesoKg);

        // La ruta feliz no marca: subir peso con el patch caído sigue dando 200.
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, Nuevo(), Body(90, 5, minuto: 1), conFallas)).StatusCode);
        LogsLimpios(sub);
    }

    // G3: marcado previo ok pero el recálculo falla: 200/204, el doc queda marcado y el GET lo repara.
    [Fact]
    public async Task MarcaPreviaOk_RecalculoFalla_QuedaMarcadoYElGetLoRepara()
    {
        var sub = Nuevo();
        string a = Nuevo(), b = Nuevo();
        await Put(sub, a, Body(70, 5));
        await Put(sub, b, Body(80, 5, minuto: 1));
        var conFallas = cosmos.ConHandler(new Fallas { Fallar = r => r.Headers["x-ms-documentdb-isquery"] is not null });

        Assert.Equal(HttpStatusCode.NoContent, (await Borrar(sub, b, conFallas)).StatusCode);
        var doc = (await DocRecord(sub))!;
        Assert.True((bool)doc["desactualizado"]!);
        Assert.Equal(80m, (decimal)doc["pesoMax"]!["pesoKg"]!);
        Assert.Equal(70m, (await Record(sub))!.PesoMax.PesoKg);

        Assert.Equal(HttpStatusCode.OK, (await Put(sub, a, Body(65, 5), conFallas)).StatusCode);
        Assert.True((bool)(await DocRecord(sub))!["desactualizado"]!);
        Assert.Equal(65m, (await Record(sub))!.PesoMax.PesoKg);
        LogsLimpios(sub);
    }

    /// <summary>Suma el RequestCharge de todas las respuestas que pasan por el cliente.</summary>
    sealed class Medidor : RequestHandler
    {
        double _ru;
        public double Ru => Volatile.Read(ref _ru);
        public void Reiniciar() => Volatile.Write(ref _ru, 0);

        public override async Task<ResponseMessage> SendAsync(RequestMessage request, CancellationToken cancellationToken)
        {
            var res = await base.SendAsync(request, cancellationToken);
            double actual, nuevo;
            do
            {
                actual = Volatile.Read(ref _ru);
                nuevo = actual + res.Headers.RequestCharge;
            } while (Interlocked.CompareExchange(ref _ru, nuevo, actual) != actual);
            return res;
        }
    }

    // Costo real de los GET que dejan pedir más seguido: rutinas llenas (20 x 15 ejercicios) y records del catálogo entero.
    [Fact]
    [Trait("Categoria", "Lenta")]
    public async Task Medir_CostoDeGetRutinasYRecords()
    {
        var sub = Nuevo();
        var userId = "google:" + sub;
        var medidor = new Medidor();
        var medido = cosmos.ConHandler(medidor);

        var rutinas = new Rutinas(_tokens.Autenticador(), Contenedor(), _limites, Reloj);
        var ejercicios = Catalogo.Ejercicios.Take(Validacion.MaxEjerciciosRutina)
            .Select(e => new { id = e.Id, series = 4, reps = 8, pesoKg = 60, descansoS = 120 }).ToArray();
        for (var i = 0; i < Validacion.MaxRutinas; i++)
        {
            var id = Nuevo();
            var body = JsonSerializer.Serialize(new { nombre = $"Rutina {i}", ejercicios });
            var res = await rutinas.Guardar(new RequestFalso("PUT", "/api/rutinas/" + id, Bearer(sub), body), id);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        foreach (var e in Catalogo.Ejercicios)
        {
            var s = new Serie(Nuevo(), e.Id, 60, 5, Hoy, Ahora.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
            await Contenedor().CreateItemAsync(RecordDoc.De(CalculoRecords.Fusionar(null, s)!, userId, s.Ts),
                new PartitionKey(userId), cancellationToken: Ct);
        }

        medidor.Reiniciar();
        var getRutinas = await new Rutinas(_tokens.Autenticador(), medido, _limites, Reloj)
            .Listar(new RequestFalso("GET", "/api/rutinas", Bearer(sub)));
        Assert.Equal(HttpStatusCode.OK, getRutinas.StatusCode);
        var ruRutinas = medidor.Ru;

        medidor.Reiniciar();
        var getRecords = (ResponseFalsa)await new Records(_tokens.Autenticador(), medido, _limites, Reloj, _logRecords)
            .Listar(new RequestFalso("GET", "/api/records", Bearer(sub)));
        Assert.Equal(Catalogo.Ejercicios.Count, JsonNode.Parse(getRecords.Texto())!.AsArray().Count);
        var ruRecords = medidor.Ru;

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"RU GET /rutinas ({Validacion.MaxRutinas} x {Validacion.MaxEjerciciosRutina}) = {ruRutinas:F2}; " +
            $"RU GET /records ({Catalogo.Ejercicios.Count} docs) = {ruRecords:F2}");
        Assert.True(ruRutinas > 0 && ruRecords > 0);
    }
}
