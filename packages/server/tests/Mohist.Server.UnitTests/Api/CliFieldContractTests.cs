using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Mohist.Cli;
using Mohist.Server.Agent.Services;
using Mohist.Server.AgentOps.Services;
using Mohist.Server.Api;
using Mohist.Server.Epic.Services;
using Mohist.Server.Infrastructure;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Issue.Services;
using Mohist.Server.Issue.Services.IssueTemplates;
using Mohist.Server.Label.Services;
using Mohist.Server.Project.Domain;
using Mohist.Server.Project.Services;
using Mohist.Server.Runner.Services;
using Mohist.Server.Sessions;
using Mohist.Server.Sessions.Grains;
using Mohist.Server.Sessions.Services;
using Mohist.Server.SystemInfo;
using Mohist.Server.Workflow.Domain;
using Mohist.Server.Workflow.Domain.Prompts;
using Mohist.Server.Workflow.Grains;
using Mohist.Server.Workflow.Services;
using Mohist.Server.Workspace.Services;
using Mohist.Server.Webhooks.Domain;
using Mohist.Server.Webhooks.Services;
using Xunit;

namespace Mohist.Server.UnitTests.Api;

public sealed class CliFieldContractTests
{
    private static readonly JsonSerializerOptions ContractJsonOptions = new(JSON.Options)
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    private static readonly IReadOnlyDictionary<MohistCliApi.TableShape, Registration> Registrations =
        new Dictionary<MohistCliApi.TableShape, Registration>
        {
            [MohistCliApi.TableShape.ProjectList] = D<ProjectInfo>(),
            [MohistCliApi.TableShape.Project] = D<ProjectInfo>(),
            [MohistCliApi.TableShape.IssueList] = D<IssueListItem>(),
            [MohistCliApi.TableShape.Issue] = D<IssueReadModel>(),
            [MohistCliApi.TableShape.WorkflowStatus] = D<IssueWorkflowStatus>(),
            [MohistCliApi.TableShape.Sessions] = D<WorkflowSessionDto>(),
            [MohistCliApi.TableShape.RepoList] = D<RepositoryInfo>(),
            [MohistCliApi.TableShape.FeedbackList] = D<WorkflowFeedbackRecord>(),
            [MohistCliApi.TableShape.FeedbackShow] = D<WorkflowFeedbackRecord>(),
            [MohistCliApi.TableShape.CommentShow] = D<IssueCommentDto>(),
            [MohistCliApi.TableShape.AgentList] = D<AgentInfo>(),
            [MohistCliApi.TableShape.AgentShow] = D<AgentInfo>(),
            [MohistCliApi.TableShape.EpicList] = D<EpicWithProgressDto>(),
            [MohistCliApi.TableShape.EpicShow] = D<EpicDetailDto>(),
            [MohistCliApi.TableShape.EpicLink] = D<BatchMembershipOutcome>(),
            [MohistCliApi.TableShape.EpicUnlink] = D<BatchMembershipOutcome>(),
            [MohistCliApi.TableShape.LabelList] = D<LabelDefinition>(),
            [MohistCliApi.TableShape.IssueTemplateList] = D<IssueTemplateInfo>(),
            [MohistCliApi.TableShape.IssueTemplateShow] = D<IssueTemplateDetail>(),
            [MohistCliApi.TableShape.WorkflowProfileList] = D<WorkflowProfileCollectionEntry>(),
            [MohistCliApi.TableShape.WorkflowProfileDetail] = D<WorkflowProfileDetailResponse>(),
            [MohistCliApi.TableShape.RunnerList] = D<RunnerStatusView>(),
            [MohistCliApi.TableShape.RunnerShow] = D<RunnerStatusView>(),
            [MohistCliApi.TableShape.Models] = S("models endpoint returns a runtime-specific string collection"),
            [MohistCliApi.TableShape.SystemInfo] = D<SystemInfoResponse>(),
            [MohistCliApi.TableShape.WorkflowProfile] = D<IssueWorkflowProfileResponse>(),
            [MohistCliApi.TableShape.WorkflowVariables] = D<VariableBundle>(),
            [MohistCliApi.TableShape.WorkflowProfilePrompt] = D<EffectivePrompt>(),
            [MohistCliApi.TableShape.WorkflowProfilePreview] = D<PromptPreviewResult>(),
            [MohistCliApi.TableShape.SessionMetadata] = D<AgentSessionMetadataDto>(),
            [MohistCliApi.TableShape.SessionTranscriptSummary] = D<AgentSessionTranscriptResponse>(),
            [MohistCliApi.TableShape.SessionRecovery] = D<AgentSessionRecoveryResult>(),
            [MohistCliApi.TableShape.AgentSessionLaunch] = D<AgentSessionLaunchResponse>(),
            [MohistCliApi.TableShape.AgentSessionSpawn] = D<AgentSessionSpawnRoutes.AgentSessionSpawnResponse>(),
            [MohistCliApi.TableShape.AgentSessionFollowup] = D<AgentSessionFollowupResult>(),
            [MohistCliApi.TableShape.AgentSessionStop] = D<RunnerStopReply>(),
            [MohistCliApi.TableShape.AgentSessionList] = D<AgentSessionListItemDto>(),
            [MohistCliApi.TableShape.AgentSessionShow] = D<GenericAgentSessionSummaryDto>(),
            [MohistCliApi.TableShape.AgentSessionTranscript] = D<AgentSessionTranscriptResponse>(),
            [MohistCliApi.TableShape.AgentSubscriptionList] = D<AgentSubscriptionListDto>(),
            [MohistCliApi.TableShape.AgentSubscription] = D<AgentSubscriptionDto>(),
            [MohistCliApi.TableShape.RoutingRuleList] = D<RoutingRuleDto>(),
            [MohistCliApi.TableShape.RoutingRule] = D<RoutingRuleDto>(),
            [MohistCliApi.TableShape.WebhookSubscriptionList] = D<WebhookSubscriptionDto>(),
            [MohistCliApi.TableShape.WebhookSubscription] = D<WebhookSubscriptionDto>(),
            [MohistCliApi.TableShape.WebhookDeliveryFailureList] = D<WebhookDeliveryFailureDto>(),
            [MohistCliApi.TableShape.ProjectTemplateList] = D<ProjectTemplateInfo>(),
            [MohistCliApi.TableShape.ProjectTemplateShow] = D<WorkflowProfileCollectionEntry>(),
            [MohistCliApi.TableShape.ProjectWorkflowProfile] = D<ProjectWorkflowProfileResponse>(),
            [MohistCliApi.TableShape.IssueArchiveCompleted] = D<IssueArchiveCompletedResponse>(),
            [MohistCliApi.TableShape.WorkflowRunDetail] = D<WorkflowRunDetailDto>(),
            [MohistCliApi.TableShape.WorkflowApproval] = S("approval is an empty mutation response"),
            [MohistCliApi.TableShape.WorkflowRunVariables] = S("run variables are a dynamically keyed object"),
            [MohistCliApi.TableShape.WorkflowRunEvents] = D<StoredCloudEventDto>(),
            [MohistCliApi.TableShape.RunList] = S("run list is projected from issue workflow data by the CLI"),
            [MohistCliApi.TableShape.DeadLetterList] = D<DeadLetterListItemResponse>(),
            [MohistCliApi.TableShape.DeadLetterRedelivery] = D<DeadLetterRedeliveryResponse>(),
            [MohistCliApi.TableShape.ActivityList] = D<ActivityEntryDto>(),
            [MohistCliApi.TableShape.OtelTracesList] = S("trace summaries are a CLI projection over the OTel traces endpoint"),
            [MohistCliApi.TableShape.IssueWatchList] = S("watch list is a CLI projection over issue watch state"),
            [MohistCliApi.TableShape.AgentJobList] = D<AgentJobListItemDto>(),
            [MohistCliApi.TableShape.AgentJobView] = D<AgentJobViewDto>(),
            [MohistCliApi.TableShape.SessionList] = D<UnifiedSessionListItemDto>(),
            [MohistCliApi.TableShape.SessionShow] = D<UnifiedSessionSummaryDto>(),
            [MohistCliApi.TableShape.SessionTree] = D<AgentSessionTreePage>(),
            [MohistCliApi.TableShape.SessionTranscript] = D<AgentSessionTranscriptResponse>(),
            [MohistCliApi.TableShape.SessionFollowup] = D<AgentSessionFollowupResult>(),
            [MohistCliApi.TableShape.SessionStop] = S("Session stop has direct-turn and cascade response shapes"),
            [MohistCliApi.TableShape.SessionDetach] = D<SessionTreeDetachResult>(),
            [MohistCliApi.TableShape.SessionScheduleCreate] = D<AgentSessionScheduleDto>(),
            [MohistCliApi.TableShape.SessionScheduleList] = D<AgentSessionScheduleDto>(),
            [MohistCliApi.TableShape.SessionScheduleCancel] = D<AgentSessionScheduleDto>(),
            [MohistCliApi.TableShape.WorkspaceList] = D<WorkspaceDto>(),
            [MohistCliApi.TableShape.WorkspaceShow] = D<WorkspaceDto>(),
        };

