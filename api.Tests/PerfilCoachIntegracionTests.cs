using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using GymCoach.Api.Functions;
using Microsoft.Azure.Cosmos;

namespace GymCoach.Api.Tests;

[Collection(ColeccionCosmos.Nombre)]
public class PerfilCoachIntegracionTests(CosmosFixture cosmos)
{
    readonly Tokens _tokens = new();
    static readonly DateTimeOffset Ahora = DateTimeOffset.UtcNow;
    static readonly RelojFalso Reloj = new(Ahora);
    readonly Limites _limites = new(Reloj);
    static readonly string Hoy = Ahora.UtcDateTime.ToString("yyyy-MM-dd");

    Container Contenedor()
    {
        Assert.SkipWhen(cosmos.Container is null, cosmos.MotivoSkip ?? "");
        return cosmos.Container!;
    }

    PerfilesCoach Perfiles() => new(Contenedor(), Reloj);
    PerfilCoachHttp Handlers() => new(_tokens.Autenticador(), Perfiles(), _limites);

    string Bearer(string sub) => "Bearer " + _tokens.Crear(sub: sub);

    async Task<string> Get(string sub)
    {
        var res = (ResponseFalsa)await Handlers().Leer(new RequestFalso("GET", "/api/coach/perfil", Bearer(sub)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return res.Texto();
    }

    async Task<ResponseFalsa> Put(string sub, string body) =>
        (ResponseFalsa)await Handlers().Guardar(new RequestFalso("PUT", "/api/coach/perfil", Bearer(sub), body));

    const string Vacio = """{"minutosDisponibles":null,"objetivo":null,"notas":[],"alturaCm":null,"pesoCorporalKg":null,"equipo":null,"experiencia":null,"diasPorSemana":null}""";

    [Fact]
    public async Task Get_SinDoc_PorDefecto_YNoLoCrea()
    {
        var sub = Guid.NewGuid().ToString();
        Assert.Equal(Vacio, await Get(sub));
        using var res = await Contenedor().ReadItemStreamAsync(PerfilCoachDoc.IdDoc, new PartitionKey("google:" + sub),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task PutLuegoGet_YPutDeNuevoReemplaza()
    {
        var sub = Guid.NewGuid().ToString();
        const string body = """{"minutosDisponibles":45,"objetivo":"Fuerza","notas":["Hombro derecho"],"alturaCm":175,"pesoCorporalKg":78.5,"equipo":null,"experiencia":null,"diasPorSemana":null}""";
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, body)).StatusCode);
        Assert.Equal(body, await Get(sub));

        Assert.Equal(HttpStatusCode.OK, (await Put(sub, Vacio)).StatusCode);
        Assert.Equal(Vacio, await Get(sub));

        // El doc lleva tipo, userId y actualizado; la API no los expone.
        var doc = (await Contenedor().ReadItemAsync<JsonElement>(PerfilCoachDoc.IdDoc, new PartitionKey("google:" + sub),
            cancellationToken: TestContext.Current.CancellationToken)).Resource;
        Assert.Equal("perfil-coach", doc.GetProperty("tipo").GetString());
        Assert.Equal("google:" + sub, doc.GetProperty("userId").GetString());
        Assert.Equal(Ahora.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"), doc.GetProperty("actualizado").GetString());
    }

    [Fact]
    public async Task Recordar_DeA_NoTocaB()
    {
        var a = Guid.NewGuid().ToString();
        var b = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await Put(b, """{"minutosDisponibles":60,"objetivo":null,"notas":["de B"]}""")).StatusCode);

        var perfiles = Perfiles();
        var (_, nuevo) = await perfiles.RecordarAsync("google:" + a, $$"""{"accion":"agregar_nota","nota":"de A","userId":"google:{{b}}"}""", TestContext.Current.CancellationToken);
        Assert.Equal(["de A"], nuevo!.Notas);
        (_, nuevo) = await perfiles.RecordarAsync("google:" + a, """{"accion":"fijar_minutos","minutos":30}""", TestContext.Current.CancellationToken);
        Assert.Equal(30, nuevo!.MinutosDisponibles);

        Assert.Equal("""{"minutosDisponibles":30,"objetivo":null,"notas":["de A"],"alturaCm":null,"pesoCorporalKg":null,"equipo":null,"experiencia":null,"diasPorSemana":null}""", await Get(a));
        Assert.Equal("""{"minutosDisponibles":60,"objetivo":null,"notas":["de B"],"alturaCm":null,"pesoCorporalKg":null,"equipo":null,"experiencia":null,"diasPorSemana":null}""", await Get(b));
    }

