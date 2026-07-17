using System.Text.Json;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Server.Workflow.Services;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.SpecTests.Specs.Workflow;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Workflow.Grain;

public class FailIfMarkerSpecs : WorkflowGrainSpecs
{
    public FailIfMarkerSpecs(WorkflowGrainFixture fixture) : base(fixture) { }

    [Fact]
    public void WorkflowYamlSerializer_PreservesFailIfInTaskWith()
    {
        // The engine stays failIf-agnostic — it does not interpret
        // expect.markers[*].failIf; the runner does. The serializer's job
        // is to preserve the value through round-trip so the runner sees
        // exactly what the profile author wrote.
        var yaml = """
        stages:
          - stage: check
            tasks:
              - id: ai-review
                title: AI review
                uses: mohist/acp-agent
                with:
                  expect:
                    markers:
                      - path: review.md
                        oneOf:
                          - "<promise>PASS</promise>"
                          - "<promise>FAIL</promise>"
                        failIf: "<promise>FAIL</promise>"
            checks: []
        """;

        var definition = WorkflowYamlSerializer.FromYaml(yaml);
        var emitted = WorkflowYamlSerializer.ToYaml(definition);

        Assert.Contains("failIf:", emitted);
        Assert.Contains("oneOf:", emitted);
    }

    [Fact]
    public void ExtractRequiredFiles_WithExpectMarkersFailIf_ReturnsFailIfAndOneOf()
    {
        // The TaskRun-level required-file extraction should surface the
        // failIf marker as a required file entry carrying oneOf and
        // failIf metadata so downstream views can show "this marker, if
        // matched, fails the task".
        var withInput = new Dictionary<string, JsonElement?>
        {
            ["expect"] = JsonSerializer.Deserialize<JsonElement>("""
                {
                  "markers": [
                    {
                      "path": "review.md",
                      "oneOf": ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
                      "failIf": "<promise>FAIL</promise>"
                    }
                  ]
                }
                """),
        };

        var result = TaskRunExtensions.ExtractRequiredFiles(withInput);

        var entry = Assert.Single(result);
        Assert.Equal("review.md", entry.Path);
        Assert.NotNull(entry.OneOf);
        Assert.Equal(new[] { "<promise>PASS</promise>", "<promise>FAIL</promise>" }, entry.OneOf!);
        Assert.Equal("<promise>FAIL</promise>", entry.FailIf);
    }

    [Fact]
    public void ExtractRequiredFiles_WithExpectFilesAndMarkers_DedupesByPath()
    {
        // When both `expect.files[*]` and `expect.markers[*]` declare the
        // same path, only one RequiredFile entry is returned. The
        // marker-derived entry wins because it carries the failIf/oneOf
        // metadata.
        var withInput = new Dictionary<string, JsonElement?>
        {
            ["expect"] = JsonSerializer.Deserialize<JsonElement>("""
                {
                  "files": [
                    {"path": "review.md", "markers": ["<promise>PASS</promise>"]}
                  ],
                  "markers": [
                    {
                      "path": "review.md",
                      "oneOf": ["<promise>PASS</promise>", "<promise>FAIL</promise>"],
                      "failIf": "<promise>FAIL</promise>"
                    }
                  ]
                }
                """),
        };

        var result = TaskRunExtensions.ExtractRequiredFiles(withInput);

        var entry = Assert.Single(result);
        Assert.Equal("review.md", entry.Path);
        Assert.Equal("<promise>FAIL</promise>", entry.FailIf);
        Assert.NotNull(entry.OneOf);
    }
}
