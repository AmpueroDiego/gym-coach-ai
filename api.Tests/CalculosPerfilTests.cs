using System.Text.Json;
using GymCoach.Api.Functions;

namespace GymCoach.Api.Tests;

/// <summary>El cálculo puro del perfil de entrenamiento (sin Cosmos).</summary>
public class CalculosPerfilTests
{
    static readonly DateOnly Hoy = new(2026, 10, 14);
    static readonly DateTimeOffset Ahora = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    static PerfilEntrenamiento Calcular(IEnumerable<Serie> series, int? offsetMin = null, bool truncado = false) =>
        CalculosPerfil.Calcular(series, Hoy, offsetMin, Ahora, truncado);

    static Serie S(string ejercicio, decimal peso, int reps, DateTime ts, string? tipo = null, string? rutina = null) =>
        new(Guid.NewGuid().ToString("D"), ejercicio, peso, reps, ts.ToString("yyyy-MM-dd"), ts.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"),
            rutina, tipo);

    /// <summary>n series del ejercicio en el día d, desde h:m UTC y separadas cadaSeg segundos.</summary>
    static IEnumerable<Serie> Dia(DateOnly d, string ejercicio, int n = 3, decimal peso = 50, int reps = 8, int h = 13, int m = 5,
        int cadaSeg = 120, string? rutina = null) =>
        Enumerable.Range(0, n).Select(i =>
            S(ejercicio, peso, reps, d.ToDateTime(new TimeOnly(h, m), DateTimeKind.Utc).AddSeconds(i * cadaSeg), rutina: rutina));

    static DateOnly D(int diasAtras) => Hoy.AddDays(-diasAtras);

    [Fact]
    public void UsuarioVacio_SinHabitosNiEjercicios()
    {
        var p = Calcular([]);
        Assert.Equal(CalculosPerfil.Version, p.Version);
        Assert.Equal("2026-10-14T12:00:00.000Z", p.CalculadoEl);
        Assert.Equal("2026-10-14", p.Hoy);
        Assert.Equal(0, p.SeriesAnalizadas);
        Assert.Null(p.PrimerEntreno);
        Assert.Null(p.UltimoEntreno);
        Assert.Null(p.Habitos);
        Assert.Equal(new EquipoHistorial(null, null, 0), p.Equipo);
        Assert.Empty(p.Principales);
        Assert.Empty(p.Tendencias);
        Assert.Empty(p.Ejercicios);
        Assert.False(p.HistorialSuficiente);

        var c = CalculosPerfil.ParaCoach(p, PerfilCoach.PorDefecto, Hoy);
        Assert.Null(c.DiasDesdeUltimo);
        Assert.Null(c.Habitos);
        Assert.Null(c.EquipoPreferido);
        Assert.Null(c.EquipoOrigen);

        // Nulls explícitos aunque Http.Json omita los nulls: el contrato es "campo | null".
        var json = JsonSerializer.Serialize(p, Http.Json);
        Assert.Contains("\"habitos\":null", json);
        Assert.Contains("\"primerEntreno\":null", json);
        Assert.Contains("\"equipo\":{\"inferido\":null,\"pctMaquina\":null,\"seriesContadas\":0}", json);
        Assert.DoesNotContain("historialSuficiente", json);
        Assert.Contains("\"diasDesdeUltimo\":null", JsonSerializer.Serialize(c, Http.Json));
    }

    [Fact]
    public void Calentamientos_NoCuentanEnNada()
    {
        var series = Dia(D(0), "press-banca", n: 2, peso: 60, reps: 5)
            .Append(S("press-banca", 100, 3, D(0).ToDateTime(new TimeOnly(12, 50), DateTimeKind.Utc), tipo: "calentamiento"))
            .Append(S("sentadilla", 40, 10, D(1).ToDateTime(new TimeOnly(12, 50), DateTimeKind.Utc), tipo: "calentamiento"));
        var p = Calcular(series);

        Assert.Equal(2, p.SeriesAnalizadas);
        Assert.Equal(1, p.DiasEntrenados);
        Assert.Equal("2026-10-14", p.PrimerEntreno);
        var e = Assert.Single(p.Ejercicios);
        Assert.Equal(2, e.Series);
        Assert.Equal(new MarcaPeso(60, 5, "2026-10-14"), e.MejorSerie);
        Assert.Equal(70m, e.E1rmMaxKg);
    }

