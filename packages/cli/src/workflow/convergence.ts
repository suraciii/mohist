import * as fs from 'fs';
import * as path from 'path';
import type { ReactionTaskOutput, WorkflowConvergenceState, WorkflowItem } from '../types/workflow-results';
import type { CheckResult, StageTaskResult } from './stage-context';
import { Log } from '../util/log';

const log = Log.create({ service: 'convergence' });

const VERIFICATION_CONTEXT_FILE = '.verification-context.json';

export interface VerificationContext {
  knownItemIds: string[];
  resolvedItemIds: string[];
  unresolvedItemIds: string[];
  attemptedItemIds: string[];
  nonBlockingItemIds: string[];
  blockingItemIds: string[];
  failedCheckName: string;
  reactionAttempt: number;
}

export function saveVerificationContext(
  changeDir: string,
  context: VerificationContext,
): void {
  const filePath = path.join(changeDir, VERIFICATION_CONTEXT_FILE);
  try {
    fs.writeFileSync(filePath, JSON.stringify(context, null, 2), 'utf-8');
    log.info('Saved verification context', { changeDir, knownItemIds: context.knownItemIds.length });
  } catch (err) {
    log.warn('Failed to save verification context', {
      changeDir,
      error: err instanceof Error ? err.message : String(err),
    });
  }
}

export function loadVerificationContext(changeDir: string): VerificationContext | null {
  const filePath = path.join(changeDir, VERIFICATION_CONTEXT_FILE);
  try {
    if (!fs.existsSync(filePath)) return null;
    const content = fs.readFileSync(filePath, 'utf-8');
    return JSON.parse(content) as VerificationContext;
  } catch (err) {
    log.warn('Failed to load verification context', {
      changeDir,
      error: err instanceof Error ? err.message : String(err),
    });
    return null;
  }
}

export function clearVerificationContext(changeDir: string): void {
  const filePath = path.join(changeDir, VERIFICATION_CONTEXT_FILE);
  try {
    if (fs.existsSync(filePath)) {
      fs.unlinkSync(filePath);
    }
  } catch (err) {
    log.warn('Failed to clear verification context', {
      changeDir,
      error: err instanceof Error ? err.message : String(err),
    });
  }
}

export function extractReactionOutput(
  taskResult: StageTaskResult,
): ReactionTaskOutput | null {
  const output = taskResult.output;
  if (!output || typeof output !== 'object') return null;

  const data = output as Record<string, unknown>;

  const kind = data.kind;
  if (typeof kind === 'string' && kind === 'agent-session-task') {
    const result = data.result as Record<string, unknown> | undefined;
    if (!result) return extractReactionFields(data);
    return extractReactionFields(result) ?? extractReactionTextFields(result) ?? extractReactionTextFields(data);
  }

  return extractReactionFields(data) ?? extractReactionTextFields(data);
}

function extractReactionFields(
  data: Record<string, unknown>,
): ReactionTaskOutput | null {
  const attemptedItemIds = asStringArray(data.attemptedItemIds);
  const resolvedItemIds = asStringArray(data.resolvedItemIds);
  const unresolvedItemIds = asStringArray(data.unresolvedItemIds);

  if (attemptedItemIds.length === 0 && resolvedItemIds.length === 0 && unresolvedItemIds.length === 0) {
    return null;
  }

  return {
    attemptedItemIds,
    resolvedItemIds,
    unresolvedItemIds,
    newItemIds: asStringArray(data.newItemIds),
    evidence: typeof data.evidence === 'string' ? data.evidence : '',
    summary: typeof data.summary === 'string' ? data.summary : '',
  };
}

function extractReactionTextFields(
  data: Record<string, unknown>,
): ReactionTaskOutput | null {
  const text = firstString(
    data.structuredOutput,
    data.text,
    data.rawText,
    data.finalText,
  );
  if (!text) return null;

  const attemptedItemIds = extractLabeledItemIds(text, 'Attempted Item IDs');
  const resolvedItemIds = extractLabeledItemIds(text, 'Resolved Item IDs');
  const unresolvedItemIds = extractLabeledItemIds(text, 'Unresolved Item IDs');

  if (attemptedItemIds.length === 0 && resolvedItemIds.length === 0 && unresolvedItemIds.length === 0) {
    return null;
  }

  return {
    attemptedItemIds,
    resolvedItemIds,
    unresolvedItemIds,
    newItemIds: extractLabeledItemIds(text, 'New Item IDs'),
    evidence: extractLabeledText(text, 'Evidence') ?? '',
    summary: extractLabeledText(text, 'Summary') ?? '',
  };
}

