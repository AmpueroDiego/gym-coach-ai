using System.Globalization;
using System.Net;
using GymCoach.Api.Auth;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace GymCoach.Api.Functions;

/// <summary>
/// GET /api/perfil-entrenamiento: el perfil calculado con las series del usuario y lo que de él ve el coach (paraCoach).
/// userId siempre del token: uno en la query se ignora.
/// </summary>
public sealed class PerfilEntrenamientoHttp(Autenticador auth, PerfilesEntrenamiento entrenamiento, PerfilesCoach perfiles,
    Limites limites, TimeProvider reloj)
{
    // ObtenerAsync no expone la espera del cupo de recálculos: su ventana es de 1 minuto.
    static readonly TimeSpan EsperaRecalculo = TimeSpan.FromMinutes(1);

    [Function("LeerPerfilEntrenamiento")]
    public async Task<HttpResponseData> Leer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "perfil-entrenamiento")] HttpRequestData req)
    {
        var userId = await auth.UserIdAsync(req);
        if (userId is null) return await Http.NoAutenticado(req);
        if (!limites.Lecturas.Permitir(userId, out var espera)) return await Http.DemasiadasPeticiones(req, espera);

        var query = System.Web.HttpUtility.ParseQueryString(req.Url.Query);
        var (hoy, offsetMin, errores) = Consulta(query["hoy"], query["offsetMin"], reloj.GetUtcNow());
        if (errores.Count > 0) return await Http.Invalido(req, errores);

        try
        {
            var perfil = await entrenamiento.ObtenerAsync(userId, hoy, offsetMin);
            // Tocaba recalcular, no hubo cupo y no hay doc viejo que mostrar.
            if (perfil is null) return await Http.DemasiadasPeticiones(req, EsperaRecalculo);
            var perfilCoach = await perfiles.LeerAsync(userId);
            return await Http.Ok(req, new { perfil, paraCoach = CalculosPerfil.ParaCoach(perfil, perfilCoach, hoy) });
        }
        catch (CosmosException e) when (Transitorio(e)) { return await Http.NoDisponible(req); }
    }

    /// <summary>
    /// hoy: opcional (sin él, el día UTC), YYYY-MM-DD a un día o menos del día UTC del servidor.
    /// offsetMin: opcional, entero entre -720 y 840.
    /// </summary>
    internal static (DateOnly Hoy, int? OffsetMin, List<ErrorCampo> Errores) Consulta(string? hoyTexto, string? offsetTexto,
        DateTimeOffset ahora)
    {
        var errores = new List<ErrorCampo>();
        var hoyUtc = DateOnly.FromDateTime(ahora.UtcDateTime);
        var hoy = hoyUtc;
        if (hoyTexto is not null &&
            (!DateOnly.TryParseExact(hoyTexto, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out hoy) ||
             Math.Abs(hoy.DayNumber - hoyUtc.DayNumber) > 1))
            errores.Add(new("hoy", "Debe ser YYYY-MM-DD a un día o menos de hoy."));

        int? offsetMin = null;
        if (offsetTexto is not null)
        {
            // Entero estricto: "60.0", " 60" o "+60" son error.
            if (int.TryParse(offsetTexto, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var o) &&
                !offsetTexto.StartsWith('+') && o >= CalculosPerfil.MinOffsetMin && o <= CalculosPerfil.MaxOffsetMin)
                offsetMin = o;
            else
                errores.Add(new("offsetMin", $"Debe ser un entero entre {CalculosPerfil.MinOffsetMin} y {CalculosPerfil.MaxOffsetMin}."));
        }
        return (hoy, offsetMin, errores);
    }

    // Base saturada o lenta: 503 con Retry-After en vez de 500.
    static bool Transitorio(CosmosException e) =>
        e.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.RequestTimeout;
}