    [Fact]
    public async Task Recordar_ConETagViejo_ReleeYNoPierdeCambios()
    {
        var sub = Guid.NewGuid().ToString();
        var userId = "google:" + sub;
        var contenedor = Contenedor();
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, """{"minutosDisponibles":null,"objetivo":null,"notas":["vieja"]}""")).StatusCode);

        // Lector que, la primera vez, entrega el ETag viejo después de que otro escritor cambió el doc.
        var real = Perfiles();
        var primera = true;
        var conCarrera = new PerfilesCoach(
            async (u, ct) =>
            {
                using var res = await contenedor.ReadItemStreamAsync(PerfilCoachDoc.IdDoc, new PartitionKey(u), cancellationToken: ct);
                var etag = res.Headers.ETag;
                if (primera)
                {
                    primera = false;
                    Assert.True(await real.GuardarAsync(u, new PerfilCoach(null, null, ["vieja", "del form"]), ct));
                    return (new PerfilCoach(null, null, ["vieja"]), etag);
                }
                return (await real.LeerAsync(u, ct), etag);
            },
            async (u, p, etag, ct) =>
            {
                try
                {
                    await contenedor.ReplaceItemAsync(PerfilCoachDoc.De(p, u, Ahora), PerfilCoachDoc.IdDoc, new PartitionKey(u),
                        new ItemRequestOptions { IfMatchEtag = etag }, ct);
                    return true;
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.PreconditionFailed) { return false; }
            });

        var (_, nuevo) = await conCarrera.RecordarAsync(userId, """{"accion":"agregar_nota","nota":"del coach"}""", TestContext.Current.CancellationToken);
        Assert.Equal(["vieja", "del form", "del coach"], nuevo!.Notas);
        Assert.Equal("""{"minutosDisponibles":null,"objetivo":null,"notas":["vieja","del form","del coach"],"alturaCm":null,"pesoCorporalKg":null,"equipo":null,"experiencia":null,"diasPorSemana":null}""", await Get(sub));
    }

    [Fact]
    public async Task DocPerfil_NoApareceEnSeriesRutinasNiRecords()
    {
        var sub = Guid.NewGuid().ToString();
        Assert.Equal(HttpStatusCode.OK, (await Put(sub, """{"minutosDisponibles":45,"objetivo":"Fuerza","notas":["n"]}""")).StatusCode);

        var bearer = Bearer(sub);
        var series = (ResponseFalsa)await new Series(_tokens.Autenticador(), Contenedor(), _limites, Reloj)
            .Listar(new RequestFalso("GET", $"/api/series?desde={Hoy}&hasta={Hoy}", bearer));
        var rutinas = (ResponseFalsa)await new Rutinas(_tokens.Autenticador(), Contenedor(), _limites, Reloj)
            .Listar(new RequestFalso("GET", "/api/rutinas", bearer));
        var records = (ResponseFalsa)await new Records(_tokens.Autenticador(), Contenedor(), _limites, Reloj)
            .Listar(new RequestFalso("GET", "/api/records", bearer));

        foreach (var res in new[] { series, rutinas, records })
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Empty(JsonNode.Parse(res.Texto())!.AsArray());
        }
    }
}
