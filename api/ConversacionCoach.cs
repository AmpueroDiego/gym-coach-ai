using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace GymCoach.Api;

/// <summary>Perfil: solo si recordar lo cambió en este turno. Rutina y Programa: la última propuesta válida del turno.</summary>
public sealed record ResultadoCoach(string Respuesta, IReadOnlyList<string> Herramientas, RutinaPropuesta? Rutina, PerfilCoach? Perfil,
    ProgramaPropuesto? Programa = null);

/// <summary>
/// Un turno del coach: manda el historial al modelo, ejecuta las herramientas que pida y repite hasta que
/// responda con texto. El userId sale siempre de ctx (del token), nunca de los argumentos del modelo.
/// </summary>
public sealed partial class ConversacionCoach(IModeloCoach modelo, HerramientasCoach herramientas, ILogger<ConversacionCoach>? log = null)
{
    // La última ronda va sin herramientas: obliga a responder con lo que ya consultó. La ronda correctiva suma hasta 2.
    public const int MaxRondas = 5;
    // Tope de ejecuciones por mensaje: acota RU y latencia aunque el modelo pida muchas en una ronda.
    public const int MaxEjecuciones = 6;
    // Llamadas distintas a progreso por mensaje (el prompt pide 2 como mucho): cada una es una consulta propia.
    public const int MaxProgreso = 3;
    // recordar por mensaje, aparte de MaxEjecuciones: un usuario nuevo cuenta varios datos y aún hay que proponer.
    public const int MaxRecordar = 4;
    public const string RespuestaFiltrada = "No puedo responder a ese mensaje.";
    public const string RespuestaSinTexto = "No pude completar la consulta. Prueba con una pregunta más concreta.";
    // El texto decía que había rutina y no la hay: nunca una tarjeta que la app no va a mostrar.
    public const string RespuestaSinTarjeta = "No pude armar la rutina esta vez. Pídemela de nuevo.";
    public const string AclaracionSinTarjeta = "(No se creó ninguna rutina.)";
    public const string RespuestaFiltradaEn = "I can't answer that message.";
    public const string RespuestaSinTextoEn = "I couldn't complete that request. Try a more specific question.";
    public const string RespuestaSinTarjetaEn = "I couldn't put the routine together this time. Ask me again.";
    public const string AclaracionSinTarjetaEn = "(No routine was created.)";
    public const string Correccion = "No llamaste proponer_rutina. Llámala ahora con ejercicios de DATOS; no respondas sin llamarla.";
    public const string CorreccionPrograma = "No llamaste proponer_programa. Llámala ahora con ejercicios de DATOS; no respondas sin llamarla.";
    public const string CorreccionError = "Corrige la rutina con estos ejercicios y vuelve a llamar proponer_rutina; si el usuario mencionó dolor, " +
        "aplica la regla 4. Resultado anterior: ";
    public const string CorreccionErrorPrograma = "Corrige el programa (rutina indica cuál, desde 0) y vuelve a llamar proponer_programa; " +
        "si el usuario mencionó dolor, aplica la regla 4. Resultado anterior: ";
    const string TopeAlcanzado = """{"error":"tope_de_herramientas"}""";
    const string UnaALaVez = """{"error":"una_a_la_vez"}""";
    const string Desconocida = """{"error":"herramienta_desconocida"}""";

