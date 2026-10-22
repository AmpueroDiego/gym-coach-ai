using System.Globalization;
using System.Net;
using System.Text.Json;
using GymCoach.Api.Auth;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace GymCoach.Api.Functions;

/// <summary>Numero: variaciones que el cliente ya tenía al pedir otra opción (idempotencia de reintentos). Idioma: es | en.</summary>
public sealed record PeticionPlanDia(DateOnly Hoy, int? OffsetMin, bool OtraOpcion, int? Numero, string Idioma = "es");

/// <summary>
/// GET y POST /api/coach/plan-dia: el plan del día guardado (uno por usuario y día). La lógica del lease y los
/// cupos del coach vive en PlanesDia; aquí van la auth, la validación y el mapeo a HTTP. userId siempre del token.
/// </summary>
public sealed class PlanDiaHttp(Autenticador auth, Limites limites, TimeProvider reloj, IModeloCoach modelo, PlanesDia planes)
{
    // {hoy, offsetMin, otraOpcion, numero, idioma}: con 1 KB sobra.
    public const int MaxBodyBytes = 1024;
    public const int OffsetMinimo = -720;
    public const int OffsetMaximo = 840;

    [Function("LeerPlanDia")]
    public async Task<HttpResponseData> Leer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "coach/plan-dia")] HttpRequestData req)
    {
        var userId = await auth.UserIdAsync(req);
        if (userId is null) return await Http.NoAutenticado(req);
        if (!limites.Lecturas.Permitir(userId, out var espera)) return await Http.DemasiadasPeticiones(req, espera);

        var ahora = reloj.GetUtcNow();
        var (hoy, error) = Hoy(System.Web.HttpUtility.ParseQueryString(req.Url.Query)["hoy"], ahora);
        if (error is not null) return await Http.Invalido(req, [error]);

        try { return await Http.Ok(req, PlanesDia.Respuesta(await planes.LeerAsync(userId, hoy), hoy, reloj.GetUtcNow())); }
        catch (CosmosException e) when (Transitorio(e)) { return await Http.NoDisponible(req); }
    }

    [Function("GenerarPlanDia")]
    public async Task<HttpResponseData> Generar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "coach/plan-dia")] HttpRequestData req,
        CancellationToken cancelacion = default)
    {
        var userId = await auth.UserIdAsync(req);
        if (userId is null) return await Http.NoAutenticado(req);
        // Sin config no se gasta cupo: no es culpa del usuario.
        if (modelo is ModeloSinConfigurar)
            return await Http.JsonAsync(req, HttpStatusCode.ServiceUnavailable, new { error = "coach_no_configurado" });

        var body = await Http.LeerBodyAsync(req, MaxBodyBytes);
        if (body is null) return await Http.Invalido(req, [new("body", $"Máximo {MaxBodyBytes} bytes.")]);
        var (peticion, errores) = Peticion(body, reloj.GetUtcNow());
        if (peticion is null) return await Http.Invalido(req, errores);
        // Leer el doc cuesta 1 RU aunque ya haya plan: el POST también tiene tope (Coach y CoachDiario van adentro).
        if (!limites.Lecturas.Permitir(userId, out var espera)) return await Http.DemasiadasPeticiones(req, espera);

        var r = await planes.ObtenerOGenerarAsync(userId, peticion.Hoy, peticion.OffsetMin, peticion.OtraOpcion, cancelacion,
            peticion.Numero, peticion.Idioma);
        return r.Estado switch
        {
            EstadoPlanDia.Listo => await Http.Ok(req, r.Plan!),
            EstadoPlanDia.TopeVariaciones => await Http.JsonAsync(req, HttpStatusCode.Conflict, new { error = "tope_variaciones" }),
            EstadoPlanDia.SinCupo => await Http.DemasiadasPeticiones(req, r.ReintentarEn),
            _ => await Http.NoDisponible(req),
        };
    }

    /// <summary>Body de POST. Propiedades desconocidas (incluido userId) se ignoran: el usuario sale del token.</summary>
    public static (PeticionPlanDia? Peticion, List<ErrorCampo> Errores) Peticion(string body, DateTimeOffset ahora)
    {
        // GetString lanza InvalidOperationException con un surrogate suelto en el JSON: es entrada mala, no un 500.
        try { return PeticionInterna(body, ahora); }
        catch (InvalidOperationException) { return (null, [new("body", "No es JSON válido.")]); }
    }

    static (PeticionPlanDia?, List<ErrorCampo>) PeticionInterna(string body, DateTimeOffset ahora)
    {
        var errores = new List<ErrorCampo>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body, Validacion.OpcionesJson); }
        catch (JsonException)
        {
            errores.Add(new("body", "No es JSON válido."));
            return (null, errores);
        }

        using (doc)
        {
            var raiz = doc.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object)
            {
                errores.Add(new("body", "Debe ser un objeto JSON."));
                return (null, errores);
            }

            // Obligatorio (contrato { hoy, offsetMin?, otraOpcion? }): fija el id del doc.
            var (hoy, error) = Hoy(raiz.TryGetProperty("hoy", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null,
                ahora);
            if (error is not null) errores.Add(error);

            int? offsetMin = null;
            if (raiz.TryGetProperty("offsetMin", out var o) && o.ValueKind != JsonValueKind.Null)
            {
                // TryGetInt32 rechaza decimales (1.5) y números fuera de int.
                if (o.ValueKind != JsonValueKind.Number || !o.TryGetInt32(out var off) || off < OffsetMinimo || off > OffsetMaximo)
                    errores.Add(new("offsetMin", $"Debe ser un entero entre {OffsetMinimo} y {OffsetMaximo}."));
                else offsetMin = off;
            }

            var otraOpcion = false;
            if (raiz.TryGetProperty("otraOpcion", out var op))
            {
                // Booleano estricto: "true" o 1 no cuentan.
                if (op.ValueKind is JsonValueKind.True or JsonValueKind.False) otraOpcion = op.GetBoolean();
                else errores.Add(new("otraOpcion", "Debe ser true o false."));
            }

            int? numero = null;
            if (raiz.TryGetProperty("numero", out var n) && n.ValueKind != JsonValueKind.Null)
            {
                if (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var num) || num < 0)
                    errores.Add(new("numero", "Debe ser un entero mayor o igual a 0."));
                else numero = num;
            }

            var idioma = ValidacionCoach.Idioma(raiz, errores);

            if (errores.Count > 0) return (null, errores);
            return (new PeticionPlanDia(hoy, offsetMin, otraOpcion, numero, idioma), errores);
        }
    }

    const string MensajeHoy = "Debe ser YYYY-MM-DD a un día o menos de hoy.";

    /// <summary>hoy del cliente a ±1 día del día UTC del servidor (zona horaria). Acota los docs plan-dia que se pueden crear.</summary>
    static (DateOnly Hoy, ErrorCampo? Error) Hoy(string? texto, DateTimeOffset ahora)
    {
        var hoyUtc = DateOnly.FromDateTime(ahora.UtcDateTime);
        if (texto is null ||
            !DateOnly.TryParseExact(texto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var hoy) ||
            Math.Abs(hoy.DayNumber - hoyUtc.DayNumber) > 1)
            return (default, new("hoy", MensajeHoy));
        return (hoy, null);
    }

    // Base saturada o lenta: 503 con Retry-After en vez de 500.
    static bool Transitorio(CosmosException e) =>
        e.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.RequestTimeout;
}
