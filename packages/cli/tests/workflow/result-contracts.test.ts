import { describe, it, expect } from 'vitest';
import {
  parseStructuredResult,
  buildStructuredResult,
  isParseError,
  isParseSuccess,
  PROMISE_PASS,
  PROMISE_FAIL,
} from '../../src/workflow/result-contracts';
import type { ResultContract } from '../../src/types/workflow-results';

function makeContract(path = 'review.md'): ResultContract {
  return {
    kind: 'promise-marker',
    required: true,
    outputSource: { type: 'artifact', path },
    allowedMarkers: [PROMISE_PASS, PROMISE_FAIL],
  };
}

describe('result-contracts: parseStructuredResult', () => {
  describe('PASS marker', () => {
    it('parses declared artifact with exactly one PASS to verdict PASS and returns same marker', () => {
      const content = `# Review\n\n<promise>PASS</promise>\n\nAll checks passed.`;
      const result = parseStructuredResult(makeContract(), content);

      expect(isParseSuccess(result)).toBe(true);
      if (isParseSuccess(result)) {
        expect(result.verdict).toBe('PASS');
        expect(result.marker).toBe('<promise>PASS</promise>');
        expect(result.items).toEqual([]);
      }
    });

    it('parses PASS with structured items', () => {
      const content = `# Review\n<promise>PASS</promise>\n\n- [ID: item-1] Fixed in session\n  Severity: info\n  Status: resolved`;
      const result = parseStructuredResult(makeContract(), content);

      expect(isParseSuccess(result)).toBe(true);
      if (isParseSuccess(result)) {
        expect(result.verdict).toBe('PASS');
        expect(result.items).toHaveLength(1);
        expect(result.items[0].id).toBe('item-1');
        expect(result.items[0].severity).toBe('info');
        expect(result.items[0].status).toBe('resolved');
      }
    });
  });

  describe('FAIL marker', () => {
    it('parses declared artifact with exactly one FAIL to verdict FAIL and returns same marker', () => {
      const content = `# Review\n\n<promise>FAIL</promise>\n\n### Blocking Issues\n- Error handling missing`;
      const result = parseStructuredResult(makeContract(), content);

      expect(isParseSuccess(result)).toBe(true);
      if (isParseSuccess(result)) {
        expect(result.verdict).toBe('FAIL');
        expect(result.marker).toBe('<promise>FAIL</promise>');
      }
    });

    it('parses FAIL with structured blocking items', () => {
      const content = `# Review\n<promise>FAIL</promise>\n\n- [ID: bug-1]\n  Severity: blocking\n  Evidence: Null reference on line 42\n  SuggestedAction: Add null guard\n  Verification: Run tests`;
      const result = parseStructuredResult(makeContract(), content);

      expect(isParseSuccess(result)).toBe(true);
      if (isParseSuccess(result)) {
        expect(result.verdict).toBe('FAIL');
        expect(result.items).toHaveLength(1);
        expect(result.items[0].id).toBe('bug-1');
        expect(result.items[0].severity).toBe('blocking');
        expect(result.items[0].evidence).toBe('Null reference on line 42');
        expect(result.items[0].suggestedAction).toBe('Add null guard');
        expect(result.items[0].verification).toBe('Run tests');
      }
    });
  });

  describe('missing marker errors', () => {
    it('returns source-missing error when content is null', () => {
      const result = parseStructuredResult(makeContract(), null);

      expect(isParseError(result)).toBe(true);
      if (isParseError(result)) {
        expect(result.error).toBe('source-missing');
        expect(result.source).toBe('review.md');
      }
    });

    it('returns no-marker error when content is empty string', () => {
      const result = parseStructuredResult(makeContract(), '');

      expect(isParseError(result)).toBe(true);
      if (isParseError(result)) {
        expect(result.error).toBe('no-marker');
        expect(result.source).toBe('review.md');
      }
    });

    it('returns no-marker error when content has no promise marker', () => {
      const content = `# Review\n\nAll checks passed. No explicit verdict.`;
      const result = parseStructuredResult(makeContract(), content);

      expect(isParseError(result)).toBe(true);
      if (isParseError(result)) {
        expect(result.error).toBe('no-marker');
      }
    });
  });

  describe('duplicate marker errors', () => {
    it('returns duplicate-markers error when both PASS and FAIL appear', () => {
      const content = `# Review\n<promise>PASS</promise>\n\nLater we decided: <promise>FAIL</promise>`;
      const result = parseStructuredResult(makeContract(), content);

      expect(isParseError(result)).toBe(true);
      if (isParseError(result)) {
        expect(result.error).toBe('duplicate-markers');
        expect(result.markers).toContain('<promise>PASS</promise>');
        expect(result.markers).toContain('<promise>FAIL</promise>');
      }
    });

    it('returns duplicate-markers error when PASS appears twice', () => {
      const content = `<promise>PASS</promise>\nSome text\n<promise>PASS</promise>`;
      const result = parseStructuredResult(makeContract(), content);

      expect(isParseError(result)).toBe(true);
      if (isParseError(result)) {
        expect(result.error).toBe('duplicate-markers');
        expect(result.markers).toHaveLength(2);
      }
    });

    it('returns duplicate-markers error when FAIL appears twice', () => {
      const content = `<promise>FAIL</promise>\nSome text\n<promise>FAIL</promise>`;
      const result = parseStructuredResult(makeContract(), content);

      expect(isParseError(result)).toBe(true);
      if (isParseError(result)) {
        expect(result.error).toBe('duplicate-markers');
      }
    });
  });

  describe('malformed marker errors', () => {
    it('returns no-marker error for malformed promise tag', () => {
      const content = `# Review\n<promise PA>S\nSome content`;
      const result = parseStructuredResult(makeContract(), content);

      expect(isParseError(result)).toBe(true);
      if (isParseError(result)) {
        expect(result.error).toBe('no-marker');
      }
    });

    it('returns malformed-marker error for broken promise tag', () => {
      const content = `<promise>PARTIAL</promise>`;
      const result = parseStructuredResult(makeContract(), content);

      expect(isParseError(result)).toBe(true);
      if (isParseError(result)) {
        expect(result.error).toBe('malformed-marker');
        if (result.error === 'malformed-marker') {
          expect(result.raw).toBe('<promise>PARTIAL</promise>');
        }
      }
    });
  });

  describe('source binding', () => {
    it('uses artifact path as source in error messages', () => {
      const result = parseStructuredResult(makeContract('self-review.md'), null);

      expect(isParseError(result)).toBe(true);
      if (isParseError(result)) {
        expect(result.source).toBe('self-review.md');
      }
    });

    it('uses task-output type as source for non-artifact sources', () => {
      const contract: ResultContract = {
        kind: 'promise-marker',
        required: true,
        outputSource: { type: 'task-output', key: 'structuredResult' },
        allowedMarkers: [PROMISE_PASS, PROMISE_FAIL],
      };
      const result = parseStructuredResult(contract, null);

      expect(isParseError(result)).toBe(true);
      if (isParseError(result)) {
        expect(result.source).toBe('task-output');
      }
    });

    it('markers in unrelated content are ignored when declared source has none', () => {
      const content = `Logs show <promise>PASS</promise> and <promise>FAIL</promise> in the transcript`;
      const result = parseStructuredResult(makeContract(), content);

      expect(isParseError(result)).toBe(true);
      if (isParseError(result)) {
        expect(result.error).toBe('duplicate-markers');
      }
    });

    it('only parses the provided declared source content, not external sources', () => {
      const declaredSourceContent = '# Review\n\nAll good, no issues found.';
      const result = parseStructuredResult(makeContract('review.md'), declaredSourceContent);

      expect(isParseError(result)).toBe(true);
      if (isParseError(result)) {
        expect(result.error).toBe('no-marker');
        expect(result.source).toBe('review.md');
      }
    });

    it('whitespace-only content is treated as no-marker', () => {
      const result = parseStructuredResult(makeContract(), '   \n\t  \n  ');

      expect(isParseError(result)).toBe(true);
      if (isParseError(result)) {
        expect(result.error).toBe('no-marker');
      }
    });
  });

  describe('item extraction', () => {
    it('extracts multiple items with all fields', () => {
      const content = `# Review\n<promise>FAIL</promise>\n\n- [ID: item-1]\n  Severity: blocking\n  Scope: src/api.ts\n  Evidence: Missing error handling\n  SuggestedAction: Add try-catch\n  Verification: Run integration tests\n  Status: open\n\n- [ID: item-2]\n  Severity: warning\n  Scope: src/utils.ts\n  Evidence: Unused import\n  Status: follow-up`;
      const result = parseStructuredResult(makeContract(), content);

      expect(isParseSuccess(result)).toBe(true);
      if (isParseSuccess(result)) {
        expect(result.items).toHaveLength(2);
        expect(result.items[0].id).toBe('item-1');
        expect(result.items[0].severity).toBe('blocking');
        expect(result.items[0].scope).toBe('src/api.ts');
        expect(result.items[0].status).toBe('open');
        expect(result.items[1].id).toBe('item-2');
        expect(result.items[1].severity).toBe('warning');
        expect(result.items[1].status).toBe('follow-up');
      }
    });

    it('extracts items with minimal fields', () => {
      const content = `<promise>FAIL</promise>\n\n- [ID: minimal]\n  Evidence: Something is wrong`;
      const result = parseStructuredResult(makeContract(), content);

      expect(isParseSuccess(result)).toBe(true);
      if (isParseSuccess(result)) {
        expect(result.items).toHaveLength(1);
        expect(result.items[0].id).toBe('minimal');
        expect(result.items[0].evidence).toBe('Something is wrong');
      }
    });
  });

  describe('evidence extraction', () => {
    it('extracts evidence text between marker and items', () => {
      const content = `<promise>FAIL</promise>\n\nCritical security issues found.\n\n- [ID: sec-1]\n  Severity: blocking\n  Evidence: SQL injection`;
      const result = parseStructuredResult(makeContract(), content);

      expect(isParseSuccess(result)).toBe(true);
      if (isParseSuccess(result)) {
        expect(result.evidence).toBe('Critical security issues found.');
      }
    });

    it('returns empty evidence when marker is immediately followed by items', () => {
      const content = `<promise>FAIL</promise>\n\n- [ID: bug-1]\n  Severity: blocking`;
      const result = parseStructuredResult(makeContract(), content);

      expect(isParseSuccess(result)).toBe(true);
      if (isParseSuccess(result)) {
        expect(result.evidence).toBe('');
      }
    });
  });

  describe('buildStructuredResult', () => {
    it('builds StructuredWorkflowResult from parse success', () => {
      const content = `# Review\n<promise>PASS</promise>\n\n- [ID: fixed-1]\n  Severity: info\n  Status: resolved`;
      const parseResult = parseStructuredResult(makeContract(), content);

      expect(isParseSuccess(parseResult)).toBe(true);
      if (isParseSuccess(parseResult)) {
        const structured = buildStructuredResult(parseResult);
        expect(structured.verdict).toBe('PASS');
        expect(structured.marker).toBe('<promise>PASS</promise>');
        expect(structured.items).toHaveLength(1);
        expect(structured.evidence).toBeUndefined();
      }
    });

    it('omits items array when no items extracted', () => {
      const content = `<promise>PASS</promise>`;
      const parseResult = parseStructuredResult(makeContract(), content);

      expect(isParseSuccess(parseResult)).toBe(true);
      if (isParseSuccess(parseResult)) {
        const structured = buildStructuredResult(parseResult);
        expect(structured.items).toBeUndefined();
      }
    });
  });
});