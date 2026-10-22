using System.Net;
using GymCoach.Api.Auth;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace GymCoach.Api.Functions;

/// <summary>
/// Importa historial (p. ej. de Hevy) en lotes. Solo crea, nunca pisa: una serie que ya existe (reimportar, o una
/// que se corrigió a mano después) se deja como está. Así el récord solo puede subir y basta con fusionar las
/// nuevas (barato) en vez de recalcular con una consulta por ejercicio.
/// </summary>
public sealed class Importar(Autenticador auth, Container series, Limites limites, TimeProvider reloj,
    PerfilesEntrenamiento perfilesEntrenamiento, ILogger<Importar>? log = null)
{
    // Por lote; la PWA espera entre lotes y reintenta el 503.
    static readonly TimeSpan TiempoMaximo = TimeSpan.FromSeconds(20);

    public sealed record Resultado(int Guardadas, int Existentes, int RecordsActualizados, int RecordsPendientes);

    sealed record SoloId(string Id);

    [Function("ImportarSeries")]
    public async Task<HttpResponseData> Guardar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "importar/series")] HttpRequestData req,
        CancellationToken cancelacion = default)
    {
        var userId = await auth.UserIdAsync(req);
        if (userId is null) return await Http.NoAutenticado(req);
        if (!limites.PermitirImportacion(userId, out var espera)) return await Http.DemasiadasPeticiones(req, espera);

        var body = await Http.LeerBodyAsync(req, Validacion.MaxBodyImportarBytes);
        if (body is null) return await Http.Invalido(req, [new("body", $"Máximo {Validacion.MaxBodyImportarBytes} bytes.")]);

        var (lote, errores) = Validacion.Importar(body, reloj.GetUtcNow());
        if (lote is null) return await Http.Invalido(req, errores);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancelacion);
        cts.CancelAfter(TiempoMaximo);
        var ct = cts.Token;

        if (await limites.BloquearAsync(userId) is not SemaphoreSlim candado) return await Http.NoDisponible(req);
        try
        {
            List<Serie> nuevas;
            try
            {
                // ReadMany: ~1 RU por id, contra el recálculo que costaría pisar series ya guardadas.
                var claves = lote.Select(s => (s.Id, new PartitionKey(userId))).ToList();
                var existentes = (await series.ReadManyItemsAsync<SoloId>(claves, cancellationToken: ct))
                    .Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
                nuevas = [.. lote.Where(s => !existentes.Contains(s.Id))];

                if (nuevas.Count > 0)
                {
                    var batch = series.CreateTransactionalBatch(new PartitionKey(userId));
                    foreach (var s in nuevas) batch.CreateItem(SerieDoc.De(s, userId));
                    using var res = await batch.ExecuteAsync(ct);
                    if (!res.IsSuccessStatusCode)
                    {
                        log?.LogWarning("Importación sin guardar: {Status}", res.StatusCode);
                        // 409: otra instancia creó uno de los ids entre la lectura y el batch; al reintentar ya sale como existente.
                        return Transitorio(res.StatusCode) || res.StatusCode == HttpStatusCode.Conflict
                            ? await Http.NoDisponible(req)
                            : await Http.Interno(req);
                    }
                }
            }
            catch (CosmosException e)
            {
                // Solo el status: el mensaje de Cosmos trae la partición (userId).
                log?.LogWarning("Importación sin guardar: {Status}", e.StatusCode);
                return Transitorio(e.StatusCode) ? await Http.NoDisponible(req) : await Http.Interno(req);
            }
            catch (OperationCanceledException)
            {
                log?.LogWarning("Importación sin guardar: tiempo agotado");
                return await Http.NoDisponible(req);
            }

            // Las series ya están guardadas: lo que falle en los récords queda pendiente, no tumba la respuesta.
            int actualizados = 0, pendientes = 0;
            var ahora = reloj.GetUtcNow();
            // Por ts: con empate la marca queda en la primera serie, igual que en el recálculo.
            foreach (var grupo in nuevas.OrderBy(s => s.Ts, StringComparer.Ordinal).GroupBy(s => s.EjercicioId, StringComparer.Ordinal))
            {
                if (await MantenerRecordAsync(userId, grupo.Key, [.. grupo], ahora, ct)) actualizados++;
                else pendientes++;
            }

            // El perfil de entrenamiento se recalcula en la próxima lectura (no aquí: un recálculo por lote sería caro).
            if (nuevas.Count > 0) await MarcarPerfilAsync(userId);

            log?.LogInformation("Importación: {Guardadas} series, {Existentes} existentes, {Actualizados} récords, {Pendientes} pendientes",
                nuevas.Count, lote.Count - nuevas.Count, actualizados, pendientes);
            return await Http.Ok(req, new Resultado(nuevas.Count, lote.Count - nuevas.Count, actualizados, pendientes));
        }
        finally
        {
            candado.Release();
        }
    }

    /// <summary>
    /// Suma las series nuevas al récord (ETag, sin consulta), también pasado el tope. Solo recalcula desde el
    /// historial si el doc no existe o está marcado/inválido. false = quedó pendiente (marcado para la siguiente operación).
    /// </summary>
    async Task<bool> MantenerRecordAsync(string userId, string ejercicioId, List<Serie> nuevas, DateTimeOffset ahora,
        CancellationToken ct)
    {
        try
        {
            var lectura = await Records.LeerAsync(series, userId, ejercicioId, ct);
            // Sin doc se arma desde el historial: puede haber series de antes de los récords.
            if (lectura.NecesitaRecalculo || (!lectura.Existe && nuevas.Any(CalculoRecords.Cuenta)))
                await Records.RecalcularAsync(series, userId, ejercicioId, ahora, lectura, ct);
            else
                await Records.ActualizarAsync(series, userId, nuevas, ahora, lectura, ct);
            return true;
        }
        catch (TopeRecalculoException e)
        {
            Records.Loguear(log, e);
            await Records.MarcarExcedeTopeAsync(series, userId, ejercicioId, log, CancellationToken.None);
            return false;
        }
        catch (Exception e)
        {
            Records.Loguear(log, e);
            // Sin el token del lote: si se agotó el tiempo, la marca es justo lo que hace falta para no perder la mejora.
            try
            {
                // Sin doc no hay nada que marcar, y reimportar no lo arregla (las series ya existen): se crea uno vacío
                // marcado, que el siguiente GET /records recalcula.
                if (await Records.MarcarDesactualizadoAsync(series, userId, ejercicioId, CancellationToken.None) is null)
                    await CrearRecordPendienteAsync(series, userId, ejercicioId);
            }
            catch (Exception m)
            {
                log?.LogWarning("Récord sin marcar: {Tipo} {Status}", m.GetType().Name, (m as CosmosException)?.StatusCode);
            }
            return false;
        }
    }

    /// <summary>Best-effort y sin el token del lote (igual que la marca de récords): si falla, el perfil se recalcula mañana.</summary>
    async Task MarcarPerfilAsync(string userId)
    {
        try
        {
            await perfilesEntrenamiento.MarcarDesactualizadoAsync(userId, CancellationToken.None);
        }
        catch (Exception e)
        {
            // Solo el tipo y el status: el mensaje de Cosmos trae la partición (userId).
            log?.LogWarning("Perfil de entrenamiento sin marcar: {Tipo} {Status}", e.GetType().Name, (e as CosmosException)?.StatusCode);
        }
    }

    static async Task CrearRecordPendienteAsync(Container series, string userId, string ejercicioId)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            id = RecordDoc.IdDoc(ejercicioId), userId, tipo = "record", ejercicioId, desactualizado = true,
        });
        using var res = await series.CreateItemStreamAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)),
            new PartitionKey(userId));
        // 409: otro lo creó entre medio; ese ya sirve.
        if (res.StatusCode != HttpStatusCode.Conflict) res.EnsureSuccessStatusCode();
    }

    // Base saturada, lenta o caída: 503 con Retry-After. Cualquier otro fallo es 500 para que la PWA no reintente sin fin.
    static bool Transitorio(HttpStatusCode s) =>
        s is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)s >= 500;
}