function extractLabeledItemIds(text: string, label: string): string[] {
  const value = extractLabeledText(text, label);
  if (!value) return [];
  return value
    .split(/[\n,]/)
    .map(part => part.trim())
    .filter(Boolean);
}

function extractLabeledText(text: string, label: string): string | null {
  const escapedLabel = label.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const regex = new RegExp(`(?:^|\\n)${escapedLabel}:\\s*([\\s\\S]*?)(?=\\n[A-Z][A-Za-z ]+:|$)`, 'i');
  const match = text.match(regex);
  return match?.[1]?.trim() || null;
}

function firstString(...values: unknown[]): string | null {
  for (const value of values) {
    if (typeof value === 'string' && value.trim().length > 0) {
      return value;
    }
  }
  return null;
}

function asStringArray(value: unknown): string[] {
  if (!Array.isArray(value)) return [];
  return value.filter((v): v is string => typeof v === 'string');
}

export function buildVerificationContextFromReaction(
  failedCheck: CheckResult,
  reactionOutput: ReactionTaskOutput,
  attempt: number,
): VerificationContext {
  const output = (failedCheck.output ?? {}) as Record<string, unknown>;
  const structuredResult = output.structuredResult as { items?: WorkflowItem[] } | undefined;
  const allItems = structuredResult?.items ?? [];

  const blockingItemIds = allItems
    .filter(item =>
      item.severity === 'blocking' &&
      item.status !== 'resolved' &&
      item.status !== 'pre-existing' &&
      item.status !== 'out-of-scope',
    )
    .map(item => item.id);

  const nonBlockingItemIds = allItems
    .filter(item =>
      item.severity !== 'blocking' ||
      item.status === 'pre-existing' ||
      item.status === 'out-of-scope',
    )
    .map(item => item.id);

  const knownItemIds = [...new Set([...blockingItemIds, ...reactionOutput.attemptedItemIds])];

  return {
    knownItemIds,
    resolvedItemIds: reactionOutput.resolvedItemIds,
    unresolvedItemIds: reactionOutput.unresolvedItemIds,
    attemptedItemIds: reactionOutput.attemptedItemIds,
    nonBlockingItemIds,
    blockingItemIds,
    failedCheckName: failedCheck.name,
    reactionAttempt: attempt,
  };
}

export function buildVerificationPromptSuffix(ctx: VerificationContext): string {
  const lines: string[] = [
    '',
    '## Verification Recheck',
    '',
    'You are re-reviewing after a repair task attempted to fix blocking items.',
    'You MUST verify the resolution status of known items BEFORE evaluating new findings.',
    '',
    `Reaction attempt: ${ctx.reactionAttempt}`,
    '',
    '### Known Items to Verify',
  ];

  for (const id of ctx.knownItemIds) {
    const wasResolved = ctx.resolvedItemIds.includes(id);
    const wasUnresolved = ctx.unresolvedItemIds.includes(id);
    if (wasResolved) {
      lines.push(`- [ID: ${id}] Previously blocking — reaction claims RESOLVED — verify this`);
    } else if (wasUnresolved) {
      lines.push(`- [ID: ${id}] Previously blocking — reaction marks UNRESOLVED — verify still present`);
    } else {
      lines.push(`- [ID: ${id}] Previously blocking — verify current status`);
    }
  }

  if (ctx.nonBlockingItemIds.length > 0) {
    lines.push('');
    lines.push('### Non-Blocking Items (do not block)');
    for (const id of ctx.nonBlockingItemIds) {
      lines.push(`- [ID: ${id}] Non-blocking / follow-up / out-of-scope`);
    }
  }

  lines.push('');
  lines.push('### Verification Rules');
  lines.push('- Verify that each resolved item is actually fixed in the current code.');
  lines.push('- If a resolved item is NOT actually fixed, report it as blocking with its original ID.');
  lines.push('- Only report NEW blockers if they are regressions from the fix, missed acceptance criteria, or serious safety/data risks.');
  lines.push('- Non-blocking follow-up and out-of-scope items remain visible but do NOT block.');
  lines.push('- Your verdict must be PASS only if all previously blocking items are resolved and no policy-allowed new blockers remain.');

  return lines.join('\n');
}

