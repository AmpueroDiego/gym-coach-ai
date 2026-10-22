using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GymCoach.Api.Tests;

public class RutinasValidacionTests
{
    static readonly DateTimeOffset Ahora = new(2026, 10, 6, 23, 45, 0, TimeSpan.Zero);
    const string Id = "3f2c1a9e-8b7d-4c6e-9f10-1234567890ab";

    static JsonObject Ej(string id = "sentadilla") => new()
    {
        ["id"] = id,
        ["series"] = 4,
        ["reps"] = 8,
        ["pesoKg"] = 80,
        ["descansoS"] = 120,
    };

    static JsonObject Valida() => new() { ["nombre"] = "Pierna", ["ejercicios"] = new JsonArray(Ej()) };

    static List<ErrorCampo> Errores(JsonObject body, string id = Id) =>
        Validacion.Rutina(id, body.ToJsonString(), Ahora).Errores;

    static List<string> Campos(JsonObject body, string id = Id) => Errores(body, id).Select(e => e.Campo).Distinct().ToList();

    static JsonObject Con(string campo, JsonNode? valor)
    {
        var b = Valida();
        b[campo] = valor;
        return b;
    }

    static JsonObject ConEj(string campo, JsonNode? valor)
    {
        var e = Ej();
        e[campo] = valor;
        return Con("ejercicios", new JsonArray(e));
    }

    [Fact]
    public void Valida_SeNormaliza()
    {
        var b = Con("nombre", "  Pierna  ");
        var (rutina, errores) = Validacion.Rutina(Id.ToUpperInvariant(), b.ToJsonString(), Ahora);
        Assert.Empty(errores);
        Assert.Equal(Id, rutina!.Id);
        Assert.Equal("Pierna", rutina.Nombre);
        Assert.Equal("2026-10-06T23:45:00.000Z", rutina.Actualizada);
        Assert.Equal(new EjercicioRutina("sentadilla", 4, 8, 80m, 120), Assert.Single(rutina.Ejercicios));
    }

    [Fact]
    public void EjemploDelContrato_EsValido() =>
        Assert.Empty(Validacion.Rutina(Id,
            """{"nombre":"Pierna","ejercicios":[{"id":"sentadilla","series":4,"reps":8,"pesoKg":80,"descansoS":120}]}""", Ahora).Errores);

    [Fact]
    public void Ejercicios_ConservanOrden()
    {
        var b = Con("ejercicios", new JsonArray(Ej("prensa"), Ej("sentadilla"), Ej("curl-femoral")));
        Assert.Equal(["prensa", "sentadilla", "curl-femoral"],
            Validacion.Rutina(Id, b.ToJsonString(), Ahora).Rutina!.Ejercicios.Select(e => e.Id));
    }

    public static TheoryData<JsonNode?> NombresMalos => new()
    {
        "",
        "   ",
        new string('a', 41),
        "a\u0007b",
        5,
        null!,
    };

    [Theory]
    [MemberData(nameof(NombresMalos))]
    public void NombreMalo_SeRechaza(JsonNode? valor) => Assert.Equal(["nombre"], Campos(Con("nombre", valor)));

    [Fact]
    public void NombreAusente_SeRechaza()
    {
        var b = Valida();
        b.Remove("nombre");
        Assert.Equal(["nombre"], Campos(b));
    }

    [Fact]
    public void Nombre_40ConEspaciosYEmoji_Valido()
    {
        Assert.Empty(Errores(Con("nombre", "  " + new string('a', 40) + "  ")));
        Assert.Empty(Errores(Con("nombre", "Pierna 🦵")));
    }

    public static TheoryData<JsonNode?> EjerciciosMalos => new()
    {
        null!,
        "sentadilla",
        new JsonObject(),
        new JsonArray(),
        new JsonArray(Enumerable.Range(0, 16).Select(_ => (JsonNode?)Ej()).ToArray()),
    };

    [Theory]
    [MemberData(nameof(EjerciciosMalos))]
    public void EjerciciosMalos_SeRechazan(JsonNode? valor) => Assert.Equal(["ejercicios"], Campos(Con("ejercicios", valor)));

