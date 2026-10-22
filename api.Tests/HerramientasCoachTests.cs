using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace GymCoach.Api.Tests;

/// <summary>Perfil de entrenamiento armado a mano (sin CalculosPerfil): ejercicios del catálogo con sus marcas.</summary>
public static class EntrenamientoFalso
{
    /// <summary>Usuario sin series: historial insuficiente, sin guardia.</summary>
    public static readonly PerfilEntrenamiento Vacio = Con(0, []);

    public static PerfilEntrenamiento Con(int dias, string[] ids, string? inferido = null, decimal? mejorKg = 60m, decimal? ultimaKg = null,
        string hoy = "2026-10-09") =>
        new(CalculosPerfil.Version, hoy + "T12:00:00.000Z", hoy, dias * ids.Length * 3, false,
            dias > 0 ? "2026-09-01" : null, dias > 0 ? "2026-10-08" : null, dias, null,
            new EquipoHistorial(inferido, null, dias * ids.Length * 3), [], [],
            [.. ids.Select(id => Catalogo.Ejercicios.Single(e => e.Id == id)).Select(e => new EjercicioHistorial(
                e.Id, e.Nombre, e.Grupo, e.Equipo, e.CargaCorporal ?? false, dias, dias * 3, "2026-09-01", "2026-10-08",
                ultimaKg is decimal u ? new MarcaPeso(u, 8, "2026-10-08") : null,
                mejorKg is decimal m ? new MarcaPeso(m, 8, "2026-10-01") : null, null))]);
}

public class HerramientasCoachTests
{
    const string UserA = "google:a";
    static readonly DateOnly Hoy = new(2026, 10, 9);
    static readonly DateTimeOffset Ahora = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    // Lo que la herramienta pidió a Cosmos: userId de la partición y parámetros de la consulta.
    readonly List<(string UserId, Dictionary<string, object> Parametros)> _consultas = [];
    List<Serie> _devolver = [];
    Exception? _lanzar;
    readonly Dictionary<string, PerfilCoach> _perfiles = [];

    HerramientasCoach Herramientas() => new((consulta, userId) =>
    {
        _consultas.Add((userId, consulta.GetQueryParameters().ToDictionary(p => p.Name, p => p.Value)));
        if (_lanzar is not null) throw _lanzar;
        // Como Cosmos: ORDER BY c.ts DESC.
        return Task.FromResult(_devolver.OrderByDescending(s => s.Ts, StringComparer.Ordinal).ToList());
    }, new PerfilesCoach(
        (userId, _) => Task.FromResult((_perfiles.GetValueOrDefault(userId, PerfilCoach.PorDefecto), (string?)null)),
        (userId, perfil, _, _) =>
        {
            _perfiles[userId] = perfil;
            return Task.FromResult(true);
        }));

    // Sin entrenamiento indicado: historial vacío (sin guardia). Para probar el null: Ctx() with { Entrenamiento = null }.
    static ContextoCoach Ctx(int? minutos = null, PerfilEntrenamiento? entrenamiento = null, string? equipo = null) =>
        new(UserA, Hoy, Ahora, PerfilCoach.PorDefecto with { MinutosDisponibles = minutos, Equipo = equipo },
            entrenamiento ?? EntrenamientoFalso.Vacio);

    async Task<JsonElement> Ejecutar(string nombre, string args, ContextoCoach? ctx = null) =>
        JsonDocument.Parse((await Herramientas().EjecutarAsync(nombre, args, ctx ?? Ctx())).Json).RootElement;

    static Serie S(string ejercicio, decimal peso, int reps, int haceDias)
    {
        var fecha = Hoy.AddDays(-haceDias).ToString("yyyy-MM-dd");
        return new(Guid.NewGuid().ToString(), ejercicio, peso, reps, fecha, fecha + "T10:00:00.000Z");
    }

    [Fact]
    public void Definiciones_SieteConSchemaValido()
    {
        Assert.Equal(["ver_entrenos_recientes", "progreso", "volumen_por_grupo", "mis_ejercicios", "recordar", "proponer_rutina", "proponer_programa"],
            HerramientasCoach.Definiciones.Select(d => d.Nombre));
        foreach (var d in HerramientasCoach.Definiciones)
        {
            using var doc = JsonDocument.Parse(d.ParametrosJson);
            Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
            // Ninguna herramienta acepta userId: sale del token.
            Assert.DoesNotContain("userId", d.ParametrosJson);
        }
        using var progreso = JsonDocument.Parse(HerramientasCoach.Definiciones[1].ParametrosJson);
        Assert.Equal(Catalogo.Ejercicios.Count,
            progreso.RootElement.GetProperty("properties").GetProperty("ejercicioId").GetProperty("enum").GetArrayLength());
    }

    [Fact]
    public async Task Progreso_IgnoraUserIdDeLosArgumentos()
    {
        _devolver = [S("press-banca", 60, 5, 1)];

        var r = await Ejecutar("progreso", """{"ejercicioId":"press-banca","userId":"google:otro"}""");

        var (userId, parametros) = Assert.Single(_consultas);
        Assert.Equal(UserA, userId);
        Assert.Equal(UserA, parametros["@userId"]);
        Assert.Equal("press-banca", parametros["@ejercicioId"]);
        // Solo las 8 semanas pedidas por defecto, no las 26 máximas.
        Assert.Equal("2026-08-15", parametros["@desde"]);
        Assert.Equal(1001, parametros["@tope"]);
        Assert.Equal(60m, r.GetProperty("resumen").GetProperty("ultima").GetProperty("mejorPesoKg").GetDecimal());
        Assert.False(r.GetProperty("truncado").GetBoolean());
    }

