using System.Globalization;
using System.Text.Json.Serialization;

namespace GymCoach.Api;

// Nullables con JsonIgnore Never: Http.Json omite nulls por defecto y el contrato es "campo | null", no opcional.
// Las marcas (reciente, anterior, ultimaSerie, mejorSerie) reusan MarcaPeso: {pesoKg, reps, fecha}.

/// <summary>
/// Cómo entrena el usuario, calculado por el código con sus series reales (sin calentamientos ni ids fuera del
/// catálogo). Lo guarda el doc perfil-entrenamiento y es la fuente de mis_ejercicios, la guardia de proponer_rutina
/// y el bloque de DATOS del prompt. Fechas YYYY-MM-DD; CalculadoEl ISO Z; Hoy es el día con el que se calculó.
/// </summary>
public sealed record PerfilEntrenamiento(
    int Version,
    string CalculadoEl,
    string Hoy,
    int SeriesAnalizadas,
    // true: la consulta llegó al tope y se perdieron las series más viejas.
    bool Truncado,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PrimerEntreno,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? UltimoEntreno,
    int DiasEntrenados,
    // null si no hay series que cuenten.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] HabitosEntrenamiento? Habitos,
    EquipoHistorial Equipo,
    // Ids de catálogo, hasta 6.
    IReadOnlyList<string> Principales,
    IReadOnlyList<TendenciaEjercicio> Tendencias,
    // Todos los ejercicios del historial: sesiones desc, series desc, ultimaFecha desc, id asc.
    IReadOnlyList<EjercicioHistorial> Ejercicios)
{
    /// <summary>Con menos historial no hay guardia de ejercicios conocidos. Se calcula; no va al doc.</summary>
    [JsonIgnore]
    public bool HistorialSuficiente => DiasEntrenados >= 4 && Ejercicios.Count >= 4;
}

/// <summary>Ventana de 56 días que termina en el último entreno (mínimo 7 si el usuario empezó después).</summary>
public sealed record HabitosEntrenamiento(
    string Desde,
    string Hasta,
    int Dias,
    // 1 decimal.
    decimal DiasPorSemana,
    // Top 4.
    IReadOnlyList<DivisionFrecuente> Division,
    // Top 5. Nombre es texto del usuario: va escapado al prompt.
    IReadOnlyList<RutinaFrecuente> RutinasFrecuentes,
    // null sin offsetMin conocido o con menos de 3 días.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] HorarioHabitual? Horario,
    // null con menos de 3 días medibles (10-240 min); lo importado de Hevy dura ~0 y no cuenta.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? DuracionTipicaMin,
    int DuracionSesionesMedidas,
    // Mediana de series que cuentan por día.
    decimal SeriesPorSesion);

/// <summary>Grupos de un día (orden alfabético, como mucho 3) y cuántos días se repitió.</summary>
public sealed record DivisionFrecuente(IReadOnlyList<string> Grupos, int Veces);

public sealed record RutinaFrecuente(string Nombre, int Veces);

/// <summary>Franja: "manana" | "mediodia" | "tarde" | "noche". HoraTipica: "HH:mm", mediana redondeada a 30 min.</summary>
public sealed record HorarioHabitual(string Franja, string HoraTipica, int Sesiones);

/// <summary>Inferido: "maquina" | "libre" | "mixto" | null (con menos de 20 series). PctMaquina 0-100.</summary>
public sealed record EquipoHistorial(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Inferido,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? PctMaquina,
    int SeriesContadas);

/// <summary>Estado: "progreso" | "estancado" | "baja" | "sin_comparar". CambioPct null si falta un lado.</summary>
public sealed record TendenciaEjercicio(
    string EjercicioId,
    string Nombre,
    string Estado,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] MarcaPeso? Reciente,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] MarcaPeso? Anterior,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? CambioPct);

/// <summary>Un ejercicio del historial. Grupo, Equipo y CargaCorporal salen del catálogo.</summary>
public sealed record EjercicioHistorial(
    string EjercicioId,
    string Nombre,
    string Grupo,
    string Equipo,
    bool CargaCorporal,
    int Sesiones,
    int Series,
    string PrimeraFecha,
    string UltimaFecha,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] MarcaPeso? UltimaSerie,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] MarcaPeso? MejorSerie,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? E1rmMaxKg);

