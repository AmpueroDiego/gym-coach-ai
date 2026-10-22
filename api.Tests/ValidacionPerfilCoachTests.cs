namespace GymCoach.Api.Tests;

public class ValidacionPerfilCoachTests
{
    static string Body(string minutos = "45", string objetivo = "\"Ganar fuerza\"", string notas = "[\"Hombro derecho delicado\"]",
        string altura = "175", string peso = "78.5") =>
        $$"""{"minutosDisponibles":{{minutos}},"objetivo":{{objetivo}},"notas":{{notas}},"alturaCm":{{altura}},"pesoCorporalKg":{{peso}}}""";

    static List<ErrorCampo> Errores(string body)
    {
        var (perfil, errores, _, _, _, _, _) = Validacion.PerfilCoach(body);
        Assert.Null(perfil);
        Assert.NotEmpty(errores);
        return errores;
    }

    [Fact]
    public void Valido_RecortaYDevuelvePerfil()
    {
        var (p, errores, _, _, _, _, _) = Validacion.PerfilCoach(Body(objetivo: "\"  Ganar fuerza \"", notas: "[\" Rodilla \",\"Sin sentadilla\"]"));
        Assert.Empty(errores);
        Assert.Equal(45, p!.MinutosDisponibles);
        Assert.Equal("Ganar fuerza", p.Objetivo);
        Assert.Equal(["Rodilla", "Sin sentadilla"], p.Notas);
        Assert.Equal(175, p.AlturaCm);
        Assert.Equal(78.5m, p.PesoCorporalKg);
    }

    [Fact]
    public void AlturaYPeso_AusentesONull_SonNull()
    {
        var (p, errores, _, _, _, _, _) = Validacion.PerfilCoach("""{"minutosDisponibles":45,"objetivo":null,"notas":[]}""");
        Assert.Empty(errores);
        Assert.Null(p!.AlturaCm);
        Assert.Null(p.PesoCorporalKg);

        Assert.False(Validacion.PerfilCoach("""{"notas":[]}""").TraeAltura);
        Assert.False(Validacion.PerfilCoach("""{"notas":[]}""").TraePeso);

        // null explícito sí "trae" la clave: borra.
        var (q, _, traeAltura, traePeso, _, _, _) = Validacion.PerfilCoach(Body(altura: "null", peso: "null"));
        Assert.Null(q!.AlturaCm);
        Assert.Null(q.PesoCorporalKg);
        Assert.True(traeAltura && traePeso);
    }

    [Theory]
    [InlineData("100")]
    [InlineData("250")]
    public void Altura_Valida(string altura) => Assert.Empty(Validacion.PerfilCoach(Body(altura: altura)).Errores);

    [Theory]
    [InlineData("99")]
    [InlineData("251")]
    [InlineData("175.5")]
    [InlineData("\"175\"")]
    [InlineData("true")]
    public void Altura_Invalida(string altura) =>
        Assert.Contains(Errores(Body(altura: altura)), e => e.Campo == "alturaCm");

    [Theory]
    [InlineData("30", 30)]
    [InlineData("300", 300)]
    [InlineData("78.5", 78.5)]
    [InlineData("78.50", 78.5)]
    public void PesoCorporal_Valido(string peso, double esperado) =>
        Assert.Equal((decimal)esperado, Validacion.PerfilCoach(Body(peso: peso)).Perfil!.PesoCorporalKg);

    [Theory]
    [InlineData("29.9")]
    [InlineData("300.1")]
    [InlineData("78.55")]
    [InlineData("\"78\"")]
    [InlineData("false")]
    [InlineData("1e400")]
    public void PesoCorporal_Invalido(string peso) =>
        Assert.Contains(Errores(Body(peso: peso)), e => e.Campo == "pesoCorporalKg");

