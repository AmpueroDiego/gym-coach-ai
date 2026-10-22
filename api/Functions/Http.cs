using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker.Http;

namespace GymCoach.Api.Functions;

/// <summary>Respuestas JSON con el formato de errores de la API.</summary>
public static class Http
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        // cargaCorporal solo aparece cuando aplica.
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // Se serializa a mano: WriteAsJsonAsync pisa el status code con 200.
    public static async Task<HttpResponseData> JsonAsync(HttpRequestData req, HttpStatusCode codigo, object cuerpo)
    {
        var res = req.CreateResponse(codigo);
        res.Headers.Add("Content-Type", "application/json; charset=utf-8");
        await res.WriteStringAsync(JsonSerializer.Serialize(cuerpo, Json), Encoding.UTF8);
        return res;
    }

    public static Task<HttpResponseData> Ok(HttpRequestData req, object cuerpo) => JsonAsync(req, HttpStatusCode.OK, cuerpo);

    public static Task<HttpResponseData> NoAutenticado(HttpRequestData req) =>
        JsonAsync(req, HttpStatusCode.Unauthorized, new { error = "no_autenticado" });

    public static Task<HttpResponseData> NoEncontrado(HttpRequestData req) =>
        JsonAsync(req, HttpStatusCode.NotFound, new { error = "no_encontrado" });

    public static Task<HttpResponseData> Invalido(HttpRequestData req, IEnumerable<ErrorCampo> detalles) =>
        JsonAsync(req, HttpStatusCode.BadRequest, new { error = "validacion", detalles });

    public static Task<HttpResponseData> Interno(HttpRequestData req) =>
        JsonAsync(req, HttpStatusCode.InternalServerError, new { error = "interno" });

    /// <summary>503 transitorio sin haber escrito nada: la PWA reintenta sola.</summary>
    public static async Task<HttpResponseData> NoDisponible(HttpRequestData req)
    {
        var res = await JsonAsync(req, HttpStatusCode.ServiceUnavailable, new { error = "no_disponible" });
        res.Headers.Add("Retry-After", "2");
        return res;
    }

    public static async Task<HttpResponseData> DemasiadasPeticiones(HttpRequestData req, TimeSpan reintentarEn)
    {
        var res = await JsonAsync(req, HttpStatusCode.TooManyRequests, new { error = "demasiadas_peticiones" });
        res.Headers.Add("Retry-After", Math.Max(1, (int)Math.Ceiling(reintentarEn.TotalSeconds)).ToString());
        return res;
    }

    // En Azure, Static Web Apps reemplaza Authorization antes de llegar a la función: la PWA manda
    // el token también en X-Custom-Authorization. Si esa trae valor, es la única que cuenta (aunque
    // sea inválida): caer a Authorization abriría una segunda oportunidad con otro token.
    // Authorization queda solo de respaldo para el emulador local y clientes que no mandan la propia.
    public static string? Authorization(HttpRequestData req)
    {
        if (req.Headers.TryGetValues("X-Custom-Authorization", out var c) && c.FirstOrDefault() is string propia &&
            !string.IsNullOrWhiteSpace(propia))
            return propia;
        return req.Headers.TryGetValues("Authorization", out var v) ? v.FirstOrDefault() : null;
    }

    /// <summary>Lee el body con tope de tamaño; null si lo excede (por Content-Length o al leer).</summary>
    public static async Task<string?> LeerBodyAsync(HttpRequestData req, int maxBytes)
    {
        // Rechazar sin leer nada si el cliente ya declara un body grande.
        if (req.Headers.TryGetValues("Content-Length", out var cl) &&
            long.TryParse(cl.FirstOrDefault(), out var declarado) && declarado > maxBytes)
            return null;

        var buffer = new byte[maxBytes + 1];
        int total = 0, leidos;
        while (total < buffer.Length && (leidos = await req.Body.ReadAsync(buffer.AsMemory(total))) > 0)
            total += leidos;
        return total > maxBytes ? null : Encoding.UTF8.GetString(buffer, 0, total);
    }
}