/// <summary>
/// Lo que ve el coach en DATOS (y GET /perfil-entrenamiento como paraCoach). EquipoPreferido = perfilCoach.Equipo
/// ?? equipo inferido; EquipoOrigen: "perfil" | "historial" | null.
/// </summary>
public sealed record PerfilParaCoach(
    string CalculadoEl,
    int SeriesAnalizadas,
    bool Truncado,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? PrimerEntreno,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? UltimoEntreno,
    // Días entre el último entreno y hoy: una pausa se informa aparte, no baja la frecuencia.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? DiasDesdeUltimo,
    int DiasEntrenados,
    bool HistorialSuficiente,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] HabitosParaCoach? Habitos,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? EquipoPreferido,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? EquipoOrigen,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? PctSeriesMaquina,
    IReadOnlyList<PrincipalParaCoach> Principales,
    IReadOnlyList<TendenciaEjercicio> Tendencias);

public sealed record HabitosParaCoach(
    decimal DiasPorSemana,
    IReadOnlyList<DivisionFrecuente> Division,
    IReadOnlyList<RutinaFrecuente> RutinasFrecuentes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] HorarioHabitual? Horario,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? DuracionTipicaMin,
    decimal SeriesPorSesion);

public sealed record PrincipalParaCoach(string EjercicioId, string Nombre, int Sesiones);

