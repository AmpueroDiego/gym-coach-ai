namespace GymCoach.Api.Tests;

public class CalculosCoachTests
{
    static readonly DateOnly Hoy = new(2026, 10, 9);

    static Serie S(string ejercicio, decimal peso, int reps, int haceDias, string? tipo = null, string? rutina = null)
    {
        var fecha = Hoy.AddDays(-haceDias).ToString("yyyy-MM-dd");
        return new(Guid.NewGuid().ToString(), ejercicio, peso, reps, fecha, fecha + "T10:00:00.000Z", rutina, tipo);
    }

    /// <summary>Banca una vez por semana, 6 semanas: top set 60 → 67.5 ×5, 3 series + calentamiento 40×10.</summary>
    static List<Serie> Banca()
    {
        decimal[] tops = [60, 62.5m, 62.5m, 65, 65, 67.5m];
        var series = new List<Serie>();
        for (var i = 0; i < tops.Length; i++)
        {
            var hace = 7 * (tops.Length - 1 - i) + 1;
            series.Add(S("press-banca", 40, 10, hace, "calentamiento"));
            for (var j = 0; j < 3; j++) series.Add(S("press-banca", tops[i], 5, hace));
        }
        return series;
    }

    [Fact]
    public void Progreso_Banca_SubeSieteYMedio()
    {
        var p = CalculosCoach.Progreso(Banca(), "press-banca", Hoy, 8);

        Assert.False(p.SinDatos);
        Assert.Equal(8, p.PorSemana.Count);
        Assert.Equal("2026-08-15", p.Desde);
        Assert.Equal("2026-10-09", p.Hasta);
        Assert.Equal("Press de banca", p.Nombre);
        Assert.Equal("pecho", p.Grupo);
        var r = p.Resumen!;
        Assert.Equal(60m, r.Primera.MejorPesoKg);
        Assert.Equal(67.5m, r.Ultima.MejorPesoKg);
        Assert.Equal(7.5m, r.CambioMejorPesoKg);
        Assert.Equal(78.75m, r.Ultima.E1rmMaxKg);
        Assert.Equal(78.75m - 70m, r.CambioE1rmKg);
    }

    [Fact]
    public void Progreso_CalentamientoNoCuenta()
    {
        var p = CalculosCoach.Progreso(Banca(), "press-banca", Hoy, 8);
        var ultima = p.PorSemana[^1];

        Assert.Equal(3, ultima.Series);
        Assert.Equal(15, ultima.Reps);
        Assert.Equal(67.5m * 15, ultima.VolumenKg);
        Assert.Equal(1, ultima.Sesiones);
    }

    [Fact]
    public void Progreso_SemanasVaciasEnCero()
    {
        var p = CalculosCoach.Progreso(Banca(), "press-banca", Hoy, 8);

        // 8 semanas, datos en las 6 últimas: las 2 más viejas en 0.
        Assert.All(p.PorSemana.Take(2), s =>
        {
            Assert.Equal(0, s.Series);
            Assert.Equal(0m, s.VolumenKg);
            Assert.Null(s.MejorSerie);
            Assert.Null(s.E1rmMaxKg);
        });
        Assert.Equal(p.PorSemana[2].Desde, p.Resumen!.Primera.Desde);
    }

    [Fact]
    public void Progreso_SinSeries_SinDatosYResumenNull()
    {
        var p = CalculosCoach.Progreso([], "hip-thrust", Hoy, 8);

        Assert.True(p.SinDatos);
        Assert.Null(p.Resumen);
        Assert.Equal(8, p.PorSemana.Count);
    }

    [Fact]
    public void Progreso_SoloCalentamientos_SinDatos()
    {
        var p = CalculosCoach.Progreso([S("press-banca", 40, 10, 1, "calentamiento")], "press-banca", Hoy, 8);

        Assert.True(p.SinDatos);
        Assert.Null(p.Resumen);
    }

    [Fact]
    public void Progreso_IgnoraOtrosEjerciciosYFueraDeVentana()
    {
        var p = CalculosCoach.Progreso([S("sentadilla", 100, 5, 1), S("press-banca", 90, 5, 14), S("press-banca", 50, 5, -1)],
            "press-banca", Hoy, 2);

        Assert.True(p.SinDatos);
    }

    [Fact]
    public void MejorSerie_EmpatePorReps()
    {
        var p = CalculosCoach.Progreso([S("press-banca", 60, 5, 0), S("press-banca", 60, 7, 0), S("press-banca", 55, 12, 0)],
            "press-banca", Hoy, 2);
        Assert.Equal(new MejorSerieSemana(60, 7, Hoy.ToString("yyyy-MM-dd")), p.PorSemana[^1].MejorSerie);

        var r = CalculosCoach.Recientes([S("press-banca", 60, 5, 0), S("press-banca", 60, 7, 0)], Hoy, 7);
        Assert.Equal(new MejorSerieDia(60, 7), r.Dias[0].Ejercicios[0].MejorSerie);
    }

