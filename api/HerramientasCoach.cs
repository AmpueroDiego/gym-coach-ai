using System.Net;
using System.Text.Json;
using GymCoach.Api.Functions;
using Microsoft.Azure.Cosmos;

namespace GymCoach.Api;

public sealed record DefinicionHerramienta(string Nombre, string Descripcion, string ParametrosJson);
public delegate Task<List<Serie>> ConsultaSeries(QueryDefinition consulta, string userId);
// Entrenamiento y PlanHoy: null si no se pudieron leer (el turno sigue). Opcionales: los new ContextoCoach(...) existentes siguen valiendo.
// ConversacionId: conversación del agente de hoy (doc del userId del token); null = bucle sin memoria.
public sealed record ContextoCoach(string UserId, DateOnly Hoy, DateTimeOffset Ahora, PerfilCoach Perfil,
    PerfilEntrenamiento? Entrenamiento = null, PlanDiaDoc? PlanHoy = null, string Idioma = "es", string? ConversacionId = null);
public sealed record RutinaPropuesta(Rutina Rutina, int MinutosEstimados);
/// <summary>Programa de 2 a 4 rutinas que propone el coach; cada rutina con su id del servidor, aún sin guardar.</summary>
public sealed record ProgramaPropuesto(string Nombre, IReadOnlyList<RutinaPropuesta> Rutinas);
public sealed record ResultadoHerramienta(string Json, RutinaPropuesta? Rutina = null, PerfilCoach? PerfilNuevo = null,
    ProgramaPropuesto? Programa = null);

/// <summary>
/// Consultas ya hechas en un turno. Recientes y volumen piden siempre su ventana máxima, así el modelo puede
/// pedir 4 y luego 8 semanas sin gastar RU dos veces; progreso pide solo lo suyo y se reusa si la ventana guardada
/// la cubre. Una por turno: no se comparte entre usuarios ni días.
/// </summary>
public sealed class CacheTurno
{
    internal readonly Dictionary<string, (List<Serie> Series, bool Truncado, DateOnly Desde)> Consultas = new(StringComparer.Ordinal);
}

/// <summary>
/// Herramientas que el modelo puede llamar. Ninguna recibe userId: sale siempre de ctx (el token), así un
/// prompt inyectado no puede pedir datos de otro. Argumentos estrictos ("8" no es 8); extras se ignoran.
/// Un argumento malo vuelve al modelo como {"error":...} sin tocar Cosmos. Cosmos saturado (429/503/408) vuelve como
/// {"error":"no_disponible"} para que el turno siga; cualquier otra excepción se propaga.
/// </summary>
public sealed class HerramientasCoach
{
    // Topes de cada consulta: se pide uno más para saber si se truncó.
    const int TopeRecientes = 1000;
    // Progreso: ~15 series/semana de un ejercicio × 26 semanas ≈ 400, más calentamientos importados (Hevy).
    const int TopeProgreso = 1000;
    const int TopeVolumen = 2000;
    // Ventanas máximas: se consulta siempre esto y se recorta en memoria a lo pedido.
    const int MaxDias = 14;
    const int MaxSemanasProgreso = 26;
    const int MaxSemanasVolumen = 8;
    // Alternativas que devuelve fuera_de_tu_historial: suficientes para rearmar la rutina sin inflar el turno.
    const int MaxPermitidos = 15;

    // Sin ignorar nulls (a diferencia de Http.Json): el contrato es "objeto | null", el modelo debe ver el null.
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly Dictionary<string, Ejercicio> PorId = Catalogo.Ejercicios.ToDictionary(e => e.Id, StringComparer.Ordinal);

    readonly ConsultaSeries _consultar;
    readonly PerfilesCoach _perfiles;

    public HerramientasCoach(Container series, PerfilesCoach perfiles)
        : this((consulta, userId) => Series.ConsultarAsync(series, consulta, userId), perfiles) { }

    internal HerramientasCoach(ConsultaSeries consultar, PerfilesCoach perfiles)
    {
        _consultar = consultar;
        _perfiles = perfiles;
    }

    public static IReadOnlyList<DefinicionHerramienta> Definiciones { get; } = CrearDefiniciones();
    // El plan del día es siempre una rutina: sin proponer_programa. Después de Definiciones (orden de inicialización estática).
    public static IReadOnlyList<DefinicionHerramienta> DefinicionesPlan { get; } = [.. Definiciones.Where(d => d.Nombre != "proponer_programa")];

