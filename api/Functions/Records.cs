using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using GymCoach.Api.Auth;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace GymCoach.Api.Functions;

/// <summary>Más series del ejercicio de las que se recalculan: el récord queda como estaba.</summary>
public sealed class TopeRecalculoException() : Exception("Demasiadas series para recalcular el récord.");

/// <summary>
/// Récords por ejercicio. Se mantienen al escribir series (no se calculan al leer): GET /records es una
/// consulta chica y el coach no tiene que recorrer el historial. Si el mantenimiento falla, el doc queda
/// marcado como desactualizado y lo recalcula la siguiente operación sobre el ejercicio o el siguiente GET.
/// </summary>
public sealed class Records(Autenticador auth, Container series, Limites limites, TimeProvider reloj, ILogger<Records>? log = null)
{
    // Pocos intentos: dentro de una instancia el candado por usuario evita los choques; el ETag cubre el resto.
    const int MaxIntentos = 2;
    // Medido en el emulador: ~3.5 RU fijos + ~0.036 RU por fila; el recálculo más caro (TOP 2001) ronda 81 RU.
    public const int MaxFilasRecalculo = 2000;
    // Recálculos que puede hacer un GET (cada uno gasta además un cupo de recálculos).
    const int MaxRecalculosPorLectura = 3;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    sealed record Fila(string Id, decimal PesoKg, int Reps, string Fecha);

    /// <summary>
    /// Estado del doc de récord. ETag null = no existe. Record null con ETag = doc inválido (se recalcula, nunca se usa).
    /// ExcedeTope: ya se sabe que no se puede recalcular; no se gasta cupo ni consulta en intentarlo.
    /// </summary>
    internal sealed record Lectura(RecordEjercicio? Record, string? ETag, bool Desactualizado, bool ExcedeTope = false)
    {
        public static readonly Lectura NoExiste = new(null, null, false);
        public bool Existe => ETag is not null;
        public bool NecesitaRecalculo => Existe && !ExcedeTope && (Record is null || Desactualizado);
    }

