using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Azure.Cosmos;

namespace GymCoach.Api;

/// <summary>
/// Lee el doc por stream y lo revalida. Doc null con ETag: existe pero está corrupto o es de otro esquema
/// (se recalcula y se reemplaza con ese ETag). (null, null): no existe.
/// </summary>
internal delegate Task<(PerfilEntrenamientoDoc? Doc, string? ETag)> LeerPerfilEntrenamiento(string userId, CancellationToken ct);
/// <summary>Series del usuario para el cálculo: TOP 3001 ORDER BY c.ts DESC (3001 filas = truncado).</summary>
internal delegate Task<IReadOnlyList<Serie>> ConsultarSeriesPerfil(string userId, CancellationToken ct);
/// <summary>etag null = crear. false = otro escribió primero (412/409/404).</summary>
internal delegate Task<bool> GuardarPerfilEntrenamiento(PerfilEntrenamientoDoc doc, string? etag, CancellationToken ct);
/// <summary>Patch de desactualizado = true. Puede lanzar CosmosException (404, 412 y el resto los decide el llamador).</summary>
internal delegate Task MarcarPerfilEntrenamiento(string userId, CancellationToken ct);

/// <summary>
/// Perfil de entrenamiento vigente de cada usuario. Se recalcula al leer si se calculó con otro hoy, si quedó
/// desactualizado (Importar) o si cambió CalculosPerfil.Version; cada recálculo gasta un cupo de Recalculos.
/// </summary>
public sealed class PerfilesEntrenamiento
{
    // Estimado con la regla de Records (~3.5 RU + ~0.036 por fila): ~33-40 RU con 806 series, ~115 RU con el tope.
    // Falta medirlo en el emulador (ImportarIntegracionTests.Medir_CostoDelPerfilDeEntrenamiento) y anotarlo aquí.
    public const int MaxSeries = 3000;

    // Al leer, un null en un campo no anulable o un parámetro que falta es un doc corrupto, no un perfil a medias.
    // Web lee números desde strings: aquí "60" no es 60.
    static readonly JsonSerializerOptions JsonLectura = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    readonly LeerPerfilEntrenamiento _leer;
    readonly ConsultarSeriesPerfil _consultar;
    readonly GuardarPerfilEntrenamiento _guardar;
    readonly MarcarPerfilEntrenamiento _marcar;
    // false = sin cupo de recálculo para ese userId.
    readonly Func<string, bool> _permitirRecalculo;
    readonly TimeProvider _reloj;

