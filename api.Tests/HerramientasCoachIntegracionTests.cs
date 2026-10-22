using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace GymCoach.Api.Tests;

[Collection(ColeccionCosmos.Nombre)]
public class HerramientasCoachIntegracionTests(CosmosFixture cosmos)
{
    static readonly DateTimeOffset Ahora = DateTimeOffset.UtcNow;
    static readonly DateOnly Hoy = DateOnly.FromDateTime(Ahora.UtcDateTime);
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    Container Contenedor()
    {
        Assert.SkipWhen(cosmos.Container is null, cosmos.MotivoSkip ?? "");
        return cosmos.Container!;
    }

    HerramientasCoach Herramientas() => new(Contenedor(), new PerfilesCoach(Contenedor(), new RelojFalso(Ahora)));

    static string NuevoUsuario() => "google:" + Guid.NewGuid().ToString("N");

    async Task Sembrar(string userId, string ejercicio, decimal peso, int reps, int haceDias, string? tipo = null)
    {
        var fecha = Hoy.AddDays(-haceDias).ToString("yyyy-MM-dd");
        var s = new Serie(Guid.NewGuid().ToString(), ejercicio, peso, reps, fecha, fecha + "T10:00:00.000Z", TipoSerie: tipo);
        // El emulador tiene pocos RU/s: sembrar cientos de series da 429 aunque sea en serie.
        for (var intento = 0; ; intento++)
        {
            try { await Contenedor().CreateItemAsync(SerieDoc.De(s, userId), new PartitionKey(userId), cancellationToken: Ct); return; }
            catch (CosmosException e) when (e.StatusCode == System.Net.HttpStatusCode.TooManyRequests && intento < 20)
            {
                await Task.Delay(e.RetryAfter ?? TimeSpan.FromMilliseconds(200), Ct);
            }
        }
    }

    async Task<JsonElement> Ejecutar(string userId, string nombre, string args) =>
        JsonDocument.Parse((await Herramientas().EjecutarAsync(nombre, args,
            new ContextoCoach(userId, Hoy, Ahora, PerfilCoach.PorDefecto), Ct)).Json).RootElement;

    [Fact]
    public async Task HerramientasDeA_NoVenDatosDeB()
    {
        var (a, b) = (NuevoUsuario(), NuevoUsuario());
        await Sembrar(a, "sentadilla", 100, 5, 1);
        await Sembrar(b, "sentadilla", 187.5m, 5, 1);
        await Sembrar(b, "press-banca", 142.5m, 5, 2);

        var progreso = await Ejecutar(a, "progreso", $$"""{"ejercicioId":"sentadilla","userId":"{{b}}"}""");
        Assert.Equal(100m, progreso.GetProperty("resumen").GetProperty("ultima").GetProperty("mejorPesoKg").GetDecimal());

        var recientes = await Ejecutar(a, "ver_entrenos_recientes", "{}");
        var dia = Assert.Single(recientes.GetProperty("dias").EnumerateArray());
        Assert.Equal("sentadilla", Assert.Single(dia.GetProperty("ejercicios").EnumerateArray()).GetProperty("ejercicioId").GetString());

        var volumen = await Ejecutar(a, "volumen_por_grupo", "{}");
        var pecho = volumen.GetProperty("grupos").EnumerateArray().Single(g => g.GetProperty("grupo").GetString() == "pecho");
        Assert.Equal(0, pecho.GetProperty("seriesUltimaSemana").GetInt32());
        Assert.DoesNotContain("187.5", recientes.GetRawText() + progreso.GetRawText() + volumen.GetRawText());
    }

    [Fact]
    public async Task Progreso_ConsultaSoloLaVentanaPedida()
    {
        var userId = NuevoUsuario();
        await Sembrar(userId, "press-banca", 50, 5, 100);
        await Sembrar(userId, "press-banca", 60, 5, 1);

        var ocho = await Ejecutar(userId, "progreso", """{"ejercicioId":"press-banca"}""");
        var veintiseis = await Ejecutar(userId, "progreso", """{"ejercicioId":"press-banca","semanas":26}""");

        Assert.Equal(60m, ocho.GetProperty("resumen").GetProperty("primera").GetProperty("mejorPesoKg").GetDecimal());
        Assert.Equal(26, veintiseis.GetProperty("porSemana").GetArrayLength());
        Assert.Equal(10m, veintiseis.GetProperty("resumen").GetProperty("cambioMejorPesoKg").GetDecimal());
    }

