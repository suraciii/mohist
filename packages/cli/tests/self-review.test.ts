import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { runSelfReview, canGeneratePrd, type SelfReviewResult } from '../src/openspec/self-review';

describe('self-review', () => {
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = path.join(os.tmpdir(), `mohist-test-${Date.now()}-${Math.random().toString(36).slice(2)}`);
    fs.mkdirSync(tmpDir, { recursive: true });
  });

  afterEach(() => {
    try {
      fs.rmSync(tmpDir, { recursive: true, force: true });
    } catch {
      // ignore cleanup errors
    }
  });

  describe('runSelfReview', () => {
    it('returns failed when proposal.md is missing', async () => {
      fs.mkdirSync(path.join(tmpDir, 'specs'), { recursive: true });
      fs.writeFileSync(path.join(tmpDir, 'design.md'), '# Design\nSome design content.');

      const result = await runSelfReview({ changePath: tmpDir });
      expect(result.passed).toBe(false);
      expect(result.issues.some(i => i.includes('Proposal'))).toBe(true);
    });

    it('returns passed when all artifacts are complete', async () => {
      fs.mkdirSync(path.join(tmpDir, 'specs', 'test-capability'), { recursive: true });
      fs.writeFileSync(path.join(tmpDir, 'proposal.md'), '# Proposal\n\n## Why\n\nThis is a detailed proposal with substantial content for the feature.');
      fs.writeFileSync(path.join(tmpDir, 'design.md'), '# Design\n\n## Technical Approach\n\nThis is a detailed design document.');
      fs.writeFileSync(
        path.join(tmpDir, 'specs', 'test-capability', 'spec.md'),
        `## ADDED Requirements

### Requirement: Test capability
This system SHALL implement test capability.

#### Scenario: Test scenario
- **WHEN** the system is triggered
- **THEN** it performs the expected action
`
      );

      const result = await runSelfReview({ changePath: tmpDir, maxIterations: 3 });
      expect(result.passed).toBe(true);
      expect(result.canGeneratePrd).toBe(true);
    });

    it('returns issues when specs have no requirements', async () => {
      fs.mkdirSync(path.join(tmpDir, 'specs', 'empty-capability'), { recursive: true });
      fs.writeFileSync(path.join(tmpDir, 'proposal.md'), '# Proposal\n\nThis is a detailed proposal content with sufficient length here.');
      fs.writeFileSync(path.join(tmpDir, 'design.md'), '# Design\n\nThis is a detailed design content with sufficient length here.');
      fs.writeFileSync(
        path.join(tmpDir, 'specs', 'empty-capability', 'spec.md'),
        '## ADDED Requirements\n\nSome content but no requirements.'
      );

      const result = await runSelfReview({ changePath: tmpDir });
      expect(result.passed).toBe(false);
      expect(result.issues.some(i => i.includes('no requirements'))).toBe(true);
    });
  });

  describe('canGeneratePrd', () => {
    it('returns false when specs are incomplete', () => {
      fs.mkdirSync(path.join(tmpDir, 'specs'), { recursive: true });
      fs.writeFileSync(path.join(tmpDir, 'proposal.md'), '# Proposal\nShort.');
      fs.writeFileSync(path.join(tmpDir, 'design.md'), '# Design\nShort.');

      expect(canGeneratePrd(tmpDir)).toBe(false);
    });

    it('returns true when all requirements met', () => {
      fs.mkdirSync(path.join(tmpDir, 'specs', 'cap'), { recursive: true });
      fs.writeFileSync(path.join(tmpDir, 'proposal.md'), '# Proposal\n\nThis is a detailed proposal content with sufficient length.');
      fs.writeFileSync(path.join(tmpDir, 'design.md'), '# Design\n\nThis is a detailed design content with sufficient length.');
      fs.writeFileSync(
        path.join(tmpDir, 'specs', 'cap', 'spec.md'),
        `## ADDED Requirements

### Requirement: Test
This system SHALL do something.

#### Scenario: Test
- **WHEN** condition
- **THEN** result
`
      );

      expect(canGeneratePrd(tmpDir)).toBe(true);
    });
  });
});