    [Fact]
    public void IdFueraDelCatalogo_SeIgnora()
    {
        var p = Calcular(Dia(D(0), "press-banca", n: 2).Concat(Dia(D(0), "inventado", n: 5)));
        Assert.Equal(2, p.SeriesAnalizadas);
        Assert.Equal("press-banca", Assert.Single(p.Ejercicios).EjercicioId);
    }

    [Theory]
    [InlineData(16, "maquina", 80)]
    [InlineData(14, "maquina", 70)]
    [InlineData(10, "mixto", 50)]
    [InlineData(6, "libre", 30)]
    [InlineData(4, "libre", 20)]
    public void Equipo_PorcentajeDeMaquina(int maquina, string inferido, int pct)
    {
        var series = Dia(D(0), "prensa", n: maquina).Concat(Dia(D(1), "sentadilla", n: 20 - maquina));
        Assert.Equal(new EquipoHistorial(inferido, pct, 20), Calcular(series).Equipo);
    }

    [Fact]
    public void Equipo_ConMenosDe20Series_Null()
    {
        Assert.Equal(new EquipoHistorial(null, null, 19), Calcular(Dia(D(0), "prensa", n: 19)).Equipo);
    }

    [Fact]
    public void Equipo_PocasEnLaVentana_SeMideSobreTodoElHistorial()
    {
        // 25 de máquina hace 100 días (fuera de la ventana de 56) y 5 libres recientes: la ventana sola no llega a 20.
        var series = Dia(D(100), "prensa", n: 25).Concat(Dia(D(0), "sentadilla", n: 5));
        Assert.Equal(new EquipoHistorial("maquina", 83, 30), Calcular(series).Equipo);
    }

    /// <summary>3 días por semana (lun, mié, vie relativos) durante 8 semanas que terminan en ultimo.</summary>
    static IEnumerable<Serie> TresPorSemana(int diasAtrasUltimo) =>
        Enumerable.Range(0, 8).SelectMany(k => new[] { 0, 2, 4 }.SelectMany(o => Dia(D(diasAtrasUltimo + 7 * k + o), "press-banca")));

    [Fact]
    public void Frecuencia_TresPorSemana()
    {
        // Un día viejo antes de la ventana: así la ventana es de 56 días completos.
        var p = Calcular(TresPorSemana(0).Concat(Dia(D(90), "press-banca")));
        Assert.Equal("2026-08-20", p.Habitos!.Desde);
        Assert.Equal("2026-10-14", p.Habitos.Hasta);
        Assert.Equal(24, p.Habitos.Dias);
        Assert.Equal(3.0m, p.Habitos.DiasPorSemana);
        Assert.Equal(25, p.DiasEntrenados);
        Assert.Equal(3m, p.Habitos.SeriesPorSesion);
    }

    [Fact]
    public void Frecuencia_UsuarioNuevoDeDosSemanas_VentanaDesdeElPrimerEntreno()
    {
        var dias = new[] { 13, 11, 9, 6, 4, 0 };
        var p = Calcular(dias.SelectMany(d => Dia(D(d), "press-banca")));
        // 6 días en una ventana de 14 (del primero al último), no de 56.
        Assert.Equal("2026-10-01", p.Habitos!.Desde);
        Assert.Equal("2026-10-14", p.Habitos.Hasta);
        Assert.Equal(3.0m, p.Habitos.DiasPorSemana);
    }

    [Fact]
    public void Frecuencia_UnSoloDia_VentanaMinimaDeSiete()
    {
        var p = Calcular(Dia(D(0), "press-banca"));
        Assert.Equal("2026-10-08", p.Habitos!.Desde);
        Assert.Equal(1.0m, p.Habitos.DiasPorSemana);
    }

