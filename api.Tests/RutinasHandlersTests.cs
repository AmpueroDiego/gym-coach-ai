using System.Net;
using GymCoach.Api.Functions;
using Microsoft.Azure.Functions.Worker.Http;

namespace GymCoach.Api.Tests;

/// <summary>Salidas de los handlers de rutinas antes de tocar Cosmos (Container null, como HandlersTests).</summary>
public class RutinasHandlersTests
{
    readonly Tokens _tokens = new();
    readonly RelojFalso _reloj = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
    readonly Rutinas _rutinas;

    public RutinasHandlersTests() => _rutinas = new Rutinas(_tokens.Autenticador(), null!, new Limites(_reloj), _reloj);

    string Bearer => "Bearer " + _tokens.Crear();
    static readonly string Id = Guid.NewGuid().ToString();

    static string Esperar(HttpResponseData res, HttpStatusCode codigo)
    {
        var texto = ((ResponseFalsa)res).Texto();
        Assert.True(codigo == res.StatusCode, $"Esperaba {codigo}, llegó {res.StatusCode}: {texto}");
        return texto;
    }

    [Fact]
    public async Task SinAuth_Todos401()
    {
        static void Es401(HttpResponseData res) =>
            Assert.Equal("""{"error":"no_autenticado"}""", Esperar(res, HttpStatusCode.Unauthorized));

        Es401(await _rutinas.Listar(new RequestFalso("GET", "/api/rutinas")));
        Es401(await _rutinas.Guardar(new RequestFalso("PUT", "/api/rutinas/" + Id, "Bearer x", "{}"), Id));
        Es401(await _rutinas.Borrar(new RequestFalso("DELETE", "/api/rutinas/" + Id), Id));
    }

    [Fact]
    public async Task Put_IdNoUuid_400()
    {
        const string body = """{"nombre":"Pierna","ejercicios":[{"id":"sentadilla","series":4,"reps":8,"pesoKg":80,"descansoS":120}]}""";
        Assert.Contains("\"campo\":\"id\"", Esperar(await _rutinas.Guardar(
            new RequestFalso("PUT", "/api/rutinas/abc", Bearer, body), "abc"), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Put_Invalido_400ConDetalles()
    {
        var texto = Esperar(await _rutinas.Guardar(
            new RequestFalso("PUT", "/api/rutinas/" + Id, Bearer, """{"nombre":5,"ejercicios":["sentadilla"]}"""), Id),
            HttpStatusCode.BadRequest);
        Assert.Contains("\"error\":\"validacion\"", texto);
        Assert.Contains("\"campo\":\"nombre\"", texto);
        Assert.Contains("\"campo\":\"ejercicios[0]\"", texto);
    }

    [Fact]
    public async Task Put_BodyMayorA4KB_400()
    {
        var grande = "{\"nombre\":\"" + new string('a', 5000) + "\"}";
        Assert.Contains("\"campo\":\"body\"", Esperar(await _rutinas.Guardar(
            new RequestFalso("PUT", "/api/rutinas/" + Id, Bearer, grande), Id), HttpStatusCode.BadRequest));
    }

    [Fact]
    public async Task Put_ContentLengthMayorA4KB_400() =>
        Assert.Contains("\"campo\":\"body\"", Esperar(await _rutinas.Guardar(
            new RequestFalso("PUT", "/api/rutinas/" + Id, Bearer, "{}", contentLength: 5000), Id), HttpStatusCode.BadRequest));

    [Fact]
    public async Task Delete_IdNoUuid_400() =>
        Assert.Contains("\"campo\":\"id\"", Esperar(await _rutinas.Borrar(
            new RequestFalso("DELETE", "/api/rutinas/abc", Bearer), "abc"), HttpStatusCode.BadRequest));

    [Fact]
    public async Task Escrituras_PasadoElLimite_429ConRetryAfter()
    {
        var bearer = Bearer;
        // Las escrituras inválidas también cuentan: el límite va antes de validar.
        for (var i = 0; i < Limites.EscriturasPorMinuto; i++)
            Esperar(await _rutinas.Guardar(new RequestFalso("PUT", "/api/rutinas/" + Id, bearer, "{}"), Id), HttpStatusCode.BadRequest);

        foreach (var res in new[]
                 {
                     await _rutinas.Guardar(new RequestFalso("PUT", "/api/rutinas/" + Id, bearer, "{}"), Id),
                     await _rutinas.Borrar(new RequestFalso("DELETE", "/api/rutinas/abc", bearer), "abc"),
                 })
        {
            Assert.Equal("""{"error":"demasiadas_peticiones"}""", Esperar(res, HttpStatusCode.TooManyRequests));
            Assert.True(res.Headers.TryGetValues("Retry-After", out var ra));
            Assert.Equal("60", ra.Single());
        }

        _reloj.Avanzar(TimeSpan.FromMinutes(1));
        Esperar(await _rutinas.Borrar(new RequestFalso("DELETE", "/api/rutinas/abc", bearer), "abc"), HttpStatusCode.BadRequest);
    }
}
