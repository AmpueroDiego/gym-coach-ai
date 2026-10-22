using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;

namespace GymCoach.Api;

/// <summary>Rutina guardada en el plan: la misma forma que la rutina de POST /coach, con los pesos ya filtrados.</summary>
public sealed record RutinaPlan(string Id, string Nombre, IReadOnlyList<EjercicioRutina> Ejercicios, string Actualizada,
    int MinutosEstimados);

/// <summary>Una generación del plan. Creado: ISO Z.</summary>
public sealed record VariacionPlan(string Texto, IReadOnlyList<string> Herramientas,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] RutinaPlan? Rutina,
    string Creado);

/// <summary>
/// Documento en Cosmos, uno por usuario y día. GenerandoHasta (ISO Z): lease de la generación en curso; vencido o
/// null = libre. Variaciones: como mucho MaxGeneraciones, la actual es la última. Idioma: el de la última generación
/// (docs viejos sin campo = "es"); RegeneradoPorIdioma: ya se regeneró hoy por cambio de idioma (una vez por día).
/// </summary>
public sealed record PlanDiaDoc(string Id, string UserId, string Tipo, string Fecha,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? GenerandoHasta,
    IReadOnlyList<VariacionPlan> Variaciones, string Actualizado, string? Idioma = null, bool RegeneradoPorIdioma = false)
{
    public const string Prefijo = "plan-dia:";
    public const string TipoDoc = "plan-dia";

    public static string IdDoc(DateOnly fecha) => Prefijo + fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

/// <summary>
/// Respuesta de GET y POST /coach/plan-dia. Actual: la última variación, o null sin plan. Numero: variaciones
/// hechas; Restantes: las que quedan de MaxGeneraciones. Generando: hay un lease vivo.
/// </summary>
public sealed record PlanDiaRespuesta(string Fecha,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] VariacionPlan? Actual,
    int Numero, int Restantes, bool Generando);

/// <summary>Cómo terminó el POST: Listo = 200 (Plan), TopeVariaciones = 409, SinCupo = 429, NoDisponible = 503.</summary>
public enum EstadoPlanDia { Listo, TopeVariaciones, SinCupo, NoDisponible }

/// <summary>Plan solo en Listo; ReintentarEn solo en SinCupo.</summary>
public sealed record ResultadoPlanDia(EstadoPlanDia Estado, PlanDiaRespuesta? Plan = null, TimeSpan ReintentarEn = default);

/// <summary>Plan de hoy como DATOS del prompt: nombre de cada rutina y nombres de catálogo de sus ejercicios.</summary>
public sealed record PlanHoyParaPrompt(IReadOnlyList<VariacionParaPrompt> Variaciones);

/// <summary>Nombre null si esa variación no trajo rutina.</summary>
public sealed record VariacionParaPrompt(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Nombre,
    IReadOnlyList<string> Nombres);

internal delegate Task<(PlanDiaDoc? Doc, string? ETag)> LeerPlanDia(string userId, string id, CancellationToken ct);
/// <summary>ETag del doc creado, o null si ya existía (409).</summary>
internal delegate Task<string?> CrearPlanDia(PlanDiaDoc doc, CancellationToken ct);
/// <summary>Replace con IfMatch: ETag nuevo, o null si otro escribió o lo borró (412/404).</summary>
internal delegate Task<string?> ReemplazarPlanDia(PlanDiaDoc doc, string etag, CancellationToken ct);
/// <summary>Delete con IfMatch: false si otro escribió o ya no estaba (412/404).</summary>
internal delegate Task<bool> BorrarPlanDia(string userId, string id, string etag, CancellationToken ct);

