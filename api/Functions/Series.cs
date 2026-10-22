using System.Net;
using GymCoach.Api.Auth;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace GymCoach.Api.Functions;

/// <summary>
/// CRUD de series. Toda operación va con partitionKey = userId del token,
/// así un usuario no puede leer ni tocar documentos de otro aunque conozca el id.
/// Guardar y borrar mantienen además el récord del ejercicio (ver Records). Editar o borrar una serie marca el perfil de
/// entrenamiento (una serie nueva no: lo de hoy el coach lo ve en vivo y el perfil se recalcula cada día).
/// perfilesEntrenamiento opcional y al final: los new Series(...) de las pruebas siguen valiendo; la DI lo resuelve.
/// </summary>
public sealed class Series(Autenticador auth, Container series, Limites limites, TimeProvider reloj, ILogger<Series>? log = null,
    PerfilesEntrenamiento? perfilesEntrenamiento = null)
{
    [Function("GuardarSerie")]
    public async Task<HttpResponseData> Guardar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "series/{id}")] HttpRequestData req, string id)
    {
        var userId = await auth.UserIdAsync(req);
        if (userId is null) return await Http.NoAutenticado(req);
        if (!limites.PermitirEscritura(userId, out var espera)) return await Http.DemasiadasPeticiones(req, espera);

        var body = await Http.LeerBodyAsync(req, Validacion.MaxBodyBytes);
        if (body is null) return await Http.Invalido(req, [new("body", $"Máximo {Validacion.MaxBodyBytes} bytes.")]);

        var (serie, errores) = Validacion.Serie(id, body, reloj.GetUtcNow());
        if (serie is null) return await Http.Invalido(req, errores);

        if (await limites.BloquearAsync(userId) is not SemaphoreSlim candado) return await Http.NoDisponible(req);
        try
        {
            var vieja = await LeerAsync(userId, serie.Id);
            var ejercicio = serie.EjercicioId;
            var otro = vieja is not null && vieja.EjercicioId != ejercicio ? vieja.EjercicioId : null;
            // null = no se pudo leer: la serie se guarda igual y el récord queda marcado.
            var lectura = await LeerRecordAsync(userId, ejercicio);
            var lecturaOtro = otro is null ? lectura : await LeerRecordAsync(userId, otro);

            // La vieja podía sostener el récord de su ejercicio y la edición se lo quita.
            var quita = vieja is not null && CalculoRecords.Empeora(vieja, serie) &&
                lecturaOtro is { Record: RecordEjercicio rv, Desactualizado: false } && CalculoRecords.PodriaSerDuena(rv, vieja);
            // Pasado el tope ya no se recalcula: el récord puede quedar alto (limitación conocida).
            if (quita && lecturaOtro!.ExcedeTope)
            {
                Records.Loguear(log, new TopeRecalculoException());
                quita = false;
            }
            // Sin doc se arma desde el historial (las series de antes de los récords también cuentan).
            var recalcular = lectura is not null &&
                (lectura.NecesitaRecalculo || (!lectura.Existe && CalculoRecords.Cuenta(serie)) || (otro is null && quita));
            var recalcularOtro = otro is not null && lecturaOtro is not null && (lecturaOtro.NecesitaRecalculo || quita);

            // Cada recálculo es una consulta del ejercicio: pide cupo antes de escribir nada.
            for (var i = (recalcular ? 1 : 0) + (recalcularOtro ? 1 : 0); i > 0; i--)
                if (!limites.Recalculos.Permitir(userId, out var esperaRecalculo))
                    return await Http.DemasiadasPeticiones(req, esperaRecalculo);

            // Si la edición puede dejar el récord alto, se marca antes de escribir: si después el recálculo falla, la
            // marca ya está. Si marcar falla, 503 sin tocar la serie (la PWA reintenta).
            if (quita)
            {
                if (await MarcarAntesAsync(userId, otro ?? ejercicio, lecturaOtro!) is not Records.Lectura marcada)
                    return await Http.NoDisponible(req);
                if (otro is null) lectura = marcada;
                lecturaOtro = marcada;
            }

            // Upsert: reintentar el mismo PUT (UI optimista) no duplica.
            await series.UpsertItemAsync(SerieDoc.De(serie, userId), new PartitionKey(userId));

            var ahora = reloj.GetUtcNow();
            if (otro is not null)
            {
                if (recalcularOtro) await MantenerAsync(userId, otro, () => Records.RecalcularAsync(series, userId, otro, ahora, lecturaOtro));
                else if (lecturaOtro is null && CalculoRecords.Cuenta(vieja!)) await MarcarAsync(userId, otro);
            }
            if (lectura is null) await MarcarAsync(userId, ejercicio);
            // El recálculo ya incluye la serie nueva.
            else if (recalcular) await MantenerAsync(userId, ejercicio, () => Records.RecalcularAsync(series, userId, ejercicio, ahora, lectura));
            else if (CalculoRecords.Cuenta(serie))
                await MantenerAsync(userId, ejercicio, () => Records.ActualizarAsync(series, userId, serie, ahora, lectura));

            // Reintentar el mismo PUT (UI optimista) no cambia nada: no marca.
            if (vieja is not null && vieja != serie) await MarcarPerfilAsync(userId);
        }
        finally
        {
            candado.Release();
        }
        return await Http.Ok(req, serie);
    }

    [Function("BorrarSerie")]
    public async Task<HttpResponseData> Borrar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "series/{id}")] HttpRequestData req, string id)
    {
        var userId = await auth.UserIdAsync(req);
        if (userId is null) return await Http.NoAutenticado(req);
        if (!limites.PermitirEscritura(userId, out var espera)) return await Http.DemasiadasPeticiones(req, espera);
        if (!Guid.TryParseExact(id, "D", out var guid)) return await Http.Invalido(req, [new("id", "Debe ser un UUID.")]);

        if (await limites.BloquearAsync(userId) is not SemaphoreSlim candado) return await Http.NoDisponible(req);
        try
        {
            // Lectura previa para saber si era la dueña del récord (y así el 404 sale sin borrar nada).
            var vieja = await LeerAsync(userId, guid.ToString("D"));
            if (vieja is null) return await Http.NoEncontrado(req);
            // Se lee también para un calentamiento: si el doc quedó marcado, esta operación lo repara.
            var lectura = await LeerRecordAsync(userId, vieja.EjercicioId);
            var duena = lectura is { Record: RecordEjercicio r, Desactualizado: false } && CalculoRecords.PodriaSerDuena(r, vieja);
            // Pasado el tope ya no se recalcula: el récord puede quedar alto (limitación conocida).
            if (duena && lectura!.ExcedeTope)
            {
                Records.Loguear(log, new TopeRecalculoException());
                duena = false;
            }
            var recalcular = lectura is not null && (lectura.NecesitaRecalculo || duena);
            if (recalcular && !limites.Recalculos.Permitir(userId, out var esperaRecalculo))
                return await Http.DemasiadasPeticiones(req, esperaRecalculo);

            // Borrar la dueña deja el récord alto hasta recalcular: se marca antes, y si no se puede, 503 sin borrar.
            if (duena)
            {
                if (await MarcarAntesAsync(userId, vieja.EjercicioId, lectura!) is not Records.Lectura marcada)
                    return await Http.NoDisponible(req);
                lectura = marcada;
            }

            try
            {
                await series.DeleteItemAsync<SerieDoc>(vieja.Id, new PartitionKey(userId));
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                return await Http.NoEncontrado(req);
            }

            if (lectura is null && CalculoRecords.Cuenta(vieja)) await MarcarAsync(userId, vieja.EjercicioId);
            else if (recalcular)
                await MantenerAsync(userId, vieja.EjercicioId,
                    () => Records.RecalcularAsync(series, userId, vieja.EjercicioId, reloj.GetUtcNow(), lectura));
            await MarcarPerfilAsync(userId);
        }
        finally
        {
            candado.Release();
        }
        return req.CreateResponse(HttpStatusCode.NoContent);
    }

    /// <summary>La serie del usuario con ese id; null si no existe.</summary>
    async Task<Serie?> LeerAsync(string userId, string id)
    {
        try
        {
            return (await series.ReadItemAsync<SerieDoc>(id, new PartitionKey(userId))).Resource.ADto();
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    // La serie es el dato que importa: si leer, mantener o marcar el récord falla, se loguea y la serie sigue su curso.
    async Task<Records.Lectura?> LeerRecordAsync(string userId, string ejercicioId)
    {
        try { return await Records.LeerAsync(series, userId, ejercicioId); }
        catch (Exception e)
        {
            Records.Loguear(log, e);
            return null;
        }
    }

    /// <summary>
    /// Marca previa a un cambio que puede dejar el récord alto. Devuelve la lectura con el ETag nuevo (el recálculo
    /// la usa sin releer); null si no se pudo marcar.
    /// </summary>
    async Task<Records.Lectura?> MarcarAntesAsync(string userId, string ejercicioId, Records.Lectura lectura)
    {
        try
        {
            // Sin doc (lo borró otra instancia) no hay récord alto que esconder.
            return await Records.MarcarDesactualizadoAsync(series, userId, ejercicioId) is string etag
                ? lectura with { ETag = etag, Desactualizado = true }
                : Records.Lectura.NoExiste;
        }
        catch (Exception e)
        {
            log?.LogWarning("Récord sin marcar: {Tipo} {Status}", e.GetType().Name, (e as CosmosException)?.StatusCode);
            return null;
        }
    }

    // Si falla, el récord puede haber quedado falso: se marca para que lo recalcule la siguiente operación o lectura.
    // Pasado el tope de filas se anota en el doc, para no repetir la consulta en cada operación.
    async Task MantenerAsync(string userId, string ejercicioId, Func<Task> mantener)
    {
        try { await mantener(); }
        catch (Exception e)
        {
            Records.Loguear(log, e);
            if (e is TopeRecalculoException) await Records.MarcarExcedeTopeAsync(series, userId, ejercicioId, log);
            else await MarcarAsync(userId, ejercicioId);
        }
    }

    async Task MarcarAsync(string userId, string ejercicioId)
    {
        try { await Records.MarcarDesactualizadoAsync(series, userId, ejercicioId); }
        catch (Exception e)
        {
            log?.LogWarning("Récord sin marcar: {Tipo} {Status}", e.GetType().Name, (e as CosmosException)?.StatusCode);
        }
    }

    /// <summary>Best-effort, igual que en Importar: si falla, el perfil se recalcula al día siguiente.</summary>
    async Task MarcarPerfilAsync(string userId)
    {
        if (perfilesEntrenamiento is null) return;
        try { await perfilesEntrenamiento.MarcarDesactualizadoAsync(userId, CancellationToken.None); }
        catch (Exception e)
        {
            // Solo el tipo y el status: el mensaje de Cosmos trae la partición (userId).
            log?.LogWarning("Perfil de entrenamiento sin marcar: {Tipo} {Status}", e.GetType().Name, (e as CosmosException)?.StatusCode);
        }
    }

    [Function("ListarSeries")]
    public async Task<HttpResponseData> Listar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "series")] HttpRequestData req)
    {
        var userId = await auth.UserIdAsync(req);
        if (userId is null) return await Http.NoAutenticado(req);
        // Cupo propio: es la consulta más cara de la API y no debe comerse el de lecturas.
        if (!limites.Rangos.Permitir(userId, out var espera)) return await Http.DemasiadasPeticiones(req, espera);

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var (desde, hasta, errores) = Validacion.Rango(query["desde"], query["hasta"]);
        if (errores.Count > 0) return await Http.Invalido(req, errores);

        // fecha es el día local del cliente: el filtro va por fecha, no por ts.
        // TOP max+1: basta para saber si se pasó del tope sin traer todo a memoria.
        var consulta = new QueryDefinition(
                "SELECT TOP @tope c.id, c.ejercicioId, c.pesoKg, c.reps, c.fecha, c.ts, c.rutina, c.tipoSerie FROM c " +
                "WHERE c.userId = @userId AND c.tipo = 'serie' AND c.fecha >= @desde AND c.fecha <= @hasta ORDER BY c.ts ASC")
            .WithParameter("@tope", Validacion.MaxSeriesRango + 1)
            .WithParameter("@userId", userId)
            .WithParameter("@desde", desde.ToString("yyyy-MM-dd"))
            .WithParameter("@hasta", hasta.ToString("yyyy-MM-dd"));

        var resultado = await ConsultarAsync(series, consulta, userId);
        if (resultado.Count > Validacion.MaxSeriesRango)
            return await Http.Invalido(req, [new("hasta", $"Más de {Validacion.MaxSeriesRango} series en el rango, acórtalo.")]);
        return await Http.Ok(req, resultado);
    }

    /// <summary>Las consultas llevan TOP, así que la lista queda acotada.</summary>
    internal static async Task<List<Serie>> ConsultarAsync(Container series, QueryDefinition consulta, string userId)
    {
        var resultado = new List<Serie>();
        using var it = series.GetItemQueryIterator<Serie>(consulta,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(userId) });
        while (it.HasMoreResults)
            resultado.AddRange(await it.ReadNextAsync());
        return resultado;
    }
}
