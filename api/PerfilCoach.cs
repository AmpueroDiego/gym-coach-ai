using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;

namespace GymCoach.Api;

/// <summary>
/// Lo que el coach sabe del usuario fuera de sus series: tiempo por sesión, objetivo, notas (dolores, gustos),
/// altura, peso corporal y equipo preferido.
/// Lo escribe el usuario (PUT /coach/perfil) o el coach con la herramienta recordar.
/// </summary>
public sealed record PerfilCoach(
    // Siempre se escriben (también como null): el contrato es number | null y string | null, no campo opcional.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? MinutosDisponibles,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Objetivo,
    IReadOnlyList<string> Notas,
    // Al final y opcionales: los new PerfilCoach(...) existentes siguen valiendo.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? AlturaCm = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? PesoCorporalKg = null,
    // "maquina" | "libre" | "mixto" | null (null: el coach lo deduce del historial).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Equipo = null,
    // "nunca" | "menos6m" | "mas6m" | null.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Experiencia = null,
    // 1..7 | null.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? DiasPorSemana = null)
{
    public static readonly PerfilCoach PorDefecto = new(null, null, []);

    // Notas es una lista: la igualdad del record la compararía por referencia.
    internal bool Igual(PerfilCoach otro) =>
        MinutosDisponibles == otro.MinutosDisponibles && Objetivo == otro.Objetivo && Notas.SequenceEqual(otro.Notas) &&
        AlturaCm == otro.AlturaCm && PesoCorporalKg == otro.PesoCorporalKg && Equipo == otro.Equipo &&
        Experiencia == otro.Experiencia && DiasPorSemana == otro.DiasPorSemana;
}

/// <summary>Documento en Cosmos, uno por usuario, en su partición. id fijo: nunca choca con un UUID de serie.</summary>
public sealed record PerfilCoachDoc(string Id, string UserId, string Tipo, int? MinutosDisponibles, string? Objetivo,
    IReadOnlyList<string> Notas, string Actualizado, int? AlturaCm, decimal? PesoCorporalKg, string? Equipo = null,
    string? Experiencia = null, int? DiasPorSemana = null)
{
    public const string IdDoc = "perfil-coach";

    public static PerfilCoachDoc De(PerfilCoach p, string userId, DateTimeOffset ahora) =>
        new(IdDoc, userId, "perfil-coach", p.MinutosDisponibles, p.Objetivo, p.Notas,
            ahora.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), p.AlturaCm, p.PesoCorporalKg,
            p.Equipo, p.Experiencia, p.DiasPorSemana);
}

internal delegate Task<(PerfilCoach Perfil, string? ETag)> LeerPerfil(string userId, CancellationToken ct);
/// <summary>etag null = crear. false = otro escribió primero (412/409/404).</summary>
internal delegate Task<bool> GuardarPerfil(string userId, PerfilCoach perfil, string? etag, CancellationToken ct);

/// <summary>
/// Lectura y escritura del perfil del coach. Escrituras condicionadas por ETag: el PUT del formulario y un
/// recordar del coach en paralelo no se pisan. Errores de Cosmos que no sean de concurrencia se propagan.
/// </summary>
public sealed class PerfilesCoach
{
    // Un reintento: los choques son raros (un usuario, dos escritores como mucho).
    const int MaxIntentos = 2;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly LeerPerfil _leer;
    readonly GuardarPerfil _guardar;

    public PerfilesCoach(Container series, TimeProvider reloj)
    {
        _leer = async (userId, ct) =>
        {
            // Por stream: 404 sin excepción y un doc corrupto no revienta.
            using var res = await series.ReadItemStreamAsync(PerfilCoachDoc.IdDoc, new PartitionKey(userId), cancellationToken: ct);
            if (res.StatusCode == HttpStatusCode.NotFound) return (PerfilCoach.PorDefecto, null);
            res.EnsureSuccessStatusCode();
            using var doc = await JsonDocument.ParseAsync(res.Content, cancellationToken: ct);
            // Se revalida al leer, campo por campo (el ETag se conserva para poder reemplazarlo).
            return (Validacion.PerfilGuardado(doc.RootElement), res.Headers.ETag);
        };
        _guardar = async (userId, perfil, etag, ct) =>
        {
            var doc = PerfilCoachDoc.De(perfil, userId, reloj.GetUtcNow());
            try
            {
                if (etag is null) await series.CreateItemAsync(doc, new PartitionKey(userId), cancellationToken: ct);
                else await series.ReplaceItemAsync(doc, doc.Id, new PartitionKey(userId),
                    new ItemRequestOptions { IfMatchEtag = etag }, ct);
                return true;
            }
            catch (CosmosException e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict
                                                or HttpStatusCode.NotFound)
            {
                // 412: lo cambiaron; 409: otro lo creó; 404: otro lo borró. En todos, releer.
                return false;
            }
        };
    }

