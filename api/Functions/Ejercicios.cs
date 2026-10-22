using GymCoach.Api.Auth;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace GymCoach.Api.Functions;

public sealed class Ejercicios(Autenticador auth, Container series, Limites limites)
{
    [Function("ListarEjercicios")]
    public async Task<HttpResponseData> Listar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ejercicios")] HttpRequestData req)
    {
        var userId = await auth.UserIdAsync(req);
        if (userId is null) return await Http.NoAutenticado(req);
        // Sin cupo: el catálogo es estático y no toca Cosmos.
        return await Http.Ok(req, Catalogo.Ejercicios);
    }

    [Function("HistorialEjercicio")]
    public async Task<HttpResponseData> Historial(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "ejercicios/{ejercicioId}/historial")] HttpRequestData req,
        string ejercicioId)
    {
        var userId = await auth.UserIdAsync(req);
        if (userId is null) return await Http.NoAutenticado(req);
        if (!limites.Lecturas.Permitir(userId, out var espera)) return await Http.DemasiadasPeticiones(req, espera);
        if (!Catalogo.Existe(ejercicioId)) return await Http.NoEncontrado(req);

        var (limite, errores) = Validacion.Limite(System.Web.HttpUtility.ParseQueryString(req.Url.Query)["limite"]);
        if (errores.Count > 0) return await Http.Invalido(req, errores);

        var consulta = new QueryDefinition(
                "SELECT TOP @limite c.id, c.ejercicioId, c.pesoKg, c.reps, c.fecha, c.ts, c.rutina, c.tipoSerie FROM c " +
                "WHERE c.userId = @userId AND c.tipo = 'serie' AND c.ejercicioId = @ejercicioId ORDER BY c.ts DESC")
            .WithParameter("@limite", limite)
            .WithParameter("@userId", userId)
            .WithParameter("@ejercicioId", ejercicioId);

        return await Http.Ok(req, await Series.ConsultarAsync(series, consulta, userId));
    }
}
