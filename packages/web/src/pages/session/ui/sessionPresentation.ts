export function getStageLabel(stage: string | null): string {
  if (!stage) return 'Session'
  return stage.charAt(0).toUpperCase() + stage.slice(1)
}

export function sessionTimeAnchorMs(meta: import('../../../entities/coder-session').SessionMetadata): number | null {
  return meta.lastActivityAt ? new Date(meta.lastActivityAt).getTime() : null
}