    public async Task<ResultadoCoach> ResponderAsync(ContextoCoach ctx, IReadOnlyList<MensajeEntrada> historial, CancellationToken ct)
    {
        // ParaCoach y ParaPrompt solo con datos: sin perfil de entrenamiento o sin plan, el prompt va sin ese bloque.
        var entrenamiento = ctx.Entrenamiento is { } e ? CalculosPerfil.ParaCoach(e, ctx.Perfil, ctx.Hoy) : null;
        var planHoy = ctx.PlanHoy is { } plan ? PlanesDia.ParaPrompt(plan) : null;
        var mensajes = new List<MensajeChat> { new("system", PromptCoach.Sistema(ctx.Hoy, ctx.Perfil, entrenamiento, planHoy, ctx.Idioma)) };
        // Con memoria (agente de Foundry) el system va como structured_inputs y el historial ya está guardado allá: al modelo
        // solo van los items nuevos. La lista completa se sigue armando porque las guardias la usan.
        // Si una guardia reemplaza la respuesta final por un texto fijo, la conversación en Foundry conserva la del modelo (aceptado).
        var sesion = modelo.ConMemoria && ctx.ConversacionId is { } conversacionId
            ? new SesionAgente(conversacionId, PromptCoach.Contexto(ctx.Hoy, ctx.Perfil, entrenamiento, planHoy, ctx.Idioma))
            : null;
        // Los textos fijos van en el idioma de la app; Correccion y CorreccionError son para el modelo y quedan en español.
        var en = ctx.Idioma == "en";
        var sinTexto = en ? RespuestaSinTextoEn : RespuestaSinTexto;
        foreach (var m in historial) mensajes.Add(new(m.Rol == "usuario" ? "user" : "assistant", m.Texto));
        // Con memoria, desde dónde mandar en la próxima ronda: en la primera, solo el último mensaje del usuario.
        var enviados = mensajes.FindLastIndex(m => m.Rol == "user") is var u and >= 0 ? u : mensajes.Count;
        var ultimoUsuario = historial.LastOrDefault(m => m.Rol == "usuario")?.Texto ?? "";
        var pideRutina = PideRutina(ultimoUsuario);
        // Pedido en texto libre ("dame un entreno de pecho"): solo da pie a la ronda correctiva, no cambia las herramientas.
        var pideLibre = PideRutinaLibre(ultimoUsuario);
        var pideProgramaLibre = PideProgramaLibre(ultimoUsuario);
        // Ante dolor manda la regla 4: el modelo puede no proponer nada, no se le fuerza.
        // En texto libre también cuentan los mensajes anteriores: "me duele el hombro" y luego "ok, dame una rutina" sigue
        // siendo con dolor. El plan del día (botón fijo) solo mira el último: siempre trae una rutina.
        var hablaDeDolor = pideRutina ? HablaDeDolor(ultimoUsuario)
            : historial.Where(m => m.Rol == "usuario").TakeLast(3).Any(m => HablaDeDolor(m.Texto));
        // Una sola ronda correctiva por mensaje; obligada solo vale para la ronda siguiente.
        var corregido = false;
        string? obligada = null;
        // Error corregible de la última propuesta del turno (JSON con permitidos): da pie a la ronda correctiva.
        string? errorProponer = null;
        // De qué herramienta es errorProponer (y la que se obliga a corregir).
        var ultimaPropuesta = "proponer_rutina";

        var usadas = new List<string>();
        // Misma herramienta con los mismos argumentos dentro del mensaje: se reutiliza el resultado (no gasta RU otra vez).
        var cache = new Dictionary<(string, string), string>();
        // Series ya consultadas en el turno: herramientas distintas sobre la misma ventana no repiten la consulta.
        var cacheTurno = new CacheTurno();
        // Mayor pesoKg que el coach vio por ejercicio: tope de los pesos que puede proponer.
        var pesosVistos = new Dictionary<string, decimal>(StringComparer.Ordinal);
        // Los pesos del perfil de entrenamiento (en DATOS y en mis_ejercicios) también son vistos: mejor y última serie,
        // y la marca reciente de cada tendencia.
        foreach (var ej in ctx.Entrenamiento?.Ejercicios ?? [])
            foreach (var marca in new[] { ej.MejorSerie, ej.UltimaSerie })
                if (marca is not null) Anotar(pesosVistos, ej.EjercicioId, marca.PesoKg);
        foreach (var t in ctx.Entrenamiento?.Tendencias ?? [])
            if (t.Reciente is not null) Anotar(pesosVistos, t.EjercicioId, t.Reciente.PesoKg);
        var progresos = new HashSet<string>(StringComparer.Ordinal);
        var ejecuciones = 0;
        var recordados = 0;
        int tokensEntrada = 0, tokensSalida = 0, rondas = 0;
        RutinaPropuesta? rutina = null;
        ProgramaPropuesto? programa = null;
        PerfilCoach? perfil = null;

        ResultadoCoach Fin(string respuesta, IReadOnlyList<string> herramientasUsadas)
        {
            // Nunca el texto: solo métricas.
            log?.LogInformation("Coach: {Rondas} rondas, herramientas [{Herramientas}], tokens {Entrada}/{Salida}",
                rondas, string.Join(",", usadas), tokensEntrada, tokensSalida);
            return new(respuesta, herramientasUsadas, rutina is null ? null : SinPesosInventados(rutina, pesosVistos), perfil,
                programa is null ? null : programa with { Rutinas = [.. programa.Rutinas.Select(p => SinPesosInventados(p, pesosVistos))] });
        }

        // La ronda correctiva puede sumar dos rondas (la obligada y la respuesta): un usuario nuevo gasta varias en
        // recordar y mis_ejercicios y, sin ellas, ya no quedaba ronda para proponer.
        var limite = MaxRondas;
        for (var ronda = 1; ronda <= limite; ronda++)
        {
            rondas = ronda;
            var permitir = ronda < limite;
            // El plan del día es siempre una rutina: sin proponer_programa.
            var ofrecidas = pideRutina ? HerramientasCoach.DefinicionesPlan : HerramientasCoach.Definiciones;
            // Los assistant (con o sin tool calls) los generó el modelo: el servidor ya los guardó, no se reenvían.
            IReadOnlyList<MensajeChat> aEnviar = sesion is null ? mensajes : [.. mensajes.Skip(enviados).Where(m => m.Rol != "assistant")];
            enviados = mensajes.Count;
            var r = await modelo.CompletarAsync(aEnviar, ofrecidas, permitir, permitir ? obligada : null, sesion, ct);
            obligada = null;
            tokensEntrada += r.TokensEntrada;
            tokensSalida += r.TokensSalida;

            if (r.Filtrada)
            {
                // Sin tarjeta: lo propuesto antes del corte no acompaña una respuesta que no se dio.
                rutina = null;
                programa = null;
                return Fin(en ? RespuestaFiltradaEn : RespuestaFiltrada, []);
            }
            // Sin tool calls (o en la última ronda, que no las admite): esta es la respuesta.
            if (r.Llamadas.Count == 0 || !permitir)
            {
                var texto = string.IsNullOrWhiteSpace(r.Texto) ? sinTexto : r.Texto.Trim();
                if (rutina is null && programa is null)
                {
                    // gpt-5-mini a veces anuncia la rutina o el programa ("te la preparo", "la verás en la app") sin llamar
                    // proponer_rutina ni proponer_programa. Pedir permiso lo frena el prompt (regla 16), no el código.
                    var nuncaLlamada = !usadas.Contains("proponer_rutina") && !usadas.Contains("proponer_programa");
                    // pesoKg o proponer_* en el texto es la rutina dictada en vez de propuesta: cuenta como anunciarla.
                    var afirma = nuncaLlamada && (AfirmaTarjeta(texto) || DictaRutina(texto));
                    // Se da una ronda para llamarla y otra para responder. Si ya la llamó, solo se insiste ante un error que el
                    // modelo puede corregir (no no_disponible ni el tope). Con dolor nunca se fuerza (regla 4).
                    if (!corregido && !hablaDeDolor &&
                        (nuncaLlamada && (pideRutina || pideLibre || pideProgramaLibre || afirma) || errorProponer is not null))
                    {
                        corregido = true;
                        limite = Math.Max(limite, ronda + 2);
                        // Si lo que anunció o pidió es un programa, se obliga proponer_programa (el plan del día no la ofrece).
                        var anunciaPrograma = nuncaLlamada && !pideRutina && (AnunciaPrograma(texto) || pideProgramaLibre);
                        obligada = nuncaLlamada ? anunciaPrograma ? "proponer_programa" : "proponer_rutina" : ultimaPropuesta;
                        if (!string.IsNullOrWhiteSpace(r.Texto)) mensajes.Add(new("assistant", r.Texto));
                        mensajes.Add(new("system", nuncaLlamada ? anunciaPrograma ? CorreccionPrograma : Correccion
                            : ultimaPropuesta == "proponer_programa" ? CorreccionErrorPrograma + errorProponer
                            : CorreccionError + errorProponer));
                        continue;
                    }
                    // Con dolor no se pisa el texto (puede traer el consejo de ver a un profesional): solo se aclara.
                    if (afirma)
                        texto = hablaDeDolor ? texto + " " + (en ? AclaracionSinTarjetaEn : AclaracionSinTarjeta)
                            : en ? RespuestaSinTarjetaEn : RespuestaSinTarjeta;
                }
                return Fin(texto, usadas);
            }

            mensajes.Add(new("assistant", r.Texto, r.Llamadas));
            // Acciones de recordar ya ejecutadas en esta ronda.
            var accionesEnLaRonda = new HashSet<string>(StringComparer.Ordinal);
            // En secuencia: cada tool_call_id necesita su respuesta, y recordar puede cambiar el perfil para las siguientes.
            foreach (var llamada in r.Llamadas)
            {
                var esRecordar = llamada.Nombre == "recordar";
                var clave = (llamada.Nombre, llamada.ArgumentosJson);
                string? json;
                // Varios recordar en una ronda van en secuencia y cada uno relee el perfil (ETag): no se pisan. Solo se corta
                // la misma fijar_* repetida, que se contradiría; agregar_nota puede repetirse. Sin accion legible se ejecuta
                // y Aplicar responde el error.
                var accion = esRecordar ? AccionDe(llamada.ArgumentosJson) : null;
                if (accion is not null && accion != "agregar_nota" && !accionesEnLaRonda.Add(accion)) json = UnaALaVez;
                // En el plan del día no existe: no se ejecuta ni cuenta. El agente tiene todas las herramientas: con memoria
                // vale solo lo ofrecido en esta ronda.
                else if (pideRutina && llamada.Nombre == "proponer_programa" ||
                    sesion is not null && !ofrecidas.Any(h => h.Nombre == llamada.Nombre)) json = Desconocida;
                else if (!cache.TryGetValue(clave, out json))
                {
                    var esProgreso = llamada.Nombre == "progreso";
                    if (esRecordar ? recordados >= MaxRecordar : ejecuciones >= MaxEjecuciones || esProgreso && progresos.Count >= MaxProgreso)
                        json = TopeAlcanzado;
                    else
                    {
                        if (esRecordar) recordados++;
                        else ejecuciones++;
                        if (esProgreso) progresos.Add(llamada.ArgumentosJson);
                        var res = await herramientas.EjecutarAsync(llamada.Nombre, llamada.ArgumentosJson, ctx, ct, cache: cacheTurno);
                        json = res.Json;
                        // Solo las consultas se reutilizan: recordar escribe, proponer_rutina fija la tarjeta (A, B, A deja A),
                        // mis_ejercicios no gasta RU y un error puede ser transitorio.
                        if (llamada.Nombre is "ver_entrenos_recientes" or "progreso" or "volumen_por_grupo" && !EsError(json)) cache[clave] = json;
                        if (!usadas.Contains(llamada.Nombre)) usadas.Add(llamada.Nombre);
                        // Cualquier propuesta, válida o fallida, limpia la otra tarjeta: gana la última, y si falló el texto dirá
                        // que no se pudo (sin una tarjeta anterior que lo contradiga).
                        if (llamada.Nombre == "proponer_rutina")
                        {
                            rutina = res.Rutina;
                            programa = null;
                            errorProponer = EsErrorCorregible(json) ? json : null;
                            ultimaPropuesta = "proponer_rutina";
                        }
                        if (llamada.Nombre == "proponer_programa")
                        {
                            programa = res.Programa;
                            rutina = null;
                            errorProponer = EsErrorCorregible(json) ? json : null;
                            ultimaPropuesta = "proponer_programa";
                        }
                        if (res.PerfilNuevo is not null)
                        {
                            perfil = res.PerfilNuevo;
                            ctx = ctx with { Perfil = res.PerfilNuevo };
                            // Lo calculado con el perfil anterior (equipo preferido, minutos) ya no vale.
                            cache.Clear();
                        }
                    }
                    if (llamada.Nombre is "proponer_rutina" or "proponer_programa" && json == TopeAlcanzado)
                    {
                        rutina = null;
                        programa = null;
                        errorProponer = null;
                    }
                }
                if (llamada.Nombre is "progreso" or "ver_entrenos_recientes") AnotarPesos(json, pesosVistos);
                mensajes.Add(new("tool", json, LlamadaId: llamada.Id));
            }
        }
        return Fin(sinTexto, usadas);
    }

