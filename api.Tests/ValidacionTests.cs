using System.Text.Json;
using System.Text.Json.Nodes;

namespace GymCoach.Api.Tests;

public class ValidacionTests
{
    // 23:45 UTC del 6: "hoy" en UTC es el 6, y el ts de Valida() (00:30 UTC del 7) queda dentro de ahora+1h.
    static readonly DateTimeOffset Ahora = new(2026, 10, 6, 23, 45, 0, TimeSpan.Zero);
    const string Id = "3f2c1a9e-8b7d-4c6e-9f10-1234567890ab";

    static JsonObject Valida() => new()
    {
        ["ejercicioId"] = "press-banca",
        ["pesoKg"] = 62.5,
        ["reps"] = 8,
        ["fecha"] = "2026-10-06",
        ["ts"] = "2026-10-06T18:30:00-06:00",
    };

    static List<ErrorCampo> Errores(JsonObject body, string id = Id) =>
        Validacion.Serie(id, body.ToJsonString(), Ahora).Errores;

    static JsonObject Con(string campo, JsonNode? valor)
    {
        var b = Valida();
        b[campo] = valor;
        return b;
    }

    [Fact]
    public void SerieValida_SeNormaliza()
    {
        var (serie, errores) = Validacion.Serie(Id.ToUpperInvariant(), Valida().ToJsonString(), Ahora);
        Assert.Empty(errores);
        Assert.Equal(new Serie(Id, "press-banca", 62.5m, 8, "2026-10-06", "2026-10-07T00:30:00.000Z"), serie);
    }

    public static TheoryData<string, JsonNode?> CamposMalos => new()
    {
        { "ejercicioId", "no-existe" },
        { "ejercicioId", 5 },
        { "ejercicioId", null },
        { "pesoKg", -0.5 },
        { "pesoKg", 500.5 },
        { "pesoKg", 62.555 },
        { "pesoKg", "60" },
        { "pesoKg", null },
        { "reps", 0 },
        { "reps", 101 },
        { "reps", 8.5 },
        { "reps", JsonNode.Parse("8.0") },
        { "reps", "8" },
        { "fecha", "1999-12-31" },
        { "fecha", "2026-10-08" },
        { "fecha", "2026-02-30" },
        { "fecha", "06/10/2026" },
        { "fecha", 20261006 },
        { "ts", "ayer" },
        { "ts", "2026-10-06" },
        { "ts", "2026-10-06T18:30:00" },
        { "ts", 1759790000 },
        { "tipoSerie", "Fallo" },
        { "tipoSerie", "" },
        { "tipoSerie", 1 },
        { "tipoSerie", new JsonObject { ["tipo"] = "fallo" } },
    };

    [Theory]
    [MemberData(nameof(CamposMalos))]
    public void CampoMalo_SeRechazaSoloEseCampo(string campo, JsonNode? valor)
    {
        var errores = Errores(Con(campo, valor));
        Assert.Equal([campo], errores.Select(e => e.Campo).Distinct());
    }

    [Theory]
    [InlineData("ejercicioId")]
    [InlineData("pesoKg")]
    [InlineData("reps")]
    [InlineData("fecha")]
    [InlineData("ts")]
    public void CampoFaltante_SeRechaza(string campo)
    {
        var b = Valida();
        b.Remove(campo);
        Assert.Equal([campo], Errores(b).Select(e => e.Campo));
    }

    [Fact]
    public void LimitesIncluidos_SonValidos()
    {
        Assert.Empty(Errores(Con("pesoKg", 0)));
        Assert.Empty(Errores(Con("pesoKg", 500)));
        Assert.Empty(Errores(Con("reps", 1)));
        Assert.Empty(Errores(Con("reps", 100)));
        Assert.Empty(Errores(Con("fecha", "2026-10-07")));
        var b = Con("fecha", "2000-01-01");
        b["ts"] = "2000-01-01T10:00:00Z";
        Assert.Empty(Errores(b));
    }

    [Theory]
    [InlineData("2026-10-07T00:44:59Z")] // ahora + 59:59
    [InlineData("2026-10-07T00:45:00Z")] // ahora + 1h justo
    public void Ts_HastaUnaHoraEnElFuturo_Valido(string ts) =>
        Assert.Empty(Errores(Con("ts", ts)));

    [Fact]
    public void Ts_MasDeUnaHoraEnElFuturo_Invalido() =>
        Assert.Equal(["ts"], Errores(Con("ts", "2026-10-07T00:45:01Z")).Select(e => e.Campo));

