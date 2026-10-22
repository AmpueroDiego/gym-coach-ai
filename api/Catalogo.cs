namespace GymCoach.Api;

/// <summary>
/// Un ejercicio del catálogo. Convenciones de pesoKg:
/// - Mancuernas: peso de UNA mancuerna.
/// - CargaCorporal = true: pesoKg es el lastre extra (0 si no hay).
/// Equipo: "maquina" (máquinas y poleas) o "libre" (barra, mancuernas, peso corporal).
/// </summary>
public sealed record Ejercicio(string Id, string Nombre, string NombreEn, string Grupo, string Equipo, bool? CargaCorporal = null);

/// <summary>Catálogo estático y compartido; el id (slug) es lo que se guarda en cada serie, no cambiarlo.</summary>
public static class Catalogo
{
    public const string Maquina = "maquina";
    public const string Libre = "libre";

    public static readonly IReadOnlyList<Ejercicio> Ejercicios =
    [
        // pecho
        new("press-banca", "Press de banca", "Bench press", "pecho", Libre),
        new("press-inclinado-barra", "Press inclinado con barra", "Incline bench press", "pecho", Libre),
        new("press-banca-mancuernas", "Press de banca con mancuernas", "Dumbbell bench press", "pecho", Libre),
        new("press-inclinado-mancuernas", "Press inclinado con mancuernas", "Incline dumbbell press", "pecho", Libre),
        new("aperturas-mancuernas", "Aperturas con mancuernas", "Dumbbell fly", "pecho", Libre),
        new("cruce-poleas", "Cruce de poleas", "Cable crossover", "pecho", Maquina),
        new("fondos", "Fondos en paralelas", "Dips", "pecho", Libre, true),
        new("lagartijas", "Lagartijas", "Push-up", "pecho", Libre, true),
        new("press-pecho-maquina", "Press de pecho en máquina", "Chest press machine", "pecho", Maquina),
        new("press-pecho-iso-lateral", "Press de pecho iso-lateral en máquina", "Iso-lateral chest press", "pecho", Maquina),
        new("pec-deck", "Mariposa (pec deck)", "Butterfly (pec deck)", "pecho", Maquina),
        // espalda
        new("peso-muerto", "Peso muerto", "Deadlift", "espalda", Libre),
        new("dominadas", "Dominadas", "Pull-up", "espalda", Libre, true),
        new("jalon-pecho", "Jalón al pecho en polea", "Lat pulldown (cable)", "espalda", Maquina),
        new("remo-barra", "Remo con barra", "Barbell row", "espalda", Libre),
        new("remo-mancuerna", "Remo con mancuerna", "Dumbbell row", "espalda", Libre),
        new("remo-polea-baja", "Remo en polea baja", "Seated cable row", "espalda", Maquina),
        new("jalon-pecho-maquina", "Jalón al pecho en máquina", "Lat pulldown machine", "espalda", Maquina),
        new("remo-iso-lateral", "Remo iso-lateral en máquina", "Iso-lateral row", "espalda", Maquina),
        new("remo-bajo-iso-lateral", "Remo bajo iso-lateral en máquina", "Iso-lateral low row", "espalda", Maquina),
        // hombros
        new("press-militar", "Press militar", "Overhead press", "hombros", Libre),
        new("press-hombro-mancuernas", "Press de hombro con mancuernas", "Dumbbell shoulder press", "hombros", Libre),
        new("elevaciones-laterales", "Elevaciones laterales", "Dumbbell lateral raise", "hombros", Libre),
        new("pajaros", "Pájaros (deltoide posterior)", "Rear delt fly", "hombros", Libre),
        new("face-pull", "Face pull", "Face pull", "hombros", Maquina),
        new("press-hombro-maquina", "Press de hombro en máquina", "Shoulder press machine", "hombros", Maquina),
        new("elevaciones-laterales-maquina", "Elevaciones laterales en máquina", "Lateral raise machine", "hombros", Maquina),
        // biceps
        new("curl-barra", "Curl con barra", "Barbell curl", "biceps", Libre),
        new("curl-mancuernas", "Curl con mancuernas", "Dumbbell curl", "biceps", Libre),
        new("curl-martillo", "Curl martillo", "Hammer curl", "biceps", Libre),
        new("curl-predicador-maquina", "Curl predicador en máquina", "Preacher curl machine", "biceps", Maquina),
        new("curl-biceps-maquina", "Curl de bíceps en máquina", "Bicep curl machine", "biceps", Maquina),
        // triceps
        new("extension-triceps-polea", "Extensión de tríceps en polea", "Triceps extension (cable)", "triceps", Maquina),
        new("press-frances", "Press francés", "Skull crusher", "triceps", Libre),
        new("press-banca-cerrado", "Press de banca agarre cerrado", "Close-grip bench press", "triceps", Libre),
        new("extension-triceps-maquina", "Extensión de tríceps en máquina", "Triceps extension machine", "triceps", Maquina),
        new("fondos-maquina", "Fondos sentado en máquina", "Seated dip machine", "triceps", Maquina),
        // cuadriceps
        new("sentadilla", "Sentadilla", "Squat", "cuadriceps", Libre),
        new("sentadilla-frontal", "Sentadilla frontal", "Front squat", "cuadriceps", Libre),
        new("prensa", "Prensa de piernas inclinada", "Leg press", "cuadriceps", Maquina),
        new("extension-cuadriceps", "Extensión de cuádriceps", "Leg extension machine", "cuadriceps", Maquina),
        new("zancadas", "Zancadas con mancuernas", "Dumbbell lunge", "cuadriceps", Libre),
        new("prensa-horizontal", "Prensa de piernas horizontal", "Horizontal leg press", "cuadriceps", Maquina),
        // isquios
        new("peso-muerto-rumano", "Peso muerto rumano", "Romanian deadlift", "isquios", Libre),
        new("curl-femoral", "Curl femoral acostado", "Lying leg curl", "isquios", Maquina),
        new("curl-femoral-sentado", "Curl femoral sentado", "Seated leg curl", "isquios", Maquina),
        // gluteos
        new("hip-thrust", "Hip thrust", "Hip thrust", "gluteos", Libre),
        new("patada-gluteo-polea", "Patada de glúteo en polea", "Cable glute kickback", "gluteos", Maquina),
        new("abduccion-cadera", "Abducción de cadera en máquina (glúteos)", "Hip abduction machine (glutes)", "gluteos", Maquina),
        new("hiperextension-inversa", "Hiperextensión inversa", "Reverse hyperextension", "gluteos", Maquina),
        // aductores
        new("aduccion-cadera", "Aducción de cadera en máquina", "Hip adduction machine", "aductores", Maquina),
        // pantorrillas
        new("elevacion-talones-pie", "Elevación de talones de pie", "Standing calf raise", "pantorrillas", Libre),
        new("elevacion-talones-sentado", "Elevación de talones sentado", "Seated calf raise", "pantorrillas", Maquina),
        new("elevacion-talones-maquina", "Elevación de talones en máquina", "Calf extension machine", "pantorrillas", Maquina),
        // core
        new("rueda-abdominal", "Rueda abdominal", "Ab wheel rollout", "core", Libre, true),
        new("crunch-polea", "Crunch en polea", "Cable crunch", "core", Maquina),
        new("crunch-maquina", "Crunch en máquina", "Crunch machine", "core", Maquina),
        new("elevacion-piernas-colgado", "Elevación de piernas colgado", "Hanging leg raise", "core", Libre, true),
    ];

    static readonly HashSet<string> Ids = Ejercicios.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);

    public static bool Existe(string id) => Ids.Contains(id);
}
