using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json;
using OpenAI;
using OpenAI.Conversations;
using OpenAI.Responses;

namespace GymCoach.Api;

// Responses y Conversations de OpenAI 2.14 son experimentales (OPENAI001) y el Patch para los campos de Foundry
// también (SCME0001): no hay otra forma de hablar con el agente sin Azure.AI.Projects.
#pragma warning disable OPENAI001, SCME0001

/// <summary>
/// Adaptador al agente de Foundry Agent Service: Responses API del proyecto ({proyecto}/openai/v1/) con agent_reference
/// y conversación guardada en el servidor. Solo OpenAI 2.14 (Azure.AI.Projects 2.0.1 rompe con esta versión).
/// </summary>
public sealed class ModeloAgenteFoundry : IModeloCoach, IConversacionesAgente
{
    // Igual que ModeloAzureOpenAI: incluye el razonamiento.
    const int MaxTokensSalida = 1200;
    // Borrado: hasta 5 páginas de 100 responses; una conversación de un día no llega ni a una.
    const int PaginasMaximas = 5;
    const int BorradosEnParalelo = 8;
    readonly ResponsesClient _respuestas;
    readonly ConversationClient _conversaciones;
    readonly BinaryData _referenciaAgente;

    public ModeloAgenteFoundry(string proyectoEndpoint, string key, string agente)
        : this(proyectoEndpoint, key, agente, null) { }

    /// <summary>transporte: para las pruebas (respuestas HTTP falsas); null = HTTP real.</summary>
    internal ModeloAgenteFoundry(string proyectoEndpoint, string key, string agente, PipelineTransport? transporte)
    {
        // La barra final importa al unir rutas.
        var endpoint = new Uri(proyectoEndpoint.TrimEnd('/') + "/openai/v1/");
        // Mismos tiempos que ModeloAzureOpenAI: sin reintento, 25 s; el tope del turno manda.
        var opcionesRespuestas = new ResponsesClientOptions
        {
            Endpoint = endpoint,
            NetworkTimeout = TimeSpan.FromSeconds(25),
            RetryPolicy = new ClientRetryPolicy(0),
        };
        var opcionesConversaciones = new OpenAIClientOptions
        {
            Endpoint = endpoint,
            NetworkTimeout = TimeSpan.FromSeconds(25),
            RetryPolicy = new ClientRetryPolicy(0),
        };
        if (transporte is not null)
        {
            opcionesRespuestas.Transport = transporte;
            opcionesConversaciones.Transport = transporte;
        }
        // El endpoint del proyecto acepta el header api-key (probado en el bloque 0). Etapa 6: cambiar por
        // new BearerTokenPolicy(cred, "https://ai.azure.com/.default") sin tocar el resto.
        AuthenticationPolicy auth = ApiKeyAuthenticationPolicy.CreateHeaderApiKeyPolicy(new ApiKeyCredential(key), "api-key");
        _respuestas = new ResponsesClient(auth, opcionesRespuestas);
        _conversaciones = new ConversationClient(auth, opcionesConversaciones);
        _referenciaAgente = BinaryData.FromObjectAsJson(new { type = "agent_reference", name = agente });
    }

    public bool ConMemoria => true;

