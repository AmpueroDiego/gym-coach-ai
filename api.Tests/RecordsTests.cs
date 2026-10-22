namespace GymCoach.Api.Tests;

public class RecordsTests
{
    static Serie S(decimal peso, int reps, string fecha = "2026-10-01", string? tipo = null, string ejercicio = "press-banca") =>
        new(Guid.NewGuid().ToString(), ejercicio, peso, reps, fecha, fecha + "T10:00:00.000Z", TipoSerie: tipo);

    [Theory]
    [InlineData(100, 1, 100)]
    [InlineData(100, 10, 133.33)]
    [InlineData(80, 5, 93.33)]
    [InlineData(60, 12, 84)]
    public void E1rm_Epley(decimal peso, int reps, decimal esperado) =>
        Assert.Equal(esperado, CalculoRecords.E1rm(peso, reps));

    [Theory]
    [InlineData(100, 13)]
    [InlineData(0, 5)]
    [InlineData(0, 1)]
    public void E1rm_SinDato(decimal peso, int reps) =>
        Assert.Null(CalculoRecords.E1rm(peso, reps));

    [Fact]
    public void Calentamiento_NoCuenta()
    {
        Assert.Null(CalculoRecords.Fusionar(null, S(200, 5, tipo: "calentamiento")));
        var r = CalculoRecords.Desde([S(100, 5), S(140, 3, tipo: "calentamiento")], "press-banca")!;
        Assert.Equal(100m, r.PesoMax.PesoKg);
        Assert.Equal(5, r.RepsMax.Reps);
    }

    [Theory]
    [InlineData("fallo")]
    [InlineData("drop")]
    [InlineData(null)]
    public void FalloDropYNormal_Cuentan(string? tipo) =>
        Assert.Equal(100m, CalculoRecords.Fusionar(null, S(100, 5, tipo: tipo))!.PesoMax.PesoKg);

    [Fact]
    public void Primera_SerieArmaTodasLasMarcas()
    {
        var r = CalculoRecords.Fusionar(null, S(100, 10, "2026-10-02"))!;
        Assert.Equal(new RecordEjercicio("press-banca", new(100, 10, "2026-10-02"), new(133.33m, 100, 10, "2026-10-02"),
            new(10, 100, "2026-10-02")), r);
    }

    [Fact]
    public void Empate_NoBate()
    {
        var r = CalculoRecords.Fusionar(null, S(100, 5, "2026-10-01"))!;
        Assert.Null(CalculoRecords.Fusionar(r, S(100, 5, "2026-10-05")));
        // En el recálculo también gana la primera.
        Assert.Equal("2026-10-01", CalculoRecords.Desde([S(100, 5, "2026-10-01"), S(100, 5, "2026-10-05")], "press-banca")!.PesoMax.Fecha);
    }

    [Fact]
    public void Fusionar_SoloCambiaLaMarcaQueMejora()
    {
        var r = CalculoRecords.Fusionar(null, S(100, 5, "2026-10-01"))!;
        // Menos peso pero más reps: sube repsMax y e1rm (90*1.4=126 > 100*35/30=116.67), no pesoMax.
        var nuevo = CalculoRecords.Fusionar(r, S(90, 12, "2026-10-03"))!;
        Assert.Equal(r.PesoMax, nuevo.PesoMax);
        Assert.Equal(new MarcaE1rm(126, 90, 12, "2026-10-03"), nuevo.E1rm);
        Assert.Equal(new MarcaReps(12, 90, "2026-10-03"), nuevo.RepsMax);
    }

    // En carga corporal pesoKg es el lastre: con 0 no hay e1rm ni sube pesoMax, pero repsMax sí.
    [Fact]
    public void CargaCorporal_RepsMax()
    {
        var r = CalculoRecords.Fusionar(null, S(0, 8, ejercicio: "dominadas"))!;
        Assert.Null(r.E1rm);
        var nuevo = CalculoRecords.Fusionar(r, S(0, 10, "2026-10-03", ejercicio: "dominadas"))!;
        Assert.Equal(new MarcaReps(10, 0, "2026-10-03"), nuevo.RepsMax);
        Assert.Null(nuevo.E1rm);
        Assert.Null(CalculoRecords.Fusionar(nuevo, S(0, 10, ejercicio: "dominadas")));
    }

