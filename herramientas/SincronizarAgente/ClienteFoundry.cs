using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

namespace GymCoach.SincronizarAgente;

public sealed record VersionAgente(string Version, string? Hash);
public sealed record ItemFoundry(string Id, long CreadoEn, string? ConversacionId);

/// <summary>REST mínimo del endpoint de agentes del proyecto. Token de AzureCliCredential, nunca se imprime.</summary>
public sealed class ClienteFoundry(string endpoint) : IDisposable
{
    const string ApiVersion = "v1";
    readonly HttpClient _http = new();
    readonly string _endpoint = endpoint.TrimEnd('/');
    readonly AzureCliCredential _cred = new();

    async Task<HttpResponseMessage> EnviarAsync(HttpMethod metodo, string ruta, JsonNode? cuerpo = null, bool conApiVersion = true)
    {
        var token = await _cred.GetTokenAsync(new TokenRequestContext(["https://ai.azure.com/.default"]), CancellationToken.None);
        using var req = new HttpRequestMessage(metodo, $"{_endpoint}{ruta}{(!conApiVersion ? "" : ruta.Contains('?') ? "&api-version=" + ApiVersion : "?api-version=" + ApiVersion)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        if (cuerpo is not null) req.Content = new StringContent(cuerpo.ToJsonString(), Encoding.UTF8, "application/json");
        return await _http.SendAsync(req);
    }

    static async Task<string> LeerAsync(HttpResponseMessage r, string accion)
    {
        var texto = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"{accion}: HTTP {(int)r.StatusCode} {texto[..Math.Min(500, texto.Length)]}");
        return texto;
    }

    /// <summary>null si el agente no existe o no tiene versiones.</summary>
    public async Task<VersionAgente?> UltimaVersionAsync(string agente)
    {
        using var r = await EnviarAsync(HttpMethod.Get, $"/agents/{Uri.EscapeDataString(agente)}/versions?limit=1&order=desc");
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        var v = JsonNode.Parse(await LeerAsync(r, "listar versiones"))?["data"]?.AsArray().FirstOrDefault();
        return v is null ? null : new VersionAgente(v["version"]!.ToString(), v["metadata"]?["hash"]?.ToString());
    }

    public async Task<string> CrearVersionAsync(string agente, JsonObject definicion, string hash)
    {
        var cuerpo = new JsonObject { ["definition"] = definicion, ["metadata"] = new JsonObject { ["hash"] = hash } };
        using var r = await EnviarAsync(HttpMethod.Post, $"/agents/{Uri.EscapeDataString(agente)}/versions", cuerpo);
        return JsonNode.Parse(await LeerAsync(r, "crear versión"))!["version"]!.ToString();
    }

    public async Task BorrarAsync(string agente)
    {
        using var r = await EnviarAsync(HttpMethod.Delete, $"/agents/{Uri.EscapeDataString(agente)}");
        await LeerAsync(r, "borrar agente");
    }

    /// <summary>Elementos de /openai/v1/{recurso} (responses | conversations) con id, creado y conversación; paginado con tope.</summary>
    public async Task<List<ItemFoundry>> ListarAsync(string recurso, int maxPaginas = 50)
    {
        var todos = new List<ItemFoundry>();
        string? despues = null;
        for (var pagina = 0; pagina < maxPaginas; pagina++)
        {
            using var r = await EnviarAsync(HttpMethod.Get, $"/openai/v1/{recurso}?limit=100{(despues is null ? "" : "&after=" + Uri.EscapeDataString(despues))}", conApiVersion: false);
            var raiz = JsonNode.Parse(await LeerAsync(r, "listar " + recurso))!;
            foreach (var x in raiz["data"]!.AsArray())
                todos.Add(new ItemFoundry(x!["id"]!.ToString(), x["created_at"]!.GetValue<long>(), x["conversation"]?["id"]?.ToString()));
            despues = raiz["last_id"]?.ToString();
            if (raiz["has_more"]?.GetValue<bool>() != true || string.IsNullOrEmpty(despues)) return todos;
        }
        throw new HttpRequestException($"listar {recurso}: más de {maxPaginas} páginas, se aborta");
    }

    /// <summary>true si se borró; 404 cuenta como ya borrado.</summary>
    public async Task<bool> BorrarItemAsync(string recurso, string id)
    {
        using var r = await EnviarAsync(HttpMethod.Delete, $"/openai/v1/{recurso}/{Uri.EscapeDataString(id)}", conApiVersion: false);
        if (r.StatusCode == System.Net.HttpStatusCode.NotFound) return true;
        return r.IsSuccessStatusCode;
    }

    public void Dispose() => _http.Dispose();
}
