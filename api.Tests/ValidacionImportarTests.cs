using System.Text.Json.Nodes;

namespace GymCoach.Api.Tests;

public class ValidacionImportarTests
{
    // Mismo "ahora" que ValidacionTests: el ts de Serie() (00:30 UTC del 7) queda dentro de ahora+1h.
    static readonly DateTimeOffset Ahora = new(2026, 10, 6, 23, 45, 0, TimeSpan.Zero);

    static JsonObject Serie(string? id = null, int reps = 8, string ejercicio = "press-pecho-maquina") => new()
    {
        ["id"] = id ?? Guid.NewGuid().ToString("D"),
        ["ejercicioId"] = ejercicio,
        ["pesoKg"] = 50,
        ["reps"] = reps,
        ["fecha"] = "2026-10-06",
        ["ts"] = "2026-10-06T18:30:00-06:00",
    };

    static string Lote(params JsonObject[] series) => new JsonObject { ["series"] = new JsonArray([.. series]) }.ToJsonString();

    static List<ErrorCampo> Errores(string body)
    {
        var (lote, errores) = Validacion.Importar(body, Ahora);
        Assert.Null(lote);
        Assert.NotEmpty(errores);
        return errores;
    }

    [Theory]
    [InlineData("{}", "series")]
    [InlineData("""{"series":[]}""", "series")]
    [InlineData("""{"series":{}}""", "series")]
    [InlineData("[]", "body")]
    [InlineData("\"hola\"", "body")]
    [InlineData("no es json", "body")]
    [InlineData("""{"series":[],"series":[]}""", "body")]
    public void BodyMalo_400(string body, string campo) => Assert.Equal(campo, Assert.Single(Errores(body)).Campo);

    [Fact]
    public void MasDe10_400()
    {
        var series = Enumerable.Range(0, Validacion.MaxSeriesImportar + 1).Select(_ => Serie()).ToArray();
        Assert.Equal("series", Assert.Single(Errores(Lote(series))).Campo);
    }

    [Fact]
    public void MasDe8KB_400()
    {
        var body = new JsonObject { ["series"] = new JsonArray(Serie()), ["relleno"] = new string('x', Validacion.MaxBodyImportarBytes) }.ToJsonString();
        Assert.Equal("body", Assert.Single(Errores(body)).Campo);
    }

    // El tope de bytes no debe cortar un lote legítimo: 10 series con todos los campos al máximo y rutina de 40 caracteres de 3 bytes.
    [Fact]
    public void LoteLlenoAlMaximo_CabeEnElTope()
    {
        var series = Enumerable.Range(0, Validacion.MaxSeriesImportar).Select(_ =>
        {
            var s = Serie(ejercicio: "press-inclinado-mancuernas");
            s["pesoKg"] = 499.99;
            s["reps"] = 100;
            s["ts"] = "2026-10-06T18:30:00.1234567-06:00";
            s["rutina"] = new string('€', Validacion.MaxNombreRutina);
            s["tipoSerie"] = "calentamiento";
            return s;
        }).ToArray();
        var body = Lote(series);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(body) < Validacion.MaxBodyImportarBytes);
        var (lote, errores) = Validacion.Importar(body, Ahora);
        Assert.Empty(errores);
        Assert.Equal(Validacion.MaxSeriesImportar, lote!.Count);
    }

    [Fact]
    public void RepsCero_ErrorConIndice()
    {
        var errores = Errores(Lote(Serie(), Serie(reps: 0)));
        Assert.Equal("series[1].reps", Assert.Single(errores).Campo);
    }

    [Fact]
    public void EjercicioInexistente_400()
    {
        var errores = Errores(Lote(Serie(ejercicio: "no-existe")));
        Assert.Equal("series[0].ejercicioId", Assert.Single(errores).Campo);
    }

    [Fact]
    public void IdInvalido_400() =>
        Assert.Equal("series[0].id", Assert.Single(Errores(Lote(Serie(id: "no-es-uuid")))).Campo);

    [Fact]
    public void ElementoNoObjeto_400()
    {
        var body = new JsonObject { ["series"] = new JsonArray(Serie(), 5) }.ToJsonString();
        Assert.Equal("series[1]", Assert.Single(Errores(body)).Campo);
    }

    [Fact]
    public void IdsRepetidos_400()
    {
        var id = Guid.NewGuid().ToString("D");
        var errores = Errores(Lote(Serie(id), Serie(id.ToUpperInvariant())));
        Assert.Equal("series[1].id", Assert.Single(errores).Campo);
    }

    [Fact]
    public void LoteValido_IdsEnMinusculas()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() }.Select(g => g.ToString("D")).ToArray();
        var (lote, errores) = Validacion.Importar(Lote([.. ids.Select(i => Serie(i.ToUpperInvariant()))]), Ahora);
        Assert.Empty(errores);
        Assert.Equal(ids, lote!.Select(s => s.Id));
        Assert.All(lote!, s => Assert.Equal("2026-10-07T00:30:00.000Z", s.Ts));
    }
}