    // fecha = día local del cliente; con offsets de -12h a +14h difiere a lo sumo un día del día UTC de ts.
    [Theory]
    [InlineData("2026-10-06", "2026-10-05T12:30:00-11:00")] // UTC 05-10 23:30, cliente en UTC-11
    [InlineData("2026-10-06", "2026-10-06T00:30:00+14:00")] // UTC 05-10 10:30, cliente en UTC+14
    [InlineData("2026-10-05", "2026-10-06T10:00:00Z")]
    [InlineData("2026-10-07", "2026-10-06T10:00:00Z")]
    public void Ts_FechaVecinaPorZonaHoraria_Valido(string fecha, string ts)
    {
        var b = Con("fecha", fecha);
        b["ts"] = ts;
        Assert.Empty(Errores(b));
    }

    [Theory]
    [InlineData("2026-10-03", "2026-10-06T10:00:00Z")]
    [InlineData("2026-10-04", "2026-10-06T00:00:00Z")]
    [InlineData("2026-10-06", "2026-10-04T23:59:59Z")]
    public void Ts_MasDeUnDiaDeLaFecha_Invalido(string fecha, string ts)
    {
        var b = Con("fecha", fecha);
        b["ts"] = ts;
        Assert.Equal(["ts"], Errores(b).Select(e => e.Campo));
    }

    [Theory]
    [InlineData("no-es-uuid")]
    [InlineData("")]
    [InlineData("3f2c1a9e8b7d4c6e9f101234567890ab")]
    public void IdNoUuid_SeRechaza(string id) =>
        Assert.Equal(["id"], Errores(Valida(), id).Select(e => e.Campo));

    [Theory]
    [InlineData("no json")]
    [InlineData("")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData("\"texto\"")]
    public void BodyNoObjeto_SeRechaza(string body) =>
        Assert.Contains(Validacion.Serie(Id, body, Ahora).Errores, e => e.Campo == "body");

    [Fact]
    public void CamposExtra_IncluidoUserId_SeIgnoran()
    {
        var b = Valida();
        b["userId"] = "google:otro";
        b["_etag"] = "x";
        b["tipo"] = "hackeo";
        var (serie, errores) = Validacion.Serie(Id, b.ToJsonString(), Ahora);
        Assert.Empty(errores);

        var doc = SerieDoc.De(serie!, "google:yo");
        Assert.Equal("google:yo", doc.UserId);
        Assert.Equal("serie", doc.Tipo);
        Assert.DoesNotContain("otro", JsonSerializer.Serialize(doc));
    }

    [Theory]
    [InlineData("2026-01-01", "2026-04-02")] // 92 días
    [InlineData("2026-10-06", "2026-10-06")]
    public void RangoValido(string desde, string hasta) =>
        Assert.Empty(Validacion.Rango(desde, hasta).Errores);

    [Theory]
    [InlineData("2026-01-01", "2026-04-03")] // 93 días
    [InlineData("2026-10-07", "2026-10-06")]
    [InlineData(null, "2026-10-06")]
    [InlineData("2026-10-06", null)]
    [InlineData("06-10-2026", "2026-10-06")]
    public void RangoInvalido(string? desde, string? hasta) =>
        Assert.NotEmpty(Validacion.Rango(desde, hasta).Errores);

    [Theory]
    [InlineData(null, 20)]
    [InlineData("1", 1)]
    [InlineData("50", 50)]
    public void LimiteValido(string? valor, int esperado) =>
        Assert.Equal(esperado, Validacion.Limite(valor).Limite);

    [Theory]
    [InlineData("0")]
    [InlineData("51")]
    [InlineData("-1")]
    [InlineData("abc")]
    public void LimiteInvalido(string valor) =>
        Assert.NotEmpty(Validacion.Limite(valor).Errores);

    [Fact]
    public void Catalogo_IdsUnicosYGruposConocidos()
    {
        string[] grupos = ["pecho", "espalda", "hombros", "biceps", "triceps", "cuadriceps", "isquios", "gluteos", "aductores", "pantorrillas", "core"];
        Assert.Equal(Catalogo.Ejercicios.Count, Catalogo.Ejercicios.Select(e => e.Id).Distinct().Count());
        Assert.All(Catalogo.Ejercicios, e => Assert.Contains(e.Grupo, grupos));
    }

