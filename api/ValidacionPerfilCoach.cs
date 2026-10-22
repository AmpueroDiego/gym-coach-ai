using System.Text.Json;

namespace GymCoach.Api;

public static partial class Validacion
{
    public const int MaxBodyPerfilBytes = 8 * 1024;
    public const int MaxNotasPerfil = 10;
    public const int MaxTextoPerfil = 200;
    public const int MinMinutosPerfil = 10;
    public const int MaxMinutosPerfil = 180;
    public const int MinAlturaCm = 100;
    public const int MaxAlturaCm = 250;
    public const decimal MinPesoCorporalKg = 30;
    public const decimal MaxPesoCorporalKg = 300;
    // Comparación ordinal: "Maquina" no vale.
    public static readonly IReadOnlyList<string> EquiposPerfil = ["maquina", "libre", "mixto"];
    // Comparación ordinal: "Nunca" no vale.
    public static readonly IReadOnlyList<string> ExperienciasPerfil = ["nunca", "menos6m", "mas6m"];
    public const int MinDiasPerfil = 1;
    public const int MaxDiasPerfil = 7;

    /// <summary>
    /// Valida el body de PUT /coach/perfil (perfil completo). Campos desconocidos (userId incluido) se ignoran.
    /// TraeAltura/TraePeso/TraeEquipo/TraeExperiencia/TraeDias: si la clave venía (aunque sea null); ausente = conservar lo guardado (PerfilesCoach.GuardarAsync).
    /// </summary>
    public static (PerfilCoach? Perfil, List<ErrorCampo> Errores, bool TraeAltura, bool TraePeso, bool TraeEquipo, bool TraeExperiencia,
        bool TraeDias) PerfilCoach(string body)
    {
        // GetString lanza InvalidOperationException con un surrogate suelto en el JSON: es entrada mala, no un 500.
        try
        {
            using var doc = JsonDocument.Parse(body, OpcionesJson);
            return PerfilDe(doc.RootElement);
        }
        catch (JsonException) { return (null, [new("body", "No es JSON válido.")], false, false, false, false, false); }
        catch (InvalidOperationException) { return (null, [new("body", "No es JSON válido.")], false, false, false, false, false); }
    }