    private static readonly IReadOnlyList<FieldDeviation> Deviations =
    [
        new(MohistCliApi.TableShape.SystemInfo, "degraded", DeviationKind.Local, "CLI emits degraded=true when the server is unavailable"),
        new(MohistCliApi.TableShape.SystemInfo, "cliVersion", DeviationKind.Local, "CLI emits its local version when the server is unavailable"),
        new(MohistCliApi.TableShape.AgentSessionSpawn, "inputId", DeviationKind.Omit, "spawn table exposes stable identities and parent linkage"),
        new(MohistCliApi.TableShape.AgentSessionSpawn, "agentId", DeviationKind.Omit, "spawn table exposes stable identities and parent linkage"),
        new(MohistCliApi.TableShape.AgentSessionSpawn, "agentName", DeviationKind.Omit, "spawn table exposes stable identities and parent linkage"),
        new(MohistCliApi.TableShape.AgentSessionSpawn, "status", DeviationKind.Omit, "spawn table exposes stable identities and parent linkage"),
        new(MohistCliApi.TableShape.AgentSessionSpawn, "attachments", DeviationKind.Omit, "spawn table exposes stable identities and parent linkage"),
        new(MohistCliApi.TableShape.AgentSessionSpawn, "rejectedAttachments", DeviationKind.Omit, "spawn table exposes stable identities and parent linkage"),
        new(MohistCliApi.TableShape.AgentSessionSpawn, "transcriptUrl", DeviationKind.Omit, "spawn table exposes stable identities and parent linkage"),
        new(MohistCliApi.TableShape.AgentSessionSpawn, "jobUrl", DeviationKind.Omit, "spawn table exposes stable identities and parent linkage"),
        new(MohistCliApi.TableShape.AgentSessionSpawn, "observationUrl", DeviationKind.Omit, "spawn table exposes stable identities and parent linkage"),
        new(MohistCliApi.TableShape.SessionScheduleCreate, "alreadyExists", DeviationKind.Omit, "schedule CLI output omits idempotency replay metadata"),
        new(MohistCliApi.TableShape.SessionScheduleList, "alreadyExists", DeviationKind.Omit, "schedule CLI output omits idempotency replay metadata"),
        new(MohistCliApi.TableShape.SessionScheduleCancel, "alreadyExists", DeviationKind.Omit, "schedule CLI output omits idempotency replay metadata"),
        new(MohistCliApi.TableShape.AgentJobList, "failureReason", DeviationKind.Omit, "agent-job recovery presentation is added by the status-surface task"),
        new(MohistCliApi.TableShape.AgentJobList, "recoveryDeadlineAt", DeviationKind.Omit, "agent-job recovery presentation is added by the status-surface task"),
        new(MohistCliApi.TableShape.AgentJobView, "recoveryDeadlineAt", DeviationKind.Omit, "agent-job recovery presentation is added by the status-surface task"),
    ];

