using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace GymCoach.Api;

/// <summary>
/// Qué motor responde POST /api/coach (COACH_MOTOR): el bucle sin memoria o el agente de Foundry con la conversación de
/// hoy. Si el agente falla en un turno, ese turno lo responde el bucle. PlanesDia usa siempre el bucle, no esta clase.
/// </summary>
public sealed class MotorCoach
{
    // Deja tiempo al bucle de respaldo dentro del tope del turno (38 s).
    static readonly TimeSpan TiempoAgentePorDefecto = TimeSpan.FromSeconds(20);

    readonly ConversacionCoach _bucle;
    readonly ConversacionCoach? _agente;
    readonly ConversacionesCoach? _conversaciones;
    readonly PerfilesCoach? _perfiles;
    readonly ILogger? _log;
    readonly TimeSpan _tiempoAgente;
    // Usuarios con un turno del agente en curso (por instancia). Se quitan al terminar: no crece sin límite.
    readonly ConcurrentDictionary<string, byte> _enCurso = new(StringComparer.Ordinal);

    /// <summary>agente y conversaciones: los dos o ninguno (null = motor bucle). perfiles: para releer el perfil en el respaldo.</summary>
    public MotorCoach(ConversacionCoach bucle, ConversacionCoach? agente = null, ConversacionesCoach? conversaciones = null,
        PerfilesCoach? perfiles = null, ILogger<MotorCoach>? log = null)
        : this(bucle, agente, conversaciones, perfiles, log, TiempoAgentePorDefecto) { }

    /// <summary>tiempoAgente: para las pruebas del sub-tope.</summary>
    internal MotorCoach(ConversacionCoach bucle, ConversacionCoach? agente, ConversacionesCoach? conversaciones, PerfilesCoach? perfiles,
        ILogger? log, TimeSpan tiempoAgente)
    {
        _bucle = bucle;
        (_agente, _conversaciones) = agente is not null && conversaciones is not null ? (agente, conversaciones) : (null, null);
        _perfiles = perfiles;
        _log = log;
        _tiempoAgente = tiempoAgente;
    }

    public bool ConAgente => _agente is not null;

    /// <summary>
    /// Con el agente, un turno a la vez por usuario: dos a la vez sobre la misma conversación la rompen. null = ya hay uno en
    /// curso (el handler responde 429 sin gastar cupo). Con el bucle no hay estado compartido: siempre se puede.
    /// </summary>
    public IDisposable? Reservar(string userId)
    {
        if (_agente is null) return Libre.Instancia;
        return _enCurso.TryAdd(userId, 0) ? new Reserva(_enCurso, userId) : null;
    }

    public async Task<ResultadoCoach> ResponderAsync(ContextoCoach ctx, IReadOnlyList<MensajeEntrada> historial, CancellationToken ct)
    {
        // El id nunca viene de afuera: sin agente, el bucle va sin conversación.
        ctx = ctx with { ConversacionId = null };
        if (_agente is null || _conversaciones is null) return await _bucle.ResponderAsync(ctx, historial, ct);

        // Un solo mensaje del usuario = la PWA empezó conversación ("Nueva conversación" o primer mensaje).
        var nueva = historial.Count(m => m.Rol == "usuario") == 1;
        string? id = null;
        try
        {
            using var sub = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sub.CancelAfter(_tiempoAgente);
            id = await _conversaciones.ObtenerAsync(ctx.UserId, ctx.Hoy, nueva, sub.Token);
            return await _agente.ResponderAsync(ctx with { ConversacionId = id }, historial, sub.Token);
        }
        // Cualquier falla del agente, incluido su sub-tope (OperationCanceled con ct vivo). Si se canceló el turno o cortó
        // el cliente, no hay respaldo: la excepción sale y el handler responde como hoy.
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Solo el tipo: el mensaje puede traer el id de la conversación o el cuerpo de Foundry.
            _log?.LogWarning("Coach: el agente falló ({Tipo}); el turno sigue con el bucle.", e.GetType().Name);
            // Solo se renueva si puede haber quedado rota: Foundry lo dijo, o se cortó a mitad de turno (quizá con un
            // function_call sin su output). Un 429/5xx conserva la memoria; si igual quedó rota, el próximo turno lo sabrá.
            if (e is ConversacionRotaException or OperationCanceledException) await _conversaciones.InvalidarAsync(ctx.UserId, id, ct);
            return await _bucle.ResponderAsync(await ConPerfilFresco(ctx, ct), historial, ct);
        }
    }

    // El agente pudo ejecutar recordar antes de fallar: el respaldo no parte del perfil viejo. Si no se puede releer, sigue igual.
    async Task<ContextoCoach> ConPerfilFresco(ContextoCoach ctx, CancellationToken ct)
    {
        if (_perfiles is null) return ctx;
        try { return ctx with { Perfil = await _perfiles.LeerAsync(ctx.UserId, ct) }; }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _log?.LogWarning("Coach: no se pudo releer el perfil para el respaldo ({Tipo}).", e.GetType().Name);
            return ctx;
        }
    }

    sealed class Reserva(ConcurrentDictionary<string, byte> enCurso, string userId) : IDisposable
    {
        int _liberada;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _liberada, 1) == 0) enCurso.TryRemove(userId, out _);
        }
    }

    sealed class Libre : IDisposable
    {
        public static readonly Libre Instancia = new();
        public void Dispose() { }
    }
}
