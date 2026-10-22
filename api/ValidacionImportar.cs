using System.Text;
using System.Text.Json;

namespace GymCoach.Api;

public static partial class Validacion
{
    public const int MaxBodyImportarBytes = 8 * 1024;
    public const int MaxSeriesImportar = 10;

    /// <summary>
    /// Valida el body de POST /importar/series: { series: [serie, ...] }, cada una con su id y las mismas reglas
    /// que PUT /series/{id}. Todo o nada: con un error en cualquier serie no se devuelve ninguna.
    /// </summary>
    public static (List<Serie>? Series, List<ErrorCampo> Errores) Importar(string body, DateTimeOffset ahora)
    {
        // GetString lanza InvalidOperationException con un surrogate suelto en el JSON: es entrada mala, no un 500.
        try { return ImportarInterna(body, ahora); }
        catch (InvalidOperationException) { return (null, [new("body", "No es JSON válido.")]); }
    }

    private static (List<Serie>? Series, List<ErrorCampo> Errores) ImportarInterna(string body, DateTimeOffset ahora)
    {
        var errores = new List<ErrorCampo>();
        // El handler ya lo corta al leer; se repite para que la validación no dependa de quién la llame.
        if (Encoding.UTF8.GetByteCount(body) > MaxBodyImportarBytes)
        {
            errores.Add(new("body", $"Máximo {MaxBodyImportarBytes} bytes."));
            return (null, errores);
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body, OpcionesJson); }
        catch (JsonException)
        {
            errores.Add(new("body", "No es JSON válido."));
            return (null, errores);
        }

        using (doc)
        {
            var raiz = doc.RootElement;
            if (raiz.ValueKind != JsonValueKind.Object)
            {
                errores.Add(new("body", "Debe ser un objeto JSON."));
                return (null, errores);
            }

            // Largo antes de recorrer: un arreglo enorme muere en un solo chequeo.
            if (!raiz.TryGetProperty("series", out var arreglo) || arreglo.ValueKind != JsonValueKind.Array ||
                arreglo.GetArrayLength() is < 1 or > MaxSeriesImportar)
            {
                errores.Add(new("series", $"Debe ser una lista de 1 a {MaxSeriesImportar} series."));
                return (null, errores);
            }

            var series = new List<Serie>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var i = 0;
            foreach (var el in arreglo.EnumerateArray())
            {
                var campo = $"series[{i++}]";
                if (el.ValueKind != JsonValueKind.Object)
                {
                    errores.Add(new(campo, "Debe ser un objeto JSON."));
                    continue;
                }

                var (serie, erroresSerie) = Serie(Texto(el, "id") ?? "", el.GetRawText(), ahora);
                errores.AddRange(erroresSerie.Select(e => e with { Campo = $"{campo}.{e.Campo}" }));
                if (serie is null) continue;
                // Comparados ya normalizados: el mismo id en mayúsculas es el mismo documento.
                if (!ids.Add(serie.Id)) errores.Add(new($"{campo}.id", "Repetido en el lote."));
                else series.Add(serie);
            }

            return errores.Count > 0 ? (null, errores) : (series, errores);
        }
    }
}