    [Fact]
    public void EveryTableShapeHasOneRegistration()
    {
        var expected = Enum.GetValues<MohistCliApi.TableShape>().ToHashSet();
        var actual = Registrations.Keys.ToHashSet();

        Assert.True(expected.SetEquals(actual), DescribeDifference(expected, actual));
    }

    [Fact]
    public void MappedCatalogsMatchServerSerializationFields()
    {
        foreach (var (shape, registration) in Registrations.Where(pair => pair.Value.DtoType is not null))
        {
            var descriptor = ResourceOutputCatalog.For(shape.ToString());
            var serverFields = ContractJsonOptions.GetTypeInfo(registration.DtoType!).Properties
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            var omitted = Deviations
                .Where(deviation => deviation.Shape == shape && deviation.Kind == DeviationKind.Omit)
                .Select(deviation => deviation.Field)
                .ToHashSet(StringComparer.Ordinal);
            var local = Deviations
                .Where(deviation => deviation.Shape == shape && deviation.Kind == DeviationKind.Local)
                .Select(deviation => deviation.Field)
                .ToHashSet(StringComparer.Ordinal);
            var expected = serverFields
                .Except(omitted, StringComparer.Ordinal)
                .Concat(local)
                .ToHashSet(StringComparer.Ordinal);
            var actual = descriptor.Fields.ToHashSet(StringComparer.Ordinal);

            Assert.True(expected.SetEquals(actual),
                $"{shape} ({registration.DtoType!.Name}): {DescribeDifference(expected, actual)}");
        }
    }

