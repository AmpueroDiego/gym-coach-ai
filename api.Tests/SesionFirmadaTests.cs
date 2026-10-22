using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using GymCoach.Api.Auth;

namespace GymCoach.Api.Tests;

public class SesionFirmadaTests
{
    readonly Tokens _tokens = new();
    readonly RelojFalso _reloj = new(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
    readonly SesionFirmada _sesion;

    public SesionFirmadaTests() => _sesion = _tokens.Sesion(_reloj);

    long Ahora => _reloj.Ahora.ToUnixTimeSeconds();

    // Firma un payload arbitrario con el mismo formato que Emitir: prueba lo que pasa tras un MAC válido.
    static string Firmar(string json, string secreto)
    {
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
        var mac = HMACSHA256.HashData(Convert.FromBase64String(secreto), Encoding.ASCII.GetBytes("gc_sesion.v1." + payload));
        return $"v1.{payload}.{Base64Url.EncodeToString(mac)}";
    }

    string FirmarJson(string json) => Firmar(json, _tokens.SecretoSesion);

    [Fact]
    public void IdaYVuelta()
    {
        var p = _sesion.Verificar(_sesion.Emitir("123", "Diego", "https://lh3.googleusercontent.com/a/foto=s96-c"));
        Assert.NotNull(p);
        Assert.Equal("123", p.Sub);
        Assert.Equal("Diego", p.Nombre);
        Assert.Equal("https://lh3.googleusercontent.com/a/foto=s96-c", p.Foto);
        Assert.Equal(Ahora + (long)SesionFirmada.Vida.TotalSeconds, p.Exp);
        Assert.Equal(Ahora, p.Iat);
        Assert.False(p.ConAnterior);
    }

    [Fact]
    public void Renovar_ConservaIatHastaElTope()
    {
        var iat = Ahora;
        var valor = _sesion.Emitir("123", "Diego", null);
        // Renovar cada 46 días como lo haría GET /api/sesion: vale hasta 365 días desde el login.
        for (var dias = 46; dias <= 365; dias += 46)
        {
            _reloj.Avanzar(TimeSpan.FromDays(46));
            var p = _sesion.Verificar(valor);
            Assert.NotNull(p);
            Assert.Equal(iat, p.Iat);
            Assert.True(_sesion.DebeRenovar(p));
            valor = _sesion.Emitir(p.Sub, p.Nombre, p.Foto, p.Iat);
        }
        // 368 días desde el login: la última cookie emitida aún no vence por exp, pero sí por el tope.
        _reloj.Ahora = DateTimeOffset.FromUnixTimeSeconds(iat).Add(SesionFirmada.VidaMaxima).AddSeconds(1);
        Assert.Null(_sesion.Verificar(valor));
        _reloj.Avanzar(TimeSpan.FromSeconds(-2));
        Assert.NotNull(_sesion.Verificar(valor));
    }

    [Theory]
    [InlineData("")]
    [InlineData(""","iat":"IAT" """)]
    [InlineData(""","iat":null""")]
    [InlineData(""","iat":1.5""")]
    [InlineData(""","iat":-1""")]
    [InlineData(""","iat":0""")]
    [InlineData(""","iat":-9223372036854775808""")]
    [InlineData(""","iat":9223372036854775807""")]
    public void IatAusenteOInvalido_FirmadoValido_EsNull(string iat) =>
        Assert.Null(_sesion.Verificar(FirmarJson(
            $$"""{"sub":"123","exp":{{Ahora + 3600}}{{iat.Trim().Replace("IAT", Ahora.ToString())}}}""")));

    [Fact]
    public void IatFuturo_FueraDeHolgura_EsNull()
    {
        Assert.Null(_sesion.Verificar(FirmarJson($$"""{"sub":"123","exp":{{Ahora + 3600}},"iat":{{Ahora + 301}}}""")));
        Assert.NotNull(_sesion.Verificar(FirmarJson($$"""{"sub":"123","exp":{{Ahora + 3600}},"iat":{{Ahora + 299}}}""")));
    }

    [Fact]
    public void IatMasViejoQueElTope_EsNull() =>
        Assert.Null(_sesion.Verificar(FirmarJson(
            $$"""{"sub":"123","exp":{{Ahora + 3600}},"iat":{{Ahora - (long)SesionFirmada.VidaMaxima.TotalSeconds - 1}}}""")));

    [Fact]
    public void NombreYFotoNull_IdaYVuelta()
    {
        var p = _sesion.Verificar(_sesion.Emitir("123", null, null));
        Assert.NotNull(p);
        Assert.Null(p.Nombre);
        Assert.Null(p.Foto);
    }

    [Fact]
    public void PayloadConUnByteCambiado_EsNull()
    {
        var partes = _sesion.Emitir("123", "Diego", null).Split('.');
        var bytes = Base64Url.DecodeFromChars(partes[1]);
        bytes[5] ^= 0x01;
        Assert.Null(_sesion.Verificar($"v1.{Base64Url.EncodeToString(bytes)}.{partes[2]}"));
    }

    [Fact]
    public void MacAlterado_EsNull()
    {
        var partes = _sesion.Emitir("123", null, null).Split('.');
        var mac = Base64Url.DecodeFromChars(partes[2]);
        mac[0] ^= 0x01;
        Assert.Null(_sesion.Verificar($"v1.{partes[1]}.{Base64Url.EncodeToString(mac)}"));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(33)]
    [InlineData(0)]
    public void MacDeOtroLargo_EsNull(int largo)
    {
        var partes = _sesion.Emitir("123", null, null).Split('.');
        var mac = Base64Url.DecodeFromChars(partes[2]);
        var otro = new byte[largo];
        Array.Copy(mac, otro, Math.Min(largo, mac.Length));
        Assert.Null(_sesion.Verificar($"v1.{partes[1]}.{Base64Url.EncodeToString(otro)}"));
    }

    [Fact]
    public void MacTruncado_EsNull()
    {
        var valor = _sesion.Emitir("123", null, null);
        Assert.Null(_sesion.Verificar(valor[..^4]));
    }

    [Fact]
    public void OtroSecreto_EsNull() =>
        Assert.Null(_sesion.Verificar(new SesionFirmada(Tokens.NuevoSecreto(), null, _reloj).Emitir("123", null, null)));

    [Fact]
    public void FirmadaConAnterior_ValidaConMarca()
    {
        var vieja = new SesionFirmada(_tokens.SecretoSesionAnterior, null, _reloj).Emitir("123", null, null);
        var rotada = new SesionFirmada(_tokens.SecretoSesion, _tokens.SecretoSesionAnterior, _reloj);
        var p = rotada.Verificar(vieja);
        Assert.NotNull(p);
        Assert.True(p.ConAnterior);
        Assert.True(rotada.DebeRenovar(p));
        // Sin la anterior configurada, la misma cookie no vale.
        Assert.Null(_sesion.Verificar(vieja));
    }

    [Fact]
    public void Vencida_EsNull()
    {
        var valor = _sesion.Emitir("123", null, null);
        _reloj.Avanzar(SesionFirmada.Vida + TimeSpan.FromSeconds(1));
        Assert.Null(_sesion.Verificar(valor));
    }

    [Fact]
    public void ExpIgualAAhora_EsNull()
    {
        var valor = _sesion.Emitir("123", null, null);
        _reloj.Avanzar(SesionFirmada.Vida);
        Assert.Null(_sesion.Verificar(valor));
    }

    [Fact]
    public void ExpUnAnio_FirmadoValido_EsNull() =>
        Assert.Null(_sesion.Verificar(FirmarJson($$"""{"sub":"123","nombre":null,"foto":null,"exp":{{Ahora + 365 * 86400}}}""")));

    [Fact]
    public void ExpDentroDeLaHolgura_EsValido() =>
        Assert.NotNull(_sesion.Verificar(FirmarJson($$"""{"sub":"123","exp":{{Ahora + 90 * 86400 + 240}},"iat":{{Ahora}}}""")));

    [Theory]
    [InlineData("""{"sub":"","exp":EXP}""")]
    [InlineData("""{"sub":"   ","exp":EXP}""")]
    [InlineData("""{"exp":EXP}""")]
    [InlineData("""{"sub":123,"exp":EXP}""")]
    [InlineData("""{"sub":null,"exp":EXP}""")]
    [InlineData("""{"sub":"123"}""")]
    [InlineData("""{"sub":"123","exp":"EXP"}""")]
    [InlineData("""{"sub":"123","exp":1.5}""")]
    [InlineData("""["sub","123"]""")]
    [InlineData("\"texto\"")]
    [InlineData("no es json")]
    public void PayloadFirmadoPeroInvalido_EsNull(string json) =>
        Assert.Null(_sesion.Verificar(FirmarJson(json.Replace("EXP", (Ahora + 3600).ToString()))));

    [Fact]
    public void SubMayorA255_FirmadoValido_EsNull() =>
        Assert.Null(_sesion.Verificar(FirmarJson($$"""{"sub":"{{new string('1', 256)}}","exp":{{Ahora + 3600}}}""")));

    [Fact]
    public void Sub255_EsValido() =>
        Assert.NotNull(_sesion.Verificar(FirmarJson($$"""{"sub":"{{new string('1', 255)}}","exp":{{Ahora + 3600}},"iat":{{Ahora}}}""")));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmitirSubVacio_Lanza(string sub) =>
        Assert.Throws<ArgumentException>(() => _sesion.Emitir(sub, null, null));

    [Fact]
    public void EmitirSubMayorA255_Lanza() =>
        Assert.Throws<ArgumentException>(() => _sesion.Emitir(new string('1', 256), null, null));

    [Fact]
    public void FormatosInvalidos_SonNull()
    {
        var partes = _sesion.Emitir("123", null, null).Split('.');
        string?[] valores =
        [
            null,
            "",
            $"v2.{partes[1]}.{partes[2]}",
            $"{partes[1]}.{partes[2]}",
            $".{partes[1]}.{partes[2]}",
            $"v1.{partes[1]}",
            $"v1.{partes[1]}.{partes[2]}.extra",
            $"v1.{partes[1]}.!!no-es-base64!!",
            $"v1.%%%.{partes[2]}",
            "basura",
        ];
        foreach (var v in valores) Assert.Null(_sesion.Verificar(v));
    }

    [Fact]
    public void PayloadNoBase64_ConMacValido_EsNull() => Assert.Null(_sesion.Verificar(FirmarCrudo("%%%")));

    string FirmarCrudo(string payload)
    {
        var mac = HMACSHA256.HashData(Convert.FromBase64String(_tokens.SecretoSesion), Encoding.ASCII.GetBytes("gc_sesion.v1." + payload));
        return $"v1.{payload}.{Base64Url.EncodeToString(mac)}";
    }

    [Fact]
    public void MasDe2048_EsNull()
    {
        var valor = FirmarJson($$"""{"sub":"123","exp":{{Ahora + 3600}},"relleno":"{{new string('x', 2000)}}"}""");
        Assert.True(valor.Length > 2048);
        Assert.Null(_sesion.Verificar(valor));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("esto no es base64!")]
    [InlineData("c2hvcnQ=")] // 5 bytes
    public void SecretoInvalido_NoArranca(string? secreto)
    {
        Assert.Throws<InvalidOperationException>(() => new SesionFirmada(secreto, null, _reloj));
        // La anterior, si viene, sigue las mismas reglas (vacía = no hay anterior).
        if (!string.IsNullOrWhiteSpace(secreto))
            Assert.Throws<InvalidOperationException>(() => new SesionFirmada(_tokens.SecretoSesion, secreto, _reloj));
    }

    [Fact]
    public void Secreto31Bytes_NoArranca() =>
        Assert.Throws<InvalidOperationException>(() =>
            new SesionFirmada(Convert.ToBase64String(RandomNumberGenerator.GetBytes(31)), null, _reloj));

    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x41)]
    [InlineData((byte)0xFF)]
    public void SecretoConTodosLosBytesIguales_NoArranca(byte b)
    {
        var relleno = Convert.ToBase64String(Enumerable.Repeat(b, 48).ToArray());
        Assert.Throws<InvalidOperationException>(() => new SesionFirmada(relleno, null, _reloj));
        Assert.Throws<InvalidOperationException>(() => new SesionFirmada(_tokens.SecretoSesion, relleno, _reloj));
    }

    [Fact]
    public void Secreto32Bytes_Arranca() =>
        Assert.NotNull(new SesionFirmada(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), null, _reloj));

    [Fact]
    public void PeorCaso_CabeEnLosLimites()
    {
        // Sub al tope, nombre de emojis compuestos (~25 bytes cada uno), foto al tope de 512.
        var familia = "\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466";
        var nombres = new[]
        {
            string.Concat(Enumerable.Repeat(familia, 100)),
            // Emoji simple fuera del BMP: el JSON lo escapa como \uXXXX\uXXXX (12 bytes).
            string.Concat(Enumerable.Repeat("\U0001F4AA", 60)),
            new string('"', 100),
            new string('\\', 100),
            new string('ñ', 100),
        };
        var foto = "https://lh3.googleusercontent.com/" + new string('a', 512 - "https://lh3.googleusercontent.com/".Length);
        Assert.Equal(512, foto.Length);
        foreach (var nombre in nombres)
        {
            var valor = _sesion.Emitir(new string('9', 255), nombre, foto);
            Assert.True(valor.Length < 2048, $"valor de {valor.Length}");
            Assert.True(_sesion.HeaderEmitir(valor).Length < 4096);
            var p = _sesion.Verificar(valor);
            Assert.NotNull(p);
            Assert.Equal(foto, p.Foto);
        }
    }

    [Fact]
    public void HeaderEmitir_Exacto() =>
        Assert.Equal("__Host-gc_sesion=abc; Max-Age=7776000; Path=/; Secure; HttpOnly; SameSite=Lax", _sesion.HeaderEmitir("abc"));

    [Fact]
    public void HeaderBorrar_Exacto() =>
        Assert.Equal("__Host-gc_sesion=; Max-Age=0; Path=/; Secure; HttpOnly; SameSite=Lax", _sesion.HeaderBorrar());

    [Fact]
    public void ModoInseguro_SinPrefijoNiSecure()
    {
        var local = new SesionFirmada(_tokens.SecretoSesion, null, _reloj, inseguro: true);
        Assert.Equal("gc_sesion", local.Nombre);
        Assert.Equal("gc_sesion=abc; Max-Age=7776000; Path=/; HttpOnly; SameSite=Lax", local.HeaderEmitir("abc"));
        Assert.Equal("gc_sesion=; Max-Age=0; Path=/; HttpOnly; SameSite=Lax", local.HeaderBorrar());
    }

    [Fact]
    public void DebeRenovar_SoloAMitadDeVida()
    {
        var p = _sesion.Verificar(_sesion.Emitir("123", null, null))!;
        Assert.False(_sesion.DebeRenovar(p));
        _reloj.Avanzar(TimeSpan.FromDays(44));
        Assert.False(_sesion.DebeRenovar(p));
        _reloj.Avanzar(TimeSpan.FromDays(2));
        Assert.True(_sesion.DebeRenovar(p));
    }

    [Theory]
    [InlineData("  Diego  ", "Diego")]
    [InlineData("Die\u0000go\u001b\u007f\u0085", "Diego")]
    [InlineData("\t\n\r", null)]
    [InlineData("   ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("José Ñandú", "José Ñandú")]
    public void NombreSeguro_Sanea(string? entrada, string? esperado) =>
        Assert.Equal(esperado, SesionFirmada.NombreSeguro(entrada));

    [Fact]
    public void NombreSeguro_TopeDe60Grafemas()
    {
        // "é" como e + acento combinante: 2 chars, 1 grafema.
        var r = SesionFirmada.NombreSeguro(string.Concat(Enumerable.Repeat("é", 80)));
        Assert.NotNull(r);
        Assert.Equal(60, new System.Globalization.StringInfo(r).LengthInTextElements);
        Assert.Equal(60, SesionFirmada.NombreSeguro(new string('a', 100))!.Length);
    }

    [Fact]
    public void NombreSeguro_SustitutoSuelto_CortaSinLanzar()
    {
        // \ud800 solo no es UTF-16 válido: el encoder lanzaría y esa cuenta no podría entrar.
        Assert.Equal("a", SesionFirmada.NombreSeguro("a\ud800b"));
        Assert.Null(SesionFirmada.NombreSeguro("\ud800"));
    }

    [Fact]
    public void NombreSeguro_NoPartaUnGrafema()
    {
        var familia = "\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466";
        var r = SesionFirmada.NombreSeguro(string.Concat(Enumerable.Repeat(familia, 60)))!;
        var escapado = System.Text.Json.JsonEncodedText.Encode(r, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping);
        Assert.True(escapado.EncodedUtf8Bytes.Length <= 360);
        Assert.True(r.Length > 0);
        Assert.Equal(0, r.Length % familia.Length);
    }

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/a/ACg8oc=s96-c", true)]
    [InlineData("https://lh6.googleusercontent.com/x", true)]
    [InlineData("https://LH4.googleusercontent.com/x", true)]
    [InlineData("https://lh7.googleusercontent.com/x", false)]
    [InlineData("https://lh2.googleusercontent.com/x", false)]
    [InlineData("http://lh3.googleusercontent.com/x", false)]
    [InlineData("https://lh3.googleusercontent.com:8443/x", false)]
    [InlineData("https://lh3.googleusercontent.com:443/x", false)]
    [InlineData("https://user@lh3.googleusercontent.com/x", false)]
    [InlineData("https://lh3.googleusercontent.com.evil.com/x", false)]
    [InlineData("https://evil.com/lh3.googleusercontent.com", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("https://lh3.googleusercontent.com/a b", false)]
    [InlineData("https://lh3.googleusercontent.com/a\tb", false)]
    [InlineData("https://lh3.googleusercontent.com/añ", false)]
    [InlineData("https://lh3.googleusercontent.com/a\u007f", false)]
    [InlineData("https://lh3.googleusercontent.com/a\"b", false)]
    [InlineData("https://lh3.googleusercontent.com/a\\b", false)]
    [InlineData("https://lh3.googleusercontent.com/​x", false)]
    [InlineData("/relativa.png", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void FotoSegura_SoloGoogle(string? foto, bool valida) =>
        Assert.Equal(valida ? foto : null, SesionFirmada.FotoSegura(foto));

    [Fact]
    public void FotoSegura_MasDe512_EsNull()
    {
        var baseUrl = "https://lh3.googleusercontent.com/";
        Assert.NotNull(SesionFirmada.FotoSegura(baseUrl + new string('a', 512 - baseUrl.Length)));
        Assert.Null(SesionFirmada.FotoSegura(baseUrl + new string('a', 513 - baseUrl.Length)));
    }
}