    [Fact]
    public void Catalogo_CamposCompletos()
    {
        Assert.All(Catalogo.Ejercicios, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Id));
            Assert.False(string.IsNullOrWhiteSpace(e.Nombre));
            Assert.False(string.IsNullOrWhiteSpace(e.NombreEn));
            Assert.Contains(e.Equipo, new[] { Catalogo.Maquina, Catalogo.Libre });
        });
    }

    // Los ids se guardan en las series: ninguno que ya existía puede desaparecer.
    [Fact]
    public void Catalogo_IdsPreviosSiguenExistiendo()
    {
        string[] previos = ["press-banca", "press-inclinado-barra", "press-banca-mancuernas", "press-inclinado-mancuernas",
            "aperturas-mancuernas", "cruce-poleas", "fondos", "lagartijas", "peso-muerto", "dominadas", "jalon-pecho", "remo-barra",
            "remo-mancuerna", "remo-polea-baja", "press-militar", "press-hombro-mancuernas", "elevaciones-laterales", "pajaros",
            "face-pull", "curl-barra", "curl-mancuernas", "curl-martillo", "extension-triceps-polea", "press-frances",
            "press-banca-cerrado", "sentadilla", "sentadilla-frontal", "prensa", "extension-cuadriceps", "zancadas",
            "peso-muerto-rumano", "curl-femoral", "hip-thrust", "patada-gluteo-polea", "elevacion-talones-pie",
            "elevacion-talones-sentado", "rueda-abdominal", "crunch-polea", "elevacion-piernas-colgado"];
        Assert.All(previos, id => Assert.True(Catalogo.Existe(id), id));
    }

    [Fact]
    public void Serie_ConRutina_SeGuardaConTrim()
    {
        var (serie, errores) = Validacion.Serie(Id, Con("rutina", "  Pierna 🦵 ").ToJsonString(), Ahora);
        Assert.Empty(errores);
        Assert.Equal("Pierna 🦵", serie!.Rutina);
        Assert.Equal("Pierna 🦵", SerieDoc.De(serie, "google:yo").Rutina);
    }

    [Fact]
    public void Serie_RutinaAusenteONull_SinRutina()
    {
        Assert.Null(Validacion.Serie(Id, Valida().ToJsonString(), Ahora).Serie!.Rutina);
        var (serie, _) = Validacion.Serie(Id, Con("rutina", null).ToJsonString(), Ahora);
        Assert.Null(serie!.Rutina);
        // Sin rutina el campo no se escribe en el documento.
        Assert.DoesNotContain("rutina", JsonSerializer.Serialize(SerieDoc.De(serie, "google:yo"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    public static TheoryData<JsonNode?> RutinasMalas => new()
    {
        "",
        "   ",
        new string('a', 41),
        "ab",
        5,
        true,
        new JsonArray("Pierna"),
    };

    [Theory]
    [MemberData(nameof(RutinasMalas))]
    public void Serie_RutinaMala_SeRechaza(JsonNode? valor) =>
        Assert.Equal(["rutina"], Errores(Con("rutina", valor)).Select(e => e.Campo));

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "normal")]
    public void Serie_TipoSerieNormal_SeGuardaComoNull(bool presente, string? valor)
    {
        var b = presente ? Con("tipoSerie", valor) : Valida();
        var (serie, errores) = Validacion.Serie(Id, b.ToJsonString(), Ahora);
        Assert.Empty(errores);
        Assert.Null(serie!.TipoSerie);
        // Normal no se escribe: series viejas y normales nuevas son el mismo documento.
        Assert.DoesNotContain("tipoSerie", JsonSerializer.Serialize(SerieDoc.De(serie, "google:yo"), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Theory]
    [InlineData("calentamiento")]
    [InlineData("fallo")]
    [InlineData("drop")]
    public void Serie_TipoSerieValido_SeGuarda(string valor)
    {
        var (serie, errores) = Validacion.Serie(Id, Con("tipoSerie", valor).ToJsonString(), Ahora);
        Assert.Empty(errores);
        Assert.Equal(valor, serie!.TipoSerie);
        Assert.Equal(valor, SerieDoc.De(serie, "google:yo").TipoSerie);
    }

    [Fact]
    public void Serie_TipoSerieMalo_MensajeDelContrato() =>
        Assert.Equal([new ErrorCampo("tipoSerie", "Debe ser calentamiento, normal, fallo o drop.")], Errores(Con("tipoSerie", "Fallo")));

    // Claves repetidas son ambiguas (¿cuál vale?): 400 aunque las dos sean válidas.
    [Fact]
    public void Serie_TipoSerieRepetido_SeRechaza()
    {
        var body = Valida().ToJsonString()[..^1] + ""","tipoSerie":"normal","tipoSerie":"calentamiento"}""";
        Assert.Contains(Validacion.Serie(Id, body, Ahora).Errores, e => e.Campo == "body");
    }
}