    [Fact]
    public void Frecuencia_ConPausaDe30Dias_NoBaja_YSeInformaAparte()
    {
        var p = Calcular(TresPorSemana(30).Concat(Dia(D(120), "press-banca")));
        Assert.Equal(3.0m, p.Habitos!.DiasPorSemana);
        Assert.Equal("2026-09-14", p.UltimoEntreno);

        var c = CalculosPerfil.ParaCoach(p, PerfilCoach.PorDefecto, Hoy);
        Assert.Equal(30, c.DiasDesdeUltimo);
        Assert.Equal(3.0m, c.Habitos!.DiasPorSemana);
    }

    [Fact]
    public void Division_GruposConTresSeriesOMas_TopTresYOrdenAlfabetico()
    {
        IEnumerable<Serie> Empuje(int d) => Dia(D(d), "press-banca", n: 4).Concat(Dia(D(d), "press-frances", n: 3, h: 14))
            .Concat(Dia(D(d), "elevaciones-laterales", n: 2, h: 15));
        IEnumerable<Serie> Pierna(int d) => Dia(D(d), "sentadilla", n: 5).Concat(Dia(D(d), "curl-femoral", n: 3, h: 14))
            .Concat(Dia(D(d), "hip-thrust", n: 3, h: 15)).Concat(Dia(D(d), "elevacion-talones-pie", n: 3, h: 16));
        var series = Empuje(0).Concat(Empuje(2)).Concat(Empuje(4)).Concat(Pierna(1)).Concat(Pierna(3))
            // Ningún grupo llega a 3: cuenta el que más tuvo.
            .Concat(Dia(D(5), "curl-barra", n: 2)).Concat(Dia(D(5), "crunch-polea", n: 1, h: 14));

        var division = Calcular(series).Habitos!.Division;
        Assert.Equal(3, division.Count);
        Assert.Equal(["pecho", "triceps"], division[0].Grupos);
        Assert.Equal(3, division[0].Veces);
        // Pierna: cuadriceps (5) y, con empate a 3, gluteos e isquios antes que pantorrillas.
        Assert.Equal(["cuadriceps", "gluteos", "isquios"], division[1].Grupos);
        Assert.Equal(2, division[1].Veces);
        Assert.Equal(["biceps"], division[2].Grupos);
    }

    [Fact]
    public void Horario_ConOffset_HoraLocalYFranja()
    {
        var series = new[] { 0, 2, 4 }.SelectMany(d => Dia(D(d), "press-banca", h: 13, m: 5));
        // 13:05Z con -360 son las 07:05 locales: mañana; la hora típica va redondeada a 30 min.
        Assert.Equal(new HorarioHabitual("manana", "07:00", 3), Calcular(series, offsetMin: -360).Habitos!.Horario);
        // Sin offset conocido no se puede saber la hora local.
        Assert.Null(Calcular(series).Habitos!.Horario);
    }

    [Fact]
    public void Horario_MedianaYFranjas()
    {
        var series = new[] { (0, 17, 40), (2, 18, 10), (4, 19, 0), (6, 6, 0) }
            .SelectMany(x => Dia(D(x.Item1), "press-banca", h: x.Item2, m: x.Item3));
        // Mediana de 06:00, 17:40, 18:10, 19:00 = 17:55 → 18:00, tarde.
        Assert.Equal(new HorarioHabitual("tarde", "18:00", 4), Calcular(series, offsetMin: 0).Habitos!.Horario);

        // 23:50 redondea a medianoche: 00:00, noche.
        var tarde = new[] { 0, 2, 4 }.SelectMany(d => Dia(D(d), "press-banca", h: 23, m: 50));
        Assert.Equal(new HorarioHabitual("noche", "00:00", 3), Calcular(tarde, offsetMin: 0).Habitos!.Horario);
    }

    [Fact]
    public void Horario_ConMenosDeTresDias_Null()
    {
        var series = new[] { 0, 2 }.SelectMany(d => Dia(D(d), "press-banca"));
        Assert.Null(Calcular(series, offsetMin: -360).Habitos!.Horario);
    }

