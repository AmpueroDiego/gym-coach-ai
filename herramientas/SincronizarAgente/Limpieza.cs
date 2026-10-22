namespace GymCoach.SincronizarAgente;

/// <summary>
/// Borra responses y conversaciones viejas del proyecto (guardan los DATOS del usuario). Las Functions de SWA no tienen timer:
/// se corre a mano. Simulación salvo confirmar. Solo imprime conteos, nunca contenido.
/// </summary>
public static class Limpieza
{
    public static async Task<int> EjecutarAsync(ClienteFoundry cliente, int dias, bool confirmar, DateTimeOffset ahora)
    {
        var corte = ahora.AddDays(-dias).ToUnixTimeSeconds();
        var responses = (await cliente.ListarAsync("responses")).Where(x => x.CreadoEn < corte).ToList();
        var conversaciones = await cliente.ListarAsync("conversations");
        // Conversaciones viejas por su fecha, más las referenciadas por responses viejas (por si el listado no las trae).
        var idsConv = conversaciones.Where(x => x.CreadoEn < corte).Select(x => x.Id)
            .Concat(responses.Where(x => x.ConversacionId is not null).Select(x => x.ConversacionId!)).Distinct().ToList();

        Console.WriteLine($"Más viejos que {dias} día(s): {responses.Count} responses, {idsConv.Count} conversaciones");
        if (!confirmar) { Console.WriteLine("Simulación: no se borró nada. Agrega --si para borrar."); return 0; }

        // Primero responses y luego conversaciones: borrar la conversación no borra sus responses.
        var fallos = await BorrarTodoAsync(cliente, "responses", responses.Select(x => x.Id));
        fallos += await BorrarTodoAsync(cliente, "conversations", idsConv);
        Console.WriteLine(fallos == 0 ? "Borrado completo" : $"Quedaron {fallos} sin borrar (reintenta)");
        return fallos == 0 ? 0 : 2;
    }

    static async Task<int> BorrarTodoAsync(ClienteFoundry cliente, string recurso, IEnumerable<string> ids)
    {
        var fallos = 0;
        using var cupo = new SemaphoreSlim(8); // paralelo moderado
        await Task.WhenAll(ids.Select(async id =>
        {
            await cupo.WaitAsync();
            try { if (!await cliente.BorrarItemAsync(recurso, id)) Interlocked.Increment(ref fallos); }
            catch (HttpRequestException) { Interlocked.Increment(ref fallos); }
            finally { cupo.Release(); }
        }));
        Console.WriteLine($"{recurso}: {ids.Count() - fallos} borrados");
        return fallos;
    }
}
