using Mohist.Server.Workflow.Errors;

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
            catch (WorkflowDomainException ex)
            {
                var response = new ApiResponse<object>(false, Error: ex.Message, Code: "workflow_conflict");
                await Results.Json(response, statusCode: 409).ExecuteAsync(context);
            }
            catch (InvalidOperationException ex)
            {
                var response = new ApiResponse<object>(false, Error: ex.Message, Code: "not_found");
                await Results.Json(response, statusCode: 404).ExecuteAsync(context);
            }
        });

        return app;
    }
}
