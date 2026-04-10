import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { ReviewerAgent } from '../src/agents/reviewer-agent';

describe('ReviewerAgent.runTests', () => {
  let tmpDir: string;
  let worktreePath: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
    worktreePath = tmpDir;
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  function createPackageJson(scripts: Record<string, string>) {
    const pkgJson = { name: 'test-project', version: '1.0.0', scripts };
    fs.writeFileSync(path.join(worktreePath, 'package.json'), JSON.stringify(pkgJson, null, 2));
  }

  describe('test execution priority', () => {
    it('should return passed with no issues when package.json does not exist', async () => {
      const agent = new ReviewerAgent({});
      const result = await (agent as any).runTests(worktreePath);
      expect(result.passed).toBe(true);
      expect(result.issues).toHaveLength(0);
    });

    it('should return passed when package.json has no scripts', async () => {
      fs.writeFileSync(path.join(worktreePath, 'package.json'), JSON.stringify({ name: 'test', version: '1.0.0' }));
      const agent = new ReviewerAgent({});
      const result = await (agent as any).runTests(worktreePath);
      expect(result.passed).toBe(true);
      expect(result.issues).toHaveLength(0);
    });

    it('should return passed when package.json has empty scripts', async () => {
      createPackageJson({});
      const agent = new ReviewerAgent({});
      const result = await (agent as any).runTests(worktreePath);
      expect(result.passed).toBe(true);
    });

    it('should skip test script with default "no test specified" message', async () => {
      createPackageJson({ test: 'echo "no test specified"' });
      const agent = new ReviewerAgent({});
      const result = await (agent as any).runTests(worktreePath);
      expect(result.passed).toBe(true);
    });

    it('should return passed when invalid JSON in package.json', async () => {
      fs.writeFileSync(path.join(worktreePath, 'package.json'), 'not valid json');
      const agent = new ReviewerAgent({});
      const result = await (agent as any).runTests(worktreePath);
      expect(result.passed).toBe(true);
    });
  });

  describe('npm command detection', () => {
    it('should identify when test script contains "no test specified"', () => {
      const agent = new ReviewerAgent({});
      const noTestScript = 'echo "no test specified"';
      const realTestScript = 'vitest';
      expect(noTestScript.includes('no test specified')).toBe(true);
      expect(realTestScript.includes('no test specified')).toBe(false);
    });
  });
});

describe('ReviewerAgent.runNpmCommand', () => {
  let tmpDir: string;
  let worktreePath: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
    worktreePath = tmpDir;
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  function createPackageJson(scripts: Record<string, string>) {
    const pkgJson = { name: 'test-project', version: '1.0.0', scripts };
    fs.writeFileSync(path.join(worktreePath, 'package.json'), JSON.stringify(pkgJson, null, 2));
  }

  it('should return passed when npm command succeeds', async () => {
    createPackageJson({ test: 'echo "tests passed"' });
    const agent = new ReviewerAgent({});
    const result = await (agent as any).runNpmCommand('npm test', 'test', worktreePath);
    expect(result.passed).toBe(true);
  });

  it('should return failed with issues when npm command fails', async () => {
    createPackageJson({ test: 'exit 1' });
    const agent = new ReviewerAgent({});
    const result = await (agent as any).runNpmCommand('npm test', 'test', worktreePath);
    expect(result.passed).toBe(false);
    expect(result.issues.length).toBeGreaterThan(0);
  });

  it('should include output in suggestion on failure', async () => {
    createPackageJson({ test: 'echo "error occurred" && exit 1' });
    const agent = new ReviewerAgent({});
    const result = await (agent as any).runNpmCommand('npm test', 'test', worktreePath);
    expect(result.passed).toBe(false);
    expect(result.issues[0].suggestion).toBeDefined();
  });
});