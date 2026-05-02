import { describe, it, expect } from 'vitest';
import { parseDimensions, parseVerdict, type ParsedDimension } from '../src/workflow';

const REVIEW_ALL_PASS = `# Code Review

## Result: PASS

### Correctness: PASS
All logic paths covered.

### Completeness: PASS
All acceptance criteria met.

### Consistency: PASS
Code style is uniform.

### Robustness: PASS
Error handling is thorough.

### Spec Compliance: PASS
All spec requirements satisfied.

<promise>PASS</promise>
`;

const REVIEW_MIXED = `# Code Review

## Result: FAIL

### Correctness: PASS
All logic paths covered.

### Completeness: FAIL
- Missing error handling for edge case X
- Missing validation for input Y

### Consistency: PASS
Code style is uniform.

### Robustness: FAIL
- No retry logic for transient failures

### Spec Compliance: PASS
All spec requirements satisfied.

<promise>FAIL</promise>
`;

const REVIEW_FAIL_WITH_ISSUES = `# Code Review

## Result: FAIL

### Correctness: FAIL
- Off-by-one error in loop at line 42
- Null pointer dereference when input is empty
- Wrong return type for helper function

### Completeness: FAIL
- Missing error handling for edge case X
- Missing validation for input Y
- Absent unit tests for module Z

### Consistency: PASS
Code style is uniform.

### Robustness: FAIL
- No retry logic for transient failures
- Unbounded retry count may cause infinite loop

### Spec Compliance: FAIL
- Color value should be #E85D3A but used #E85D3B
- Month format should be MMMM but used MMM

<promise>FAIL</promise>
`;

const NO_DIMENSIONS = `# Code Review

## Result: PASS

The implementation looks good overall.
No dimension headers present.
`;

const LEGACY_FORMAT = `# Code Review

## Verdict: PASS

### Correctness: PASS
All logic paths covered.

### Completeness: PASS
All acceptance criteria met.

### Consistency: PASS
Code style is uniform.

### Robustness: PASS
Error handling is thorough.

### Spec Compliance: PASS
All spec requirements satisfied.
`;

const EMPTY_CONTENT = '';

const DIMENSION_NAMES = ['Correctness', 'Completeness', 'Consistency', 'Robustness', 'Spec Compliance'];

describe('parseDimensions', () => {
  it('extracts all 5 dimensions from standard review report format', () => {
    const dims = parseDimensions(REVIEW_ALL_PASS);
    expect(dims).toHaveLength(5);
    const names = dims.map(d => d.name);
    expect(names).toEqual(DIMENSION_NAMES);
    for (const d of dims) {
      expect(d.status).toBe('PASS');
      expect(d.issues).toBeUndefined();
    }
  });

  it('returns empty array for content with no dimension headers', () => {
    const dims = parseDimensions(NO_DIMENSIONS);
    expect(dims).toEqual([]);
  });

  it('returns empty array for empty content', () => {
    const dims = parseDimensions(EMPTY_CONTENT);
    expect(dims).toEqual([]);
  });

  it('handles mixed PASS/FAIL dimensions correctly', () => {
    const dims = parseDimensions(REVIEW_MIXED);
    expect(dims).toHaveLength(5);

    const passDims = dims.filter(d => d.status === 'PASS');
    const failDims = dims.filter(d => d.status === 'FAIL');
    expect(passDims).toHaveLength(3);
    expect(failDims).toHaveLength(2);

    const failNames = failDims.map(d => d.name);
    expect(failNames).toContain('Completeness');
    expect(failNames).toContain('Robustness');
  });

  it('associates bullet-point issues with their FAIL dimension', () => {
    const dims = parseDimensions(REVIEW_FAIL_WITH_ISSUES);

    const correctness = dims.find(d => d.name === 'Correctness')!;
    expect(correctness.status).toBe('FAIL');
    expect(correctness.issues).toBeDefined();
    expect(correctness.issues!).toHaveLength(3);
    expect(correctness.issues![0]).toContain('Off-by-one');

    const completeness = dims.find(d => d.name === 'Completeness')!;
    expect(completeness.status).toBe('FAIL');
    expect(completeness.issues).toBeDefined();
    expect(completeness.issues!).toHaveLength(3);

    const consistency = dims.find(d => d.name === 'Consistency')!;
    expect(consistency.status).toBe('PASS');
    expect(consistency.issues).toBeUndefined();

    const robustness = dims.find(d => d.name === 'Robustness')!;
    expect(robustness.status).toBe('FAIL');
    expect(robustness.issues).toBeDefined();
    expect(robustness.issues!).toHaveLength(2);

    const spec = dims.find(d => d.name === 'Spec Compliance')!;
    expect(spec.status).toBe('FAIL');
    expect(spec.issues).toBeDefined();
    expect(spec.issues!).toHaveLength(2);
    expect(spec.issues![0]).toContain('#E85D3A');
  });

  it('does not include issues property when no bullet points exist', () => {
    const dims = parseDimensions(REVIEW_ALL_PASS);
    for (const d of dims) {
      expect(d.issues).toBeUndefined();
    }
  });

  it('parses legacy format with ## Verdict: header', () => {
    const dims = parseDimensions(LEGACY_FORMAT);
    expect(dims).toHaveLength(5);
    for (const d of dims) {
      expect(d.status).toBe('PASS');
    }
  });

  it('handles content with only a single dimension', () => {
    const content = '### Quality: PASS\n\nLooks good.\n';
    const dims = parseDimensions(content);
    expect(dims).toHaveLength(1);
    expect(dims[0].name).toBe('Quality');
    expect(dims[0].status).toBe('PASS');
  });

  it('handles dimension names with spaces', () => {
    const content = '### Spec Compliance: FAIL\n- Issue A\n';
    const dims = parseDimensions(content);
    expect(dims).toHaveLength(1);
    expect(dims[0].name).toBe('Spec Compliance');
    expect(dims[0].status).toBe('FAIL');
    expect(dims[0].issues).toEqual(['Issue A']);
  });
});

