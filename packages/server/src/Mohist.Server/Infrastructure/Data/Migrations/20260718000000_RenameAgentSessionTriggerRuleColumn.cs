using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Mohist.Server.Infrastructure.Data.Migrations;

public partial class RenameAgentSessionTriggerRuleColumn : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE AgentSessions DROP COLUMN LabelTriggerSubscriptionId");
        migrationBuilder.Sql("ALTER TABLE AgentSessions ADD COLUMN LabelTriggerRuleId TEXT GENERATED ALWAYS AS (json_extract(State, '$.metadata.labels.\"mohist.io/trigger/rule-id\"')) VIRTUAL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE AgentSessions DROP COLUMN LabelTriggerRuleId");
        migrationBuilder.Sql("ALTER TABLE AgentSessions ADD COLUMN LabelTriggerSubscriptionId TEXT GENERATED ALWAYS AS (json_extract(State, '$.metadata.labels.\"mohist.io/trigger/subscription-id\"')) VIRTUAL");
    }
}
