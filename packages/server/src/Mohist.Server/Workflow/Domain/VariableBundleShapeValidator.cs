using System.Text.Json;

namespace Mohist.Server.Workflow.Domain;

public static class VariableBundleShapeValidator
{
    public static void Validate(VariableBundle bundle)
    {
        ValidateObject(bundle.Vars, "vars");

        if (bundle.Stages is null) return;

        foreach (var (stage, stageVariables) in bundle.Stages)
        {
            if (stageVariables is null)
            {
                throw new ArgumentException(
                    $"Variables stage '{stage}' must be a JSON object.",
                    stage);
            }

            ValidateObject(stageVariables.Vars, $"stages.{stage}.vars");
        }
    }

    private static void ValidateObject(JsonElement? value, string path)
    {
        if (value.HasValue && value.Value.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException(
                $"Variables field '{path}' must be a JSON object.",
                path);
        }
    }
}