    [Fact]
    public void Semana_LimiteEnHoyMenosSeis()
    {
        // hoy-6 cae en la semana actual; hoy-7 en la anterior.
        var p = CalculosCoach.Progreso([S("press-banca", 60, 5, 6), S("press-banca", 70, 5, 7)], "press-banca", Hoy, 2);

        Assert.Equal("2026-10-03", p.PorSemana[1].Desde);
        Assert.Equal(60m, p.PorSemana[1].MejorSerie!.PesoKg);
        Assert.Equal("2026-09-26", p.PorSemana[0].Desde);
        Assert.Equal("2026-10-02", p.PorSemana[0].Hasta);
        Assert.Equal(70m, p.PorSemana[0].MejorSerie!.PesoKg);
    }

    [Fact]
    public void Volumen_TodosLosGruposDelCatalogo()
    {
        var v = CalculosCoach.Volumen([], Hoy, 4);

        var grupos = Catalogo.Ejercicios.Select(e => e.Grupo).Distinct().ToList();
        Assert.Equal(grupos, v.Grupos.Select(g => g.Grupo));
        Assert.True(v.SinDatos);
        Assert.Equal(4, v.Semanas.Count);
        Assert.All(v.Grupos, g =>
        {
            Assert.Equal(4, g.PorSemana.Count);
            Assert.Null(g.CambioSeriesPct);
        });
    }

    [Fact]
    public void Volumen_EspaldaBajando()
    {
        // 8 series/semana en las semanas 4 a 2 (k=3..1), 3 en la actual; calentamientos no cuentan.
        var series = new List<Serie>();
        for (var k = 3; k >= 1; k--)
            for (var i = 0; i < 4; i++)
            {
                series.Add(S("remo-barra", 60, 10, 7 * k + 1));
                series.Add(S("jalon-pecho", 50, 10, 7 * k + 2));
            }
        for (var i = 0; i < 3; i++) series.Add(S("remo-barra", 60, 10, 1));
        series.Add(S("remo-barra", 30, 10, 1, "calentamiento"));

        var espalda = CalculosCoach.Volumen(series, Hoy, 4).Grupos.Single(g => g.Grupo == "espalda");

        Assert.Equal([8, 8, 8, 3], espalda.PorSemana.Select(s => s.Series));
        Assert.Equal(3, espalda.SeriesUltimaSemana);
        Assert.Equal(8m, espalda.PromedioSeriesSemanasAnteriores);
        Assert.Equal(-62.5m, espalda.CambioSeriesPct);
        Assert.Equal(1800m, espalda.PorSemana[^1].VolumenKg);
    }

    [Fact]
    public void Volumen_UnaSemana_SinDividirEntreCero()
    {
        var espalda = CalculosCoach.Volumen([S("remo-barra", 60, 10, 1)], Hoy, 1).Grupos.Single(g => g.Grupo == "espalda");

        Assert.Equal(1, espalda.SeriesUltimaSemana);
        Assert.Equal(0m, espalda.PromedioSeriesSemanasAnteriores);
        Assert.Null(espalda.CambioSeriesPct);
    }

    [Fact]
    public void Recientes_AgrupaPorDiaYEjercicio()
    {
        var series = new List<Serie>
        {
            S("press-banca", 40, 10, 1, "calentamiento", "Empuje"),
            S("press-banca", 60, 5, 1, rutina: "Empuje"),
            S("press-banca", 62.5m, 5, 1, rutina: "Empuje"),
            S("fondos", 0, 12, 1, rutina: "Empuje"),
            S("sentadilla", 100, 5, 0),
            S("sentadilla", 100, 5, 8),
        };

        var r = CalculosCoach.Recientes(series, Hoy, 7);

        Assert.False(r.SinDatos);
        Assert.Equal("2026-10-03", r.Desde);
        Assert.Equal(["2026-10-08", "2026-10-09"], r.Dias.Select(d => d.Fecha));
        var ayer = r.Dias[0];
        Assert.Equal(["Empuje"], ayer.Rutinas);
        Assert.Equal(["press-banca", "fondos"], ayer.Ejercicios.Select(e => e.EjercicioId));
        var banca = ayer.Ejercicios[0];
        Assert.Equal(2, banca.Series);
        Assert.Equal(1, banca.SeriesCalentamiento);
        Assert.Equal(new MejorSerieDia(62.5m, 5), banca.MejorSerie);
        Assert.Equal(612.5m, banca.VolumenKg);
        Assert.Equal(10, banca.Reps);
        Assert.True(ayer.Ejercicios[1].CargaCorporal);
        Assert.Empty(r.Dias[1].Rutinas);
    }

    [Fact]
    public void Recientes_Vacio()
    {
        var r = CalculosCoach.Recientes([], Hoy, 7);

        Assert.True(r.SinDatos);
        Assert.Empty(r.Dias);
    }

    [Fact]
    public void MinutosEstimados_TresEjerciciosCuatroSeries()
    {
        var ejercicios = Enumerable.Range(0, 3).Select(_ => new EjercicioRutina("press-banca", 4, 8, null, 90));
        Assert.Equal(26, CalculosCoach.MinutosEstimados(ejercicios));
        // Redondea hacia arriba: 1 × 50 s → 1 min.
        Assert.Equal(1, CalculosCoach.MinutosEstimados([new EjercicioRutina("press-banca", 1, 8, null, 10)]));
    }
}