    // Solo los mensajes fijos del plan del día: palabras sueltas daban falsos positivos ("no quiero una rutina").
    static readonly string[] PedidosFijos = [Normalizar(PlanesDia.MensajePlan), Normalizar(PlanesDia.MensajeOtraOpcion),
        Normalizar(PlanesDia.MensajeTraducirEs), Normalizar(PlanesDia.MensajeTraducirEn)];
    // Afirmaciones concretas de que la rutina ya está; "en la app" o "tarjeta" sueltos no ("registra tus series en la app").
    static readonly string[] AnunciosDeTarjeta = ["ya esta en la app", "la veras en la app", "lo veras en la app", "te deje la rutina",
        "te dejo la rutina", "te dejo la tarjeta", "rutina lista", "he creado", "te la preparo", "te armo", "la genero", "dame un segundo",
        "te la muestro", "voy a proponerte", "in the app now", "you'll see it in the app", "it's in the app", "i've created",
        "i've prepared", "i've built", "i'll put together", "i'll prepare", "i'll create", "routine is ready", "here's your routine",
        "give me a second", "let me put together",
        // Programa: solo afirmaciones de que ya está, ninguna pregunta de permiso (forzaban la tarjeta ante un "¿cómo voy?").
        "te dejo el programa", "programa listo", "te preparo una rutina", "te preparo un programa", "here's your program", "program is ready"];
    static readonly string[] Dolor = ["duele", "dolor", "lesion", "lesione", "molest", "tendinitis", "lastim", "esguince", "hinchad",
        "tiron", "desgarr", "contractur", "inflamad", "calambre"];

