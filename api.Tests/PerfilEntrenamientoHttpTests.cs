using System.Net;
using System.Text.Json;
using GymCoach.Api.Functions;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker.Http;

namespace GymCoach.Api.Tests;

/// <summary>GET /api/perfil-entrenamiento sobre perfiles en memoria (sin Cosmos).</summary>
public class PerfilEntrenamientoHttpTests
{
    readonly Tokens _tokens = new();
    readonly RelojFalso _reloj = new(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
    readonly PerfilesEntrenamientoFalsos _entrenamiento = new();
    readonly PerfilesFalsos _perfiles = new();
    readonly Limites _limites;
    readonly PerfilEntrenamientoHttp _http;

    public PerfilEntrenamientoHttpTests()
    {
        _limites = new Limites(_reloj);
        _http = new PerfilEntrenamientoHttp(_tokens.Autenticador(), _entrenamiento.Crear(_reloj), _perfiles.Crear(), _limites, _reloj);
        _entrenamiento.Series = [.. Enumerable.Range(0, 3).Select(i =>
        {
            var t = new DateTime(2026, 10, 14 - i * 2, 13, 5, 0, DateTimeKind.Utc);
            return new Serie(Guid.NewGuid().ToString("D"), "prensa", 100, 10, t.ToString("yyyy-MM-dd"),
                t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), "Pierna");
        })];
    }

    string Bearer(string sub = "123") => "Bearer " + _tokens.Crear(sub: sub);

    static string Esperar(HttpResponseData res, HttpStatusCode codigo)
    {
        var texto = ((ResponseFalsa)res).Texto();
        Assert.True(codigo == res.StatusCode, $"Esperaba {codigo}, llegó {res.StatusCode}: {texto}");
        return texto;
    }

    Task<HttpResponseData> Get(string? auth, string query = "?hoy=2026-10-14&offsetMin=-360") =>
        _http.Leer(new RequestFalso("GET", "/api/perfil-entrenamiento" + query, auth));

    [Fact]
    public async Task SinAuth_401_SinTocarPerfiles()
    {
        Assert.Equal("""{"error":"no_autenticado"}""", Esperar(await Get(null), HttpStatusCode.Unauthorized));
        Assert.Equal("""{"error":"no_autenticado"}""", Esperar(await Get("Bearer x"), HttpStatusCode.Unauthorized));
        Assert.Empty(_entrenamiento.Leidos);
        Assert.Empty(_perfiles.Leidos);
    }

    [Theory]
    [InlineData("?hoy=2026-10-12", "hoy")]
    [InlineData("?hoy=2026-10-16", "hoy")]
    [InlineData("?hoy=14-10-2026", "hoy")]
    [InlineData("?hoy=", "hoy")]
    [InlineData("?hoy=2026-10-14&offsetMin=-721", "offsetMin")]
    [InlineData("?hoy=2026-10-14&offsetMin=841", "offsetMin")]
    [InlineData("?hoy=2026-10-14&offsetMin=60.0", "offsetMin")]
    [InlineData("?hoy=2026-10-14&offsetMin=abc", "offsetMin")]
    [InlineData("?hoy=2026-10-14&offsetMin=", "offsetMin")]
    [InlineData("?hoy=2026-10-14&offsetMin=%2B60", "offsetMin")]
    [InlineData("?hoy=2026-10-14&offsetMin=%2060", "offsetMin")]
    public async Task ConsultaInvalida_400_SinLeer(string query, string campo)
    {
        var texto = Esperar(await Get(Bearer(), query), HttpStatusCode.BadRequest);
        Assert.Contains("\"error\":\"validacion\"", texto);
        Assert.Contains($"\"campo\":\"{campo}\"", texto);
        Assert.Empty(_entrenamiento.Leidos);
    }

    [Theory]
    [InlineData("?hoy=2026-10-13&offsetMin=-720")]
    [InlineData("?hoy=2026-10-15&offsetMin=840")]
    [InlineData("?offsetMin=0")]
    [InlineData("")]
    public async Task ConsultaEnLosBordes_200(string query) => Esperar(await Get(Bearer(), query), HttpStatusCode.OK);