    public PerfilesEntrenamiento(Container series, Limites limites, TimeProvider reloj)
    {
        _leer = async (userId, ct) =>
        {
            // Por stream: 404 sin excepción y un doc corrupto no revienta.
            using var res = await series.ReadItemStreamAsync(PerfilEntrenamientoDoc.IdDoc, new PartitionKey(userId), cancellationToken: ct);
            if (res.StatusCode == HttpStatusCode.NotFound) return (null, null);
            res.EnsureSuccessStatusCode();
            using var reader = new StreamReader(res.Content);
            return (Interpretar(await reader.ReadToEndAsync(ct), userId), res.Headers.ETag);
        };
        _consultar = async (userId, ct) =>
        {
            // Sin calentamientos desde la consulta: el tope cuenta solo series que entran al cálculo.
            // Las series viejas no tienen tipoSerie, y un != solo las dejaría fuera.
            var consulta = new QueryDefinition(
                    "SELECT TOP @tope c.id, c.ejercicioId, c.pesoKg, c.reps, c.fecha, c.ts, c.rutina, c.tipoSerie FROM c " +
                    "WHERE c.userId = @userId AND c.tipo = 'serie' AND " +
                    "(NOT IS_DEFINED(c.tipoSerie) OR c.tipoSerie != 'calentamiento') ORDER BY c.ts DESC")
                .WithParameter("@tope", MaxSeries + 1)
                .WithParameter("@userId", userId);
            var filas = new List<Serie>();
            using var it = series.GetItemQueryIterator<Serie>(consulta,
                requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(userId) });
            while (it.HasMoreResults)
                filas.AddRange(await it.ReadNextAsync(ct));
            return filas;
        };
        _guardar = async (doc, etag, ct) =>
        {
            try
            {
                if (etag is null) await series.CreateItemAsync(doc, new PartitionKey(doc.UserId), cancellationToken: ct);
                else await series.ReplaceItemAsync(doc, doc.Id, new PartitionKey(doc.UserId),
                    new ItemRequestOptions { IfMatchEtag = etag }, ct);
                return true;
            }
            catch (CosmosException e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict
                                                or HttpStatusCode.NotFound)
            {
                // 412: lo cambiaron (p. ej. lo marcó Importar); 409: otro lo creó; 404: otro lo borró.
                return false;
            }
        };
        _marcar = (userId, ct) =>
            // El filtro hace que un doc ya marcado dé 412 sin escribir (~1-2 RU en vez de ~10-15).
            series.PatchItemAsync<object>(PerfilEntrenamientoDoc.IdDoc, new PartitionKey(userId),
                [PatchOperation.Set("/desactualizado", true)],
                new PatchItemRequestOptions
                {
                    FilterPredicate = "FROM c WHERE NOT IS_DEFINED(c.desactualizado)",
                    EnableContentResponseOnWrite = false,
                }, ct);
        _permitirRecalculo = userId => limites.Recalculos.Permitir(userId, out _);
        _reloj = reloj;
    }

    internal PerfilesEntrenamiento(LeerPerfilEntrenamiento leer, ConsultarSeriesPerfil consultar,
        GuardarPerfilEntrenamiento guardar, MarcarPerfilEntrenamiento marcar, Func<string, bool> permitirRecalculo,
        TimeProvider reloj)
    {
        _leer = leer;
        _consultar = consultar;
        _guardar = guardar;
        _marcar = marcar;
        _permitirRecalculo = permitirRecalculo;
        _reloj = reloj;
    }

    /// <summary>
    /// Perfil vigente; lo recalcula y guarda si hace falta. offsetMin null usa el guardado.
    /// null: hacía falta recalcular, no hubo cupo y no hay doc viejo (el GET responde 429).
    /// Sin cupo, o con Cosmos saturado al consultar, devuelve el viejo si lo hay. Un 412/409/404 o un error transitorio
    /// al guardar devuelve lo calculado (el siguiente read recalcula). El resto de errores de Cosmos se propaga.
    /// </summary>
    public async Task<PerfilEntrenamiento?> ObtenerAsync(string userId, DateOnly hoy, int? offsetMin, CancellationToken ct = default)
    {
        var (doc, etag) = await _leer(userId, ct);
        if (doc is not null && Vigente(doc, hoy, offsetMin)) return doc.Perfil;

        // Un doc viejo (de otro día o marcado) es mejor que nada; uno corrupto ya llega como null y no se usa nunca.
        if (!_permitirRecalculo(userId)) return doc?.Perfil;

        IReadOnlyList<Serie> filas;
        try { filas = await _consultar(userId, ct); }
        catch (CosmosException e) when (doc is not null && Transitorio(e)) { return doc.Perfil; }

        var offset = offsetMin ?? doc?.OffsetMin;
        // Más recientes primero: si se pasó del tope, se pierden las más viejas.
        var recientes = filas.OrderByDescending(s => s.Ts, StringComparer.Ordinal).Take(MaxSeries);
        var perfil = CalculosPerfil.Calcular(recientes, hoy, offset, _reloj.GetUtcNow(), filas.Count > MaxSeries);

        // Sin reintento: si otro escribió (Importar lo marcó), su doc gana y el siguiente read recalcula.
        try { await _guardar(new(PerfilEntrenamientoDoc.IdDoc, userId, PerfilEntrenamientoDoc.TipoDoc, offset, perfil), etag, ct); }
        catch (CosmosException e) when (Transitorio(e)) { }
        return perfil;
    }

    /// <summary>Marca el doc para recalcular en la próxima lectura. 404 (sin doc) y 412 (ya marcado) se ignoran.</summary>
    public async Task MarcarDesactualizadoAsync(string userId, CancellationToken ct = default)
    {
        try { await _marcar(userId, ct); }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed) { }
    }

    /// <summary>
    /// Sin marca, de esta versión, calculado con este hoy o con mañana (otro dispositivo adelantado a UTC: aceptarlo evita
    /// recalcular alternado cerca de medianoche; el día nuevo de un cliente sí recalcula) y con el offset del request, si
    /// trae uno (un horario null por falta de offset no espera al día siguiente).
    /// </summary>
    static bool Vigente(PerfilEntrenamientoDoc doc, DateOnly hoy, int? offsetMin) =>
        !doc.Desactualizado && doc.Perfil.Version == CalculosPerfil.Version &&
        DateOnly.TryParseExact(doc.Perfil.Hoy, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var h) &&
        h >= hoy && h <= hoy.AddDays(1) &&
        (offsetMin is null || offsetMin == doc.OffsetMin);

    /// <summary>
    /// Doc revalidado, o null si está corrupto o es de otro esquema/usuario. Los elementos de las listas los revisa a mano:
    /// RespectNullableAnnotations no mira dentro de las colecciones.
    /// </summary>
    internal static PerfilEntrenamientoDoc? Interpretar(string json, string userId)
    {
        try
        {
            var d = JsonSerializer.Deserialize<PerfilEntrenamientoDoc>(json, JsonLectura);
            if (d is null || d.Id != PerfilEntrenamientoDoc.IdDoc || d.Tipo != PerfilEntrenamientoDoc.TipoDoc || d.UserId != userId ||
                d.OffsetMin is < CalculosPerfil.MinOffsetMin or > CalculosPerfil.MaxOffsetMin)
                return null;
            var p = d.Perfil;
            if (!p.Principales.All(x => x is not null) || !p.Tendencias.All(x => x is not null) ||
                !p.Ejercicios.All(x => x is not null))
                return null;
            if (p.Habitos is { } h && (!h.Division.All(x => x is not null && x.Grupos.All(g => g is not null)) ||
                                       !h.RutinasFrecuentes.All(x => x is not null)))
                return null;
            return d;
        }
        catch (JsonException) { return null; }
    }

    // Base saturada o lenta.
    static bool Transitorio(CosmosException e) =>
        e.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.RequestTimeout;
}
