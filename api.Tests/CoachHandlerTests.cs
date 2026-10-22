using System.Net;
using System.Text.Json;
using GymCoach.Api.Functions;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker.Http;

namespace GymCoach.Api.Tests;

/// <summary>POST /api/coach con modelo guionado y perfiles en memoria (sin Cosmos ni Azure OpenAI).</summary>
public class CoachHandlerTests
{
    const string BodyValido = """{"hoy":"2026-10-09","mensajes":[{"rol":"usuario","texto":"¿Cómo voy?"}]}""";
    readonly Tokens _tokens = new();
    readonly RelojFalso _reloj = new(new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.Zero));
    readonly PerfilesFalsos _perfiles = new();
    readonly Limites _limites;

    public CoachHandlerTests() => _limites = new Limites(_reloj);

    string Bearer => "Bearer " + _tokens.Crear();

    /// <summary>Lo que lanza la lectura del perfil de entrenamiento o del plan (null: lee bien).</summary>
    Exception? _lanzarEntrenamiento, _lanzarPlan;
    /// <summary>Plan de hoy guardado (null: sin plan).</summary>
    PlanDiaDoc? _plan;

    // Doc vigente (mismo hoy y versión): ObtenerAsync lo devuelve sin recalcular. Historial vacío: sin guardia.
    PerfilesEntrenamiento Entrenamiento() => new(
        (userId, _) => _lanzarEntrenamiento is { } e ? throw e
            : Task.FromResult<(PerfilEntrenamientoDoc?, string?)>((new PerfilEntrenamientoDoc(PerfilEntrenamientoDoc.IdDoc, userId,
                PerfilEntrenamientoDoc.TipoDoc, null, EntrenamientoFalso.Vacio), "e1")),
        (_, _) => Task.FromResult<IReadOnlyList<Serie>>([]),
        (_, _, _) => Task.FromResult(true),
        (_, _) => Task.CompletedTask,
        _ => true, _reloj);

    /// <summary>Docs de conversación del agente en memoria, por userId (ETag nuevo en cada escritura).</summary>
    readonly Dictionary<string, (ConversacionAgenteDoc Doc, string ETag)> _docs = [];
    int _lecturasDoc;
    readonly ConversacionesAgenteFalsas _foundry = new();
    readonly LoggerFalso _logMotor = new();

    ConversacionesCoach Conversaciones() => new(
        (userId, _) =>
        {
            _lecturasDoc++;
            return Task.FromResult<(ConversacionAgenteDoc?, string?)>(_docs.TryGetValue(userId, out var d) ? (d.Doc, d.ETag) : (null, null));
        },
        (doc, etag, _) =>
        {
            var existe = _docs.TryGetValue(doc.UserId, out var d);
            if (etag is null ? existe : !existe || d.ETag != etag) return Task.FromResult(false);
            _docs[doc.UserId] = (doc, Guid.NewGuid().ToString());
            return Task.FromResult(true);
        },
        (userId, etag, _) => Task.FromResult(_docs.TryGetValue(userId, out var d) && d.ETag == etag && _docs.Remove(userId)),
        _foundry, _reloj);

    void Sembrar(string userId, string conversacionId, string dia = "2026-10-09") =>
        _docs[userId] = (new ConversacionAgenteDoc(ConversacionAgenteDoc.IdDoc, userId, ConversacionAgenteDoc.TipoDoc, conversacionId, dia,
            "2026-10-09T07:00:00.000Z"), "e0");

    /// <summary>agente: null = motor bucle. modelo es siempre el del bucle (el de PlanesDia).</summary>
    Coach Handler(IModeloCoach modelo, PerfilesCoach? perfiles = null, IModeloCoach? agente = null, TimeSpan? tiempoAgente = null)
    {
        var p = perfiles ?? _perfiles.Crear();
        var herramientas = new HerramientasCoach((QueryDefinition _, string _) => Task.FromResult(new List<Serie>()), p);
        var conversacion = new ConversacionCoach(modelo, herramientas);
        var motor = new MotorCoach(conversacion, agente is null ? null : new ConversacionCoach(agente, herramientas), Conversaciones(),
            p, _logMotor, tiempoAgente ?? TimeSpan.FromSeconds(20));
        var entrenamiento = Entrenamiento();
        var planes = new PlanesDia(
            (_, _, _) => _lanzarPlan is { } e ? throw e : Task.FromResult<(PlanDiaDoc?, string?)>((_plan, _plan is null ? null : "p1")),
            (_, _) => Task.FromResult<string?>("p1"),
            (_, _, _) => Task.FromResult<string?>("p2"),
            (_, _, _, _) => Task.FromResult(true),
            conversacion, p, entrenamiento, _limites, _reloj);
        return new Coach(_tokens.Autenticador(), _limites, _reloj, modelo, motor, p, entrenamiento, planes);
    }

    static RequestFalso Post(string? authorization, string body = BodyValido, string? custom = null) =>
        new("POST", "/api/coach", authorization, body, customAuthorization: custom);

    static string Esperar(HttpResponseData res, HttpStatusCode codigo)
    {
        var texto = ((ResponseFalsa)res).Texto();
        Assert.True(codigo == res.StatusCode, $"Esperaba {codigo}, llegó {res.StatusCode}: {texto}");
        return texto;
    }

    static int RetryAfter(HttpResponseData res) => int.Parse(res.Headers.GetValues("Retry-After").Single());

    sealed class ModeloQueFalla : IModeloCoach
    {
        public Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
            bool permitirHerramientas, CancellationToken ct) => throw new ModeloNoDisponibleException("429");
    }

    [Fact]
    public async Task IdiomaEn_PromptDelSistemaEnIngles()
    {
        var modelo = new ModeloGuionado(ModeloGuionado.Texto("Going well."));
        var body = """{"hoy":"2026-10-09","idioma":"en","mensajes":[{"rol":"usuario","texto":"How am I doing?"}]}""";
        Esperar(await Handler(modelo).Responder(Post(Bearer, body)), HttpStatusCode.OK);
        Assert.StartsWith(PromptCoach.Reglas(new DateOnly(2026, 10, 9), "en"), modelo.Llamadas[0].Mensajes[0].Texto);
    }

    [Fact]
    public async Task IdiomaInvalido_400SinModelo()
    {
        var modelo = new ModeloGuionado(ModeloGuionado.Texto("x"));
        var body = """{"hoy":"2026-10-09","idioma":"fr","mensajes":[{"rol":"usuario","texto":"a"}]}""";
        Assert.Contains("\"campo\":\"idioma\"", Esperar(await Handler(modelo).Responder(Post(Bearer, body)), HttpStatusCode.BadRequest));
        Assert.Empty(modelo.Llamadas);
    }

    [Fact]
    public async Task SinToken_401() =>
        Assert.Equal("""{"error":"no_autenticado"}""",
            Esperar(await Handler(new ModeloGuionado(ModeloGuionado.Texto("x"))).Responder(Post(null)), HttpStatusCode.Unauthorized));

    [Fact]
    public async Task TokenSoloEnXCustomAuthorization_200()
    {
        var texto = Esperar(await Handler(new ModeloGuionado(ModeloGuionado.Texto("Vas bien.")))
            .Responder(Post(null, custom: Bearer)), HttpStatusCode.OK);
        Assert.Equal("""{"respuesta":"Vas bien.","herramientas":[]}""", texto);
    }

    [Fact]
    public async Task SinConfigurar_503_SinGastarCupo()
    {
        var handler = Handler(new ModeloSinConfigurar());
        var bearer = Bearer;
        for (var i = 0; i <= Limites.CoachPorMinuto; i++)
            Assert.Equal("""{"error":"coach_no_configurado"}""",
                Esperar(await handler.Responder(Post(bearer)), HttpStatusCode.ServiceUnavailable));
        Assert.Equal(0, _limites.Coach.Claves);
    }

    [Fact]
    public async Task BodyInvalido_400_NoGastaCupoDiario()
    {
        var handler = Handler(new ModeloGuionado(ModeloGuionado.Texto("ok")));
        var bearer = Bearer;
        // Más inválidos que el cupo diario, respetando el de por minuto.
        for (var i = 0; i < Limites.CoachPorDia + 5; i++)
        {
            if (i > 0 && i % Limites.CoachPorMinuto == 0) _reloj.Avanzar(TimeSpan.FromSeconds(61));
            Assert.Contains("\"error\":\"validacion\"",
                Esperar(await handler.Responder(Post(bearer, """{"mensajes":[]}""")), HttpStatusCode.BadRequest));
        }
        _reloj.Avanzar(TimeSpan.FromSeconds(61));
        Esperar(await handler.Responder(Post(bearer)), HttpStatusCode.OK);
    }

    [Fact]
    public async Task BodyMayorA64KB_400()
    {
        var grande = "{\"mensajes\":[{\"rol\":\"usuario\",\"texto\":\"" + new string('a', 70_000) + "\"}]}";
        Assert.Contains("\"campo\":\"body\"", Esperar(await Handler(new ModeloGuionado(ModeloGuionado.Texto("x")))
            .Responder(Post(Bearer, grande)), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task SextaDelMinuto_429()
    {
        var handler = Handler(new ModeloGuionado(ModeloGuionado.Texto("ok")));
        var bearer = Bearer;
        for (var i = 0; i < Limites.CoachPorMinuto; i++) Esperar(await handler.Responder(Post(bearer)), HttpStatusCode.OK);

        var res = await handler.Responder(Post(bearer));
        Assert.Equal("""{"error":"demasiadas_peticiones"}""", Esperar(res, HttpStatusCode.TooManyRequests));
        Assert.InRange(RetryAfter(res), 1, 60);
    }

    [Fact]
    public async Task VeintiunoDelDia_429ConEsperaLarga()
    {
        var handler = Handler(new ModeloGuionado(ModeloGuionado.Texto("ok")));
        var bearer = Bearer;
        for (var i = 0; i < Limites.CoachPorDia; i++)
        {
            if (i > 0 && i % Limites.CoachPorMinuto == 0) _reloj.Avanzar(TimeSpan.FromSeconds(61));
            Esperar(await handler.Responder(Post(bearer)), HttpStatusCode.OK);
        }
        _reloj.Avanzar(TimeSpan.FromSeconds(61));

        var res = await handler.Responder(Post(bearer));
        Esperar(res, HttpStatusCode.TooManyRequests);
        Assert.InRange(RetryAfter(res), 61, 86400);
    }

    [Fact]
    public async Task ModeloNoDisponible_503()
    {
        var res = await Handler(new ModeloQueFalla()).Responder(Post(Bearer));
        Assert.Equal("""{"error":"no_disponible"}""", Esperar(res, HttpStatusCode.ServiceUnavailable));
        Assert.Equal(2, RetryAfter(res));
    }

    [Fact]
    public async Task CosmosSaturadoAlLeerPerfil_503()
    {
        var saturado = new PerfilesCoach(
            (_, _) => throw new CosmosException("429", HttpStatusCode.TooManyRequests, 0, "", 0),
            (_, _, _, _) => Task.FromResult(true));
        Assert.Contains("no_disponible", Esperar(await Handler(new ModeloGuionado(ModeloGuionado.Texto("x")), saturado)
            .Responder(Post(Bearer)), HttpStatusCode.ServiceUnavailable));
    }

    [Fact]
    public async Task Respuesta_ConRutinaYPerfil()
    {
        const string rutina = """{"nombre":"Pecho","ejercicios":[{"id":"press-banca","series":3,"reps":8,"pesoKg":null,"descansoS":90}]}""";
        var modelo = new ModeloGuionado(
            ModeloGuionado.Pide(("recordar", """{"accion":"fijar_minutos","minutos":45}"""), ("proponer_rutina", rutina)),
            ModeloGuionado.Texto("Listo."));
        var texto = Esperar(await Handler(modelo).Responder(Post(Bearer)), HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(texto);
        var raiz = doc.RootElement;
        Assert.Equal("Listo.", raiz.GetProperty("respuesta").GetString());
        Assert.Equal(["recordar", "proponer_rutina"], raiz.GetProperty("herramientas").EnumerateArray().Select(e => e.GetString()));

        var r = raiz.GetProperty("rutina");
        Assert.True(Guid.TryParse(r.GetProperty("id").GetString(), out _));
        Assert.Equal("Pecho", r.GetProperty("nombre").GetString());
        Assert.Equal(JsonValueKind.Null, r.GetProperty("ejercicios")[0].GetProperty("pesoKg").ValueKind);
        Assert.Equal(7, r.GetProperty("minutosEstimados").GetInt32());
        Assert.True(r.TryGetProperty("actualizada", out _));

        var p = raiz.GetProperty("perfil");
        Assert.Equal(45, p.GetProperty("minutosDisponibles").GetInt32());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("objetivo").ValueKind);
        Assert.Equal(0, p.GetProperty("notas").GetArrayLength());
        Assert.False(p.TryGetProperty("horaAsesoria", out _));
    }

    [Fact]
    public async Task Respuesta_ConPrograma()
    {
        const string programa = """{"nombre":"Full body","rutinas":[{"nombre":"A","ejercicios":[{"id":"press-banca","series":3,"reps":10,"pesoKg":null,"descansoS":60}]},{"nombre":"B","ejercicios":[{"id":"prensa","series":3,"reps":10,"pesoKg":null,"descansoS":60}]}]}""";
        var modelo = new ModeloGuionado(ModeloGuionado.Pide(("proponer_programa", programa)), ModeloGuionado.Texto("Listo."));
        var texto = Esperar(await Handler(modelo).Responder(Post(Bearer)), HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(texto);
        var raiz = doc.RootElement;
        // Igual que rutina: null no se escribe (Http.Json ignora nulls).
        Assert.False(raiz.TryGetProperty("rutina", out _));
        var g = raiz.GetProperty("programa");
        Assert.Equal("Full body", g.GetProperty("nombre").GetString());
        var rutinas = g.GetProperty("rutinas");
        Assert.Equal(2, rutinas.GetArrayLength());
        foreach (var r in rutinas.EnumerateArray())
        {
            Assert.True(Guid.TryParse(r.GetProperty("id").GetString(), out _));
            Assert.Equal(1, r.GetProperty("ejercicios").GetArrayLength());
            Assert.True(r.GetProperty("minutosEstimados").GetInt32() > 0);
            Assert.True(r.TryGetProperty("actualizada", out _));
        }
        Assert.Equal(["A", "B"], rutinas.EnumerateArray().Select(r => r.GetProperty("nombre").GetString()));
    }

    [Fact]
    public async Task Respuesta_SinPrograma_NoLoTrae()
    {
        var texto = Esperar(await Handler(new ModeloGuionado(ModeloGuionado.Texto("Vas bien."))).Responder(Post(Bearer)), HttpStatusCode.OK);
        Assert.DoesNotContain("\"programa\"", texto);
    }

    [Fact]
    public async Task FallaTransitoria_DevuelveElCupoDiario()
    {
        var bearer = Bearer;
        var falla = Handler(new ModeloQueFalla());
        for (var i = 0; i < Limites.CoachPorDia; i++)
        {
            if (i > 0 && i % Limites.CoachPorMinuto == 0) _reloj.Avanzar(TimeSpan.FromSeconds(61));
            Esperar(await falla.Responder(Post(bearer)), HttpStatusCode.ServiceUnavailable);
        }
        _reloj.Avanzar(TimeSpan.FromSeconds(61));

        // Las 20 fallidas no contaron: el cupo diario sigue entero.
        var ok = Handler(new ModeloGuionado(ModeloGuionado.Texto("ok")));
        for (var i = 0; i < Limites.CoachPorMinuto; i++) Esperar(await ok.Responder(Post(bearer)), HttpStatusCode.OK);
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

    [Fact]
    public async Task ClienteCorta_NoDevuelveElCupoDiario()
    {
        // Abortar la petición a propósito no debe dar mensajes ilimitados.
        var bearer = Bearer;
        var handler = Handler(new ModeloQueEsperaCancelacion());
        using var cortada = new CancellationTokenSource();
        cortada.Cancel();
        for (var i = 0; i < Limites.CoachPorDia; i++)
        {
            if (i > 0 && i % Limites.CoachPorMinuto == 0) _reloj.Avanzar(TimeSpan.FromSeconds(61));
            Esperar(await handler.Responder(Post(bearer), cortada.Token), HttpStatusCode.ServiceUnavailable);
        }
        _reloj.Avanzar(TimeSpan.FromSeconds(61));

        var res = await handler.Responder(Post(bearer));
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
    }

    [Theory]
    [InlineData("-721")]
    [InlineData("841")]
    [InlineData("60.5")]
    [InlineData("\"-360\"")]
    public async Task OffsetMinInvalido_400(string offset)
    {
        var body = $$"""{"hoy":"2026-10-09","offsetMin":{{offset}},"mensajes":[{"rol":"usuario","texto":"hola"}]}""";
        var modelo = new ModeloGuionado(ModeloGuionado.Texto("x"));
        Assert.Contains("\"campo\":\"offsetMin\"", Esperar(await Handler(modelo).Responder(Post(Bearer, body)), HttpStatusCode.BadRequest));
        Assert.Empty(modelo.Llamadas);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.RequestTimeout)]
    public async Task CosmosSaturadoEnEntrenamientoOPlan_ElTurnoSigue(HttpStatusCode codigo)
    {
        _lanzarEntrenamiento = new CosmosException("saturado", codigo, 0, "", 0);
        _lanzarPlan = new CosmosException("saturado", codigo, 0, "", 0);
        const string rutina = """{"nombre":"Pecho","ejercicios":[{"id":"press-banca","series":3,"reps":8,"pesoKg":null,"descansoS":90}]}""";
        var modelo = new ModeloGuionado(ModeloGuionado.Pide(("proponer_rutina", rutina)), ModeloGuionado.Texto("Sin datos por ahora."));

        var texto = Esperar(await Handler(modelo).Responder(Post(Bearer)), HttpStatusCode.OK);

        Assert.Contains("Sin datos por ahora.", texto);
        // Sin perfil de entrenamiento: proponer_rutina falla cerrado y el prompt va sin ese bloque.
        Assert.Contains("no_disponible", modelo.Llamadas[1].Mensajes.Last().Texto);
        Assert.DoesNotContain("\"rutina\"", texto);
        Assert.DoesNotContain("Perfil de entrenamiento del usuario", modelo.Llamadas[0].Mensajes[0].Texto);
        Assert.DoesNotContain("Plan de hoy ya propuesto", modelo.Llamadas[0].Mensajes[0].Texto);
    }

    [Fact]
    public async Task OtraFallaDeCosmosEnEntrenamiento_NoSeTraga()
    {
        _lanzarEntrenamiento = new CosmosException("malo", HttpStatusCode.BadRequest, 0, "", 0);
        await Assert.ThrowsAsync<CosmosException>(() => Handler(new ModeloGuionado(ModeloGuionado.Texto("x"))).Responder(Post(Bearer)));
    }

    [Fact]
    public async Task ConEntrenamientoYPlan_ElPromptLosLleva()
    {
        _plan = new PlanDiaDoc(PlanDiaDoc.IdDoc(new DateOnly(2026, 10, 9)), "google:123", PlanDiaDoc.TipoDoc, "2026-10-09", null,
            [new VariacionPlan("Hoy pierna.", ["proponer_rutina"],
                new RutinaPlan(Guid.NewGuid().ToString(), "Pierna", [new EjercicioRutina("prensa", 3, 10, null, 90)], "2026-10-09T07:30:00.000Z", 10),
                "2026-10-09T07:30:00.000Z")],
            "2026-10-09T07:30:00.000Z");
        var modelo = new ModeloGuionado(ModeloGuionado.Texto("ok"));

        Esperar(await Handler(modelo).Responder(Post(Bearer)), HttpStatusCode.OK);

        var sistema = modelo.Llamadas[0].Mensajes[0].Texto!;
        Assert.Contains("Perfil de entrenamiento del usuario", sistema);
        Assert.Contains("Plan de hoy ya propuesto", sistema);
        Assert.Contains("Prensa de piernas inclinada", sistema);
    }

    // --- Motor agente ---

    const string BodyDosMensajes = """{"hoy":"2026-10-09","mensajes":[{"rol":"usuario","texto":"Hola"},{"rol":"coach","texto":"Hola, ¿qué tal?"},{"rol":"usuario","texto":"¿Cómo voy?"}]}""";

    /// <summary>Agente que siempre lanza e (con memoria, como ModeloAgenteFoundry).</summary>
    sealed class AgenteQueFalla(Exception e) : IModeloCoach
    {
        public bool ConMemoria => true;
        public Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
            bool permitirHerramientas, CancellationToken ct) => throw e;
    }

    /// <summary>Agente que no responde hasta que se cancela su token.</summary>
    sealed class AgenteQueNoResponde : IModeloCoach
    {
        public bool ConMemoria => true;
        public async Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
            bool permitirHerramientas, CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new("nunca", []);
        }
    }

    sealed class AgenteQueEsperaCancelacion : IModeloCoach
    {
        public bool ConMemoria => true;
        public Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
            bool permitirHerramientas, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new RespuestaModelo("ok", []));
        }
    }

    [Fact]
    public async Task MotorBucle_NoTocaElDocNiFoundry()
    {
        Sembrar("google:123", "conv_a");
        var modelo = new ModeloGuionado(ModeloGuionado.Texto("Vas bien."));
        Esperar(await Handler(modelo).Responder(Post(Bearer)), HttpStatusCode.OK);
        Assert.Equal(0, _lecturasDoc);
        Assert.Empty(_foundry.Creadas);
        Assert.Null(modelo.Sesiones[0]);
    }

    [Fact]
    public async Task Agente_UsaLaConversacionDelDocDelUsuario_NuncaLaDelBody()
    {
        Sembrar("google:123", "conv_a");
        Sembrar("google:otro", "conv_b");
        var bucle = new ModeloGuionado(ModeloGuionado.Texto("bucle"));
        var agente = new ModeloGuionado(ModeloGuionado.Texto("Vas bien.")) { ConMemoria = true };
        var body = BodyDosMensajes.Replace("{\"hoy\"", "{\"conversacionId\":\"conv_b\",\"hoy\"");

        var texto = Esperar(await Handler(bucle, agente: agente).Responder(Post(Bearer, body)), HttpStatusCode.OK);

        Assert.Equal("""{"respuesta":"Vas bien.","herramientas":[]}""", texto);
        Assert.Equal("conv_a", Assert.Single(agente.Sesiones)!.ConversacionId);
        // Con memoria al agente solo va el último mensaje del usuario.
        Assert.Equal("¿Cómo voy?", Assert.Single(agente.Llamadas[0].Mensajes).Texto);
        Assert.Empty(bucle.Llamadas);
        Assert.Empty(_foundry.Creadas);
        Assert.DoesNotContain("conv_", texto);
    }

    [Fact]
    public async Task Agente_UnSoloMensajeDeUsuario_RenuevaYBorraLaAnterior()
    {
        Sembrar("google:123", "conv_a");
        var agente = new ModeloGuionado(ModeloGuionado.Texto("Hola.")) { ConMemoria = true };

        var texto = Esperar(await Handler(new ModeloGuionado(ModeloGuionado.Texto("bucle")), agente: agente).Responder(Post(Bearer)),
            HttpStatusCode.OK);

        var nueva = Assert.Single(_foundry.Creadas);
        Assert.Equal(nueva, agente.Sesiones[0]!.ConversacionId);
        Assert.Equal(["conv_a"], _foundry.Borradas);
        Assert.Equal(nueva, _docs["google:123"].Doc.ConversacionId);
        Assert.DoesNotContain(nueva, texto);
    }

    [Fact]
    public async Task Agente_DocDeOtroDia_Renueva()
    {
        Sembrar("google:123", "conv_a", dia: "2026-10-08");
        var agente = new ModeloGuionado(ModeloGuionado.Texto("Hola.")) { ConMemoria = true };
        Esperar(await Handler(new ModeloGuionado(ModeloGuionado.Texto("bucle")), agente: agente).Responder(Post(Bearer, BodyDosMensajes)),
            HttpStatusCode.OK);
        Assert.Equal(_foundry.Creadas.Single(), agente.Sesiones[0]!.ConversacionId);
        Assert.Equal("2026-10-09", _docs["google:123"].Doc.Dia);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(400)]
    public async Task Agente_ConversacionRota_RespondeElBucleEInvalida(int status)
    {
        Sembrar("google:123", "conv_a");
        var bucle = new ModeloGuionado(ModeloGuionado.Texto("Respaldo."));

        var texto = Esperar(await Handler(bucle, agente: new AgenteQueFalla(new ConversacionRotaException(status)))
            .Responder(Post(Bearer, BodyDosMensajes)), HttpStatusCode.OK);

        Assert.Equal("""{"respuesta":"Respaldo.","herramientas":[]}""", texto);
        // El bucle va sin memoria: todo el historial y sin sesión.
        Assert.Null(bucle.Sesiones[0]);
        Assert.Equal(4, bucle.Llamadas[0].Mensajes.Count);
        Assert.False(_docs.ContainsKey("google:123"));
        Assert.Equal(["conv_a"], _foundry.Borradas);
        // Solo el tipo de la excepción: ni el id ni el mensaje.
        var (nivel, log) = Assert.Single(_logMotor.Mensajes);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, nivel);
        Assert.Contains(nameof(ConversacionRotaException), log);
        Assert.DoesNotContain("conv_", log);
    }

    public static TheoryData<Exception> FallasTransitorias() =>
    [
        new ModeloNoDisponibleException("429"),
        new ModeloRechazoException(400),
        new InvalidOperationException("sin sesión"),
        new CosmosException("conv_a", HttpStatusCode.BadRequest, 0, "", 0),
    ];

    [Theory]
    [MemberData(nameof(FallasTransitorias))]
    public async Task Agente_FallaQueNoRompe_RespondeElBucle_YConservaLaMemoria(Exception falla)
    {
        Sembrar("google:123", "conv_a");
        var bucle = new ModeloGuionado(ModeloGuionado.Texto("Respaldo."));

        var texto = Esperar(await Handler(bucle, agente: new AgenteQueFalla(falla)).Responder(Post(Bearer, BodyDosMensajes)),
            HttpStatusCode.OK);

        Assert.Equal("""{"respuesta":"Respaldo.","herramientas":[]}""", texto);
        Assert.Equal("conv_a", _docs["google:123"].Doc.ConversacionId);
        Assert.Empty(_foundry.Borradas);
        var (_, log) = Assert.Single(_logMotor.Mensajes);
        Assert.Contains(falla.GetType().Name, log);
        Assert.DoesNotContain("conv_", log);
    }

    [Fact]
    public async Task Agente_FallaTrasRecordar_ElRespaldoUsaElPerfilFresco()
    {
        Sembrar("google:123", "conv_a");
        var bucle = new ModeloGuionado(ModeloGuionado.Texto("Respaldo."));
        var agente = new ModeloGuionado(
            ModeloGuionado.Pide(("recordar", """{"accion":"fijar_minutos","minutos":45}""")),
            _ => throw new ConversacionRotaException(400)) { ConMemoria = true };

        var texto = Esperar(await Handler(bucle, agente: agente).Responder(Post(Bearer, BodyDosMensajes)), HttpStatusCode.OK);

        Assert.Contains("Respaldo.", texto);
        Assert.Contains("\"minutosDisponibles\":45", bucle.Llamadas[0].Mensajes[0].Texto);
    }

    [Fact]
    public async Task Agente_Falla_YNoSePuedeReleerElPerfil_RespondeElBucleIgual()
    {
        var lecturas = 0;
        var perfiles = new PerfilesCoach(
            (_, _) => lecturas++ == 0 ? Task.FromResult<(PerfilCoach, string?)>((PerfilCoach.PorDefecto, null))
                : throw new CosmosException("429", HttpStatusCode.TooManyRequests, 0, "", 0),
            (_, _, _, _) => Task.FromResult(true));
        var bucle = new ModeloGuionado(ModeloGuionado.Texto("Respaldo."));
        Assert.Contains("Respaldo.", Esperar(await Handler(bucle, perfiles, agente: new AgenteQueFalla(new ModeloNoDisponibleException("503")))
            .Responder(Post(Bearer, BodyDosMensajes)), HttpStatusCode.OK));
        Assert.Equal(2, lecturas);
    }

    /// <summary>Agente cuya primera llamada no responde hasta que la prueba la suelta; las demás responden enseguida.</summary>
    sealed class AgenteRetenido : IModeloCoach
    {
        int _llamadas;
        public readonly TaskCompletionSource Llamado = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Soltar = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ConMemoria => true;
        public async Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
            bool permitirHerramientas, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _llamadas) == 1)
            {
                Llamado.TrySetResult();
                await Soltar.Task.WaitAsync(ct);
            }
            return new("Listo.", []);
        }
    }

    [Fact]
    public async Task Agente_DosTurnosALaVezDelMismoUsuario_ElSegundoEs429()
    {
        Sembrar("google:123", "conv_a");
        var agente = new AgenteRetenido();
        var handler = Handler(new ModeloGuionado(ModeloGuionado.Texto("bucle")), agente: agente);
        var bearer = Bearer;

        var primero = handler.Responder(Post(bearer, BodyDosMensajes));
        await agente.Llamado.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var segundo = await handler.Responder(Post(bearer, BodyDosMensajes));
        Assert.Equal("""{"error":"demasiadas_peticiones"}""", Esperar(segundo, HttpStatusCode.TooManyRequests));
        Assert.Equal(3, RetryAfter(segundo));
        // Otro usuario no espera.
        Esperar(await handler.Responder(Post("Bearer " + _tokens.Crear(sub: "otro"), BodyDosMensajes)), HttpStatusCode.OK);

        agente.Soltar.SetResult();
        Assert.Contains("Listo.", Esperar(await primero, HttpStatusCode.OK));
        // Terminado el primero, el usuario vuelve a poder.
        Esperar(await handler.Responder(Post(bearer, BodyDosMensajes)), HttpStatusCode.OK);
    }

    [Fact]
    public async Task Agente_TurnoEnCurso_NoGastaCupoDiario()
    {
        Sembrar("google:123", "conv_a");
        var agente = new AgenteRetenido();
        var handler = Handler(new ModeloGuionado(ModeloGuionado.Texto("bucle")), agente: agente);
        var bearer = Bearer;
        var primero = handler.Responder(Post(bearer, BodyDosMensajes));
        await agente.Llamado.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        // Más rechazos que el cupo diario, respetando el de por minuto.
        for (var i = 0; i < Limites.CoachPorDia; i++)
        {
            if (i > 0 && i % (Limites.CoachPorMinuto - 1) == 0) _reloj.Avanzar(TimeSpan.FromSeconds(61));
            Esperar(await handler.Responder(Post(bearer, BodyDosMensajes)), HttpStatusCode.TooManyRequests);
        }
        agente.Soltar.SetResult();
        Esperar(await primero, HttpStatusCode.OK);
        _reloj.Avanzar(TimeSpan.FromSeconds(61));
        Esperar(await handler.Responder(Post(bearer, BodyDosMensajes)), HttpStatusCode.OK);
    }

    [Fact]
    public void Reservar_ConBucleSiempre_ConAgenteUnoPorUsuario()
    {
        var p = _perfiles.Crear();
        var herramientas = new HerramientasCoach((QueryDefinition _, string _) => Task.FromResult(new List<Serie>()), p);
        var bucle = new ConversacionCoach(new ModeloGuionado(ModeloGuionado.Texto("x")), herramientas);
        var soloBucle = new MotorCoach(bucle);
        using (soloBucle.Reservar("u")) Assert.NotNull(soloBucle.Reservar("u"));

        var conAgente = new MotorCoach(bucle, new ConversacionCoach(new ModeloGuionado(ModeloGuionado.Texto("x")) { ConMemoria = true }, herramientas),
            Conversaciones());
        var r = conAgente.Reservar("u");
        Assert.NotNull(r);
        Assert.Null(conAgente.Reservar("u"));
        using (conAgente.Reservar("otro")) { }
        r!.Dispose();
        r.Dispose();
        using var otra = conAgente.Reservar("u");
        Assert.NotNull(otra);
    }

    [Fact]
    public async Task Agente_NoCreaLaConversacion_RespondeElBucle()
    {
        _foundry.FallarCrear = new ModeloNoDisponibleException("503");
        var bucle = new ModeloGuionado(ModeloGuionado.Texto("Respaldo."));
        Assert.Contains("Respaldo.", Esperar(await Handler(bucle, agente: new ModeloGuionado(ModeloGuionado.Texto("x")) { ConMemoria = true })
            .Responder(Post(Bearer)), HttpStatusCode.OK));
        Assert.Empty(_docs);
    }

    [Fact]
    public async Task Agente_VenceSuSubTope_RespondeElBucle()
    {
        Sembrar("google:123", "conv_a");
        var bucle = new ModeloGuionado(ModeloGuionado.Texto("Respaldo."));
        var res = await Handler(bucle, agente: new AgenteQueNoResponde(), tiempoAgente: TimeSpan.FromMilliseconds(50))
            .Responder(Post(Bearer, BodyDosMensajes));
        Assert.Contains("Respaldo.", Esperar(res, HttpStatusCode.OK));
        Assert.Equal(["conv_a"], _foundry.Borradas);
    }

    [Fact]
    public async Task Agente_Filtrada_NoEsFalla()
    {
        Sembrar("google:123", "conv_a");
        var bucle = new ModeloGuionado(ModeloGuionado.Texto("bucle"));
        var agente = new ModeloGuionado(_ => new RespuestaModelo(null, [], Filtrada: true)) { ConMemoria = true };
        var texto = Esperar(await Handler(bucle, agente: agente).Responder(Post(Bearer, BodyDosMensajes)), HttpStatusCode.OK);
        Assert.Contains(ConversacionCoach.RespuestaFiltrada, texto);
        Assert.Empty(bucle.Llamadas);
        Assert.Equal("conv_a", _docs["google:123"].Doc.ConversacionId);
        Assert.Empty(_foundry.Borradas);
    }

    [Fact]
    public async Task Agente_ClienteCorta_SinRespaldo_503()
    {
        Sembrar("google:123", "conv_a");
        var bucle = new ModeloGuionado(ModeloGuionado.Texto("bucle"));
        using var cortada = new CancellationTokenSource();
        cortada.Cancel();
        Esperar(await Handler(bucle, agente: new AgenteQueEsperaCancelacion()).Responder(Post(Bearer, BodyDosMensajes), cortada.Token),
            HttpStatusCode.ServiceUnavailable);
        Assert.Empty(bucle.Llamadas);
    }

    [Fact]
    public async Task Agente_YBucleFallan_503_YDevuelveElCupoDiario()
    {
        var bearer = Bearer;
        var falla = Handler(new ModeloQueFalla(), agente: new AgenteQueFalla(new ModeloNoDisponibleException("429")));
        for (var i = 0; i < Limites.CoachPorDia; i++)
        {
            if (i > 0 && i % Limites.CoachPorMinuto == 0) _reloj.Avanzar(TimeSpan.FromSeconds(61));
            Assert.Equal("""{"error":"no_disponible"}""", Esperar(await falla.Responder(Post(bearer, BodyDosMensajes)),
                HttpStatusCode.ServiceUnavailable));
        }
        _reloj.Avanzar(TimeSpan.FromSeconds(61));

        // Las fallidas no contaron: el cupo diario sigue entero.
        var ok = Handler(new ModeloGuionado(ModeloGuionado.Texto("ok")));
        for (var i = 0; i < Limites.CoachPorMinuto; i++) Esperar(await ok.Responder(Post(bearer)), HttpStatusCode.OK);
    }

    [Fact]
    public void Validacion_HistorialSinMensajeDeUsuario_SeRechaza()
    {
        var (peticion, errores) = ValidacionCoach.Peticion("""{"mensajes":[{"rol":"coach","texto":"Hola"}]}""", _reloj.GetUtcNow());
        Assert.Null(peticion);
        Assert.Contains(errores, e => e.Campo == "mensajes");
    }

    [Fact]
    public void Liberar_QuitaSoloLaUltimaMarca()
    {
        var l = new Limitador(2, TimeSpan.FromMinutes(1), _reloj);
        Assert.True(l.Permitir("a", out _));
        Assert.True(l.Permitir("a", out _));
        Assert.False(l.Permitir("a", out _));
        l.Liberar("a");
        Assert.True(l.Permitir("a", out _));
        Assert.False(l.Permitir("a", out _));
        l.Liberar("sin-marcas");
    }
}
