using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace GymCoach.Api;

/// <summary>
/// Conversación de hoy del usuario con el agente, una por usuario, en su partición. id y tipo fijos: nunca choca con
/// un UUID de serie y las consultas de series/rutinas/records filtran por tipo. Dia: yyyy-MM-dd; CreadaEl: ISO Z.
/// ConversacionId null = invalidada (se renueva). Pendientes: conversaciones viejas que no se terminaron de borrar en
/// Foundry (traen DATOS); se reintentan al renovar o invalidar. Como mucho MaxPendientes, las más nuevas al final.
/// </summary>
public sealed record ConversacionAgenteDoc(string Id, string UserId, string Tipo,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ConversacionId, string Dia, string CreadaEl,
    IReadOnlyList<string>? Pendientes = null)
{
    public const string IdDoc = "coach-conversacion";
    public const string TipoDoc = "coach-conversacion";
    public const int MaxPendientes = 5;
}

/// <summary>Doc null con ETag = existe pero está corrupto (se reemplaza al renovar).</summary>
internal delegate Task<(ConversacionAgenteDoc? Doc, string? ETag)> LeerConversacionAgente(string userId, CancellationToken ct);
/// <summary>etag null = crear. false = otro escribió primero (409/412/404).</summary>
internal delegate Task<bool> GuardarConversacionAgente(ConversacionAgenteDoc doc, string? etag, CancellationToken ct);
/// <summary>Delete con IfMatch: false si otro escribió o ya no estaba (412/404).</summary>
internal delegate Task<bool> BorrarConversacionAgente(string userId, string etag, CancellationToken ct);

/// <summary>
/// Qué conversación del agente usa cada usuario hoy. El conversacionId sale solo de aquí (doc del userId del token):
/// nunca del body, nunca en la respuesta HTTP ni en logs.
/// </summary>
public sealed class ConversacionesCoach
{
    // Un reintento: los choques son raros (dos mensajes del mismo usuario a la vez).
    const int MaxIntentos = 2;
    // Borrar en Foundry es best-effort: con tope propio total (no el del turno), para no comerse el tiempo del turno.
    static readonly TimeSpan TiempoBorrado = TimeSpan.FromSeconds(5);

    readonly LeerConversacionAgente _leer;
    readonly GuardarConversacionAgente _guardar;
    readonly BorrarConversacionAgente _borrar;
    readonly IConversacionesAgente _agente;
    readonly TimeProvider _reloj;
    readonly ILogger? _log;