    [Function("ListarRecords")]
    public async Task<HttpResponseData> Listar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "records")] HttpRequestData req)
    {
        var userId = await auth.UserIdAsync(req);
        if (userId is null) return await Http.NoAutenticado(req);
        if (!limites.Records.Permitir(userId, out var espera)) return await Http.DemasiadasPeticiones(req, espera);

        // TOP 100 holgado sobre el catálogo (~53): acota la consulta aunque el catálogo crezca.
        var consulta = new QueryDefinition(
                "SELECT TOP 100 c.id, c.tipo, c.ejercicioId, c.pesoMax, c.e1rm, c.repsMax, c.actualizado, c.desactualizado, " +
                "c.excedeTope, c._etag FROM c WHERE c.userId = @userId AND c.tipo = 'record'")
            .WithParameter("@userId", userId);

        // JsonElement y no RecordDoc: un doc corrupto no debe tumbar la página entera.
        var docs = new List<JsonElement>();
        using (var it = series.GetItemQueryIterator<JsonElement>(consulta,
                   requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(userId) }))
            while (it.HasMoreResults)
                docs.AddRange(await it.ReadNextAsync());

        var resultado = new List<RecordEjercicio>();
        var recalculos = 0;
        foreach (var d in docs)
        {
            var id = d.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString()! : "";
            var ejercicioId = id.StartsWith(RecordDoc.Prefijo, StringComparison.Ordinal) ? id[RecordDoc.Prefijo.Length..] : "";
            if (!Catalogo.Existe(ejercicioId)) continue;

            var etag = d.TryGetProperty("_etag", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            var lectura = Interpretar(d, etag, ejercicioId);
            if (!lectura.NecesitaRecalculo)
            {
                // Pasado el tope se muestra el guardado aunque esté marcado: posiblemente viejo, pero mejor que ocultarlo.
                if (lectura.Record is not null) resultado.Add(lectura.Record);
                continue;
            }

            // Marcado o inválido: se recalcula si hay cupo; si no, se omite (mejor que mostrar un récord falso).
            if (recalculos >= MaxRecalculosPorLectura || !limites.Recalculos.Permitir(userId, out _)) continue;
            recalculos++;
            try
            {
                if (await RecalcularAsync(series, userId, ejercicioId, reloj.GetUtcNow(), lectura) is RecordEjercicio r)
                    resultado.Add(r);
            }
            catch (Exception ex)
            {
                Loguear(log, ex);
                if (ex is TopeRecalculoException)
                {
                    if (lectura.Record is not null) resultado.Add(lectura.Record);
                    await MarcarExcedeTopeAsync(series, userId, ejercicioId, log);
                }
            }
        }
        return await Http.Ok(req, resultado);
    }

    /// <summary>Lee el récord del ejercicio con su ETag. Por stream: un doc corrupto da Lectura inválida, no una excepción.</summary>
    internal static async Task<Lectura> LeerAsync(Container series, string userId, string ejercicioId, CancellationToken ct = default)
    {
        using var res = await series.ReadItemStreamAsync(RecordDoc.IdDoc(ejercicioId), new PartitionKey(userId), cancellationToken: ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return Lectura.NoExiste;
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(res.Content, cancellationToken: ct);
        return Interpretar(doc.RootElement, res.Headers.ETag, ejercicioId);
    }

    static Lectura Interpretar(JsonElement json, string? etag, string ejercicioId)
    {
        var excede = json.TryGetProperty("excedeTope", out var x) && x.ValueKind == JsonValueKind.True;
        try
        {
            var doc = json.Deserialize<RecordDoc>(Json);
            if (doc is not null && doc.EsValido(ejercicioId)) return new(doc.ADto(), etag, doc.Desactualizado, excede);
        }
        catch (JsonException) { }
        return new(null, etag, false, excede);
    }

    /// <summary>
    /// Suma la serie (ya guardada) al récord de su ejercicio si lo mejora: reemplazo condicionado por ETag.
    /// Sin doc, o con doc inválido o marcado, no se fusiona: eso lo resuelve un recálculo. Pasado el tope sí
    /// (es lo único que lo mantiene) y conserva sus marcas.
    /// </summary>
    internal static Task ActualizarAsync(Container series, string userId, Serie serie, DateTimeOffset ahora, Lectura inicial) =>
        ActualizarAsync(series, userId, [serie], ahora, inicial);

    /// <summary>
    /// Igual, pero suma varias series del mismo ejercicio (en orden) con un solo reemplazo. La usa la importación:
    /// con una escritura por serie, cada una tras la primera perdería un intento por ETag.
    /// </summary>
    internal static async Task ActualizarAsync(Container series, string userId, IReadOnlyList<Serie> nuevas, DateTimeOffset ahora,
        Lectura inicial, CancellationToken ct = default)
    {
        if (!nuevas.Any(CalculoRecords.Cuenta)) return;
        var lectura = inicial;
        for (var intento = 0; intento < MaxIntentos; intento++)
        {
            if (intento > 0)
            {
                await EsperarReintento(intento, ct);
                lectura = await LeerAsync(series, userId, nuevas[0].EjercicioId, ct);
            }
            if (lectura.ExcedeTope && lectura.Record is null) return;
            if (!lectura.Existe || lectura.NecesitaRecalculo)
                throw new InvalidOperationException("Récord sin base para sumar la serie.");
            RecordEjercicio? nuevo = null;
            foreach (var serie in nuevas)
                if (CalculoRecords.Fusionar(nuevo ?? lectura.Record, serie) is RecordEjercicio r) nuevo = r;
            if (nuevo is null) return;
            var doc = RecordDoc.De(nuevo, userId, Marca(ahora)) with
            {
                Desactualizado = lectura.Desactualizado,
                ExcedeTope = lectura.ExcedeTope,
            };
            if (await EscribirAsync(series, userId, doc, lectura.ETag, ct)) return;
        }
        throw new InvalidOperationException("Récord sin actualizar tras los reintentos.");
    }

    /// <summary>
    /// Calentamiento fuera (las series viejas no tienen el campo y un != solo las dejaría fuera).
    /// ORDER BY ts: con empate la marca queda en la primera serie, igual que al sumarlas una a una.
    /// TOP tope+1: basta para saber si se pasó sin traer de más.
    /// </summary>
    internal static QueryDefinition ConsultaRecalculo(string userId, string ejercicioId) =>
        new QueryDefinition(
                "SELECT TOP @tope c.id, c.pesoKg, c.reps, c.fecha FROM c WHERE c.userId = @userId AND c.tipo = 'serie' AND " +
                "c.ejercicioId = @ejercicioId AND (NOT IS_DEFINED(c.tipoSerie) OR c.tipoSerie != 'calentamiento') ORDER BY c.ts ASC")
            .WithParameter("@tope", MaxFilasRecalculo + 1)
            .WithParameter("@userId", userId)
            .WithParameter("@ejercicioId", ejercicioId);

    /// <summary>
    /// Recalcula el récord desde las series del ejercicio y devuelve el resultado (null = no quedan series que cuenten,
    /// y el doc se borra). inicial: lectura hecha antes de guardar la serie; su ETag sigue protegiendo el reemplazo.
    /// Como mucho MaxIntentos consultas. Con más de MaxFilasRecalculo series lanza TopeRecalculoException sin tocar el doc.
    /// </summary>
    internal static async Task<RecordEjercicio?> RecalcularAsync(Container series, string userId, string ejercicioId,
        DateTimeOffset ahora, Lectura? inicial = null, CancellationToken ct = default)
    {
        for (var intento = 0; intento < MaxIntentos; intento++)
        {
            if (intento > 0) await EsperarReintento(intento, ct);
            // El doc se lee antes que las series: si otra escritura lo cambia en medio, el ETag no coincide y se repite.
            var lectura = intento == 0 && inicial is not null ? inicial : await LeerAsync(series, userId, ejercicioId, ct);

            var filas = new List<Serie>();
            using (var it = series.GetItemQueryIterator<Fila>(ConsultaRecalculo(userId, ejercicioId),
                       requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(userId) }))
                while (it.HasMoreResults)
                    foreach (var f in await it.ReadNextAsync(ct))
                        filas.Add(new Serie(f.Id, ejercicioId, f.PesoKg, f.Reps, f.Fecha, ""));
            if (filas.Count > MaxFilasRecalculo) throw new TopeRecalculoException();

            var calculado = CalculoRecords.Desde(filas, ejercicioId);
            if (!lectura.NecesitaRecalculo && !lectura.ExcedeTope && calculado == lectura.Record) return calculado;

            if (calculado is null)
            {
                using var res = await series.DeleteItemStreamAsync(RecordDoc.IdDoc(ejercicioId), new PartitionKey(userId),
                    new ItemRequestOptions { IfMatchEtag = lectura.ETag }, ct);
                if (res.StatusCode == HttpStatusCode.PreconditionFailed) continue;
                if (res.StatusCode != HttpStatusCode.NotFound) res.EnsureSuccessStatusCode();
                return null;
            }

            // El reemplazo borra las marcas: el doc vuelve a ser confiable.
            if (await EscribirAsync(series, userId, RecordDoc.De(calculado, userId, Marca(ahora)), lectura.ETag, ct)) return calculado;
        }
        throw new InvalidOperationException("Récord sin recalcular tras los reintentos.");
    }

    /// <summary>
    /// Marca el récord como desactualizado (patch atómico: cambia el ETag, así un reemplazo en curso falla y relee).
    /// Devuelve el ETag nuevo; null si no hay doc (no hay récord falso que esconder y el siguiente PUT del ejercicio
    /// lo arma desde el historial).
    /// </summary>
    internal static async Task<string?> MarcarDesactualizadoAsync(Container series, string userId, string ejercicioId,
        CancellationToken ct = default)
    {
        using var res = await series.PatchItemStreamAsync(RecordDoc.IdDoc(ejercicioId), new PartitionKey(userId),
            [PatchOperation.Set("/desactualizado", true)], cancellationToken: ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        return res.Headers.ETag;
    }

    /// <summary>
    /// Anota que el ejercicio pasó el tope, para no repetir la consulta de ~81 RU en cada operación. Sin doc se crea
    /// uno sin marcas (no se muestra; solo evita reintentar). Best-effort: si falla, se loguea.
    /// </summary>
    internal static async Task MarcarExcedeTopeAsync(Container series, string userId, string ejercicioId, ILogger? log,
        CancellationToken ct = default)
    {
        try
        {
            using var res = await series.PatchItemStreamAsync(RecordDoc.IdDoc(ejercicioId), new PartitionKey(userId),
                [PatchOperation.Set("/excedeTope", true)], cancellationToken: ct);
            if (res.StatusCode != HttpStatusCode.NotFound)
            {
                res.EnsureSuccessStatusCode();
                return;
            }
            var json = JsonSerializer.Serialize(new
            {
                id = RecordDoc.IdDoc(ejercicioId), userId, tipo = "record", ejercicioId, excedeTope = true,
            });
            using var creado = await series.CreateItemStreamAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), new PartitionKey(userId),
                cancellationToken: ct);
            if (creado.StatusCode != HttpStatusCode.Conflict) creado.EnsureSuccessStatusCode();
        }
        catch (Exception e)
        {
            log?.LogWarning("Récord sin anotar el tope: {Tipo} {Status}", e.GetType().Name, (e as CosmosException)?.StatusCode);
        }
    }

    /// <summary>Solo el tipo y el status: el mensaje de Cosmos trae la partición (userId).</summary>
    internal static void Loguear(ILogger? log, Exception e)
    {
        if (e is TopeRecalculoException)
            log?.LogWarning("Récord sin recalcular: el ejercicio tiene más de {Tope} series", MaxFilasRecalculo);
        else
            log?.LogWarning("Récord sin mantener: {Tipo} {Status}", e.GetType().Name, (e as CosmosException)?.StatusCode);
    }

    /// <summary>Crea (sin ETag) o reemplaza si nadie lo cambió desde la lectura. false = otro escribió primero.</summary>
    static async Task<bool> EscribirAsync(Container series, string userId, RecordDoc doc, string? etag, CancellationToken ct = default)
    {
        try
        {
            if (etag is null) await series.CreateItemAsync(doc, new PartitionKey(userId), cancellationToken: ct);
            else await series.ReplaceItemAsync(doc, doc.Id, new PartitionKey(userId), new ItemRequestOptions { IfMatchEtag = etag }, ct);
            return true;
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict
                                            or HttpStatusCode.NotFound)
        {
            // 412: lo cambiaron; 409: otro lo creó; 404: otro lo borró. En todos, releer.
            return false;
        }
    }

    // Espera corta y al azar antes de reintentar: sin ella dos instancias vuelven a chocar en la misma vuelta.
    static Task EsperarReintento(int intento, CancellationToken ct = default) => Task.Delay(Random.Shared.Next(10, 40) * intento, ct);

    static string Marca(DateTimeOffset ahora) => ahora.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