    /// <summary>
    /// Perfil desde el body del PUT. minutosDisponibles, objetivo, alturaCm, pesoCorporalKg, equipo, experiencia y
    /// diasPorSemana: ausente = null en el perfil; los Trae* dicen si la clave venía.
    /// </summary>
    internal static (PerfilCoach? Perfil, List<ErrorCampo> Errores, bool TraeAltura, bool TraePeso, bool TraeEquipo, bool TraeExperiencia,
        bool TraeDias) PerfilDe(JsonElement raiz)
    {
        var errores = new List<ErrorCampo>();
        if (raiz.ValueKind != JsonValueKind.Object) return (null, [new("body", "Debe ser un objeto JSON.")], false, false, false, false, false);

        int? minutos = null;
        if (raiz.TryGetProperty("minutosDisponibles", out var m) && m.ValueKind != JsonValueKind.Null)
        {
            if (ErrorMinutosPerfil(raiz, "minutosDisponibles", out var valor) is string em) errores.Add(new("minutosDisponibles", em));
            else minutos = valor;
        }

        string? objetivo = null;
        if (raiz.TryGetProperty("objetivo", out var o) && o.ValueKind != JsonValueKind.Null)
        {
            if (ErrorTextoPerfil(o, out var valor) is string eo) errores.Add(new("objetivo", eo));
            else objetivo = valor;
        }

        int? altura = null;
        var traeAltura = raiz.TryGetProperty("alturaCm", out var a);
        if (traeAltura && a.ValueKind != JsonValueKind.Null)
        {
            if (ErrorAlturaPerfil(raiz, "alturaCm", out var valor) is string ea) errores.Add(new("alturaCm", ea));
            else altura = valor;
        }

        decimal? pesoCorporal = null;
        var traePeso = raiz.TryGetProperty("pesoCorporalKg", out var p);
        if (traePeso && p.ValueKind != JsonValueKind.Null)
        {
            if (ErrorPesoCorporal(p, out var valor) is string ep) errores.Add(new("pesoCorporalKg", ep));
            else pesoCorporal = valor;
        }

        string? equipo = null;
        var traeEquipo = raiz.TryGetProperty("equipo", out var eq);
        if (traeEquipo)
        {
            if (ErrorEquipoPerfil(eq, out var valor) is string ee) errores.Add(new("equipo", ee));
            else equipo = valor;
        }

        string? experiencia = null;
        var traeExperiencia = raiz.TryGetProperty("experiencia", out var ex);
        if (traeExperiencia)
        {
            if (ErrorExperienciaPerfil(ex, out var valor) is string eex) errores.Add(new("experiencia", eex));
            else experiencia = valor;
        }

        int? dias = null;
        var traeDias = raiz.TryGetProperty("diasPorSemana", out var d);
        if (traeDias && d.ValueKind != JsonValueKind.Null)
        {
            if (ErrorDiasPerfil(raiz, "diasPorSemana", out var valor) is string ed) errores.Add(new("diasPorSemana", ed));
            else dias = valor;
        }

        var notas = new List<string>();
        if (!raiz.TryGetProperty("notas", out var n) || n.ValueKind != JsonValueKind.Array)
            errores.Add(new("notas", "Debe ser una lista."));
        else if (n.GetArrayLength() > MaxNotasPerfil)
            errores.Add(new("notas", $"Máximo {MaxNotasPerfil} notas."));
        else
        {
            var i = 0;
            foreach (var nota in n.EnumerateArray())
            {
                if (ErrorTextoPerfil(nota, out var valor) is string en) errores.Add(new($"notas[{i}]", en));
                else notas.Add(valor);
                i++;
            }
        }

        return errores.Count > 0
            ? (null, errores, traeAltura, traePeso, traeEquipo, traeExperiencia, traeDias)
            : (new PerfilCoach(minutos, objetivo, notas, altura, pesoCorporal, equipo, experiencia, dias), errores, traeAltura, traePeso,
                traeEquipo, traeExperiencia, traeDias);
    }

    /// <summary>
    /// Perfil guardado en Cosmos: tolerante campo por campo. Una nota inválida se descarta sola (las de lesión no se
    /// pierden por una mala); minutos, objetivo, altura, peso, equipo, experiencia o días inválidos pasan a null, igual que ausentes (perfiles
    /// anteriores a esos campos). Nunca falla.
    /// </summary>
    internal static PerfilCoach PerfilGuardado(JsonElement raiz)
    {
        if (raiz.ValueKind != JsonValueKind.Object) return global::GymCoach.Api.PerfilCoach.PorDefecto;

        int? minutos = raiz.TryGetProperty("minutosDisponibles", out var m) && m.ValueKind != JsonValueKind.Null &&
            ErrorMinutosPerfil(raiz, "minutosDisponibles", out var vm) is null ? vm : null;
        string? objetivo = raiz.TryGetProperty("objetivo", out var o) && ErrorTextoPerfil(o, out var vo) is null ? vo : null;
        int? altura = raiz.TryGetProperty("alturaCm", out var a) && a.ValueKind != JsonValueKind.Null &&
            ErrorAlturaPerfil(raiz, "alturaCm", out var va) is null ? va : null;
        decimal? pesoCorporal = raiz.TryGetProperty("pesoCorporalKg", out var p) && ErrorPesoCorporal(p, out var vp) is null ? vp : null;
        string? equipo = raiz.TryGetProperty("equipo", out var eq) && ErrorEquipoPerfil(eq, out var ve) is null ? ve : null;
        string? experiencia = raiz.TryGetProperty("experiencia", out var ex) && ErrorExperienciaPerfil(ex, out var vex) is null ? vex : null;
        int? dias = raiz.TryGetProperty("diasPorSemana", out var d) && d.ValueKind != JsonValueKind.Null &&
            ErrorDiasPerfil(raiz, "diasPorSemana", out var vd) is null ? vd : null;

        var notas = new List<string>();
        if (raiz.TryGetProperty("notas", out var n) && n.ValueKind == JsonValueKind.Array)
            foreach (var nota in n.EnumerateArray())
                if (notas.Count < MaxNotasPerfil && ErrorTextoPerfil(nota, out var vn) is null) notas.Add(vn);

        return new PerfilCoach(minutos, objetivo, notas, altura, pesoCorporal, equipo, experiencia, dias);
    }

