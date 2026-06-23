using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Mohist.Server.Api;

public static class ExceptionMiddleware
{
    public static WebApplication UseApiExceptionHandler(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (KeyNotFoundException ex)
            {
                var response = new ApiResponse<object>(false, Error: ex.Message, Code: "not_found");
                await Results.Json(response, statusCode: 404).ExecuteAsync(context);
            }
            catch (ArgumentException ex)
            {
                var response = new ApiResponse<object>(false, Error: ex.Message, Code: "validation");
                await Results.Json(response, statusCode: 400).ExecuteAsync(context);
            }
            catch (InvalidOperationException ex)
            {
                var response = new ApiResponse<object>(false, Error: ex.Message, Code: "conflict");
                await Results.Json(response, statusCode: 409).ExecuteAsync(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                // Request cancellation must not surface as a 500; let the
                // framework handle it.
                throw;
            }
            catch (Exception ex)
            {
                // Fallback so every unhandled exception surfaces a
                // diagnosable ApiResponse body (with the exception
                // message) instead of an opaque empty 500.
                var logger = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger(nameof(ExceptionMiddleware));
                logger?.LogError(ex, "Unhandled exception in {Path}", context.Request.Path);
                var response = new ApiResponse<object>(false, Error: ex.Message, Code: "internal_error");
                await Results.Json(response, statusCode: 500).ExecuteAsync(context);
            }
        });

        return app;
    }
}