    // En inglés por palabra completa: como subcadena "pain" está en "Spain", "sore" en "tesorería", "strain" en "constraint".
    [GeneratedRegex(@"\b(?:pain|pains|painful|hurt|hurts|hurting|sore|soreness|sprain\w*|strain|strains|strained|swollen|swelling|" +
        @"ache|aches|aching|achy|tendonitis|torn|pulled|tweak\w*|cramp\w*|numb|numbness|injur\w*|fractur\w*|dislocat\w*|bruis\w*)\b")]
    private static partial Regex DolorEn();

    /// <summary>Filtrada o sin texto, en cualquier idioma: no es una respuesta que valga guardar (plan del día).</summary>
    public static bool EsRespuestaVacia(string r) =>
        r is RespuestaFiltrada or RespuestaSinTexto or RespuestaFiltradaEn or RespuestaSinTextoEn;

    internal static bool PideRutina(string mensaje) => PedidosFijos.Contains(Normalizar(mensaje.Trim()));

    // Verbo de pedido y, a lo sumo, dos palabras de relleno antes del sustantivo: "quiero saber si mi rutina" no es un pedido.
    // Sin "mi": "dame mi sesión de ayer" consulta el historial, no pide una nueva.
    // En inglés el adjetivo va antes ("a chest workout"): se admite una palabra más. Con "no", "tampoco", "nunca" o "don't" delante no pide.
    const string VerboDePedido = @"(?<!\b(?:no|tampoco|nunca|jamas) )(?<!n't )\b(?:dame|arma|armame|haz|hazme|propon|proponme|prepara|preparame|disena|disename|" +
        @"quiero|necesito|recomiendame|sugiereme|give me|make me|build me|build|create|i want|i need|can you make|suggest|recommend)" +
        @"(?:\s+(?:un|una|otro|otra|nuevo|nueva|a|an|another|new|me)){0,2}(?:\s+[a-z]+(?=\s+(?:workout|routine|session|exercises|" +
        @"program|weekly plan|split)\b))?\s+";