    // Sin e1rm previo (todo era > 12 reps), la primera serie con e1rm lo crea.
    [Fact]
    public void E1rm_SeCreaAunqueNoSubaPesoNiReps()
    {
        var r = CalculoRecords.Fusionar(null, S(50, 15))!;
        var nuevo = CalculoRecords.Fusionar(r, S(50, 10))!;
        Assert.Equal(66.67m, nuevo.E1rm!.Kg);
        Assert.Equal(15, nuevo.RepsMax.Reps);
    }

    [Fact]
    public void Desde_IgnoraOtrosEjercicios_YVacioEsNull()
    {
        Assert.Null(CalculoRecords.Desde([], "press-banca"));
        Assert.Null(CalculoRecords.Desde([S(100, 5, ejercicio: "sentadilla")], "press-banca"));
        Assert.Null(CalculoRecords.Desde([S(100, 5, tipo: "calentamiento")], "press-banca"));
    }

    [Fact]
    public void PodriaSerDuena()
    {
        var r = CalculoRecords.Desde([S(100, 5), S(80, 12)], "press-banca")!;
        Assert.True(CalculoRecords.PodriaSerDuena(r, S(100, 5)));
        Assert.True(CalculoRecords.PodriaSerDuena(r, S(80, 12)));
        Assert.False(CalculoRecords.PodriaSerDuena(r, S(90, 6)));
        Assert.False(CalculoRecords.PodriaSerDuena(r, S(100, 5, tipo: "calentamiento")));
        Assert.False(CalculoRecords.PodriaSerDuena(r, S(100, 5, ejercicio: "sentadilla")));
    }

    [Fact]
    public void Empeora()
    {
        var vieja = S(100, 10);
        Assert.False(CalculoRecords.Empeora(vieja, vieja));
        Assert.False(CalculoRecords.Empeora(vieja, vieja with { PesoKg = 105 }));
        Assert.False(CalculoRecords.Empeora(vieja, vieja with { TipoSerie = "fallo" }));
        Assert.True(CalculoRecords.Empeora(vieja, vieja with { PesoKg = 95 }));
        Assert.True(CalculoRecords.Empeora(vieja, vieja with { Reps = 9 }));
        Assert.True(CalculoRecords.Empeora(vieja, vieja with { TipoSerie = "calentamiento" }));
        Assert.True(CalculoRecords.Empeora(vieja, vieja with { EjercicioId = "sentadilla" }));
        // Más reps pero pierde el e1rm (más de 12).
        Assert.True(CalculoRecords.Empeora(vieja, vieja with { Reps = 13 }));
        // Un calentamiento nunca sostuvo un récord.
        Assert.False(CalculoRecords.Empeora(vieja with { TipoSerie = "calentamiento" }, vieja with { PesoKg = 1 }));
    }

    // La marca guarda la fecha y el ts decide los empates: cambiarlos también obliga a recalcular.
    [Fact]
    public void Empeora_CambioDeFechaOTs()
    {
        var vieja = S(100, 10);
        Assert.True(CalculoRecords.Empeora(vieja, vieja with { Fecha = "2026-09-30" }));
        Assert.True(CalculoRecords.Empeora(vieja, vieja with { Ts = "2026-10-01T11:00:00.000Z" }));
    }

    [Fact]
    public void RecordDoc_EsValido()
    {
        var r = CalculoRecords.Fusionar(null, S(100, 5))!;
        var doc = RecordDoc.De(r, "google:yo", "2026-10-01T10:00:00.000Z");
        Assert.True(doc.EsValido("press-banca"));
        Assert.False(doc.EsValido("sentadilla"));
        Assert.False((doc with { PesoMax = null! }).EsValido("press-banca"));
        Assert.False((doc with { RepsMax = null! }).EsValido("press-banca"));
        Assert.False((doc with { PesoMax = r.PesoMax with { Fecha = null! } }).EsValido("press-banca"));
        Assert.False((doc with { E1rm = r.E1rm! with { Fecha = null! } }).EsValido("press-banca"));
        Assert.False((doc with { Tipo = "serie" }).EsValido("press-banca"));
        Assert.True((doc with { E1rm = null }).EsValido("press-banca"));
    }

    // desactualizado solo se escribe en true: el reemplazo de un recálculo lo borra.
    [Fact]
    public void RecordDoc_DesactualizadoSoloSiEsTrue()
    {
        var doc = RecordDoc.De(CalculoRecords.Fusionar(null, S(100, 5))!, "google:yo", "x");
        var web = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        Assert.DoesNotContain("desactualizado", System.Text.Json.JsonSerializer.Serialize(doc, web));
        Assert.Contains("\"desactualizado\":true", System.Text.Json.JsonSerializer.Serialize(doc with { Desactualizado = true }, web));
    }
}
