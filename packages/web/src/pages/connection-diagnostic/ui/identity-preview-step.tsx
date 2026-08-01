import { ExternalLinkIcon, InfoIcon } from 'lucide-react'

interface IdentityPreviewStepProps {
  botName: string
  appDescription: string
  slackAppCreationReference: string
}

export function IdentityPreviewStep({
  botName,
  appDescription,
  slackAppCreationReference,
}: IdentityPreviewStepProps) {
  return (
    <div
      className="space-y-3"
      data-testid="connection-setup-identity-preview"
      data-bot-name={botName}
    >
      <p className="text-sm text-muted-foreground">
        Mohist will not create or install the Slack App on your behalf. Open Slack to create the App, paste
        the Bot identity below into the App settings, then return here to add the credentials.
      </p>
      <dl className="grid grid-cols-[minmax(7rem,1fr)_minmax(0,2fr)] gap-4 rounded-md border border-border bg-background/60 p-3 text-sm">
        <dt className="text-muted-foreground">Bot name</dt>
        <dd
          className="min-w-0 break-words font-mono text-foreground"
          data-testid="connection-setup-identity-bot-name"
        >
          {botName || 'Unknown'}
        </dd>
        <dt className="text-muted-foreground">App description</dt>
        <dd
          className="min-w-0 break-words text-foreground"
          data-testid="connection-setup-identity-app-description"
        >
          {appDescription || 'Unknown'}
        </dd>
      </dl>
      <a
        href={slackAppCreationReference}
        target="_blank"
        rel="noreferrer noopener"
        className="inline-flex items-center gap-1.5 rounded-md border border-info/40 bg-info-subtle px-3 py-2 text-sm font-medium text-info hover:underline"
        data-testid="connection-setup-create-in-slack"
      >
        <ExternalLinkIcon className="size-4" />
        Create in Slack
      </a>
      <p
        className="inline-flex items-start gap-1.5 text-xs text-muted-foreground"
        data-testid="connection-setup-identity-avatar-note"
      >
        <InfoIcon className="mt-0.5 size-3.5 shrink-0" />
        <span>
          Configure the avatar manually in the Slack App settings. Mohist does not derive the avatar from
          the Agent.
        </span>
      </p>
    </div>
  )
}
