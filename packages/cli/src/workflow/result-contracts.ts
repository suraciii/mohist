import type {
  WorkflowVerdict,
  WorkflowItem,
  StructuredWorkflowResult,
  ResultContract,
} from '../types/workflow-results';

export const PROMISE_PASS = '<promise>PASS</promise>';
export const PROMISE_FAIL = '<promise>FAIL</promise>';
export const PROMISE_MARKERS = [PROMISE_PASS, PROMISE_FAIL] as const;

export type PromiseMarker = typeof PROMISE_MARKERS[number];

export type ParseSuccess = {
  ok: true;
  verdict: WorkflowVerdict;
  marker: PromiseMarker;
  items: WorkflowItem[];
  evidence: string;
  rawContent: string;
};

export type ParseError =
  | { ok: false; error: 'source-missing'; source: string }
  | { ok: false; error: 'source-unavailable'; source: string; cause?: string }
  | { ok: false; error: 'no-marker'; source: string }
  | { ok: false; error: 'duplicate-markers'; source: string; markers: PromiseMarker[] }
  | { ok: false; error: 'malformed-marker'; source: string; raw: string };

export type ParseResult = ParseSuccess | ParseError;

function normalizeContent(content: string): string {
  return content.trim();
}

function findPromiseMarkerOccurrences(normalized: string): PromiseMarker[] {
  const found: PromiseMarker[] = [];
  for (const marker of PROMISE_MARKERS) {
    let pos = 0;
    while ((pos = normalized.indexOf(marker, pos)) !== -1) {
      found.push(marker);
      pos += marker.length;
    }
  }
  return found;
}

function findMalformedPromises(normalized: string): string[] {
  const malformed: string[] = [];
  const regex = /<promise>([\s\S]*?)<\/promise>/gi;
  let match: RegExpExecArray | null;
  while ((match = regex.exec(normalized)) !== null) {
    const inner = match[1].trim().toUpperCase();
    if (inner !== 'PASS' && inner !== 'FAIL') {
      malformed.push(match[0]);
    }
  }
  return malformed;
}

function parseStructuredItems(content: string): WorkflowItem[] {
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

    if (currentItem) {
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
        continue;
      }
    }
  }

  if (currentItem && currentItem.id) {
    items.push(currentItem as WorkflowItem);
  }

  return items;
}

function extractEvidence(content: string, marker: PromiseMarker): string {
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

export function parseStructuredResult(
  contract: ResultContract,
  sourceContent: string | null
): ParseResult {
  if (sourceContent === null) {
    return {
      ok: false,
      error: 'source-missing',
      source: contract.outputSource.type === 'artifact'
        ? contract.outputSource.path
        : contract.outputSource.type,
    };
  }

  const normalized = normalizeContent(sourceContent);
  if (normalized.length === 0) {
    return {
      ok: false,
      error: 'no-marker',
      source: contract.outputSource.type === 'artifact'
        ? contract.outputSource.path
        : contract.outputSource.type,
    };
  }

  const foundMarkers = findPromiseMarkerOccurrences(normalized);

  if (foundMarkers.length === 0) {
    const malformed = findMalformedPromises(normalized);
    if (malformed.length > 0) {
      return {
        ok: false,
        error: 'malformed-marker',
        source: contract.outputSource.type === 'artifact'
          ? contract.outputSource.path
          : contract.outputSource.type,
        raw: malformed[0],
      };
    }
    return {
      ok: false,
      error: 'no-marker',
      source: contract.outputSource.type === 'artifact'
        ? contract.outputSource.path
        : contract.outputSource.type,
    };
  }

  if (foundMarkers.length > 1) {
    return {
      ok: false,
      error: 'duplicate-markers',
      source: contract.outputSource.type === 'artifact'
        ? contract.outputSource.path
        : contract.outputSource.type,
      markers: foundMarkers,
    };
  }

  const marker = foundMarkers[0];

  const markerLower = marker.toLowerCase();
  const verdict: WorkflowVerdict = markerLower.includes('pass') ? 'PASS' : 'FAIL';

  const items = parseStructuredItems(normalized);
  const evidence = extractEvidence(normalized, marker);

  return {
    ok: true,
    verdict,
    marker,
    items,
    evidence,
    rawContent: sourceContent,
  };
}

export function buildStructuredResult(result: ParseSuccess): StructuredWorkflowResult {
  return {
    verdict: result.verdict,
    marker: result.marker,
    items: result.items.length > 0 ? result.items : undefined,
    evidence: result.evidence || undefined,
  };
}

export function isParseError(result: ParseResult): result is ParseError {
  return result.ok === false;
}

export function isParseSuccess(result: ParseResult): result is ParseSuccess {
  return result.ok === true;
}