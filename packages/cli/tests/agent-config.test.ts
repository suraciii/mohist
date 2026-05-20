import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { loadAgentConfig } from '../src/agents/agent-config';

describe('loadAgentConfig', () => {
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-agent-cfg-'));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  it('returns empty object when no workflow.yaml exists', () => {
    const result = loadAgentConfig(tempDir);
    expect(result).toEqual({});
  });

  it('returns empty object when workflow.yaml has no agent section', () => {
    fs.writeFileSync(path.join(tempDir, 'workflow.yaml'), 'stages:\n  - stage: build\n');
    const result = loadAgentConfig(tempDir);
    expect(result).toEqual({});
  });

  it('returns context and rules when both present', () => {
    fs.writeFileSync(
      path.join(tempDir, 'workflow.yaml'),
      `stages:
  - stage: build
agent:
  context: |
    Tech stack: TypeScript
    Build: npm run build
  rules:
    build:
      - Keep changes scoped
      - Run tests before commit
    plan:
      - Write clear specs
`,
    );
    const result = loadAgentConfig(tempDir);
    expect(result.context).toBe('Tech stack: TypeScript\nBuild: npm run build\n');
    expect(result.rules).toEqual({
      build: ['Keep changes scoped', 'Run tests before commit'],
      plan: ['Write clear specs'],
    });
  });

  it('returns context only when rules absent', () => {
    fs.writeFileSync(
      path.join(tempDir, 'workflow.yaml'),
      `stages:
  - stage: build
agent:
  context: "Tech stack: TypeScript"
`,
    );
    const result = loadAgentConfig(tempDir);
    expect(result.context).toBe('Tech stack: TypeScript');
    expect(result.rules).toBeUndefined();
  });

  it('returns rules only when context absent', () => {
    fs.writeFileSync(
      path.join(tempDir, 'workflow.yaml'),
      `stages:
  - stage: build
agent:
  rules:
    build:
      - Keep changes scoped
`,
    );
    const result = loadAgentConfig(tempDir);
    expect(result.context).toBeUndefined();
    expect(result.rules).toEqual({ build: ['Keep changes scoped'] });
  });

  it('returns empty object when agent section is empty', () => {
    fs.writeFileSync(
      path.join(tempDir, 'workflow.yaml'),
      `stages:
  - stage: build
agent: {}
`,
    );
    const result = loadAgentConfig(tempDir);
    expect(result).toEqual({});
  });

  it('reads from .mohist/workflow.yaml when workflow.yaml is absent', () => {
    const mohistDir = path.join(tempDir, '.mohist');
    fs.mkdirSync(mohistDir);
    fs.writeFileSync(
      path.join(mohistDir, 'workflow.yaml'),
      `agent:
  context: Hello from .mohist
`,
    );
    const result = loadAgentConfig(tempDir);
    expect(result.context).toBe('Hello from .mohist');
  });

  it('prefers .mohist/workflow.yaml over workflow.yaml', () => {
    const mohistDir = path.join(tempDir, '.mohist');
    fs.mkdirSync(mohistDir);
    fs.writeFileSync(
      path.join(mohistDir, 'workflow.yaml'),
      `agent:\n  context: from-dot-mohist\n`,
    );
    fs.writeFileSync(
      path.join(tempDir, 'workflow.yaml'),
      `agent:\n  context: from-root\n`,
    );
    const result = loadAgentConfig(tempDir);
    expect(result.context).toBe('from-dot-mohist');
  });

  it('ignores non-string context values', () => {
    fs.writeFileSync(
      path.join(tempDir, 'workflow.yaml'),
      `agent:\n  context: 123\n`,
    );
    const result = loadAgentConfig(tempDir);
    expect(result.context).toBeUndefined();
  });

  it('ignores non-array rule values', () => {
    fs.writeFileSync(
      path.join(tempDir, 'workflow.yaml'),
      `agent:\n  rules:\n    build: "not an array"\n`,
    );
    const result = loadAgentConfig(tempDir);
    expect(result.rules).toBeUndefined();
  });

  it('does not throw on malformed yaml', () => {
    fs.writeFileSync(path.join(tempDir, 'workflow.yaml'), '{{invalid yaml:::');
    const result = loadAgentConfig(tempDir);
    expect(result).toEqual({});
  });
});
