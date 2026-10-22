using System.Collections.Concurrent;

namespace GymCoach.Api;

/// <summary>
/// Ventana deslizante en memoria por clave (userId). Es por instancia de Functions:
/// frena abuso de una cuenta, no es un límite global exacto.
/// </summary>
public sealed class Limitador(int maximo, TimeSpan ventana, TimeProvider reloj)
{
    sealed class Ventana
    {
        public readonly Queue<DateTimeOffset> Marcas = new();
        public DateTimeOffset Ultima;
        public bool Retirada;
    }

    readonly ConcurrentDictionary<string, Ventana> _porClave = new();
    readonly Lock _limpieza = new();
    DateTimeOffset _ultimaLimpieza = DateTimeOffset.MinValue;

    /// <summary>Claves vivas (para pruebas: confirma que la limpieza funciona).</summary>
    public int Claves => _porClave.Count;

    public bool Permitir(string clave, out TimeSpan reintentarEn)
    {
        var ahora = reloj.GetUtcNow();
        Limpiar(ahora);

        while (true)
        {
            var v = _porClave.GetOrAdd(clave, _ => new Ventana());
            lock (v)
            {
                // La limpieza pudo retirarla entre GetOrAdd y el lock: tomar la nueva.
                if (v.Retirada) continue;

                while (v.Marcas.Count > 0 && ahora - v.Marcas.Peek() >= ventana) v.Marcas.Dequeue();
                if (v.Marcas.Count >= maximo)
                {
                    reintentarEn = v.Marcas.Peek() + ventana - ahora;
                    return false;
                }
                v.Marcas.Enqueue(ahora);
                v.Ultima = ahora;
                reintentarEn = TimeSpan.Zero;
                return true;
            }
        }
    }

    /// <summary>Devuelve la marca más reciente de la clave (la petición no llegó a gastar lo que el cupo protege).</summary>
    public void Liberar(string clave)
    {
        if (!_porClave.TryGetValue(clave, out var v)) return;
        lock (v)
        {
            if (v.Retirada || v.Marcas.Count == 0) return;
            // Queue no saca del final: se rearma sin la última (son pocas marcas).
            var marcas = v.Marcas.ToArray();
            v.Marcas.Clear();
            foreach (var m in marcas[..^1]) v.Marcas.Enqueue(m);
        }
    }

    // Como mucho una vez por ventana: retira claves sin actividad reciente para que el diccionario no crezca.
    void Limpiar(DateTimeOffset ahora)
    {
        lock (_limpieza)
        {
            if (ahora - _ultimaLimpieza < ventana) return;
            _ultimaLimpieza = ahora;
        }

        foreach (var (clave, v) in _porClave)
        {
            lock (v)
            {
                if (ahora - v.Ultima < ventana) continue;
                v.Retirada = true;
                _porClave.TryRemove(new KeyValuePair<string, Ventana>(clave, v));
            }
        }
    }
}

/// <summary>Límites por usuario de la API (y el candado de escrituras de series, que también es por usuario).</summary>
public sealed class Limites(TimeProvider reloj)
{
    public const int LecturasPorMinuto = 300;
    public const int EscriturasPorMinuto = 120;
    // GET /api/series puede costar ~45 RU con el rango lleno; la PWA solo lo pide al montar y al reintentar.
    public const int RangosPorMinuto = 10;
    // Cada recálculo de récord es una consulta del ejercicio (hasta ~81 RU con el tope de filas).
    public const int RecalculosPorMinuto = 5;
    // Tope diario: con 120/min sostenido una cuenta podría escribir ~170 mil series al día. 1500 sobra para entrenar.
    // Guarda hasta 1500 marcas por usuario activo (~24 KB) y las claves quietas viven hasta 48 h.
    public const int EscriturasPorDia = 1500;
    public const int RecordsPorMinuto = 60;
    // GET /rutinas trae hasta 20 rutinas completas: la PWA lo pide al montar, no en cada serie.
    public const int RutinasPorMinuto = 30;
    // Cada mensaje al coach son varias llamadas al modelo y 10-60 RU: cupo bajo por minuto y por día.
    public const int CoachPorMinuto = 5;
    public const int CoachPorDia = 20;
    // Importar historial (lotes de hasta 10: un lote lleno son ~120 RU de golpe sobre 400 compartidos): cupo aparte de las escrituras, así una importación grande
    // no deja sin cupo registrar el entreno del día. 300 lotes = 3000 series al día.
    public const int ImportLotesPorMinuto = 25;
    public const int ImportLotesPorDia = 300;
    static readonly TimeSpan EsperaCandado = TimeSpan.FromSeconds(5);

    public Limitador Lecturas { get; } = new(LecturasPorMinuto, TimeSpan.FromMinutes(1), reloj);
    public Limitador Escrituras { get; } = new(EscriturasPorMinuto, TimeSpan.FromMinutes(1), reloj);
    public Limitador EscriturasDiarias { get; } = new(EscriturasPorDia, TimeSpan.FromDays(1), reloj);
    public Limitador Rangos { get; } = new(RangosPorMinuto, TimeSpan.FromMinutes(1), reloj);
    public Limitador Recalculos { get; } = new(RecalculosPorMinuto, TimeSpan.FromMinutes(1), reloj);
    // GET /records puede recalcular récords marcados: cupo propio y más bajo que el de lecturas.
    public Limitador Records { get; } = new(RecordsPorMinuto, TimeSpan.FromMinutes(1), reloj);
    public Limitador Rutinas { get; } = new(RutinasPorMinuto, TimeSpan.FromMinutes(1), reloj);
    public Limitador Coach { get; } = new(CoachPorMinuto, TimeSpan.FromMinutes(1), reloj);
    public Limitador CoachDiario { get; } = new(CoachPorDia, TimeSpan.FromDays(1), reloj);
    public Limitador Importaciones { get; } = new(ImportLotesPorMinuto, TimeSpan.FromMinutes(1), reloj);
    public Limitador ImportacionesDiarias { get; } = new(ImportLotesPorDia, TimeSpan.FromDays(1), reloj);

    /// <summary>Cupo por minuto y diario de escrituras; false con la espera del que se negó.</summary>
    public bool PermitirEscritura(string userId, out TimeSpan reintentarEn) =>
        Escrituras.Permitir(userId, out reintentarEn) && EscriturasDiarias.Permitir(userId, out reintentarEn);

    /// <summary>Cupo por minuto y diario de lotes importados; false con la espera del que se negó.</summary>
    public bool PermitirImportacion(string userId, out TimeSpan reintentarEn) =>
        Importaciones.Permitir(userId, out reintentarEn) && ImportacionesDiarias.Permitir(userId, out reintentarEn);

    // Candados por usuario, repartidos en un arreglo fijo (no crece): serializan las escrituras de series de un
    // usuario en esta instancia, así los PUT en paralelo del cliente no pelean por el doc del récord.
    readonly SemaphoreSlim[] _candados = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

    /// <summary>Toma el candado del usuario (liberarlo con Release); null si no entró en 5 s (algo quedó colgado).</summary>
    public async Task<SemaphoreSlim?> BloquearAsync(string userId)
    {
        var candado = _candados[(uint)StringComparer.Ordinal.GetHashCode(userId) % (uint)_candados.Length];
        return await candado.WaitAsync(EsperaCandado) ? candado : null;
    }
}
