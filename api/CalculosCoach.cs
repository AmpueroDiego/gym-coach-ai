using System.Globalization;

namespace GymCoach.Api;

// Resultados de las herramientas del coach: viajan al modelo como JSON (camelCase, nulls explícitos).
public sealed record MejorSerieDia(decimal PesoKg, int Reps);
public sealed record EjercicioDelDia(string EjercicioId, string Nombre, bool CargaCorporal, int Series, int SeriesCalentamiento,
    MejorSerieDia? MejorSerie, decimal VolumenKg, int Reps);
public sealed record DiaEntreno(string Fecha, IReadOnlyList<string> Rutinas, IReadOnlyList<EjercicioDelDia> Ejercicios);
public sealed record EntrenosRecientes(string Desde, string Hasta, bool SinDatos, bool Truncado, IReadOnlyList<DiaEntreno> Dias);

public sealed record MejorSerieSemana(decimal PesoKg, int Reps, string Fecha);
public sealed record SemanaProgreso(string Desde, string Hasta, int Sesiones, int Series, int Reps, decimal VolumenKg,
    MejorSerieSemana? MejorSerie, decimal? E1rmMaxKg);
public sealed record MarcaSemana(string Desde, decimal MejorPesoKg, decimal? E1rmMaxKg);
public sealed record ResumenProgreso(MarcaSemana Primera, MarcaSemana Ultima, decimal CambioMejorPesoKg, decimal? CambioE1rmKg);
public sealed record ProgresoEjercicio(string EjercicioId, string Nombre, string Grupo, bool CargaCorporal, string Desde, string Hasta,
    bool SinDatos, bool Truncado, IReadOnlyList<SemanaProgreso> PorSemana, ResumenProgreso? Resumen);

public sealed record RangoSemana(string Desde, string Hasta);
public sealed record VolumenSemana(int Series, int Reps, decimal VolumenKg);
public sealed record VolumenGrupo(string Grupo, IReadOnlyList<VolumenSemana> PorSemana, int SeriesUltimaSemana,
    decimal PromedioSeriesSemanasAnteriores, decimal? CambioSeriesPct);
public sealed record VolumenPorGrupo(string Desde, string Hasta, bool SinDatos, bool Truncado, IReadOnlyList<RangoSemana> Semanas,
    IReadOnlyList<VolumenGrupo> Grupos);

/// <summary>
/// Agregados para el coach, precalculados en C# para que el modelo no haga aritmética. Funciones puras:
/// filtran la ventana (por fecha, el día local del cliente) y no cuentan calentamientos (CalculoRecords.Cuenta).
/// Semanas = bloques de 7 días que terminan en hoy: el bloque 0 es hoy-6..hoy.
/// </summary>
public static class CalculosCoach
{
    const string FormatoFecha = "yyyy-MM-dd";
    // Segundos que se le suman al descanso por serie al estimar la duración de una rutina.
    const int SegundosPorSerie = 40;

    static readonly Dictionary<string, Ejercicio> PorId = Catalogo.Ejercicios.ToDictionary(e => e.Id, StringComparer.Ordinal);

    /// <summary>Grupos del catálogo en su orden: el coach los ve todos, también los que están en 0.</summary>
    public static readonly IReadOnlyList<string> Grupos = Catalogo.Ejercicios.Select(e => e.Grupo).Distinct().ToList();

    /// <summary>Bloque k de 7 días: hoy-6-7k .. hoy-7k.</summary>
    public static (DateOnly Desde, DateOnly Hasta) Semana(DateOnly hoy, int k) => (hoy.AddDays(-7 * k - 6), hoy.AddDays(-7 * k));

    /// <summary>Primer día de una ventana de n semanas que termina hoy.</summary>
    public static DateOnly InicioSemanas(DateOnly hoy, int semanas) => hoy.AddDays(-(7 * semanas - 1));

    /// <summary>ceil(Σ series × (descanso + 40 s) / 60).</summary>
    public static int MinutosEstimados(IEnumerable<EjercicioRutina> ejercicios) =>
        (int)Math.Ceiling(ejercicios.Sum(e => e.Series * (e.DescansoS + SegundosPorSerie)) / 60.0);

