import type {
  SelfRepairPolicy,
  WorkflowItem,
  WorkflowItemSeverity,
  WorkflowVerification,
} from '../../types/workflow-results';
import { parseStructuredResult, isParseSuccess } from '../result-contracts';
import type { ResultContract } from '../../types/workflow-results';

const NON_BLOCKING_SEVERITIES: WorkflowItemSeverity[] = ['follow-up', 'info'];

export interface SelfRepairResult {
  repairedItemIds: string[];
  repairedItems: WorkflowItem[];
  unresolvedItems: WorkflowItem[];
  allItems: WorkflowItem[];
  verification: WorkflowVerification[];
  hadRepairs: boolean;
  postRepairVerdict: 'PASS' | 'FAIL' | null;
}

export function extractRepairResultFromArtifact(
  contract: ResultContract,
  artifactContent: string | null,
  _policy: SelfRepairPolicy,
): SelfRepairResult {
  const empty: SelfRepairResult = {
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

  const allItems = parsed.items;
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

  const verificationLines = extractVerificationFromItems(repairedItems);

  return {
    repairedItemIds,
    repairedItems,
    unresolvedItems,
    allItems,
    verification: verificationLines,
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

export function isRepairAllowed(
  policy: SelfRepairPolicy,
  item: WorkflowItem,
): { allowed: boolean; reason?: string } {
  if (!policy.enabled) {
    return { allowed: false, reason: 'Self-repair is disabled' };
  }

  for (const disallowed of policy.disallowedReasons) {
    const tag = `[disallowed:${disallowed}]`;
    if (item.evidence?.includes(tag) || item.suggestedAction?.includes(tag)) {
      return { allowed: false, reason: `Item marked as disallowed: ${disallowed}` };
    }
  }

  if (policy.allowedScopes.length > 0) {
    const itemScopes = (item.scope ?? '').split(',').map(s => s.trim()).filter(Boolean);
    const hasAllowedScope = itemScopes.some(s => policy.allowedScopes.includes(s));
    if (itemScopes.length > 0 && !hasAllowedScope) {
      return { allowed: false, reason: `Item scope not in allowed scopes: ${item.scope}` };
    }
  }

  return { allowed: true };
}
