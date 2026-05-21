import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { ArtifactMarkerCheck } from '../../../../src/workflow/builtins/checks/artifact-marker-check';
import { registerMohistDefaultMarkerFormats } from '../../../../src/workflow/builtins/workflows/mohist-default';

registerMohistDefaultMarkerFormats();

function makeCheckContext(changeDir: string) {
  return {
    issue: {
      id: 'issue-1',
      number: 1,
      title: 'Test issue',
      stage: 'check' as const,
      status: 'in_progress' as const,
      projectId: 'proj-1',
    } as any,
    changeDir,
    eventBus: { emit: vi.fn() } as any,
    projectId: 'proj-1',
    acpOptions: {},
  };
}

describe('ArtifactMarkerCheck builtin', () => {
  let changeDir: string;

  beforeEach(() => {
    changeDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-artifact-marker-'));
  });

  afterEach(() => {
    fs.rmSync(changeDir, { recursive: true, force: true });
  });

  it('does not modify the review artifact while parsing structured findings', async () => {
    const reviewPath = path.join(changeDir, 'review.md');
    const reviewContent = [
      '<promise>FAIL</promise>',
      '',
      '- [ID: bug-1]',
      '  Severity: blocking',
      '  Evidence: Missing guard',
      '',
      '- [ID: bug-2]',
      '  Severity: blocking',
      '  Evidence: Unused import',
    ].join('\n');
    fs.writeFileSync(reviewPath, reviewContent, 'utf-8');

    const check = new ArtifactMarkerCheck('review-passed', reviewPath, '<promise>PASS</promise>', {
      format: 'mohist/review',
      markers: ['<promise>PASS</promise>', '<promise>FAIL</promise>'],
      verdicts: {
        '<promise>PASS</promise>': 'PASS',
        '<promise>FAIL</promise>': 'FAIL',
      },
    });

    const before = fs.readFileSync(reviewPath, 'utf-8');
    const result = await check.run(makeCheckContext(changeDir));
    const after = fs.readFileSync(reviewPath, 'utf-8');

    expect(result.status).toBe('fail');
    expect(after).toBe(before);
    expect((result.output as any).structuredResult.items).toHaveLength(2);
  });
});
