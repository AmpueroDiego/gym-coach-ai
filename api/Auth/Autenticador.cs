using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace GymCoach.Api.Auth;

public enum EstadoCookie { Ninguna, Duplicada, Presente }

/// <summary>
/// userId de una petición de datos: la cookie de sesión si viene; si no, el ID token de Google del header
/// (flujo anterior). Es la única fuente del userId: nunca se toma del body ni del query.
/// </summary>
public sealed class Autenticador(ValidadorGoogle google, SesionFirmada sesion, ILogger<Autenticador>? log = null)
{
    /// <summary>Devuelve "google:&lt;sub&gt;" o null (= 401 no_autenticado).</summary>
    public async Task<string?> UserIdAsync(HttpRequestData req, CancellationToken ct = default)
    {
        var (estado, valor) = LeerCookie(req, sesion.Nombre);
        if (estado == EstadoCookie.Ninguna) return await google.UserIdAsync(Functions.Http.Authorization(req), ct);

        // Cookie presente pero mala: no se cae al header, sería una segunda oportunidad con otra credencial.
        if (estado == EstadoCookie.Duplicada) return Rechazo("cookie_duplicada");
        var perfil = sesion.Verificar(valor, out var motivo);
        if (perfil is null) return Rechazo(motivo ?? "cookie_formato");

        // La cookie viaja sola en peticiones de otros sitios (SameSite=Lax no cubre todo): las escrituras
        // exigen un header que un formulario ajeno no puede poner.
        if (EsEscritura(req.Method) && !TieneValor(req, "X-Requested-With")) return Rechazo("csrf");

        // La cookie es de todo el navegador: una cola vieja de la cuenta A no debe escribir en B
        // tras un login de B en otra pestaña.
        if (req.Headers.TryGetValues("X-Gc-Sub", out var s) && s.FirstOrDefault() is string esperado &&
            esperado.Length > 0 && esperado != perfil.Sub)
            return Rechazo("otra_cuenta");

        return "google:" + perfil.Sub;
    }

    /// <summary>
    /// Busca la cookie <paramref name="nombre"/> en todos los headers Cookie (HTTP/2 puede mandar varios).
    /// Dos o más apariciones cuentan como Duplicada: no se elige una.
    /// </summary>
    public static (EstadoCookie Estado, string? Valor) LeerCookie(HttpRequestData req, string nombre)
    {
        if (!req.Headers.TryGetValues("Cookie", out var headers)) return (EstadoCookie.Ninguna, null);
        string? valor = null;
        var vistas = 0;
        foreach (var header in headers)
            foreach (var parte in header.Split(';'))
            {
                var p = parte.Trim();
                var igual = p.IndexOf('=');
                if (igual <= 0 || !string.Equals(p[..igual].Trim(), nombre, StringComparison.Ordinal)) continue;
                valor = p[(igual + 1)..].Trim();
                vistas++;
            }
        return vistas switch
        {
            0 => (EstadoCookie.Ninguna, null),
            1 => (EstadoCookie.Presente, valor),
            _ => (EstadoCookie.Duplicada, null),
        };
    }

    static bool EsEscritura(string metodo) => metodo.ToUpperInvariant() is "POST" or "PUT" or "PATCH" or "DELETE";

    internal static bool TieneValor(HttpRequestData req, string header) =>
        req.Headers.TryGetValues(header, out var v) && v.Any(x => !string.IsNullOrWhiteSpace(x));

    // Solo etiquetas fijas: nunca el valor de la cookie ni el sub. El log no puede cambiar el resultado.
    string? Rechazo(string motivo)
    {
        try { log?.LogWarning("Sesión rechazada: {Motivo}", motivo); }
        catch { }
        return null;
    }
}