    [Fact]
    public void Duracion_DiasDeHevyExcluidos_MedianaDeLosMedibles()
    {
        // Hevy: ts = inicio + fila en segundos, unos 0 minutos por día.
        var hevy = new[] { 20, 22, 24 }.SelectMany(d => Dia(D(d), "press-banca", n: 10, cadaSeg: 1));
        // 60, 70 y 65 minutos entre la primera y la última serie.
        var medidos = new[] { (0, 60), (2, 70), (4, 65) }.SelectMany(x => Dia(D(x.Item1), "press-banca", n: 2, cadaSeg: x.Item2 * 60));
        var h = Calcular(hevy.Concat(medidos)).Habitos!;
        Assert.Equal(65, h.DuracionTipicaMin);
        Assert.Equal(3, h.DuracionSesionesMedidas);

        var soloHevy = Calcular(hevy).Habitos!;
        Assert.Null(soloHevy.DuracionTipicaMin);
        Assert.Equal(0, soloHevy.DuracionSesionesMedidas);
        Assert.Equal(10m, soloHevy.SeriesPorSesion);
    }

    [Fact]
    public void Tendencias_LosCuatroEstados_YCargaCorporalPorReps()
    {
        var series = Dia(D(40), "press-banca", n: 1, peso: 100, reps: 5).Concat(Dia(D(0), "press-banca", n: 1, peso: 105, reps: 5))
            .Concat(Dia(D(40), "sentadilla", n: 1, peso: 100, reps: 5)).Concat(Dia(D(1), "sentadilla", n: 1, peso: 100, reps: 5))
            .Concat(Dia(D(40), "peso-muerto", n: 1, peso: 100, reps: 5)).Concat(Dia(D(2), "peso-muerto", n: 1, peso: 90, reps: 5))
            .Concat(Dia(D(10), "remo-barra", n: 1)).Concat(Dia(D(3), "remo-barra", n: 1))
            .Concat(Dia(D(40), "dominadas", n: 1, peso: 0, reps: 8)).Concat(Dia(D(4), "dominadas", n: 1, peso: 0, reps: 10))
            // Fuera de las dos ventanas (-27 a -21 días): no cuenta para la tendencia.
            .Concat(Dia(D(25), "press-banca", n: 1, peso: 200, reps: 1));

        var t = Calcular(series).Tendencias.ToDictionary(x => x.EjercicioId);
        Assert.Equal(5, t.Count);

        Assert.Equal("progreso", t["press-banca"].Estado);
        Assert.Equal(new MarcaPeso(105, 5, "2026-10-14"), t["press-banca"].Reciente);
        Assert.Equal(new MarcaPeso(100, 5, "2026-09-04"), t["press-banca"].Anterior);
        Assert.Equal(5.0m, t["press-banca"].CambioPct);

        Assert.Equal("estancado", t["sentadilla"].Estado);
        Assert.Equal(0m, t["sentadilla"].CambioPct);

        Assert.Equal("baja", t["peso-muerto"].Estado);
        Assert.Equal(-10.0m, t["peso-muerto"].CambioPct);

        Assert.Equal("sin_comparar", t["remo-barra"].Estado);
        Assert.Null(t["remo-barra"].Anterior);
        Assert.Null(t["remo-barra"].CambioPct);
        Assert.NotNull(t["remo-barra"].Reciente);

        // Carga corporal: reps de la mejor serie (8 → 10 = +25%).
        Assert.Equal("progreso", t["dominadas"].Estado);
        Assert.Equal(25.0m, t["dominadas"].CambioPct);
        Assert.Equal(new MarcaPeso(0, 10, "2026-10-10"), t["dominadas"].Reciente);
    }

