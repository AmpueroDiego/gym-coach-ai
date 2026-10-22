using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using System.Text.Json;

namespace GymCoach.Api.Tests;

/// <summary>El adaptador del agente contra respuestas HTTP falsas: qué manda, cómo traduce errores y qué borra.</summary>
public class ModeloAgenteFoundryTests
{
    const string Secreto = "me duele la rodilla izquierda";
    const string Proyecto = "https://recurso.services.ai.azure.com/api/projects/gym-coach";
    static readonly SesionAgente Sesion = new("conv_1", new Dictionary<string, string> { ["datos"] = "PR banca 87.5 kg", ["hoy"] = "2026-10-20", ["idioma"] = "español" });

    sealed class HttpFalso(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Req, string Cuerpo)> Pedidos = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var cuerpoPedido = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            // Los DELETE de responses van en paralelo.
            lock (Pedidos) Pedidos.Add((request, cuerpoPedido));
            var (codigo, cuerpo) = responder(request);
            return new HttpResponseMessage(codigo) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") };
        }
    }

    static (ModeloAgenteFoundry Modelo, HttpFalso Http) Crear(HttpStatusCode codigo, string cuerpo) => Crear(_ => (codigo, cuerpo));

    static (ModeloAgenteFoundry Modelo, HttpFalso Http) Crear(Func<HttpRequestMessage, (HttpStatusCode, string)> responder)
    {
        var http = new HttpFalso(responder);
        var modelo = new ModeloAgenteFoundry(Proyecto + "/", "clave", "gym-coach", new HttpClientPipelineTransport(new HttpClient(http)));
        return (modelo, http);
    }

    // Lo que el bucle manda en una ronda con memoria: el assistant ya está en el servidor y no debe salir.
    static readonly MensajeChat[] Ronda =
    [
        new("assistant", null, [new LlamadaHerramienta("call_0", "progreso", "{}")]),
        new("tool", """{"kg":70}""", LlamadaId: "call_0"),
        new("user", Secreto),
    ];

    static Task<RespuestaModelo> Completar(ModeloAgenteFoundry m, bool permitir = true, string? obligada = null) =>
        m.CompletarAsync(Ronda, HerramientasCoach.Definiciones, permitir, obligada, Sesion, CancellationToken.None);

    static string Respuesta(string output, string status = "completed", string incompleto = "null", string error = "null") =>
        $$$"""{"id":"resp_1","object":"response","created_at":1,"status":"{{{status}}}","model":"m","output":[{{{output}}}],"incomplete_details":{{{incompleto}}},"error":{{{error}}},"usage":{"input_tokens":10,"output_tokens":5,"total_tokens":15,"input_tokens_details":{"cached_tokens":0},"output_tokens_details":{"reasoning_tokens":0}},"tool_choice":"auto","tools":[],"parallel_tool_calls":true,"conversation":{"id":"conv_1"}}""";

    const string Mensaje = """{"type":"message","id":"msg_1","status":"completed","role":"assistant","content":[{"type":"output_text","text":"Vas bien.","annotations":[]}]}""";
    const string Llamada = """{"type":"function_call","id":"fc_1","status":"completed","call_id":"call_1","name":"progreso","arguments":"{\"ejercicioId\":\"press-banca\"}"}""";

    [Fact]
    public async Task Peticion_AgenteConversacionYEntradas_SinInstruccionesNiTools()
    {
        var (m, http) = Crear(HttpStatusCode.OK, Respuesta(Mensaje));
        var r = await Completar(m, permitir: false);

        Assert.Equal("Vas bien.", r.Texto);
        Assert.Equal((10, 5), (r.TokensEntrada, r.TokensSalida));
        Assert.False(r.Filtrada);
        var (req, cuerpo) = Assert.Single(http.Pedidos);
        Assert.Equal(Proyecto + "/openai/v1/responses", req.RequestUri!.ToString());
        Assert.Equal("clave", Assert.Single(req.Headers.GetValues("api-key")));
        Assert.Contains("\"agent_reference\":{\"type\":\"agent_reference\",\"name\":\"gym-coach\"}", cuerpo);
        Assert.Contains("\"structured_inputs\":{\"datos\":\"PR banca 87.5 kg\",\"hoy\":\"2026-10-20\",\"idioma\":", cuerpo);
        // El SDK la manda como objeto (forma válida de la API; probada en vivo contra Foundry).
        Assert.Contains("\"conversation\":{\"id\":\"conv_1\"}", cuerpo);
        Assert.Contains("\"max_output_tokens\":1200", cuerpo);
        Assert.Contains("\"truncation\":\"auto\"", cuerpo);
        Assert.Contains("\"tool_choice\":\"none\"", cuerpo);
        Assert.DoesNotContain("\"instructions\"", cuerpo);
        Assert.DoesNotContain("\"tools\"", cuerpo);
        Assert.DoesNotContain("\"model\"", cuerpo);
    }

    [Fact]
    public async Task Peticion_SoloItemsUserYFunctionCallOutput()
    {
        var (m, http) = Crear(HttpStatusCode.OK, Respuesta(Mensaje));
        await Completar(m);

        var cuerpo = Assert.Single(http.Pedidos).Cuerpo;
        Assert.Contains("\"type\":\"function_call_output\"", cuerpo);
        Assert.Contains("\"call_id\":\"call_0\"", cuerpo);
        Assert.Contains(Secreto, cuerpo);
        Assert.Contains("\"role\":\"user\"", cuerpo);
        Assert.DoesNotContain("\"role\":\"developer\"", cuerpo);
        Assert.DoesNotContain("\"role\":\"assistant\"", cuerpo);
        Assert.DoesNotContain("\"type\":\"function_call\"", cuerpo);
        Assert.Contains("\"tool_choice\":\"auto\"", cuerpo);
    }

    [Fact]
    public async Task Peticion_InputEnOrdenYSinAssistant()
    {
        var (m, http) = Crear(HttpStatusCode.OK, Respuesta(Mensaje));
        MensajeChat[] ronda =
        [
            new("assistant", null, [new LlamadaHerramienta("call_0", "progreso", "{}")]),
            new("tool", "{}", LlamadaId: "call_0"),
            new("system", "corrige"),
            new("assistant", "texto viejo"),
            new("user", "sigue"),
        ];
        await m.CompletarAsync(ronda, HerramientasCoach.Definiciones, true, null, Sesion, CancellationToken.None);

        using var doc = JsonDocument.Parse(Assert.Single(http.Pedidos).Cuerpo);
        var items = doc.RootElement.GetProperty("input").EnumerateArray()
            .Select(i => i.GetProperty("type").GetString() + (i.TryGetProperty("role", out var rol) ? ":" + rol.GetString() : ""))
            .ToList();
        Assert.Equal(["function_call_output", "message:developer", "message:user"], items);
    }

    [Fact]
    public async Task CorreccionSystem_VaComoDeveloper()
    {
        var (m, http) = Crear(HttpStatusCode.OK, Respuesta(Mensaje));
        await m.CompletarAsync([new("system", "Corrige: no inventes números.")], HerramientasCoach.Definiciones, true, null, Sesion,
            CancellationToken.None);

        var cuerpo = Assert.Single(http.Pedidos).Cuerpo;
        Assert.Contains("\"role\":\"developer\"", cuerpo);
        Assert.Contains("Corrige: no inventes números.", cuerpo);
        Assert.DoesNotContain("\"role\":\"system\"", cuerpo);
        Assert.DoesNotContain("\"instructions\"", cuerpo);
    }

    [Fact]
    public async Task Obligada_ToolChoiceDeFuncion()
    {
        var (m, http) = Crear(HttpStatusCode.OK, Respuesta(Llamada));
        await Completar(m, obligada: "proponer_rutina");
        Assert.Contains("\"tool_choice\":{\"type\":\"function\",\"name\":\"proponer_rutina\"}", Assert.Single(http.Pedidos).Cuerpo);
    }

    [Fact]
    public async Task FunctionCalls_SeTraducen()
    {
        var (m, _) = Crear(HttpStatusCode.OK, Respuesta(Llamada));
        var r = await Completar(m);
        Assert.Null(r.Texto);
        Assert.Equal(new LlamadaHerramienta("call_1", "progreso", """{"ejercicioId":"press-banca"}"""), Assert.Single(r.Llamadas));
    }

    [Fact]
    public async Task TextoYFunctionCalls_LlegaAmbos()
    {
        var (m, _) = Crear(HttpStatusCode.OK, Respuesta(Mensaje + "," + Llamada));
        var r = await Completar(m);
        Assert.Equal("Vas bien.", r.Texto);
        Assert.Equal("progreso", Assert.Single(r.Llamadas).Nombre);
    }

    [Fact]
    public async Task IncompletoPorContentFilter_Filtrada()
    {
        var (m, _) = Crear(HttpStatusCode.OK, Respuesta(Mensaje, "incomplete", """{"reason":"content_filter"}"""));
        var r = await Completar(m);
        Assert.True(r.Filtrada);
        Assert.Null(r.Texto);
    }

    [Fact]
    public async Task IncompletoPorLongitud_DescartaFunctionCalls()
    {
        var (m, _) = Crear(HttpStatusCode.OK, Respuesta(Llamada, "incomplete", """{"reason":"max_output_tokens"}"""));
        var r = await Completar(m);
        Assert.False(r.Filtrada);
        Assert.Empty(r.Llamadas);
    }

    [Fact]
    public async Task Error400ContentFilter_Filtrada()
    {
        var (m, _) = Crear(HttpStatusCode.BadRequest, """{"error":{"code":"content_filter","message":"bloqueado"}}""");
        Assert.True((await Completar(m)).Filtrada);
    }

    [Fact]
    public async Task Error400Generico_ConversacionRotaSoloElStatus()
    {
        // Un function_call sin su output o una conversación inválida da 400: hay que renovarla.
        var (m, _) = Crear(HttpStatusCode.BadRequest, $$$"""{"error":{"code":"invalid_request","message":"{{{Secreto}}}"}}""");
        var e = await Assert.ThrowsAsync<ConversacionRotaException>(() => Completar(m));
        Assert.Equal(400, e.Status);
        Assert.Null(e.InnerException);
        Assert.DoesNotContain(Secreto, e.ToString());
    }

    [Fact]
    public async Task Error404_ConversacionRota()
    {
        var (m, _) = Crear(HttpStatusCode.NotFound, $$$"""{"error":{"code":"not_found","message":"{{{Secreto}}}"}}""");
        var e = await Assert.ThrowsAsync<ConversacionRotaException>(() => Completar(m));
        Assert.Equal(404, e.Status);
        Assert.Null(e.InnerException);
        Assert.DoesNotContain(Secreto, e.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task SaturadoOConfig_NoDisponibleSinCuerpo(HttpStatusCode codigo)
    {
        var (m, _) = Crear(codigo, $$$"""{"error":{"code":"x","message":"{{{Secreto}}}"}}""");
        var e = await Assert.ThrowsAsync<ModeloNoDisponibleException>(() => Completar(m));
        Assert.Null(e.InnerException);
        Assert.DoesNotContain(Secreto, e.ToString());
    }

    [Fact]
    public async Task FallidaPorContentFilter_FiltradaConTokens()
    {
        var (m, _) = Crear(HttpStatusCode.OK, Respuesta(Mensaje, "failed", error: """{"code":"content_filter","message":"bloqueado"}"""));
        var r = await Completar(m);
        Assert.True(r.Filtrada);
        Assert.Null(r.Texto);
        Assert.Equal((10, 5), (r.TokensEntrada, r.TokensSalida));
    }

    [Theory]
    [InlineData("failed", """{"code":"server_error","message":"x"}""")]
    [InlineData("cancelled", "null")]
    [InlineData("queued", "null")]
    [InlineData("in_progress", "null")]
    public async Task EstadoNoTerminado_NoDisponible(string status, string error)
    {
        var (m, _) = Crear(HttpStatusCode.OK, Respuesta(Mensaje, status, error: error));
        await Assert.ThrowsAsync<ModeloNoDisponibleException>(() => Completar(m));
    }

    [Fact]
    public async Task Timeout_NoDisponible()
    {
        var (m, _) = Crear(_ => throw new TaskCanceledException("timeout"));
        await Assert.ThrowsAsync<ModeloNoDisponibleException>(() => Completar(m));
    }

    [Fact]
    public async Task SinSesion_InvalidOperation()
    {
        var (m, http) = Crear(HttpStatusCode.OK, Respuesta(Mensaje));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            m.CompletarAsync(Ronda, HerramientasCoach.Definiciones, true, null, null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            m.CompletarAsync(Ronda, HerramientasCoach.Definiciones, true, "progreso", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            m.CompletarAsync(Ronda, HerramientasCoach.Definiciones, true, CancellationToken.None));
        Assert.Empty(http.Pedidos);
        Assert.True(m.ConMemoria);
    }

    [Fact]
    public async Task Crear_ConversacionVaciaSinMetadata()
    {
        var (m, http) = Crear(HttpStatusCode.OK, """{"id":"conv_nueva","object":"conversation","created_at":1,"metadata":{}}""");
        Assert.Equal("conv_nueva", await m.CrearAsync(CancellationToken.None));
        var (req, cuerpo) = Assert.Single(http.Pedidos);
        Assert.Equal(Proyecto + "/openai/v1/conversations", req.RequestUri!.ToString());
        Assert.Equal("{}", cuerpo);
    }

    [Theory]
    [InlineData("""{"object":"conversation"}""")]
    [InlineData("""{"id":5}""")]
    [InlineData("no es json")]
    public async Task Crear_CuerpoInesperado_NoDisponible(string cuerpo)
    {
        var (m, _) = Crear(HttpStatusCode.OK, cuerpo);
        await Assert.ThrowsAsync<ModeloNoDisponibleException>(() => m.CrearAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Crear_429NoDisponible()
    {
        var (m, _) = Crear(HttpStatusCode.TooManyRequests, "{}");
        await Assert.ThrowsAsync<ModeloNoDisponibleException>(() => m.CrearAsync(CancellationToken.None));
    }

    const string Borrada = """{"id":"x","deleted":true}""";

    static string Listado(bool hayMas, params (string Id, string Conv)[] responses)
    {
        var data = string.Join(",", responses.Select(r => "{\"id\":\"" + r.Id + "\",\"conversation\":{\"id\":\"" + r.Conv + "\"}}"));
        return "{\"object\":\"list\",\"data\":[" + data + "],\"has_more\":" + (hayMas ? "true" : "false") + "}";
    }

    static List<string> Borrados(HttpFalso http) =>
        [.. http.Pedidos.Where(p => p.Req.Method == HttpMethod.Delete).Select(p => p.Req.RequestUri!.AbsolutePath.Split('/')[^1])];

    [Fact]
    public async Task Borrar_PrimeroLaConversacion_LuegoSoloSusResponses()
    {
        // El listado trae una de otra conversación: si el filtro del servidor fallara, no se borra.
        var listado = Listado(false, ("resp_a", "conv_1"), ("resp_otra", "conv_2"), ("resp_b", "conv_1"));
        var (m, http) = Crear(req => req.Method == HttpMethod.Get ? (HttpStatusCode.OK, listado) : (HttpStatusCode.OK, Borrada));

        Assert.True(await m.BorrarAsync("conv_1", CancellationToken.None));

        var pedidos = http.Pedidos.Select(p => $"{p.Req.Method} {p.Req.RequestUri!.PathAndQuery}").ToList();
        Assert.Equal(4, pedidos.Count);
        Assert.Equal("DELETE /api/projects/gym-coach/openai/v1/conversations/conv_1", pedidos[0]);
        Assert.Equal("GET /api/projects/gym-coach/openai/v1/responses?conversation_id=conv_1&limit=100", pedidos[1]);
        // En paralelo: el orden entre responses no importa, la de otra conversación nunca se toca.
        Assert.Equal(["DELETE /api/projects/gym-coach/openai/v1/responses/resp_a", "DELETE /api/projects/gym-coach/openai/v1/responses/resp_b"],
            pedidos.Skip(2).Order());
        Assert.All(http.Pedidos, p => Assert.Equal("clave", Assert.Single(p.Req.Headers.GetValues("api-key"))));
    }

    [Fact]
    public async Task Borrar_ParaleloConMuchas_SoloLasPropias()
    {
        var propias = Enumerable.Range(0, 40).Select(i => ($"resp_{i}", "conv_1"));
        var ajenas = Enumerable.Range(0, 10).Select(i => ($"ajena_{i}", "conv_2"));
        var listado = Listado(false, [.. propias.Concat(ajenas)]);
        var (m, http) = Crear(req => req.Method == HttpMethod.Get ? (HttpStatusCode.OK, listado) : (HttpStatusCode.OK, Borrada));

        Assert.True(await m.BorrarAsync("conv_1", CancellationToken.None));

        var borrados = Borrados(http);
        Assert.Equal(41, borrados.Count);
        Assert.DoesNotContain(borrados, b => b.StartsWith("ajena_"));
        Assert.Equal(40, borrados.Where(b => b.StartsWith("resp_")).Distinct().Count());
    }

    [Fact]
    public async Task Borrar_ConversacionYa404_True()
    {
        var (m, _) = Crear(req => req.Method == HttpMethod.Get ? (HttpStatusCode.OK, Listado(false)) : (HttpStatusCode.NotFound, "{}"));
        Assert.True(await m.BorrarAsync("conv_1", CancellationToken.None));
    }

    [Fact]
    public async Task Borrar_ResponseYa404_CuentaComoBorrada()
    {
        var (m, _) = Crear(req =>
            req.Method == HttpMethod.Get ? (HttpStatusCode.OK, Listado(false, ("resp_a", "conv_1")))
            : req.RequestUri!.AbsolutePath.EndsWith("/resp_a") ? (HttpStatusCode.NotFound, "{}")
            : (HttpStatusCode.OK, Borrada));
        Assert.True(await m.BorrarAsync("conv_1", CancellationToken.None));
    }

    [Fact]
    public async Task Borrar_ConversacionFalla_SigueConResponsesYFalse()
    {
        var (m, http) = Crear(req =>
            req.Method == HttpMethod.Get ? (HttpStatusCode.OK, Listado(false, ("resp_a", "conv_1")))
            : req.RequestUri!.AbsolutePath.EndsWith("/conv_1") ? (HttpStatusCode.InternalServerError, "{}")
            : (HttpStatusCode.OK, Borrada));
        Assert.False(await m.BorrarAsync("conv_1", CancellationToken.None));
        Assert.Contains("resp_a", Borrados(http));
    }

    [Fact]
    public async Task Borrar_ConMasPaginas_VuelveAListarConTopeYFalse()
    {
        // has_more siempre true: el tope de páginas corta el bucle y no se puede asegurar que no quede nada.
        var listado = Listado(true, ("resp_a", "conv_1"));
        var (m, http) = Crear(req => req.Method == HttpMethod.Get ? (HttpStatusCode.OK, listado) : (HttpStatusCode.OK, Borrada));

        Assert.False(await m.BorrarAsync("conv_1", CancellationToken.None));
        Assert.Equal(5, http.Pedidos.Count(p => p.Req.Method == HttpMethod.Get));
    }

    [Fact]
    public async Task Borrar_DeleteQueFallaEnMedio_SigueConElRestoYFalse()
    {
        var listado = Listado(false, ("resp_a", "conv_1"), ("resp_b", "conv_1"), ("resp_c", "conv_1"));
        var (m, http) = Crear(req =>
            req.Method == HttpMethod.Get ? (HttpStatusCode.OK, listado)
            : req.RequestUri!.AbsolutePath.EndsWith("/resp_a") ? (HttpStatusCode.InternalServerError, "{}")
            : req.RequestUri!.AbsolutePath.EndsWith("/resp_b") ? throw new TaskCanceledException("timeout")
            : (HttpStatusCode.OK, Borrada));

        Assert.False(await m.BorrarAsync("conv_1", CancellationToken.None));
        Assert.Equal(["conv_1", "resp_a", "resp_b", "resp_c"], Borrados(http).Order());
    }

    [Fact]
    public async Task Borrar_HasMoreConIdsAjenos_CortaYFalse()
    {
        // Si el servidor dejara de filtrar: nada nuestro en la página, no se puede saber si quedan.
        var (m, http) = Crear(req => req.Method == HttpMethod.Get
            ? (HttpStatusCode.OK, Listado(true, ("resp_otra", "conv_2")))
            : (HttpStatusCode.OK, Borrada));

        Assert.False(await m.BorrarAsync("conv_1", CancellationToken.None));
        Assert.Equal(1, http.Pedidos.Count(p => p.Req.Method == HttpMethod.Get));
        Assert.Equal(["conv_1"], Borrados(http));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Borrar_ListadoFalla_FalseSinLanzar(HttpStatusCode codigo)
    {
        var (m, _) = Crear(req => req.Method == HttpMethod.Get ? (codigo, "{}") : (HttpStatusCode.OK, Borrada));
        Assert.False(await m.BorrarAsync("conv_1", CancellationToken.None));
    }

    [Fact]
    public async Task Borrar_Timeout_FalseSinLanzar()
    {
        var (m, _) = Crear(_ => throw new TaskCanceledException("timeout"));
        Assert.False(await m.BorrarAsync("conv_1", CancellationToken.None));
    }

    [Fact]
    public async Task Borrar_CtCancelado_FalseSinLanzar()
    {
        var (m, http) = Crear(HttpStatusCode.OK, Borrada);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.False(await m.BorrarAsync("conv_1", cts.Token));
        Assert.Empty(http.Pedidos);
    }

    [Fact]
    public async Task Borrar_CtCanceladoEnMedio_FalseSinLanzar()
    {
        using var cts = new CancellationTokenSource();
        var (m, _) = Crear(req =>
        {
            // Se cancela al borrar la conversación: lo que sigue ya no puede contar como limpio.
            if (req.Method == HttpMethod.Delete) cts.Cancel();
            return req.Method == HttpMethod.Get ? (HttpStatusCode.OK, Listado(false, ("resp_a", "conv_1"))) : (HttpStatusCode.OK, Borrada);
        });
        Assert.False(await m.BorrarAsync("conv_1", cts.Token));
    }
}
