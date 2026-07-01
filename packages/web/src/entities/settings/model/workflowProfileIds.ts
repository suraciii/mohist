export function workflowProfileIdEquals(left: string, right: string) {
  return left.toLowerCase() === right.toLowerCase()
}

export function includesWorkflowProfileId(ids: readonly string[] | undefined, profileId: string) {
  return ids?.some((id) => workflowProfileIdEquals(id, profileId)) ?? false
}
