using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GymCoach.Api.Functions;
using Microsoft.Azure.Cosmos;

namespace GymCoach.Api.Tests;

[Collection(ColeccionCosmos.Nombre)]
public class ImportarIntegracionTests(CosmosFixture cosmos)
{
    readonly Tokens _tokens = new();
    static readonly DateTimeOffset Ahora = DateTimeOffset.UtcNow;
    static readonly RelojFalso Reloj = new(Ahora);
    // Cada instancia de prueba tiene sus límites: el 429 no se contagia entre pruebas.
    readonly Limites _limites = new(Reloj);
    static readonly string Hoy = Ahora.UtcDateTime.ToString("yyyy-MM-dd");
    const string Ejercicio = "press-pecho-maquina";

    Container Contenedor()
    {
        Assert.SkipWhen(cosmos.Container is null, cosmos.MotivoSkip ?? "");
        return cosmos.Container!;
    }

    Importar Handler(Container? c = null, PerfilesEntrenamiento? perfiles = null) =>
        new(_tokens.Autenticador(), c ?? Contenedor(), _limites, Reloj, perfiles ?? new PerfilesEntrenamiento(c ?? Contenedor(), _limites, Reloj));
    Series HandlersSeries() => new(_tokens.Autenticador(), Contenedor(), _limites, Reloj);

    string Bearer(string sub) => "Bearer " + _tokens.Crear(sub: sub);
    static string Sub() => "importar-" + Guid.NewGuid().ToString("N");
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ts distintos y crecientes por minuto: el recálculo ordena por ts.
    static JsonObject Serie(string id, decimal peso, int reps = 5, string? tipoSerie = null, int minuto = 0, string ejercicio = Ejercicio) => new()
    {
        ["id"] = id,
        ["ejercicioId"] = ejercicio,
        ["pesoKg"] = peso,
        ["reps"] = reps,
        ["fecha"] = Hoy,
        ["ts"] = Ahora.AddMinutes(-30 + minuto).ToString("o"),
        ["tipoSerie"] = tipoSerie,
    };

    static string Lote(params JsonObject[] series) => new JsonObject { ["series"] = new JsonArray([.. series]) }.ToJsonString();
    static string Id() => Guid.NewGuid().ToString("D");

    async Task<ResponseFalsa> Post(string? sub, string body, Container? c = null, PerfilesEntrenamiento? perfiles = null) =>
        (ResponseFalsa)await Handler(c, perfiles).Guardar(
            new RequestFalso("POST", "/api/importar/series", sub is null ? null : Bearer(sub), body), Ct);

    async Task<Importar.Resultado> PostOk(string sub, string body, Container? c = null, PerfilesEntrenamiento? perfiles = null)
    {
        var res = await Post(sub, body, c, perfiles);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonSerializer.Deserialize<Importar.Resultado>(res.Texto(), Http.Json)!;
    }

