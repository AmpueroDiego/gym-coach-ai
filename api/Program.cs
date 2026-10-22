using System.Text.Json;
using GymCoach.Api;
using GymCoach.Api.Auth;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

static string Requerida(string nombre) =>
    Environment.GetEnvironmentVariable(nombre) is string v && !string.IsNullOrWhiteSpace(v)
        ? v
        : throw new InvalidOperationException($"Falta la app setting {nombre}.");

// Se leen al arrancar: una config incompleta tumba el host en vez de fallar en la primera petición.
var clientId = Requerida("GOOGLE_CLIENT_ID");
var cosmosEndpoint = Requerida("COSMOS_ENDPOINT");
var cosmosKey = Requerida("COSMOS_KEY");
var cosmosDb = Requerida("COSMOS_DB");
var sesionSecreto = Requerida("SESSION_SECRET");

// El coach es opcional: sin estas settings el host arranca y /api/coach responde 503 coach_no_configurado.
static string? Opcional(string nombre) =>
    Environment.GetEnvironmentVariable(nombre) is string v && !string.IsNullOrWhiteSpace(v) ? v : null;
// Solo para verificar cookies durante una rotación de SESSION_SECRET.
var sesionSecretoAnterior = Opcional("SESSION_SECRET_ANTERIOR");
// Cookie sin Secure para probar por http://localhost; solo cuenta con Core Tools (Development), nunca en Azure.
var cookieInseguraPedida = Opcional("SESSION_COOKIE_INSECURE") == "1";
var cookieInsegura = cookieInseguraPedida && Opcional("AZURE_FUNCTIONS_ENVIRONMENT") == "Development";
var openAiEndpoint = Opcional("AZURE_OPENAI_ENDPOINT");
var openAiKey = Opcional("AZURE_OPENAI_KEY");
var openAiDeployment = Opcional("AZURE_OPENAI_DEPLOYMENT");
// gpt-5-mini necesita "minimal" para responder rápido; sin setting se usa el esfuerzo por defecto del modelo.
var openAiRazonamiento = Opcional("AZURE_OPENAI_REASONING");
// Endpoint mal escrito o sin https cuenta como no configurado: la key nunca viaja en claro.
var coachConfigurado = Uri.TryCreate(openAiEndpoint, UriKind.Absolute, out var openAiUri) && openAiUri.Scheme == Uri.UriSchemeHttps &&
    openAiKey is not null && openAiDeployment is not null;
// Motor del coach: "agente" (Foundry Agent Service, con memoria del día) o "bucle" (por defecto y ante cualquier otro valor).
// El agente reusa AZURE_OPENAI_KEY; sin endpoint https del proyecto o sin coach configurado, queda el bucle.
var motorAgentePedido = string.Equals(Opcional("COACH_MOTOR")?.Trim(), "agente", StringComparison.OrdinalIgnoreCase);
var foundryEndpoint = Opcional("FOUNDRY_PROJECT_ENDPOINT");
var coachAgente = Opcional("COACH_AGENTE") ?? "gym-coach";
var motorAgente = motorAgentePedido && coachConfigurado &&
    Uri.TryCreate(foundryEndpoint, UriKind.Absolute, out var foundryUri) && foundryUri.Scheme == Uri.UriSchemeHttps;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults(app => app.UseMiddleware<ManejoErrores>())
    .ConfigureServices(services =>
    {
        services.AddSingleton(sp => ValidadorGoogle.ParaGoogle(
            clientId, sp.GetRequiredService<ILoggerFactory>().CreateLogger<ValidadorGoogle>()));
        services.AddSingleton(TimeProvider.System);
        // El constructor valida el secreto: uno corto o mal formado tumba el host al arrancar.
        services.AddSingleton(new SesionFirmada(sesionSecreto, sesionSecretoAnterior, TimeProvider.System, cookieInsegura));
        services.AddSingleton<Autenticador>();
        services.AddSingleton<Limites>();

        // Un solo CosmosClient por proceso (reutiliza conexiones).
        var cosmos = new CosmosClient(cosmosEndpoint, cosmosKey, new CosmosClientOptions
        {
            // System.Text.Json: camelCase y ts se queda como string (Newtonsoft lo convertiría a fecha).
            UseSystemTextJsonSerializerWithOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web),
            // Con la base saturada (429), el SDK reintentaba hasta ~30 s: mejor fallar rápido.
            MaxRetryAttemptsOnRateLimitedRequests = 3,
            MaxRetryWaitTimeOnRateLimitedRequests = TimeSpan.FromSeconds(5),
        });
        services.AddSingleton(cosmos);
        var series = cosmos.GetContainer(cosmosDb, "series");
        services.AddSingleton(series);

        // Fábricas explícitas: estas clases también tienen constructores internos para las pruebas.
        services.AddSingleton<IModeloCoach>(_ => coachConfigurado
            ? new ModeloAzureOpenAI(openAiEndpoint!, openAiKey!, openAiDeployment!, openAiRazonamiento)
            : new ModeloSinConfigurar());
        services.AddSingleton(sp => new PerfilesCoach(series, sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new HerramientasCoach(series, sp.GetRequiredService<PerfilesCoach>()));
        // El del bucle: lo usan PlanesDia (siempre) y POST /coach con el motor bucle o como respaldo del agente.
        services.AddSingleton(sp => new ConversacionCoach(sp.GetRequiredService<IModeloCoach>(),
            sp.GetRequiredService<HerramientasCoach>(), sp.GetRequiredService<ILogger<ConversacionCoach>>()));
        services.AddSingleton(sp =>
        {
            var bucle = sp.GetRequiredService<ConversacionCoach>();
            var log = sp.GetRequiredService<ILogger<MotorCoach>>();
            if (!motorAgente) return new MotorCoach(bucle, log: log);
            // Una sola instancia: es el modelo del agente y también crea/borra sus conversaciones.
            var agente = new ModeloAgenteFoundry(foundryEndpoint!, openAiKey!, coachAgente);
            return new MotorCoach(bucle,
                new ConversacionCoach(agente, sp.GetRequiredService<HerramientasCoach>(), sp.GetRequiredService<ILogger<ConversacionCoach>>()),
                new ConversacionesCoach(series, agente, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<ConversacionesCoach>>()),
                sp.GetRequiredService<PerfilesCoach>(), log);
        });
        services.AddSingleton(sp => new PerfilesEntrenamiento(series, sp.GetRequiredService<Limites>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new PlanesDia(series, sp.GetRequiredService<Limites>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ConversacionCoach>(), sp.GetRequiredService<PerfilesCoach>(),
            sp.GetRequiredService<PerfilesEntrenamiento>(), sp.GetRequiredService<ILogger<PlanesDia>>()));
    })
    .Build();

if (!coachConfigurado)
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program")
        .LogWarning("Faltan AZURE_OPENAI_ENDPOINT (https), AZURE_OPENAI_KEY o AZURE_OPENAI_DEPLOYMENT: el coach queda desactivado.");
if (motorAgentePedido && !motorAgente)
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program")
        .LogWarning("COACH_MOTOR=agente sin FOUNDRY_PROJECT_ENDPOINT (https) o sin el coach configurado: se usa el bucle.");
if (cookieInsegura)
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program")
        .LogWarning("SESSION_COOKIE_INSECURE activo: cookie de sesión sin Secure (solo desarrollo local).");
else if (cookieInseguraPedida)
    host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program")
        .LogWarning("SESSION_COOKIE_INSECURE=1 se ignora: solo aplica con AZURE_FUNCTIONS_ENVIRONMENT=Development.");

host.Run();