/// <summary>
/// Plan del día guardado: uno por usuario y día, el mismo en todos los dispositivos. La generación toma un lease
/// bajo ETag para que dos POST no llamen dos veces al modelo. Los cupos (Coach y CoachDiario) se piden y liberan aquí.
/// </summary>
public sealed class PlanesDia
{
    // La original más 2 "Otra opción"; se cuenta en el doc, así vale entre dispositivos e instancias.
    public const int MaxGeneraciones = 3;
    // Más que el tope del turno (38 s): si la función muere, vence solo.
    public static readonly TimeSpan Lease = TimeSpan.FromSeconds(50);
    // El mismo tope que POST /coach: SWA corta a los 45 s y la PWA espera 45 s.
    static readonly TimeSpan TiempoMaximo = TimeSpan.FromSeconds(38);
    // Tope propio de cada escritura que no puede depender del token del turno (guardar el resultado, soltar el lease).
    static readonly TimeSpan TiempoEscritura = TimeSpan.FromSeconds(8);
    static readonly TimeSpan EsperaReintento = TimeSpan.FromMilliseconds(200);
    // Los arma el servidor: el cliente solo dice si quiere otra opción.
    public const string MensajePlan = "Tu entreno de hoy";
    public const string MensajeOtraOpcion = "Otra opción para hoy, distinta de las anteriores";
    // Cambio de idioma: el mismo plan, traducido. Pide proponer_rutina explícito para no caer en "descríbelo sin armar otro" (regla 11).
    public const string MensajeTraducirEn =
        "Repite este mismo plan de hoy (mismos ejercicios, series y orden) en inglés: llama proponer_rutina con los ejercicios del plan de hoy en DATOS.";
    public const string MensajeTraducirEs =
        "Repite este mismo plan de hoy (mismos ejercicios, series y orden) en español: llama proponer_rutina con los ejercicios del plan de hoy en DATOS.";
    const string FormatoTs = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly LeerPlanDia _leer;
    readonly CrearPlanDia _crear;
    readonly ReemplazarPlanDia _reemplazar;
    readonly BorrarPlanDia _borrar;
    readonly ConversacionCoach _conversacion;
    readonly PerfilesCoach _perfiles;
    readonly Func<string, DateOnly, int?, CancellationToken, Task<PerfilEntrenamiento?>> _entrenamiento;
    readonly Limites _limites;
    readonly TimeProvider _reloj;
    readonly ILogger<PlanesDia>? _log;

