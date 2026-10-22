using System.Text.Json.Serialization;

namespace GymCoach.Api;

public sealed record MarcaPeso(decimal PesoKg, int Reps, string Fecha);
public sealed record MarcaE1rm(decimal Kg, decimal PesoKg, int Reps, string Fecha);
public sealed record MarcaReps(int Reps, decimal PesoKg, string Fecha);

/// <summary>
/// Récords de un ejercicio tal como viajan en la API. RepsMax existe porque en carga corporal pesoKg es
/// el lastre: con lastre 0 nunca habría récord de peso ni de e1rm.
/// </summary>
public sealed record RecordEjercicio(string EjercicioId, MarcaPeso PesoMax,
    // Siempre se escribe (también como null): el contrato es objeto | null, no campo opcional.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] MarcaE1rm? E1rm,
    MarcaReps RepsMax);

/// <summary>
/// Documento en Cosmos, en el contenedor de series. Igual que las rutinas, el prefijo "record:" hace que
/// PUT/DELETE /series/{id} (que exigen un UUID pelado) nunca lo alcancen. Uno por ejercicio del catálogo.
/// Desactualizado: falló su mantenimiento y sus valores no son confiables hasta recalcularlo.
/// ExcedeTope: el ejercicio tiene más series de las que se recalculan; ya no se intenta (solo se suman mejoras).
/// </summary>
public sealed record RecordDoc(string Id, string UserId, string Tipo, string EjercicioId, MarcaPeso PesoMax,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] MarcaE1rm? E1rm,
    MarcaReps RepsMax, string Actualizado,
    // Solo se escribe en true: el reemplazo de un recálculo lo borra.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Desactualizado = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool ExcedeTope = false)
{
    public const string Prefijo = "record:";

    public static string IdDoc(string ejercicioId) => Prefijo + ejercicioId;

    public static RecordDoc De(RecordEjercicio r, string userId, string actualizado) =>
        new(IdDoc(r.EjercicioId), userId, "record", r.EjercicioId, r.PesoMax, r.E1rm, r.RepsMax, actualizado);

    public RecordEjercicio ADto() => new(EjercicioId, PesoMax, E1rm, RepsMax);

    /// <summary>
    /// Con todas sus marcas completas. Un doc editado a mano o de otro esquema llega con nulls aunque los tipos
    /// digan lo contrario (System.Text.Json no los rechaza): se trata como "hay que recalcular", nunca se usa.
    /// </summary>
    public bool EsValido(string ejercicioId) =>
        Tipo == "record" && EjercicioId == ejercicioId && PesoMax is { Fecha: not null } && RepsMax is { Fecha: not null } &&
        E1rm is null or { Fecha: not null };
}

/// <summary>Una sola regla de récords; el cliente TS usa la misma.</summary>
public static class CalculoRecords
{
    const int MaxRepsE1rm = 12;

    /// <summary>
    /// 1RM estimado (Epley): con 1 rep es el peso; si no, peso * (30 + reps) / 30 redondeado a 2 decimales.
    /// null con más de 12 reps (la fórmula ya no es fiable) o sin peso.
    /// </summary>
    public static decimal? E1rm(decimal pesoKg, int reps)
    {
        if (reps > MaxRepsE1rm || pesoKg == 0) return null;
        if (reps == 1) return pesoKg;
        return Math.Round(pesoKg * (30 + reps) / 30, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>El calentamiento no cuenta para récords; normal (null), fallo y drop sí.</summary>
    public static bool Cuenta(Serie s) => s.TipoSerie != "calentamiento";

    /// <summary>
    /// Récord tras sumar la serie (del mismo ejercicio), o null si no lo mejora. Solo un valor
    /// estrictamente mayor bate: con empate se queda la marca que ya estaba.
    /// </summary>
    public static RecordEjercicio? Fusionar(RecordEjercicio? actual, Serie s)
    {
        if (!Cuenta(s)) return null;
        var e1rm = E1rm(s.PesoKg, s.Reps);
        var marcaE1rm = e1rm is decimal kg ? new MarcaE1rm(kg, s.PesoKg, s.Reps, s.Fecha) : null;
        if (actual is null)
            return new(s.EjercicioId, new(s.PesoKg, s.Reps, s.Fecha), marcaE1rm, new(s.Reps, s.PesoKg, s.Fecha));

        var mejoraPeso = s.PesoKg > actual.PesoMax.PesoKg;
        var mejoraE1rm = e1rm is decimal k && (actual.E1rm is null || k > actual.E1rm.Kg);
        var mejoraReps = s.Reps > actual.RepsMax.Reps;
        if (!mejoraPeso && !mejoraE1rm && !mejoraReps) return null;

        return actual with
        {
            PesoMax = mejoraPeso ? new(s.PesoKg, s.Reps, s.Fecha) : actual.PesoMax,
            E1rm = mejoraE1rm ? marcaE1rm : actual.E1rm,
            RepsMax = mejoraReps ? new(s.Reps, s.PesoKg, s.Fecha) : actual.RepsMax,
        };
    }

    /// <summary>Récord desde cero; las series deben venir en orden cronológico para que el empate quede en la primera.</summary>
    public static RecordEjercicio? Desde(IEnumerable<Serie> series, string ejercicioId)
    {
        RecordEjercicio? r = null;
        foreach (var s in series)
            if (s.EjercicioId == ejercicioId && Fusionar(r, s) is RecordEjercicio nuevo)
                r = nuevo;
        return r;
    }

    /// <summary>
    /// La serie podría ser la dueña de alguna marca del récord (iguala algún valor). Si se borra o se
    /// baja, hay que recalcular; si no, el récord no cambia.
    /// </summary>
    public static bool PodriaSerDuena(RecordEjercicio r, Serie s)
    {
        if (!Cuenta(s) || s.EjercicioId != r.EjercicioId) return false;
        return s.PesoKg >= r.PesoMax.PesoKg || s.Reps >= r.RepsMax.Reps ||
            (E1rm(s.PesoKg, s.Reps) is decimal e && r.E1rm is not null && e >= r.E1rm.Kg);
    }

    /// <summary>
    /// La edición cambia algo con lo que la serie vieja podía sostener un récord: cambia de ejercicio,
    /// pasa a calentamiento, baja peso o reps, se queda sin e1rm (más de 12 reps), o cambia fecha o ts
    /// (la marca guarda la fecha y el ts decide los empates). Subir valores no: eso lo resuelve Fusionar.
    /// </summary>
    public static bool Empeora(Serie vieja, Serie nueva) =>
        Cuenta(vieja) && (vieja.EjercicioId != nueva.EjercicioId || !Cuenta(nueva) ||
            nueva.PesoKg < vieja.PesoKg || nueva.Reps < vieja.Reps ||
            (E1rm(vieja.PesoKg, vieja.Reps) is not null && E1rm(nueva.PesoKg, nueva.Reps) is null) ||
            nueva.Fecha != vieja.Fecha || nueva.Ts != vieja.Ts);
}
