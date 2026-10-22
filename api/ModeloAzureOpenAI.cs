using System.ClientModel;
using System.ClientModel.Primitives;
using OpenAI;
using OpenAI.Chat;

namespace GymCoach.Api;

/// <summary>Adaptador al deployment de Azure OpenAI vía el SDK oficial OpenAI contra el endpoint v1.</summary>
public sealed class ModeloAzureOpenAI : IModeloCoach
{
    // Incluye el razonamiento: con "minimal" sobra para ~120 palabras o unas tool calls.
    const int MaxTokensSalida = 1200;
    readonly ChatClient _cliente;
    readonly string? _razonamiento;

    public ModeloAzureOpenAI(string endpoint, string key, string deployment, string? razonamiento)
        : this(endpoint, key, deployment, razonamiento, null) { }

    /// <summary>transporte: para las pruebas (respuestas HTTP falsas); null = HTTP real.</summary>
    internal ModeloAzureOpenAI(string endpoint, string key, string deployment, string? razonamiento, PipelineTransport? transporte)
    {
        var opciones = new OpenAIClientOptions
        {
            // El endpoint v1 de Azure acepta el cliente de OpenAI tal cual; la barra final importa al unir rutas.
            Endpoint = new Uri(endpoint.TrimEnd('/') + "/openai/v1/"),
            // Medido: el deployment a veces tarda ~13 s en una llamada normal. Con 12 s + reintento esos picos
            // se volvían ~33 s; sin reintento y con 25 s el pico cuesta lo que tarda. El tope del turno (38 s) manda.
            NetworkTimeout = TimeSpan.FromSeconds(25),
            RetryPolicy = new ClientRetryPolicy(0),
        };
        if (transporte is not null) opciones.Transport = transporte;
        _cliente = new ChatClient(deployment, new ApiKeyCredential(key), opciones);
        _razonamiento = string.IsNullOrWhiteSpace(razonamiento) ? null : razonamiento.Trim();
    }

    public Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
        bool permitirHerramientas, CancellationToken ct) =>
        CompletarAsync(mensajes, herramientas, permitirHerramientas, null, ct);

    public async Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
        bool permitirHerramientas, string? obligada, CancellationToken ct)
    {
        // Sin Temperature: los modelos de razonamiento solo aceptan el valor por defecto.
        var opciones = new ChatCompletionOptions
        {
            MaxOutputTokenCount = MaxTokensSalida,
            // Las herramientas van siempre (el historial puede traer tool calls); "none" impide pedir más.
            ToolChoice = !permitirHerramientas ? ChatToolChoice.CreateNoneChoice()
                : obligada is not null ? ChatToolChoice.CreateFunctionChoice(obligada)
                : ChatToolChoice.CreateAutoChoice(),
        };
        if (_razonamiento is not null)
        {
            // Marcado experimental en el SDK (OPENAI001): es la única forma de mandar reasoning_effort.
#pragma warning disable OPENAI001
            opciones.ReasoningEffortLevel = new ChatReasoningEffortLevel(_razonamiento);
#pragma warning restore OPENAI001
        }
        foreach (var h in herramientas)
            opciones.Tools.Add(ChatTool.CreateFunctionTool(h.Nombre, h.Descripcion, BinaryData.FromString(h.ParametrosJson)));

        ChatCompletion completado;
        try
        {
            completado = (await _cliente.CompleteChatAsync(mensajes.Select(AlSdk), opciones, ct)).Value;
        }
        catch (ClientResultException e) when (e.Status == 400 && EsFiltroDeContenido(e))
        {
            return new RespuestaModelo(null, [], Filtrada: true);
        }
        // Sin la excepción interna: su Message trae el cuerpo de Azure, que puede repetir el prompt y el perfil
        // (datos de salud), y ManejoErrores lo loguearía. Solo el status.
        catch (ClientResultException e) when (e.Status is 0 or 408 or 429 or >= 500)
        {
            throw new ModeloNoDisponibleException($"Azure OpenAI respondió {e.Status}.");
        }
        catch (ClientResultException e) when (e.Status is 401 or 403 or 404)
        {
            // Key, permisos o deployment mal configurados: para el usuario es "no disponible".
            throw new ModeloNoDisponibleException($"Azure OpenAI respondió {e.Status} (revisar configuración).");
        }
        catch (ClientResultException e)
        {
            throw new ModeloRechazoException(e.Status);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // NetworkTimeout del SDK: el turno sigue vivo, es el modelo el que no respondió.
            throw new ModeloNoDisponibleException("Azure OpenAI no respondió a tiempo.");
        }

        var entrada = completado.Usage?.InputTokenCount ?? 0;
        var salida = completado.Usage?.OutputTokenCount ?? 0;
        if (completado.FinishReason == ChatFinishReason.ContentFilter)
            return new RespuestaModelo(null, [], Filtrada: true, entrada, salida);

        var texto = string.Concat(completado.Content
            .Where(p => p.Kind == ChatMessageContentPartKind.Text)
            .Select(p => p.Text));
        // Una negativa del modelo se trata como filtrada: respuesta fija, sin repetir su texto.
        if (string.IsNullOrWhiteSpace(texto) && !string.IsNullOrWhiteSpace(completado.Refusal))
            return new RespuestaModelo(null, [], Filtrada: true, entrada, salida);

        // Cortado por longitud: los argumentos de una tool call pueden venir truncados, solo vale el texto.
        IReadOnlyList<LlamadaHerramienta> llamadas = completado.FinishReason == ChatFinishReason.Length
            ? []
            : [.. completado.ToolCalls
                .Where(t => t.Kind == ChatToolCallKind.Function)
                .Select(t => new LlamadaHerramienta(t.Id, t.FunctionName, t.FunctionArguments.ToString()))];
        return new RespuestaModelo(string.IsNullOrWhiteSpace(texto) ? null : texto, llamadas, false, entrada, salida);
    }

    static ChatMessage AlSdk(MensajeChat m) => m.Rol switch
    {
        "system" => new SystemChatMessage(m.Texto ?? ""),
        "user" => new UserChatMessage(m.Texto ?? ""),
        "tool" => new ToolChatMessage(m.LlamadaId ?? "", m.Texto ?? ""),
        "assistant" when m.Llamadas is { Count: > 0 } => ConLlamadas(m),
        "assistant" => new AssistantChatMessage(m.Texto ?? ""),
        _ => throw new ArgumentException($"Rol desconocido: {m.Rol}"),
    };

    static AssistantChatMessage ConLlamadas(MensajeChat m)
    {
        var msg = new AssistantChatMessage(m.Llamadas!.Select(l =>
            ChatToolCall.CreateFunctionToolCall(l.Id, l.Nombre, BinaryData.FromString(l.ArgumentosJson))));
        if (!string.IsNullOrEmpty(m.Texto)) msg.Content.Add(ChatMessageContentPart.CreateTextPart(m.Texto));
        return msg;
    }

    // Azure responde 400 con código content_filter cuando el filtro bloquea el prompt.
    static bool EsFiltroDeContenido(ClientResultException e) =>
        e.GetRawResponse()?.Content?.ToString().Contains("content_filter", StringComparison.Ordinal) == true;
}
