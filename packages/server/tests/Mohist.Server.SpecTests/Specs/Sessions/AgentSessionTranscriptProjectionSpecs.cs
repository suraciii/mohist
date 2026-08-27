using System.Text.Json;
using Mohist.Server.Sessions.Grains;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mohist.Server.Infrastructure.Data.Db;
using Mohist.Server.Infrastructure.Data.Issue;
using Mohist.Server.Infrastructure.Data.Sessions;
using Mohist.Server.Infrastructure.Orleans;
using Mohist.Server.Issue.Grains;
using Mohist.Server.Runner.Grains;
using Mohist.Server.SpecTests.Support;
using Mohist.Server.TestSupport;
using Mohist.Server.Sessions.Services;
using Mohist.Server.Workflow.Domain.Run;
using Mohist.Workflow.Definition;
using Mohist.Server.Workflow.Grains;
using Xunit;

namespace Mohist.Server.SpecTests.Specs.Sessions;

/// <summary>
/// Representative application composition for the session transcript read
/// path (#676): runtime events flow through the session grain into the
/// transcript store, and the transcript/metadata endpoints project the
/// persisted parts. The persistence rules (turn/append/merge/ordering) and
/// summary reduction are owned by the UnitTests transcript store tests and
/// the accumulator/builder/projector tests; only route binding and the
/// public/raw view contract stay here.
/// </summary>
public class AgentSessionTranscriptProjectionSpecs : AgentSessionTestSupport
{
    public AgentSessionTranscriptProjectionSpecs(MohistIntegrationFixture fixture) : base(fixture)
    {
    }
}
