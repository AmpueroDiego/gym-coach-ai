using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace GymCoach.Api;

public sealed record ErrorCampo(string Campo, string Mensaje);

/// <summary>
/// Serie tal como viaja en la API (sin userId ni campos internos de Cosmos).
/// Rutina: nombre de la rutina al momento de registrarla (copia de texto, sobrevive a renombrar o borrar la rutina).
/// TipoSerie: calentamiento, fallo o drop; null = normal (así las series viejas y las normales nuevas son iguales).
/// </summary>
public sealed record Serie(string Id, string EjercicioId, decimal PesoKg, int Reps, string Fecha, string Ts, string? Rutina = null,
    string? TipoSerie = null);

/// <summary>Documento en Cosmos: se arma solo con campos validados + userId del token.</summary>
public sealed record SerieDoc(string Id, string UserId, string Tipo, string EjercicioId, decimal PesoKg, int Reps, string Fecha, string Ts,
    // Sin rutina no se guarda el campo (el serializador de Cosmos escribiría null).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Rutina = null,
    // "tipoSerie" y no "tipo": tipo ya distingue serie/rutina/record. Normal no se guarda.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TipoSerie = null)
{
    public static SerieDoc De(Serie s, string userId) =>
        new(s.Id, userId, "serie", s.EjercicioId, s.PesoKg, s.Reps, s.Fecha, s.Ts, s.Rutina, s.TipoSerie);

    public Serie ADto() => new(Id, EjercicioId, PesoKg, Reps, Fecha, Ts, Rutina, TipoSerie);
}

public static partial class Validacion
{
    public const int MaxBodyBytes = 4 * 1024;
    public const int MaxDiasRango = 92;
    public const int MaxSeriesRango = 1000;
    const int MaxTsFuturoHoras = 1;
    const string FormatoFecha = "yyyy-MM-dd";
    // ts siempre en este formato UTC para que ORDER BY c.ts (orden de strings) sea cronológico.
    const string FormatoTs = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
    static readonly DateOnly FechaMinima = new(2000, 1, 1);

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2}(\.\d{1,7})?)?(Z|[+-]\d{2}:\d{2})$")]
    private static partial Regex TsIso();

    public static bool EsUuid(string? id) => Guid.TryParseExact(id, "D", out _);

    /// <summary>Valida el body de PUT /series/{id}. ahora se inyecta para poder probar los límites de fecha y ts.</summary>
    public static (Serie? Serie, List<ErrorCampo> Errores) Serie(string id, string body, DateTimeOffset ahora)
    {
        // GetString lanza InvalidOperationException con un surrogate suelto en el JSON: es entrada mala, no un 500.
        try { return SerieInterna(id, body, ahora); }
        catch (InvalidOperationException) { return (null, [new("body", "No es JSON válido.")]); }
    }

    private static (Serie? Serie, List<ErrorCampo> Errores) SerieInterna(string id, string body, DateTimeOffset ahora)
    {
        var hoyUtc = DateOnly.FromDateTime(ahora.UtcDateTime);
        var errores = new List<ErrorCampo>();
        if (!EsUuid(id)) errores.Add(new("id", "Debe ser un UUID."));

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body, OpcionesJson); }
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

            // Tipos estrictos: "60" o 8.0 son error, no se coercionan.
            string? ejercicioId = Texto(raiz, "ejercicioId");
            if (ejercicioId is null || !Catalogo.Existe(ejercicioId))
                errores.Add(new("ejercicioId", "No existe en el catálogo."));

            decimal pesoKg = 0;
            if (!raiz.TryGetProperty("pesoKg", out var p)) errores.Add(new("pesoKg", "Debe ser un número."));
            else if (ErrorPeso(p, out pesoKg) is string ep) errores.Add(new("pesoKg", ep));

            int reps = 0;
            if (ErrorEntero(raiz, "reps", 1, 100, out reps) is string er) errores.Add(new("reps", er));

            // Opcional: ausente o null = serie sin rutina.
            string? rutina = null;
            if (raiz.TryGetProperty("rutina", out var ru) && ru.ValueKind != JsonValueKind.Null &&
                ErrorNombre(ru, out rutina) is string eru)
                errores.Add(new("rutina", eru));

            // Opcional: ausente, null o "normal" = serie normal (se guarda como null). Sin coerción de mayúsculas.
            string? tipoSerie = null;
            if (raiz.TryGetProperty("tipoSerie", out var tse) && tse.ValueKind != JsonValueKind.Null)
            {
                var valor = tse.ValueKind == JsonValueKind.String ? tse.GetString() : null;
                if (valor is not ("calentamiento" or "normal" or "fallo" or "drop"))
                    errores.Add(new("tipoSerie", "Debe ser calentamiento, normal, fallo o drop."));
                else if (valor != "normal") tipoSerie = valor;
            }

            string? fecha = Texto(raiz, "fecha");
            // +1 día cubre clientes adelantados respecto a UTC.
            var fechaValida = TryFecha(fecha, out var f) && f >= FechaMinima && f <= hoyUtc.AddDays(1);
            if (!fechaValida)
                errores.Add(new("fecha", "Debe ser YYYY-MM-DD entre 2000-01-01 y mañana."));

            string? ts = Texto(raiz, "ts");
            string tsNormal = "";
            if (ts is null || !TsIso().IsMatch(ts) ||
                !DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
                errores.Add(new("ts", "Debe ser fecha-hora ISO 8601 con zona."));
            else if (t > ahora.AddHours(MaxTsFuturoHoras))
                errores.Add(new("ts", "No puede estar en el futuro."));
            // fecha es el día local del cliente: con offsets entre -12h y +14h, ese día es el día UTC de ts
            // o uno de sus vecinos. Más de un día de diferencia es una fecha que no corresponde al ts.
            else if (fechaValida && Math.Abs(f.DayNumber - DateOnly.FromDateTime(t.UtcDateTime).DayNumber) > 1)
                errores.Add(new("ts", "No corresponde a la fecha."));
            else
                tsNormal = t.UtcDateTime.ToString(FormatoTs, CultureInfo.InvariantCulture);

            if (errores.Count > 0) return (null, errores);
            // Guid normalizado a minúsculas para que el mismo id no genere dos documentos.
            return (new Serie(Guid.Parse(id).ToString("D"), ejercicioId!, pesoKg, reps, fecha!, tsNormal, rutina, tipoSerie), errores);
        }
    }

    /// <summary>Valida desde/hasta de GET /series (ambos obligatorios, desde ≤ hasta, ≤ 92 días).</summary>
    public static (DateOnly Desde, DateOnly Hasta, List<ErrorCampo> Errores) Rango(string? desde, string? hasta)
    {
        var errores = new List<ErrorCampo>();
        if (!TryFecha(desde, out var d)) errores.Add(new("desde", "Obligatorio, formato YYYY-MM-DD."));
        if (!TryFecha(hasta, out var h)) errores.Add(new("hasta", "Obligatorio, formato YYYY-MM-DD."));
        if (errores.Count == 0)
        {
            if (d > h) errores.Add(new("desde", "Debe ser menor o igual a hasta."));
            else if (h.DayNumber - d.DayNumber + 1 > MaxDiasRango)
                errores.Add(new("hasta", $"El rango no puede superar {MaxDiasRango} días."));
        }
        return (d, h, errores);
    }

    /// <summary>limite del historial: 1–50, 20 si no viene.</summary>
    public static (int Limite, List<ErrorCampo> Errores) Limite(string? valor)
    {
        var errores = new List<ErrorCampo>();
        if (string.IsNullOrEmpty(valor)) return (20, errores);
        if (!int.TryParse(valor, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > 50)
        {
            errores.Add(new("limite", "Debe ser un entero entre 1 y 50."));
            return (0, errores);
        }
        return (n, errores);
    }

    /// <summary>pesoKg: número 0–500 con máximo 2 decimales. Devuelve el mensaje de error o null.</summary>
    static string? ErrorPeso(JsonElement p, out decimal pesoKg)
    {
        pesoKg = 0;
        // Tipos estrictos: "60" es error, no se coerciona.
        if (p.ValueKind != JsonValueKind.Number || !p.TryGetDecimal(out pesoKg)) return "Debe ser un número.";
        if (pesoKg < 0 || pesoKg > 500) return "Debe estar entre 0 y 500.";
        if (decimal.Round(pesoKg, 2) != pesoKg) return "Máximo 2 decimales.";
        return null;
    }

    /// <summary>Entero estricto en [min, max]: 8.0, "8" o null son error.</summary>
    static string? ErrorEntero(JsonElement obj, string campo, int min, int max, out int valor)
    {
        valor = 0;
        if (!obj.TryGetProperty(campo, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out valor))
            return "Debe ser un entero.";
        return valor < min || valor > max ? $"Debe estar entre {min} y {max}." : null;
    }

    // Cc (control), Co (uso privado), Cn (sin asignar). Sin Cs ni Cf: rechazarían emojis (pares surrogados, ZWJ).
    // Claves repetidas (nombre dos veces) son entrada ambigua: se rechazan.
    internal static readonly JsonDocumentOptions OpcionesJson = new() { AllowDuplicateProperties = false };

    // Control, uso privado y sin asignar; más marcas bidi y espacios de ancho cero (spoofing visual). ZWJ y VS16 quedan: forman emojis.
    [GeneratedRegex(@"[\p{Cc}\p{Co}\p{Cn}\u200B\u200E\u200F\u202A-\u202E\u2066-\u2069\uFEFF]")]
    private static partial Regex CaracterProhibido();

    /// <summary>Nombre corto (rutina): string, trim, 1–40, sin caracteres de control.</summary>
    internal static string? ErrorNombre(JsonElement v, out string nombre)
    {
        nombre = "";
        if (v.ValueKind != JsonValueKind.String) return "Debe ser texto.";
        nombre = v.GetString()!.Trim();
        if (nombre.Length < 1 || nombre.Length > MaxNombreRutina) return $"Debe tener entre 1 y {MaxNombreRutina} caracteres.";
        if (CaracterProhibido().IsMatch(nombre)) return "No puede tener caracteres de control.";
        return null;
    }

    static string? Texto(JsonElement raiz, string campo) =>
        raiz.TryGetProperty(campo, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool TryFecha(string? s, out DateOnly fecha) =>
        DateOnly.TryParseExact(s, FormatoFecha, CultureInfo.InvariantCulture, DateTimeStyles.None, out fecha);
}