export function computeConvergenceState(
  failedCheck: CheckResult | undefined,
  reactionOutputs: ReactionTaskOutput[],
  verificationCheckResult: CheckResult | undefined,
): WorkflowConvergenceState {
  const blockingItems = extractBlockingItemsFromCheck(failedCheck);
  const nonBlockingItems = extractNonBlockingItemsFromCheck(failedCheck);

  const directlyRepairedCount = extractDirectlyRepairedCount(failedCheck);

  const attemptedItemIds = reactionOutputs.flatMap(r => r.attemptedItemIds);
  const resolvedItemIds = reactionOutputs.flatMap(r => r.resolvedItemIds);
  const unresolvedItemIds = reactionOutputs.flatMap(r => r.unresolvedItemIds);

  const verificationBlockingItems = extractBlockingItemsFromCheck(verificationCheckResult);
  const newBlockingItemIds = verificationBlockingItems
    .map(item => item.id)
    .filter(id => !attemptedItemIds.includes(id) && !blockingItems.some(bi => bi.id === id));

  let blockedReason: string | undefined;
  if (failedCheck && failedCheck.status !== 'pass') {
    const unresolved = unresolvedItemIds.length;
    const newBlockers = newBlockingItemIds.length;
    if (unresolved > 0 && newBlockers > 0) {
      blockedReason = `${unresolved} unresolved items and ${newBlockers} new blockers from verification`;
    } else if (unresolved > 0) {
      blockedReason = `${unresolved} unresolved items from reaction`;
    } else if (newBlockers > 0) {
      blockedReason = `${newBlockers} new blockers found during verification`;
    }
  }

  return {
    failedCheck: failedCheck?.name,
    blockingItemCount: blockingItems.length,
    directlyRepairedCount,
    reactionAttempts: reactionOutputs.length,
    attemptedItemIds: [...new Set(attemptedItemIds)],
    resolvedItemIds: [...new Set(resolvedItemIds)],
    unresolvedItemIds: [...new Set(unresolvedItemIds)],
    newBlockingItemIds: [...new Set(newBlockingItemIds)],
    nonBlockingItemIds: nonBlockingItems.map(item => item.id),
    blockedReason,
  };
}

function extractBlockingItemsFromCheck(check: CheckResult | undefined): WorkflowItem[] {
  if (!check?.output || typeof check.output !== 'object') return [];
  const output = check.output as Record<string, unknown>;
  const structuredResult = output.structuredResult as { items?: WorkflowItem[] } | undefined;
  const items = structuredResult?.items ?? [];
  return items.filter(item =>
    item.severity === 'blocking' &&
    item.status !== 'resolved' &&
    item.status !== 'pre-existing' &&
    item.status !== 'out-of-scope',
  );
}

function extractNonBlockingItemsFromCheck(check: CheckResult | undefined): WorkflowItem[] {
  if (!check?.output || typeof check.output !== 'object') return [];
  const output = check.output as Record<string, unknown>;
  const structuredResult = output.structuredResult as { items?: WorkflowItem[] } | undefined;
  const items = structuredResult?.items ?? [];
  return items.filter(item =>
    item.severity !== 'blocking' ||
    (item.status != null && ['pre-existing', 'out-of-scope', 'resolved'].includes(item.status)),
  );
}

function extractDirectlyRepairedCount(check: CheckResult | undefined): number {
  if (!check?.output || typeof check.output !== 'object') return 0;
  const output = check.output as Record<string, unknown>;
  const structuredResult = output.structuredResult as { repairedItemIds?: string[] } | undefined;
  return structuredResult?.repairedItemIds?.length ?? 0;
}
