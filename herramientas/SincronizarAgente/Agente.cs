using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using GymCoach.Api;

namespace GymCoach.SincronizarAgente;

/// <summary>Definición del agente tal como la espera Foundry y su hash. Todo sale del código de la API.</summary>
public static class Agente
{
    public const string EndpointPorDefecto = "https://aif-gym-coach-diego.services.ai.azure.com/api/projects/gym-coach"; // sin key
    public const string NombrePorDefecto = "gym-coach";
    public const string Modelo = "coach-mini"; // nombre del deployment
    public const string Razonamiento = "minimal";

    public static JsonObject Definicion() => Definicion(PromptCoach.Instrucciones(), PromptCoach.Entradas, HerramientasCoach.Definiciones);

    public static JsonObject Definicion(string instrucciones, IEnumerable<string> entradas, IEnumerable<DefinicionHerramienta> herramientas)
    {
        // CRLF/LF cambiaría el hash según cómo se haya descargado el repo: se normaliza antes de hashear y de enviar.
        static string Lf(string t) => t.ReplaceLineEndings("\n");
        instrucciones = Lf(instrucciones);
        var inputs = new JsonObject();
        foreach (var e in entradas)
            inputs[e] = new JsonObject { ["description"] = "Valor de " + e + " para el turno", ["default_value"] = "" };
        var tools = new JsonArray();
        // Igual que ModeloAzureOpenAI: sin strict (los esquemas no son compatibles con strict).
        foreach (var h in herramientas)
            tools.Add(new JsonObject
            {
                ["type"] = "function", ["name"] = h.Nombre, ["description"] = Lf(h.Descripcion),
                ["parameters"] = JsonNode.Parse(Lf(h.ParametrosJson)),
            });
        return new JsonObject
        {
            ["kind"] = "prompt",
            ["model"] = Modelo,
            ["instructions"] = instrucciones,
            ["reasoning"] = new JsonObject { ["effort"] = Razonamiento },
            ["tools"] = tools,
            ["structured_inputs"] = inputs,
        };
    }

    /// <summary>SHA-256 (hex) del JSON de la definición; determinista porque las claves se escriben siempre en el mismo orden.</summary>
    public static string Hash(JsonObject definicion) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(definicion.ToJsonString()))).ToLowerInvariant();
}