    [Fact]
    public void EjerciciosAusente_SeRechaza()
    {
        var b = Valida();
        b.Remove("ejercicios");
        Assert.Equal(["ejercicios"], Campos(b));
    }

    [Fact]
    public void Quince_DistintosConTodosLosCampos_CabenEnElBody()
    {
        var ids = Catalogo.Ejercicios.OrderByDescending(e => e.Id.Length).Take(Validacion.MaxEjerciciosRutina).Select(e => e.Id);
        var lista = new JsonArray(ids.Select(id => (JsonNode?)new JsonObject
        {
            ["id"] = id, ["series"] = 10, ["reps"] = 100, ["pesoKg"] = 499.75, ["descansoS"] = 600,
        }).ToArray());
        var b = Con("ejercicios", lista);
        b["nombre"] = new string('a', 40);
        var json = b.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        Assert.True(Encoding.UTF8.GetByteCount(json) <= Validacion.MaxBodyBytes);
        Assert.Empty(Validacion.Rutina(Id, json, Ahora).Errores);
    }

    [Fact]
    public void EjercicioDuplicado_SeRechaza() =>
        Assert.Equal(["ejercicios[1].id"], Campos(Con("ejercicios", new JsonArray(Ej(), Ej()))));

    [Fact]
    public void EjercicioInexistente_SeRechaza() =>
        Assert.Equal(["ejercicios[1].id"], Campos(Con("ejercicios", new JsonArray(Ej(), Ej("no-existe")))));

    [Theory]
    [InlineData("[1]")]
    [InlineData("""["sentadilla"]""")] // formato viejo: lista de ids
    [InlineData("[null]")]
    public void EjercicioNoObjeto_SeRechaza(string lista) =>
        Assert.Equal(["ejercicios[0]"], Campos(Con("ejercicios", JsonNode.Parse(lista))));

    [Fact]
    public void EjercicioSinId_SeRechaza()
    {
        var e = Ej();
        e.Remove("id");
        Assert.Equal(["ejercicios[0].id"], Campos(Con("ejercicios", new JsonArray(e))));
    }

    public static TheoryData<string, JsonNode?> CamposEjercicioMalos => new()
    {
        { "id", 5 },
        { "series", 0 },
        { "series", 11 },
        { "series", 3.5 },
        { "series", "3" },
        { "series", null },
        { "reps", 0 },
        { "reps", 101 },
        { "reps", JsonNode.Parse("8.0") },
        { "pesoKg", -1 },
        { "pesoKg", 500.01 },
        { "pesoKg", 62.555 },
        { "pesoKg", "60" },
        { "descansoS", -1 },
        { "descansoS", 601 },
        { "descansoS", 60.5 },
    };

    [Theory]
    [MemberData(nameof(CamposEjercicioMalos))]
    public void CampoEjercicioMalo_SeRechazaSoloEse(string campo, JsonNode? valor) =>
        Assert.Equal([$"ejercicios[0].{campo}"], Campos(ConEj(campo, valor)));

    [Theory]
    [InlineData("series")]
    [InlineData("reps")]
    [InlineData("descansoS")]
    public void CampoEjercicioFaltante_SeRechaza(string campo)
    {
        var e = Ej();
        e.Remove(campo);
        Assert.Equal([$"ejercicios[0].{campo}"], Campos(Con("ejercicios", new JsonArray(e))));
    }

    [Fact]
    public void Error_LlevaElIndiceDelEjercicio()
    {
        var malo = Ej("prensa");
        malo["series"] = 0;
        Assert.Equal(["ejercicios[2].series"], Campos(Con("ejercicios", new JsonArray(Ej("sentadilla"), Ej("curl-femoral"), malo))));
    }

    [Fact]
    public void PesoAusenteONull_EsNull()
    {
        var e = Ej();
        e.Remove("pesoKg");
        Assert.Null(Validacion.Rutina(Id, Con("ejercicios", new JsonArray(e)).ToJsonString(), Ahora).Rutina!.Ejercicios[0].PesoKg);
        Assert.Null(Validacion.Rutina(Id, ConEj("pesoKg", null).ToJsonString(), Ahora).Rutina!.Ejercicios[0].PesoKg);
    }