    public static EntrenosRecientes Recientes(IEnumerable<Serie> series, DateOnly hoy, int dias, bool truncado = false)
    {
        var desde = hoy.AddDays(-(dias - 1));
        var enVentana = series.Where(s => EnRango(s, desde, hoy, out _)).ToList();
        var diasEntreno = enVentana
            .GroupBy(s => s.Fecha)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new DiaEntreno(g.Key,
                g.Select(s => s.Rutina).OfType<string>().Distinct().ToList(),
                // GroupBy conserva el orden de aparición: los ejercicios salen en el orden en que se hicieron.
                g.GroupBy(s => s.EjercicioId).Select(e => DelDia(e.Key, e.ToList())).ToList()))
            .ToList();
        return new(Texto(desde), Texto(hoy), !enVentana.Any(CalculoRecords.Cuenta), truncado, diasEntreno);
    }

    static EjercicioDelDia DelDia(string ejercicioId, List<Serie> series)
    {
        var cuentan = series.Where(CalculoRecords.Cuenta).ToList();
        var mejor = Mejor(cuentan);
        PorId.TryGetValue(ejercicioId, out var ej);
        return new(ejercicioId, ej?.Nombre ?? ejercicioId, ej?.CargaCorporal == true, cuentan.Count, series.Count - cuentan.Count,
            mejor is null ? null : new(mejor.PesoKg, mejor.Reps), cuentan.Sum(s => s.PesoKg * s.Reps), cuentan.Sum(s => s.Reps));
    }

    public static ProgresoEjercicio Progreso(IEnumerable<Serie> series, string ejercicioId, DateOnly hoy, int semanas, bool truncado = false)
    {
        var ej = PorId[ejercicioId];
        var desde = InicioSemanas(hoy, semanas);
        var porSemana = new List<SemanaProgreso>();
        var bloques = PorBloque(series.Where(s => s.EjercicioId == ejercicioId), hoy, semanas);
        // Vieja → nueva.
        for (var k = semanas - 1; k >= 0; k--)
        {
            var (d, h) = Semana(hoy, k);
            var b = bloques[k];
            var mejor = Mejor(b);
            var e1rm = b.Select(s => CalculoRecords.E1rm(s.PesoKg, s.Reps)).Max();
            porSemana.Add(new(Texto(d), Texto(h), b.Select(s => s.Fecha).Distinct().Count(), b.Count, b.Sum(s => s.Reps),
                b.Sum(s => s.PesoKg * s.Reps), mejor is null ? null : new(mejor.PesoKg, mejor.Reps, mejor.Fecha), e1rm));
        }

        var conDatos = porSemana.Where(s => s.MejorSerie is not null).ToList();
        ResumenProgreso? resumen = null;
        if (conDatos.Count > 0)
        {
            var (p, u) = (conDatos[0], conDatos[^1]);
            resumen = new(new(p.Desde, p.MejorSerie!.PesoKg, p.E1rmMaxKg), new(u.Desde, u.MejorSerie!.PesoKg, u.E1rmMaxKg),
                u.MejorSerie.PesoKg - p.MejorSerie.PesoKg,
                u.E1rmMaxKg is decimal ue && p.E1rmMaxKg is decimal pe ? ue - pe : null);
        }

        return new(ejercicioId, ej.Nombre, ej.Grupo, ej.CargaCorporal == true, Texto(desde), Texto(hoy), conDatos.Count == 0, truncado,
            porSemana, resumen);
    }

    public static VolumenPorGrupo Volumen(IEnumerable<Serie> series, DateOnly hoy, int semanas, bool truncado = false)
    {
        var bloques = PorBloque(series, hoy, semanas);
        var rangos = new List<RangoSemana>();
        for (var k = semanas - 1; k >= 0; k--)
        {
            var (d, h) = Semana(hoy, k);
            rangos.Add(new(Texto(d), Texto(h)));
        }

        var grupos = new List<VolumenGrupo>();
        foreach (var grupo in Grupos)
        {
            var porSemana = new List<VolumenSemana>();
            for (var k = semanas - 1; k >= 0; k--)
            {
                var b = bloques[k].Where(s => PorId.TryGetValue(s.EjercicioId, out var e) && e.Grupo == grupo).ToList();
                porSemana.Add(new(b.Count, b.Sum(s => s.Reps), b.Sum(s => s.PesoKg * s.Reps)));
            }
            var ultima = porSemana[^1].Series;
            // Con 1 semana no hay anteriores: promedio 0 y cambio null (sin dividir entre 0).
            var promedio = Math.Round((decimal)porSemana.Take(semanas - 1).Sum(s => s.Series) / Math.Max(1, semanas - 1), 1,
                MidpointRounding.AwayFromZero);
            // Sin series en las semanas anteriores no hay base contra la cual comparar.
            decimal? cambio = promedio == 0 ? null : Math.Round((ultima - promedio) / promedio * 100, 1, MidpointRounding.AwayFromZero);
            grupos.Add(new(grupo, porSemana, ultima, promedio, cambio));
        }

        return new(Texto(InicioSemanas(hoy, semanas)), Texto(hoy), bloques.All(b => b.Count == 0), truncado, rangos, grupos);
    }

    /// <summary>Series que cuentan, repartidas en los bloques 0..semanas-1 (índice = k).</summary>
    static List<Serie>[] PorBloque(IEnumerable<Serie> series, DateOnly hoy, int semanas)
    {
        var bloques = Enumerable.Range(0, semanas).Select(_ => new List<Serie>()).ToArray();
        foreach (var s in series)
            if (CalculoRecords.Cuenta(s) && EnRango(s, InicioSemanas(hoy, semanas), hoy, out var f))
                bloques[(hoy.DayNumber - f.DayNumber) / 7].Add(s);
        return bloques;
    }

    /// <summary>Mayor peso; con empate, más reps; con empate total, la primera.</summary>
    static Serie? Mejor(IEnumerable<Serie> series)
    {
        Serie? mejor = null;
        foreach (var s in series)
            if (mejor is null || s.PesoKg > mejor.PesoKg || (s.PesoKg == mejor.PesoKg && s.Reps > mejor.Reps))
                mejor = s;
        return mejor;
    }

    static bool EnRango(Serie s, DateOnly desde, DateOnly hasta, out DateOnly fecha) =>
        DateOnly.TryParseExact(s.Fecha, FormatoFecha, CultureInfo.InvariantCulture, DateTimeStyles.None, out fecha) &&
        fecha >= desde && fecha <= hasta;

    static string Texto(DateOnly d) => d.ToString(FormatoFecha, CultureInfo.InvariantCulture);
}