    async Task<List<Serie>> SeriesDe(string sub)
    {
        var res = (ResponseFalsa)await HandlersSeries().Listar(new RequestFalso("GET", $"/api/series?desde={Hoy}&hasta={Hoy}", Bearer(sub)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonSerializer.Deserialize<List<Serie>>(res.Texto(), Http.Json)!;
    }

    async Task<RecordDoc?> RecordDe(string sub, string ejercicio = Ejercicio)
    {
        try
        {
            return (await Contenedor().ReadItemAsync<RecordDoc>(RecordDoc.IdDoc(ejercicio), new PartitionKey("google:" + sub),
                cancellationToken: Ct)).Resource;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>Cuenta las consultas y suma el RequestCharge de lo que pasa por el cliente.</summary>
    sealed class Contador : RequestHandler
    {
        int _consultas;
        double _ru;
        public int Consultas => Volatile.Read(ref _consultas);
        public double Ru { get { lock (this) return _ru; } }

        public void Reiniciar()
        {
            Volatile.Write(ref _consultas, 0);
            lock (this) _ru = 0;
        }

        public override async Task<ResponseMessage> SendAsync(RequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers["x-ms-documentdb-isquery"] is not null) Interlocked.Increment(ref _consultas);
            var res = await base.SendAsync(request, cancellationToken);
            lock (this) _ru += res.Headers.RequestCharge;
            return res;
        }
    }

    [Fact]
    public async Task LoteDeTres_SeGuardaYReimportarNoDuplica()
    {
        var sub = Sub();
        var body = Lote(Serie(Id(), 50, minuto: 0), Serie(Id(), 60, minuto: 1), Serie(Id(), 55, minuto: 2));

        Assert.Equal(new Importar.Resultado(3, 0, 1, 0), await PostOk(sub, body));
        Assert.Equal(3, (await SeriesDe(sub)).Count);

        Assert.Equal(new Importar.Resultado(0, 3, 0, 0), await PostOk(sub, body));
        Assert.Equal(3, (await SeriesDe(sub)).Count);
    }

    [Fact]
    public async Task Reimportar_NoPisaLaExistente()
    {
        var sub = Sub();
        var id = Id();
        await PostOk(sub, Lote(Serie(id, 60)));

        // Mismo id con otro peso (p. ej. Diego la corrigió en la app y luego reimporta el CSV): se deja como está.
        var r = await PostOk(sub, Lote(Serie(id, 40), Serie(Id(), 45, minuto: 1)));
        Assert.Equal(new Importar.Resultado(1, 1, 1, 0), r);
        var guardadas = await SeriesDe(sub);
        Assert.Equal(60m, Assert.Single(guardadas, s => s.Id == id).PesoKg);
        Assert.Equal(60m, (await RecordDe(sub))!.PesoMax.PesoKg);
    }

    [Fact]
    public async Task RecordInexistente_SeCreaYElCalentamientoNoBate()
    {
        var sub = Sub();
        await PostOk(sub, Lote(
            Serie(Id(), 50, reps: 10, minuto: 0),
            Serie(Id(), 60, reps: 5, minuto: 1),
            Serie(Id(), 100, reps: 3, tipoSerie: "calentamiento", minuto: 2)));

        var record = await RecordDe(sub);
        Assert.NotNull(record);
        Assert.Equal(new MarcaPeso(60, 5, Hoy), record.PesoMax);
        Assert.Equal(new MarcaReps(10, 50, Hoy), record.RepsMax);
        Assert.False(record.Desactualizado);
    }

    [Fact]
    public async Task RecordExistente_SeFusionaSinRecalcular()
    {
        var sub = Sub();
        await PostOk(sub, Lote(Serie(Id(), 60, minuto: 0)));
        var contador = new Contador();
        var contado = cosmos.ConHandler(contador);

        // Línea base: un lote de 2 ya importadas solo hace la lectura de ids (con 2 ids el SDK la hace por consulta).
        var repetidas = Lote(Serie(Id(), 10, minuto: 1), Serie(Id(), 10, minuto: 1));
        await PostOk(sub, repetidas);
        await PostOk(sub, repetidas, contado);
        var consultasBase = contador.Consultas;

        contador.Reiniciar();
        var r = await PostOk(sub, Lote(Serie(Id(), 70, minuto: 2), Serie(Id(), 80, reps: 3, minuto: 3)), contado);
        Assert.Equal(new Importar.Resultado(2, 0, 1, 0), r);
        Assert.Equal(consultasBase, contador.Consultas);
        var record = (await RecordDe(sub))!;
        Assert.Equal(new MarcaPeso(80, 3, Hoy), record.PesoMax);
        Assert.Equal(new MarcaReps(5, 60, Hoy), record.RepsMax);
    }

    [Fact]
    public async Task RecordPasadoElTope_SeFusionaIgual()
    {
        var sub = Sub();
        var userId = "google:" + sub;
        var vieja = new Serie(Id(), Ejercicio, 60, 5, Hoy, Ahora.AddMinutes(-40).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        var doc = RecordDoc.De(CalculoRecords.Fusionar(null, vieja)!, userId, vieja.Ts) with { ExcedeTope = true };
        await Contenedor().CreateItemAsync(doc, new PartitionKey(userId), cancellationToken: Ct);

        var contador = new Contador();
        var r = await PostOk(sub, Lote(Serie(Id(), 90, minuto: 1)), cosmos.ConHandler(contador));
        Assert.Equal(new Importar.Resultado(1, 0, 1, 0), r);
        // Con un solo id la lectura es puntual: ninguna consulta, o sea, ningún recálculo.
        Assert.Equal(0, contador.Consultas);
        var record = (await RecordDe(sub))!;
        Assert.Equal(90m, record.PesoMax.PesoKg);
        Assert.True(record.ExcedeTope);
    }

    [Fact]
    public async Task OtroUsuario_NoVeNiPisa()
    {
        string a = Sub(), b = Sub();
        var id = Id();
        await PostOk(a, Lote(Serie(id, 60)));
        // Mismo id desde B: queda en la partición de B (para B es nueva), la de A no cambia.
        Assert.Equal(1, (await PostOk(b, Lote(Serie(id, 99)))).Guardadas);

        Assert.Equal(60, Assert.Single(await SeriesDe(a)).PesoKg);
        Assert.Equal(99, Assert.Single(await SeriesDe(b)).PesoKg);
        Assert.Equal(60, (await RecordDe(a))!.PesoMax.PesoKg);
    }

    [Fact]
    public async Task SinToken_401()
    {
        var res = await Post(null, Lote(Serie(Id(), 60)));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task LoteFueraDeCupo_429()
    {
        var sub = Sub();
        for (var i = 0; i < Limites.ImportLotesPorMinuto; i++)
            await PostOk(sub, Lote(Serie(Id(), 60, minuto: i)));

        var res = await Post(sub, Lote(Serie(Id(), 60)));
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.True(res.Headers.Contains("Retry-After"));
        // Importar no gasta el cupo de escrituras: registrar el entreno del día sigue pasando.
        Assert.True(_limites.PermitirEscritura("google:" + sub, out _));
    }

    /// <summary>Perfil de entrenamiento con la marca falsa: cuenta las llamadas y puede fallar.</summary>
    static PerfilesEntrenamiento PerfilesMarcados(List<string> marcados, Exception? falla = null) => new(
        (_, _) => throw new InvalidOperationException("Importar no lee el perfil."),
        (_, _) => throw new InvalidOperationException("Importar no recalcula el perfil."),
        (_, _, _) => throw new InvalidOperationException("Importar no guarda el perfil."),
        (userId, _) =>
        {
            marcados.Add(userId);
            return falla is null ? Task.CompletedTask : Task.FromException(falla);
        },
        _ => true, Reloj);

    [Fact]
    public async Task PerfilEntrenamiento_SeMarcaSoloConSeriesNuevas()
    {
        var sub = Sub();
        var marcados = new List<string>();
        var body = Lote(Serie(Id(), 50, minuto: 0), Serie(Id(), 60, minuto: 1));

        await PostOk(sub, body, perfiles: PerfilesMarcados(marcados));
        Assert.Equal(["google:" + sub], marcados);

        // Reimportar el mismo lote no trae series nuevas: el perfil no cambia, no se marca.
        Assert.Equal(new Importar.Resultado(0, 2, 0, 0), await PostOk(sub, body, perfiles: PerfilesMarcados(marcados)));
        Assert.Single(marcados);
    }

    [Fact]
    public async Task PerfilEntrenamiento_SiMarcarFalla_LaImportacionSigueEn200()
    {
        var sub = Sub();
        var marcados = new List<string>();
        var falla = new CosmosException("saturado", HttpStatusCode.ServiceUnavailable, 0, "", 0);

        var r = await PostOk(sub, Lote(Serie(Id(), 50)), perfiles: PerfilesMarcados(marcados, falla));
        Assert.Equal(new Importar.Resultado(1, 0, 1, 0), r);
        Assert.Single(marcados);
        Assert.Single(await SeriesDe(sub));
    }

    [Fact]
    public async Task PerfilEntrenamiento_ConDoc_QuedaMarcado_YSinDocNoSeCrea()
    {
        var sub = Sub();
        var userId = "google:" + sub;
        var hoy = DateOnly.FromDateTime(Ahora.UtcDateTime);
        var perfiles = new PerfilesEntrenamiento(Contenedor(), _limites, Reloj);

        // Sin doc: el patch da 404 y se ignora (el siguiente GET lo arma con todo).
        await PostOk(sub, Lote(Serie(Id(), 50, minuto: 0)));
        await Assert.ThrowsAsync<CosmosException>(() =>
            Contenedor().ReadItemAsync<JsonElement>(PerfilEntrenamientoDoc.IdDoc, new PartitionKey(userId), cancellationToken: Ct));

        Assert.NotNull(await perfiles.ObtenerAsync(userId, hoy, null, Ct));
        await PostOk(sub, Lote(Serie(Id(), 60, minuto: 1)));
        var doc = (await Contenedor().ReadItemAsync<JsonElement>(PerfilEntrenamientoDoc.IdDoc, new PartitionKey(userId),
            cancellationToken: Ct)).Resource;
        Assert.True(doc.GetProperty("desactualizado").GetBoolean());

        // Marcado: el siguiente read recalcula (ve la serie nueva) y el replace borra la marca.
        Assert.Equal(2, (await perfiles.ObtenerAsync(userId, hoy, null, Ct))!.SeriesAnalizadas);
        doc = (await Contenedor().ReadItemAsync<JsonElement>(PerfilEntrenamientoDoc.IdDoc, new PartitionKey(userId),
            cancellationToken: Ct)).Resource;
        Assert.False(doc.TryGetProperty("desactualizado", out _));
    }

    // Costo real del perfil de entrenamiento con un historial como el de Diego (~800 series): recálculo, lectura vigente
    // y las dos marcas de Importar (la primera escribe; la segunda da 412 por el filtro).
    [Fact]
    [Trait("Categoria", "Lenta")]
    public async Task Medir_CostoDelPerfilDeEntrenamiento()
    {
        var sub = Sub();
        var userId = "google:" + sub;
        var hoy = DateOnly.FromDateTime(Ahora.UtcDateTime);
        var ejercicios = new[] { "press-banca", "press-pecho-maquina", "sentadilla", "prensa", "jalon-pecho", "remo-barra", "curl-barra", "pec-deck" };
        // 800 series: 100 días con 8 series cada uno, en batches de 100 (el máximo de un batch).
        for (var lote = 0; lote < 8; lote++)
        {
            var batch = Contenedor().CreateTransactionalBatch(new PartitionKey(userId));
            for (var i = 0; i < 100; i++)
            {
                var n = lote * 100 + i;
                var t = Ahora.UtcDateTime.AddDays(-(n / 8)).AddMinutes(-(n % 8) * 3);
                batch.CreateItem(SerieDoc.De(new Serie(Id(), ejercicios[n % ejercicios.Length], 40 + n % 30, 8,
                    t.ToString("yyyy-MM-dd"), t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), "Rutina " + n % 3), userId));
            }
            using var res = await batch.ExecuteAsync(Ct);
            Assert.True(res.IsSuccessStatusCode, res.StatusCode.ToString());
        }

        var contador = new Contador();
        var perfiles = new PerfilesEntrenamiento(cosmos.ConHandler(contador), _limites, Reloj);
        var perfil = await perfiles.ObtenerAsync(userId, hoy, -360, Ct);
        Assert.Equal(800, perfil!.SeriesAnalizadas);
        var ruRecalculo = contador.Ru;

        contador.Reiniciar();
        await perfiles.ObtenerAsync(userId, hoy, -360, Ct);
        var ruLectura = contador.Ru;
        Assert.Equal(0, contador.Consultas);

        contador.Reiniciar();
        await perfiles.MarcarDesactualizadoAsync(userId, Ct);
        var ruMarca = contador.Ru;
        contador.Reiniciar();
        await perfiles.MarcarDesactualizadoAsync(userId, Ct);
        var ruMarcaRepetida = contador.Ru;

        contador.Reiniciar();
        await perfiles.ObtenerAsync(userId, hoy, null, Ct);
        var ruRecalculoMarcado = contador.Ru;

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"RU perfil de entrenamiento (800 series): recálculo sin doc = {ruRecalculo:F2}; lectura vigente = {ruLectura:F2}; " +
            $"marca = {ruMarca:F2}; marca repetida (412) = {ruMarcaRepetida:F2}; recálculo con replace = {ruRecalculoMarcado:F2}");
        Assert.True(ruRecalculo > 0 && ruLectura > 0);
    }

    // Costo real de un lote lleno: 10 series nuevas en 5 ejercicios, con los récords por crear y ya creados.
    // Incluye el patch que marca el perfil de entrenamiento (aquí sin doc: 404).
    [Fact]
    [Trait("Categoria", "Lenta")]
    public async Task Medir_CostoDeUnLoteLleno()
    {
        var sub = Sub();
        var ejercicios = new[] { "press-banca", "press-pecho-maquina", "peso-muerto", "pec-deck", "cruce-poleas" };
        string LoteLleno(int desde) => Lote([.. Enumerable.Range(0, Validacion.MaxSeriesImportar)
            .Select(i => Serie(Id(), 40 + desde + i, minuto: -10 + i, ejercicio: ejercicios[i % ejercicios.Length]))]);

        var contador = new Contador();
        var contado = cosmos.ConHandler(contador);
        Assert.Equal(new Importar.Resultado(10, 0, 5, 0), await PostOk(sub, LoteLleno(0), contado));
        var ruCreando = contador.Ru;

        contador.Reiniciar();
        var segundo = LoteLleno(30);
        Assert.Equal(new Importar.Resultado(10, 0, 5, 0), await PostOk(sub, segundo, contado));
        var ruFusionando = contador.Ru;

        contador.Reiniciar();
        Assert.Equal(new Importar.Resultado(0, 10, 0, 0), await PostOk(sub, segundo, contado));
        var ruRepetido = contador.Ru;

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"RU lote de {Validacion.MaxSeriesImportar} (5 ejercicios): récords por crear = {ruCreando:F2}; récords existentes = {ruFusionando:F2}; " +
            $"lote ya importado = {ruRepetido:F2}");
        Assert.True(ruCreando > 0 && ruFusionando > 0);
    }
}