    [Theory]
    [InlineData("borrar_todo", "{}")]
    [InlineData("progreso", """{"ejercicioId":"press-banca-inventado"}""")]
    [InlineData("progreso", "{}")]
    [InlineData("progreso", """{"ejercicioId":"press-banca","semanas":27}""")]
    [InlineData("progreso", """{"ejercicioId":"press-banca","semanas":1}""")]
    [InlineData("progreso", """{"ejercicioId":"press-banca","semanas":"8"}""")]
    [InlineData("progreso", """{"ejercicioId":"press-banca","semanas":8.5}""")]
    [InlineData("progreso", """{"ejercicioId":"press-banca","ejercicioId":"sentadilla"}""")]
    [InlineData("progreso", "{no es json")]
    [InlineData("progreso", "[]")]
    [InlineData("progreso", "{\"ejercicioId\":\"\\uD800\"}")]
    [InlineData("ver_entrenos_recientes", """{"dias":15}""")]
    [InlineData("ver_entrenos_recientes", """{"dias":0}""")]
    [InlineData("volumen_por_grupo", """{"semanas":9}""")]
    public async Task ArgumentosInvalidos_ErrorSinConsultar(string nombre, string args)
    {
        var r = await Ejecutar(nombre, args);

        Assert.True(r.TryGetProperty("error", out var error));
        Assert.Equal(nombre == "borrar_todo" ? "herramienta_desconocida" : "parametros_invalidos", error.GetString());
        Assert.Empty(_consultas);
    }

    [Fact]
    public async Task Recientes_DefaultSieteDias_ConsultaLaVentanaMaxima()
    {
        var r = await Ejecutar("ver_entrenos_recientes", "");

        Assert.Equal("2026-10-03", r.GetProperty("desde").GetString());
        var (_, parametros) = Assert.Single(_consultas);
        Assert.Equal("2026-09-26", parametros["@desde"]);
        Assert.Equal("2026-10-09", parametros["@hoy"]);
        Assert.Equal(1001, parametros["@tope"]);
    }

    [Fact]
    public async Task Volumen_DefaultCuatroSemanasYNullAceptado()
    {
        var r = await Ejecutar("volumen_por_grupo", """{"semanas":null}""");

        // Consulta las 8 semanas máximas y recorta a las 4 pedidas.
        Assert.Equal("2026-08-15", Assert.Single(_consultas).Parametros["@desde"]);
        Assert.Equal("2026-09-12", r.GetProperty("desde").GetString());
        Assert.Equal(4, r.GetProperty("semanas").GetArrayLength());
        // Los nulls se escriben: el modelo ve cambioSeriesPct: null.
        Assert.Equal(JsonValueKind.Null, r.GetProperty("grupos")[0].GetProperty("cambioSeriesPct").ValueKind);
    }

    [Fact]
    public async Task Progreso_Truncado()
    {
        _devolver = Enumerable.Range(0, 1001).Select(_ => S("press-banca", 60, 5, 1)).ToList();

        var r = await Ejecutar("progreso", """{"ejercicioId":"press-banca"}""");

        Assert.True(r.GetProperty("truncado").GetBoolean());
        Assert.Equal(1000, r.GetProperty("porSemana").EnumerateArray().Last().GetProperty("series").GetInt32());
    }

    [Fact]
    public async Task Progreso_VeintiseisSemanas_BloquesYJsonAcotado()
    {
        // Historial realista: 3 sesiones × 5 series por semana durante 26 semanas, subiendo de a 2.5 kg.
        _devolver = [.. Enumerable.Range(0, 26 * 3).SelectMany(sesion => Enumerable.Range(0, 5).Select(_ =>
            S("press-banca", 60m + sesion / 3 * 2.5m, 8, sesion * 7 / 3)))];

        var res = await Herramientas().EjecutarAsync("progreso", """{"ejercicioId":"press-banca","semanas":26}""", Ctx());

        using var doc = JsonDocument.Parse(res.Json);
        var r = doc.RootElement;
        Assert.False(r.GetProperty("truncado").GetBoolean());
        Assert.Equal(26, r.GetProperty("porSemana").GetArrayLength());
        Assert.Equal(390, r.GetProperty("porSemana").EnumerateArray().Sum(s => s.GetProperty("series").GetInt32()));
        // Va al modelo: solo un resumen por semana, nada por serie.
        var bytes = System.Text.Encoding.UTF8.GetByteCount(res.Json);
        Assert.True(bytes < 6 * 1024, $"{bytes} bytes");
    }

    [Fact]
    public async Task Truncado_PierdeLasMasViejas()
    {
        // 1001 series con ts distintos: la más vieja hace 20 días, la más nueva hoy con 80 kg.
        var vieja = S("press-banca", 100, 5, 20);
        var nueva = S("press-banca", 80, 5, 0) with { Ts = Hoy.ToString("yyyy-MM-dd") + "T23:00:00.000Z" };
        _devolver = [vieja, nueva, .. Enumerable.Range(0, 999).Select(i => S("press-banca", 60, 5, 1) with
            { Ts = Hoy.AddDays(-1).ToString("yyyy-MM-dd") + $"T10:{i / 60:00}:{i % 60:00}.000Z" })];

        var r = await Ejecutar("progreso", """{"ejercicioId":"press-banca","semanas":4}""");

        Assert.True(r.GetProperty("truncado").GetBoolean());
        var semanas = r.GetProperty("porSemana").EnumerateArray().ToList();
        Assert.Equal(80m, semanas[^1].GetProperty("mejorSerie").GetProperty("pesoKg").GetDecimal());
        Assert.Equal(1000, semanas[^1].GetProperty("series").GetInt32());
        // La de 100 kg (la más vieja) es la que se perdió.
        Assert.Equal(0, semanas[^3].GetProperty("series").GetInt32());
    }