    [Fact]
    public async Task Ok_FormaPerfilYParaCoach()
    {
        _perfiles.Sembrar("google:123", PerfilCoach.PorDefecto with { Equipo = "libre" });
        using var doc = JsonDocument.Parse(Esperar(await Get(Bearer()), HttpStatusCode.OK));
        var raiz = doc.RootElement;
        Assert.Equal(["perfil", "paraCoach"], raiz.EnumerateObject().Select(p => p.Name));

        var perfil = raiz.GetProperty("perfil");
        Assert.Equal(3, perfil.GetProperty("seriesAnalizadas").GetInt32());
        Assert.Equal("2026-10-14", perfil.GetProperty("hoy").GetString());
        Assert.Equal("prensa", perfil.GetProperty("ejercicios")[0].GetProperty("ejercicioId").GetString());
        // offsetMin -360: 13:05Z son las 07:05 locales.
        Assert.Equal("manana", perfil.GetProperty("habitos").GetProperty("horario").GetProperty("franja").GetString());
        Assert.False(perfil.TryGetProperty("historialSuficiente", out _));

        var paraCoach = raiz.GetProperty("paraCoach");
        // Lo que se serializa en el prompt, exacto: mismo objeto que arma ParaCoach.
        var esperado = CalculosPerfil.ParaCoach(_entrenamiento.Doc!.Perfil, PerfilCoach.PorDefecto with { Equipo = "libre" },
            new DateOnly(2026, 10, 14));
        Assert.Equal(JsonSerializer.Serialize(esperado, Http.Json), paraCoach.GetRawText());
        Assert.Equal("libre", paraCoach.GetProperty("equipoPreferido").GetString());
        Assert.Equal("perfil", paraCoach.GetProperty("equipoOrigen").GetString());
        Assert.Equal(JsonValueKind.Null, paraCoach.GetProperty("pctSeriesMaquina").ValueKind);
        Assert.False(paraCoach.GetProperty("historialSuficiente").GetBoolean());
        Assert.Equal(0, paraCoach.GetProperty("diasDesdeUltimo").GetInt32());
    }

    [Fact]
    public async Task UserIdDeLaQuery_SeIgnora()
    {
        Esperar(await Get(Bearer("a"), "?hoy=2026-10-14&userId=google:otro"), HttpStatusCode.OK);
        Assert.Equal(["google:a"], _entrenamiento.Leidos);
        Assert.Equal("google:a", _entrenamiento.Doc!.UserId);
        Assert.Equal(["google:a"], _perfiles.Leidos);
    }

    [Fact]
    public async Task CosmosSaturado_503()
    {
        _entrenamiento.FallaLeer = new CosmosException("saturado", HttpStatusCode.TooManyRequests, 0, "", 0);
        var res = await Get(Bearer());
        Assert.Equal("""{"error":"no_disponible"}""", Esperar(res, HttpStatusCode.ServiceUnavailable));
        Assert.True(res.Headers.TryGetValues("Retry-After", out _));

        _entrenamiento.FallaLeer = new CosmosException("lento", HttpStatusCode.RequestTimeout, 0, "", 0);
        Esperar(await Get(Bearer()), HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task PerfilCoachSaturado_503()
    {
        var saturado = new PerfilesCoach(
            (_, _) => throw new CosmosException("saturado", HttpStatusCode.ServiceUnavailable, 0, "", 0),
            (_, _, _, _) => throw new InvalidOperationException());
        var http = new PerfilEntrenamientoHttp(_tokens.Autenticador(), _entrenamiento.Crear(_reloj), saturado, _limites, _reloj);
        Esperar(await http.Leer(new RequestFalso("GET", "/api/perfil-entrenamiento", Bearer())), HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task SinCupoDeRecalculo_SinDoc_429_ConDocViejo_200()
    {
        _entrenamiento.HayCupo = false;
        var res = await Get(Bearer());
        Assert.Equal("""{"error":"demasiadas_peticiones"}""", Esperar(res, HttpStatusCode.TooManyRequests));
        Assert.True(res.Headers.TryGetValues("Retry-After", out var ra));
        Assert.Equal("60", ra.Single());

        // Doc de ayer: sin cupo se devuelve tal cual.
        _entrenamiento.HayCupo = true;
        Esperar(await Get(Bearer(), "?hoy=2026-10-13"), HttpStatusCode.OK);
        _entrenamiento.HayCupo = false;
        using var doc = JsonDocument.Parse(Esperar(await Get(Bearer()), HttpStatusCode.OK));
        Assert.Equal("2026-10-13", doc.RootElement.GetProperty("perfil").GetProperty("hoy").GetString());
    }

    [Fact]
    public async Task PasadoElLimiteDeLecturas_429()
    {
        var bearer = Bearer();
        for (var i = 0; i < Limites.LecturasPorMinuto; i++) Esperar(await Get(bearer), HttpStatusCode.OK);
        Esperar(await Get(bearer), HttpStatusCode.TooManyRequests);
        // Vigente: un solo recálculo en todas esas lecturas.
        Assert.Equal(1, _entrenamiento.Consultas);
    }
}
