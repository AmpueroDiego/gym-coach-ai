using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GymCoach.Api.Auth;

/// <summary>
/// Lo que trae una cookie de sesión válida. Sub es el de Google, sin el prefijo "google:".
/// Iat es el login original con Google: la renovación lo conserva.
/// </summary>
public sealed record PerfilSesion(string Sub, string? Nombre, string? Foto, long Exp, long Iat, bool ConAnterior = false);

/// <summary>
/// Cookie de sesión firmada con HMAC-SHA256: <c>v1.&lt;payload&gt;.&lt;mac&gt;</c> en base64url.
/// Sin estado en el servidor (0 RU): revocar todas = rotar SESSION_SECRET.
/// </summary>
public sealed partial class SesionFirmada
{
    public const string NombreCookie = "__Host-gc_sesion";
    // Solo local sin https: el prefijo __Host- exige Secure y el navegador descartaría la cookie.
    public const string NombreCookieInseguro = "gc_sesion";
    public static readonly TimeSpan Vida = TimeSpan.FromDays(90);
    // Tope absoluto desde el login con Google: una cookie robada no se renueva para siempre.
    public static readonly TimeSpan VidaMaxima = TimeSpan.FromDays(365);

    const int MaxValor = 2048;
    const int MaxSub = 255;
    const int MaxNombreGrafemas = 60;
    // Además del tope de grafemas, bytes ya escapados en el JSON: un emoji fuera del BMP sale como
    // \uXXXX\uXXXX (12 bytes) y la cookie debe caber en 2048.
    const int MaxNombreBytes = 360;
    const int MaxFoto = 512;
    const string Version = "v1";
    // Dominio del MAC fijo, aunque el nombre de la cookie cambie en modo local.
    const string DominioMac = "gc_sesion.v1.";
    static readonly TimeSpan Renovar = TimeSpan.FromDays(45);
    static readonly TimeSpan Holgura = TimeSpan.FromMinutes(5);

    // Relajado: el payload va en base64url, nunca se renderiza; así los acentos no se inflan a \uXXXX.
    static readonly JsonSerializerOptions JsonPayload = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly byte[] _clave;
    readonly byte[]? _claveAnterior;
    readonly TimeProvider _reloj;
    readonly bool _inseguro;

    /// <param name="inseguro">Solo desarrollo local sin https: cookie sin Secure y sin prefijo __Host-.</param>
    public SesionFirmada(string? secreto, string? secretoAnterior, TimeProvider reloj, bool inseguro = false)
    {
        // Sin secreto válido no hay sesión posible: fallar al arrancar, como con GOOGLE_CLIENT_ID.
        _clave = Clave(secreto, "SESSION_SECRET");
        _claveAnterior = string.IsNullOrWhiteSpace(secretoAnterior) ? null : Clave(secretoAnterior, "SESSION_SECRET_ANTERIOR");
        _reloj = reloj;
        _inseguro = inseguro;
    }

    public string Nombre => _inseguro ? NombreCookieInseguro : NombreCookie;

    static byte[] Clave(string? secreto, string setting)
    {
        if (string.IsNullOrWhiteSpace(secreto)) throw new InvalidOperationException($"Falta {setting}.");
        byte[] clave;
        try { clave = Convert.FromBase64String(secreto.Trim()); }
        catch (FormatException) { throw new InvalidOperationException($"{setting} no es base64."); }
        if (clave.Length < 32) throw new InvalidOperationException($"{setting} debe tener al menos 32 bytes.");
        // Un relleno (todo ceros, todo 'A') tiene el largo pero no la entropía.
        if (clave.All(b => b == clave[0])) throw new InvalidOperationException($"{setting} no es aleatorio.");
        return clave;
    }

