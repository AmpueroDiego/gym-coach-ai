using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GymCoach.Api;

/// <summary>Mensaje del historial que manda la PWA. Rol: usuario | coach.</summary>
public sealed record MensajeEntrada(string Rol, string Texto);

/// <summary>OffsetMin: minutos de la zona del cliente (-getTimezoneOffset()); null si no vino. Idioma: es | en.</summary>
public sealed record PeticionCoach(IReadOnlyList<MensajeEntrada> Mensajes, DateOnly Hoy, int? OffsetMin = null, string Idioma = "es");

/// <summary>Valida el body de POST /api/coach. Clase aparte: su tope de body (64 KB) no es el de Validacion.</summary>
public static partial class ValidacionCoach
{
    public const int MaxBodyBytes = 64 * 1024;
    public const int MaxMensajes = 10;
    public const int MaxTextoUsuario = 1000;
    public const int MaxTextoCoach = 2000;
    // Zonas horarias reales: UTC-12 a UTC+14.
    public const int MinOffsetMin = -720;
    public const int MaxOffsetMin = 840;

    // Lo mismo que rechaza Validacion (control, uso privado, sin asignar, bidi, ancho cero) salvo \t \n \r:
    // los mensajes del chat sí pueden tener varias líneas.
    [GeneratedRegex(@"[\p{Cc}\p{Co}\p{Cn}\u200B\u200E\u200F\u202A-\u202E\u2066-\u2069\uFEFF-[\t\n\r]]")]
    private static partial Regex CaracterProhibido();

    /// <summary>ahora fija el día UTC del servidor: hoy del cliente puede diferir en ±1 día (zona horaria).</summary>
    public static (PeticionCoach? Peticion, List<ErrorCampo> Errores) Peticion(string body, DateTimeOffset ahora)
    {
        // GetString lanza InvalidOperationException con un surrogate suelto en el JSON: es entrada mala, no un 500.
        try { return PeticionInterna(body, ahora); }
        catch (InvalidOperationException) { return (null, [new("body", "No es JSON válido.")]); }
    }

    static (PeticionCoach?, List<ErrorCampo>) PeticionInterna(string body, DateTimeOffset ahora)
    {
        var errores = new List<ErrorCampo>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(body, Validacion.OpcionesJson); }
        catch (JsonException)
        {
            errores.Add(new("body", "No es JSON válido."));
            return (null, errores);
        }

        using (doc)
        {
            var raiz = doc.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object)
            {
                errores.Add(new("body", "Debe ser un objeto JSON."));
                return (null, errores);
            }

            // Propiedades desconocidas (incluido userId) se ignoran: el usuario sale del token.
            var hoyUtc = DateOnly.FromDateTime(ahora.UtcDateTime);
            var hoy = hoyUtc;
            if (raiz.TryGetProperty("hoy", out var h) && h.ValueKind != JsonValueKind.Null)
            {
                if (h.ValueKind != JsonValueKind.String ||
                    !DateOnly.TryParseExact(h.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out hoy) ||
                    Math.Abs(hoy.DayNumber - hoyUtc.DayNumber) > 1)
                    errores.Add(new("hoy", "Debe ser YYYY-MM-DD a un día o menos de hoy."));
            }

            int? offsetMin = null;
            if (raiz.TryGetProperty("offsetMin", out var o) && o.ValueKind != JsonValueKind.Null)
            {
                // Entero estricto: "-360" o -360.5 no se coercionan.
                if (o.ValueKind != JsonValueKind.Number || !o.TryGetInt32(out var valor) || valor < MinOffsetMin || valor > MaxOffsetMin)
                    errores.Add(new("offsetMin", $"Debe ser un entero entre {MinOffsetMin} y {MaxOffsetMin}."));
                else offsetMin = valor;
            }

            var idioma = Idioma(raiz, errores);

            var mensajes = new List<MensajeEntrada>();
            if (!raiz.TryGetProperty("mensajes", out var ms) || ms.ValueKind != JsonValueKind.Array)
                errores.Add(new("mensajes", "Debe ser una lista."));
            else if (ms.GetArrayLength() < 1 || ms.GetArrayLength() > MaxMensajes)
                errores.Add(new("mensajes", $"Debe tener entre 1 y {MaxMensajes} mensajes."));
            else
            {
                var i = 0;
                foreach (var m in ms.EnumerateArray())
                {
                    var campo = $"mensajes[{i++}]";
                    if (m.ValueKind != JsonValueKind.Object)
                    {
                        errores.Add(new(campo, "Debe ser un objeto."));
                        continue;
                    }
                    var rol = m.TryGetProperty("rol", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                    if (rol is not ("usuario" or "coach"))
                    {
                        errores.Add(new(campo + ".rol", "Debe ser usuario o coach."));
                        continue;
                    }
                    var max = rol == "usuario" ? MaxTextoUsuario : MaxTextoCoach;
                    if (!m.TryGetProperty("texto", out var t) || t.ValueKind != JsonValueKind.String)
                        errores.Add(new(campo + ".texto", "Debe ser texto."));
                    else if (t.GetString()!.Trim() is var texto && (texto.Length < 1 || texto.Length > max))
                        errores.Add(new(campo + ".texto", $"Debe tener entre 1 y {max} caracteres."));
                    else if (CaracterProhibido().IsMatch(texto))
                        errores.Add(new(campo + ".texto", "No puede tener caracteres de control."));
                    else
                        mensajes.Add(new(rol, texto));
                }
                // El modelo responde al último mensaje: tiene que ser del usuario.
                if (ms[ms.GetArrayLength() - 1] is var ultimo && !(ultimo.ValueKind == JsonValueKind.Object &&
                    ultimo.TryGetProperty("rol", out var ru) && ru.ValueKind == JsonValueKind.String && ru.GetString() == "usuario"))
                    errores.Add(new("mensajes", "El último mensaje debe ser del usuario."));
            }

            if (errores.Count > 0) return (null, errores);
            return (new PeticionCoach(mensajes, hoy, offsetMin, idioma), errores);
        }
    }

    /// <summary>
    /// Campo opcional idioma de POST /coach y /coach/plan-dia: ausente o null es "es". Estricto: "EN", "fr" o 1 son 400
    /// (llega al prompt y a la decisión de regenerar el plan).
    /// </summary>
    internal static string Idioma(JsonElement raiz, List<ErrorCampo> errores)
    {
        if (!raiz.TryGetProperty("idioma", out var i) || i.ValueKind == JsonValueKind.Null) return "es";
        if (i.ValueKind == JsonValueKind.String && i.GetString() is "es" or "en") return i.GetString()!;
        errores.Add(new("idioma", "Debe ser es o en."));
        return "es";
    }
}