    [GeneratedRegex(VerboDePedido + @"(?:rutina|entreno|entrenamiento|sesion|ejercicios|workout|routine|session|exercises)\b")]
    private static partial Regex PedidoRutina();

    [GeneratedRegex(VerboDePedido + @"(?:programa|program|plan semanal|weekly plan|split)\b")]
    private static partial Regex PedidoPrograma();

    internal static bool PideRutinaLibre(string mensaje) => PedidoRutina().IsMatch(Normalizar(mensaje));

    internal static bool PideProgramaLibre(string mensaje) => PedidoPrograma().IsMatch(Normalizar(mensaje));

    // Solo lo que delata una rutina dictada en texto. Otros nombres internos ("revisé mis_ejercicios") no obligan una tarjeta
    // que el usuario no pidió.
    static readonly string[] DeRutinaDictada = ["pesokg", "proponer_rutina", "proponer_programa"];

    internal static bool DictaRutina(string texto)
    {
        var t = Normalizar(texto);
        return DeRutinaDictada.Any(p => t.Contains(p, StringComparison.Ordinal));
    }

    internal static bool HablaDeDolor(string mensaje)
    {
        var t = Normalizar(mensaje);
        return Dolor.Any(p => t.Contains(p, StringComparison.Ordinal)) || DolorEn().IsMatch(t);
    }