    [Fact]
    public void LimitesIncluidos_SonValidos()
    {
        Assert.Empty(Errores(ConEj("series", 1)));
        Assert.Empty(Errores(ConEj("series", 10)));
        Assert.Empty(Errores(ConEj("reps", 1)));
        Assert.Empty(Errores(ConEj("reps", 100)));
        Assert.Empty(Errores(ConEj("pesoKg", 0)));
        Assert.Empty(Errores(ConEj("pesoKg", 500)));
        Assert.Empty(Errores(ConEj("descansoS", 0)));
        Assert.Empty(Errores(ConEj("descansoS", 600)));
        var quince = new JsonArray(Catalogo.Ejercicios.Take(15).Select(e => (JsonNode?)Ej(e.Id)).ToArray());
        Assert.Empty(Errores(Con("ejercicios", quince)));
    }

    [Theory]
    [InlineData("no-es-uuid")]
    [InlineData("")]
    [InlineData("rutina:3f2c1a9e-8b7d-4c6e-9f10-1234567890ab")]
    public void IdNoUuid_SeRechaza(string id) => Assert.Equal(["id"], Campos(Valida(), id));

    [Theory]
    [InlineData("no json")]
    [InlineData("")]
    [InlineData("""[{"nombre":"Pierna"}]""")]
    [InlineData("null")]
    public void BodyNoObjeto_SeRechaza(string body) =>
        Assert.Contains(Validacion.Rutina(Id, body, Ahora).Errores, e => e.Campo == "body");

    [Fact]
    public void CamposExtra_IncluidoUserId_SeIgnoran()
    {
        var b = Valida();
        b["userId"] = "google:otro";
        b["tipo"] = "serie";
        b["id"] = "rutina:otro";
        ((JsonObject)b["ejercicios"]![0]!)["extra"] = "x";
        var (rutina, errores) = Validacion.Rutina(Id, b.ToJsonString(), Ahora);
        Assert.Empty(errores);

        var doc = RutinaDoc.De(rutina!, "google:yo");
        Assert.Equal("google:yo", doc.UserId);
        Assert.Equal("rutina", doc.Tipo);
        Assert.Equal("rutina:" + Id, doc.Id);
        Assert.Equal(rutina, doc.ADto());
        var json = JsonSerializer.Serialize(doc);
        Assert.DoesNotContain("otro", json);
        Assert.DoesNotContain("extra", json);
    }

    [Theory]
    [InlineData("a\u202Eb")]            // bidi override
    [InlineData("\u200B\u200B")]        // solo espacios de ancho cero: quedaría invisible
    [InlineData("x\u2066y")]
    [InlineData("\ufeffPierna")]
    public void Nombre_ConMarcasBidiOAnchoCero_Falla(string nombre)
    {
        Assert.Contains("nombre", Campos(Con("nombre", nombre)));
    }

    [Fact]
    public void Nombre_ConEmojiCompuesto_Pasa()
    {
        // pierna + ZWJ: Cf permitido a propósito para no romper emojis compuestos
        Assert.Empty(Errores(Con("nombre", "Pierna \U0001F9B5 \u200D\u2640\ufe0f")));
    }

    [Fact]
    public void ClavesDuplicadas_Falla()
    {
        var json = "{" + "\"nombre\":\"a\",\"nombre\":\"b\",\"ejercicios\":[" + Ej().ToJsonString() + "]}";
        var (r, errores) = Validacion.Rutina(Id, json, Ahora);
        Assert.Null(r);
        Assert.Contains(errores, e => e.Campo == "body");
    }

    [Fact]
    public void SurrogateSuelto_EsErrorDeValidacion_NoExcepcion()
    {
        var json = "{" + "\"nombre\":\"a\\ud800\",\"ejercicios\":[" + Ej().ToJsonString() + "]}";
        var (r, errores) = Validacion.Rutina(Id, json, Ahora);
        Assert.Null(r);
        Assert.NotEmpty(errores);
        var serie = "{" + "\"ejercicioId\":\"\\ud800\",\"pesoKg\":10,\"reps\":5,\"ts\":\"2026-10-06T10:00:00Z\",\"fecha\":\"2026-10-06\"}";
        var (s2, e2) = Validacion.Serie(Id, serie, Ahora);
        Assert.Null(s2);
        Assert.NotEmpty(e2);
    }
}