    /// <summary>Valor de la cookie para este usuario, vigente 90 días desde ahora.</summary>
    /// <param name="iat">Login original al renovar; null = login nuevo (ahora).</param>
    public string Emitir(string sub, string? nombre, string? foto, long? iat = null)
    {
        if (string.IsNullOrWhiteSpace(sub) || sub.Length > MaxSub) throw new ArgumentException("sub inválido.", nameof(sub));
        var ahora = _reloj.GetUtcNow();
        var exp = ahora.Add(Vida).ToUnixTimeSeconds();
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            sub, nombre = NombreSeguro(nombre), foto = FotoSegura(foto), exp, iat = iat ?? ahora.ToUnixTimeSeconds(),
        }, JsonPayload);
        var payload = Base64Url.EncodeToString(json);
        var valor = $"{Version}.{payload}.{Base64Url.EncodeToString(Mac(_clave, payload))}";
        // Con los topes de arriba no pasa; si pasara, Verificar la rechazaría: mejor fallar aquí.
        if (valor.Length > MaxValor) throw new InvalidOperationException("La sesión excede el tamaño máximo.");
        return valor;
    }

    /// <summary>Perfil si la cookie es auténtica y vigente; null ante cualquier fallo.</summary>
    public PerfilSesion? Verificar(string? valor) => Verificar(valor, out _);

    /// <param name="motivo">Etiqueta fija del rechazo para el log (cookie_formato, cookie_firma, cookie_vencida).</param>
    public PerfilSesion? Verificar(string? valor, out string? motivo)
    {
        motivo = "cookie_formato";
        if (string.IsNullOrEmpty(valor) || valor.Length > MaxValor) return null;
        var partes = valor.Split('.');
        if (partes.Length != 3 || partes[0] != Version) return null;

        byte[] mac;
        try { mac = Base64Url.DecodeFromChars(partes[2]); }
        catch (FormatException) { return null; }
        if (mac.Length != 32) return null;

        motivo = "cookie_firma";
        var payload = partes[1];
        bool conAnterior;
        if (CryptographicOperations.FixedTimeEquals(mac, Mac(_clave, payload))) conAnterior = false;
        else if (_claveAnterior is not null && CryptographicOperations.FixedTimeEquals(mac, Mac(_claveAnterior, payload))) conAnterior = true;
        else return null;

        // Solo con el MAC válido se toca el JSON: nada de parsear entrada no autenticada.
        motivo = "cookie_formato";
        try
        {
            using var doc = JsonDocument.Parse(Base64Url.DecodeFromChars(payload));
            var raiz = doc.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object) return null;
            if (!raiz.TryGetProperty("sub", out var s) || s.ValueKind != JsonValueKind.String) return null;
            var sub = s.GetString();
            if (string.IsNullOrWhiteSpace(sub) || sub.Length > MaxSub) return null;
            if (!raiz.TryGetProperty("exp", out var e) || e.ValueKind != JsonValueKind.Number || !e.TryGetInt64(out var exp)) return null;

            motivo = "cookie_vencida";
            var ahora = _reloj.GetUtcNow().ToUnixTimeSeconds();
            if (exp <= ahora || exp > ahora + (long)(Vida + Holgura).TotalSeconds) return null;
            // Sin iat válido no hay forma de aplicar el tope de 365 días: se trata como vencida.
            if (!raiz.TryGetProperty("iat", out var i) || i.ValueKind != JsonValueKind.Number || !i.TryGetInt64(out var iat)) return null;
            // iat <= 0 primero: con long.MinValue la resta desbordaría y pasaría el tope.
            if (iat <= 0 || iat > ahora + (long)Holgura.TotalSeconds || ahora - iat > (long)VidaMaxima.TotalSeconds) return null;

            motivo = null;
            return new PerfilSesion(sub, Texto(raiz, "nombre"), Texto(raiz, "foto"), exp, iat, conAnterior);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>Renovación deslizante: a mitad de vida, o si vino firmada con el secreto anterior.</summary>
    public bool DebeRenovar(PerfilSesion perfil) =>
        perfil.ConAnterior || perfil.Exp - _reloj.GetUtcNow().ToUnixTimeSeconds() < (long)Renovar.TotalSeconds;

    // A mano: ResponseData.Cookies no está disponible en las pruebas y no controla bien los atributos.
    public string HeaderEmitir(string valor) =>
        $"{Nombre}={valor}; Max-Age={(long)Vida.TotalSeconds}; Path=/;{(_inseguro ? "" : " Secure;")} HttpOnly; SameSite=Lax";

    public string HeaderBorrar() =>
        $"{Nombre}=; Max-Age=0; Path=/;{(_inseguro ? "" : " Secure;")} HttpOnly; SameSite=Lax";

    /// <summary>Claim name recortado, sin caracteres de control, ≤60 grafemas y ≤360 bytes escapado; vacío → null.</summary>
    public static string? NombreSeguro(string? nombre)
    {
        if (nombre is null) return null;
        var limpio = new string(nombre.Where(c => !char.IsControl(c)).ToArray()).Trim();
        var sb = new StringBuilder();
        int grafemas = 0, bytes = 0;
        var e = StringInfo.GetTextElementEnumerator(limpio);
        while (e.MoveNext() && grafemas < MaxNombreGrafemas)
        {
            var g = e.GetTextElement();
            // Un sustituto UTF-16 suelto hace lanzar al encoder: el nombre se corta ahí, la cuenta no queda sin entrar.
            int escapados;
            try { escapados = JsonEncodedText.Encode(g, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).EncodedUtf8Bytes.Length; }
            catch (ArgumentException) { break; }
            bytes += escapados;
            if (bytes > MaxNombreBytes) break;
            sb.Append(g);
            grafemas++;
        }
        var r = sb.ToString().Trim();
        return r.Length == 0 ? null : r;
    }

    /// <summary>Claim picture solo si es https a lh3-6.googleusercontent.com, sin usuario ni puerto; si no, null.</summary>
    public static string? FotoSegura(string? foto)
    {
        if (string.IsNullOrEmpty(foto) || foto.Length > MaxFoto) return null;
        // Solo ASCII imprimible, y sin " ni \ (el JSON los escaparía y la cookie podría pasar de 2048).
        if (foto.Any(c => c <= ' ' || c >= 127 || c == '"' || c == '\\')) return null;
        if (!Uri.TryCreate(foto, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0 || !uri.IsDefaultPort) return null;
        // IsDefaultPort acepta ":443" escrito: el texto debe empezar justo con esquema + host.
        if (!foto.StartsWith("https://" + uri.Host + "/", StringComparison.OrdinalIgnoreCase)) return null;
        return HostFoto().IsMatch(uri.Host) ? foto : null;
    }

    [GeneratedRegex(@"^lh[3-6]\.googleusercontent\.com$")]
    private static partial Regex HostFoto();

    static byte[] Mac(byte[] clave, string payload) =>
        HMACSHA256.HashData(clave, Encoding.ASCII.GetBytes(DominioMac + payload));

    static string? Texto(JsonElement raiz, string campo) =>
        raiz.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