    [Theory]
    // Caso real (CSV de Diego, prensa horizontal): subió el peso con más de 12 reps; comparar solo reps daba "baja".
    [InlineData(133.81, 16, 151.95, 15, 11.1, "progreso")]
    [InlineData(50, 15, 60, 15, 20.0, "progreso")]
    [InlineData(60, 15, 60, 14, -2.2, "estancado")]
    [InlineData(60, 10, 50, 15, -6.3, "baja")]
    public void Tendencias_MasDe12Reps_EpleySinTope(double pesoAntes, int repsAntes, double pesoAhora, int repsAhora,
        double cambio, string estado)
    {
        var series = Dia(D(40), "prensa-horizontal", n: 1, peso: (decimal)pesoAntes, reps: repsAntes)
            .Concat(Dia(D(0), "prensa-horizontal", n: 1, peso: (decimal)pesoAhora, reps: repsAhora));
        var t = Assert.Single(Calcular(series).Tendencias);
        Assert.Equal((decimal)cambio, t.CambioPct);
        Assert.Equal(estado, t.Estado);
        Assert.Equal(new MarcaPeso((decimal)pesoAhora, repsAhora, "2026-10-14"), t.Reciente);
    }

    [Fact]
    public void Tendencias_SinPesoFueraDeCargaCorporal_SinComparar()
    {
        var series = Dia(D(40), "prensa", n: 1, peso: 0, reps: 10).Concat(Dia(D(0), "prensa", n: 1, peso: 50, reps: 10));
        var t = Assert.Single(Calcular(series).Tendencias);
        Assert.Equal("sin_comparar", t.Estado);
        Assert.Null(t.Anterior);
    }

    [Fact]
    public void Principales_HastaSeis_ConDosSesionesOMas()
    {
        var ids = new[] { "press-banca", "sentadilla", "peso-muerto", "remo-barra", "press-militar", "curl-barra", "prensa" };
        var series = new List<Serie>();
        // Sesiones: 4, 3, 2, 2, 2, 2, 2; con empate de sesiones, más series primero.
        for (var i = 0; i < ids.Length; i++)
            for (var s = 0; s < Math.Max(2, 4 - i); s++)
                series.AddRange(Dia(D(s * 2), ids[i], n: i == 6 ? 4 : 3));
        // Una sola sesión: no es principal aunque tenga muchas series.
        series.AddRange(Dia(D(1), "hip-thrust", n: 10));

        Assert.Equal(["press-banca", "sentadilla", "prensa", "curl-barra", "peso-muerto", "press-militar"], Calcular(series).Principales);
    }

    [Fact]
    public void Ejercicios_OrdenSesionesSeriesUltimaFechaId()
    {
        var series = Dia(D(0), "sentadilla").Concat(Dia(D(2), "sentadilla")).Concat(Dia(D(4), "sentadilla"))
            .Concat(Dia(D(1), "prensa", n: 3)).Concat(Dia(D(3), "prensa", n: 3))
            .Concat(Dia(D(0), "press-banca", n: 2)).Concat(Dia(D(5), "press-banca", n: 2))
            .Concat(Dia(D(1), "remo-barra", n: 2)).Concat(Dia(D(5), "remo-barra", n: 2))
            .Concat(Dia(D(1), "curl-barra", n: 2)).Concat(Dia(D(5), "curl-barra", n: 2));

        Assert.Equal(["sentadilla", "prensa", "press-banca", "curl-barra", "remo-barra"],
            Calcular(series).Ejercicios.Select(e => e.EjercicioId));
    }

    [Fact]
    public void Ejercicio_UltimaSerieMejorSerieYCatalogo()
    {
        var series = Dia(D(7), "press-banca", n: 1, peso: 80, reps: 3)
            .Concat(Dia(D(0), "press-banca", n: 1, peso: 70, reps: 8))
            .Concat(Dia(D(0), "press-banca", n: 1, peso: 60, reps: 12, h: 14))
            .Concat(Dia(D(0), "dominadas", n: 1, peso: 0, reps: 9));
        var e = Calcular(series).Ejercicios.ToDictionary(x => x.EjercicioId);

        var banca = e["press-banca"];
        Assert.Equal(("pecho", "libre", false), (banca.Grupo, banca.Equipo, banca.CargaCorporal));
        Assert.Equal(("2026-10-07", "2026-10-14"), (banca.PrimeraFecha, banca.UltimaFecha));
        // La mejor del último día (no la última en hacerse): la referencia para el siguiente peso.
        Assert.Equal(new MarcaPeso(70, 8, "2026-10-14"), banca.UltimaSerie);
        Assert.Equal(new MarcaPeso(80, 3, "2026-10-07"), banca.MejorSerie);
        Assert.Equal(88.67m, banca.E1rmMaxKg);

        Assert.True(e["dominadas"].CargaCorporal);
        Assert.Null(e["dominadas"].E1rmMaxKg);
    }

