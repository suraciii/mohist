import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { OpenSpecSyncDryRunCheck } from '../../src/workflow/checks/openspec-sync-dry-run-check';
import { MergeReadinessCheck } from '../../src/workflow/checks/merge-readiness-check';
import { Stage } from '../../src/types';

function makeCheckContext(overrides?: Partial<{
  changeDir: string;
  issue: any;
  acpOptions: any;
  worktreeManager: any;
  projectRepo: any;
}>) {
  return {
    issue: {
      id: 'issue-1',
      number: 1,
      title: 'Test Issue',
      stage: 'check' as any,
      status: 'active' as any,
      projectId: 'proj-1',
      labels: [],
      priority: 'p2' as any,
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
    },
    changeDir: '/tmp/change',
    eventBus: { emit: vi.fn() } as any,
    projectId: 'proj-1',
    acpOptions: { cwd: '/tmp/worktree' },
    workflowLogRepo: undefined,
    sessionStreamLogRepo: undefined,
    coderSessionRepo: undefined,
    projectRepo: {
      findById: vi.fn().mockReturnValue({
        id: 'proj-1',
        name: 'test-project',
        path: '/tmp/project',
        baseBranch: 'master',
        createdAt: new Date().toISOString(),
        updatedAt: new Date().toISOString(),
      }),
    },
    worktreeManager: {
      canFastForward: vi.fn().mockResolvedValue(true),
      getWorktreeStatus: vi.fn().mockResolvedValue({
        exists: true,
        branch: 'mo/issue-1',
        baseBranch: 'main',
        ahead: 2,
        behind: 0,
        canFastForward: false,
        isRebaseInProgress: false,
        conflictingFiles: [],
      }),
    },
    ...overrides,
  };
}

describe('OpenSpecSyncDryRunCheck', () => {
  let tmpDir: string;
  let changeDir: string;
  let projectPath: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'openspec-sync-dry-run-test-'));
    changeDir = path.join(tmpDir, 'change');
    projectPath = tmpDir;
    fs.mkdirSync(changeDir, { recursive: true });
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  function createMainSpec(capability: string, requirements: string[]) {
    const specDir = path.join(projectPath, 'openspec', 'specs', capability);
    fs.mkdirSync(specDir, { recursive: true });
    let content = '# OpenSpec Capability: ' + capability + '\n\n';
    for (const req of requirements) {
      content += req + '\n\n';
    }
    fs.writeFileSync(path.join(specDir, 'spec.md'), content, 'utf-8');
  }

  function createChangeSpec(capability: string, content: string) {
    const specsDir = path.join(changeDir, 'specs');
    fs.mkdirSync(specsDir, { recursive: true });
    const capabilityDir = path.join(specsDir, capability);
    fs.mkdirSync(capabilityDir, { recursive: true });
    fs.writeFileSync(path.join(capabilityDir, 'spec.md'), content, 'utf-8');
  }

  describe('pass scenario', () => {
    it('returns pass when spec sync dry-run has no conflicts', async () => {
      createMainSpec('test-cap', [
        '### Requirement: ExistingReq\n\nContent.\n\n#### Scenario: Test\nContent.',
      ]);
      createChangeSpec('test-cap', `## ADDED Requirements

### Requirement: NewReq

New requirement.

#### Scenario: New scenario
Content.`);

      const check = new OpenSpecSyncDryRunCheck();
      const ctx = makeCheckContext({ changeDir, acpOptions: { cwd: projectPath } });
      const result = await check.run(ctx);

      expect(result.status).toBe('pass');
      expect(result.name).toBe('openspec-sync-dry-run');
      expect(result.output).toMatchObject({
        kind: 'openspec-sync-dry-run',
        capabilities: ['test-cap'],
        valid: true,
        counts: { added: 1, modified: 0, removed: 0, renamed: 0 },
      });
    });

    it('does not modify openspec/specs after dry-run', async () => {
      createMainSpec('test-cap', [
        '### Requirement: ExistingReq\n\nContent.\n\n#### Scenario: Test\nContent.',
      ]);
      createChangeSpec('test-cap', `## ADDED Requirements

### Requirement: NewReq

New requirement.

#### Scenario: New scenario
Content.`);

      const check = new OpenSpecSyncDryRunCheck();
      const ctx = makeCheckContext({ changeDir, acpOptions: { cwd: projectPath } });
      await check.run(ctx);

      const mainSpecContent = fs.readFileSync(
        path.join(projectPath, 'openspec', 'specs', 'test-cap', 'spec.md'),
        'utf-8'
      );
      expect(mainSpecContent).not.toContain('NewReq');
    });
  });

  describe('fail scenario', () => {
    it('returns fail when spec sync has conflicts', async () => {
      createMainSpec('test-cap', [
        '### Requirement: ExistingReq\n\nContent.\n\n#### Scenario: Test\nContent.',
      ]);
      createChangeSpec('test-cap', `## ADDED Requirements

### Requirement: ExistingReq

Duplicate requirement.

#### Scenario: New scenario
Content.`);

      const check = new OpenSpecSyncDryRunCheck();
      const ctx = makeCheckContext({ changeDir, acpOptions: { cwd: projectPath } });
      const result = await check.run(ctx);

      expect(result.status).toBe('fail');
      expect(result.output).toMatchObject({
        kind: 'openspec-sync-dry-run',
        valid: false,
      });
      expect((result.output as any).conflicts.length).toBeGreaterThan(0);
    });
  });

  describe('error scenario', () => {
    it('returns error when change directory is not provided', async () => {
      const check = new OpenSpecSyncDryRunCheck();
      const ctx = makeCheckContext({ changeDir: '' });
      const result = await check.run(ctx);

      expect(result.status).toBe('error');
    });
  });
});