    [Fact]
    public void Nulls_YNotasVacias_Valido()
    {
        var (p, errores, _, _, _, _, _) = Validacion.PerfilCoach("""{"minutosDisponibles":null,"objetivo":null,"notas":[]}""");
        Assert.Empty(errores);
        Assert.True(p!.Igual(PerfilCoach.PorDefecto));
    }

    [Theory]
    [InlineData("10")]
    [InlineData("180")]
    [InlineData("null")]
    public void Minutos_Validos(string minutos) => Assert.Empty(Validacion.PerfilCoach(Body(minutos: minutos)).Errores);

    [Theory]
    [InlineData("9")]
    [InlineData("181")]
    [InlineData("\"45\"")]
    [InlineData("45.5")]
    public void Minutos_Invalidos(string minutos) =>
        Assert.Contains(Errores(Body(minutos: minutos)), e => e.Campo == "minutosDisponibles");

    [Fact]
    public void Objetivo_201_Invalido() =>
        Assert.Contains(Errores(Body(objetivo: $"\"{new string('a', 201)}\"")), e => e.Campo == "objetivo");

    [Fact]
    public void Objetivo_200_Valido() => Assert.Empty(Validacion.PerfilCoach(Body(objetivo: $"\"{new string('a', 200)}\"")).Errores);

    [Fact]
    public void Objetivo_SoloEspacios_Invalido() => Assert.Contains(Errores(Body(objetivo: "\"   \"")), e => e.Campo == "objetivo");

    [Fact]
    public void OnceNotas_Invalido()
    {
        var notas = "[" + string.Join(",", Enumerable.Range(1, 11).Select(i => $"\"n{i}\"")) + "]";
        Assert.Contains(Errores(Body(notas: notas)), e => e.Campo == "notas");
    }

    [Fact]
    public void DiezNotas_Valido()
    {
        var notas = "[" + string.Join(",", Enumerable.Range(1, 10).Select(i => $"\"n{i}\"")) + "]";
        Assert.Equal(10, Validacion.PerfilCoach(Body(notas: notas)).Perfil!.Notas.Count);
    }

    [Fact]
    public void Nota_201_Invalida() =>
        Assert.Contains(Errores(Body(notas: $"[\"ok\",\"{new string('a', 201)}\"]")), e => e.Campo == "notas[1]");

    [Theory]
    [InlineData("\"a\\nb\"")]
    [InlineData("\"a\\tb\"")]
    [InlineData("\"a\\u202Eb\"")]
    [InlineData("5")]
    [InlineData("null")]
    public void Nota_ConControlONoTexto_Invalida(string nota) =>
        Assert.Contains(Errores(Body(notas: $"[{nota}]")), e => e.Campo == "notas[0]");

    [Fact]
    public void Objetivo_ConSaltoDeLinea_Invalido() => Assert.Contains(Errores(Body(objetivo: "\"a\\nb\"")), e => e.Campo == "objetivo");

    [Fact]
    public void Notas_Ausentes_Invalido() =>
        Assert.Contains(Errores("""{"minutosDisponibles":45,"objetivo":null}"""), e => e.Campo == "notas");

    [Fact]
    public void ClavesDuplicadas_Invalido() =>
        Assert.Contains(Errores("""{"minutosDisponibles":45,"minutosDisponibles":60,"notas":[]}"""), e => e.Campo == "body");

    [Theory]
    [InlineData("no json")]
    [InlineData("[]")]
    [InlineData("""{"notas":["\uD800"]}""")]
    public void BodyMalo_Invalido(string body) => Assert.Contains(Errores(body), e => e.Campo == "body");

    [Fact]
    public void UserIdEnBody_SeIgnora() =>
        Assert.Empty(Validacion.PerfilCoach("""{"userId":"google:otro","minutosDisponibles":45,"notas":[]}""").Errores);

    static PerfilCoach Guardado(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return Validacion.PerfilGuardado(doc.RootElement);
    }