    public ConversacionesCoach(Container series, IConversacionesAgente agente, TimeProvider reloj, ILogger<ConversacionesCoach>? log = null)
    {
        _leer = async (userId, ct) =>
        {
            // Por stream: 404 sin excepción y un doc corrupto no revienta.
            using var res = await series.ReadItemStreamAsync(ConversacionAgenteDoc.IdDoc, new PartitionKey(userId), cancellationToken: ct);
            if (res.StatusCode == HttpStatusCode.NotFound) return (null, null);
            res.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(res.Content, cancellationToken: ct);
            return (Leido(doc.RootElement, userId), res.Headers.ETag);
        };
        _guardar = async (doc, etag, ct) =>
        {
            try
            {
                if (etag is null) await series.CreateItemAsync(doc, new PartitionKey(doc.UserId), cancellationToken: ct);
                else await series.ReplaceItemAsync(doc, doc.Id, new PartitionKey(doc.UserId),
                    new ItemRequestOptions { IfMatchEtag = etag, EnableContentResponseOnWrite = false }, ct);
                return true;
            }
            catch (CosmosException e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict
                                                or HttpStatusCode.NotFound)
            {
                // 412: lo cambiaron; 409: otro lo creó; 404: otro lo borró.
                return false;
            }
        };
        _borrar = async (userId, etag, ct) =>
        {
            try
            {
                await series.DeleteItemAsync<object>(ConversacionAgenteDoc.IdDoc, new PartitionKey(userId),
                    new ItemRequestOptions { IfMatchEtag = etag }, ct);
                return true;
            }
            catch (CosmosException e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
            {
                return false;
            }
        };
        _agente = agente;
        _reloj = reloj;
        _log = log;
    }

    internal ConversacionesCoach(LeerConversacionAgente leer, GuardarConversacionAgente guardar, BorrarConversacionAgente borrar,
        IConversacionesAgente agente, TimeProvider reloj, ILogger? log = null)
    {
        _leer = leer;
        _guardar = guardar;
        _borrar = borrar;
        _agente = agente;
        _reloj = reloj;
        _log = log;
    }

    /// <summary>
    /// Conversación de hoy del usuario. Renueva si no hay doc (o está invalidado), si es de otro día o si nueva (la PWA empezó
    /// una conversación): crea una en Foundry, borra la anterior y los pendientes (best-effort, 5 s en total) y guarda el doc
    /// bajo ETag con lo que no se pudo borrar. Si otro mensaje del mismo usuario ganó la carrera, se borra la propia y se usa
    /// la ganadora: nunca queda una conversación huérfana con datos.
    /// </summary>
    public async Task<string> ObtenerAsync(string userId, DateOnly hoy, bool nueva, CancellationToken ct)
    {
        var dia = hoy.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var (actual, etag) = await _leer(userId, ct);
        if (actual is { ConversacionId: { } vigente } && actual.Dia == dia && !nueva) return vigente;

        var id = await _agente.CrearAsync(ct);
        try
        {
            // Se borran antes de guardar: así el doc se escribe una sola vez, solo con lo que quedó.
            var quedan = await BorrarEnFoundryAsync(Viejas(actual));
            for (var i = 0; i < MaxIntentos; i++)
            {
                if (await _guardar(Doc(userId, id, dia, quedan), etag, ct)) return id;
                // Perdió la carrera: si el ganador ya dejó la de hoy, se usa esa (la propia está vacía, se borra).
                (var ganador, etag) = await _leer(userId, ct);
                if (ganador is { ConversacionId: { } suya } && ganador.Dia == dia)
                {
                    await BorrarEnFoundryAsync([id]);
                    return suya;
                }
                // Lo del ganador (otro día o invalidado) no se pierde: pasa a pendientes.
                quedan = [.. quedan, .. Viejas(ganador)];
            }
        }
        catch
        {
            // Cosmos falló o se canceló el turno: la recién creada no queda apuntada por nadie.
            await BorrarEnFoundryAsync([id]);
            throw;
        }
        await BorrarEnFoundryAsync([id]);
        throw new ModeloNoDisponibleException("No se pudo guardar la conversación del agente.");
    }

    /// <summary>
    /// Para la conversación que quedó rota (404, o un function_call sin su output): si el doc sigue apuntando a ella, queda
    /// invalidado (el próximo turno renueva); la borra en Foundry junto con los pendientes. Lo que no se pudo borrar queda en
    /// pendientes. Best-effort: nunca lanza salvo cancelación de ct.
    /// </summary>
    public async Task InvalidarAsync(string userId, string? conversacionId, CancellationToken ct)
    {
        // Sin id no se llegó a usar ninguna conversación: nada que deshacer.
        if (conversacionId is null) return;
        ConversacionAgenteDoc? actual = null;
        string? etag = null;
        var leido = false;
        try
        {
            (actual, etag) = await _leer(userId, ct);
            leido = true;
        }
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            // Solo el tipo: el mensaje de Cosmos trae la partición (userId).
            _log?.LogWarning("Coach: no se pudo leer la conversación del agente al invalidar ({Tipo}).", e.GetType().Name);
        }

        var quedan = await BorrarEnFoundryAsync([.. actual?.Pendientes ?? [], conversacionId]);
        // Sin leer el doc no se escribe: si sigue apuntando a la rota, el próximo turno vuelve a fallar e invalidar.
        if (!leido) return;
        try
        {
            // Si el doc ya apunta a otra (más nueva), esa se conserva; si no, queda invalidado.
            var otra = actual is { ConversacionId: { } c } && c != conversacionId ? actual : null;
            if (etag is null)
            {
                if (quedan.Count > 0) await _guardar(Doc(userId, null, "", quedan), null, ct);
            }
            else if (otra is null && quedan.Count == 0) await _borrar(userId, etag, ct);
            else if (otra is null) await _guardar(Doc(userId, null, "", quedan), etag, ct);
            else if (!(otra.Pendientes ?? []).SequenceEqual(Ultimas(quedan))) await _guardar(otra with { Pendientes = Ultimas(quedan) }, etag, ct);
        }
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            _log?.LogWarning("Coach: no se pudo invalidar la conversación del agente ({Tipo}).", e.GetType().Name);
        }
    }

    ConversacionAgenteDoc Doc(string userId, string? conversacionId, string dia, IReadOnlyList<string> pendientes) =>
        new(ConversacionAgenteDoc.IdDoc, userId, ConversacionAgenteDoc.TipoDoc, conversacionId, dia,
            _reloj.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), Ultimas(pendientes));

    // Pendientes del doc y su conversación, de la más vieja a la más nueva.
    static List<string> Viejas(ConversacionAgenteDoc? doc) =>
        doc is null ? [] : [.. doc.Pendientes ?? [], .. doc.ConversacionId is { } c ? [c] : Array.Empty<string>()];

    // Sin repetidas y solo las MaxPendientes más nuevas: el doc no crece sin límite.
    static List<string> Ultimas(IEnumerable<string> ids)
    {
        var distintas = ids.Reverse().Distinct(StringComparer.Ordinal).Take(ConversacionAgenteDoc.MaxPendientes).ToList();
        distintas.Reverse();
        return distintas;
    }

    /// <summary>
    /// Borra en paralelo, con un tope propio total y sin el token del turno (un cliente que corta no deja datos en Foundry);
    /// devuelve las que no se terminaron de borrar. Nunca lanza.
    /// </summary>
    async Task<List<string>> BorrarEnFoundryAsync(IReadOnlyList<string> ids)
    {
        if (ids.Count == 0) return [];
        using var cts = new CancellationTokenSource(TiempoBorrado);
        var resultados = await Task.WhenAll(ids.Select(async id =>
        {
            try { return await _agente.BorrarAsync(id, cts.Token); }
            catch (Exception e)
            {
                _log?.LogWarning("Coach: no se pudo borrar una conversación del agente ({Tipo}).", e.GetType().Name);
                return false;
            }
        }));
        return [.. ids.Where((_, i) => !resultados[i])];
    }

    // Se revalida al leer: un doc editado a mano o de otro esquema cuenta como corrupto (se renueva).
    static ConversacionAgenteDoc? Leido(JsonElement raiz, string userId)
    {
        if (raiz.ValueKind != JsonValueKind.Object) return null;
        string? Texto(string nombre) =>
            raiz.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        // conversacionId: texto no vacío, o null (invalidado). Ausente o de otro tipo = corrupto.
        if (!raiz.TryGetProperty("conversacionId", out var cid) ||
            !(cid.ValueKind == JsonValueKind.Null || cid.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cid.GetString())))
            return null;
        var dia = Texto("dia");
        if (dia is null || Texto("userId") != userId) return null;
        // Los pendientes son best-effort: lo que no sea texto se descarta.
        List<string> pendientes = raiz.TryGetProperty("pendientes", out var p) && p.ValueKind == JsonValueKind.Array
            ? [.. p.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()))
                .Select(x => x.GetString()!)]
            : [];
        return new(ConversacionAgenteDoc.IdDoc, userId, ConversacionAgenteDoc.TipoDoc, cid.ValueKind == JsonValueKind.String ? cid.GetString() : null,
            dia, Texto("creadaEl") ?? "", Ultimas(pendientes));
    }
}