describe('MergeReadinessCheck', () => {
  describe('pass scenario', () => {
    it('returns pass when squash preflight reports canMerge true', async () => {
      const check = new MergeReadinessCheck();
      const ctx = makeCheckContext({
        worktreeManager: {
          checkSquashMergeability: vi.fn().mockResolvedValue({
            kind: 'merge-ready',
            strategy: 'squash',
            targetBranch: 'master',
            baseSha: 'abc123',
            candidateHeadSha: 'def456',
            mergeBaseSha: '789abc',
            canMerge: true,
            conflictFiles: [],
            checkedAt: new Date().toISOString(),
          }),
        },
      });
      const result = await check.run(ctx);

      expect(result.status).toBe('pass');
      expect(result.name).toBe('merge-readiness');
      expect(result.output).toMatchObject({
        kind: 'merge-readiness',
        targetBranch: 'master',
        canMerge: true,
        strategy: 'squash',
      });
    });
  });

  describe('fail scenario', () => {
    it('returns fail when squash preflight reports canMerge false', async () => {
      const check = new MergeReadinessCheck();
      const ctx = makeCheckContext({
        worktreeManager: {
          checkSquashMergeability: vi.fn().mockResolvedValue({
            kind: 'merge-ready',
            strategy: 'squash',
            targetBranch: 'master',
            baseSha: 'abc123',
            candidateHeadSha: 'def456',
            mergeBaseSha: '789abc',
            canMerge: false,
            conflictFiles: ['src/foo.ts', 'src/bar.ts'],
            checkedAt: new Date().toISOString(),
            error: 'Squash merge conflict',
          }),
        },
      });
      const result = await check.run(ctx);

      expect(result.status).toBe('fail');
      expect(result.output).toMatchObject({
        kind: 'merge-readiness',
        canMerge: false,
        conflictFiles: ['src/foo.ts', 'src/bar.ts'],
      });
    });
  });

  describe('error scenario', () => {
    it('returns error when checkSquashMergeability throws', async () => {
      const check = new MergeReadinessCheck();
      const ctx = makeCheckContext({
        worktreeManager: {
          checkSquashMergeability: vi.fn().mockRejectedValue(new Error('git error')),
        },
      });
      const result = await check.run(ctx);

      expect(result.status).toBe('error');
      expect(result.message).toContain('git error');
    });
  });
});

describe('CHECK non-blocking OpenSpec preview', () => {
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'check-nonblocking-openspec-test-'));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  it('OpenSpecSyncDryRunCheck returns fail for missing_source conflict but this is non-blocking in default CHECK', async () => {
    const changeDir = path.join(tmpDir, 'change');
    fs.mkdirSync(changeDir, { recursive: true });

    const projectPath = tmpDir;
    const specDir = path.join(projectPath, 'openspec', 'specs', 'test-cap');
    fs.mkdirSync(specDir, { recursive: true });
    fs.writeFileSync(path.join(specDir, 'spec.md'), '# OpenSpec Capability: test-cap\n\n', 'utf-8');

    const specsDir = path.join(changeDir, 'specs');
    fs.mkdirSync(specsDir, { recursive: true });
    const capabilityDir = path.join(specsDir, 'test-cap');
    fs.mkdirSync(capabilityDir, { recursive: true });
    fs.writeFileSync(path.join(capabilityDir, 'spec.md'), `## MODIFIED Requirements

### Requirement: NewReq

New requirement content.

#### Scenario: Test
Content.`, 'utf-8');

    const check = new OpenSpecSyncDryRunCheck();
    const ctx = makeCheckContext({ changeDir, acpOptions: { cwd: projectPath } });
    const result = await check.run(ctx);

    expect(result.status).toBe('fail');
    expect(result.output).toMatchObject({
      kind: 'openspec-sync-dry-run',
      valid: false,
    });
    const conflicts = (result.output as any).conflicts;
    expect(conflicts.length).toBeGreaterThan(0);
    expect(conflicts[0].type).toBe('missing_source');
  });

  it('default Check stage definition contains review and merge checks without openspec sync dry-run', async () => {
    const { DEFAULT_STAGE_DEFINITIONS } = await import('../../src/workflow/definitions/default-workflow');
    const checkDefinition = DEFAULT_STAGE_DEFINITIONS.find(definition => definition.stage === Stage.Check)!;
    const checkNames = checkDefinition.checks.map(check => check.name);

    expect(checkNames).toContain('health:check');
    expect(checkNames).not.toContain('openspec-sync-dry-run');
    expect(checkNames).toContain('review-passed');
    expect(checkNames).toContain('merge-ready');
  });
});
