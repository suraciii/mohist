/**
 * Human step names for the durable setup phases. Mohist creates the App and
 * applies the manifest itself, so no phase asks the user to do that work, and
 * Server-internal protocol steps never appear as a step name.
 */
const SETUP_PROGRESS_LABELS: Record<string, string> = {
  create_app_credentials: 'Approve install in Slack',
  waiting_for_slack_service: 'Waiting for verification',
  fix_slack_setup: 'Fix Slack setup',
  claim_owner: 'Claim owner',
  complete: 'Complete',
}

export function setupProgressLabel(value: string | null | undefined): string {
  if (!value) return 'Unknown'
  return SETUP_PROGRESS_LABELS[value] ?? value.replaceAll('_', ' ')
}