    [Fact]
    public async Task Truncado_VentanaPedidaCompletaNoSeMarca()
    {
        // Lo que se perdió es más viejo que las 2 semanas pedidas: esa ventana está completa.
        _devolver = [.. Enumerable.Range(0, 1001).Select(i => S("press-banca", 60, 5, 20 + i % 30))];

        var r = await Ejecutar("progreso", """{"ejercicioId":"press-banca","semanas":2}""");

        Assert.False(r.GetProperty("truncado").GetBoolean());
    }

    [Fact]
    public async Task Cache_UnaConsultaPorTurnoAunqueCambienLasSemanas()
    {
        var h = Herramientas();
        var cache = new CacheTurno();

        // Progreso: la ventana guardada (26) cubre la pedida después (4).
        await h.EjecutarAsync("progreso", """{"ejercicioId":"press-banca","semanas":26}""", Ctx(), cache: cache);
        await h.EjecutarAsync("progreso", """{"ejercicioId":"press-banca","semanas":4}""", Ctx(), cache: cache);
        await h.EjecutarAsync("volumen_por_grupo", """{"semanas":2}""", Ctx(), cache: cache);
        await h.EjecutarAsync("volumen_por_grupo", "{}", Ctx(), cache: cache);
        await h.EjecutarAsync("ver_entrenos_recientes", """{"dias":3}""", Ctx(), cache: cache);
        await h.EjecutarAsync("ver_entrenos_recientes", """{"dias":14}""", Ctx(), cache: cache);
        Assert.Equal(3, _consultas.Count);

        // Una ventana mayor que la guardada se vuelve a consultar, y desde ahí la cubre.
        await h.EjecutarAsync("progreso", """{"ejercicioId":"sentadilla","semanas":4}""", Ctx(), cache: cache);
        await h.EjecutarAsync("progreso", """{"ejercicioId":"sentadilla","semanas":26}""", Ctx(), cache: cache);
        await h.EjecutarAsync("progreso", """{"ejercicioId":"sentadilla","semanas":8}""", Ctx(), cache: cache);
        Assert.Equal(5, _consultas.Count);
        Assert.Equal("2026-04-11", _consultas[4].Parametros["@desde"]);

        // Otro usuario es otra consulta; sin caché (otro turno) se vuelve a consultar.
        await h.EjecutarAsync("progreso", """{"ejercicioId":"press-banca"}""", Ctx() with { UserId = "google:b" }, cache: cache);
        await h.EjecutarAsync("progreso", """{"ejercicioId":"press-banca"}""", Ctx());
        Assert.Equal(7, _consultas.Count);
        Assert.Equal("google:b", _consultas[5].UserId);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task CosmosSaturado_NoDisponibleAlModelo(HttpStatusCode codigo)
    {
        _lanzar = new CosmosException("saturado", codigo, 0, "", 0);

        var r = await Ejecutar("volumen_por_grupo", "{}");

        Assert.Equal("no_disponible", r.GetProperty("error").GetString());
    }

    [Fact]
    public async Task OtraFallaDeCosmos_SePropaga()
    {
        _lanzar = new CosmosException("malo", HttpStatusCode.BadRequest, 0, "", 0);

        await Assert.ThrowsAsync<CosmosException>(() => Ejecutar("volumen_por_grupo", "{}"));
    }

    static string Rutina(int ejercicios, int series = 4, int descansoS = 90, string? extra = null, string[]? ids = null)
    {
        ids ??= Catalogo.Ejercicios.Select(e => e.Id).ToArray();
        var lista = Enumerable.Range(0, ejercicios).Select(i => new { id = ids[i], series, reps = 8, pesoKg = (decimal?)null, descansoS });
        var json = JsonSerializer.Serialize(new { nombre = "Hoy", ejercicios = lista, userId = "google:otro" });
        return extra is null ? json : json[..^1] + "," + extra + "}";
    }

    [Fact]
    public async Task ProponerRutina_EstimaMinutos()
    {
        var res = await Herramientas().EjecutarAsync("proponer_rutina", Rutina(3), Ctx());

        using var doc = JsonDocument.Parse(res.Json);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(26, doc.RootElement.GetProperty("minutosEstimados").GetInt32());
        Assert.Equal(26, res.Rutina!.MinutosEstimados);
        Assert.Equal(3, res.Rutina.Rutina.Ejercicios.Count);
        Assert.Equal("Hoy", res.Rutina.Rutina.Nombre);
        Assert.True(Guid.TryParse(res.Rutina.Rutina.Id, out _));
        Assert.Empty(_consultas);
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(2, true)]
    public async Task ProponerRutina_Invalida(int ejercicios, bool repetido)
    {
        var json = repetido ? Rutina(2, ids: ["press-banca", "press-banca"]) : Rutina(ejercicios);

        var res = await Herramientas().EjecutarAsync("proponer_rutina", json, Ctx());

        using var doc = JsonDocument.Parse(res.Json);
        Assert.Equal("rutina_invalida", doc.RootElement.GetProperty("error").GetString());
        Assert.True(doc.RootElement.GetProperty("detalles").GetArrayLength() > 0);
        Assert.Null(res.Rutina);
    }

    [Fact]
    public async Task ProponerRutina_IdFueraDeCatalogo()
    {
        var res = await Herramientas().EjecutarAsync("proponer_rutina", Rutina(1, ids: ["press-inventado"]), Ctx());

        Assert.Contains("rutina_invalida", res.Json);
        Assert.Null(res.Rutina);
    }

    [Fact]
    public async Task ProponerRutina_ExcedeElPerfil()
    {
        var res = await Herramientas().EjecutarAsync("proponer_rutina", Rutina(3), Ctx(minutos: 20));

        using var doc = JsonDocument.Parse(res.Json);
        Assert.Equal("excede_tiempo", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(26, doc.RootElement.GetProperty("minutosEstimados").GetInt32());
        Assert.Equal(20, doc.RootElement.GetProperty("minutosDisponibles").GetInt32());
        Assert.Null(res.Rutina);
    }

    [Fact]
    public async Task ProponerRutina_MinutosObjetivoPriorizaSobreElPerfil()
    {
        var holgado = await Herramientas().EjecutarAsync("proponer_rutina", Rutina(3, extra: "\"minutosObjetivo\":30"), Ctx(minutos: 20));
        Assert.NotNull(holgado.Rutina);

        var justo = await Herramientas().EjecutarAsync("proponer_rutina", Rutina(3, extra: "\"minutosObjetivo\":25"), Ctx(minutos: 60));
        Assert.Contains("excede_tiempo", justo.Json);
        Assert.Null(justo.Rutina);
    }

    [Theory]
    [InlineData("\"minutosObjetivo\":5")]
    [InlineData("\"minutosObjetivo\":\"30\"")]
    public async Task ProponerRutina_MinutosObjetivoInvalido(string extra)
    {
        var res = await Herramientas().EjecutarAsync("proponer_rutina", Rutina(3, extra: extra), Ctx());

        Assert.Contains("parametros_invalidos", res.Json);
        Assert.Null(res.Rutina);
    }

    [Fact]
    public async Task Recordar_UsaElUserIdDelContexto()
    {
        var res = await Herramientas().EjecutarAsync("recordar",
            """{"accion":"fijar_minutos","minutos":45,"userId":"google:otro"}""", Ctx());

        Assert.Equal(45, res.PerfilNuevo!.MinutosDisponibles);
        Assert.Equal(45, _perfiles[UserA].MinutosDisponibles);
        Assert.False(_perfiles.ContainsKey("google:otro"));
        Assert.Empty(_consultas);
    }

    [Fact]
    public void Definiciones_RecordarAceptaAlturaYPesoCorporal()
    {
        using var doc = JsonDocument.Parse(HerramientasCoach.Definiciones.Single(d => d.Nombre == "recordar").ParametrosJson);
        var props = doc.RootElement.GetProperty("properties");
        var acciones = props.GetProperty("accion").GetProperty("enum").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains("fijar_altura", acciones);
        Assert.Contains("fijar_peso", acciones);
        Assert.Equal(100, props.GetProperty("alturaCm").GetProperty("minimum").GetInt32());
        Assert.Contains("no el peso de un ejercicio", props.GetProperty("pesoCorporalKg").GetProperty("description").GetString());
    }

    [Fact]
    public async Task Recordar_FijarAlturaYPeso_GuardaEnElPerfil()
    {
        var res = await Herramientas().EjecutarAsync("recordar", """{"accion":"fijar_altura","alturaCm":180}""", Ctx());
        Assert.Equal(180, res.PerfilNuevo!.AlturaCm);
        res = await Herramientas().EjecutarAsync("recordar", """{"accion":"fijar_peso","pesoCorporalKg":82.5}""", Ctx());
        Assert.Equal(82.5m, _perfiles[UserA].PesoCorporalKg);
        Assert.Equal(180, _perfiles[UserA].AlturaCm);

        res = await Herramientas().EjecutarAsync("recordar", """{"accion":"fijar_peso","pesoCorporalKg":82.55}""", Ctx());
        Assert.Contains("parametros_invalidos", res.Json);
        Assert.Null(res.PerfilNuevo);
    }

    [Fact]
    public void Definiciones_RecordarFijarEquipo_YNuevoBooleano()
    {
        using var recordar = JsonDocument.Parse(HerramientasCoach.Definiciones.Single(d => d.Nombre == "recordar").ParametrosJson);
        var props = recordar.RootElement.GetProperty("properties");
        Assert.Contains("fijar_equipo", props.GetProperty("accion").GetProperty("enum").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(["maquina", "libre", "mixto", null], props.GetProperty("equipo").GetProperty("enum").EnumerateArray().Select(a => a.GetString()));

        using var proponer = JsonDocument.Parse(HerramientasCoach.Definiciones.Single(d => d.Nombre == "proponer_rutina").ParametrosJson);
        var item = proponer.RootElement.GetProperty("properties").GetProperty("ejercicios").GetProperty("items");
        Assert.Equal("boolean", item.GetProperty("properties").GetProperty("nuevo").GetProperty("type").GetString());
        // Opcional: no está en required.
        Assert.DoesNotContain("nuevo", item.GetProperty("required").EnumerateArray().Select(r => r.GetString()));

        using var mis = JsonDocument.Parse(HerramientasCoach.Definiciones.Single(d => d.Nombre == "mis_ejercicios").ParametrosJson);
        Assert.Empty(mis.RootElement.GetProperty("properties").EnumerateObject());
    }

    [Fact]
    public async Task Recordar_FijarEquipo()
    {
        var res = await Herramientas().EjecutarAsync("recordar", """{"accion":"fijar_equipo","equipo":"maquina"}""", Ctx());
        Assert.Equal("maquina", res.PerfilNuevo!.Equipo);
        Assert.Equal("maquina", _perfiles[UserA].Equipo);

        await Herramientas().EjecutarAsync("recordar", """{"accion":"fijar_equipo","equipo":null}""", Ctx());
        Assert.Null(_perfiles[UserA].Equipo);

        foreach (var malo in new[] { """{"accion":"fijar_equipo"}""", """{"accion":"fijar_equipo","equipo":"Maquina"}""" })
            Assert.Contains("parametros_invalidos", (await Herramientas().EjecutarAsync("recordar", malo, Ctx())).Json);
    }

    // Historial de 4 días con 4 ejercicios (el borde de historialSuficiente): 2 libres y 2 de máquina.
    static readonly string[] Conocidos = ["press-banca", "press-inclinado-barra", "cruce-poleas", "prensa"];
    static readonly string[] DeMaquina = ["press-pecho-maquina", "jalon-pecho", "prensa", "extension-cuadriceps"];

    [Fact]
    public async Task MisEjercicios_DesdeCtx_SinConsultar()
    {
        var e = EntrenamientoFalso.Con(5, Conocidos, inferido: "mixto");
        var r = await Ejecutar("mis_ejercicios", """{"userId":"google:otro","extra":1}""", Ctx(entrenamiento: e));

        Assert.Empty(_consultas);
        Assert.Equal(e.CalculadoEl, r.GetProperty("calculadoEl").GetString());
        Assert.False(r.GetProperty("truncado").GetBoolean());
        Assert.Equal("mixto", r.GetProperty("equipoPreferido").GetString());
        Assert.Equal("historial", r.GetProperty("equipoOrigen").GetString());
        var ejs = r.GetProperty("ejercicios");
        Assert.Equal(Conocidos, ejs.EnumerateArray().Select(x => x.GetProperty("ejercicioId").GetString()));
        Assert.Equal(60m, ejs[0].GetProperty("mejorSerie").GetProperty("pesoKg").GetDecimal());
        Assert.Equal(JsonValueKind.Null, ejs[0].GetProperty("ultimaSerie").ValueKind);

        // El equipo del perfil gana sobre el inferido; sin ninguno, null.
        r = await Ejecutar("mis_ejercicios", "", Ctx(entrenamiento: e, equipo: "libre"));
        Assert.Equal(("libre", "perfil"), (r.GetProperty("equipoPreferido").GetString(), r.GetProperty("equipoOrigen").GetString()));
        r = await Ejecutar("mis_ejercicios", "{}", Ctx(entrenamiento: EntrenamientoFalso.Con(5, Conocidos)));
        Assert.Equal(JsonValueKind.Null, r.GetProperty("equipoPreferido").ValueKind);
        Assert.Equal(JsonValueKind.Null, r.GetProperty("equipoOrigen").ValueKind);
    }

    [Fact]
    public async Task MisEjercicios_SinEntrenamiento_NoDisponible() =>
        Assert.Equal("no_disponible", (await Ejecutar("mis_ejercicios", "{}", Ctx() with { Entrenamiento = null })).GetProperty("error").GetString());

    /// <summary>Rutina con nuevo opcional por ejercicio (literal JSON: true, "true", 1...).</summary>
    static string RutinaNuevos(params (string Id, string? Nuevo)[] ejercicios) =>
        "{\"nombre\":\"Hoy\",\"ejercicios\":[" + string.Join(",", ejercicios.Select(e =>
            "{\"id\":\"" + e.Id + "\",\"series\":3,\"reps\":8,\"pesoKg\":null,\"descansoS\":60" +
            (e.Nuevo is null ? "" : ",\"nuevo\":" + e.Nuevo) + "}")) + "]}";

    async Task<(JsonElement Json, RutinaPropuesta? Rutina)> Proponer(string rutina, ContextoCoach ctx, CacheTurno? cache = null)
    {
        var res = await Herramientas().EjecutarAsync("proponer_rutina", rutina, ctx, cache: cache);
        return (JsonDocument.Parse(res.Json).RootElement, res.Rutina);
    }

    static string? ErrorDe(JsonElement r) => r.TryGetProperty("error", out var e) ? e.GetString() : null;

    [Theory]
    [InlineData(3, 4)]
    [InlineData(10, 3)]
    public async Task Guardia_HistorialInsuficiente_NoAplica(int dias, int ejercicios)
    {
        var e = EntrenamientoFalso.Con(dias, Conocidos[..ejercicios], inferido: "maquina");
        var (r, rutina) = await Proponer(RutinaNuevos(("sentadilla", null), ("peso-muerto", null)), Ctx(entrenamiento: e));
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.NotNull(rutina);
    }

    [Fact]
    public async Task Guardia_TodosConocidos_Ok()
    {
        var (r, rutina) = await Proponer(RutinaNuevos(("press-banca", null), ("cruce-poleas", "false")),
            Ctx(entrenamiento: EntrenamientoFalso.Con(4, Conocidos)));
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.NotNull(rutina);
        Assert.Empty(_consultas);
    }

    [Fact]
    public async Task Guardia_DesconocidoSinNuevo_FueraDeTuHistorial()
    {
        var (r, rutina) = await Proponer(RutinaNuevos(("press-banca", null), ("sentadilla", null)),
            Ctx(entrenamiento: EntrenamientoFalso.Con(4, Conocidos)));
        Assert.Equal("fuera_de_tu_historial", ErrorDe(r));
        Assert.Equal(["sentadilla"], r.GetProperty("ejercicios").EnumerateArray().Select(x => x.GetString()));
        // Alternativas: del historial, sin las que ya están en la rutina.
        Assert.Equal(["press-inclinado-barra", "cruce-poleas", "prensa"], r.GetProperty("permitidos").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("permitidos", r.GetProperty("detalle").GetString());
        Assert.Null(rutina);
    }

    [Fact]
    public async Task Guardia_UnNuevoDelMismoEquipo_Ok()
    {
        var (r, rutina) = await Proponer(RutinaNuevos(("prensa", null), ("curl-femoral", "true")),
            Ctx(entrenamiento: EntrenamientoFalso.Con(6, DeMaquina, inferido: "maquina")));
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal(2, rutina!.Rutina.Ejercicios.Count);
    }

    [Fact]
    public async Task Guardia_DosNuevos_FueraDeTuHistorial()
    {
        var (r, rutina) = await Proponer(RutinaNuevos(("prensa", null), ("curl-femoral", "true"), ("pec-deck", "true")),
            Ctx(entrenamiento: EntrenamientoFalso.Con(6, DeMaquina, inferido: "maquina")));
        Assert.Equal("fuera_de_tu_historial", ErrorDe(r));
        Assert.Equal(["curl-femoral", "pec-deck"], r.GetProperty("ejercicios").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["press-pecho-maquina", "jalon-pecho", "extension-cuadriceps"], r.GetProperty("permitidos").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("deja uno", r.GetProperty("detalle").GetString());
        Assert.Null(rutina);
    }

    [Fact]
    public async Task Guardia_NuevoLibreConEquipoMaquina_FueraDeTuHistorial()
    {
        var maquina = EntrenamientoFalso.Con(6, DeMaquina, inferido: "maquina");
        var (r, _) = await Proponer(RutinaNuevos(("prensa", null), ("sentadilla", "true")), Ctx(entrenamiento: maquina));
        Assert.Equal("fuera_de_tu_historial", ErrorDe(r));
        Assert.Equal(["sentadilla"], r.GetProperty("ejercicios").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("maquina", r.GetProperty("equipo").GetString());
        Assert.Equal(3, r.GetProperty("permitidos").GetArrayLength());

        // El equipo del perfil gana sobre el inferido.
        (r, _) = await Proponer(RutinaNuevos(("prensa", null), ("sentadilla", "true")), Ctx(entrenamiento: maquina, equipo: "libre"));
        Assert.True(r.GetProperty("ok").GetBoolean());
        (r, _) = await Proponer(RutinaNuevos(("prensa", null), ("curl-femoral", "true")), Ctx(entrenamiento: maquina, equipo: "libre"));
        Assert.Equal("fuera_de_tu_historial", ErrorDe(r));
    }

    [Theory]
    [InlineData("mixto", null)]
    [InlineData(null, "mixto")]
    [InlineData(null, null)]
    public async Task Guardia_EquipoMixtoONull_NoFiltraEquipo(string? inferido, string? equipoPerfil)
    {
        var (r, _) = await Proponer(RutinaNuevos(("prensa", null), ("sentadilla", "true")),
            Ctx(entrenamiento: EntrenamientoFalso.Con(6, DeMaquina, inferido: inferido), equipo: equipoPerfil));
        Assert.True(r.GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("{}")]
    public async Task Guardia_NuevoNoBooleano_ParametrosInvalidos(string nuevo)
    {
        var (r, rutina) = await Proponer(RutinaNuevos(("prensa", null), ("sentadilla", nuevo)),
            Ctx(entrenamiento: EntrenamientoFalso.Con(6, DeMaquina)));
        Assert.Equal("parametros_invalidos", ErrorDe(r));
        Assert.Null(rutina);
    }

    [Fact]
    public async Task Guardia_NuevoSobreConocido_NoCuentaComoNuevo()
    {
        // Dos nuevo: true sobre conocidos y uno de verdad: es 1 nuevo, pasa.
        var (r, _) = await Proponer(RutinaNuevos(("prensa", "true"), ("jalon-pecho", "true"), ("curl-femoral", "true")),
            Ctx(entrenamiento: EntrenamientoFalso.Con(6, DeMaquina, inferido: "maquina")));
        Assert.True(r.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Guardia_PermitidosHasta15_PorSesiones()
    {
        // 20 ejercicios del catálogo (orden = sesiones desc en el perfil): permitidos son los primeros 15 que no están en la rutina.
        var veinte = Catalogo.Ejercicios.Select(x => x.Id).Where(id => id != "sentadilla").Take(20).ToArray();
        var (r, _) = await Proponer(RutinaNuevos((veinte[0], null), ("sentadilla", null)),
            Ctx(entrenamiento: EntrenamientoFalso.Con(6, veinte)));
        Assert.Equal(veinte[1..16], r.GetProperty("permitidos").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Guardia_RecientesDelTurnoCuentanComoConocidos()
    {
        _devolver = [S("remo-barra", 50, 8, 1)];
        var ctx = Ctx(entrenamiento: EntrenamientoFalso.Con(4, Conocidos));
        var rutina = RutinaNuevos(("press-banca", null), ("remo-barra", null));

        // Sin haber consultado recientes en el turno: desconocido.
        Assert.Equal("fuera_de_tu_historial", ErrorDe((await Proponer(rutina, ctx)).Json));

        var cache = new CacheTurno();
        await Herramientas().EjecutarAsync("ver_entrenos_recientes", "{}", ctx, cache: cache);
        var (r, _) = await Proponer(rutina, ctx, cache);
        Assert.True(r.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Guardia_SinEntrenamiento_NoDisponible()
    {
        var (r, rutina) = await Proponer(RutinaNuevos(("press-banca", null)), Ctx() with { Entrenamiento = null });
        Assert.Equal("no_disponible", ErrorDe(r));
        Assert.Null(rutina);
    }

    [Fact]
    public async Task Guardia_OrdenDeErrores()
    {
        var ctx = Ctx(minutos: 10, entrenamiento: EntrenamientoFalso.Con(4, Conocidos));
        // parametros_invalidos antes que rutina_invalida (id fuera del catálogo).
        Assert.Equal("parametros_invalidos", ErrorDe((await Proponer(RutinaNuevos(("press-inventado", "\"si\"")), ctx)).Json));
        // rutina_invalida antes que fuera_de_tu_historial y que no_disponible (ids con mayúsculas no están en el catálogo).
        Assert.Equal("rutina_invalida", ErrorDe((await Proponer(RutinaNuevos(("press-inventado", null), ("sentadilla", null)), ctx)).Json));
        Assert.Equal("rutina_invalida", ErrorDe((await Proponer(RutinaNuevos(("Press-Banca", null)), ctx with { Entrenamiento = null })).Json));
        // fuera_de_tu_historial antes que excede_tiempo (10 minutos no alcanzan).
        var muchas = RutinaNuevos(("press-banca", null), ("press-inclinado-barra", null), ("cruce-poleas", null), ("sentadilla", null));
        Assert.Equal("fuera_de_tu_historial", ErrorDe((await Proponer(muchas, ctx)).Json));
        var conocidas = RutinaNuevos(("press-banca", null), ("press-inclinado-barra", null), ("cruce-poleas", null), ("prensa", null));
        Assert.Equal("excede_tiempo", ErrorDe((await Proponer(conocidas, ctx)).Json));
    }

    [Fact]
    public void DefinicionesPlan_SinProponerPrograma()
    {
        Assert.Equal(HerramientasCoach.Definiciones.Where(d => d.Nombre != "proponer_programa"), HerramientasCoach.DefinicionesPlan);
        Assert.Equal(6, HerramientasCoach.DefinicionesPlan.Count);

        using var doc = JsonDocument.Parse(HerramientasCoach.Definiciones[^1].ParametrosJson);
        var rutinas = doc.RootElement.GetProperty("properties").GetProperty("rutinas");
        Assert.Equal((2, 4), (rutinas.GetProperty("minItems").GetInt32(), rutinas.GetProperty("maxItems").GetInt32()));
        // El objeto rutina es el mismo de proponer_rutina.
        using var rutina = JsonDocument.Parse(HerramientasCoach.Definiciones.Single(d => d.Nombre == "proponer_rutina").ParametrosJson);
        Assert.Equal(rutina.RootElement.GetProperty("properties").GetProperty("ejercicios").GetRawText(),
            rutinas.GetProperty("items").GetProperty("properties").GetProperty("ejercicios").GetRawText());
        Assert.Equal(["nombre", "rutinas"], doc.RootElement.GetProperty("required").EnumerateArray().Select(r => r.GetString()));
    }

    /// <summary>Programa con rutinas de ejercicios (id, nuevo literal o null); extra se agrega al objeto raíz.</summary>
    static string Programa(string? extra, params (string Id, string? Nuevo)[][] rutinas) =>
        "{\"nombre\":\"Full body\",\"rutinas\":[" + string.Join(",", rutinas.Select((r, i) =>
            RutinaNuevos(r).Replace("\"nombre\":\"Hoy\"", $"\"nombre\":\"Día {i + 1}\""))) + "]" + (extra is null ? "" : "," + extra) + "}";

    async Task<(JsonElement Json, ProgramaPropuesto? Programa)> ProponerPrograma(string programa, ContextoCoach ctx)
    {
        var res = await Herramientas().EjecutarAsync("proponer_programa", programa, ctx);
        Assert.Null(res.Rutina);
        return (JsonDocument.Parse(res.Json).RootElement, res.Programa);
    }

    [Fact]
    public async Task ProponerPrograma_DosRutinas_Ok()
    {
        var (r, programa) = await ProponerPrograma(Programa(null, [("press-banca", null), ("remo-barra", null)], [("prensa", null)]), Ctx());

        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal(["Día 1", "Día 2"], r.GetProperty("rutinas").EnumerateArray().Select(x => x.GetProperty("nombre").GetString()));
        Assert.Equal("Full body", programa!.Nombre);
        Assert.Equal(2, programa.Rutinas.Count);
        Assert.NotEqual(programa.Rutinas[0].Rutina.Id, programa.Rutinas[1].Rutina.Id);
        Assert.All(programa.Rutinas, p => Assert.True(p.MinutosEstimados > 0));
        Assert.Equal(programa.Rutinas[1].MinutosEstimados, r.GetProperty("rutinas")[1].GetProperty("minutosEstimados").GetInt32());
        Assert.Empty(_consultas);
    }

    [Fact]
    public async Task ProponerPrograma_UnaRutina_ParametrosInvalidos()
    {
        var (r, programa) = await ProponerPrograma(Programa(null, [("press-banca", null)]), Ctx());
        Assert.Equal("parametros_invalidos", ErrorDe(r));
        Assert.Contains("rutinas", r.GetProperty("detalle").GetString());
        Assert.Null(programa);
    }

    [Fact]
    public async Task ProponerPrograma_CincoRutinas_ParametrosInvalidos()
    {
        (string, string?)[] una = [("prensa", null)];
        var (r, programa) = await ProponerPrograma(Programa(null, una, una, una, una, una), Ctx());
        Assert.Equal("parametros_invalidos", ErrorDe(r));
        Assert.Null(programa);
    }

    [Theory]
    [InlineData("""{"rutinas":[]}""")]
    [InlineData("""{"nombre":"","rutinas":[]}""")]
    [InlineData("""{"nombre":"P","rutinas":[1,2]}""")]
    public async Task ProponerPrograma_NombreORutinasMalos_ParametrosInvalidos(string args) =>
        Assert.Equal("parametros_invalidos", ErrorDe((await ProponerPrograma(args, Ctx())).Json));

    [Fact]
    public async Task ProponerPrograma_NuevoNoBooleanoEnLaSegunda_GanaARutinaInvalidaEnLaPrimera()
    {
        var (r, _) = await ProponerPrograma(Programa(null, [("press-inventado", null)], [("prensa", "\"true\"")]), Ctx());
        Assert.Equal("parametros_invalidos", ErrorDe(r));
        Assert.Equal("rutinas[1].ejercicios[0].nuevo: debe ser booleano.", r.GetProperty("detalle").GetString());
    }

    [Fact]
    public async Task ProponerPrograma_RutinaInvalida_TraeIndice()
    {
        var programa = """{"nombre":"Full body","rutinas":[{"nombre":"A","ejercicios":[{"id":"prensa","series":3,"reps":8,"pesoKg":null,"descansoS":60}]},{"nombre":"B","ejercicios":[]}]}""";
        var res = await Herramientas().EjecutarAsync("proponer_programa", programa, Ctx());

        Assert.StartsWith("""{"error":"rutina_invalida""", res.Json);
        Assert.EndsWith("\"rutina\":1}", res.Json);
        Assert.Null(res.Programa);
    }

    [Fact]
    public async Task ProponerPrograma_UnNuevoPorRutina_Ok()
    {
        var e = EntrenamientoFalso.Con(6, DeMaquina, inferido: "maquina");
        var (r, programa) = await ProponerPrograma(
            Programa(null, [("prensa", null), ("curl-femoral", "true")], [("jalon-pecho", null), ("pec-deck", "true")]), Ctx(entrenamiento: e));
        Assert.True(r.GetProperty("ok").GetBoolean());
        Assert.Equal(2, programa!.Rutinas.Count);
    }

    [Fact]
    public async Task ProponerPrograma_DosNuevosEnUna_FueraDeTuHistorialConIndice()
    {
        var e = EntrenamientoFalso.Con(6, DeMaquina, inferido: "maquina");
        var res = await Herramientas().EjecutarAsync("proponer_programa",
            Programa(null, [("prensa", null)], [("jalon-pecho", null), ("curl-femoral", "true"), ("pec-deck", "true")]), Ctx(entrenamiento: e));

        Assert.StartsWith("""{"error":"fuera_de_tu_historial""", res.Json);
        using var doc = JsonDocument.Parse(res.Json);
        Assert.Equal(1, doc.RootElement.GetProperty("rutina").GetInt32());
        Assert.Equal(["curl-femoral", "pec-deck"], doc.RootElement.GetProperty("ejercicios").EnumerateArray().Select(x => x.GetString()));
        Assert.Null(res.Programa);
    }

    [Fact]
    public async Task ProponerPrograma_ExcedeTiempo_TraeIndice()
    {
        // La rutina 1 (3 ejercicios × 3 series × (60 + 40) s = 15 min) no cabe en 10; la 0 sí.
        var (r, programa) = await ProponerPrograma(
            Programa("\"minutosObjetivo\":10", [("prensa", null)], [("prensa", null), ("press-banca", null), ("remo-barra", null)]), Ctx(minutos: 60));
        Assert.Equal("excede_tiempo", ErrorDe(r));
        Assert.Equal(10, r.GetProperty("minutosDisponibles").GetInt32());
        Assert.Equal(1, r.GetProperty("rutina").GetInt32());
        Assert.Null(programa);
    }

    [Fact]
    public async Task ProponerPrograma_SinEntrenamiento_NoDisponible()
    {
        var (r, programa) = await ProponerPrograma(Programa(null, [("prensa", null)], [("press-banca", null)]), Ctx() with { Entrenamiento = null });
        Assert.Equal("""{"error":"no_disponible"}""", r.GetRawText());
        Assert.Null(programa);
    }

    [Fact]
    public async Task Recordar_FijarExperienciaYDias()
    {
        var res = await Herramientas().EjecutarAsync("recordar", """{"accion":"fijar_experiencia","experiencia":"menos6m"}""", Ctx());
        Assert.Equal("menos6m", res.PerfilNuevo!.Experiencia);
        res = await Herramientas().EjecutarAsync("recordar", """{"accion":"fijar_dias","diasPorSemana":3}""", Ctx());
        Assert.Equal(3, res.PerfilNuevo!.DiasPorSemana);
        Assert.Equal(("menos6m", 3), (_perfiles[UserA].Experiencia, _perfiles[UserA].DiasPorSemana));

        using var doc = JsonDocument.Parse(HerramientasCoach.Definiciones.Single(d => d.Nombre == "recordar").ParametrosJson);
        var props = doc.RootElement.GetProperty("properties");
        var acciones = props.GetProperty("accion").GetProperty("enum").EnumerateArray().Select(a => a.GetString()).ToList();
        Assert.Contains("fijar_experiencia", acciones);
        Assert.Contains("fijar_dias", acciones);
        Assert.Equal(["nunca", "menos6m", "mas6m", null], props.GetProperty("experiencia").GetProperty("enum").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(7, props.GetProperty("diasPorSemana").GetProperty("maximum").GetInt32());
    }

    [Fact]
    public async Task Cancelado_NoConsulta()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Herramientas().EjecutarAsync("ver_entrenos_recientes", "{}", Ctx(), new CancellationToken(true)));
        Assert.Empty(_consultas);
    }
}
