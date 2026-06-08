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
        });

        return app;
    }
}
