import type { FailedCheckContext, WorkflowItem, WorkflowSnapshot } from '../workflow-results';

export function buildFailedCheckContext(
  failedCheck: { name: string; status: string; message?: string; output?: unknown },
  priorTaskOutputs?: Record<string, unknown>[],
): FailedCheckContext {
  const output = (failedCheck.output ?? {}) as Record<string, unknown>;
  const structuredResult = output.structuredResult as { verdict?: string; items?: WorkflowItem[]; snapshot?: WorkflowSnapshot } | undefined;
  const verdict = (structuredResult?.verdict ?? output.verdict ?? 'FAIL') as FailedCheckContext['verdict'];
  const allItems = structuredResult?.items ?? [];
  const blockingItems = allItems.filter(item =>
    item.severity === 'blocking' && item.status !== 'resolved' && item.status !== 'pre-existing' && item.status !== 'out-of-scope',
  );
  const nonBlockingItems = allItems.filter(item =>
    item.severity !== 'blocking' || item.status === 'pre-existing' || item.status === 'out-of-scope',
  );
  const sourceArtifactRefs: string[] = [];
  if (typeof output.reviewReport === 'string') sourceArtifactRefs.push('review.md');
  if (typeof output.fixSuggestions === 'string' && output.fixSuggestions.length > 0) {
    const reportRef = sourceArtifactRefs.length > 0 ? sourceArtifactRefs[0] : 'review.md';
    if (!sourceArtifactRefs.includes('fix-suggestions')) sourceArtifactRefs.push(reportRef);
  }
  const snapshot = structuredResult?.snapshot;
  return {
    checkName: failedCheck.name,
    verdict,
    blockingItems,
    nonBlockingItems,
    sourceArtifactRefs: sourceArtifactRefs.length > 0 ? sourceArtifactRefs : undefined,
    snapshot: snapshot ?? (output.snapshotSha ? { sha: output.snapshotSha as string } : undefined),
    priorTaskOutputs: priorTaskOutputs && priorTaskOutputs.length > 0 ? priorTaskOutputs : undefined,
  };
}