    // Solo las frases de anuncio de programa: "programa" suelto ("sigue tu programa de pecho") no obliga un programa.
    static readonly string[] AnunciosDePrograma = ["te dejo el programa", "programa listo", "te preparo un programa", "here's your program",
        "program is ready"];

    internal static bool AnunciaPrograma(string texto)
    {
        var t = Normalizar(texto);
        return AnunciosDePrograma.Any(p => t.Contains(p, StringComparison.Ordinal));
    }

    internal static bool AfirmaTarjeta(string texto)
    {
        var t = Normalizar(texto);
        return AnunciosDeTarjeta.Any(p => t.Contains(p, StringComparison.Ordinal));
    }

    /// <summary>Minúsculas, sin tildes y con apóstrofo recto: "Pídeme" y "pideme" valen igual, "I’ve" e "I've" también.</summary>
    static string Normalizar(string texto)
    {
        var sb = new StringBuilder(texto.Length);
        foreach (var c in texto.Replace('\u2019', '\'').Replace('\u2018', '\'').ToLowerInvariant().Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    static bool EsErrorCorregible(string json) =>
        json.StartsWith("""{"error":"fuera_de_tu_historial""", StringComparison.Ordinal) ||
        json.StartsWith("""{"error":"rutina_invalida""", StringComparison.Ordinal) ||
        json.StartsWith("""{"error":"excede_tiempo""", StringComparison.Ordinal);

    /// <summary>accion de los argumentos de recordar; null si no es JSON, no es objeto o no es texto.</summary>
    static string? AccionDe(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("accion", out var a) &&
                a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    static bool EsError(string json) => json.StartsWith("""{"error":""", StringComparison.Ordinal);

    /// <summary>Mejor pesoKg por ejercicio en un resultado de progreso o ver_entrenos_recientes.</summary>
    static void AnotarPesos(string json, Dictionary<string, decimal> vistos)
    {
        if (EsError(json)) return;
        using var doc = JsonDocument.Parse(json);
        var raiz = doc.RootElement;

        void Anotar(string? id, JsonElement mejorSerie)
        {
            if (id is null || mejorSerie.ValueKind != JsonValueKind.Object ||
                !mejorSerie.TryGetProperty("pesoKg", out var p) || !p.TryGetDecimal(out var peso)) return;
            ConversacionCoach.Anotar(vistos, id, peso);
        }

        // progreso: {ejercicioId, porSemana:[{mejorSerie}]}
        if (raiz.TryGetProperty("porSemana", out var semanas) && semanas.ValueKind == JsonValueKind.Array &&
            raiz.TryGetProperty("ejercicioId", out var ej) && ej.ValueKind == JsonValueKind.String)
            foreach (var s in semanas.EnumerateArray())
                if (s.TryGetProperty("mejorSerie", out var m)) Anotar(ej.GetString(), m);

        // ver_entrenos_recientes: {dias:[{ejercicios:[{ejercicioId, mejorSerie}]}]}
        if (raiz.TryGetProperty("dias", out var dias) && dias.ValueKind == JsonValueKind.Array)
            foreach (var d in dias.EnumerateArray())
                if (d.TryGetProperty("ejercicios", out var ejs) && ejs.ValueKind == JsonValueKind.Array)
                    foreach (var e in ejs.EnumerateArray())
                        if (e.TryGetProperty("ejercicioId", out var id) && id.ValueKind == JsonValueKind.String &&
                            e.TryGetProperty("mejorSerie", out var m))
                            Anotar(id.GetString(), m);
    }

    static void Anotar(Dictionary<string, decimal> vistos, string id, decimal peso)
    {
        if (!vistos.TryGetValue(id, out var max) || peso > max) vistos[id] = peso;
    }

    /// <summary>
    /// El coach no inventa números: un pesoKg propuesto por encima de lo que vio en las herramientas (o de un
    /// ejercicio que no consultó) pasa a null, y la PWA lo muestra sin peso.
    /// </summary>
    static RutinaPropuesta SinPesosInventados(RutinaPropuesta propuesta, Dictionary<string, decimal> vistos) =>
        propuesta with
        {
            Rutina = propuesta.Rutina with
            {
                Ejercicios = [.. propuesta.Rutina.Ejercicios.Select(e =>
                    e.PesoKg is decimal p && (!vistos.TryGetValue(e.Id, out var max) || p > max) ? e with { PesoKg = null } : e)],
            },
        };
}
