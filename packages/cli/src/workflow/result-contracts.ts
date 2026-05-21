import type {
  WorkflowVerdict,
  StructuredWorkflowResult,
  ResultContract,
} from '../types/workflow-results';

export type ParseSuccess = {
  ok: true;
  verdict: WorkflowVerdict;
  marker: string;
  rawContent: string;
};

export type ParseError =
  | { ok: false; error: 'source-missing'; source: string }
  | { ok: false; error: 'source-unavailable'; source: string; cause?: string }
  | { ok: false; error: 'no-marker'; source: string }
  | { ok: false; error: 'duplicate-markers'; source: string; markers: string[] };

export type ParseResult = ParseSuccess | ParseError;

function normalizeContent(content: string): string {
  return content.trim();
}

function findMarkerOccurrences(normalized: string, allowedMarkers: readonly string[]): string[] {
  const found: string[] = [];
  for (const marker of allowedMarkers) {
    let pos = 0;
    while ((pos = normalized.indexOf(marker, pos)) !== -1) {
      found.push(marker);
      pos += marker.length;
    }
  }
  return found;
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
  const allowedMarkers = contract.allowedMarkers;
  if (normalized.length === 0) {
    return {
      ok: false,
      error: 'no-marker',
      source: contract.outputSource.type === 'artifact'
        ? contract.outputSource.path
        : contract.outputSource.type,
    };
  }

  const foundMarkers = findMarkerOccurrences(normalized, allowedMarkers);

  if (foundMarkers.length === 0) {
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
  const verdict = contract.verdicts?.[marker] ?? markerToDefaultVerdict(marker);

  return {
    ok: true,
    verdict,
    marker,
    rawContent: sourceContent,
  };
}

export function buildStructuredResult(result: ParseSuccess): StructuredWorkflowResult {
  return {
    verdict: result.verdict,
    marker: result.marker,
  };
}

export function markerContractForPath(
  path: string,
  allowedMarkers: string[],
  verdicts?: Record<string, WorkflowVerdict>,
): ResultContract {
  return {
    kind: 'marker',
    required: true,
    outputSource: { type: 'artifact', path },
    allowedMarkers,
    ...(verdicts ? { verdicts } : {}),
  };
}

export function validateMarkerFile(
  path: string,
  sourceContent: string | null,
  allowedMarkers: string[],
  verdicts?: Record<string, WorkflowVerdict>,
): ParseResult {
  return parseStructuredResult(markerContractForPath(path, allowedMarkers, verdicts), sourceContent);
}

export function isParseError(result: ParseResult): result is ParseError {
  return result.ok === false;
}

export function isParseSuccess(result: ParseResult): result is ParseSuccess {
  return result.ok === true;
}

function markerToDefaultVerdict(marker: string): WorkflowVerdict {
  return marker.toLowerCase().includes('pass') ? 'PASS' : 'FAIL';
}
