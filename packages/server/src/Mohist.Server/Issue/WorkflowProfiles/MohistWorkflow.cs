using System.Text.Json;
using System.Globalization;
using Mohist.Server.Workflow.Grains;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Mohist.Server.Issue.WorkflowProfiles;

public static class MohistWorkflow
{
    private const string DefinitionFileName = "mohist-default.workflow.yaml";
    private static readonly Lazy<WorkflowDefinitionInput> DefaultDefinition = new(LoadDefaultDefinition);

    public static WorkflowDefinitionInput Definition => DefaultDefinition.Value;

    public static WorkflowDefinitionInput ParseYaml(string yaml)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        var document = deserializer.Deserialize<WorkflowYaml>(yaml)
            ?? throw new InvalidOperationException("Workflow YAML is empty");

        if (document.Stages.Count == 0)
            throw new InvalidOperationException("Workflow YAML requires at least one stage");

        return new WorkflowDefinitionInput(document.Stages.Select(ToStage).ToList());
    }

    private static WorkflowDefinitionInput LoadDefaultDefinition()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Issue", "WorkflowProfiles", DefinitionFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Default Mohist workflow definition not found: {path}", path);
        return ParseYaml(File.ReadAllText(path));
    }

    private static StageDefinitionInput ToStage(StageYaml stage)
    {
        if (string.IsNullOrWhiteSpace(stage.Stage))
            throw new InvalidOperationException("Workflow stage requires stage");
        return new StageDefinitionInput(
            stage.Stage,
            stage.Tasks.Select(ToTask).ToList(),
            stage.Checks.Select(ToCheck).ToList(),
            stage.TasksFrom?.Uses,
            SerializeWith(stage.TasksFrom?.With),
            stage.RequiresApproval);
    }

    private static TaskDefinitionInput ToTask(TaskYaml task)
    {
        if (string.IsNullOrWhiteSpace(task.Id))
            throw new InvalidOperationException("Workflow task requires id");
        if (string.IsNullOrWhiteSpace(task.Title))
            throw new InvalidOperationException($"Workflow task {task.Id} requires title");
        return new TaskDefinitionInput(task.Id, task.Title, task.Uses, SerializeWith(task.With));
    }

    private static CheckDefinitionInput ToCheck(CheckYaml check)
    {
        if (string.IsNullOrWhiteSpace(check.Name))
            throw new InvalidOperationException("Workflow check requires name");
        if (string.IsNullOrWhiteSpace(check.Title))
            throw new InvalidOperationException($"Workflow check {check.Name} requires title");
        return new CheckDefinitionInput(
            check.Name,
            check.Title,
            check.Uses,
            SerializeWith(check.With),
            check.RetryLimit,
            check.RetryTask is null ? null : ToTask(check.RetryTask));
    }

    private static string? SerializeWith(Dictionary<string, object?>? with) =>
        with is null ? null : JsonSerializer.Serialize(Normalize(with), WorkflowVariableJson.Options);

    private static object? Normalize(object? value) => value switch
    {
        Dictionary<object, object?> map => map.ToDictionary(kv => kv.Key.ToString() ?? "", kv => Normalize(kv.Value)),
        Dictionary<string, object?> map => map.ToDictionary(kv => kv.Key, kv => Normalize(kv.Value)),
        IList<object?> list => list.Select(Normalize).ToList(),
        string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        string text when bool.TryParse(text, out var flag) => flag,
        _ => value,
    };

    private sealed class WorkflowYaml
    {
        public List<StageYaml> Stages { get; set; } = [];
    }

    private sealed class StageYaml
    {
        public string Stage { get; set; } = "";
        public bool RequiresApproval { get; set; }
        public List<TaskYaml> Tasks { get; set; } = [];
        public List<CheckYaml> Checks { get; set; } = [];
        public TasksFromYaml? TasksFrom { get; set; }
    }

    private sealed class TasksFromYaml
    {
        public string? Uses { get; set; }
        public Dictionary<string, object?>? With { get; set; }
    }

    private sealed class TaskYaml
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Uses { get; set; }
        public Dictionary<string, object?>? With { get; set; }
    }

    private sealed class CheckYaml
    {
        public string Name { get; set; } = "";
        public string Title { get; set; } = "";
        public string? Uses { get; set; }
        public Dictionary<string, object?>? With { get; set; }
        public int RetryLimit { get; set; }
        public TaskYaml? RetryTask { get; set; }
    }
}