    internal PerfilesCoach(LeerPerfil leer, GuardarPerfil guardar)
    {
        _leer = leer;
        _guardar = guardar;
    }

    /// <summary>Perfil del usuario; PorDefecto si no tiene (no se crea el doc al leer).</summary>
    public async Task<PerfilCoach> LeerAsync(string userId, CancellationToken ct = default) => (await _leer(userId, ct)).Perfil;

    /// <summary>Guarda el perfil completo, altura, peso, equipo, experiencia y días incluidos. false si otro escritor ganó dos veces seguidas.</summary>
    public async Task<bool> GuardarAsync(string userId, PerfilCoach perfil, CancellationToken ct = default) =>
        await GuardarAsync(userId, perfil, traeAltura: true, traePeso: true, traeEquipo: true, traeExperiencia: true, traeDias: true,
            ct) is not null;

    /// <summary>
    /// PUT: altura, peso, equipo, experiencia o días sin su clave en el body conservan lo guardado (una PWA vieja no borra lo que guardó
    /// el coach); null explícito borra. Devuelve el perfil que quedó guardado, o null si otro escritor ganó dos veces seguidas.
    /// </summary>
    public async Task<PerfilCoach?> GuardarAsync(string userId, PerfilCoach perfil, bool traeAltura, bool traePeso, bool traeEquipo,
        bool traeExperiencia = false, bool traeDias = false, CancellationToken ct = default)
    {
        // Leer el ETag (1 RU) y reemplazar condicionado, en vez de upsert: así un recordar en curso falla y relee.
        for (var i = 0; i < MaxIntentos; i++)
        {
            var (actual, etag) = await _leer(userId, ct);
            // El merge va dentro del bucle: con cada intento se completa con lo releído, bajo el mismo ETag.
            var final = perfil with
            {
                AlturaCm = traeAltura ? perfil.AlturaCm : actual.AlturaCm,
                PesoCorporalKg = traePeso ? perfil.PesoCorporalKg : actual.PesoCorporalKg,
                Equipo = traeEquipo ? perfil.Equipo : actual.Equipo,
                Experiencia = traeExperiencia ? perfil.Experiencia : actual.Experiencia,
                DiasPorSemana = traeDias ? perfil.DiasPorSemana : actual.DiasPorSemana,
            };
            if (await _guardar(userId, final, etag, ct)) return final;
        }
        return null;
    }

    /// <summary>
    /// Herramienta recordar: aplica la acción sobre el perfil guardado. Json va al modelo; Nuevo solo si cambió algo.
    /// userId lo pone el backend (del token): un userId en los argumentos se ignora.
    /// </summary>
    public async Task<(string Json, PerfilCoach? Nuevo)> RecordarAsync(string userId, string argumentosJson, CancellationToken ct = default)
    {
        for (var i = 0; i < MaxIntentos; i++)
        {
            // Con cada intento se relee y se vuelve a aplicar: reusar el primer resultado borraría lo que escribió el otro.
            var (actual, etag) = await _leer(userId, ct);
            var (nuevo, error) = Aplicar(actual, argumentosJson);
            if (error is not null) return (JsonSerializer.Serialize(new { error }, Json), null);
            if (nuevo!.Igual(actual)) return (Ok(actual), null);
            if (await _guardar(userId, nuevo, etag, ct)) return (Ok(nuevo), nuevo);
        }
        return ("""{"error":"no_disponible"}""", null);
    }

    static string Ok(PerfilCoach perfil) => JsonSerializer.Serialize(new { ok = true, perfil }, Json);

    /// <summary>
    /// Aplica una acción de recordar. Error: notas_llenas, indice_invalido, parametros_invalidos o accion_desconocida.
    /// Mismas reglas por campo que el PUT (Validacion.ErrorTextoPerfil / ErrorMinutosPerfil / ErrorAlturaPerfil / ErrorPesoCorporal /
    /// ErrorEquipoPerfil / ErrorExperienciaPerfil / ErrorDiasPerfil).
    /// </summary>
    public static (PerfilCoach? Nuevo, string? Error) Aplicar(PerfilCoach actual, string argumentosJson)
    {
        // GetString lanza InvalidOperationException con un surrogate suelto: son argumentos malos, no un 500.
        try
        {
            using var doc = JsonDocument.Parse(argumentosJson, Validacion.OpcionesJson);
            return AplicarInterna(actual, doc.RootElement);
        }
        catch (JsonException) { return (null, "parametros_invalidos"); }
        catch (InvalidOperationException) { return (null, "parametros_invalidos"); }
    }