    [Fact]
    public async Task Progreso_TruncadoConTop()
    {
        var userId = NuevoUsuario();
        using var cupo = new SemaphoreSlim(5); // El emulador tiene 400 RU/s: con más en paralelo da 429 al sembrar.
        await Task.WhenAll(Enumerable.Range(0, 1001).Select(async _ =>
        {
            await cupo.WaitAsync(Ct);
            try { await Sembrar(userId, "press-banca", 60, 5, 1); }
            finally { cupo.Release(); }
        }));

        var r = await Ejecutar(userId, "progreso", """{"ejercicioId":"press-banca"}""");

        Assert.True(r.GetProperty("truncado").GetBoolean());
        Assert.Equal(1000, r.GetProperty("porSemana").EnumerateArray().Last().GetProperty("series").GetInt32());
    }

    [Fact]
    public async Task DocsQueNoSonSeries_NoEntran()
    {
        var userId = NuevoUsuario();
        await Sembrar(userId, "press-banca", 60, 5, 0);
        var ahora = Ahora.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        var rutina = new Rutina(Guid.NewGuid().ToString(), "Empuje", [new("press-banca", 3, 5, 200, 90)], ahora);
        await Contenedor().CreateItemAsync(RutinaDoc.De(rutina, userId), new PartitionKey(userId), cancellationToken: Ct);
        var record = new RecordEjercicio("press-banca", new(200, 5, Hoy.ToString("yyyy-MM-dd")), null, new(5, 200, Hoy.ToString("yyyy-MM-dd")));
        await Contenedor().CreateItemAsync(RecordDoc.De(record, userId, ahora), new PartitionKey(userId), cancellationToken: Ct);
        await Contenedor().CreateItemAsync(PerfilCoachDoc.De(new(45, "fuerza", ["rodilla"]), userId, Ahora), new PartitionKey(userId),
            cancellationToken: Ct);

        var recientes = await Ejecutar(userId, "ver_entrenos_recientes", "{}");
        var banca = Assert.Single(Assert.Single(recientes.GetProperty("dias").EnumerateArray()).GetProperty("ejercicios").EnumerateArray());
        Assert.Equal(1, banca.GetProperty("series").GetInt32());
        Assert.Equal(60m, banca.GetProperty("mejorSerie").GetProperty("pesoKg").GetDecimal());

        var progreso = await Ejecutar(userId, "progreso", """{"ejercicioId":"press-banca"}""");
        Assert.Equal(60m, progreso.GetProperty("resumen").GetProperty("ultima").GetProperty("mejorPesoKg").GetDecimal());

        var volumen = await Ejecutar(userId, "volumen_por_grupo", "{}");
        var pecho = volumen.GetProperty("grupos").EnumerateArray().Single(g => g.GetProperty("grupo").GetString() == "pecho");
        Assert.Equal(1, pecho.GetProperty("seriesUltimaSemana").GetInt32());
    }

    [Fact]
    public async Task Recordar_EscribeSoloEnElUsuarioDelContexto()
    {
        var (a, b) = (NuevoUsuario(), NuevoUsuario());

        var r = await Ejecutar(a, "recordar", $$"""{"accion":"fijar_minutos","minutos":45,"userId":"{{b}}"}""");

        Assert.True(r.GetProperty("ok").GetBoolean());
        var perfiles = new PerfilesCoach(Contenedor(), new RelojFalso(Ahora));
        Assert.Equal(45, (await perfiles.LeerAsync(a, Ct)).MinutosDisponibles);
        Assert.Null((await perfiles.LeerAsync(b, Ct)).MinutosDisponibles);
    }
}