    [Fact]
    public void DeviationsHaveCorrectDirectionAndReasons()
    {
        foreach (var deviation in Deviations)
        {
            Assert.False(string.IsNullOrWhiteSpace(deviation.Reason));
            var registration = Registrations[deviation.Shape];
            Assert.NotNull(registration.DtoType);
            var fields = ContractJsonOptions.GetTypeInfo(registration.DtoType!).Properties
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            var descriptorFields = ResourceOutputCatalog.For(deviation.Shape.ToString()).Fields;
            if (deviation.Kind == DeviationKind.Omit)
            {
                Assert.Contains(deviation.Field, fields);
                Assert.DoesNotContain(deviation.Field, descriptorFields);
            }
            else
            {
                Assert.DoesNotContain(deviation.Field, fields);
                Assert.Contains(deviation.Field, descriptorFields);
            }
        }
    }

    [Fact]
    public void SyntheticRegistrationsHaveReasons()
    {
        foreach (var registration in Registrations.Values.Where(registration => registration.DtoType is null))
            Assert.False(string.IsNullOrWhiteSpace(registration.SyntheticReason));
    }

    private static Registration D<T>() => new(typeof(T), null);

    private static Registration S(string reason) => new(null, reason);

    private static string DescribeDifference(
        IReadOnlySet<MohistCliApi.TableShape> expected,
        IReadOnlySet<MohistCliApi.TableShape> actual) =>
        $"missing=[{string.Join(",", expected.Except(actual))}] extra=[{string.Join(",", actual.Except(expected))}]";

    private static string DescribeDifference(
        IReadOnlySet<string> expected,
        IReadOnlySet<string> actual) =>
        $"missing=[{string.Join(",", expected.Except(actual))}] extra=[{string.Join(",", actual.Except(expected))}]";

    private sealed record Registration(Type? DtoType, string? SyntheticReason);

    private sealed record FieldDeviation(
        MohistCliApi.TableShape Shape,
        string Field,
        DeviationKind Kind,
        string Reason);

    private enum DeviationKind
    {
        Omit,
        Local,
    }
}
