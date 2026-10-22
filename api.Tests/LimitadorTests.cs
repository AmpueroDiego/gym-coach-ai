namespace GymCoach.Api.Tests;

public class LimitadorTests
{
    readonly RelojFalso _reloj = new(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void PermiteHastaElMaximo_YDiceCuantoEsperar()
    {
        var l = new Limitador(3, TimeSpan.FromSeconds(60), _reloj);
        Assert.True(l.Permitir("a", out _));
        _reloj.Avanzar(TimeSpan.FromSeconds(10));
        Assert.True(l.Permitir("a", out _));
        Assert.True(l.Permitir("a", out _));

        Assert.False(l.Permitir("a", out var espera));
        Assert.Equal(TimeSpan.FromSeconds(50), espera);
    }

    [Fact]
    public void VentanaDeslizante_LiberaLasMarcasViejas()
    {
        var l = new Limitador(2, TimeSpan.FromSeconds(60), _reloj);
        l.Permitir("a", out _);
        _reloj.Avanzar(TimeSpan.FromSeconds(30));
        l.Permitir("a", out _);
        Assert.False(l.Permitir("a", out _));

        _reloj.Avanzar(TimeSpan.FromSeconds(30)); // la primera sale de la ventana
        Assert.True(l.Permitir("a", out _));
        Assert.False(l.Permitir("a", out _));
    }

    [Fact]
    public void ClavesIndependientes()
    {
        var l = new Limitador(1, TimeSpan.FromSeconds(60), _reloj);
        Assert.True(l.Permitir("a", out _));
        Assert.False(l.Permitir("a", out _));
        Assert.True(l.Permitir("b", out _));
    }

    [Fact]
    public void LimpiaClavesInactivas()
    {
        var l = new Limitador(5, TimeSpan.FromSeconds(60), _reloj);
        for (var i = 0; i < 100; i++) l.Permitir("u" + i, out _);
        Assert.Equal(100, l.Claves);

        _reloj.Avanzar(TimeSpan.FromSeconds(61));
        l.Permitir("nuevo", out _);
        Assert.Equal(1, l.Claves);
    }

    [Fact]
    public async Task Concurrente_NoPasaDelMaximo()
    {
        var l = new Limitador(50, TimeSpan.FromSeconds(60), _reloj);
        var permitidos = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(hilo => Task.Run(() =>
        {
            for (var i = 0; i < 100; i++)
                if (l.Permitir("a", out _)) Interlocked.Increment(ref permitidos);
        }, TestContext.Current.CancellationToken)));
        Assert.Equal(50, permitidos);
    }
}
