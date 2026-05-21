import type { ResultContract, WorkflowItem, WorkflowItemSeverity, WorkflowVerification } from '../../../../types/workflow-results';
import { isParseSuccess, parseStructuredResult } from '../../../result-contracts';
import { parseReviewItems } from './review-contracts';

const NON_BLOCKING_SEVERITIES: WorkflowItemSeverity[] = ['follow-up', 'info'];

export interface StructuredResultMetadata {
  repairedItemIds: string[];
  repairedItems: WorkflowItem[];
  unresolvedItems: WorkflowItem[];
  allItems: WorkflowItem[];
  verification: WorkflowVerification[];
  hadRepairs: boolean;
  postRepairVerdict: 'PASS' | 'FAIL' | null;
}

export function extractStructuredResultMetadata(
  contract: ResultContract,
  artifactContent: string | null,
): StructuredResultMetadata {
  const empty: StructuredResultMetadata = {
    repairedItemIds: [],
    repairedItems: [],
    unresolvedItems: [],
    allItems: [],
    verification: [],
    hadRepairs: false,
    postRepairVerdict: null,
  };

  if (artifactContent === null) {
    return empty;
  }

  const parsed = parseStructuredResult(contract, artifactContent);
  if (!isParseSuccess(parsed)) {
    return empty;
  }

  const allItems = parseReviewItems(artifactContent);
  const repairedItems = allItems.filter(
    item => item.status === 'resolved' && item.verification != null && item.verification.length > 0,
  );
  const repairedItemIds = repairedItems.map(item => item.id);

  const nonBlockingStatuses = new Set(['pre-existing', 'out-of-scope']);
  const unresolvedItems = allItems.filter(item => {
    if (item.status && nonBlockingStatuses.has(item.status)) return false;
    if (NON_BLOCKING_SEVERITIES.includes(item.severity)) return false;
    if (item.status === 'resolved') return false;
    return true;
  });

  const verification = extractVerificationFromItems(repairedItems);

  return {
    repairedItemIds,
    repairedItems,
    unresolvedItems,
    allItems,
    verification,
    hadRepairs: repairedItemIds.length > 0,
    postRepairVerdict: parsed.verdict,
  };
}

function extractVerificationFromItems(items: WorkflowItem[]): WorkflowVerification[] {
  const verifications: WorkflowVerification[] = [];
  for (const item of items) {
    if (item.verification) {
      verifications.push({
        checkName: `repair:${item.id}`,
        status: 'pass',
        command: item.verification,
        duration: 0,
        summary: `Verified repair for ${item.id}`,
        logExcerpt: '',
        checkedAt: new Date().toISOString(),
      });
    }
  }
  return verifications;
}