    public async Task<ResultadoHerramienta> EjecutarAsync(string nombre, string argumentosJson, ContextoCoach ctx, CancellationToken ct = default,
        CacheTurno? cache = null)
    {
        ct.ThrowIfCancellationRequested();
        try { return await EjecutarInternoAsync(nombre, argumentosJson, ctx, cache ?? new CacheTurno(), ct); }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.RequestTimeout)
        {
            // El modelo puede responder con lo que ya tiene en vez de perder el turno entero.
            return Error(new { error = "no_disponible" });
        }
    }

    async Task<ResultadoHerramienta> EjecutarInternoAsync(string nombre, string argumentosJson, ContextoCoach ctx, CacheTurno cache,
        CancellationToken ct)
    {
        // recordar valida sus propios argumentos contra el perfil actual (PerfilesCoach.Aplicar).
        if (nombre == "recordar")
        {
            var (json, nuevo) = await _perfiles.RecordarAsync(ctx.UserId, argumentosJson, ct);
            return new(json, PerfilNuevo: nuevo);
        }
        // Sin parámetros: los argumentos se ignoran (el userId sale de ctx).
        if (nombre == "mis_ejercicios") return MisEjercicios(ctx);
        if (nombre is not ("ver_entrenos_recientes" or "progreso" or "volumen_por_grupo" or "proponer_rutina" or "proponer_programa"))
            return Error(new { error = "herramienta_desconocida" });

        // Sin argumentos el modelo a veces manda "" en vez de "{}".
        if (string.IsNullOrWhiteSpace(argumentosJson)) argumentosJson = "{}";
        JsonDocument doc;
        try { doc = JsonDocument.Parse(argumentosJson, Validacion.OpcionesJson); }
        catch (JsonException) { return Invalidos("Los argumentos no son JSON válido."); }

        using (doc)
        {
            var args = doc.RootElement;
            if (args.ValueKind != JsonValueKind.Object) return Invalidos("Los argumentos deben ser un objeto.");
            return nombre switch
            {
                "ver_entrenos_recientes" => await RecientesAsync(args, ctx, cache),
                "progreso" => await ProgresoAsync(args, ctx, cache),
                "volumen_por_grupo" => await VolumenAsync(args, ctx, cache),
                "proponer_programa" => ProponerPrograma(args, ctx, cache),
                "proponer_rutina" => ProponerRutina(args, argumentosJson, ctx, cache),
                _ => Error(new { error = "herramienta_desconocida" }),
            };
        }
    }

    async Task<ResultadoHerramienta> RecientesAsync(JsonElement args, ContextoCoach ctx, CacheTurno cache)
    {
        if (Entero(args, "dias", 1, MaxDias, 7, out var dias) is string e) return Invalidos(e);
        var (series, truncado) = await ConsultarAsync(ctx, cache, "recientes", TopeRecientes, ctx.Hoy.AddDays(-(MaxDias - 1)), null);
        return Ok(CalculosCoach.Recientes(series, ctx.Hoy, dias, TruncadoDesde(series, truncado, ctx.Hoy.AddDays(-(dias - 1)))));
    }

    async Task<ResultadoHerramienta> ProgresoAsync(JsonElement args, ContextoCoach ctx, CacheTurno cache)
    {
        string? ejercicioId;
        // GetString lanza con un surrogate suelto: es entrada mala, no un 500. Solo aquí: una falla de Cosmos sí se propaga.
        try { ejercicioId = args.TryGetProperty("ejercicioId", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null; }
        catch (InvalidOperationException) { return Invalidos("Los argumentos no son JSON válido."); }
        if (ejercicioId is null || !Catalogo.Existe(ejercicioId)) return Invalidos("ejercicioId: no existe en el catálogo.");
        if (Entero(args, "semanas", 2, MaxSemanasProgreso, 8, out var semanas) is string e) return Invalidos(e);
        // Solo la ventana pedida: hasta 26 semanas de un ejercicio es mucho RU para pedirlo siempre.
        var (series, truncado) = await ConsultarAsync(ctx, cache, "progreso", TopeProgreso,
            CalculosCoach.InicioSemanas(ctx.Hoy, semanas), ejercicioId);
        return Ok(CalculosCoach.Progreso(series, ejercicioId, ctx.Hoy, semanas,
            TruncadoDesde(series, truncado, CalculosCoach.InicioSemanas(ctx.Hoy, semanas))));
    }

    async Task<ResultadoHerramienta> VolumenAsync(JsonElement args, ContextoCoach ctx, CacheTurno cache)
    {
        if (Entero(args, "semanas", 2, MaxSemanasVolumen, 4, out var semanas) is string e) return Invalidos(e);
        var (series, truncado) = await ConsultarAsync(ctx, cache, "volumen", TopeVolumen,
            CalculosCoach.InicioSemanas(ctx.Hoy, MaxSemanasVolumen), null);
        return Ok(CalculosCoach.Volumen(series, ctx.Hoy, semanas,
            TruncadoDesde(series, truncado, CalculosCoach.InicioSemanas(ctx.Hoy, semanas))));
    }

    /// <summary>
    /// Truncar quita las más viejas: a la ventana pedida le falta algo solo si la serie más vieja que quedó cae
    /// dentro de ella (si cae antes, la ventana está completa).
    /// </summary>
    static bool TruncadoDesde(List<Serie> series, bool truncado, DateOnly desde) =>
        truncado && (series.Count == 0 || string.CompareOrdinal(series[0].Fecha, desde.ToString("yyyy-MM-dd")) >= 0);

    /// <summary>
    /// Todos los ejercicios del historial (perfil de entrenamiento ya calculado): 0 RU. Equipo preferido: el del perfil
    /// del coach y, si no hay, el inferido de las series.
    /// </summary>
    static ResultadoHerramienta MisEjercicios(ContextoCoach ctx)
    {
        if (ctx.Entrenamiento is not { } e) return Error(new { error = "no_disponible" });
        var (equipo, origen) = EquipoPreferido(ctx.Perfil, e);
        return Ok(new { calculadoEl = e.CalculadoEl, truncado = e.Truncado, equipoPreferido = equipo, equipoOrigen = origen, ejercicios = e.Ejercicios });
    }

    static (string? Equipo, string? Origen) EquipoPreferido(PerfilCoach perfil, PerfilEntrenamiento e) =>
        perfil.Equipo is not null ? (perfil.Equipo, "perfil")
        : e.Equipo.Inferido is not null ? (e.Equipo.Inferido, "historial")
        : (null, null);

    /// <summary>
    /// Valida y estima, no guarda nada: la PWA decide si la empieza o la guarda. Errores en este orden:
    /// parametros_invalidos, rutina_invalida, no_disponible / fuera_de_tu_historial, excede_tiempo.
    /// </summary>
    static ResultadoHerramienta ProponerRutina(JsonElement args, string argumentosJson, ContextoCoach ctx, CacheTurno cache)
    {
        if (MinutosObjetivo(args, out var minutosObjetivo) is string e) return Invalidos(e);
        var (propuesta, error) = ValidarRutina(args, argumentosJson, ctx, cache, minutosObjetivo);
        if (propuesta is null) return Error(error!);
        return new(JsonSerializer.Serialize(new { ok = true, minutosEstimados = propuesta.MinutosEstimados }, Json), propuesta);
    }

    // Orden de los errores de proponer_programa: gana el de menor rango y, a igual rango, la rutina de menor índice.
    static readonly string[] OrdenErrores = ["parametros_invalidos", "rutina_invalida", "no_disponible", "fuera_de_tu_historial", "excede_tiempo"];

    /// <summary>
    /// Programa de 2 a 4 rutinas: cada una se valida como proponer_rutina (el tope de 1 nuevo es por rutina) y nada se
    /// guarda. Un error de una rutina trae su índice (desde 0) al final, con error como primera propiedad.
    /// </summary>
    static ResultadoHerramienta ProponerPrograma(JsonElement args, ContextoCoach ctx, CacheTurno cache)
    {
        if (!args.TryGetProperty("nombre", out var n)) return Invalidos("nombre: es obligatorio.");
        if (Validacion.ErrorNombre(n, out var nombre) is string en) return Invalidos($"nombre: {en}");
        if (!args.TryGetProperty("rutinas", out var rs) || rs.ValueKind != JsonValueKind.Array || rs.GetArrayLength() is < 2 or > 4)
            return Invalidos("rutinas: debe ser un array de 2 a 4 rutinas.");
        if (MinutosObjetivo(args, out var minutosObjetivo) is string em) return Invalidos(em);
        // Antes que cualquier rutina_invalida: un nuevo mal tipado en la rutina 1 gana a un error de validación en la 0.
        var i = 0;
        foreach (var item in rs.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return Invalidos($"rutinas[{i}]: debe ser un objeto.");
            if (LeerNuevos(item, $"rutinas[{i}].", out _) is string eu) return Invalidos(eu);
            i++;
        }

        // Todo en memoria: se validan todas y gana el primer error según OrdenErrores.
        var resultados = rs.EnumerateArray().Select(item => ValidarRutina(item, item.GetRawText(), ctx, cache, minutosObjetivo)).ToList();
        var fallo = resultados.Select((r, idx) => (r.Error, Indice: idx)).Where(x => x.Error is not null)
            .Select(x => (Nodo: JsonSerializer.SerializeToNode(x.Error, Json)!.AsObject(), x.Indice))
            .OrderBy(x => Array.IndexOf(OrdenErrores, (string?)x.Nodo["error"])).ThenBy(x => x.Indice).FirstOrDefault();
        if (fallo.Nodo is { } nodo)
        {
            // no_disponible no es de una rutina: va sin índice.
            if ((string?)nodo["error"] != "no_disponible") nodo["rutina"] = fallo.Indice;
            return Error(nodo);
        }

        var props = resultados.Select(r => r.Propuesta!).ToList();
        return new(JsonSerializer.Serialize(new { ok = true, rutinas = props.Select(p => new { p.Rutina.Nombre, p.MinutosEstimados }) }, Json),
            Programa: new ProgramaPropuesto(nombre, props));
    }

    /// <summary>minutosObjetivo opcional (null = ausente), entero en [10, 180]. Devuelve el detalle del error o null.</summary>
    static string? MinutosObjetivo(JsonElement args, out int? minutosObjetivo)
    {
        minutosObjetivo = null;
        if (!args.TryGetProperty("minutosObjetivo", out var m) || m.ValueKind == JsonValueKind.Null) return null;
        if (Entero(args, "minutosObjetivo", 10, 180, 0, out var mo) is string e) return e;
        minutosObjetivo = mo;
        return null;
    }

    /// <summary>
    /// Índices de los ejercicios marcados nuevo: true. Se lee aparte porque Validacion.Rutina ignora las claves extra;
    /// por índice, sin leer el id (lo valida Rutina). Estricto: "true" no es true.
    /// </summary>
    static string? LeerNuevos(JsonElement obj, string prefijo, out HashSet<int> nuevos)
    {
        nuevos = [];
        if (!obj.TryGetProperty("ejercicios", out var ejs) || ejs.ValueKind != JsonValueKind.Array) return null;
        var i = 0;
        foreach (var item in ejs.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("nuevo", out var n) && n.ValueKind != JsonValueKind.Null)
            {
                if (n.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return $"{prefijo}ejercicios[{i}].nuevo: debe ser booleano.";
                if (n.GetBoolean()) nuevos.Add(i);
            }
            i++;
        }
        return null;
    }

    /// <summary>
    /// Lo común de proponer_rutina y de cada rutina de proponer_programa. Errores en este orden: parametros_invalidos (nuevo),
    /// rutina_invalida, no_disponible / fuera_de_tu_historial, excede_tiempo. Error es el objeto sin serializar.
    /// </summary>
    static (RutinaPropuesta? Propuesta, object? Error) ValidarRutina(JsonElement obj, string json, ContextoCoach ctx, CacheTurno cache,
        int? minutosObjetivo)
    {
        if (LeerNuevos(obj, "", out var nuevos) is string en) return (null, new { error = "parametros_invalidos", detalle = en });

        // Misma validación que PUT /rutinas (ignora minutosObjetivo y nuevo).
        var (rutina, errores) = Validacion.Rutina(Guid.NewGuid().ToString("D"), json, ctx.Ahora);
        if (rutina is null) return (null, new { error = "rutina_invalida", detalles = errores });

        // Sin perfil de entrenamiento no se puede saber qué conoce: falla cerrado.
        if (ctx.Entrenamiento is not { } entrenamiento) return (null, new { error = "no_disponible" });
        if (FueraDeTuHistorial(rutina, nuevos, ctx, entrenamiento, cache) is { } fuera) return (null, fuera);

        var minutos = CalculosCoach.MinutosEstimados(rutina.Ejercicios);
        var limite = minutosObjetivo ?? ctx.Perfil.MinutosDisponibles;
        if (limite is int l && minutos > l)
            return (null, new { error = "excede_tiempo", minutosEstimados = minutos, minutosDisponibles = l });
        return (new RutinaPropuesta(rutina, minutos), null);
    }

    /// <summary>
    /// Guardia de proponer_rutina: con historial suficiente, los ejercicios salen de lo que el usuario ya hace (perfil
    /// de entrenamiento o ver_entrenos_recientes de este turno). Como mucho 1 nuevo, marcado nuevo: true y, si el equipo
    /// preferido es maquina o libre, de ese equipo. nuevo sobre un ejercicio conocido no cuenta. null = pasa.
    /// </summary>
    static object? FueraDeTuHistorial(Rutina rutina, HashSet<int> nuevos, ContextoCoach ctx, PerfilEntrenamiento e, CacheTurno cache)
    {
        if (!e.HistorialSuficiente) return null;

        var conocidos = e.Ejercicios.Select(x => x.EjercicioId).ToHashSet(StringComparer.Ordinal);
        if (cache.Consultas.TryGetValue(Clave("recientes", ctx, null), out var recientes))
            conocidos.UnionWith(recientes.Series.Select(s => s.EjercicioId));

        // Distinct: un id repetido no cuenta dos veces (Validacion.Rutina ya los rechaza; esto no depende de eso).
        var desconocidos = rutina.Ejercicios.Select((x, i) => (x.Id, Nuevo: nuevos.Contains(i))).Where(x => !conocidos.Contains(x.Id))
            .DistinctBy(x => x.Id).ToList();
        // Con qué reemplazar: los del historial con más sesiones (Ejercicios ya viene ordenado) que no estén en la rutina.
        var enRutina = rutina.Ejercicios.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        var permitidos = e.Ejercicios.Select(x => x.EjercicioId).Where(id => !enRutina.Contains(id)).Take(MaxPermitidos).ToList();

        var sinMarcar = desconocidos.Where(x => !x.Nuevo).Select(x => x.Id).ToList();
        if (sinMarcar.Count > 0)
            return new { error = "fuera_de_tu_historial", ejercicios = sinMarcar, permitidos,
                detalle = "No están en su historial: cámbialos por ejercicios de permitidos; como mucho 1 nuevo, marcado nuevo: true." };
        if (desconocidos.Count > 1)
            return new { error = "fuera_de_tu_historial", ejercicios = desconocidos.Select(x => x.Id).ToList(), permitidos,
                detalle = "Como mucho 1 ejercicio nuevo por rutina: deja uno de ejercicios y cambia los demás por ejercicios de permitidos." };

        var (equipo, _) = EquipoPreferido(ctx.Perfil, e);
        // mixto o null: no se filtra por equipo.
        if (equipo is not (Catalogo.Maquina or Catalogo.Libre)) return null;
        var otroEquipo = desconocidos.Where(x => PorId[x.Id].Equipo != equipo).Select(x => x.Id).ToList();
        return otroEquipo.Count == 0 ? null
            : new { error = "fuera_de_tu_historial", ejercicios = otroEquipo, permitidos, equipo,
                detalle = $"El ejercicio nuevo tiene que ser de equipo {equipo}, el que usa el usuario." };
    }

    static string Clave(string herramienta, ContextoCoach ctx, string? ejercicioId) =>
        $"{herramienta}|{ctx.UserId}|{ctx.Hoy:yyyy-MM-dd}|{ejercicioId}";

    /// <summary>
    /// Series del usuario del contexto con fecha en [desde, hoy], en orden cronológico; TOP tope+1 para detectar el
    /// truncado. Se pide por ts descendente para que, si hay más del tope, se pierdan las más viejas y no las de hoy.
    /// La misma consulta en el mismo turno sale de la caché si su ventana cubre la pedida; si no, se consulta y se reemplaza.
    /// </summary>
    async Task<(List<Serie> Series, bool Truncado)> ConsultarAsync(ContextoCoach ctx, CacheTurno cache, string herramienta, int tope,
        DateOnly desde, string? ejercicioId)
    {
        var clave = Clave(herramienta, ctx, ejercicioId);
        if (cache.Consultas.TryGetValue(clave, out var guardada) && guardada.Desde <= desde) return (guardada.Series, guardada.Truncado);

        var consulta = new QueryDefinition(
                "SELECT TOP @tope c.id, c.ejercicioId, c.pesoKg, c.reps, c.fecha, c.ts, c.rutina, c.tipoSerie FROM c " +
                "WHERE c.userId = @userId AND c.tipo = 'serie' " + (ejercicioId is null ? "" : "AND c.ejercicioId = @ejercicioId ") +
                "AND c.fecha >= @desde AND c.fecha <= @hoy ORDER BY c.ts DESC")
            .WithParameter("@tope", tope + 1)
            .WithParameter("@userId", ctx.UserId)
            .WithParameter("@desde", desde.ToString("yyyy-MM-dd"))
            .WithParameter("@hoy", ctx.Hoy.ToString("yyyy-MM-dd"));
        if (ejercicioId is not null) consulta = consulta.WithParameter("@ejercicioId", ejercicioId);

        var series = await _consultar(consulta, ctx.UserId);
        var truncado = series.Count > tope;
        if (truncado) series.RemoveRange(tope, series.Count - tope);
        series.Reverse();
        cache.Consultas[clave] = (series, truncado, desde);
        return (series, truncado);
    }

    /// <summary>Entero estricto en [min, max]; ausente o null = valor por defecto. Devuelve el detalle del error o null.</summary>
    static string? Entero(JsonElement args, string campo, int min, int max, int defecto, out int valor)
    {
        valor = defecto;
        if (!args.TryGetProperty(campo, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out valor)) return $"{campo}: debe ser un entero.";
        return valor < min || valor > max ? $"{campo}: debe estar entre {min} y {max}." : null;
    }

    static ResultadoHerramienta Ok(object resultado) => new(JsonSerializer.Serialize(resultado, Json));
    static ResultadoHerramienta Error(object error) => new(JsonSerializer.Serialize(error, Json));
    static ResultadoHerramienta Invalidos(string detalle) => Error(new { error = "parametros_invalidos", detalle });

    static List<DefinicionHerramienta> CrearDefiniciones()
    {
        var ids = Catalogo.Ejercicios.Select(e => e.Id).ToArray();
        static object EnteroSchema(int min, int max, string descripcion) =>
            new { type = "integer", minimum = min, maximum = max, description = descripcion };
        static string Schema(object propiedades, string[]? requeridos = null) =>
            JsonSerializer.Serialize(new { type = "object", properties = propiedades, required = requeridos ?? [] }, Json);

        // Objeto rutina (nombre + ejercicios): el mismo en proponer_rutina y en cada rutina de proponer_programa.
        var nombreRutina = new { type = "string", maxLength = 40 };
        var ejercicios = new
        {
            type = "array",
            minItems = 1,
            maxItems = 15,
            items = new
            {
                type = "object",
                properties = new
                {
                    id = new { type = "string", @enum = ids },
                    series = EnteroSchema(1, 10, "Series."),
                    reps = EnteroSchema(1, 100, "Reps por serie."),
                    pesoKg = new { type = new[] { "number", "null" }, minimum = 0, maximum = 500,
                        description = "Solo un peso que salió de progreso, ver_entrenos_recientes, mis_ejercicios o del perfil de entrenamiento; si no, null." },
                    descansoS = EnteroSchema(0, 600, "Descanso entre series en segundos."),
                    nuevo = new { type = "boolean",
                        description = "true si el ejercicio no está en su historial. Como mucho 1 por rutina y del mismo equipo que usa." },
                },
                required = new[] { "id", "series", "reps", "pesoKg", "descansoS" },
            },
        };

        return
        [
            new("ver_entrenos_recientes",
                "Series registradas por el usuario en los últimos días, agrupadas por día y ejercicio (sin contar calentamientos en volumen ni mejor serie).",
                Schema(new { dias = EnteroSchema(1, 14, "Días hacia atrás contando hoy. Por defecto 7.") })),
            new("progreso",
                "Progreso semanal de un ejercicio (hasta 26 semanas): mejor serie, e1RM, volumen y el cambio entre la primera y la última semana con datos. " +
                "En ejercicios de carga corporal (cargaCorporal: true) pesoKg es el lastre: el progreso se mide en mejorSerie.reps.",
                Schema(new
                {
                    ejercicioId = new { type = "string", @enum = ids, description = "Id del ejercicio del catálogo." },
                    semanas = EnteroSchema(2, MaxSemanasProgreso, "Semanas hacia atrás (bloques de 7 días que terminan hoy). Por defecto 8."),
                }, ["ejercicioId"])),
            new("volumen_por_grupo",
                "Series, reps y volumen por grupo muscular y semana, con el cambio de la última semana frente al promedio de las anteriores.",
                Schema(new { semanas = EnteroSchema(2, 8, "Semanas hacia atrás (bloques de 7 días que terminan hoy). Por defecto 4.") })),
            new("mis_ejercicios",
                "Todos los ejercicios que el usuario hizo en su historial, con sesiones, series, fechas, última serie, mejor serie y e1RM máximo, " +
                "más su equipo preferido (maquina, libre o mixto). Opcional: el perfil de entrenamiento ya trae los principales. " +
                "Si la usas para una rutina, llama proponer_rutina en el mismo turno.",
                Schema(new { })),
            new("recordar",
                "Guarda en el perfil del usuario una nota, sus minutos disponibles por sesión, su objetivo, su altura, su peso corporal, su equipo preferido, " +
                "su experiencia entrenando o los días por semana que puede entrenar.",
                Schema(new
                {
                    accion = new { type = "string", @enum = new[] { "agregar_nota", "quitar_nota", "fijar_minutos", "fijar_objetivo",
                        "fijar_altura", "fijar_peso", "fijar_equipo", "fijar_experiencia", "fijar_dias" } },
                    nota = new { type = "string", maxLength = 200, description = "Para agregar_nota." },
                    indice = EnteroSchema(1, 10, "Para quitar_nota: número de la nota tal como aparece en el perfil."),
                    minutos = EnteroSchema(10, 180, "Para fijar_minutos."),
                    objetivo = new { type = "string", maxLength = 200, description = "Para fijar_objetivo; vacío lo borra." },
                    alturaCm = new { type = new[] { "integer", "null" }, minimum = Validacion.MinAlturaCm, maximum = Validacion.MaxAlturaCm,
                        description = "Para fijar_altura: altura del usuario en cm; null la borra." },
                    pesoCorporalKg = new { type = new[] { "number", "null" }, minimum = Validacion.MinPesoCorporalKg,
                        maximum = Validacion.MaxPesoCorporalKg,
                        description = "Para fijar_peso: peso corporal del usuario en kg (máximo 1 decimal), no el peso de un ejercicio; null lo borra." },
                    equipo = new { type = new[] { "string", "null" }, @enum = new[] { "maquina", "libre", "mixto", null },
                        description = "Para fijar_equipo: equipo con el que prefiere entrenar; null lo borra (se deduce del historial)." },
                    experiencia = new { type = new[] { "string", "null" }, @enum = new[] { "nunca", "menos6m", "mas6m", null },
                        description = "Para fijar_experiencia: nunca (no ha entrenado), menos6m (menos de 6 meses) o mas6m; null la borra." },
                    diasPorSemana = new { type = new[] { "integer", "null" }, minimum = 1, maximum = 7,
                        description = "Para fijar_dias: días por semana que puede entrenar; null lo borra." },
                }, ["accion"])),
            new("proponer_rutina",
                "Propone una rutina para hoy. Valida los ejercicios y estima su duración; no la guarda. " +
                "Llámala directamente cuando pidan una rutina, un entreno o ejercicios: no requiere confirmación del usuario.",
                Schema(new
                {
                    nombre = nombreRutina,
                    ejercicios,
                    minutosObjetivo = EnteroSchema(10, 180, "Minutos que pidió el usuario; si no, se usan los del perfil."),
                }, ["nombre", "ejercicios"])),
            new("proponer_programa",
                "Propone un programa de 2 a 4 rutinas, una por día de entreno de la semana, cuando piden un programa, un plan de varios días o de la semana. " +
                "Valida cada rutina igual que proponer_rutina y estima su duración; no guarda nada. Llámala directamente: no requiere confirmación del usuario.",
                Schema(new
                {
                    nombre = new { type = "string", maxLength = 40 },
                    rutinas = new
                    {
                        type = "array",
                        minItems = 2,
                        maxItems = 4,
                        items = new { type = "object", properties = new { nombre = nombreRutina, ejercicios }, required = new[] { "nombre", "ejercicios" } },
                    },
                    minutosObjetivo = EnteroSchema(10, 180, "Minutos que pidió el usuario para cada rutina; si no, se usan los del perfil."),
                }, ["nombre", "rutinas"])),
        ];
    }
}
