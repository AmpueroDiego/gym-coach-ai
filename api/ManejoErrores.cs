using GymCoach.Api.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Middleware;
using Microsoft.Extensions.Logging;

namespace GymCoach.Api;

/// <summary>Cualquier excepción no controlada sale como 500 {error:"interno"}, sin detalles internos.</summary>
public sealed class ManejoErrores(ILogger<ManejoErrores> log) : IFunctionsWorkerMiddleware
{
    public async Task Invoke(FunctionContext context, FunctionExecutionDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (Exception e)
        {
            log.LogError(e, "Error no controlado en {Funcion}", context.FunctionDefinition.Name);
            var req = await context.GetHttpRequestDataAsync();
            if (req is null) throw;
            context.GetInvocationResult().Value = await Http.Interno(req);
        }
    }
}
