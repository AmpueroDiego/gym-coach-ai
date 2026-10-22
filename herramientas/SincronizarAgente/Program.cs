// Publica el agente `gym-coach` en Foundry si cambió algo (instrucciones, entradas, herramientas, modelo, razonamiento).
// Uso: dotnet run --project herramientas/SincronizarAgente -- [--comprobar] [--endpoint URL] [--agente NOMBRE]
// REST directo: Azure.AI.Projects 2.0.1 choca con el OpenAI 2.14 de la API. Auth: az login (rol "Foundry User").
using GymCoach.SincronizarAgente;

const string Uso = "Uso: SincronizarAgente [--comprobar] [--endpoint URL] [--agente NOMBRE] [--borrar --agente NOMBRE] [--limpiar [--dias N] [--si]]";
var conValor = new[] { "--endpoint", "--agente", "--dias" };
var sinValor = new[] { "--comprobar", "--borrar", "--limpiar", "--si" };
for (var i = 0; i < args.Length; i++)
{
    if (conValor.Contains(args[i])) { if (++i >= args.Length || args[i].StartsWith("--")) { Console.Error.WriteLine(Uso); return 2; } }
    else if (!sinValor.Contains(args[i])) { Console.Error.WriteLine(Uso); return 2; }
}
var comprobar = args.Contains("--comprobar");
string? Opcion(string nombre) { var i = Array.IndexOf(args, nombre); return i >= 0 ? args[i + 1] : null; }
var endpoint = Opcion("--endpoint") ?? Environment.GetEnvironmentVariable("FOUNDRY_PROJECT_ENDPOINT") ?? Agente.EndpointPorDefecto;
var nombre = Opcion("--agente") ?? Agente.NombrePorDefecto;
var limpiar = args.Contains("--limpiar");
var dias = 2;
if (Opcion("--dias") is { } d && (!int.TryParse(d, out dias) || dias < 0 || dias > 365)) { Console.Error.WriteLine(Uso); return 2; }
var borrar = args.Contains("--borrar"); // solo para limpiar un agente temporal de pruebas
if (borrar && (Opcion("--agente") is null || nombre == Agente.NombrePorDefecto))
{
    Console.Error.WriteLine("--borrar exige --agente con un nombre distinto de " + Agente.NombrePorDefecto);
    return 2;
}

try
{
    using var cliente = new ClienteFoundry(endpoint);
    if (limpiar) return await Limpieza.EjecutarAsync(cliente, dias, args.Contains("--si"), DateTimeOffset.UtcNow);
    if (borrar) { await cliente.BorrarAsync(nombre); Console.WriteLine($"agente {nombre} borrado"); return 0; }

    var definicion = Agente.Definicion();
    var hash = Agente.Hash(definicion);
    var ultima = await cliente.UltimaVersionAsync(nombre);

    if (ultima is not null && ultima.Hash == hash) { Console.WriteLine($"{nombre} v{ultima.Version}: sin cambios"); return 0; }
    if (comprobar)
    {
        Console.WriteLine(ultima is null ? $"{nombre}: no existe (o endpoint incorrecto), hace falta sincronizar" : $"{nombre} v{ultima.Version}: desactualizado, hace falta sincronizar");
        return 1;
    }
    var version = await cliente.CrearVersionAsync(nombre, definicion, hash);
    Console.WriteLine($"{nombre}: versión {version} creada");
    return 0;
}
catch (Exception ex) when (ex is HttpRequestException or Azure.Identity.CredentialUnavailableException or Azure.Identity.AuthenticationFailedException)
{
    Console.Error.WriteLine("Error: " + ex.Message); // sin tokens ni keys en los mensajes
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Error inesperado: " + ex.GetType().Name); // sin cuerpo ni stack
    return 2;
}
