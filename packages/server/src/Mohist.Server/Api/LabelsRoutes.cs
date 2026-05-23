namespace Mohist.Server.Api;

public static class LabelsRoutes
{
    private static readonly List<string> DefaultLabels =
    [
        "bug",
        "feature",
        "refactor",
        "docs",
        "test",
        "debt",
        "urgent",
    ];

    public static WebApplication MapLabelsRoutes(this WebApplication app)
    {
        app.MapGet("/api/labels", () => ApiResults.Ok(DefaultLabels));
        return app;
    }
}