    /// <summary>Equipo del perfil: "maquina" | "libre" | "mixto" (ordinal) o null (lo deduce del historial). PUT y recordar.</summary>
    internal static string? ErrorEquipoPerfil(JsonElement v, out string? equipo)
    {
        equipo = null;
        if (v.ValueKind == JsonValueKind.Null) return null;
        // ValueEquals compara ordinal y no decodifica: un surrogate suelto no lanza.
        equipo = v.ValueKind == JsonValueKind.String ? EquiposPerfil.FirstOrDefault(e => v.ValueEquals(e)) : null;
        return equipo is null ? "Debe ser maquina, libre, mixto o null." : null;
    }

    /// <summary>Experiencia: "nunca" | "menos6m" | "mas6m" (ordinal) o null (no se sabe). PUT y recordar.</summary>
    internal static string? ErrorExperienciaPerfil(JsonElement v, out string? experiencia)
    {
        experiencia = null;
        if (v.ValueKind == JsonValueKind.Null) return null;
        experiencia = v.ValueKind == JsonValueKind.String ? ExperienciasPerfil.FirstOrDefault(e => v.ValueEquals(e)) : null;
        return experiencia is null ? "Debe ser nunca, menos6m, mas6m o null." : null;
    }

    /// <summary>Días por semana: entero estricto 1–7 ("3" es error). PUT y recordar.</summary>
    internal static string? ErrorDiasPerfil(JsonElement obj, string campo, out int valor) =>
        ErrorEntero(obj, campo, MinDiasPerfil, MaxDiasPerfil, out valor);

    /// <summary>Minutos del perfil: entero estricto 10–180. Lo usan el PUT y la herramienta recordar.</summary>
    internal static string? ErrorMinutosPerfil(JsonElement obj, string campo, out int valor) =>
        ErrorEntero(obj, campo, MinMinutosPerfil, MaxMinutosPerfil, out valor);

    /// <summary>Altura del perfil: entero estricto 100–250 cm. Lo usan el PUT y la herramienta recordar.</summary>
    internal static string? ErrorAlturaPerfil(JsonElement obj, string campo, out int valor) =>
        ErrorEntero(obj, campo, MinAlturaCm, MaxAlturaCm, out valor);

    /// <summary>Peso corporal: número 30–300 kg con máximo 1 decimal; "78" es error, no se coerciona. PUT y recordar.</summary>
    internal static string? ErrorPesoCorporal(JsonElement v, out decimal peso)
    {
        peso = 0;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetDecimal(out peso)) return "Debe ser un número.";
        if (peso < MinPesoCorporalKg || peso > MaxPesoCorporalKg) return $"Debe estar entre {MinPesoCorporalKg} y {MaxPesoCorporalKg}.";
        if (decimal.Round(peso, 1) != peso) return "Máximo 1 decimal.";
        // 78.50 se guarda como 78.5.
        peso = decimal.Round(peso, 1);
        return null;
    }

    /// <summary>
    /// Objetivo o nota: string, trim, 1–200, sin caracteres de control (tampoco saltos de línea: va al prompt del coach).
    /// Mismas reglas en el PUT y en recordar, así un perfil escrito por el coach siempre se puede volver a guardar.
    /// </summary>
    internal static string? ErrorTextoPerfil(JsonElement v, out string texto)
    {
        texto = "";
        if (v.ValueKind != JsonValueKind.String) return "Debe ser texto.";
        texto = v.GetString()!.Trim();
        if (texto.Length < 1 || texto.Length > MaxTextoPerfil) return $"Debe tener entre 1 y {MaxTextoPerfil} caracteres.";
        if (CaracterProhibido().IsMatch(texto)) return "No puede tener caracteres de control.";
        return null;
    }
}
