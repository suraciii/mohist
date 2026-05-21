import { describe, expect, it } from 'vitest';
import {
  buildStructuredResult,
  isParseError,
  isParseSuccess,
  parseStructuredResult,
} from '../../src/workflow/result-contracts';
import type { ResultContract } from '../../src/types/workflow-results';

function makeContract(path = 'result.txt'): ResultContract {
  return {
    kind: 'marker',
    required: true,
    outputSource: { type: 'artifact', path },
    allowedMarkers: ['<ok/>', '<failed/>'],
    verdicts: {
      '<ok/>': 'PASS',
      '<failed/>': 'FAIL',
    },
  };
}

describe('result-contracts: marker parsing', () => {
  it('parses the single declared success marker', () => {
    const result = parseStructuredResult(makeContract(), 'done\n<ok/>\n');

    expect(isParseSuccess(result)).toBe(true);
    if (isParseSuccess(result)) {
      expect(result.verdict).toBe('PASS');
      expect(result.marker).toBe('<ok/>');
    }
  });

  it('parses the single declared failure marker', () => {
    const result = parseStructuredResult(makeContract(), 'done\n<failed/>\n');

    expect(isParseSuccess(result)).toBe(true);
    if (isParseSuccess(result)) {
      expect(result.verdict).toBe('FAIL');
      expect(result.marker).toBe('<failed/>');
    }
  });

  it('returns source-missing when content is null', () => {
    const result = parseStructuredResult(makeContract('missing.txt'), null);

    expect(isParseError(result)).toBe(true);
    if (isParseError(result)) {
      expect(result.error).toBe('source-missing');
      expect(result.source).toBe('missing.txt');
    }
  });

  it('returns no-marker for empty or markerless content', () => {
    const empty = parseStructuredResult(makeContract(), '');
    const markerless = parseStructuredResult(makeContract(), 'all good');

    expect(isParseError(empty)).toBe(true);
    expect(isParseError(markerless)).toBe(true);
    if (isParseError(empty)) expect(empty.error).toBe('no-marker');
    if (isParseError(markerless)) expect(markerless.error).toBe('no-marker');
  });

  it('fails when more than one declared marker is present', () => {
    const result = parseStructuredResult(makeContract(), '<ok/>\n<failed/>');

    expect(isParseError(result)).toBe(true);
    if (isParseError(result)) {
      expect(result.error).toBe('duplicate-markers');
      expect(result.markers).toEqual(['<ok/>', '<failed/>']);
    }
  });

  it('treats undeclared marker-like text as no marker', () => {
    const contract: ResultContract = {
      kind: 'marker',
      required: true,
      outputSource: { type: 'artifact', path: 'review.md' },
      allowedMarkers: ['<promise>PASS</promise>', '<promise>FAIL</promise>'],
      verdicts: {
        '<promise>PASS</promise>': 'PASS',
        '<promise>FAIL</promise>': 'FAIL',
      },
    };

    const result = parseStructuredResult(contract, '<promise>MAYBE</promise>');

    expect(isParseError(result)).toBe(true);
    if (isParseError(result)) {
      expect(result.error).toBe('no-marker');
    }
  });

  it('uses task-output as source for non-artifact sources', () => {
    const contract: ResultContract = {
      ...makeContract(),
      outputSource: { type: 'task-output', key: 'structuredResult' },
    };
    const result = parseStructuredResult(contract, null);

    expect(isParseError(result)).toBe(true);
    if (isParseError(result)) expect(result.source).toBe('task-output');
  });

  it('builds a generic structured result without endpoint-specific items', () => {
    const parsed = parseStructuredResult(makeContract(), '<ok/>');

    expect(isParseSuccess(parsed)).toBe(true);
    if (isParseSuccess(parsed)) {
      expect(buildStructuredResult(parsed)).toEqual({
        verdict: 'PASS',
        marker: '<ok/>',
      });
    }
  });
});
