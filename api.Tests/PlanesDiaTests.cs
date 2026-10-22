using System.Text;
using Microsoft.Azure.Cosmos;

namespace GymCoach.Api.Tests;

/// <summary>Docs plan-dia en un diccionario con ETag: simula Cosmos, incluidos el 409 del create y el 412 del replace.</summary>
public sealed class PlanesFalsos
{
    public readonly Dictionary<(string UserId, string Id), (PlanDiaDoc Doc, string ETag)> Docs = [];
    public int Creados, Reemplazos, Borrados;
    /// <summary>Cuántas lecturas más devuelven "no existe" aunque exista (un lector que llegó antes del create de otro).</summary>
    public int LecturasViejas;
    /// <summary>La próxima lectura devuelve esto (doc y ETag de antes): un lector que llegó antes del lease de otro.</summary>
    public (PlanDiaDoc Doc, string ETag)? LecturaFija;
    /// <summary>Se ejecuta antes de cada replace (para meter un escritor concurrente).</summary>
    public Action<PlanDiaDoc>? AntesDeReemplazar;
    int _version;

    public void Sembrar(PlanDiaDoc doc) => Docs[(doc.UserId, doc.Id)] = (doc, "e" + ++_version);

    public PlanDiaDoc? Doc(string userId, DateOnly hoy) =>
        Docs.TryGetValue((userId, PlanDiaDoc.IdDoc(hoy)), out var d) ? d.Doc : null;

    internal PlanesDia Crear(ConversacionCoach conversacion, PerfilesCoach perfiles, Limites limites, TimeProvider reloj,
        Func<string, DateOnly, int?, CancellationToken, Task<PerfilEntrenamiento?>>? entrenamiento = null,
        Microsoft.Extensions.Logging.ILogger<PlanesDia>? log = null) => new(
        (userId, id, _) =>
        {
            if (LecturaFija is { } fija)
            {
                LecturaFija = null;
                return Task.FromResult<(PlanDiaDoc?, string?)>((fija.Doc, fija.ETag));
            }
            if (LecturasViejas > 0)
            {
                LecturasViejas--;
                return Task.FromResult<(PlanDiaDoc?, string?)>((null, null));
            }
            return Task.FromResult<(PlanDiaDoc?, string?)>(Docs.TryGetValue((userId, id), out var d) ? (d.Doc, d.ETag) : (null, null));
        },
        (doc, _) =>
        {
            Creados++;
            if (Docs.ContainsKey((doc.UserId, doc.Id))) return Task.FromResult<string?>(null);
            Sembrar(doc);
            return Task.FromResult<string?>(Docs[(doc.UserId, doc.Id)].ETag);
        },
        (doc, etag, _) =>
        {
            Reemplazos++;
            AntesDeReemplazar?.Invoke(doc);
            if (!Docs.TryGetValue((doc.UserId, doc.Id), out var d) || d.ETag != etag) return Task.FromResult<string?>(null);
            Sembrar(doc);
            return Task.FromResult<string?>(Docs[(doc.UserId, doc.Id)].ETag);
        },
        (userId, id, etag, _) =>
        {
            Borrados++;
            if (!Docs.TryGetValue((userId, id), out var d) || d.ETag != etag) return Task.FromResult(false);
            Docs.Remove((userId, id));
            return Task.FromResult(true);
        },
        conversacion, perfiles, SinPerfilEntrenamiento(reloj), limites, reloj,
        entrenamiento ?? ((_, _, _, _) => Task.FromResult<PerfilEntrenamiento?>(PerfilVacio)), log);

    /// <summary>Historial insuficiente: sin guardia de ejercicios conocidos y sin pesos sembrados.</summary>
    public static readonly PerfilEntrenamiento PerfilVacio = new(1, "2026-10-09T12:00:00.000Z", "2026-10-09", 0, false, null, null, 0,
        null, new(null, null, 0), [], [], []);

    // Las pruebas pasan leerEntrenamiento: esta instancia no se llama.
    static PerfilesEntrenamiento SinPerfilEntrenamiento(TimeProvider reloj) => new(
        (_, _) => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException(),
        (_, _, _) => throw new InvalidOperationException(), (_, _) => throw new InvalidOperationException(), _ => false, reloj);
}

/// <summary>PlanesDia con el modelo guionado y Cosmos en memoria: lease, tope, cupos y limpieza ante fallas.</summary>
public class PlanesDiaTests
{
    const string Usuario = "google:123";
    static readonly DateOnly Hoy = new(2026, 10, 9);
    readonly RelojFalso _reloj = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    readonly PlanesFalsos _planes = new();
    readonly PerfilesFalsos _perfiles = new();
    readonly Limites _limites;

    public PlanesDiaTests() => _limites = new Limites(_reloj);

    PlanesDia Planes(IModeloCoach modelo)
    {
        var p = _perfiles.Crear();
        var herramientas = new HerramientasCoach((QueryDefinition _, string _) => Task.FromResult(new List<Serie>()), p);
        return _planes.Crear(new ConversacionCoach(modelo, herramientas), p, _limites, _reloj);
    }

