#:package Microsoft.Azure.Cosmos@3.63.2
#:package Newtonsoft.Json@13.0.4
#:property PublishAot=false

// Crea la DB (400 RU/s a nivel de base de datos) y el contenedor series si no existen.
// Uso: npm run init-cosmos  (lee COSMOS_* de las variables de entorno o de api/local.settings.json)
using System.Text.Json;
using Microsoft.Azure.Cosmos;

var raiz = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", ".."));
var settings = Path.Combine(raiz, "api", "local.settings.json");
var valores = File.Exists(settings)
    ? JsonDocument.Parse(File.ReadAllText(settings)).RootElement.GetProperty("Values")
    : default;

string Valor(string nombre) =>
    Environment.GetEnvironmentVariable(nombre)
    ?? (valores.ValueKind == JsonValueKind.Object && valores.TryGetProperty(nombre, out var v) ? v.GetString() : null)
    ?? throw new InvalidOperationException($"Falta {nombre} (variable de entorno o api/local.settings.json).");

using var client = new CosmosClient(Valor("COSMOS_ENDPOINT"), Valor("COSMOS_KEY"));

// Throughput compartido a nivel de DB: el free tier da 1000 RU/s para toda la cuenta.
var db = (await client.CreateDatabaseIfNotExistsAsync(Valor("COSMOS_DB"), ThroughputProperties.CreateManualThroughput(400))).Database;

// Política de indexado por defecto (todo indexado): alcanza para filtrar por fecha/ejercicio y ordenar por ts.
var respuesta = await db.CreateContainerIfNotExistsAsync("series", "/userId");

Console.WriteLine($"DB {db.Id} y contenedor series listos ({respuesta.StatusCode}).");