    public PlanesDia(Container series, Limites limites, TimeProvider reloj, ConversacionCoach conversacion,
        PerfilesCoach perfiles, PerfilesEntrenamiento perfilesEntrenamiento, ILogger<PlanesDia>? log = null)
        : this(
            async (userId, id, ct) =>
            {
                // Por stream: 404 sin excepción y un doc corrupto no revienta.
                using var res = await series.ReadItemStreamAsync(id, new PartitionKey(userId), cancellationToken: ct);
                if (res.StatusCode == HttpStatusCode.NotFound) return (null, null);
                res.EnsureSuccessStatusCode();
                return (await DocValidoAsync(res.Content, id, userId, ct), res.Headers.ETag);
            },
            async (doc, ct) =>
            {
                try
                {
                    return (await series.CreateItemAsync(doc, new PartitionKey(doc.UserId),
                        new ItemRequestOptions { EnableContentResponseOnWrite = false }, ct)).ETag;
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict) { return null; }
            },
            async (doc, etag, ct) =>
            {
                try
                {
                    return (await series.ReplaceItemAsync(doc, doc.Id, new PartitionKey(doc.UserId),
                        new ItemRequestOptions { IfMatchEtag = etag, EnableContentResponseOnWrite = false }, ct)).ETag;
                }
                catch (CosmosException e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
                {
                    return null;
                }
            },
            async (userId, id, etag, ct) =>
            {
                using var res = await series.DeleteItemStreamAsync(id, new PartitionKey(userId),
                    new ItemRequestOptions { IfMatchEtag = etag }, ct);
                if (res.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound) return false;
                res.EnsureSuccessStatusCode();
                return true;
            },
            conversacion, perfiles, perfilesEntrenamiento, limites, reloj, log: log)
    {
    }

    /// <summary>leerEntrenamiento: para probar sin el cálculo real del perfil; null usa perfilesEntrenamiento.</summary>
    internal PlanesDia(LeerPlanDia leer, CrearPlanDia crear, ReemplazarPlanDia reemplazar, BorrarPlanDia borrar,
        ConversacionCoach conversacion, PerfilesCoach perfiles, PerfilesEntrenamiento perfilesEntrenamiento,
        Limites limites, TimeProvider reloj,
        Func<string, DateOnly, int?, CancellationToken, Task<PerfilEntrenamiento?>>? leerEntrenamiento = null,
        ILogger<PlanesDia>? log = null)
    {
        _leer = leer;
        _crear = crear;
        _reemplazar = reemplazar;
        _borrar = borrar;
        _conversacion = conversacion;
        _perfiles = perfiles;
        _entrenamiento = leerEntrenamiento ?? perfilesEntrenamiento.ObtenerAsync;
        _limites = limites;
        _reloj = reloj;
        _log = log;
    }

    /// <summary>Doc del día (1 RU), o null si no hay. Lo usan el GET y el turno del coach (ctx.PlanHoy).</summary>
    public async Task<PlanDiaDoc?> LeerAsync(string userId, DateOnly hoy, CancellationToken ct = default) =>
        (await _leer(userId, PlanDiaDoc.IdDoc(hoy), ct)).Doc;

    /// <summary>Respuesta del GET y del POST a partir del doc (null = sin plan).</summary>
    public static PlanDiaRespuesta Respuesta(PlanDiaDoc? doc, DateOnly hoy, DateTimeOffset ahora)
    {
        var n = doc?.Variaciones.Count ?? 0;
        return new(Fecha(hoy), n > 0 ? doc!.Variaciones[^1] : null, n, Math.Max(0, MaxGeneraciones - n), LeaseVivo(doc, ahora));
    }

    /// <summary>
    /// POST /coach/plan-dia, pasos 4 a 12 del contrato (la auth, el coach sin configurar y la validación van en el
    /// handler). cancelacion es la del cliente: si cortó, no se libera CoachDiario. El tope de 38 s va adentro.
    /// numero (opcional, con otraOpcion): variaciones que el cliente ya vio; si no coincide es un reintento de una
    /// generación que ya se hizo y se devuelve el estado sin modelo ni cupo.
    /// idioma: si el plan de hoy está en otro, se traduce (reemplaza la última variación, no gasta una de las 3) un solo
    /// intento por día, con el lease y los cupos de siempre; si ya se intentó, no hay cupo o falla, se devuelve el existente.
    /// </summary>
    public async Task<ResultadoPlanDia> ObtenerOGenerarAsync(string userId, DateOnly hoy, int? offsetMin, bool otraOpcion,
        CancellationToken cancelacion = default, int? numero = null, string idioma = "es")
    {
        var id = PlanDiaDoc.IdDoc(hoy);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancelacion);
        cts.CancelAfter(TiempoMaximo);
        var cupoDiarioTomado = false;
        // Desde que arranca el turno el modelo puede gastar tokens: un corte por el tope de 38 s ya no devuelve el cupo.
        var turnoIniciado = false;
        var modeloRespondio = false;
        PlanDiaDoc? lease = null;
        string? etagLease = null;
        // Traducir nunca falla: si no se puede, queda el plan que ya había (leído sin lease vivo).
        PlanDiaDoc? existente = null;
        var porIdioma = false;
        try
        {
            var (doc, etag) = await _leer(userId, id, cts.Token);
            var ahora = _reloj.GetUtcNow();
            var previas = doc?.Variaciones ?? [];
            // Decidido sobre esta misma lectura y su ETag: si dos pedidos lo deciden a la vez, solo uno toma el lease.
            porIdioma = previas.Count > 0 && !otraOpcion && IdiomaDe(doc) != idioma && !doc!.RegeneradoPorIdioma;
            // Ya hay plan y no pidió otro (ni cambió de idioma), alguien lo está generando, o es el reintento de un
            // "Otra opción" ya hecho: el estado, sin modelo ni cupo.
            if ((previas.Count > 0 && !otraOpcion && !porIdioma) || LeaseVivo(doc, ahora) ||
                (otraOpcion && numero is int visto && visto != previas.Count))
                return new(EstadoPlanDia.Listo, Respuesta(doc, hoy, ahora));
            if (porIdioma) existente = doc;
            // Traducir reemplaza la actual: vale aunque ya estén las 3.
            if (!porIdioma && previas.Count >= MaxGeneraciones) return new(EstadoPlanDia.TopeVariaciones);

            if (!_limites.Coach.Permitir(userId, out var espera)) return Existente(EstadoPlanDia.SinCupo, espera);
            if (!_limites.CoachDiario.Permitir(userId, out espera)) return Existente(EstadoPlanDia.SinCupo, espera);
            cupoDiarioTomado = true;

            // Un doc corrupto llega como null con ETag: se reemplaza como si no hubiera plan. El lease conserva el idioma
            // y la marca del doc: si la generación falla, soltarlo deja todo como estaba (y se puede reintentar).
            var nuevo = new PlanDiaDoc(id, userId, PlanDiaDoc.TipoDoc, Fecha(hoy), Iso(ahora + Lease), previas, Iso(ahora),
                doc?.Idioma, doc?.RegeneradoPorIdioma ?? false);
            etagLease = etag is null ? await _crear(nuevo, cts.Token) : await _reemplazar(nuevo, etag, cts.Token);
            if (etagLease is null)
            {
                // Otro POST tomó el lease (o escribió) entre la lectura y la escritura: no se llamó al modelo.
                _limites.CoachDiario.Liberar(userId);
                cupoDiarioTomado = false;
                return new(EstadoPlanDia.Listo, Respuesta(await LeerAsync(userId, hoy, cts.Token), hoy, _reloj.GetUtcNow()));
            }
            lease = nuevo;

            var perfil = await _perfiles.LeerAsync(userId, cts.Token);
            var entrenamiento = await EntrenamientoAsync(userId, hoy, offsetMin, cts.Token);
            // PlanHoy con las variaciones anteriores: le llegan al modelo como DATOS para que proponga otra cosa. Al traducir,
            // solo la actual (la que se traduce).
            var ctx = new ContextoCoach(userId, hoy, ahora, perfil, entrenamiento,
                porIdioma ? lease with { Variaciones = [previas[^1]] } : lease, idioma);
            var mensaje = porIdioma ? (idioma == "en" ? MensajeTraducirEn : MensajeTraducirEs)
                : previas.Count == 0 ? MensajePlan : MensajeOtraOpcion;
            turnoIniciado = true;
            var r = await _conversacion.ResponderAsync(ctx, [new("usuario", mensaje)], cts.Token);
            // Desde aquí hubo tokens: un intento de traducir cuenta para el día aunque salga mal.
            modeloRespondio = true;

            // Filtro de contenido, sin texto o sin rutina: no es un plan, no ocupa una de las 3 generaciones ni el mensaje del día.
            if (ConversacionCoach.EsRespuestaVacia(r.Respuesta) || r.Rutina is null)
            {
                await SoltarLeaseAsync(lease, etagLease, marcarIdioma: porIdioma);
                _limites.CoachDiario.Liberar(userId);
                return Existente(EstadoPlanDia.NoDisponible);
            }

            var fin = _reloj.GetUtcNow();
            // r.Rutina ya viene sin pesos inventados (ConversacionCoach los filtra). Al traducir, del modelo solo valen el
            // texto y el nombre: los ejercicios (ids, series, reps, pesos) no dependen del idioma y quedan los del plan.
            var p = r.Rutina.Rutina;
            var original = porIdioma ? previas[^1].Rutina : null;
            var variacion = new VariacionPlan(r.Respuesta, r.Herramientas,
                new RutinaPlan(p.Id, p.Nombre, original?.Ejercicios ?? p.Ejercicios, p.Actualizada,
                    original?.MinutosEstimados ?? r.Rutina.MinutosEstimados),
                Iso(fin));
            var final = lease with
            {
                GenerandoHasta = null, Variaciones = porIdioma ? [.. previas.Take(previas.Count - 1), variacion] : [.. previas, variacion],
                Actualizado = Iso(fin), Idioma = idioma, RegeneradoPorIdioma = lease.RegeneradoPorIdioma || porIdioma,
            };
            var guardado = await GuardarFinalAsync(final, etagLease);
            lease = null;
            // 412: el lease venció a mitad del turno y otro escribió; gana lo que haya (el cupo sí se gastó).
            if (guardado is null)
            {
                using var relectura = new CancellationTokenSource(TiempoEscritura);
                return new(EstadoPlanDia.Listo, Respuesta(await LeerAsync(userId, hoy, relectura.Token), hoy, fin));
            }
            return new(EstadoPlanDia.Listo, Respuesta(final, hoy, fin));
        }
        // El 429 de Azure OpenAI no sale como 429: el cupo que se agotó no es el del usuario.
        // OperationCanceled: venció el tope del turno o el cliente cortó.
        catch (Exception e) when (EsTransitorio(e) || e is ModeloNoDisponibleException)
        {
            await SoltarLeaseAsync(lease, etagLease, marcarIdioma: porIdioma && modeloRespondio);
            // Falla transitoria nuestra antes de gastar tokens: no cuenta para el tope diario. No se devuelve si el
            // cliente cortó (abortar a propósito daría generaciones ilimitadas), si el modelo ya respondió, ni si
            // venció el tope de 38 s a mitad del turno (el modelo ya estaba gastando).
            var topeDelTurno = turnoIniciado && e is OperationCanceledException;
            if (cupoDiarioTomado && !modeloRespondio && !topeDelTurno && !cancelacion.IsCancellationRequested)
                _limites.CoachDiario.Liberar(userId);
            return Existente(EstadoPlanDia.NoDisponible);
        }
        catch (Exception e)
        {
            // No transitorio (500): igual se suelta el lease para que el usuario no espere 50 s a que venza.
            await SoltarLeaseAsync(lease, etagLease, marcarIdioma: porIdioma && modeloRespondio);
            if (existente is null) throw;
            // Al traducir queda el plan que había. Solo el tipo: el mensaje podría traer datos del usuario.
            _log?.LogError("Plan del día: falló la traducción por idioma ({Tipo}); se devuelve el plan existente", e.GetType().Name);
            return Existente(EstadoPlanDia.NoDisponible);
        }

        // Traducción que no se pudo hacer: el plan que había, con su lease soltado (o vencerá solo). Sin traducción, el estado tal cual.
        ResultadoPlanDia Existente(EstadoPlanDia estado, TimeSpan espera = default) => existente is not null
            ? new(EstadoPlanDia.Listo, Respuesta(existente, hoy, _reloj.GetUtcNow()))
            : new(estado, ReintentarEn: espera);
    }