/// <summary>
/// Documento en Cosmos, uno por usuario. id fijo (no UUID): PUT/DELETE /series/{id} nunca lo alcanzan.
/// OffsetMin: el último que mandó la PWA; los recálculos sin offset usan este.
/// </summary>
public sealed record PerfilEntrenamientoDoc(string Id, string UserId, string Tipo,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? OffsetMin,
    PerfilEntrenamiento Perfil,
    // Solo se escribe en true (lo marca Importar): el replace del recálculo lo borra.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Desactualizado = false)
{
    public const string IdDoc = "perfil-entrenamiento";
    public const string TipoDoc = "perfil-entrenamiento";
}

/// <summary>El cálculo del perfil: puro, sin Cosmos ni reloj propio.</summary>
public static class CalculosPerfil
{
    // Cambiarla invalida los docs guardados: se recalculan al leer. 2: tendencias con Epley sin tope de reps.
    public const int Version = 2;
    // Rango de offsetMin (-getTimezoneOffset() de la PWA): UTC-12 a UTC+14.
    public const int MinOffsetMin = -720;
    public const int MaxOffsetMin = 840;

    const string FormatoFecha = "yyyy-MM-dd";
    const int DiasHabitos = 56;
    const int MinDiasVentana = 7;
    const int MaxDivision = 4;
    const int MaxRutinas = 5;
    const int MaxPrincipales = 6;
    // Un grupo cuenta en la división del día con 3 series o más; como mucho 3 grupos por día.
    const int MinSeriesGrupoDia = 3;
    const int MaxGruposDia = 3;
    // Con menos días medidos, horario y duración quedan en null (una sesión suelta no es un hábito).
    const int MinDiasMedidos = 3;
    const int MinSeriesEquipo = 20;
    // Lo importado de Hevy dura ~0 min (ts = inicio + fila en segundos): por debajo de 10 no es una duración real.
    const int MinDuracionMin = 10;
    const int MaxDuracionMin = 240;
    const decimal UmbralTendenciaPct = 2.5m;

    static readonly Dictionary<string, Ejercicio> PorId = Catalogo.Ejercicios.ToDictionary(e => e.Id, StringComparer.Ordinal);

    /// <summary>Serie que cuenta para el perfil, con su fecha ya parseada y su ejercicio del catálogo.</summary>
    sealed record Fila(Serie Serie, DateOnly Fecha, Ejercicio Ejercicio);

    /// <summary>
    /// Perfil a partir de las series del usuario (cualquier orden). truncado: la consulta llegó al tope.
    /// offsetMin null: horario null. SeriesAnalizadas cuenta solo las que cuentan (sin calentamientos ni ids fuera del catálogo).
    /// </summary>
    public static PerfilEntrenamiento Calcular(IEnumerable<Serie> series, DateOnly hoy, int? offsetMin, DateTimeOffset ahora,
        bool truncado)
    {
        // Por ts (OrderBy es estable): con empate de marcas gana la primera serie, igual que en los récords.
        var filas = new List<Fila>();
        foreach (var s in series.OrderBy(s => s.Ts, StringComparer.Ordinal))
            // EjercicioId null: un doc de serie editado a mano no tumba el cálculo.
            if (CalculoRecords.Cuenta(s) && s.EjercicioId is not null && PorId.TryGetValue(s.EjercicioId, out var ej) &&
                TryFecha(s.Fecha, out var f))
                filas.Add(new(s, f, ej));
        var calculadoEl = ahora.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        if (filas.Count == 0)
            return new(Version, calculadoEl, Texto(hoy), 0, truncado, null, null, 0, null, new EquipoHistorial(null, null, 0), [], [], []);

        var primero = filas.Min(f => f.Fecha);
        var ultimo = filas.Max(f => f.Fecha);

        // La ventana termina en el último entreno, no en hoy: una pausa no deforma "cómo entrenas".
        var desde = ultimo.AddDays(-(DiasHabitos - 1));
        if (desde < primero) desde = primero;
        if (ultimo.DayNumber - desde.DayNumber + 1 < MinDiasVentana) desde = ultimo.AddDays(-(MinDiasVentana - 1));
        var enVentana = filas.Where(f => f.Fecha >= desde && f.Fecha <= ultimo).ToList();

        var principales = enVentana
            .GroupBy(f => f.Serie.EjercicioId, StringComparer.Ordinal)
            .Select(g => (Id: g.Key, Sesiones: g.Select(f => f.Fecha).Distinct().Count(), Series: g.Count()))
            .Where(e => e.Sesiones >= 2)
            .OrderByDescending(e => e.Sesiones).ThenByDescending(e => e.Series).ThenBy(e => e.Id, StringComparer.Ordinal)
            .Take(MaxPrincipales)
            .Select(e => e.Id)
            .ToList();

        return new(Version, calculadoEl, Texto(hoy), filas.Count, truncado, Texto(primero), Texto(ultimo),
            filas.Select(f => f.Fecha).Distinct().Count(),
            Habitos(enVentana, desde, ultimo, offsetMin),
            Equipo(enVentana, filas),
            principales,
            [.. principales.Select(id => Tendencia(id, filas, ultimo))],
            Ejercicios(filas));
    }

    static HabitosEntrenamiento Habitos(List<Fila> enVentana, DateOnly desde, DateOnly hasta, int? offsetMin)
    {
        // Cada día en orden de ts (filas ya viene ordenada): dia[0] es la primera serie y dia[^1] la última.
        var porDia = enVentana.GroupBy(f => f.Fecha).OrderBy(g => g.Key).Select(g => g.ToList()).ToList();
        var diasVentana = hasta.DayNumber - desde.DayNumber + 1;

        // Grupos con 3+ series (los 3 con más); si ninguno llega, el que más tuvo. La combinación va en orden alfabético.
        var division = porDia
            .Select(dia =>
            {
                var grupos = dia.GroupBy(f => f.Ejercicio.Grupo, StringComparer.Ordinal)
                    .Select(g => (Grupo: g.Key, Series: g.Count()))
                    .OrderByDescending(g => g.Series).ThenBy(g => g.Grupo, StringComparer.Ordinal)
                    .ToList();
                var elegidos = grupos.Where(g => g.Series >= MinSeriesGrupoDia).Take(MaxGruposDia).Select(g => g.Grupo).ToList();
                if (elegidos.Count == 0) elegidos.Add(grupos[0].Grupo);
                return string.Join(',', elegidos.Order(StringComparer.Ordinal));
            })
            .GroupBy(clave => clave, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(MaxDivision)
            .Select(g => new DivisionFrecuente(g.Key.Split(','), g.Count()))
            .ToList();

        // Veces = días en que aparece el nombre (texto del usuario, tal cual).
        var rutinas = enVentana
            .Where(f => f.Serie.Rutina is not null)
            .Select(f => (f.Fecha, Nombre: f.Serie.Rutina!))
            .Distinct()
            .GroupBy(r => r.Nombre, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(MaxRutinas)
            .Select(g => new RutinaFrecuente(g.Key, g.Count()))
            .ToList();

        var duraciones = porDia
            .Select(dia => Instante(dia[^1].Serie.Ts) - Instante(dia[0].Serie.Ts) is TimeSpan t ? (decimal)t.TotalMinutes : -1)
            .Where(m => m >= MinDuracionMin && m <= MaxDuracionMin)
            .ToList();

        return new(Texto(desde), Texto(hasta), porDia.Count,
            Math.Round(porDia.Count * 7m / diasVentana, 1, MidpointRounding.AwayFromZero),
            division, rutinas,
            offsetMin is int off ? Horario(porDia, off) : null,
            duraciones.Count >= MinDiasMedidos ? (int)Math.Round(Mediana(duraciones), MidpointRounding.AwayFromZero) : null,
            duraciones.Count,
            Math.Round(Mediana([.. porDia.Select(d => (decimal)d.Count)]), 1, MidpointRounding.AwayFromZero));
    }

    /// <summary>Inicio de cada día (primer ts más offsetMin); hora típica = mediana redondeada a 30 min, y su franja.</summary>
    static HorarioHabitual? Horario(List<List<Fila>> porDia, int offsetMin)
    {
        var inicios = porDia
            .Select(dia => Instante(dia[0].Serie.Ts) is DateTimeOffset t ? t.UtcDateTime.AddMinutes(offsetMin) : (DateTime?)null)
            .OfType<DateTime>()
            .Select(local => (decimal)(local.Hour * 60 + local.Minute))
            .ToList();
        if (inicios.Count < MinDiasMedidos) return null;
        var minutos = (int)Math.Round(Mediana(inicios) / 30, MidpointRounding.AwayFromZero) * 30 % (24 * 60);
        var hora = minutos / 60;
        var franja = hora switch
        {
            >= 5 and <= 11 => "manana",
            >= 12 and <= 15 => "mediodia",
            >= 16 and <= 19 => "tarde",
            _ => "noche",
        };
        return new(franja, $"{hora:00}:{minutos % 60:00}", inicios.Count);
    }

    /// <summary>% de series de máquina en la ventana; con menos de 20 ahí, sobre todo lo analizado; con menos de 20 en total, null.</summary>
    static EquipoHistorial Equipo(List<Fila> enVentana, List<Fila> todas)
    {
        var medidas = enVentana.Count >= MinSeriesEquipo ? enVentana : todas;
        if (medidas.Count < MinSeriesEquipo) return new(null, null, medidas.Count);
        var pct = (int)Math.Round(medidas.Count(f => f.Ejercicio.Equipo == Catalogo.Maquina) * 100m / medidas.Count,
            MidpointRounding.AwayFromZero);
        // Sobre el % redondeado: la etiqueta coincide con el número que se muestra.
        var inferido = pct >= 70 ? Catalogo.Maquina : pct <= 30 ? Catalogo.Libre : "mixto";
        return new(inferido, pct, medidas.Count);
    }

    /// <summary>
    /// Reciente (21 días que terminan en el último entreno) contra anterior (de -55 a -28 días). Métrica: Epley máximo
    /// sin el tope de 12 reps de los récords (con series de 15 reps, comparar solo reps daba "baja" aunque el peso
    /// subiera); en carga corporal, reps (pesoKg es el lastre).
    /// </summary>
    static TendenciaEjercicio Tendencia(string ejercicioId, List<Fila> filas, DateOnly ultimo)
    {
        var ej = PorId[ejercicioId];
        var delEjercicio = filas.Where(f => f.Serie.EjercicioId == ejercicioId).ToList();
        var reciente = delEjercicio.Where(f => f.Fecha >= ultimo.AddDays(-20) && f.Fecha <= ultimo).ToList();
        var anterior = delEjercicio.Where(f => f.Fecha >= ultimo.AddDays(-55) && f.Fecha <= ultimo.AddDays(-28)).ToList();

        var usarE1rm = ej.CargaCorporal != true;
        var (marcaR, valorR) = MejorPorMetrica(reciente, usarE1rm);
        var (marcaA, valorA) = MejorPorMetrica(anterior, usarE1rm);

        decimal? cambio = valorR is decimal r && valorA is decimal a && a != 0
            ? Math.Round((r - a) / a * 100, 1, MidpointRounding.AwayFromZero)
            : null;
        var estado = cambio switch
        {
            null => "sin_comparar",
            >= UmbralTendenciaPct => "progreso",
            <= -UmbralTendenciaPct => "baja",
            _ => "estancado",
        };
        return new(ejercicioId, ej.Nombre, estado, marcaR, marcaA, cambio);
    }

    /// <summary>
    /// La serie dueña de la métrica (con empate, la primera; en reps, la de más peso) y su valor. Series sin peso no
    /// tienen Epley: un lado solo con ellas queda sin comparar.
    /// </summary>
    static (MarcaPeso? Marca, decimal? Valor) MejorPorMetrica(List<Fila> filas, bool usarE1rm)
    {
        Serie? mejor = null;
        decimal valor = 0;
        foreach (var s in filas.Select(f => f.Serie))
        {
            if ((usarE1rm ? EpleySinTope(s.PesoKg, s.Reps) : s.Reps) is not decimal v) continue;
            if (mejor is null || v > valor || (!usarE1rm && v == valor && s.PesoKg > mejor.PesoKg))
                (mejor, valor) = (s, v);
        }
        return mejor is null ? (null, null) : (Marca(mejor), valor);
    }

    /// <summary>
    /// Todos los ejercicios del historial. UltimaSerie: la mejor serie (peso, luego reps) del último día, la referencia
    /// para el siguiente peso; MejorSerie: la mejor de todo el historial.
    /// </summary>
    static List<EjercicioHistorial> Ejercicios(List<Fila> filas) =>
        filas.GroupBy(f => f.Serie.EjercicioId, StringComparer.Ordinal)
            .Select(g =>
            {
                var ej = g.First().Ejercicio;
                var ultimaFecha = g.Max(f => f.Fecha);
                return new EjercicioHistorial(g.Key, ej.Nombre, ej.Grupo, ej.Equipo, ej.CargaCorporal == true,
                    g.Select(f => f.Fecha).Distinct().Count(), g.Count(), Texto(g.Min(f => f.Fecha)), Texto(ultimaFecha),
                    Marca(Mejor(g.Where(f => f.Fecha == ultimaFecha))),
                    Marca(Mejor(g)),
                    g.Select(f => CalculoRecords.E1rm(f.Serie.PesoKg, f.Serie.Reps)).Max());
            })
            .OrderByDescending(e => e.Sesiones).ThenByDescending(e => e.Series)
            .ThenByDescending(e => e.UltimaFecha, StringComparer.Ordinal).ThenBy(e => e.EjercicioId, StringComparer.Ordinal)
            .ToList();

    /// <summary>Mayor peso; con empate, más reps; con empate total, la primera (igual que CalculosCoach).</summary>
    static Serie Mejor(IEnumerable<Fila> filas)
    {
        Serie? mejor = null;
        foreach (var s in filas.Select(f => f.Serie))
            if (mejor is null || s.PesoKg > mejor.PesoKg || (s.PesoKg == mejor.PesoKg && s.Reps > mejor.Reps))
                mejor = s;
        return mejor!;
    }

    /// <summary>El objeto exacto que se serializa en el prompt y en paraCoach del GET.</summary>
    public static PerfilParaCoach ParaCoach(PerfilEntrenamiento perfil, PerfilCoach perfilCoach, DateOnly hoy)
    {
        var porId = perfil.Ejercicios.ToDictionary(e => e.EjercicioId, StringComparer.Ordinal);
        // Una fecha de mañana (cliente adelantado a UTC) no da días negativos.
        int? diasDesdeUltimo = TryFecha(perfil.UltimoEntreno, out var u) ? Math.Max(0, hoy.DayNumber - u.DayNumber) : null;
        var origen = perfilCoach.Equipo is not null ? "perfil" : perfil.Equipo.Inferido is not null ? "historial" : null;
        var h = perfil.Habitos;
        return new(perfil.CalculadoEl, perfil.SeriesAnalizadas, perfil.Truncado, perfil.PrimerEntreno, perfil.UltimoEntreno,
            diasDesdeUltimo, perfil.DiasEntrenados, perfil.HistorialSuficiente,
            h is null ? null : new(h.DiasPorSemana, h.Division, h.RutinasFrecuentes, h.Horario, h.DuracionTipicaMin, h.SeriesPorSesion),
            perfilCoach.Equipo ?? perfil.Equipo.Inferido, origen, perfil.Equipo.PctMaquina,
            // Sesiones de todo el historial: el doc guarda solo los ids de los principales.
            [.. perfil.Principales.Where(porId.ContainsKey).Select(id => new PrincipalParaCoach(id, porId[id].Nombre, porId[id].Sesiones))],
            perfil.Tendencias);
    }

    // Solo para comparar dos ventanas del mismo ejercicio: con muchas reps no es un 1RM fiable, pero sí sube y baja con él.
    static decimal? EpleySinTope(decimal pesoKg, int reps) =>
        pesoKg == 0 ? null : reps == 1 ? pesoKg : Math.Round(pesoKg * (30 + reps) / 30, 2, MidpointRounding.AwayFromZero);

    static MarcaPeso Marca(Serie s) => new(s.PesoKg, s.Reps, s.Fecha);

    /// <summary>Mediana; con cantidad par, el promedio de las dos del medio.</summary>
    static decimal Mediana(List<decimal> valores)
    {
        var o = valores.Order().ToList();
        var m = o.Count / 2;
        return o.Count % 2 == 1 ? o[m] : (o[m - 1] + o[m]) / 2;
    }

    // ts llega normalizado a UTC por la validación; uno ilegible (doc editado a mano) queda fuera de horario y duración.
    static DateTimeOffset? Instante(string? ts) =>
        DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) &&
        t > DateTimeOffset.MinValue.AddDays(1) ? t : null;

    static bool TryFecha(string? s, out DateOnly fecha) =>
        DateOnly.TryParseExact(s, FormatoFecha, CultureInfo.InvariantCulture, DateTimeStyles.None, out fecha);

    static string Texto(DateOnly d) => d.ToString(FormatoFecha, CultureInfo.InvariantCulture);
}