    [Fact]
    public void Truncado_SePasaTalCual()
    {
        Assert.True(Calcular(Dia(D(0), "press-banca"), truncado: true).Truncado);
        Assert.False(Calcular(Dia(D(0), "press-banca")).Truncado);
    }

    [Fact]
    public void HistorialSuficiente_CuatroDiasYCuatroEjercicios()
    {
        var ids = new[] { "press-banca", "sentadilla", "peso-muerto", "remo-barra" };
        Assert.True(Calcular(ids.SelectMany((id, i) => Dia(D(i), id))).HistorialSuficiente);
        // 3 días (4 ejercicios) o 3 ejercicios (4 días): no alcanza.
        Assert.False(Calcular(ids.SelectMany((id, i) => Dia(D(Math.Min(i, 2)), id))).HistorialSuficiente);
        Assert.False(Calcular(ids.SelectMany((id, i) => Dia(D(i), i == 3 ? ids[0] : id))).HistorialSuficiente);
    }

    [Fact]
    public void ParaCoach_EquipoOrigen()
    {
        var maquina = Calcular(Dia(D(0), "prensa", n: 20));

        var porPerfil = CalculosPerfil.ParaCoach(maquina, PerfilCoach.PorDefecto with { Equipo = "libre" }, Hoy);
        Assert.Equal(("libre", "perfil", 100), (porPerfil.EquipoPreferido, porPerfil.EquipoOrigen, porPerfil.PctSeriesMaquina));

        var porHistorial = CalculosPerfil.ParaCoach(maquina, PerfilCoach.PorDefecto, Hoy);
        Assert.Equal(("maquina", "historial"), (porHistorial.EquipoPreferido, porHistorial.EquipoOrigen));

        var sinDatos = CalculosPerfil.ParaCoach(Calcular(Dia(D(0), "prensa", n: 5)), PerfilCoach.PorDefecto, Hoy);
        Assert.Null(sinDatos.EquipoPreferido);
        Assert.Null(sinDatos.EquipoOrigen);
        Assert.Null(sinDatos.PctSeriesMaquina);
    }

    [Fact]
    public void ParaCoach_PrincipalesConNombreYSesiones()
    {
        var p = Calcular(Dia(D(0), "press-banca").Concat(Dia(D(2), "press-banca")).Concat(Dia(D(4), "press-banca")));
        var c = CalculosPerfil.ParaCoach(p, PerfilCoach.PorDefecto, Hoy);
        Assert.Equal(new PrincipalParaCoach("press-banca", "Press de banca", 3), Assert.Single(c.Principales));
        Assert.Same(p.Tendencias, c.Tendencias);
        Assert.Equal(0, c.DiasDesdeUltimo);
    }

    [Fact]
    public void RutinaConComillasYCierreDeEtiqueta_QuedaTalCual_ElEscapeEsDelSerializador()
    {
        const string nombre = "Push \"A\" </script>";
        var p = Calcular(Dia(D(0), "press-banca", rutina: nombre).Concat(Dia(D(2), "press-banca", rutina: nombre)));
        Assert.Equal(new RutinaFrecuente(nombre, 2), Assert.Single(p.Habitos!.RutinasFrecuentes));

        var c = CalculosPerfil.ParaCoach(p, PerfilCoach.PorDefecto, Hoy);
        Assert.Equal(nombre, Assert.Single(c.Habitos!.RutinasFrecuentes).Nombre);
        var json = JsonSerializer.Serialize(c, Http.Json);
        Assert.DoesNotContain("</", json);
        Assert.DoesNotContain("\"A\"", json);
    }
}