    [Fact]
    public void Guardado_NotaInvalida_SeDescartaSolaYConservaLasDemas()
    {
        var p = Guardado($$"""
            {"id":"perfil-coach","minutosDisponibles":45,"objetivo":"Fuerza",
             "notas":["Rodilla izquierda","a\nb",5,"{{new string('a', 201)}}","Hombro"]}
            """);
        Assert.Equal(45, p.MinutosDisponibles);
        Assert.Equal("Fuerza", p.Objetivo);
        Assert.Equal(["Rodilla izquierda", "Hombro"], p.Notas);
    }

    [Fact]
    public void Guardado_MinutosYObjetivoInvalidos_PasanANull_SinPerderNotas()
    {
        var p = Guardado("""{"minutosDisponibles":"45","objetivo":"a\u202Eb","notas":["Rodilla"]}""");
        Assert.Null(p.MinutosDisponibles);
        Assert.Null(p.Objetivo);
        Assert.Equal(["Rodilla"], p.Notas);
        Assert.Null(Guardado("""{"minutosDisponibles":500,"notas":[]}""").MinutosDisponibles);
    }

    [Fact]
    public void Guardado_AlturaYPeso_ValidosSeLeen_InvalidosPasanANull()
    {
        var ok = Guardado("""{"notas":[],"alturaCm":180,"pesoCorporalKg":82.5}""");
        Assert.Equal(180, ok.AlturaCm);
        Assert.Equal(82.5m, ok.PesoCorporalKg);

        var malo = Guardado("""{"minutosDisponibles":45,"notas":["Rodilla"],"alturaCm":"180","pesoCorporalKg":82.55}""");
        Assert.Null(malo.AlturaCm);
        Assert.Null(malo.PesoCorporalKg);
        Assert.Equal(45, malo.MinutosDisponibles);
        Assert.Equal(["Rodilla"], malo.Notas);

        var fuera = Guardado("""{"notas":[],"alturaCm":300,"pesoCorporalKg":10}""");
        Assert.Null(fuera.AlturaCm);
        Assert.Null(fuera.PesoCorporalKg);
    }

    [Fact]
    public void Guardado_PerfilViejoSinAlturaNiPeso_LosLeeNull()
    {
        var p = Guardado("""{"id":"perfil-coach","minutosDisponibles":45,"objetivo":"Fuerza","notas":["Hombro"]}""");
        Assert.Equal(45, p.MinutosDisponibles);
        Assert.Null(p.AlturaCm);
        Assert.Null(p.PesoCorporalKg);
    }

    [Fact]
    public void Guardado_MasDeDiezNotas_SeQuedaConLasPrimerasDiez_YBasuraEsPorDefecto()
    {
        var notas = "[" + string.Join(",", Enumerable.Range(1, 12).Select(i => $"\"n{i}\"")) + "]";
        Assert.Equal(10, Guardado($$"""{"notas":{{notas}}}""").Notas.Count);
        Assert.True(Guardado("[]").Igual(PerfilCoach.PorDefecto));
        Assert.True(Guardado("""{"notas":"x"}""").Igual(PerfilCoach.PorDefecto));
    }
    [Theory]
    [InlineData("\"maquina\"", "maquina")]
    [InlineData("\"libre\"", "libre")]
    [InlineData("\"mixto\"", "mixto")]
    [InlineData("null", null)]
    public void Equipo_ValidoONull(string equipo, string? esperado)
    {
        var (p, errores, _, _, traeEquipo, _, _) = Validacion.PerfilCoach($$"""{"notas":[],"equipo":{{equipo}}}""");
        Assert.Empty(errores);
        Assert.Equal(esperado, p!.Equipo);
        // null explícito trae la clave: borra.
        Assert.True(traeEquipo);
    }

    [Fact]
    public void Equipo_Ausente_NoLoTrae()
    {
        var (p, errores, _, _, traeEquipo, _, _) = Validacion.PerfilCoach("""{"notas":[]}""");
        Assert.Empty(errores);
        Assert.Null(p!.Equipo);
        Assert.False(traeEquipo);
    }

