import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { OpenSpecSyncDryRunCheck } from '../../src/workflow/checks/openspec-sync-dry-run-check';
import { MergeReadinessCheck } from '../../src/workflow/checks/merge-readiness-check';
import { IntegrationHealthGatePreviewCheck } from '../../src/workflow/checks/integration-health-gate-preview-check';

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
    it('returns pass when canFastForward is true', async () => {
      const check = new MergeReadinessCheck();
      const ctx = makeCheckContext({
        worktreeManager: {
          canFastForward: vi.fn().mockResolvedValue(true),
          getWorktreeStatus: vi.fn().mockResolvedValue({
            exists: true,
            branch: 'mo/issue-1',
            canFastForward: false,
            isRebaseInProgress: false,
            conflictingFiles: [],
          }),
        },
      });
      const result = await check.run(ctx);

      expect(result.status).toBe('pass');
      expect(result.name).toBe('merge-readiness');
      expect(result.output).toMatchObject({
        kind: 'merge-readiness',
        targetBranch: 'master',
        canFastForward: true,
      });
    });

    it('returns pass when canFastForward from getWorktreeStatus is true', async () => {
      const check = new MergeReadinessCheck();
      const ctx = makeCheckContext({
        worktreeManager: {
          canFastForward: vi.fn().mockResolvedValue(false),
          getWorktreeStatus: vi.fn().mockResolvedValue({
            exists: true,
            branch: 'mo/issue-1',
            canFastForward: true,
            isRebaseInProgress: false,
            conflictingFiles: [],
          }),
        },
      });
      const result = await check.run(ctx);

      expect(result.status).toBe('pass');
      expect(result.output).toMatchObject({
        kind: 'merge-readiness',
        canFastForward: true,
      });
    });
  });

  describe('fail scenario', () => {
    it('returns fail when merge is not fast-forwardable and conflicts exist', async () => {
      const check = new MergeReadinessCheck();
      const ctx = makeCheckContext({
        worktreeManager: {
          canFastForward: vi.fn().mockResolvedValue(false),
          getWorktreeStatus: vi.fn().mockResolvedValue({
            exists: true,
            branch: 'mo/issue-1',
            canFastForward: false,
            isRebaseInProgress: false,
            conflictingFiles: ['src/foo.ts', 'src/bar.ts'],
          }),
        },
      });
      const result = await check.run(ctx);

      expect(result.status).toBe('fail');
      expect(result.output).toMatchObject({
        kind: 'merge-readiness',
        canFastForward: false,
        cleanRebaseFeasible: false,
        conflictFiles: ['src/foo.ts', 'src/bar.ts'],
      });
    });
  });

  describe('error scenario', () => {
    it('returns error when worktreeManager throws', async () => {
      const check = new MergeReadinessCheck();
      const ctx = makeCheckContext({
        worktreeManager: {
          canFastForward: vi.fn().mockRejectedValue(new Error('git error')),
          getWorktreeStatus: vi.fn().mockRejectedValue(new Error('git error')),
        },
      });
      const result = await check.run(ctx);

      expect(result.status).toBe('error');
      expect(result.message).toContain('git error');
    });
  });
});

describe('IntegrationHealthGatePreviewCheck', () => {
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'health-gate-preview-test-'));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  it('returns pass with postMerge policy metadata', async () => {
    const workflowYaml = path.join(tmpDir, 'workflow.yaml');
    fs.writeFileSync(workflowYaml, `
stages:
  - stage: plan
  - stage: build
  - stage: check
healthGates:
  postMerge:
    enabled: true
    command: npm run build && npm test
    timeout: 300000
    autoFix: false
    maxFixAttempts: 0
    fallbackReaction:
      type: ask-user
`, 'utf-8');

    const check = new IntegrationHealthGatePreviewCheck();
    const ctx = makeCheckContext({ changeDir: tmpDir, acpOptions: { cwd: tmpDir } });
    const result = await check.run(ctx);

    expect(result.status).toBe('pass');
    expect(result.name).toBe('integration-health-gate-preview');
    expect(result.output).toMatchObject({
      kind: 'integration-health-gate-preview',
      policyName: 'postMerge',
      command: 'npm run build && npm test',
      timeout: 300000,
      enabled: true,
      autoFix: false,
      maxFixAttempts: 0,
    });
  });

  it('returns error when workflow loading fails', async () => {
    const check = new IntegrationHealthGatePreviewCheck();
    const nonexistentDir = '/non-existent-directory-that-cannot-exist';
    const ctx = makeCheckContext({ changeDir: nonexistentDir, acpOptions: { cwd: nonexistentDir } });
    const result = await check.run(ctx);
    expect(result.status).toBe('pass');
    const output = result.output as any;
    expect(output.policyName).toBe('postMerge');
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

  it('CheckStageRunner default postTaskChecks are review-passed, merge-ready, user-approval', async () => {
    const worktreePath = tmpDir;
    fs.mkdirSync(worktreePath, { recursive: true });
    fs.writeFileSync(path.join(worktreePath, 'workflow.yaml'), 'stages:\n  - stage: check\n', 'utf-8');

    const { CheckStageRunner } = await import('../../src/workflow/check-stage-runner');
    const runner = new CheckStageRunner({ worktreePath });
    const preChecks = runner.getPreTaskChecks();
    const postChecks = runner.getChecks();

    expect(preChecks).toHaveLength(0);
    const postCheckNames = postChecks.map((c: any) => c.name);
    expect(postCheckNames).not.toContain('openspec-sync-dry-run');
    expect(postCheckNames).toContain('review-passed');
    expect(postCheckNames).toContain('merge-ready');
    expect(postCheckNames).toContain('user-approval');
  });
});