    /// <summary>"en" o "es": un doc viejo sin idioma, o con uno ilegible, es "es".</summary>
    static string IdiomaDe(PlanDiaDoc? doc) => doc?.Idioma is "en" ? "en" : "es";

    /// <summary>
    /// Replace final con IfMatch del lease, con su propio tope (el del turno puede estar vencido o el cliente
    /// haberse ido: los tokens ya se gastaron) y un reintento corto ante Cosmos saturado. null = 412.
    /// </summary>
    async Task<string?> GuardarFinalAsync(PlanDiaDoc final, string etag)
    {
        for (var intento = 1; ; intento++)
        {
            using var cts = new CancellationTokenSource(TiempoEscritura);
            // Si el primero sí escribió y solo se perdió la respuesta, el reintento da 412 y se relee: queda lo nuestro.
            try { return await _reemplazar(final, etag, cts.Token); }
            catch (Exception e) when (intento < 2 && EsTransitorio(e)) { await Task.Delay(EsperaReintento); }
        }
    }

    static bool EsTransitorio(Exception e) =>
        e is OperationCanceledException ||
        e is CosmosException { StatusCode: HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.RequestTimeout };

    /// <summary>Bloque de DATOS del prompt, o null sin variaciones.</summary>
    public static PlanHoyParaPrompt? ParaPrompt(PlanDiaDoc? plan)
    {
        if (plan is null || plan.Variaciones.Count == 0) return null;
        return new([.. plan.Variaciones.Select(v => new VariacionParaPrompt(v.Rutina?.Nombre,
            v.Rutina is null ? [] : [.. v.Rutina.Ejercicios.Select(e => NombreCatalogo(e.Id))]))]);
    }

