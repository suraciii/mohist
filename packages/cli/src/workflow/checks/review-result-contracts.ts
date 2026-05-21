import type { ResultContract } from '../../types/workflow-results';
import type { WorkflowItem } from '../../types/workflow-results';

export const PROMISE_PASS = '<promise>PASS</promise>';
export const PROMISE_FAIL = '<promise>FAIL</promise>';
export const PROMISE_MARKERS = [PROMISE_PASS, PROMISE_FAIL] as const;

export const REVIEW_RESULT_CONTRACT: ResultContract = {
  kind: 'marker',
  required: true,
  outputSource: { type: 'artifact', path: 'review.md' },
  allowedMarkers: [...PROMISE_MARKERS],
  verdicts: {
    [PROMISE_PASS]: 'PASS',
    [PROMISE_FAIL]: 'FAIL',
  },
};

export const SELF_REVIEW_RESULT_CONTRACT: ResultContract = {
  kind: 'marker',
  required: true,
  outputSource: { type: 'artifact', path: 'self-review.md' },
  allowedMarkers: [...PROMISE_MARKERS],
  verdicts: {
    [PROMISE_PASS]: 'PASS',
    [PROMISE_FAIL]: 'FAIL',
  },
};

export function parseReviewItems(content: string): WorkflowItem[] {
  const items: WorkflowItem[] = [];
  const lines = content.split('\n');
  let currentItem: Partial<WorkflowItem> | null = null;

  for (const line of lines) {
    const idMatch = line.match(/^-\s+\[ID:\s*([^\]\s]+)/i);
    if (idMatch) {
      if (currentItem && currentItem.id) {
        items.push(currentItem as WorkflowItem);
      }
      currentItem = { id: idMatch[1], evidence: '' };
      continue;
    }

    if (!currentItem) continue;

    const sevMatch = line.match(/^\s*Severity:\s*(\S+)/i);
    if (sevMatch) {
      currentItem.severity = sevMatch[1] as WorkflowItem['severity'];
      continue;
    }

    const scopeMatch = line.match(/^\s*Scope:\s*(.+)/i);
    if (scopeMatch) {
      currentItem.scope = scopeMatch[1].trim();
      continue;
    }

    const evidenceMatch = line.match(/^\s*Evidence:\s*(.+)/i);
    if (evidenceMatch) {
      currentItem.evidence = evidenceMatch[1].trim();
      continue;
    }

    const actionMatch = line.match(/^\s*SuggestedAction:\s*(.+)/i);
    if (actionMatch) {
      currentItem.suggestedAction = actionMatch[1].trim();
      continue;
    }

    const verificationMatch = line.match(/^\s*Verification:\s*(.+)/i);
    if (verificationMatch) {
      currentItem.verification = verificationMatch[1].trim();
      continue;
    }

    const statusMatch = line.match(/^\s*Status:\s*(\S+)/i);
    if (statusMatch) {
      currentItem.status = statusMatch[1] as WorkflowItem['status'];
    }
  }

  if (currentItem && currentItem.id) {
    items.push(currentItem as WorkflowItem);
  }

  return items;
}

export function extractReviewEvidence(content: string, marker: string): string {
  const markerIndex = content.indexOf(marker);
  if (markerIndex === -1) return '';

  const afterMarker = content.slice(markerIndex + marker.length).trim();
  if (!afterMarker) return '';

  const itemStart = afterMarker.search(/^- \[ID:/m);
  if (itemStart > 0) {
    return afterMarker.slice(0, itemStart).trim();
  }

  return '';
}

export function enrichReviewStructuredResult<T extends { marker?: string }>(
  structured: T,
  content: string,
): T & { items?: WorkflowItem[]; evidence?: string } {
  const items = parseReviewItems(content);
  const evidence = structured.marker ? extractReviewEvidence(content, structured.marker) : '';
  return {
    ...structured,
    ...(items.length > 0 ? { items } : {}),
    ...(evidence ? { evidence } : {}),
  };
}
