import { describe, it, expect } from 'vitest';
import fs from 'fs';
import path from 'path';

const PROMPTS_DIR = path.resolve(__dirname, '../../src/agents/prompts');
const ARTIFACTS_DIR = path.join(PROMPTS_DIR, 'artifacts');

function readPrompt(filename: string): string {
  const filePath = path.join(PROMPTS_DIR, filename);
  return fs.readFileSync(filePath, 'utf-8');
}

function readArtifact(filename: string): string {
  const filePath = path.join(ARTIFACTS_DIR, filename);
  return fs.readFileSync(filePath, 'utf-8');
}

describe('review.md prompt structure', () => {
  const content = readPrompt('review.md');

  it('requires comprehensive pass and instructs not to stop after first blocker', () => {
    expect(content).toContain('comprehensive review pass');
    expect(content).toContain('Do NOT stop after finding the first blocker');
  });

  it('includes structured item groups for repaired, blocking, follow-up, and pre-existing/out-of-scope items', () => {
    expect(content).toContain('## Repaired Items');
    expect(content).toContain('## Blocking Items');
    expect(content).toContain('## Follow-up Items');
    expect(content).toContain('## Pre-existing or Out-of-scope Items');
  });

  it('requires evidence, suggestedAction, verification, stable IDs, and severity for each item', () => {
    expect(content).toContain('[ID: item-N]');
    expect(content).toMatch(/Severity:\s*(blocking|info|follow-up|warning)/);
    expect(content).toContain('Evidence:');
    expect(content).toContain('SuggestedAction:');
    expect(content).toContain('Verification:');
    expect(content).toContain('Status:');
  });

  it('requires exactly one promise marker', () => {
    expect(content).toContain('exactly one of these tags');
    expect(content).toContain('<promise>PASS</promise>');
    expect(content).toContain('<promise>FAIL</promise>');
  });

  it('treats review.md as a fresh latest report produced by the task', () => {
    expect(content).toContain('Report Lifecycle');
    expect(content).toContain('Produce a fresh `review.md`');
    expect(content).toContain('removes any prior `review.md` before this task starts');
    expect(content).toContain('Do not rely on old review verdicts or stale report content');
  });

  it('emits follow-up and out-of-scope items as visible non-blocking items', () => {
    expect(content).toContain('do not prevent PASS');
    expect(content).toMatch(/Status: follow-up/);
    expect(content).toMatch(/Status: pre-existing \| out-of-scope/);
  });

  it('includes in-session repair policy', () => {
    expect(content).toContain('In-Session Repair Policy');
    expect(content).toContain('MUST NOT repair');
    expect(content).toContain('[disallowed:reason]');
  });

  it('requires post-repair verdict', () => {
    expect(content).toContain('POST-REPAIR candidate snapshot');
    expect(content).toContain('MUST NOT produce PASS if any repaired item lacks verification');
  });

  it('requires item field documentation', () => {
    expect(content).toMatch(/Every reported item MUST include/);
    expect(content).toContain('ID');
    expect(content).toContain('Severity');
    expect(content).toContain('Evidence');
    expect(content).toContain('Status');
  });
});

describe('self-review.md artifact prompt structure', () => {
  const content = readArtifact('self-review.md');

  it('uses the same generic structured verdict contract and marker requirement', () => {
    expect(content).toContain('exactly one of these tags');
    expect(content).toContain('<promise>PASS</promise>');
    expect(content).toContain('<promise>FAIL</promise>');
  });

  it('includes structured item groups matching the review contract', () => {
    expect(content).toContain('## Repaired Items');
    expect(content).toContain('## Blocking Items');
    expect(content).toContain('## Follow-up Items');
  });

  it('requires item fields: ID, Severity, Evidence, Status', () => {
    expect(content).toContain('[ID: item-N]');
    expect(content).toMatch(/Severity:/);
    expect(content).toMatch(/Evidence:/);
    expect(content).toMatch(/Status:/);
  });

  it('requires SuggestedAction for blocking items', () => {
    expect(content).toMatch(/SuggestedAction:/);
  });

  it('restricts verdict marker placement to the end', () => {
    expect(content).toContain('on its own line at the end');
    expect(content).toContain('Do NOT include more than one marker');
  });
});

describe('review-self-check.md prompt structure', () => {
  const content = readArtifact('review-self-check.md');

  it('verifies the structured output format', () => {
    expect(content).toContain('## Result: PASS');
    expect(content).toContain('## Result: FAIL');
    expect(content).toContain('<promise>PASS</promise>');
    expect(content).toContain('<promise>FAIL</promise>');
  });

  it('verifies structured item fields', () => {
    expect(content).toContain('stable ID');
    expect(content).toContain('Severity field');
    expect(content).toContain('Evidence field');
    expect(content).toContain('Status field');
    expect(content).toContain('Verification');
  });

  it('verifies exactly one marker constraint', () => {
    expect(content).toContain('exactly one');
    expect(content).toContain('No duplicate');
  });

  it('verifies all item groups', () => {
    expect(content).toContain('Repaired Items');
    expect(content).toContain('Blocking Items');
    expect(content).toContain('Follow-up Items');
    expect(content).toContain('Pre-existing or Out-of-scope Items');
  });

  it('verifies blocking items have suggestedAction', () => {
    expect(content).toContain('SuggestedAction');
  });

  it('verifies non-blocking severities for follow-up and pre-existing items', () => {
    expect(content).toContain('non-blocking severities');
  });
});

describe('re-verify.md prompt structure', () => {
  const content = readArtifact('re-verify.md');

  it('uses the same structured format as review', () => {
    expect(content).toContain('# Review Report');
    expect(content).toContain('## Result: PASS / FAIL');
    expect(content).toContain('## Repaired Items');
    expect(content).toContain('## Blocking Items');
    expect(content).toContain('## Follow-up Items');
    expect(content).toContain('## Pre-existing or Out-of-scope Items');
  });

  it('requires exactly one promise marker', () => {
    expect(content).toContain('exactly one machine-readable verdict tag');
    expect(content).toContain('<promise>PASS</promise>');
    expect(content).toContain('<promise>FAIL</promise>');
    expect(content).toContain('Do NOT include more than one marker');
  });

  it('requires comprehensive re-verification', () => {
    expect(content).toContain('complete re-review');
    expect(content).toContain('Do NOT stop after verifying the first');
  });

  it('requires item fields for all reported items', () => {
    expect(content).toContain('[ID: item-N]');
    expect(content).toContain('Severity:');
    expect(content).toContain('Evidence:');
    expect(content).toContain('Status:');
  });
});
