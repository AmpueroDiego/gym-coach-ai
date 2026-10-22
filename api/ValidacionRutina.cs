using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GymCoach.Api;

/// <summary>Un ejercicio dentro de una rutina, con lo planeado. PesoKg null = sin peso objetivo.</summary>
public sealed record EjercicioRutina(string Id, int Series, int Reps,
    // Siempre se escribe (también como null): el contrato es number | null, no campo opcional.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? PesoKg,
    int DescansoS);

/// <summary>Rutina tal como viaja en la API: Id es el UUID pelado, sin userId ni tipo.</summary>
public sealed record Rutina(string Id, string Nombre, IReadOnlyList<EjercicioRutina> Ejercicios, string Actualizada);

/// <summary>
/// Documento en Cosmos, en el mismo contenedor que las series. El id lleva prefijo "rutina:" para que
/// nunca choque con el de una serie ni lo alcancen PUT/DELETE /series/{id} (que exigen un UUID pelado).
/// </summary>
public sealed record RutinaDoc(string Id, string UserId, string Tipo, string Nombre, IReadOnlyList<EjercicioRutina> Ejercicios, string Actualizada)
{
    public const string Prefijo = "rutina:";

    public static string IdDoc(string uuid) => Prefijo + uuid;

    public static RutinaDoc De(Rutina r, string userId) =>
        new(IdDoc(r.Id), userId, "rutina", r.Nombre, r.Ejercicios, r.Actualizada);

    public Rutina ADto() => new(Id[Prefijo.Length..], Nombre, Ejercicios, Actualizada);
}

public static partial class Validacion
{
    public const int MaxRutinas = 20;
    public const int MaxEjerciciosRutina = 15;
    public const int MaxNombreRutina = 40;

    /// <summary>Valida el body de PUT /rutinas/{id}. ahora fija "actualizada".</summary>
    public static (Rutina? Rutina, List<ErrorCampo> Errores) Rutina(string id, string body, DateTimeOffset ahora)
    {
        // GetString lanza InvalidOperationException con un surrogate suelto en el JSON: es entrada mala, no un 500.
        try { return RutinaInterna(id, body, ahora); }
        catch (InvalidOperationException) { return (null, [new("body", "No es JSON válido.")]); }
    }

    private static (Rutina? Rutina, List<ErrorCampo> Errores) RutinaInterna(string id, string body, DateTimeOffset ahora)
    {
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

            string nombre = "";
            if (!raiz.TryGetProperty("nombre", out var n)) errores.Add(new("nombre", "Debe ser texto."));
            else if (ErrorNombre(n, out nombre) is string en) errores.Add(new("nombre", en));

            var ejercicios = new List<EjercicioRutina>();
            if (!raiz.TryGetProperty("ejercicios", out var ej) || ej.ValueKind != JsonValueKind.Array)
                errores.Add(new("ejercicios", "Debe ser una lista."));
            else if (ej.GetArrayLength() < 1 || ej.GetArrayLength() > MaxEjerciciosRutina)
                errores.Add(new("ejercicios", $"Debe tener entre 1 y {MaxEjerciciosRutina} ejercicios."));
            else
            {
                var vistos = new HashSet<string>(StringComparer.Ordinal);
                var i = 0;
                foreach (var e in ej.EnumerateArray())
                {
                    if (EjercicioDeRutina(e, $"ejercicios[{i}]", errores) is EjercicioRutina er)
                    {
                        if (vistos.Add(er.Id)) ejercicios.Add(er);
                        else errores.Add(new($"ejercicios[{i}].id", "Ejercicio repetido."));
                    }
                    i++;
                }
            }

            if (errores.Count > 0) return (null, errores);
            var actualizada = ahora.UtcDateTime.ToString(FormatoTs, CultureInfo.InvariantCulture);
            // Guid normalizado a minúsculas para que el mismo id no genere dos documentos.
            return (new Rutina(Guid.Parse(id).ToString("D"), nombre, ejercicios, actualizada), errores);
        }
    }

    /// <summary>Un ejercicio de la rutina; null si tiene errores (ya agregados con el prefijo del índice).</summary>
    static EjercicioRutina? EjercicioDeRutina(JsonElement e, string campo, List<ErrorCampo> errores)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            errores.Add(new(campo, "Debe ser un objeto."));
            return null;
        }

        var antes = errores.Count;
        var id = Texto(e, "id");
        if (id is null || !Catalogo.Existe(id)) errores.Add(new($"{campo}.id", "No existe en el catálogo."));
        if (ErrorEntero(e, "series", 1, 10, out var series) is string es) errores.Add(new($"{campo}.series", es));
        if (ErrorEntero(e, "reps", 1, 100, out var reps) is string er) errores.Add(new($"{campo}.reps", er));

        // Ausente o null = sin peso objetivo.
        decimal? pesoKg = null;
        if (e.TryGetProperty("pesoKg", out var p) && p.ValueKind != JsonValueKind.Null)
        {
            if (ErrorPeso(p, out var peso) is string ep) errores.Add(new($"{campo}.pesoKg", ep));
            else pesoKg = peso;
        }

        // 0 = sin descanso.
        if (ErrorEntero(e, "descansoS", 0, 600, out var descansoS) is string ed) errores.Add(new($"{campo}.descansoS", ed));

        return errores.Count > antes ? null : new EjercicioRutina(id!, series, reps, pesoKg, descansoS);
    }
}
