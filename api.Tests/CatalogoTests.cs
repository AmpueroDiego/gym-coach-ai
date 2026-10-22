namespace GymCoach.Api.Tests;

public class CatalogoTests
{
    // Ids a los que mapea la importación de Hevy.
    static readonly string[] IdsHevy =
    [
        "curl-biceps-maquina", "pec-deck", "elevacion-talones-maquina", "press-pecho-maquina", "curl-martillo",
        "aduccion-cadera", "remo-iso-lateral", "jalon-pecho", "jalon-pecho-maquina", "elevaciones-laterales",
        "extension-cuadriceps", "prensa", "prensa-horizontal", "curl-femoral", "curl-predicador-maquina",
        "curl-femoral-sentado", "press-hombro-maquina", "extension-triceps-maquina", "crunch-maquina",
        "press-pecho-iso-lateral", "remo-bajo-iso-lateral", "hiperextension-inversa", "fondos-maquina",
    ];

    static readonly string[] IdsNuevos =
        ["crunch-maquina", "press-pecho-iso-lateral", "remo-bajo-iso-lateral", "hiperextension-inversa", "fondos-maquina"];

    [Fact]
    public void Los_ids_del_mapeo_de_Hevy_existen()
    {
        Assert.Equal(23, IdsHevy.Length);
        foreach (var id in IdsHevy) Assert.True(Catalogo.Existe(id), id);
    }

    [Fact]
    public void Los_ids_son_unicos()
    {
        var repetidos = Catalogo.Ejercicios.GroupBy(e => e.Id).Where(g => g.Count() > 1).Select(g => g.Key);
        Assert.Empty(repetidos);
    }

    [Fact]
    public void Los_nuevos_son_de_maquina_y_de_grupos_existentes()
    {
        foreach (var id in IdsNuevos)
        {
            var e = Catalogo.Ejercicios.Single(x => x.Id == id);
            Assert.Equal(Catalogo.Maquina, e.Equipo);
            Assert.True(Catalogo.Ejercicios.Count(x => x.Grupo == e.Grupo && x.Id != id) > 0, e.Grupo);
        }
    }
}