    static string NombreCatalogo(string id) => Catalogo.Ejercicios.FirstOrDefault(e => e.Id == id)?.Nombre ?? id;

    static string Fecha(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString(FormatoTs, CultureInfo.InvariantCulture);

    /// <summary>GenerandoHasta legible y en el futuro. Ilegible cuenta como libre.</summary>
    static bool LeaseVivo(PlanDiaDoc? doc, DateTimeOffset ahora) =>
        doc?.GenerandoHasta is string g &&
        DateTimeOffset.TryParse(g, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var hasta) &&
        hasta > ahora;

    /// <summary>Sin perfil de entrenamiento el turno sigue (ContextoCoach lo admite null): solo ante Cosmos transitorio.</summary>
    async Task<PerfilEntrenamiento?> EntrenamientoAsync(string userId, DateOnly hoy, int? offsetMin, CancellationToken ct)
    {
        try { return await _entrenamiento(userId, hoy, offsetMin, ct); }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
                                            or HttpStatusCode.RequestTimeout)
        {
            return null;
        }
    }

    /// <summary>
    /// Suelta el lease con IfMatch y con su propio tope (el token del turno puede estar ya cancelado). Sin
    /// variaciones el doc no sirve y se borra; con variaciones solo se limpia generandoHasta. Si falla, vence solo.
    /// </summary>
    async Task SoltarLeaseAsync(PlanDiaDoc? lease, string? etag, bool marcarIdioma = false)
    {
        if (lease is null || etag is null) return;
        // El modelo ya gastó tokens en traducir: el intento del día queda usado aunque no haya plan nuevo.
        if (marcarIdioma) lease = lease with { RegeneradoPorIdioma = true };
        using var cts = new CancellationTokenSource(TiempoEscritura);
        try
        {
            if (lease.Variaciones.Count == 0) await _borrar(lease.UserId, lease.Id, etag, cts.Token);
            else await _reemplazar(lease with { GenerandoHasta = null, Actualizado = Iso(_reloj.GetUtcNow()) }, etag, cts.Token);
        }
        catch (Exception e) when (e is CosmosException or OperationCanceledException) { }
    }

    /// <summary>Revalida el doc leído: null si no es un plan-dia sano de este usuario y día (se trata como corrupto).</summary>
    internal static async Task<PlanDiaDoc?> DocValidoAsync(Stream contenido, string id, string userId, CancellationToken ct)
    {
        PlanDiaDoc? doc;
        try { doc = await JsonSerializer.DeserializeAsync<PlanDiaDoc>(contenido, Json, ct); }
        catch (JsonException) { return null; }
        // El deserializador deja null lo que falte aunque el tipo diga que no: se revisa campo por campo.
        if (doc is null || doc.Id != id || doc.UserId != userId || doc.Tipo != PlanDiaDoc.TipoDoc ||
            doc.Variaciones is null || doc.Variaciones.Count > MaxGeneraciones ||
            doc.Variaciones.Any(v => v is null || v.Texto is null || v.Herramientas is null || v.Creado is null ||
                                     (v.Rutina is not null && (v.Rutina.Nombre is null || v.Rutina.Ejercicios is null))))
            return null;
        return doc;
    }
}

