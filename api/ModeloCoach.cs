namespace GymCoach.Api;

/// <summary>Pedido del modelo de ejecutar una herramienta. ArgumentosJson llega tal cual (no confiable).</summary>
public sealed record LlamadaHerramienta(string Id, string Nombre, string ArgumentosJson);

/// <summary>Mensaje del chat independiente del SDK. Rol: system | user | assistant | tool.</summary>
public sealed record MensajeChat(string Rol, string? Texto, IReadOnlyList<LlamadaHerramienta>? Llamadas = null, string? LlamadaId = null);

/// <summary>Filtrada = el filtro de contenido cortó la petición o la respuesta.</summary>
public sealed record RespuestaModelo(string? Texto, IReadOnlyList<LlamadaHerramienta> Llamadas, bool Filtrada = false,
    int TokensEntrada = 0, int TokensSalida = 0);

/// <summary>
/// Turno con memoria en Foundry Agent Service. ConversacionId sale solo del doc del userId del token (nunca del body);
/// Entradas: PromptCoach.Contexto (van como structured_inputs, no se guardan como mensajes).
/// </summary>
public sealed record SesionAgente(string ConversacionId, IReadOnlyDictionary<string, string> Entradas);

/// <summary>El modelo detrás del coach: el bucle no conoce el SDK, así se prueba con un modelo guionado.</summary>
public interface IModeloCoach
{
    Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
        bool permitirHerramientas, CancellationToken ct);

    /// <summary>obligada: herramienta que el modelo debe llamar en esta ronda (null = elige él). Por defecto se ignora.</summary>
    Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
        bool permitirHerramientas, string? obligada, CancellationToken ct) =>
        CompletarAsync(mensajes, herramientas, permitirHerramientas, ct);

    /// <summary>true = el modelo guarda la conversación (agente): con sesión solo se le mandan los items nuevos de la ronda.</summary>
    bool ConMemoria => false;

    /// <summary>sesion: null = sin memoria (todo el historial va en mensajes). Por defecto se ignora.</summary>
    Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
        bool permitirHerramientas, string? obligada, SesionAgente? sesion, CancellationToken ct) =>
        CompletarAsync(mensajes, herramientas, permitirHerramientas, obligada, ct);
}

/// <summary>Conversaciones guardadas en Foundry. Borrar es best-effort: borra también sus responses (traen DATOS).</summary>
public interface IConversacionesAgente
{
    Task<string> CrearAsync(CancellationToken ct);
    /// <summary>true = se borró todo (conversación y sus responses); false = quedó algo (reintentar más tarde). Nunca lanza salvo cancelación.</summary>
    Task<bool> BorrarAsync(string conversacionId, CancellationToken ct);
}

/// <summary>La conversación guardada en Foundry ya no sirve (404, o 400 por un function_call sin su output): hay que renovarla.</summary>
public sealed class ConversacionRotaException(int status) : Exception($"La conversación del agente no sirve ({status}).")
{
    public int Status { get; } = status;
}

/// <summary>Fallo transitorio del modelo (429, 5xx, timeout): el handler responde 503, nunca 429.</summary>
public sealed class ModeloNoDisponibleException(string motivo, Exception? interna = null) : Exception(motivo, interna);

/// <summary>Azure OpenAI rechazó la petición (400 que no es del filtro, etc.). Solo el status: nada del cuerpo.</summary>
public sealed class ModeloRechazoException(int status) : Exception($"Azure OpenAI rechazó la petición ({status}).")
{
    public int Status { get; } = status;
}

/// <summary>Faltan las app settings de Azure OpenAI: el host arranca igual y /api/coach responde 503 coach_no_configurado.</summary>
public sealed class ModeloSinConfigurar : IModeloCoach
{
    // El handler lo detecta antes con `is`: llegar aquí es un error de programación.
    public Task<RespuestaModelo> CompletarAsync(IReadOnlyList<MensajeChat> mensajes, IReadOnlyList<DefinicionHerramienta> herramientas,
        bool permitirHerramientas, CancellationToken ct) =>
        throw new InvalidOperationException("El coach no está configurado.");
}
