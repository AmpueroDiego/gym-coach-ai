using System.Text.Json.Nodes;

namespace GymCoach.Api.Tests;

/// <summary>Perfiles en un diccionario con ETag: simula Cosmos, incluido el 412 de un escritor concurrente.</summary>
public sealed class PerfilesFalsos
{
    public readonly Dictionary<string, (PerfilCoach Perfil, string ETag)> Docs = [];
    public readonly List<string> Leidos = [];
    public readonly List<string> Escritos = [];
    /// <summary>Cuántas escrituras más fallan como si otro hubiera escrito primero (412/409).</summary>
    public int ConflictosPendientes;
    /// <summary>Se ejecuta en cada lectura (para simular un escritor que mete un cambio entre intentos).</summary>
    public Action<string>? AlLeer;
    int _version;

    public PerfilesCoach Crear() => new(
        (userId, _) =>
        {
            Leidos.Add(userId);
            AlLeer?.Invoke(userId);
            return Task.FromResult(Docs.TryGetValue(userId, out var d) ? (d.Perfil, (string?)d.ETag) : (PerfilCoach.PorDefecto, null));
        },
        (userId, perfil, etag, _) =>
        {
            Escritos.Add(userId);
            if (ConflictosPendientes > 0)
            {
                ConflictosPendientes--;
                return Task.FromResult(false);
            }
            var actual = Docs.TryGetValue(userId, out var d) ? d.ETag : null;
            if (actual != etag) return Task.FromResult(false);
            Docs[userId] = (perfil, "e" + ++_version);
            return Task.FromResult(true);
        });

    public void Sembrar(string userId, PerfilCoach perfil) => Docs[userId] = (perfil, "e" + ++_version);
}

public class PerfilesCoachTests
{
    static readonly PerfilCoach ConNotas = new(45, "Fuerza", ["uno", "dos", "tres"]);

    static PerfilCoach Ok((PerfilCoach? Nuevo, string? Error) r)
    {
        Assert.Null(r.Error);
        return r.Nuevo!;
    }

    static void Error(string esperado, (PerfilCoach? Nuevo, string? Error) r)
    {
        Assert.Equal(esperado, r.Error);
        Assert.Null(r.Nuevo);
    }

    [Fact]
    public void AgregarNota_RecortaYAgregaAlFinal() =>
        Assert.Equal(["uno", "dos", "tres", "rodilla"],
            Ok(PerfilesCoach.Aplicar(ConNotas, """{"accion":"agregar_nota","nota":"  rodilla "}""")).Notas);

    [Fact]
    public async Task AgregarNota_Repetida_NoDuplicaNiEscribe()
    {
        Assert.Equal(["uno", "dos", "tres"], Ok(PerfilesCoach.Aplicar(ConNotas, """{"accion":"agregar_nota","nota":" dos "}""")).Notas);
        // Sin distinguir mayúsculas: la misma nota repetida por el agente no se duplica.
        Assert.Equal(["uno", "dos", "tres"], Ok(PerfilesCoach.Aplicar(ConNotas, """{"accion":"agregar_nota","nota":"  DOS "}""")).Notas);

        var f = new PerfilesFalsos();
        f.Sembrar("a", ConNotas);
        var (json, nuevo) = await f.Crear().RecordarAsync("a", """{"accion":"agregar_nota","nota":" Dos"}""",
            TestContext.Current.CancellationToken);
        Assert.Null(nuevo);
        Assert.Empty(f.Escritos);
        Assert.Contains("\"ok\":true", json);
    }

    [Fact]
    public void AgregarNota_LaOnce_NotasLlenas()
    {
        var diez = new PerfilCoach(null, null, [.. Enumerable.Range(1, 10).Select(i => $"n{i}")]);
        Error("notas_llenas", PerfilesCoach.Aplicar(diez, """{"accion":"agregar_nota","nota":"otra"}"""));
        var nueve = diez with { Notas = [.. diez.Notas.Take(9)] };
        Assert.Equal(10, Ok(PerfilesCoach.Aplicar(nueve, """{"accion":"agregar_nota","nota":"otra"}""")).Notas.Count);
    }

    [Theory]
    [InlineData("""{"accion":"agregar_nota"}""")]
    [InlineData("""{"accion":"agregar_nota","nota":""}""")]
    [InlineData("""{"accion":"agregar_nota","nota":"a\nb"}""")]
    [InlineData("""{"accion":"agregar_nota","nota":5}""")]
    public void AgregarNota_Invalida(string args) => Error("parametros_invalidos", PerfilesCoach.Aplicar(ConNotas, args));