    // El agente solo se usa con conversación; sin memoria va ModeloAzureOpenAI.
    public Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
        bool permitirHerramientas, CancellationToken ct) =>
        throw new InvalidOperationException("El agente necesita una sesión.");

    public Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
        bool permitirHerramientas, string? obligada, CancellationToken ct) =>
        throw new InvalidOperationException("El agente necesita una sesión.");

    /// <summary>
    /// mensajes: solo los items nuevos de la ronda (user, tool, system de corrección). herramientas se ignora: las tools
    /// y las instrucciones viven en el agente (mandar instructions con agent_reference da 400).
    /// </summary>
    public async Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
        bool permitirHerramientas, string? obligada, SesionAgente? sesion, CancellationToken ct)
    {
        if (sesion is null) throw new InvalidOperationException("El agente necesita una sesión.");

        var opciones = new CreateResponseOptions
        {
            MaxOutputTokenCount = MaxTokensSalida,
            // Una conversación larga no revienta el contexto: el servidor descarta lo más viejo.
            TruncationMode = ResponseTruncationMode.Auto,
            ConversationOptions = new ResponseConversationOptions(sesion.ConversacionId),
            ToolChoice = !permitirHerramientas ? ResponseToolChoice.CreateNoneChoice()
                : obligada is not null ? ResponseToolChoice.CreateFunctionChoice(obligada)
                : ResponseToolChoice.CreateAutoChoice(),
        };
        // El prompt de sistema vive en el agente (su contexto va por structured_inputs): un "system" nuevo de la ronda es una
        // corrección del bucle y va como developer. assistant se ignora: ya quedó en la conversación.
        foreach (var m in mensajes)
        {
            if (m.Rol == "user") opciones.InputItems.Add(ResponseItem.CreateUserMessageItem(m.Texto ?? ""));
            else if (m.Rol == "system") opciones.InputItems.Add(ResponseItem.CreateDeveloperMessageItem(m.Texto ?? ""));
            else if (m.Rol == "tool") opciones.InputItems.Add(ResponseItem.CreateFunctionCallOutputItem(m.LlamadaId ?? "", m.Texto ?? ""));
        }
        if (opciones.InputItems.Count == 0) throw new InvalidOperationException("La ronda no trae items nuevos para el agente.");
        // Campos de Foundry que el SDK no modela: van por Patch.
        opciones.Patch.Set("$.agent_reference"u8, _referenciaAgente);
        opciones.Patch.Set("$.structured_inputs"u8, BinaryData.FromObjectAsJson(sesion.Entradas));

        ResponseResult r;
        try
        {
            r = (await _respuestas.CreateResponseAsync(opciones, ct)).Value;
        }
        catch (ClientResultException e) when (e.Status == 400 && EsFiltroDeContenido(e))
        {
            return new RespuestaModelo(null, [], Filtrada: true);
        }
        // La conversación ya no existe (404) o quedó inválida (400: p. ej. un function_call sin su output): hay que renovarla.
        catch (ClientResultException e) when (e.Status is 400 or 404)
        {
            throw new ConversacionRotaException(e.Status);
        }
        catch (Exception e) when (Traducir(e, ct) is { } traducida)
        {
            throw traducida;
        }

        var entrada = r.Usage?.InputTokenCount ?? 0;
        var salida = r.Usage?.OutputTokenCount ?? 0;
        if (r.Status == ResponseStatus.Failed && r.Error?.Code.ToString()?.Contains("content_filter", StringComparison.Ordinal) == true)
            return new RespuestaModelo(null, [], Filtrada: true, entrada, salida);
        // Failed, Cancelled, Queued, InProgress (no pedimos background): no hay respuesta que usar.
        if (r.Status is not (ResponseStatus.Completed or ResponseStatus.Incomplete))
            throw new ModeloNoDisponibleException($"Foundry devolvió la respuesta con estado {r.Status}.");
        var incompleta = r.Status == ResponseStatus.Incomplete;
        if (incompleta && r.IncompleteStatusDetails?.Reason == ResponseIncompleteStatusReason.ContentFilter)
            return new RespuestaModelo(null, [], Filtrada: true, entrada, salida);

        var partes = r.OutputItems.OfType<MessageResponseItem>().SelectMany(m => m.Content).ToList();
        var texto = string.Concat(partes.Where(p => p.Kind == ResponseContentPartKind.OutputText).Select(p => p.Text));
        // Una negativa del modelo se trata como filtrada: respuesta fija, sin repetir su texto.
        if (string.IsNullOrWhiteSpace(texto) && partes.Any(p => p.Kind == ResponseContentPartKind.Refusal))
            return new RespuestaModelo(null, [], Filtrada: true, entrada, salida);

        // Cortado por longitud: los argumentos de una tool call pueden venir truncados, solo vale el texto.
        IReadOnlyList<LlamadaHerramienta> llamadas = incompleta
            ? []
            : [.. r.OutputItems.OfType<FunctionCallResponseItem>()
                .Select(f => new LlamadaHerramienta(f.CallId, f.FunctionName, f.FunctionArguments?.ToString() ?? "{}"))];
        return new RespuestaModelo(string.IsNullOrWhiteSpace(texto) ? null : texto, llamadas, false, entrada, salida);
    }

    /// <summary>Conversación vacía, sin metadata: nada del usuario sale de nuestra base.</summary>
    public async Task<string> CrearAsync(CancellationToken ct)
    {
        try
        {
            var resultado = await _conversaciones.CreateConversationAsync(BinaryContent.Create(BinaryData.FromString("{}")),
                new RequestOptions { CancellationToken = ct });
            using var doc = JsonDocument.Parse(resultado.GetRawResponse().Content);
            return doc.RootElement.GetProperty("id").GetString()
                ?? throw new ModeloNoDisponibleException("Foundry no devolvió el id de la conversación.");
        }
        catch (Exception e) when (Traducir(e, ct) is { } traducida)
        {
            throw traducida;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // 2xx con un cuerpo que no trae el id como esperamos.
            throw new ModeloNoDisponibleException("Foundry devolvió una conversación sin id.");
        }
    }

    /// <summary>
    /// Nunca lanza: true = la conversación quedó borrada (o ya no existía) y no quedan responses suyas; false = algo
    /// quedó (incluido ct vencido), el llamador reintenta más tarde. Borrar la conversación NO borra sus responses, y
    /// cada una guarda las instrucciones renderizadas con los DATOS del usuario: se borran aparte.
    /// </summary>
    public async Task<bool> BorrarAsync(string conversacionId, CancellationToken ct)
    {
        try
        {
            // El transporte no siempre mira el token antes de enviar.
            if (ct.IsCancellationRequested) return false;
            // Primero la conversación: una sola llamada y es la que guarda los mensajes con datos.
            var conversacion = await BorrarConversacionAsync(conversacionId, ct);
            var responses = await BorrarResponsesAsync(conversacionId, ct);
            return conversacion && responses && !ct.IsCancellationRequested;
        }
        catch
        {
            return false;
        }
    }

    async Task<bool> BorrarConversacionAsync(string conversacionId, CancellationToken ct)
    {
        try
        {
            await _conversaciones.DeleteConversationAsync(conversacionId, new RequestOptions { CancellationToken = ct });
            return true;
        }
        catch (ClientResultException e) when (e.Status == 404)
        {
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }

    async Task<bool> BorrarResponsesAsync(string conversacionId, CancellationToken ct)
    {
        // Medido contra Foundry: GET responses?conversation_id= filtra en el servidor (1 llamada por 100 responses).
        // No está documentado: se vuelve a filtrar aquí por conversation.id para no borrar nunca las de otro usuario.
        // Plan B si deja de filtrar: los items de la conversación traen created_by.response_id (más llamadas).
        // Se pide siempre la primera página: lo borrado ya no aparece, así no depende del cursor.
        for (var vuelta = 0; vuelta < PaginasMaximas; vuelta++)
        {
            if (await ListarResponsesAsync(conversacionId, ct) is not { } listado) return false;
            var (ids, hayMas) = listado;
            // Página sin nada nuestro pero con más: el servidor no filtró y no se puede saber si quedan.
            if (ids.Count == 0) return !hayMas;

            var fallidos = 0;
            await Parallel.ForEachAsync(ids, new ParallelOptions { MaxDegreeOfParallelism = BorradosEnParalelo, CancellationToken = ct },
                async (id, t) =>
                {
                    try { await _respuestas.DeleteResponseAsync(id, t); }
                    catch (ClientResultException e) when (e.Status == 404) { }
                    // Un DELETE que falla o se pasa de tiempo no corta el resto; el resultado queda en false.
                    catch (Exception e) when (!(e is OperationCanceledException && t.IsCancellationRequested))
                    {
                        Interlocked.Increment(ref fallidos);
                    }
                });
            if (fallidos > 0) return false;
            if (!hayMas) return true;
        }
        return false;
    }

    /// <summary>null = el listado falló.</summary>
    async Task<(List<string> Ids, bool HayMas)?> ListarResponsesAsync(string conversacionId, CancellationToken ct)
    {
        // El SDK 2.14 no tiene listado de responses: GET crudo por el pipeline del cliente (lleva la auth).
        using var mensaje = _respuestas.Pipeline.CreateMessage();
        mensaje.Request.Method = "GET";
        mensaje.Request.Uri = new Uri(_respuestas.Endpoint,
            $"responses?conversation_id={Uri.EscapeDataString(conversacionId)}&limit=100");
        mensaje.Apply(new RequestOptions { CancellationToken = ct });
        await _respuestas.Pipeline.SendAsync(mensaje);
        if (mensaje.Response is not { IsError: false } respuesta) return null;

        using var doc = JsonDocument.Parse(respuesta.Content);
        var ids = new List<string>();
        foreach (var r in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            if (r.TryGetProperty("conversation", out var c) && c.ValueKind == JsonValueKind.Object
                && c.TryGetProperty("id", out var cid) && cid.GetString() == conversacionId
                && r.GetProperty("id").GetString() is { } id)
                ids.Add(id);
        }
        var hayMas = doc.RootElement.TryGetProperty("has_more", out var m) && m.ValueKind == JsonValueKind.True;
        return (ids, hayMas);
    }

    /// <summary>
    /// Misma traducción que ModeloAzureOpenAI. Sin la excepción interna: su Message trae el cuerpo de Foundry,
    /// que puede repetir el prompt y los DATOS. null = no se traduce (cancelación del turno).
    /// </summary>
    static Exception? Traducir(Exception e, CancellationToken ct) => e switch
    {
        ClientResultException { Status: 0 or 408 or 429 or >= 500 } c => new ModeloNoDisponibleException($"Foundry respondió {c.Status}."),
        // Key, permisos, agente o conversación inexistentes: para el usuario es "no disponible".
        ClientResultException { Status: 401 or 403 or 404 } c => new ModeloNoDisponibleException($"Foundry respondió {c.Status} (revisar configuración)."),
        ClientResultException c => new ModeloRechazoException(c.Status),
        // NetworkTimeout del SDK: el turno sigue vivo, es el modelo el que no respondió.
        OperationCanceledException when !ct.IsCancellationRequested => new ModeloNoDisponibleException("Foundry no respondió a tiempo."),
        _ => null,
    };

    // Azure responde 400 con código content_filter cuando el filtro bloquea el prompt.
    static bool EsFiltroDeContenido(ClientResultException e) =>
        e.GetRawResponse()?.Content?.ToString().Contains("content_filter", StringComparison.Ordinal) == true;
}
#pragma warning restore OPENAI001, SCME0001
