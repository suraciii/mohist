import { AlertTriangleIcon } from 'lucide-react'
import type { AgentExecutabilityFacts } from '../../../entities/agent-connection'
import { CardSection } from '@/shared/ui/components/card-section'

/** States that stop the Agent from accepting work while Slack setup is complete. */
const BLOCKED_EXECUTION_STATES = new Set(['not-configured', 'not-executable'])

export function isAgentExecutionBlocked(state: string | null | undefined): boolean {
  return !!state && BLOCKED_EXECUTION_STATES.has(state)
}

/**
 * Slack setup completion and the Agent's ability to accept work are separate
 * facts. A claimed Connection whose Agent cannot execute states that limitation
 * on its own instead of folding it into the Connection's health. The repair
 * action itself stays the one primary action the Server projected, so this
 * section reports the gaps and repeats no second link.
 */
export function AgentExecutabilitySection({ executability }: { executability: AgentExecutabilityFacts }) {
  return (
    <CardSection title="Agent cannot accept work yet" tone="amber">
      <div className="space-y-3" data-testid="connection-agent-executability">
        <div className="flex items-start gap-2 text-sm text-foreground">
          <AlertTriangleIcon className="mt-0.5 size-4 shrink-0 text-warning" />
          <p>
            Slack setup for this Connection is complete. Its Agent is {executability.state.replaceAll('-', ' ')}, so new
            delegations are safely rejected until the Agent is repaired.
          </p>
        </div>
        {executability.gaps.length > 0 && (
          <ul className="space-y-2" data-testid="connection-agent-executability-gaps">
            {executability.gaps.map((gap) => (
              <li
                key={gap.code}
                data-testid={`connection-agent-executability-gap-${gap.code}`}
                className="rounded-md border border-border bg-background/60 px-3 py-2 text-sm"
              >
                <p className="font-medium text-foreground">{gap.message}</p>
                <p className="mt-0.5 text-muted-foreground">{gap.nextAction}</p>
              </li>
            ))}
          </ul>
        )}
      </div>
    </CardSection>
  )
}