describe('parseVerdict', () => {
  it('returns PASS for <promise>PASS</promise>', () => {
    expect(parseVerdict('<promise>PASS</promise>')).toBe('PASS');
  });

  it('returns FAIL for <promise>FAIL</promise>', () => {
    expect(parseVerdict('<promise>FAIL</promise>')).toBe('FAIL');
  });

  it('is case-insensitive', () => {
    expect(parseVerdict('<PROMISE>pass</PROMISE>')).toBe('PASS');
    expect(parseVerdict('<Promise>Fail</Promise>')).toBe('FAIL');
  });

  it('returns null for content with no verdict', () => {
    expect(parseVerdict('Some random content')).toBeNull();
  });

  it('returns null for empty content', () => {
    expect(parseVerdict('')).toBeNull();
  });

  it('returns null for legacy ## Result: and ## Verdict: formats', () => {
    expect(parseVerdict('## Result: PASS')).toBeNull();
    expect(parseVerdict('## Verdict: FAIL')).toBeNull();
  });
});

describe('backend output enrichment', () => {
  it('review output shape includes verdict and dimensions from parseVerdict + parseDimensions', () => {
    const reviewReport = REVIEW_FAIL_WITH_ISSUES;
    const verdict = parseVerdict(reviewReport);
    const dimensions = parseDimensions(reviewReport);

    const output = {
      stage: 'check',
      issueNumber: 42,
      reviewReport,
      verdict,
      dimensions,
    };

    expect(output.verdict).toBe('FAIL');
    expect(output.dimensions).toHaveLength(5);
    expect(output.dimensions).toEqual(expect.arrayContaining([
      expect.objectContaining({ name: 'Correctness', status: 'FAIL', issues: expect.any(Array) }),
    ]));
    expect(output.reviewReport).toBe(reviewReport);
    expect(output.stage).toBe('check');
    expect(output.issueNumber).toBe(42);
  });

  it('review output shape for PASS verdict has no issues on dimensions', () => {
    const reviewReport = REVIEW_ALL_PASS;
    const verdict = parseVerdict(reviewReport);
    const dimensions = parseDimensions(reviewReport);

    const output = {
      stage: 'check',
      issueNumber: 7,
      reviewReport,
      verdict,
      dimensions,
    };

    expect(output.verdict).toBe('PASS');
    expect(output.dimensions).toHaveLength(5);
    for (const d of output.dimensions) {
      expect(d.status).toBe('PASS');
      expect(d.issues).toBeUndefined();
    }
  });

  it('plan output shape includes verdict but no dimensions', () => {
    const selfReviewNotes = '<promise>PASS</promise>\n\nAll artifacts are complete.';
    const verdict = parseVerdict(selfReviewNotes);

    const output = {
      stage: 'plan',
      issueNumber: 10,
      selfReviewNotes,
      verdict,
      artifacts: [{ name: 'proposal.md', path: 'proposal.md', content: '# Proposal' }],
    };

    expect(output.verdict).toBe('PASS');
    expect((output as any).dimensions).toBeUndefined();
    expect(output.artifacts).toHaveLength(1);
    expect(output.selfReviewNotes).toBe(selfReviewNotes);
  });

  it('output enrichment works with null verdict for malformed content', () => {
    const reviewReport = 'No verdict here';
    const verdict = parseVerdict(reviewReport);
    const dimensions = parseDimensions(reviewReport);

    expect(verdict).toBeNull();
    expect(dimensions).toEqual([]);

    const output = {
      stage: 'check',
      issueNumber: 99,
      reviewReport,
      verdict,
      dimensions,
    };

    expect(output.verdict).toBeNull();
    expect(output.dimensions).toEqual([]);
  });
});
