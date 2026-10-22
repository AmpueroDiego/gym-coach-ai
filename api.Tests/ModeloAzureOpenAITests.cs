using System.ClientModel.Primitives;
using System.Net;
using System.Text;

namespace GymCoach.Api.Tests;

/// <summary>El adaptador contra respuestas HTTP falsas: qué manda y cómo traduce cada error de Azure.</summary>
public class ModeloAzureOpenAITests
{
    const string Secreto = "me duele la rodilla izquierda";

    sealed class HttpFalso(HttpStatusCode codigo, string cuerpo) : HttpMessageHandler
    {
        public readonly List<(HttpRequestMessage Req, string Cuerpo)> Pedidos = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Pedidos.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(codigo) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") };
        }
    }

    static (ModeloAzureOpenAI Modelo, HttpFalso Http) Crear(HttpStatusCode codigo, string cuerpo)
    {
        var http = new HttpFalso(codigo, cuerpo);
        var modelo = new ModeloAzureOpenAI("https://recurso.openai.azure.com/", "clave", "coach-mini", "minimal",
            new HttpClientPipelineTransport(new HttpClient(http)));
        return (modelo, http);
    }

    static Task<RespuestaModelo> Completar(ModeloAzureOpenAI m, bool permitir = true) =>
        m.CompletarAsync([new("system", "reglas"), new("user", Secreto)], HerramientasCoach.Definiciones, permitir, CancellationToken.None);

    static string Completado(string finish, string mensaje) =>
        $$$"""{"id":"x","object":"chat.completion","created":1,"model":"m","choices":[{"index":0,"finish_reason":"{{{finish}}}","message":{{{mensaje}}}}],"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}""";

    const string ConToolCall = """{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"progreso","arguments":"{\"ejercicioId\":\"press-banca\"}"}}]}""";

    [Fact]
    public async Task Peticion_EndpointV1YOpciones()
    {
        var (m, http) = Crear(HttpStatusCode.OK, Completado("stop", """{"role":"assistant","content":"Vas bien."}"""));
        var r = await Completar(m, permitir: false);

        Assert.Equal("Vas bien.", r.Texto);
        Assert.Equal((10, 5), (r.TokensEntrada, r.TokensSalida));
        var (req, cuerpo) = Assert.Single(http.Pedidos);
        Assert.Equal("https://recurso.openai.azure.com/openai/v1/chat/completions", req.RequestUri!.ToString());
        Assert.Contains("\"max_completion_tokens\":1200", cuerpo);
        Assert.DoesNotContain("\"max_tokens\"", cuerpo);
        Assert.Contains("\"reasoning_effort\":\"minimal\"", cuerpo);
        Assert.Contains("\"tool_choice\":\"none\"", cuerpo);
        Assert.Contains("\"name\":\"volumen_por_grupo\"", cuerpo);
        Assert.DoesNotContain("temperature", cuerpo);
    }

    [Fact]
    public async Task ToolCalls_SeTraducen()
    {
        var (m, _) = Crear(HttpStatusCode.OK, Completado("tool_calls", ConToolCall));
        var llamada = Assert.Single((await Completar(m)).Llamadas);
        Assert.Equal(new LlamadaHerramienta("call_1", "progreso", """{"ejercicioId":"press-banca"}"""), llamada);
    }

    [Fact]
    public async Task TextoYToolCalls_LlegaAmbos()
    {
        const string conTexto = """{"role":"assistant","content":"Reviso tu banca.","tool_calls":[{"id":"call_1","type":"function","function":{"name":"progreso","arguments":"{}"}}]}""";
        var (m, _) = Crear(HttpStatusCode.OK, Completado("tool_calls", conTexto));
        var r = await Completar(m);
        Assert.Equal("Reviso tu banca.", r.Texto);
        Assert.Equal("progreso", Assert.Single(r.Llamadas).Nombre);
    }

    [Fact]
    public async Task Peticion_LasSieteHerramientas_MisEjerciciosSinParametros()
    {
        var (m, http) = Crear(HttpStatusCode.OK, Completado("stop", """{"role":"assistant","content":"ok"}"""));
        await Completar(m);

        var cuerpo = Assert.Single(http.Pedidos).Cuerpo;
        foreach (var h in HerramientasCoach.Definiciones) Assert.Contains($"\"name\":\"{h.Nombre}\"", cuerpo);
        Assert.Equal(7, HerramientasCoach.Definiciones.Count);
        Assert.Contains("\"tool_choice\":\"auto\"", cuerpo);
        Assert.Equal("""{"type":"object","properties":{},"required":[]}""",
            HerramientasCoach.Definiciones.Single(h => h.Nombre == "mis_ejercicios").ParametrosJson);
    }

    [Fact]
    public async Task Obligada_ToolChoiceDeFuncion()
    {
        var (m, http) = Crear(HttpStatusCode.OK, Completado("tool_calls", ConToolCall));
        await m.CompletarAsync([new("user", "x")], HerramientasCoach.Definiciones, true, "proponer_rutina", CancellationToken.None);
        Assert.Contains("\"tool_choice\":{\"type\":\"function\",\"function\":{\"name\":\"proponer_rutina\"}}", Assert.Single(http.Pedidos).Cuerpo);
    }

    [Fact]
    public async Task CortePorLongitud_DescartaToolCalls()
    {
        var (m, _) = Crear(HttpStatusCode.OK, Completado("length", ConToolCall));
        Assert.Empty((await Completar(m)).Llamadas);
    }

    [Fact]
    public async Task FinishContentFilter_Filtrada()
    {
        var (m, _) = Crear(HttpStatusCode.OK, Completado("content_filter", """{"role":"assistant","content":null}"""));
        Assert.True((await Completar(m)).Filtrada);
    }

    [Fact]
    public async Task Error400ContentFilter_Filtrada()
    {
        var (m, _) = Crear(HttpStatusCode.BadRequest, """{"error":{"code":"content_filter","message":"bloqueado"}}""");
        Assert.True((await Completar(m)).Filtrada);
    }

    [Fact]
    public async Task Error400Generico_SoloElStatus()
    {
        var (m, _) = Crear(HttpStatusCode.BadRequest, $$$"""{"error":{"code":"invalid_request","message":"{{{Secreto}}}"}}""");
        var e = await Assert.ThrowsAsync<ModeloRechazoException>(() => Completar(m));
        Assert.Equal(400, e.Status);
        Assert.Null(e.InnerException);
        Assert.DoesNotContain(Secreto, e.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task ConfigOSaturado_NoDisponibleSinCuerpo(HttpStatusCode codigo)
    {
        var (m, _) = Crear(codigo, $$$"""{"error":{"code":"x","message":"{{{Secreto}}}"}}""");
        var e = await Assert.ThrowsAsync<ModeloNoDisponibleException>(() => Completar(m));
        Assert.Null(e.InnerException);
        Assert.DoesNotContain(Secreto, e.ToString());
    }
}