    [Fact]
    public void AgregarNota_201_Invalida() =>
        Error("parametros_invalidos", PerfilesCoach.Aplicar(ConNotas, $$"""{"accion":"agregar_nota","nota":"{{new string('a', 201)}}"}"""));

    [Fact]
    public void QuitarNota_EsUnoBase() =>
        Assert.Equal(["uno", "tres"], Ok(PerfilesCoach.Aplicar(ConNotas, """{"accion":"quitar_nota","indice":2}""")).Notas);

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(11)]
    public void QuitarNota_IndiceFuera_IndiceInvalido(int indice) =>
        Error("indice_invalido", PerfilesCoach.Aplicar(ConNotas, $$"""{"accion":"quitar_nota","indice":{{indice}}}"""));

    [Theory]
    [InlineData("""{"accion":"quitar_nota"}""")]
    [InlineData("""{"accion":"quitar_nota","indice":"2"}""")]
    [InlineData("""{"accion":"quitar_nota","indice":1.5}""")]
    public void QuitarNota_SinEntero_ParametrosInvalidos(string args) => Error("parametros_invalidos", PerfilesCoach.Aplicar(ConNotas, args));

    [Fact]
    public void FijarMinutos() =>
        Assert.Equal(60, Ok(PerfilesCoach.Aplicar(ConNotas, """{"accion":"fijar_minutos","minutos":60}""")).MinutosDisponibles);

    [Theory]
    [InlineData("5")]
    [InlineData("181")]
    [InlineData("\"60\"")]
    [InlineData("null")]
    public void FijarMinutos_Invalido(string minutos) =>
        Error("parametros_invalidos", PerfilesCoach.Aplicar(ConNotas, $$"""{"accion":"fijar_minutos","minutos":{{minutos}}}"""));

    [Fact]
    public void FijarObjetivo_YVacioLoBorra()
    {
        Assert.Equal("Hipertrofia", Ok(PerfilesCoach.Aplicar(ConNotas, """{"accion":"fijar_objetivo","objetivo":" Hipertrofia "}""")).Objetivo);
        Assert.Null(Ok(PerfilesCoach.Aplicar(ConNotas, """{"accion":"fijar_objetivo","objetivo":""}""")).Objetivo);
    }

    [Fact]
    public void FijarObjetivo_201_Invalido() =>
        Error("parametros_invalidos", PerfilesCoach.Aplicar(ConNotas, $$"""{"accion":"fijar_objetivo","objetivo":"{{new string('a', 201)}}"}"""));

    [Fact]
    public void FijarAltura_YNullLaBorra()
    {
        var con = Ok(PerfilesCoach.Aplicar(ConNotas, """{"accion":"fijar_altura","alturaCm":175}"""));
        Assert.Equal(175, con.AlturaCm);
        Assert.Equal(ConNotas.Notas, con.Notas);
        Assert.Null(Ok(PerfilesCoach.Aplicar(con, """{"accion":"fijar_altura","alturaCm":null}""")).AlturaCm);
    }

    [Theory]
    [InlineData("""{"accion":"fijar_altura"}""")]
    [InlineData("""{"accion":"fijar_altura","alturaCm":99}""")]
    [InlineData("""{"accion":"fijar_altura","alturaCm":251}""")]
    [InlineData("""{"accion":"fijar_altura","alturaCm":175.5}""")]
    [InlineData("""{"accion":"fijar_altura","alturaCm":"175"}""")]
    public void FijarAltura_Invalida(string args) => Error("parametros_invalidos", PerfilesCoach.Aplicar(ConNotas, args));

    [Fact]
    public void FijarPeso_YNullLoBorra()
    {
        var con = Ok(PerfilesCoach.Aplicar(ConNotas, """{"accion":"fijar_peso","pesoCorporalKg":78.5}"""));
        Assert.Equal(78.5m, con.PesoCorporalKg);
        Assert.Null(Ok(PerfilesCoach.Aplicar(con, """{"accion":"fijar_peso","pesoCorporalKg":null}""")).PesoCorporalKg);
    }

    [Theory]
    [InlineData("""{"accion":"fijar_peso"}""")]
    [InlineData("""{"accion":"fijar_peso","pesoCorporalKg":29.9}""")]
    [InlineData("""{"accion":"fijar_peso","pesoCorporalKg":300.1}""")]
    [InlineData("""{"accion":"fijar_peso","pesoCorporalKg":78.55}""")]
    [InlineData("""{"accion":"fijar_peso","pesoCorporalKg":"78"}""")]
    [InlineData("""{"accion":"fijar_peso","pesoCorporalKg":true}""")]
    public void FijarPeso_Invalido(string args) => Error("parametros_invalidos", PerfilesCoach.Aplicar(ConNotas, args));

    [Fact]
    public void FijarEquipo_YNullLoBorra()
    {
        var con = Ok(PerfilesCoach.Aplicar(ConNotas, """{"accion":"fijar_equipo","equipo":"maquina"}"""));
        Assert.Equal("maquina", con.Equipo);
        Assert.Equal(ConNotas.Notas, con.Notas);
        Assert.Null(Ok(PerfilesCoach.Aplicar(con, """{"accion":"fijar_equipo","equipo":null}""")).Equipo);
    }

    [Theory]
    [InlineData("""{"accion":"fijar_equipo"}""")]
    [InlineData("""{"accion":"fijar_equipo","equipo":"Maquina"}""")]
    [InlineData("""{"accion":"fijar_equipo","equipo":"barra"}""")]
    [InlineData("""{"accion":"fijar_equipo","equipo":""}""")]
    [InlineData("""{"accion":"fijar_equipo","equipo":1}""")]
    [InlineData("""{"accion":"fijar_equipo","equipo":true}""")]
    public void FijarEquipo_Invalido(string args) => Error("parametros_invalidos", PerfilesCoach.Aplicar(ConNotas, args));

    [Fact]
    public async Task Recordar_FijarEquipo_EscribeYMismoValorNoEscribe()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = new PerfilesFalsos();
        f.Sembrar("a", ConNotas);
        var (_, nuevo) = await f.Crear().RecordarAsync("a", """{"accion":"fijar_equipo","equipo":"libre"}""", ct);

        // Igual compara Equipo: si no, el cambio se tomaría como "sin cambio" y no se escribiría.
        Assert.Equal("libre", nuevo!.Equipo);
        Assert.Equal("libre", f.Docs["a"].Perfil.Equipo);
        var escritos = f.Escritos.Count;
        Assert.Null((await f.Crear().RecordarAsync("a", """{"accion":"fijar_equipo","equipo":"libre"}""", ct)).Nuevo);
        Assert.Equal(escritos, f.Escritos.Count);
    }

    [Fact]
    public async Task Recordar_FijarPeso_EscribeYDevuelveElPerfilConAlturaYPeso()
    {
        var f = new PerfilesFalsos();
        f.Sembrar("a", PerfilCoach.PorDefecto with { AlturaCm = 175 });
        var (json, nuevo) = await f.Crear().RecordarAsync("a", """{"accion":"fijar_peso","pesoCorporalKg":80}""",
            TestContext.Current.CancellationToken);

        Assert.Equal(80m, nuevo!.PesoCorporalKg);
        Assert.Equal(175, f.Docs["a"].Perfil.AlturaCm);
        Assert.Equal("""{"ok":true,"perfil":{"minutosDisponibles":null,"objetivo":null,"notas":[],"alturaCm":175,"pesoCorporalKg":80,"equipo":null,"experiencia":null,"diasPorSemana":null}}""", json);

        // Mismo valor: sin cambio, no escribe.
        var escritos = f.Escritos.Count;
        Assert.Null((await f.Crear().RecordarAsync("a", """{"accion":"fijar_peso","pesoCorporalKg":80.0}""",
            TestContext.Current.CancellationToken)).Nuevo);
        Assert.Equal(escritos, f.Escritos.Count);
    }

    [Theory]
    [InlineData("\"nunca\"", "nunca")]
    [InlineData("\"menos6m\"", "menos6m")]
    [InlineData("\"mas6m\"", "mas6m")]
    public void FijarExperiencia_Valida(string experiencia, string esperado) =>
        Assert.Equal(esperado, Ok(PerfilesCoach.Aplicar(ConNotas, $$"""{"accion":"fijar_experiencia","experiencia":{{experiencia}}}""")).Experiencia);

    [Fact]
    public void FijarExperiencia_NullBorra()
    {
        var con = ConNotas with { Experiencia = "nunca" };
        Assert.Null(Ok(PerfilesCoach.Aplicar(con, """{"accion":"fijar_experiencia","experiencia":null}""")).Experiencia);
    }

    [Theory]
    [InlineData("""{"accion":"fijar_experiencia"}""")]
    [InlineData("""{"accion":"fijar_experiencia","experiencia":"Nunca"}""")]
    [InlineData("""{"accion":"fijar_experiencia","experiencia":"principiante"}""")]
    [InlineData("""{"accion":"fijar_experiencia","experiencia":3}""")]
    public void FijarExperiencia_Invalida(string args) => Error("parametros_invalidos", PerfilesCoach.Aplicar(ConNotas, args));

    [Fact]
    public void FijarDias_ValidoYNullBorra()
    {
        var con = Ok(PerfilesCoach.Aplicar(ConNotas, """{"accion":"fijar_dias","diasPorSemana":3}"""));
        Assert.Equal(3, con.DiasPorSemana);
        Assert.Null(Ok(PerfilesCoach.Aplicar(con, """{"accion":"fijar_dias","diasPorSemana":null}""")).DiasPorSemana);
    }

    [Theory]
    [InlineData("""{"accion":"fijar_dias"}""")]
    [InlineData("""{"accion":"fijar_dias","diasPorSemana":0}""")]
    [InlineData("""{"accion":"fijar_dias","diasPorSemana":8}""")]
    [InlineData("""{"accion":"fijar_dias","diasPorSemana":"3"}""")]
    [InlineData("""{"accion":"fijar_dias","diasPorSemana":3.5}""")]
    public void FijarDias_Invalido(string args) => Error("parametros_invalidos", PerfilesCoach.Aplicar(ConNotas, args));

    [Fact]
    public async Task Recordar_FijarExperienciaYDias_Escribe()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = new PerfilesFalsos();
        f.Sembrar("a", ConNotas);
        await f.Crear().RecordarAsync("a", """{"accion":"fijar_experiencia","experiencia":"nunca"}""", ct);
        var (json, nuevo) = await f.Crear().RecordarAsync("a", """{"accion":"fijar_dias","diasPorSemana":3}""", ct);

        // Igual compara los dos campos: si no, el cambio se tomaría como "sin cambio" y no se escribiría.
        Assert.Equal(("nunca", 3), (nuevo!.Experiencia, nuevo.DiasPorSemana));
        Assert.Equal(("nunca", 3), (f.Docs["a"].Perfil.Experiencia, f.Docs["a"].Perfil.DiasPorSemana));
        Assert.Contains("\"experiencia\":\"nunca\",\"diasPorSemana\":3", json);
    }

    [Fact]
    public void AccionDesconocida() =>
        Error("accion_desconocida", PerfilesCoach.Aplicar(ConNotas, """{"accion":"fijar_hora","hora":"06:00"}"""));

    [Theory]
    [InlineData("no json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("""{"accion":5}""")]
    [InlineData("""{"accion":"agregar_nota","accion":"quitar_nota","indice":1}""")]
    [InlineData("""{"accion":"agregar_nota","nota":"\uD800"}""")]
    public void ArgumentosMalos_ParametrosInvalidos(string args) => Error("parametros_invalidos", PerfilesCoach.Aplicar(ConNotas, args));

    [Fact]
    public void Aplicar_NoMutaElActual()
    {
        PerfilesCoach.Aplicar(ConNotas, """{"accion":"quitar_nota","indice":1}""");
        Assert.Equal(["uno", "dos", "tres"], ConNotas.Notas);
    }

    [Fact]
    public async Task Recordar_UsaElUserIdDelBackend_NoElDeLosArgs()
    {
        var f = new PerfilesFalsos();
        f.Sembrar("google:b", ConNotas);
        var (json, nuevo) = await f.Crear().RecordarAsync("google:a",
            """{"accion":"fijar_minutos","minutos":30,"userId":"google:b"}""", TestContext.Current.CancellationToken);

        Assert.Equal(30, nuevo!.MinutosDisponibles);
        Assert.Equal(["google:a"], f.Leidos);
        Assert.Equal(["google:a"], f.Escritos);
        Assert.Equal(45, f.Docs["google:b"].Perfil.MinutosDisponibles);
        Assert.Equal(30, f.Docs["google:a"].Perfil.MinutosDisponibles);
        Assert.Equal("""{"ok":true,"perfil":{"minutosDisponibles":30,"objetivo":null,"notas":[],"alturaCm":null,"pesoCorporalKg":null,"equipo":null,"experiencia":null,"diasPorSemana":null}}""", json);
    }

    [Fact]
    public async Task Recordar_412LuegoExito_ReleeYReaplica()
    {
        var f = new PerfilesFalsos();
        f.Sembrar("a", ConNotas);
        // Entre el primer intento y el segundo, otro escritor agrega una nota: no se debe perder.
        var lecturas = 0;
        f.AlLeer = u => { if (++lecturas == 2) f.Sembrar(u, f.Docs[u].Perfil with { Notas = [.. f.Docs[u].Perfil.Notas, "del form"] }); };
        f.ConflictosPendientes = 1;

        var (json, nuevo) = await f.Crear().RecordarAsync("a", """{"accion":"agregar_nota","nota":"del coach"}""", TestContext.Current.CancellationToken);

        Assert.Equal(["uno", "dos", "tres", "del form", "del coach"], nuevo!.Notas);
        Assert.Equal(nuevo.Notas, f.Docs["a"].Perfil.Notas);
        Assert.True((bool)JsonNode.Parse(json)!["ok"]!);
    }

    [Fact]
    public async Task Recordar_Dos412_NoDisponible()
    {
        var f = new PerfilesFalsos { ConflictosPendientes = 2 };
        f.Sembrar("a", ConNotas);
        var (json, nuevo) = await f.Crear().RecordarAsync("a", """{"accion":"fijar_minutos","minutos":30}""", TestContext.Current.CancellationToken);
        Assert.Equal("""{"error":"no_disponible"}""", json);
        Assert.Null(nuevo);
        Assert.Equal(45, f.Docs["a"].Perfil.MinutosDisponibles);
    }

    [Fact]
    public async Task Recordar_SinDoc_LoCrea()
    {
        var f = new PerfilesFalsos();
        var (_, nuevo) = await f.Crear().RecordarAsync("a", """{"accion":"agregar_nota","nota":"rodilla"}""", TestContext.Current.CancellationToken);
        Assert.Equal(["rodilla"], nuevo!.Notas);
        Assert.Equal(["rodilla"], f.Docs["a"].Perfil.Notas);
    }

    [Fact]
    public async Task Recordar_Error_NoEscribe()
    {
        var f = new PerfilesFalsos();
        var (json, nuevo) = await f.Crear().RecordarAsync("a", """{"accion":"quitar_nota","indice":1}""", TestContext.Current.CancellationToken);
        Assert.Equal("""{"error":"indice_invalido"}""", json);
        Assert.Null(nuevo);
        Assert.Empty(f.Escritos);
    }

    [Fact]
    public async Task Recordar_SinCambio_NoEscribeNiDevuelveNuevo()
    {
        var f = new PerfilesFalsos();
        f.Sembrar("a", ConNotas);
        var (json, nuevo) = await f.Crear().RecordarAsync("a", """{"accion":"fijar_minutos","minutos":45}""", TestContext.Current.CancellationToken);
        Assert.Null(nuevo);
        Assert.Empty(f.Escritos);
        Assert.Contains("\"ok\":true", json);
    }

    [Fact]
    public async Task Leer_SinDoc_PorDefecto_SinEscribir()
    {
        var f = new PerfilesFalsos();
        Assert.Same(PerfilCoach.PorDefecto, await f.Crear().LeerAsync("a", TestContext.Current.CancellationToken));
        Assert.Empty(f.Escritos);
    }

    [Fact]
    public async Task GuardarPut_SinClavesConservaAlturaYPeso_ConNullLosBorra()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = new PerfilesFalsos();
        f.Sembrar("a", ConNotas with { AlturaCm = 175, PesoCorporalKg = 78.5m });
        var perfiles = f.Crear();

        var g = await perfiles.GuardarAsync("a", new PerfilCoach(30, null, []), traeAltura: false, traePeso: false, traeEquipo: false, ct: ct);
        Assert.Equal((30, 175, 78.5m), (g!.MinutosDisponibles, g.AlturaCm, g.PesoCorporalKg));
        Assert.True(g.Igual(f.Docs["a"].Perfil));

        g = await perfiles.GuardarAsync("a", new PerfilCoach(30, null, [], 180, null), traeAltura: true, traePeso: false, traeEquipo: false, ct: ct);
        Assert.Equal((180, 78.5m), (g!.AlturaCm, g.PesoCorporalKg));

        g = await perfiles.GuardarAsync("a", new PerfilCoach(30, null, []), traeAltura: true, traePeso: true, traeEquipo: true, ct: ct);
        Assert.Null(f.Docs["a"].Perfil.AlturaCm);
        Assert.Null(f.Docs["a"].Perfil.PesoCorporalKg);
    }

    [Fact]
    public async Task GuardarPut_SinClaveConservaEquipo_ConNullLoBorra()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = new PerfilesFalsos();
        f.Sembrar("a", ConNotas with { Equipo = "maquina" });
        var perfiles = f.Crear();

        var g = await perfiles.GuardarAsync("a", new PerfilCoach(30, null, []), traeAltura: false, traePeso: false, traeEquipo: false, ct: ct);
        Assert.Equal("maquina", g!.Equipo);

        g = await perfiles.GuardarAsync("a", new PerfilCoach(30, null, [], Equipo: "libre"), traeAltura: false, traePeso: false, traeEquipo: true, ct: ct);
        Assert.Equal("libre", f.Docs["a"].Perfil.Equipo);

        await perfiles.GuardarAsync("a", new PerfilCoach(30, null, []), traeAltura: false, traePeso: false, traeEquipo: true, ct: ct);
        Assert.Null(f.Docs["a"].Perfil.Equipo);
    }

    [Fact]
    public async Task GuardarPut_SinClavesConservaExperienciaYDias_ConNullLosBorra()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = new PerfilesFalsos();
        f.Sembrar("a", ConNotas with { Experiencia = "menos6m", DiasPorSemana = 4 });
        var perfiles = f.Crear();

        var g = await perfiles.GuardarAsync("a", new PerfilCoach(30, null, []), traeAltura: false, traePeso: false, traeEquipo: false,
            ct: ct);
        Assert.Equal(("menos6m", 4), (g!.Experiencia, g.DiasPorSemana));

        g = await perfiles.GuardarAsync("a", new PerfilCoach(30, null, [], Experiencia: "mas6m"), traeAltura: false, traePeso: false,
            traeEquipo: false, traeExperiencia: true, traeDias: false, ct: ct);
        Assert.Equal(("mas6m", 4), (f.Docs["a"].Perfil.Experiencia, f.Docs["a"].Perfil.DiasPorSemana));

        await perfiles.GuardarAsync("a", new PerfilCoach(30, null, []), traeAltura: false, traePeso: false, traeEquipo: false,
            traeExperiencia: true, traeDias: true, ct: ct);
        Assert.Null(f.Docs["a"].Perfil.Experiencia);
        Assert.Null(f.Docs["a"].Perfil.DiasPorSemana);
    }

    [Fact]
    public async Task GuardarPut_TrasConflicto_ConservaLoQueEscribioElOtro()
    {
        var f = new PerfilesFalsos();
        f.Sembrar("a", PerfilCoach.PorDefecto);
        // Entre intentos el coach guarda el peso: el reintento del PUT sin la clave no lo borra.
        var lecturas = 0;
        f.AlLeer = u => { if (++lecturas == 2) f.Sembrar(u, f.Docs[u].Perfil with { PesoCorporalKg = 80m }); };
        f.ConflictosPendientes = 1;

        var g = await f.Crear().GuardarAsync("a", new PerfilCoach(45, null, []), traeAltura: false, traePeso: false, traeEquipo: false,
            ct: TestContext.Current.CancellationToken);
        Assert.Equal(80m, g!.PesoCorporalKg);
        Assert.Equal(80m, f.Docs["a"].Perfil.PesoCorporalKg);
        Assert.Equal(45, f.Docs["a"].Perfil.MinutosDisponibles);
    }

    [Fact]
    public async Task Guardar_ConflictoUnaVez_Reintenta_DosVeces_False()
    {
        var f = new PerfilesFalsos { ConflictosPendientes = 1 };
        Assert.True(await f.Crear().GuardarAsync("a", ConNotas, TestContext.Current.CancellationToken));
        Assert.Equal(ConNotas.Notas, f.Docs["a"].Perfil.Notas);

        f.ConflictosPendientes = 2;
        Assert.False(await f.Crear().GuardarAsync("a", PerfilCoach.PorDefecto, TestContext.Current.CancellationToken));
        Assert.Equal(ConNotas.Notas, f.Docs["a"].Perfil.Notas);
    }
}