    static (PerfilCoach? Nuevo, string? Error) AplicarInterna(PerfilCoach actual, JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty("accion", out var a) || a.ValueKind != JsonValueKind.String)
            return (null, "parametros_invalidos");

        switch (a.GetString())
        {
            case "agregar_nota":
                if (!args.TryGetProperty("nota", out var n) || Validacion.ErrorTextoPerfil(n, out var nota) is not null)
                    return (null, "parametros_invalidos");
                // Repetida (recortada, sin distinguir mayúsculas): sin cambio, RecordarAsync no escribe. El fallback del
                // agente al bucle puede repetir la misma llamada.
                if (actual.Notas.Any(x => string.Equals(x.Trim(), nota, StringComparison.OrdinalIgnoreCase))) return (actual, null);
                if (actual.Notas.Count >= Validacion.MaxNotasPerfil) return (null, "notas_llenas");
                return (actual with { Notas = [.. actual.Notas, nota] }, null);

            case "quitar_nota":
                // 1-based, como se numeran en el prompt.
                if (!args.TryGetProperty("indice", out var ix) || ix.ValueKind != JsonValueKind.Number || !ix.TryGetInt32(out var indice))
                    return (null, "parametros_invalidos");
                if (indice < 1 || indice > actual.Notas.Count) return (null, "indice_invalido");
                return (actual with { Notas = [.. actual.Notas.Where((_, i) => i != indice - 1)] }, null);

            case "fijar_minutos":
                if (Validacion.ErrorMinutosPerfil(args, "minutos", out var minutos) is not null) return (null, "parametros_invalidos");
                return (actual with { MinutosDisponibles = minutos }, null);

            case "fijar_objetivo":
                if (!args.TryGetProperty("objetivo", out var o) || o.ValueKind != JsonValueKind.String) return (null, "parametros_invalidos");
                // "" (o solo espacios) borra el objetivo.
                if (o.GetString()!.Trim().Length == 0) return (actual with { Objetivo = null }, null);
                if (Validacion.ErrorTextoPerfil(o, out var objetivo) is not null) return (null, "parametros_invalidos");
                return (actual with { Objetivo = objetivo }, null);

            // null borra; el campo tiene que venir (ausente es un argumento mal armado, no un borrado).
            case "fijar_altura":
                if (!args.TryGetProperty("alturaCm", out var al)) return (null, "parametros_invalidos");
                if (al.ValueKind == JsonValueKind.Null) return (actual with { AlturaCm = null }, null);
                if (Validacion.ErrorAlturaPerfil(args, "alturaCm", out var altura) is not null) return (null, "parametros_invalidos");
                return (actual with { AlturaCm = altura }, null);

            case "fijar_peso":
                if (!args.TryGetProperty("pesoCorporalKg", out var pc)) return (null, "parametros_invalidos");
                if (pc.ValueKind == JsonValueKind.Null) return (actual with { PesoCorporalKg = null }, null);
                if (Validacion.ErrorPesoCorporal(pc, out var peso) is not null) return (null, "parametros_invalidos");
                return (actual with { PesoCorporalKg = peso }, null);

            case "fijar_equipo":
                if (!args.TryGetProperty("equipo", out var eq) || Validacion.ErrorEquipoPerfil(eq, out var equipo) is not null)
                    return (null, "parametros_invalidos");
                return (actual with { Equipo = equipo }, null);

            case "fijar_experiencia":
                if (!args.TryGetProperty("experiencia", out var ex) || Validacion.ErrorExperienciaPerfil(ex, out var experiencia) is not null)
                    return (null, "parametros_invalidos");
                return (actual with { Experiencia = experiencia }, null);

            case "fijar_dias":
                if (!args.TryGetProperty("diasPorSemana", out var di)) return (null, "parametros_invalidos");
                if (di.ValueKind == JsonValueKind.Null) return (actual with { DiasPorSemana = null }, null);
                if (Validacion.ErrorDiasPerfil(args, "diasPorSemana", out var dias) is not null) return (null, "parametros_invalidos");
                return (actual with { DiasPorSemana = dias }, null);

            default:
                return (null, "accion_desconocida");
        }
    }
}
