using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using GymCoach.Api.Auth;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;

namespace GymCoach.Api.Functions;

/// <summary>
/// /api/sesion: cambia un ID token de Google por una cookie de sesión HttpOnly de 90 días.
/// No toca Cosmos (0 RU). Todas las respuestas llevan Cache-Control: no-store.
/// </summary>
public sealed class Sesion(ValidadorGoogle google, SesionFirmada sesion, TimeProvider reloj)
{
    const int MaxBodyBytes = 8 * 1024;
    // Un ID token sirve para crear sesión solo recién emitido: uno viejo filtrado (logs, historial) no.
    static readonly TimeSpan MaxEdadToken = TimeSpan.FromMinutes(10);

    [Function("CrearSesion")]
    public async Task<HttpResponseData> Crear(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "sesion")] HttpRequestData req,
        CancellationToken ct = default)
    {
        if (!Autenticador.TieneValor(req, "X-Requested-With")) return await Csrf(req);

        var body = await Http.LeerBodyAsync(req, MaxBodyBytes);
        if (body is null) return SinCache(await Http.Invalido(req, [new("idToken", $"Máximo {MaxBodyBytes} bytes.")]));
        if (IdToken(body) is not string idToken)
            return SinCache(await Http.Invalido(req, [new("idToken", "Requerido: el ID token de Google.")]));

        // Cualquier cookie previa se ignora y se sobrescribe: el login manda.
        var id = await google.IdentidadAsync(idToken, ct);
        if (id is null || id.Sub.Length > 255 || id.Iat is not long iat ||
            reloj.GetUtcNow().ToUnixTimeSeconds() - iat > (long)MaxEdadToken.TotalSeconds)
            return SinCache(await Http.NoAutenticado(req));

        var nombre = id.Nombre;
        string valor;
        try { valor = sesion.Emitir(id.Sub, nombre, id.Foto); }
        catch (InvalidOperationException)
        {
            // Excede 2048 (no debería con los topes): mejor sesión sin nombre que un 500.
            nombre = null;
            valor = sesion.Emitir(id.Sub, null, id.Foto);
        }
        var res = SinCache(await Http.Ok(req, new Perfil(id.Sub, nombre, id.Foto)));
        res.Headers.Add("Set-Cookie", sesion.HeaderEmitir(valor));
        return res;
    }

    [Function("LeerSesion")]
    public async Task<HttpResponseData> Leer(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "sesion")] HttpRequestData req)
    {
        // Solo cookie: aquí el header Authorization no cuenta, la PWA pregunta si su cookie sigue viva.
        var (estado, valor) = Autenticador.LeerCookie(req, sesion.Nombre);
        if (estado == EstadoCookie.Ninguna)
            return SinCache(await Http.JsonAsync(req, HttpStatusCode.Unauthorized, new { error = "sin_sesion" }));

        var perfil = estado == EstadoCookie.Presente ? sesion.Verificar(valor) : null;
        if (perfil is null)
        {
            var mala = SinCache(await Http.JsonAsync(req, HttpStatusCode.Unauthorized, new { error = "sesion_invalida" }));
            mala.Headers.Add("Set-Cookie", sesion.HeaderBorrar());
            return mala;
        }

        var res = SinCache(await Http.Ok(req, new Perfil(perfil.Sub, perfil.Nombre, perfil.Foto)));
        // Deslizante: solo a mitad de vida (o tras rotar el secreto), no en cada GET. Conserva el iat
        // original: el tope de 365 días cuenta desde el login con Google, no desde la última renovación.
        if (sesion.DebeRenovar(perfil))
            res.Headers.Add("Set-Cookie", sesion.HeaderEmitir(sesion.Emitir(perfil.Sub, perfil.Nombre, perfil.Foto, perfil.Iat)));
        return res;
    }

    [Function("BorrarSesion")]
    public async Task<HttpResponseData> Borrar(
        [HttpTrigger(AuthorizationLevel.Anonymous, "delete", Route = "sesion")] HttpRequestData req)
    {
        // Sin auth e idempotente: borrar la cookie propia no necesita probar nada.
        if (!Autenticador.TieneValor(req, "X-Requested-With")) return await Csrf(req);
        var res = SinCache(req.CreateResponse(HttpStatusCode.NoContent));
        res.Headers.Add("Set-Cookie", sesion.HeaderBorrar());
        return res;
    }

    // nombre y foto viajan como null explícito: Http.Json omite los null por defecto.
    sealed record Perfil(string Sub,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Nombre,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Foto);

    static async Task<HttpResponseData> Csrf(HttpRequestData req) =>
        SinCache(await Http.JsonAsync(req, HttpStatusCode.Forbidden, new { error = "csrf" }));

    static HttpResponseData SinCache(HttpResponseData res)
    {
        res.Headers.Add("Cache-Control", "no-store");
        return res;
    }

    static string? IdToken(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("idToken", out var t) && t.ValueKind == JsonValueKind.String &&
                t.GetString() is string s && !string.IsNullOrWhiteSpace(s) ? s : null;
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }
}
