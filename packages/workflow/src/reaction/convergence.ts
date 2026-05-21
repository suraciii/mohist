import type { ReactionTaskOutput } from '../workflow-results';
import type { StageTaskResult } from '../runtime';

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
