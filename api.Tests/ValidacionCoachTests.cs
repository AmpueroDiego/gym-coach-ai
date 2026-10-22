namespace GymCoach.Api.Tests;

public class ValidacionCoachTests
{
    static readonly DateTimeOffset Ahora = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    static string Body(int usuarios, string? hoy = "2026-10-09", string texto = "hola") =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            hoy,
            mensajes = Enumerable.Range(0, usuarios).Select(_ => new { rol = "usuario", texto }),
        });

    static List<ErrorCampo> Errores(string body)
    {
        var (p, errores) = ValidacionCoach.Peticion(body, Ahora);
        Assert.Null(p);
        Assert.NotEmpty(errores);
        return errores;
    }

    [Fact]
    public void Valida_UnMensaje()
    {
        var (p, errores) = ValidacionCoach.Peticion(Body(1), Ahora);
        Assert.Empty(errores);
        Assert.Equal(new DateOnly(2026, 10, 9), p!.Hoy);
        Assert.Equal([new MensajeEntrada("usuario", "hola")], p.Mensajes);
    }

    [Fact]
    public void DiezMensajes_Ok_OnceNo()
    {
        Assert.NotNull(ValidacionCoach.Peticion(Body(10), Ahora).Peticion);
        Assert.Contains(Errores(Body(11)), e => e.Campo == "mensajes");
        Assert.Contains(Errores(Body(0)), e => e.Campo == "mensajes");
    }

    [Fact]
    public void TextoUsuario_1000Ok_1001No()
    {
        Assert.NotNull(ValidacionCoach.Peticion(Body(1, texto: new string('a', 1000)), Ahora).Peticion);
        Assert.Contains(Errores(Body(1, texto: new string('a', 1001))), e => e.Campo == "mensajes[0].texto");
        // Se recorta antes de medir: solo espacios es vacío.
        Assert.Contains(Errores(Body(1, texto: "   ")), e => e.Campo == "mensajes[0].texto");
    }

    [Fact]
    public void TextoCoach_Hasta2000()
    {
        var ok = $$"""{"mensajes":[{"rol":"usuario","texto":"a"},{"rol":"coach","texto":"{{new string('b', 2000)}}"},{"rol":"usuario","texto":"c"}]}""";
        Assert.NotNull(ValidacionCoach.Peticion(ok, Ahora).Peticion);
        var largo = ok.Replace(new string('b', 2000), new string('b', 2001));
        Assert.Contains(Errores(largo), e => e.Campo == "mensajes[1].texto");
    }

    [Fact]
    public void UltimoDelCoach_400() =>
        Assert.Contains(Errores("""{"mensajes":[{"rol":"usuario","texto":"a"},{"rol":"coach","texto":"b"}]}"""),
            e => e.Campo == "mensajes" && e.Mensaje.Contains("último"));

    [Theory]
    [InlineData("""{"mensajes":[{"rol":"system","texto":"a"}]}""")]
    [InlineData("""{"mensajes":[{"rol":"assistant","texto":"a"},{"rol":"usuario","texto":"b"}]}""")]
    [InlineData("""{"mensajes":[{"texto":"a"}]}""")]
    public void RolInvalido_400(string body) => Assert.Contains(Errores(body), e => e.Campo.EndsWith(".rol") || e.Campo == "mensajes");

    [Fact]
    public void SaltosDeLinea_Ok_BidiNo()
    {
        var (p, _) = ValidacionCoach.Peticion("""{"mensajes":[{"rol":"usuario","texto":"línea 1\nlínea 2\r\n\tfin 💪"}]}""", Ahora);
        Assert.Equal("línea 1\nlínea 2\r\n\tfin 💪", p!.Mensajes[0].Texto);
        Assert.Contains(Errores("""{"mensajes":[{"rol":"usuario","texto":"hola\u202Emundo"}]}"""), e => e.Campo == "mensajes[0].texto");
        Assert.Contains(Errores("""{"mensajes":[{"rol":"usuario","texto":"a\u200Bb"}]}"""), e => e.Campo == "mensajes[0].texto");
        Assert.Contains(Errores("""{"mensajes":[{"rol":"usuario","texto":"a\u0007b"}]}"""), e => e.Campo == "mensajes[0].texto");
    }

    [Fact]
    public void ClavesDuplicadas_400() =>
        Assert.Contains(Errores("""{"mensajes":[{"rol":"usuario","texto":"a","texto":"b"}]}"""), e => e.Campo == "body");

    [Fact]
    public void SurrogateSuelto_400() =>
        Assert.Contains(Errores("""{"mensajes":[{"rol":"usuario","texto":"a\uD800"}]}"""), e => e.Campo == "body");

    [Theory]
    [InlineData("2026-10-08", true)]
    [InlineData("2026-10-10", true)]
    [InlineData("2026-10-07", false)]
    [InlineData("2026-10-11", false)]
    [InlineData("09/10/2026", false)]
    public void Hoy_AUnDiaComoMucho(string hoy, bool valido)
    {
        var (p, errores) = ValidacionCoach.Peticion(Body(1, hoy), Ahora);
        if (valido) Assert.Equal(DateOnly.Parse(hoy), p!.Hoy);
        else Assert.Contains(errores, e => e.Campo == "hoy");
    }

    [Fact]
    public void SinHoy_UsaElDiaUtc() =>
        Assert.Equal(new DateOnly(2026, 10, 9), ValidacionCoach.Peticion(Body(1, hoy: null), Ahora).Peticion!.Hoy);

    [Fact]
    public void UserIdEnElBody_SeIgnora()
    {
        var (p, errores) = ValidacionCoach.Peticion("""{"userId":"google:otro","mensajes":[{"rol":"usuario","texto":"a","userId":"x"}]}""", Ahora);
        Assert.Empty(errores);
        Assert.Single(p!.Mensajes);
    }

    [Theory]
    [InlineData("no es json")]
    [InlineData("[]")]
    [InlineData("""{"mensajes":"hola"}""")]
    [InlineData("""{"mensajes":[{"rol":"usuario","texto":5}]}""")]
    public void Malformado_400(string body) => Errores(body);

    static string ConOffset(string offset) => $$"""{"offsetMin":{{offset}},"mensajes":[{"rol":"usuario","texto":"a"}]}""";

    [Theory]
    [InlineData("-720", -720)]
    [InlineData("840", 840)]
    [InlineData("-360", -360)]
    [InlineData("0", 0)]
    public void OffsetMin_Valido(string offset, int esperado) =>
        Assert.Equal(esperado, ValidacionCoach.Peticion(ConOffset(offset), Ahora).Peticion!.OffsetMin);

    [Fact]
    public void OffsetMin_AusenteONull_EsNull()
    {
        Assert.Null(ValidacionCoach.Peticion(Body(1), Ahora).Peticion!.OffsetMin);
        Assert.Null(ValidacionCoach.Peticion(ConOffset("null"), Ahora).Peticion!.OffsetMin);
    }

    [Theory]
    [InlineData("-721")]
    [InlineData("841")]
    [InlineData("-360.5")]
    [InlineData("\"-360\"")]
    [InlineData("true")]
    [InlineData("99999999999")]
    public void OffsetMin_Invalido(string offset) => Assert.Contains(Errores(ConOffset(offset)), e => e.Campo == "offsetMin");

    static string ConIdioma(string idioma) => $$"""{"idioma":{{idioma}},"mensajes":[{"rol":"usuario","texto":"a"}]}""";

    [Theory]
    [InlineData("\"es\"", "es")]
    [InlineData("\"en\"", "en")]
    [InlineData("null", "es")]
    public void Idioma_Valido(string idioma, string esperado) =>
        Assert.Equal(esperado, ValidacionCoach.Peticion(ConIdioma(idioma), Ahora).Peticion!.Idioma);

    [Fact]
    public void Idioma_Ausente_EsEspanol() => Assert.Equal("es", ValidacionCoach.Peticion(Body(1), Ahora).Peticion!.Idioma);

    [Theory]
    [InlineData("\"EN\"")]
    [InlineData("\"fr\"")]
    [InlineData("\"\"")]
    [InlineData("\"en \"")]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("[\"en\"]")]
    public void Idioma_Invalido(string idioma) =>
        Assert.Contains(Errores(ConIdioma(idioma)), e => e.Campo == "idioma" && e.Mensaje == "Debe ser es o en.");
}
