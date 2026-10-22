using System.Net;
using GymCoach.Api.Auth;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace GymCoach.Api.Functions;

/// <summary>
/// POST /api/coach: un turno de conversación con el coach. El historial lo guarda la PWA; aquí solo se
/// valida, se acota (cupos) y responde el motor (bucle o agente, COACH_MOTOR) con el userId del token.
/// </summary>
public sealed class Coach(Autenticador auth, Limites limites, TimeProvider reloj, IModeloCoach modelo,
    MotorCoach motor, PerfilesCoach perfiles, PerfilesEntrenamiento perfilesEntrenamiento, PlanesDia planesDia)
{
    // Por turno completo (varias rondas de modelo + herramientas).
    // SWA corta las funciones administradas a los 45 s y la PWA espera 45 s: margen para responder el 503.
    static readonly TimeSpan TiempoMaximo = TimeSpan.FromSeconds(38);
    // Retry-After cuando el mismo usuario ya tiene un turno con el agente en curso.
    static readonly TimeSpan TurnoEnCurso = TimeSpan.FromSeconds(3);

    [Function("Coach")]
    public async Task<HttpResponseData> Responder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "coach")] HttpRequestData req,
        CancellationToken cancelacion = default)
    {
        var userId = await auth.UserIdAsync(req);
        if (userId is null) return await Http.NoAutenticado(req);
        // Sin config no se gasta cupo: no es culpa del usuario.
        if (modelo is ModeloSinConfigurar)
            return await Http.JsonAsync(req, HttpStatusCode.ServiceUnavailable, new { error = "coach_no_configurado" });
        if (!limites.Coach.Permitir(userId, out var espera)) return await Http.DemasiadasPeticiones(req, espera);

        var body = await Http.LeerBodyAsync(req, ValidacionCoach.MaxBodyBytes);
        if (body is null) return await Http.Invalido(req, [new("body", $"Máximo {ValidacionCoach.MaxBodyBytes} bytes.")]);
        var (peticion, errores) = ValidacionCoach.Peticion(body, reloj.GetUtcNow());
        if (peticion is null) return await Http.Invalido(req, errores);
        // Con el agente, un turno a la vez por usuario (comparten la conversación). Antes del cupo diario: no lo gasta.
        using var turno = motor.Reservar(userId);
        if (turno is null) return await Http.DemasiadasPeticiones(req, TurnoEnCurso);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancelacion);
        cts.CancelAfter(TiempoMaximo);
        var cupoDiarioTomado = false;
        try
        {
            // En paralelo. El perfil del coach es obligatorio; el de entrenamiento y el plan de hoy son contexto: si Cosmos
            // está saturado, el turno sigue sin ellos (proponer_rutina falla cerrado sin perfil de entrenamiento).
            var leerPerfil = perfiles.LeerAsync(userId, cts.Token);
            var leerEntrenamiento = Opcional(() => perfilesEntrenamiento.ObtenerAsync(userId, peticion.Hoy, peticion.OffsetMin, cts.Token));
            var leerPlan = Opcional(() => planesDia.LeerAsync(userId, peticion.Hoy, cts.Token));
            await Task.WhenAll(leerPerfil, leerEntrenamiento, leerPlan);
            var perfil = await leerPerfil;
            // El cupo diario se gasta solo con un mensaje válido que de verdad va al modelo.
            if (!limites.CoachDiario.Permitir(userId, out espera)) return await Http.DemasiadasPeticiones(req, espera);
            cupoDiarioTomado = true;

            var ctx = new ContextoCoach(userId, peticion.Hoy, reloj.GetUtcNow(), perfil, await leerEntrenamiento, await leerPlan,
                peticion.Idioma);
            // La validación exige que el último mensaje sea del usuario: el motor siempre tiene uno.
            var r = await motor.ResponderAsync(ctx, peticion.Mensajes, cts.Token);
            return await Http.Ok(req, new
            {
                respuesta = r.Respuesta,
                herramientas = r.Herramientas,
                rutina = r.Rutina is { } p
                    ? new { p.Rutina.Id, p.Rutina.Nombre, p.Rutina.Ejercicios, p.Rutina.Actualizada, p.MinutosEstimados }
                    : null,
                programa = r.Programa is { } g
                    ? new { g.Nombre, rutinas = g.Rutinas.Select(p => new { p.Rutina.Id, p.Rutina.Nombre, p.Rutina.Ejercicios, p.Rutina.Actualizada, p.MinutosEstimados }) }
                    : null,
                // Solo si recordar cambió algo. PerfilCoach fuerza null en minutosDisponibles/objetivo (JsonIgnore Never).
                perfil = r.Perfil,
            });
        }
        // El 429 de Azure OpenAI no sale como 429: el cupo que se agotó no es el del usuario.
        // OperationCanceled: venció el tope del turno (o Cosmos canceló por tiempo).
        catch (Exception e) when (e is ModeloNoDisponibleException or OperationCanceledException ||
                                  e is CosmosException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                                      or HttpStatusCode.RequestTimeout })
        {
            // Falla transitoria nuestra: el mensaje no cuenta para el tope diario. Si el cliente cortó la
            // conexión no se devuelve: abortar a propósito tras gastar tokens daría mensajes ilimitados.
            if (cupoDiarioTomado && !cancelacion.IsCancellationRequested) limites.CoachDiario.Liberar(userId);
            return await Http.NoDisponible(req);
        }
    }

    // Func: una excepción síncrona del llamado también cae en el catch.
    static async Task<T?> Opcional<T>(Func<Task<T?>> leer) where T : class
    {
        try { return await leer(); }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                                            or HttpStatusCode.RequestTimeout) { return null; }
    }
}
