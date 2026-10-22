using System.Net;
using GymCoach.Api.Auth;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace GymCoach.Api.Functions;

/// <summary>Perfil del coach (doc "perfil-coach" en la partición del usuario). userId siempre del token.</summary>
public sealed class PerfilCoachHttp(Autenticador auth, PerfilesCoach perfiles, Limites limites)
{
    [Function("LeerPerfilCoach")]
    public async Task<HttpResponseData> Leer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "coach/perfil")] HttpRequestData req)
    {
        var userId = await auth.UserIdAsync(req);
        if (userId is null) return await Http.NoAutenticado(req);
        if (!limites.Lecturas.Permitir(userId, out var espera)) return await Http.DemasiadasPeticiones(req, espera);

        try { return await Http.Ok(req, await perfiles.LeerAsync(userId)); }
        catch (CosmosException e) when (Transitorio(e)) { return await Http.NoDisponible(req); }
    }

    [Function("GuardarPerfilCoach")]
    public async Task<HttpResponseData> Guardar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "coach/perfil")] HttpRequestData req)
    {
        var userId = await auth.UserIdAsync(req);
        if (userId is null) return await Http.NoAutenticado(req);
        // Cupo de lecturas, no el de escrituras: guardar el perfil no debe frenar el registro de series.
        if (!limites.Lecturas.Permitir(userId, out var espera)) return await Http.DemasiadasPeticiones(req, espera);

        var body = await Http.LeerBodyAsync(req, Validacion.MaxBodyPerfilBytes);
        if (body is null) return await Http.Invalido(req, [new("body", $"Máximo {Validacion.MaxBodyPerfilBytes} bytes.")]);

        var (perfil, errores, traeAltura, traePeso, traeEquipo, traeExperiencia, traeDias) = Validacion.PerfilCoach(body);
        if (perfil is null) return await Http.Invalido(req, errores);

        PerfilCoach? guardado;
        try
        {
            // null = otro escritor ganó dos veces: nada se guardó, la PWA puede reintentar.
            guardado = await perfiles.GuardarAsync(userId, perfil, traeAltura, traePeso, traeEquipo, traeExperiencia, traeDias);
            if (guardado is null) return await Http.NoDisponible(req);
        }
        catch (CosmosException e) when (Transitorio(e)) { return await Http.NoDisponible(req); }
        // El guardado, no el del body: puede traer altura, peso, equipo, experiencia o días conservados.
        return await Http.Ok(req, guardado);
    }

    // Base saturada o lenta: 503 con Retry-After en vez de 500.
    static bool Transitorio(CosmosException e) =>
        e.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.RequestTimeout;
}
