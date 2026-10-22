using System.Net;
using GymCoach.Api.Auth;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace GymCoach.Api.Functions;

/// <summary>
/// CRUD de rutinas, en el contenedor de series (tipo = "rutina"). Igual que las series:
/// toda operación va con partitionKey = userId del token.
/// </summary>
public sealed class Rutinas(Autenticador auth, Container series, Limites limites, TimeProvider reloj)
{
    sealed record Fila(string Id, string Nombre, List<EjercicioRutina> Ejercicios, string Actualizada);

    [Function("ListarRutinas")]
    public async Task<HttpResponseData> Listar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "rutinas")] HttpRequestData req)
    {
        var userId = await auth.UserIdAsync(req);
        if (userId is null) return await Http.NoAutenticado(req);
        // Cupo propio y bajo: trae todas las rutinas completas (las 20 con 15 ejercicios), no debe gastar el de lecturas.
        if (!limites.Rutinas.Permitir(userId, out var espera)) return await Http.DemasiadasPeticiones(req, espera);

        // TOP 50 holgado sobre MaxRutinas: acota la consulta aunque el tope cambie.
        var consulta = new QueryDefinition(
                "SELECT TOP 50 c.id, c.nombre, c.ejercicios, c.actualizada FROM c WHERE c.userId = @userId AND c.tipo = 'rutina'")
            .WithParameter("@userId", userId);

        var resultado = new List<Rutina>();
        using var it = series.GetItemQueryIterator<Fila>(consulta,
            requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(userId) });
        while (it.HasMoreResults)
            foreach (var f in await it.ReadNextAsync())
                resultado.Add(new RutinaDoc(f.Id, userId, "rutina", f.Nombre, f.Ejercicios, f.Actualizada).ADto());
        return await Http.Ok(req, resultado);
    }

    [Function("GuardarRutina")]
    public async Task<HttpResponseData> Guardar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "rutinas/{id}")] HttpRequestData req, string id)
    {
        var userId = await auth.UserIdAsync(req);
        if (userId is null) return await Http.NoAutenticado(req);
        if (!limites.PermitirEscritura(userId, out var espera)) return await Http.DemasiadasPeticiones(req, espera);

        var body = await Http.LeerBodyAsync(req, Validacion.MaxBodyBytes);
        if (body is null) return await Http.Invalido(req, [new("body", $"Máximo {Validacion.MaxBodyBytes} bytes.")]);

        var (rutina, errores) = Validacion.Rutina(id, body, reloj.GetUtcNow());
        if (rutina is null) return await Http.Invalido(req, errores);

        // Las demás rutinas (sin contar esta): editar una existente con el tope lleno sí se permite.
        var idDoc = RutinaDoc.IdDoc(rutina.Id);
        var conteo = new QueryDefinition(
                "SELECT VALUE COUNT(1) FROM c WHERE c.userId = @userId AND c.tipo = 'rutina' AND c.id != @idDoc")
            .WithParameter("@userId", userId)
            .WithParameter("@idDoc", idDoc);
        var otras = 0;
        using (var it = series.GetItemQueryIterator<int>(conteo,
                   requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(userId) }))
            while (it.HasMoreResults)
                otras += (await it.ReadNextAsync()).Sum();
        if (otras >= Validacion.MaxRutinas)
            return await Http.Invalido(req, [new("rutinas", $"Máximo {Validacion.MaxRutinas} rutinas.")]);

        // Upsert: reintentar el mismo PUT no duplica.
        await series.UpsertItemAsync(RutinaDoc.De(rutina, userId), new PartitionKey(userId));

        // El conteo previo y el upsert no son atómicos: con PUT simultáneos se podría pasar del tope. Se recuenta
        // (~3 RU) y, si se pasó, se deshace esta creación (con empate fallan todas: es lo seguro).
        var tras = 0;
        using (var it = series.GetItemQueryIterator<int>(conteo,
                   requestOptions: new QueryRequestOptions { PartitionKey = new PartitionKey(userId) }))
            while (it.HasMoreResults)
                tras += (await it.ReadNextAsync()).Sum();
        if (tras >= Validacion.MaxRutinas)
        {
            try { await series.DeleteItemAsync<RutinaDoc>(idDoc, new PartitionKey(userId)); }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound) { }
            return await Http.Invalido(req, [new("rutinas", $"Máximo {Validacion.MaxRutinas} rutinas.")]);
        }
        return await Http.Ok(req, rutina);
    }

    [Function("BorrarRutina")]
    public async Task<HttpResponseData> Borrar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "rutinas/{id}")] HttpRequestData req, string id)
    {
        var userId = await auth.UserIdAsync(req);
        if (userId is null) return await Http.NoAutenticado(req);
        if (!limites.PermitirEscritura(userId, out var espera)) return await Http.DemasiadasPeticiones(req, espera);
        if (!Guid.TryParseExact(id, "D", out var guid)) return await Http.Invalido(req, [new("id", "Debe ser un UUID.")]);

        try
        {
            await series.DeleteItemAsync<RutinaDoc>(RutinaDoc.IdDoc(guid.ToString("D")), new PartitionKey(userId));
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return await Http.NoEncontrado(req);
        }
        return req.CreateResponse(HttpStatusCode.NoContent);
    }
}