    static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    static VariacionPlan Variacion(string texto, RutinaPlan? rutina = null) => new(texto, [], rutina, "2026-10-09T11:00:00.000Z");

    PlanDiaDoc Doc(int variaciones, DateTimeOffset? generandoHasta = null) => new(PlanDiaDoc.IdDoc(Hoy), Usuario, PlanDiaDoc.TipoDoc,
        "2026-10-09", generandoHasta is { } g ? Iso(g) : null,
        [.. Enumerable.Range(1, variaciones).Select(i => Variacion("plan " + i))], "2026-10-09T11:00:00.000Z");

    /// <summary>Cuántos mensajes diarios le quedan al usuario (los gasta: va al final de la prueba).</summary>
    int CupoDiarioRestante()
    {
        var n = 0;
        while (_limites.CoachDiario.Permitir(Usuario, out _)) n++;
        return n;
    }

    static string UltimoMensajeUsuario(ModeloGuionado modelo) => modelo.Llamadas[0].Mensajes.Last(m => m.Rol == "user").Texto!;

    sealed class ModeloQueFalla : IModeloCoach
    {
        public Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
            bool permitirHerramientas, CancellationToken ct) => throw new ModeloNoDisponibleException("429");
    }

    sealed class ModeloQueEsperaCancelacion : IModeloCoach
    {
        public Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
            bool permitirHerramientas, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RespuestaModelo("ok", []));
        }
    }

    /// <summary>Responde cuando la prueba suelta la puerta: deja un lease vivo mientras corre otro POST.</summary>
    sealed class ModeloBloqueado : IModeloCoach
    {
        public readonly TaskCompletionSource Llamado = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Puerta = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Llamadas;

        public async Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
            bool permitirHerramientas, CancellationToken ct)
        {
            Interlocked.Increment(ref Llamadas);
            Llamado.TrySetResult();
            await Puerta.Task;
            return ModeloGuionado.ConRutina("Hoy: pierna.")(mensajes);
        }
    }

    [Fact]
    public async Task SinDoc_GeneraConTuEntrenoDeHoy()
    {
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("Hoy toca pierna."));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, false);

        Assert.Equal(EstadoPlanDia.Listo, r.Estado);
        // Un turno: proponer_rutina y luego el texto.
        Assert.Equal(2, modelo.Llamadas.Count);
        Assert.Equal(PlanesDia.MensajePlan, UltimoMensajeUsuario(modelo));
        Assert.Equal("Hoy toca pierna.", r.Plan!.Actual!.Texto);
        Assert.Equal((1, 2, false, "2026-10-09"), (r.Plan.Numero, r.Plan.Restantes, r.Plan.Generando, r.Plan.Fecha));

        var doc = _planes.Doc(Usuario, Hoy)!;
        Assert.Null(doc.GenerandoHasta);
        Assert.Single(doc.Variaciones);
        Assert.Equal("plan-dia:2026-10-09", doc.Id);
        Assert.Equal(Limites.CoachPorDia - 1, CupoDiarioRestante());
    }

    [Fact]
    public async Task ConPlan_NoLlamaAlModeloNiGastaCupo()
    {
        _planes.Sembrar(Doc(1));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("x"));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, false);

        Assert.Equal(EstadoPlanDia.Listo, r.Estado);
        Assert.Equal("plan 1", r.Plan!.Actual!.Texto);
        Assert.Empty(modelo.Llamadas);
        Assert.Equal(0, _limites.Coach.Claves);
        Assert.Equal(0, _limites.CoachDiario.Claves);
        Assert.Equal(0, _planes.Reemplazos + _planes.Creados);
    }

    [Fact]
    public async Task LeaseVivo_DevuelveGenerandoSinModelo()
    {
        _planes.Sembrar(Doc(1, _reloj.Ahora.AddSeconds(30)));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("x"));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, true);

        Assert.True(r.Plan!.Generando);
        Assert.Empty(modelo.Llamadas);
        Assert.Equal(0, _limites.CoachDiario.Claves);
    }

    [Fact]
    public async Task LeaseVencido_SePuedeTomar()
    {
        // Una función que murió a mitad: doc sin variaciones con el lease ya vencido.
        _planes.Sembrar(Doc(0, _reloj.Ahora.AddSeconds(-1)));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("Hoy: espalda."));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, false);

        // Un turno: proponer_rutina y luego el texto.
        Assert.Equal(2, modelo.Llamadas.Count);
        Assert.Equal("Hoy: espalda.", r.Plan!.Actual!.Texto);
        Assert.False(r.Plan.Generando);
    }

    [Fact]
    public async Task DosConcurrentes_UnaSolaLlamadaYElSegundoVeGenerando()
    {
        var modelo = new ModeloBloqueado();
        var planes = Planes(modelo);
        var primero = planes.ObtenerOGenerarAsync(Usuario, Hoy, null, false);
        await modelo.Llamado.Task;

        // El segundo leyó antes del create del primero: su create da 409.
        _planes.LecturasViejas = 1;
        var segundo = await planes.ObtenerOGenerarAsync(Usuario, Hoy, null, false);
        Assert.Equal(EstadoPlanDia.Listo, segundo.Estado);
        Assert.True(segundo.Plan!.Generando);
        Assert.Null(segundo.Plan.Actual);
        Assert.Equal(2, _planes.Creados);

        modelo.Puerta.SetResult();
        var r = await primero;
        Assert.Equal("Hoy: pierna.", r.Plan!.Actual!.Texto);
        Assert.Equal(2, modelo.Llamadas);
        // El segundo devolvió su mensaje diario: solo cuenta el que llamó al modelo.
        Assert.Equal(Limites.CoachPorDia - 1, CupoDiarioRestante());
    }

    [Fact]
    public async Task OtraOpcion_AgregaVariacionYBajaRestantes()
    {
        _planes.Sembrar(Doc(1));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("Mejor torso."));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, true);

        Assert.Equal(PlanesDia.MensajeOtraOpcion, UltimoMensajeUsuario(modelo));
        Assert.Equal((2, 1), (r.Plan!.Numero, r.Plan.Restantes));
        Assert.Equal("Mejor torso.", r.Plan.Actual!.Texto);
        var doc = _planes.Doc(Usuario, Hoy)!;
        Assert.Equal(["plan 1", "Mejor torso."], doc.Variaciones.Select(v => v.Texto));
        Assert.Null(doc.GenerandoHasta);
    }

    [Fact]
    public async Task OtraOpcionSinPlan_EsLaPrimeraGeneracion()
    {
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("ok"));
        await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, true);
        Assert.Equal(PlanesDia.MensajePlan, UltimoMensajeUsuario(modelo));
    }

    [Fact]
    public async Task CuartaGeneracion_TopeSinModeloNiCupo()
    {
        _planes.Sembrar(Doc(PlanesDia.MaxGeneraciones));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("x"));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, true);

        Assert.Equal(EstadoPlanDia.TopeVariaciones, r.Estado);
        Assert.Empty(modelo.Llamadas);
        Assert.Equal(0, _limites.Coach.Claves);
    }

    [Fact]
    public async Task TresGeneraciones_LaCuartaDaTope()
    {
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("ok"));
        var planes = Planes(modelo);
        for (var i = 0; i < PlanesDia.MaxGeneraciones; i++)
            Assert.Equal(EstadoPlanDia.Listo, (await planes.ObtenerOGenerarAsync(Usuario, Hoy, null, i > 0)).Estado);
        Assert.Equal(EstadoPlanDia.TopeVariaciones, (await planes.ObtenerOGenerarAsync(Usuario, Hoy, null, true)).Estado);
        Assert.Equal(2 * PlanesDia.MaxGeneraciones, modelo.Llamadas.Count);
    }

    [Fact]
    public async Task Al412AlGuardar_DevuelveLoQueGano()
    {
        var ganador = Doc(1) with { Variaciones = [Variacion("el que ganó")] };
        // Justo antes del replace final (sin lease) otro escribe: el ETag del lease ya no vale.
        _planes.AntesDeReemplazar = d =>
        {
            if (d.GenerandoHasta is null && d.Variaciones.Count == 1 && d.Variaciones[0].Texto == "perdedor") _planes.Sembrar(ganador);
        };
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("perdedor"));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, false);

        Assert.Equal(EstadoPlanDia.Listo, r.Estado);
        Assert.Equal("el que ganó", r.Plan!.Actual!.Texto);
        Assert.Equal("el que ganó", _planes.Doc(Usuario, Hoy)!.Variaciones[^1].Texto);
        // Los tokens se gastaron: el mensaje diario no se devuelve.
        Assert.Equal(Limites.CoachPorDia - 1, CupoDiarioRestante());
    }

    [Fact]
    public async Task FallaElModeloEnLaPrimera_BorraElDocYLiberaElCupo()
    {
        var r = await Planes(new ModeloQueFalla()).ObtenerOGenerarAsync(Usuario, Hoy, null, false);

        Assert.Equal(EstadoPlanDia.NoDisponible, r.Estado);
        Assert.Null(_planes.Doc(Usuario, Hoy));
        Assert.Equal(1, _planes.Borrados);
        Assert.Equal(Limites.CoachPorDia, CupoDiarioRestante());
    }

    [Fact]
    public async Task FallaUnaVariacion_ConservaLaAnteriorYLimpiaElLease()
    {
        _planes.Sembrar(Doc(1));
        var r = await Planes(new ModeloQueFalla()).ObtenerOGenerarAsync(Usuario, Hoy, null, true);

        Assert.Equal(EstadoPlanDia.NoDisponible, r.Estado);
        var doc = _planes.Doc(Usuario, Hoy)!;
        Assert.Equal(["plan 1"], doc.Variaciones.Select(v => v.Texto));
        Assert.Null(doc.GenerandoHasta);
        Assert.Equal(Limites.CoachPorDia, CupoDiarioRestante());
    }

    [Fact]
    public async Task ClienteCancela_SueltaElLeasePeroNoLiberaElCupo()
    {
        using var cortada = new CancellationTokenSource();
        cortada.Cancel();
        var r = await Planes(new ModeloQueEsperaCancelacion()).ObtenerOGenerarAsync(Usuario, Hoy, null, false, cortada.Token);

        Assert.Equal(EstadoPlanDia.NoDisponible, r.Estado);
        Assert.Null(_planes.Doc(Usuario, Hoy));
        // Abortar a propósito no debe dar generaciones ilimitadas.
        Assert.Equal(Limites.CoachPorDia - 1, CupoDiarioRestante());
    }

    [Fact]
    public async Task CupoCoachAgotado_SinCupoYSinTomarElLease()
    {
        for (var i = 0; i < Limites.CoachPorMinuto; i++) Assert.True(_limites.Coach.Permitir(Usuario, out _));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("x"));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, false);

        Assert.Equal(EstadoPlanDia.SinCupo, r.Estado);
        Assert.InRange(r.ReintentarEn, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
        Assert.Equal(0, _planes.Creados);
        Assert.Empty(modelo.Llamadas);
        Assert.Equal(Limites.CoachPorDia, CupoDiarioRestante());
    }

    [Fact]
    public async Task CupoDiarioAgotado_SinCupo()
    {
        while (_limites.CoachDiario.Permitir(Usuario, out _)) { }
        var r = await Planes(new ModeloGuionado(ModeloGuionado.ConRutina("x"))).ObtenerOGenerarAsync(Usuario, Hoy, null, false);
        Assert.Equal(EstadoPlanDia.SinCupo, r.Estado);
        Assert.Equal(0, _planes.Creados);
    }

    [Fact]
    public async Task CosmosSaturadoAlLeerElPerfilDelCoach_NoDisponibleYBorraElLease()
    {
        var saturado = new PerfilesCoach(
            (_, _) => throw new CosmosException("429", System.Net.HttpStatusCode.TooManyRequests, 0, "", 0),
            (_, _, _, _) => Task.FromResult(true));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("x"));
        var herramientas = new HerramientasCoach((QueryDefinition _, string _) => Task.FromResult(new List<Serie>()), saturado);
        var planes = _planes.Crear(new ConversacionCoach(modelo, herramientas), saturado, _limites, _reloj);

        Assert.Equal(EstadoPlanDia.NoDisponible, (await planes.ObtenerOGenerarAsync(Usuario, Hoy, null, false)).Estado);
        Assert.Null(_planes.Doc(Usuario, Hoy));
        Assert.Equal(Limites.CoachPorDia, CupoDiarioRestante());
    }

    [Fact]
    public async Task PerfilDeEntrenamientoRecibeElOffset()
    {
        int? recibido = null;
        var p = _perfiles.Crear();
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("ok"));
        var herramientas = new HerramientasCoach((QueryDefinition _, string _) => Task.FromResult(new List<Serie>()), p);
        var planes = _planes.Crear(new ConversacionCoach(modelo, herramientas), p, _limites, _reloj, (_, _, off, _) =>
        {
            recibido = off;
            return Task.FromResult<PerfilEntrenamiento?>(PlanesFalsos.PerfilVacio);
        });
        await planes.ObtenerOGenerarAsync(Usuario, Hoy, -360, false);
        Assert.Equal(-360, recibido);
    }

    [Fact]
    public async Task RutinaGuardada_ConPesosFiltrados()
    {
        // pesoKg que el coach nunca vio en una herramienta: se guarda como null.
        const string rutina = """{"nombre":"Pecho","ejercicios":[{"id":"press-banca","series":3,"reps":8,"pesoKg":80,"descansoS":90}]}""";
        var modelo = new ModeloGuionado(ModeloGuionado.Pide(("proponer_rutina", rutina)), ModeloGuionado.ConRutina("Listo."));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, false);

        var guardada = _planes.Doc(Usuario, Hoy)!.Variaciones[0].Rutina!;
        Assert.Equal("Pecho", guardada.Nombre);
        Assert.Null(guardada.Ejercicios[0].PesoKg);
        Assert.True(guardada.MinutosEstimados > 0);
        Assert.Equal(["proponer_rutina"], r.Plan!.Actual!.Herramientas);
        Assert.Equal(guardada, r.Plan.Actual.Rutina);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task RespuestaFiltrada_NoGuardaVariacionYLiberaElCupo(int previas)
    {
        if (previas > 0) _planes.Sembrar(Doc(previas));
        var modelo = new ModeloGuionado(_ => new RespuestaModelo(null, [], Filtrada: true));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, true);

        Assert.Equal(EstadoPlanDia.NoDisponible, r.Estado);
        var doc = _planes.Doc(Usuario, Hoy);
        if (previas == 0) Assert.Null(doc);
        else
        {
            Assert.Equal(previas, doc!.Variaciones.Count);
            Assert.Null(doc.GenerandoHasta);
        }
        Assert.Equal(Limites.CoachPorDia, CupoDiarioRestante());
    }

    [Fact]
    public async Task RespuestaSinTexto_NoGuardaVariacion()
    {
        var r = await Planes(new ModeloGuionado(ModeloGuionado.Texto("  "))).ObtenerOGenerarAsync(Usuario, Hoy, null, false);
        Assert.Equal(EstadoPlanDia.NoDisponible, r.Estado);
        Assert.Null(_planes.Doc(Usuario, Hoy));
        Assert.Equal(Limites.CoachPorDia, CupoDiarioRestante());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SinRutina_NoGuardaVariacionYLiberaElCupo(int previas)
    {
        // Texto sin proponer_rutina, también tras la ronda correctiva: un plan sin tarjeta no es un plan.
        if (previas > 0) _planes.Sembrar(Doc(previas));
        var modelo = new ModeloGuionado(ModeloGuionado.Texto("Hoy toca pierna."));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, previas > 0);

        Assert.Equal(EstadoPlanDia.NoDisponible, r.Estado);
        Assert.Equal([null, "proponer_rutina"], modelo.Obligadas);
        var doc = _planes.Doc(Usuario, Hoy);
        if (previas == 0) Assert.Null(doc);
        else
        {
            Assert.Equal(previas, doc!.Variaciones.Count);
            Assert.Null(doc.GenerandoHasta);
        }
        Assert.Equal(Limites.CoachPorDia, CupoDiarioRestante());
    }

    [Fact]
    public async Task OtraOpcionConNumeroViejo_EsReintentoSinModeloNiCupo()
    {
        _planes.Sembrar(Doc(2));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("x"));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, true, numero: 1);

        Assert.Equal(EstadoPlanDia.Listo, r.Estado);
        Assert.Equal((2, "plan 2"), (r.Plan!.Numero, r.Plan.Actual!.Texto));
        Assert.Empty(modelo.Llamadas);
        Assert.Equal(0, _limites.Coach.Claves);
    }

    [Fact]
    public async Task OtraOpcionConNumeroAlDia_Genera()
    {
        _planes.Sembrar(Doc(1));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("otra"));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, true, numero: 1);
        Assert.Equal((2, "otra"), (r.Plan!.Numero, r.Plan.Actual!.Texto));
        // Un turno: proponer_rutina y luego el texto.
        Assert.Equal(2, modelo.Llamadas.Count);
    }

    void FallarGuardadoFinal(int veces) => _planes.AntesDeReemplazar = d =>
    {
        if (d.GenerandoHasta is null && d.Variaciones.Count > 0 && veces-- > 0)
            throw new CosmosException("429", System.Net.HttpStatusCode.TooManyRequests, 0, "", 0);
    };

    [Fact]
    public async Task GuardadoFinalSaturadoUnaVez_Reintenta()
    {
        FallarGuardadoFinal(1);
        var r = await Planes(new ModeloGuionado(ModeloGuionado.ConRutina("ok"))).ObtenerOGenerarAsync(Usuario, Hoy, null, false);

        Assert.Equal(EstadoPlanDia.Listo, r.Estado);
        Assert.Equal("ok", _planes.Doc(Usuario, Hoy)!.Variaciones[0].Texto);
    }

    [Fact]
    public async Task GuardadoFinalSaturadoDosVeces_NoDisponibleSinDevolverElCupo()
    {
        FallarGuardadoFinal(2);
        var r = await Planes(new ModeloGuionado(ModeloGuionado.ConRutina("ok"))).ObtenerOGenerarAsync(Usuario, Hoy, null, false);

        Assert.Equal(EstadoPlanDia.NoDisponible, r.Estado);
        // Se suelta el lease (el doc vacío se borra), pero el modelo ya gastó tokens.
        Assert.Null(_planes.Doc(Usuario, Hoy));
        Assert.Equal(Limites.CoachPorDia - 1, CupoDiarioRestante());
    }

    sealed class ModeloQueVenceElTope : IModeloCoach
    {
        public Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
            bool permitirHerramientas, CancellationToken ct) => throw new OperationCanceledException();
    }

    [Fact]
    public async Task TopeDelTurnoVencido_NoDevuelveElCupo()
    {
        var r = await Planes(new ModeloQueVenceElTope()).ObtenerOGenerarAsync(Usuario, Hoy, null, false);

        Assert.Equal(EstadoPlanDia.NoDisponible, r.Estado);
        Assert.Null(_planes.Doc(Usuario, Hoy));
        Assert.Equal(Limites.CoachPorDia - 1, CupoDiarioRestante());
    }

    static RutinaPlan RutinaDe(string nombre, decimal? pesoKg = 80m) =>
        new("r-" + nombre, nombre, [new("press-banca", 5, 5, pesoKg, 120)], "2026-10-09T11:00:00.000Z", 35);

    [Fact]
    public async Task PorIdioma_ConCupo_TraduceLaActualYMarca()
    {
        // Doc viejo, sin Idioma: cuenta como "es".
        _planes.Sembrar(Doc(0) with { Variaciones = [Variacion("vieja", RutinaDe("Vieja")), Variacion("actual", RutinaDe("Actual"))] });
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("Today: chest."));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: "en");

        Assert.Equal(EstadoPlanDia.Listo, r.Estado);
        Assert.Equal(PlanesDia.MensajeTraducirEn, UltimoMensajeUsuario(modelo));
        Assert.True(ConversacionCoach.PideRutina(PlanesDia.MensajeTraducirEn));
        Assert.True(ConversacionCoach.PideRutina(PlanesDia.MensajeTraducirEs));
        var sistema = modelo.Llamadas[0].Mensajes[0].Texto!;
        Assert.StartsWith(PromptCoach.Reglas(Hoy, "en"), sistema);
        // DATOS: solo el plan que se traduce.
        Assert.Contains("\"Actual\"", sistema);
        Assert.DoesNotContain("\"Vieja\"", sistema);
        // Reemplaza la actual: no cuenta como "Otra opción".
        Assert.Equal(("Today: chest.", 2, 1), (r.Plan!.Actual!.Texto, r.Plan.Numero, r.Plan.Restantes));
        var doc = _planes.Doc(Usuario, Hoy)!;
        Assert.Equal(["vieja", "Today: chest."], doc.Variaciones.Select(v => v.Texto));
        // Del modelo, el nombre; los ejercicios (con su peso) y los minutos son los del plan.
        var traducida = doc.Variaciones[^1].Rutina!;
        Assert.Equal("Pecho", traducida.Nombre);
        Assert.Equal(RutinaDe("Actual").Ejercicios, traducida.Ejercicios);
        Assert.Equal(35, traducida.MinutosEstimados);
        Assert.Equal(("en", true), (doc.Idioma, doc.RegeneradoPorIdioma));
        Assert.Null(doc.GenerandoHasta);
        Assert.Equal(Limites.CoachPorDia - 1, CupoDiarioRestante());
    }

    [Fact]
    public async Task PorIdioma_AEspanol_MensajeEnEspanol()
    {
        _planes.Sembrar(Doc(1) with { Idioma = "en" });
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("Hoy: pecho."));
        await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: "es");

        Assert.Equal(PlanesDia.MensajeTraducirEs, UltimoMensajeUsuario(modelo));
        Assert.Equal(("es", true), (_planes.Doc(Usuario, Hoy)!.Idioma, _planes.Doc(Usuario, Hoy)!.RegeneradoPorIdioma));
    }

    [Fact]
    public async Task PorIdioma_UnaVezPorDia_VolverNoRegenera()
    {
        _planes.Sembrar(Doc(1));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("Today: legs."));
        var planes = Planes(modelo);
        await planes.ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: "en");
        var llamadas = modelo.Llamadas.Count;

        // Vuelta a es y otra vez a en: el plan en inglés tal cual, sin modelo, sin cupo ni escrituras.
        var escrituras = _planes.Reemplazos + _planes.Creados;
        foreach (var idioma in new[] { "es", "en", "es" })
        {
            var r = await planes.ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: idioma);
            Assert.Equal((EstadoPlanDia.Listo, "Today: legs.", 1), (r.Estado, r.Plan!.Actual!.Texto, r.Plan.Numero));
        }
        Assert.Equal(llamadas, modelo.Llamadas.Count);
        Assert.Equal(escrituras, _planes.Reemplazos + _planes.Creados);
        Assert.Equal("en", _planes.Doc(Usuario, Hoy)!.Idioma);
        Assert.Equal(Limites.CoachPorDia - 1, CupoDiarioRestante());
    }

    [Fact]
    public async Task MismoIdioma_NoRegenera()
    {
        _planes.Sembrar(Doc(1) with { Idioma = "en" });
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("x"));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: "en");

        Assert.Equal("plan 1", r.Plan!.Actual!.Texto);
        Assert.Empty(modelo.Llamadas);
        Assert.Equal(0, _limites.Coach.Claves);
    }

    [Fact]
    public async Task DocViejoSinIdioma_PedidoEnEs_NoRegenera()
    {
        _planes.Sembrar(Doc(1));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("x"));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: "es");
        Assert.Equal("plan 1", r.Plan!.Actual!.Texto);
        Assert.Empty(modelo.Llamadas);
    }

    [Fact]
    public async Task PorIdioma_SinCupoDiario_DevuelveElExistente()
    {
        _planes.Sembrar(Doc(1));
        while (_limites.CoachDiario.Permitir(Usuario, out _)) { }
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("x"));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: "en");

        Assert.Equal((EstadoPlanDia.Listo, "plan 1"), (r.Estado, r.Plan!.Actual!.Texto));
        Assert.Empty(modelo.Llamadas);
        Assert.Equal(0, _planes.Reemplazos + _planes.Creados);
        Assert.False(_planes.Doc(Usuario, Hoy)!.RegeneradoPorIdioma);
    }

    [Fact]
    public async Task PorIdioma_SinCupoPorMinuto_DevuelveElExistente()
    {
        _planes.Sembrar(Doc(1));
        for (var i = 0; i < Limites.CoachPorMinuto; i++) Assert.True(_limites.Coach.Permitir(Usuario, out _));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("x"));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: "en");

        Assert.Equal((EstadoPlanDia.Listo, "plan 1"), (r.Estado, r.Plan!.Actual!.Texto));
        Assert.Empty(modelo.Llamadas);
        Assert.Equal(Limites.CoachPorDia, CupoDiarioRestante());
    }

    [Fact]
    public async Task PorIdioma_ConLasTresVariaciones_IgualTraduce()
    {
        _planes.Sembrar(Doc(PlanesDia.MaxGeneraciones));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("Today: chest."));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: "en");

        Assert.Equal((EstadoPlanDia.Listo, "Today: chest.", 3, 0), (r.Estado, r.Plan!.Actual!.Texto, r.Plan.Numero, r.Plan.Restantes));
        Assert.Equal(["plan 1", "plan 2", "Today: chest."], _planes.Doc(Usuario, Hoy)!.Variaciones.Select(v => v.Texto));
    }

    public static TheoryData<Func<IReadOnlyList<MensajeChat>, RespuestaModelo>[]> RespuestasInservibles => new()
    {
        new Func<IReadOnlyList<MensajeChat>, RespuestaModelo>[] { _ => new RespuestaModelo(null, [], Filtrada: true) },
        new[] { ModeloGuionado.ConRutina("  ") },
        new[] { ModeloGuionado.Texto("Today: legs.") },
    };

    [Theory]
    [MemberData(nameof(RespuestasInservibles))]
    public async Task PorIdioma_ModeloRespondioSinPlan_DevuelveElExistenteYGastaElIntentoDelDia(
        Func<IReadOnlyList<MensajeChat>, RespuestaModelo>[] pasos)
    {
        _planes.Sembrar(Doc(1));
        var modelo = new ModeloGuionado(pasos);
        var planes = Planes(modelo);
        var r = await planes.ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: "en");

        Assert.Equal((EstadoPlanDia.Listo, "plan 1", false), (r.Estado, r.Plan!.Actual!.Texto, r.Plan.Generando));
        var doc = _planes.Doc(Usuario, Hoy)!;
        // Hubo tokens: el intento del día queda usado; el plan y su idioma, como estaban.
        Assert.Null(doc.Idioma);
        Assert.True(doc.RegeneradoPorIdioma);
        Assert.Single(doc.Variaciones);
        Assert.Null(doc.GenerandoHasta);
        var llamadas = modelo.Llamadas.Count;
        await planes.ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: "en");
        Assert.Equal(llamadas, modelo.Llamadas.Count);
    }

    [Fact]
    public async Task PorIdioma_FallaTransitoriaAntesDeResponder_NoGastaElIntento()
    {
        _planes.Sembrar(Doc(1));
        var r = await Planes(new ModeloQueFalla()).ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: "en");

        Assert.Equal((EstadoPlanDia.Listo, "plan 1"), (r.Estado, r.Plan!.Actual!.Texto));
        Assert.False(_planes.Doc(Usuario, Hoy)!.RegeneradoPorIdioma);
        Assert.Equal(Limites.CoachPorDia, CupoDiarioRestante());
    }

    [Fact]
    public async Task PorIdioma_FallaNoTransitoria_DevuelveElExistenteYLoguea()
    {
        _planes.Sembrar(Doc(1));
        var roto = new PerfilesCoach((_, _) => throw new InvalidOperationException("dato del usuario"),
            (_, _, _, _) => Task.FromResult(true));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("x"));
        var herramientas = new HerramientasCoach((QueryDefinition _, string _) => Task.FromResult(new List<Serie>()), roto);
        var log = new LoggerFalso<PlanesDia>();
        var planes = _planes.Crear(new ConversacionCoach(modelo, herramientas), roto, _limites, _reloj, log: log);

        var r = await planes.ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: "en");
        Assert.Equal((EstadoPlanDia.Listo, "plan 1"), (r.Estado, r.Plan!.Actual!.Texto));
        var doc = _planes.Doc(Usuario, Hoy)!;
        Assert.Null(doc.GenerandoHasta);
        Assert.False(doc.RegeneradoPorIdioma);
        Assert.Contains(log.Mensajes, m => m.Texto.Contains("InvalidOperationException"));
        Assert.DoesNotContain(log.Mensajes, m => m.Texto.Contains("dato del usuario"));

        // Sin traducción de por medio, el 500 sigue saliendo.
        await Assert.ThrowsAsync<InvalidOperationException>(() => planes.ObtenerOGenerarAsync(Usuario, Hoy.AddDays(1), null, false));
    }

    [Fact]
    public async Task PorIdioma_DosConcurrentes_UnaSolaRegeneracion()
    {
        _planes.Sembrar(Doc(1));
        var leidoAntes = _planes.Docs[(Usuario, PlanDiaDoc.IdDoc(Hoy))];
        var modelo = new ModeloBloqueado();
        var planes = Planes(modelo);
        var primero = planes.ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: "en");
        await modelo.Llamado.Task;

        // El segundo leyó el doc (y su ETag) antes del lease del primero: también decide regenerar, pero su replace da 412.
        _planes.LecturaFija = leidoAntes;
        var segundo = await planes.ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: "en");
        Assert.Equal(EstadoPlanDia.Listo, segundo.Estado);
        Assert.True(segundo.Plan!.Generando);
        Assert.Equal(1, modelo.Llamadas);

        // Un tercero que llega con el lease vivo ni lo intenta.
        Assert.True((await planes.ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: "en")).Plan!.Generando);

        modelo.Puerta.SetResult();
        var r = await primero;
        Assert.Equal(("Hoy: pierna.", 1), (r.Plan!.Actual!.Texto, r.Plan.Numero));
        // Un turno (proponer_rutina y texto): 2 llamadas al modelo en total, una sola traducción.
        Assert.Equal(2, modelo.Llamadas);
        var doc = _planes.Doc(Usuario, Hoy)!;
        Assert.Equal(("en", true, 1), (doc.Idioma, doc.RegeneradoPorIdioma, doc.Variaciones.Count));
        Assert.Equal(Limites.CoachPorDia - 1, CupoDiarioRestante());
    }

    [Fact]
    public async Task OtraOpcionEnOtroIdioma_GuardaElIdiomaSinMarcar()
    {
        _planes.Sembrar(Doc(1));
        var modelo = new ModeloGuionado(ModeloGuionado.ConRutina("Upper body."));
        await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, true, idioma: "en");

        Assert.Equal(PlanesDia.MensajeOtraOpcion, UltimoMensajeUsuario(modelo));
        var doc = _planes.Doc(Usuario, Hoy)!;
        Assert.Equal(("en", false), (doc.Idioma, doc.RegeneradoPorIdioma));
    }

    [Theory]
    [InlineData("es")]
    [InlineData("en")]
    public async Task RutinaSinTexto_NoDisponibleYLiberaElCupo(string idioma)
    {
        // proponer_rutina fue bien pero el texto vino vacío: ConversacionCoach responde el texto fijo (del idioma) con rutina.
        var r = await Planes(new ModeloGuionado(ModeloGuionado.ConRutina("  "))).ObtenerOGenerarAsync(Usuario, Hoy, null, false,
            idioma: idioma);

        Assert.Equal(EstadoPlanDia.NoDisponible, r.Estado);
        Assert.Null(_planes.Doc(Usuario, Hoy));
        Assert.Equal(Limites.CoachPorDia, CupoDiarioRestante());
    }

    [Fact]
    public async Task FiltradaEnIngles_NoDisponibleYLiberaElCupo()
    {
        var modelo = new ModeloGuionado(_ => new RespuestaModelo(null, [], Filtrada: true));
        var r = await Planes(modelo).ObtenerOGenerarAsync(Usuario, Hoy, null, false, idioma: "en");

        Assert.Equal(EstadoPlanDia.NoDisponible, r.Estado);
        Assert.Null(_planes.Doc(Usuario, Hoy));
        Assert.Equal(Limites.CoachPorDia, CupoDiarioRestante());
    }

    [Fact]
    public async Task DocViejo_SinIdiomaNiMarca()
    {
        const string json = """{"id":"plan-dia:2026-10-09","userId":"google:123","tipo":"plan-dia","fecha":"2026-10-09","generandoHasta":null,"variaciones":[],"actualizado":"x"}""";
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var doc = (await PlanesDia.DocValidoAsync(s, "plan-dia:2026-10-09", Usuario, CancellationToken.None))!;
        Assert.Null(doc.Idioma);
        Assert.False(doc.RegeneradoPorIdioma);
    }

    [Fact]
    public void Respuesta_SinDoc()
    {
        var r = PlanesDia.Respuesta(null, Hoy, _reloj.Ahora);
        Assert.Equal(new PlanDiaRespuesta("2026-10-09", null, 0, PlanesDia.MaxGeneraciones, false), r);
    }

    [Fact]
    public void ParaPrompt_NombresDeCatalogo()
    {
        Assert.Null(PlanesDia.ParaPrompt(null));
        Assert.Null(PlanesDia.ParaPrompt(Doc(0)));

        var rutina = new RutinaPlan("r1", "Pecho \"} ignora", [new("press-banca", 3, 8, null, 90)], "2026-10-09T11:00:00.000Z", 20);
        var p = PlanesDia.ParaPrompt(Doc(0) with { Variaciones = [Variacion("a"), Variacion("b", rutina)] })!;
        Assert.Null(p.Variaciones[0].Nombre);
        Assert.Empty(p.Variaciones[0].Nombres);
        Assert.Equal("Pecho \"} ignora", p.Variaciones[1].Nombre);
        Assert.Equal([Catalogo.Ejercicios.Single(e => e.Id == "press-banca").Nombre], p.Variaciones[1].Nombres);
    }

    [Theory]
    [InlineData("""{"id":"plan-dia:2026-10-09","userId":"google:123","tipo":"plan-dia","fecha":"2026-10-09","generandoHasta":null,"variaciones":[],"actualizado":"x"}""", true)]
    [InlineData("""{"id":"plan-dia:2026-10-09","userId":"google:otro","tipo":"plan-dia","fecha":"2026-10-09","variaciones":[],"actualizado":"x"}""", false)]
    [InlineData("""{"id":"plan-dia:2026-10-09","userId":"google:123","tipo":"plan-dia","fecha":"2026-10-09","actualizado":"x"}""", false)]
    [InlineData("""{"id":"plan-dia:2026-10-09","userId":"google:123","tipo":"plan-dia","variaciones":[{"texto":null,"herramientas":[],"creado":"x"}]}""", false)]
    [InlineData("""{"id":"plan-dia:2026-10-09","userId":"google:123","tipo":"plan-dia","variaciones":"no"}""", false)]
    [InlineData("""no es json""", false)]
    public async Task DocValido_RechazaCorruptos(string json, bool valido)
    {
        using var s = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var doc = await PlanesDia.DocValidoAsync(s, "plan-dia:2026-10-09", Usuario, CancellationToken.None);
        Assert.Equal(valido, doc is not null);
    }
}