    [Theory]
    [InlineData("\"Maquina\"")]
    [InlineData("\"maquina \"")]
    [InlineData("\"barra\"")]
    [InlineData("\"\"")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("[\"maquina\"]")]
    public void Equipo_Invalido(string equipo) =>
        Assert.Contains(Errores($$"""{"notas":[],"equipo":{{equipo}}}"""), e => e.Campo == "equipo");

    [Fact]
    public void Guardado_Equipo_ValidoSeLee_InvalidoPasaANull()
    {
        Assert.Equal("libre", Guardado("""{"notas":[],"equipo":"libre"}""").Equipo);
        Assert.Null(Guardado("""{"notas":["Rodilla"],"equipo":"Maquina"}""").Equipo);
        Assert.Equal(["Rodilla"], Guardado("""{"notas":["Rodilla"],"equipo":5}""").Notas);
        Assert.Null(Guardado("""{"notas":[]}""").Equipo);
    }

    [Theory]
    [InlineData("\"nunca\"", "nunca")]
    [InlineData("\"menos6m\"", "menos6m")]
    [InlineData("\"mas6m\"", "mas6m")]
    [InlineData("null", null)]
    public void Experiencia_ValidaONull(string experiencia, string? esperado)
    {
        var (p, errores, _, _, _, traeExperiencia, _) = Validacion.PerfilCoach($$"""{"notas":[],"experiencia":{{experiencia}}}""");
        Assert.Empty(errores);
        Assert.Equal(esperado, p!.Experiencia);
        // null explícito trae la clave: borra.
        Assert.True(traeExperiencia);
    }

    [Theory]
    [InlineData("\"Nunca\"")]
    [InlineData("\"\"")]
    [InlineData("3")]
    [InlineData("\"principiante\"")]
    public void Experiencia_Invalida(string experiencia) =>
        Assert.Contains(Errores($$"""{"notas":[],"experiencia":{{experiencia}}}"""), e => e.Campo == "experiencia");

    [Theory]
    [InlineData("1", 1)]
    [InlineData("7", 7)]
    [InlineData("null", null)]
    public void Dias_ValidosONull(string dias, int? esperado)
    {
        var (p, errores, _, _, _, _, traeDias) = Validacion.PerfilCoach($$"""{"notas":[],"diasPorSemana":{{dias}}}""");
        Assert.Empty(errores);
        Assert.Equal(esperado, p!.DiasPorSemana);
        Assert.True(traeDias);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("8")]
    [InlineData("\"3\"")]
    [InlineData("3.5")]
    public void Dias_Invalidos(string dias) =>
        Assert.Contains(Errores($$"""{"notas":[],"diasPorSemana":{{dias}}}"""), e => e.Campo == "diasPorSemana");

    [Fact]
    public void ExperienciaYDias_Ausentes_NoLosTrae()
    {
        var (p, errores, _, _, _, traeExperiencia, traeDias) = Validacion.PerfilCoach("""{"notas":[]}""");
        Assert.Empty(errores);
        Assert.Null(p!.Experiencia);
        Assert.Null(p.DiasPorSemana);
        Assert.False(traeExperiencia);
        Assert.False(traeDias);
    }

    [Fact]
    public void Guardado_ExperienciaYDias_ValidosSeLeen_InvalidosOAusentesPasanANull()
    {
        var p = Guardado("""{"notas":[],"experiencia":"mas6m","diasPorSemana":3}""");
        Assert.Equal(("mas6m", 3), (p.Experiencia, p.DiasPorSemana));
        Assert.Null(Guardado("""{"notas":[],"experiencia":"x"}""").Experiencia);
        Assert.Null(Guardado("""{"notas":[],"diasPorSemana":9}""").DiasPorSemana);
        var viejo = Guardado("""{"notas":["Rodilla"]}""");
        Assert.Null(viejo.Experiencia);
        Assert.Null(viejo.DiasPorSemana);
    }
}
