import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import {
  loadHealthGatePolicies,
  loadWorkflow,
  DEFAULT_HEALTH_GATE_POLICIES,
  DEFAULT_CHECKS_CONFIG,
} from '../src/workflow/workflow-loader';

function parseYamlWorkflow(content: string): any {
  const yaml = require('yaml');
  return yaml.parse(content);
}

describe('loadHealthGatePolicies', () => {
  let tempDir: string;

  beforeEach(() => {
    tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-health-gate-'));
  });

  afterEach(() => {
    fs.rmSync(tempDir, { recursive: true, force: true });
  });

  describe('default policies', () => {
    it('returns default policies when no workflow.yaml exists', () => {
      const result = loadHealthGatePolicies({ stages: [], source: 'builtin' } as any);
      expect(result.plan.command).toBe('npm ci && npm run typecheck');
      expect(result.build.command).toBe('npm ci && npm run build');
      expect(result.check.command).toBe('npm ci && npm run build && npm test');
      expect(result.postMerge.command).toBe('npm ci && npm run build && npm test');
    });

    it('plan gate defaults to npm ci && npm run typecheck', () => {
      const result = loadHealthGatePolicies({ stages: [], source: 'builtin' } as any);
      expect(result.plan.enabled).toBe(true);
      expect(result.plan.command).toBe('npm ci && npm run typecheck');
      expect(result.plan.timeout).toBe(5 * 60 * 1000);
      expect(result.plan.autoFix).toBe(false);
      expect(result.plan.maxFixAttempts).toBe(0);
      expect(result.plan.fallbackReaction.type).toBe('ask-user');
    });

    it('build gate defaults to npm ci && npm run build with autoFix', () => {
      const result = loadHealthGatePolicies({ stages: [], source: 'builtin' } as any);
      expect(result.build.enabled).toBe(true);
      expect(result.build.command).toBe('npm ci && npm run build');
      expect(result.build.timeout).toBe(5 * 60 * 1000);
      expect(result.build.autoFix).toBe(true);
      expect(result.build.maxFixAttempts).toBe(2);
      expect(result.build.fallbackReaction.type).toBe('escalate');
      expect(result.build.fallbackReaction.escalateTarget).toBe('plan');
    });

    it('check gate defaults to npm ci && npm run build && npm test', () => {
      const result = loadHealthGatePolicies({ stages: [], source: 'builtin' } as any);
      expect(result.check.enabled).toBe(true);
      expect(result.check.command).toBe('npm ci && npm run build && npm test');
      expect(result.check.timeout).toBe(5 * 60 * 1000);
      expect(result.check.autoFix).toBe(true);
      expect(result.check.maxFixAttempts).toBe(2);
      expect(result.check.fallbackReaction.type).toBe('escalate');
      expect(result.check.fallbackReaction.escalateTarget).toBe('build');
    });

    it('postMerge gate defaults to same command as check', () => {
      const result = loadHealthGatePolicies({ stages: [], source: 'builtin' } as any);
      expect(result.postMerge.enabled).toBe(true);
      expect(result.postMerge.command).toBe('npm ci && npm run build && npm test');
      expect(result.postMerge.timeout).toBe(5 * 60 * 1000);
      expect(result.postMerge.autoFix).toBe(false);
      expect(result.postMerge.maxFixAttempts).toBe(0);
      expect(result.postMerge.fallbackReaction.type).toBe('ask-user');
    });
  });

  describe('per-stage overrides', () => {
    it('overrides build command when healthGates.build.command is set', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: build
healthGates:
  build:
    command: npm run custom-build
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.build.command).toBe('npm run custom-build');
      expect(result.build.timeout).toBe(DEFAULT_HEALTH_GATE_POLICIES.build.timeout);
      expect(result.build.autoFix).toBe(DEFAULT_HEALTH_GATE_POLICIES.build.autoFix);
    });

    it('partially overrides fields with field-by-field fallback', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: build
healthGates:
  build:
    enabled: true
    timeout: 600000
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.build.enabled).toBe(true);
      expect(result.build.timeout).toBe(600000);
      expect(result.build.command).toBe('npm ci && npm run build');
      expect(result.build.autoFix).toBe(DEFAULT_HEALTH_GATE_POLICIES.build.autoFix);
      expect(result.build.maxFixAttempts).toBe(DEFAULT_HEALTH_GATE_POLICIES.build.maxFixAttempts);
    });

    it('allows overriding plan gate', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: plan
healthGates:
  plan:
    command: npm run lint
    autoFix: true
    maxFixAttempts: 1
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.plan.command).toBe('npm run lint');
      expect(result.plan.autoFix).toBe(true);
      expect(result.plan.maxFixAttempts).toBe(1);
    });

    it('allows overriding postMerge gate', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: done
healthGates:
  postMerge:
    command: npm run e2e
    enabled: false
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.postMerge.command).toBe('npm run e2e');
      expect(result.postMerge.enabled).toBe(false);
    });
  });

  describe('disabled gates', () => {
    it('preserves enabled: false explicitly', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: plan
healthGates:
  plan:
    enabled: false
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.plan.enabled).toBe(false);
      expect(result.plan.command).toBe('npm ci && npm run typecheck');
    });

    it('disabling one gate does not affect others', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: build
healthGates:
  build:
    enabled: false
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.build.enabled).toBe(false);
      expect(result.plan.enabled).toBe(true);
      expect(result.check.enabled).toBe(true);
      expect(result.postMerge.enabled).toBe(true);
    });
  });

  describe('checks.buildTest fallback', () => {
    it('preserves real workflow.yaml healthGates and checks through loadWorkflow', () => {
      fs.writeFileSync(path.join(tempDir, 'workflow.yaml'), `
stages:
  - stage: check
healthGates:
  build:
    enabled: false
    command: npm run configured-build
checks:
  buildTest:
    command: npm run configured-ci
    timeout: 600000
    autoFix: false
    maxFixAttempts: 1
`, 'utf-8');

      const workflow = loadWorkflow(tempDir);
      expect(typeof workflow).not.toBe('string');

      const result = loadHealthGatePolicies(workflow as any);
      expect(result.build.enabled).toBe(false);
      expect(result.build.command).toBe('npm run configured-build');
      expect(result.check.command).toBe('npm run configured-ci');
      expect(result.check.timeout).toBe(600000);
      expect(result.check.autoFix).toBe(false);
      expect(result.check.maxFixAttempts).toBe(1);
    });

    it('loads compatibility health gates from new full custom workflow yaml', () => {
      fs.mkdirSync(path.join(tempDir, '.mohist'));
      fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
workflow:
  id: project/custom
  stages:
    - id: build
      tasks: []
      checks: []
healthGates:
  build:
    command: npm run custom-build
checks:
  buildTest:
    command: npm run custom-ci
    timeout: 600000
`, 'utf-8');

      const workflow = loadWorkflow(tempDir);
      expect(typeof workflow).not.toBe('string');
      expect((workflow as any).source).toContain('.mohist/workflow.yaml');

      const result = loadHealthGatePolicies(workflow as any);
      expect(result.build.command).toBe('npm run custom-build');
      expect(result.check.command).toBe('npm run custom-ci');
      expect(result.check.timeout).toBe(600000);
    });

    it('loads compatibility health gates from extends workflow yaml', () => {
      fs.mkdirSync(path.join(tempDir, '.mohist'));
      fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
extends: mohist/default
healthGates:
  check:
    command: npm run verify
`, 'utf-8');

      const workflow = loadWorkflow(tempDir);
      expect(typeof workflow).not.toBe('string');

      const result = loadHealthGatePolicies(workflow as any);
      expect(result.check.command).toBe('npm run verify');
    });

    it('prefers .mohist/workflow.yaml over workflow.yaml for compatibility config', () => {
      fs.mkdirSync(path.join(tempDir, '.mohist'));
      fs.writeFileSync(path.join(tempDir, 'workflow.yaml'), `
stages:
  - stage: check
healthGates:
  check:
    command: npm run root
`, 'utf-8');
      fs.writeFileSync(path.join(tempDir, '.mohist', 'workflow.yaml'), `
extends: mohist/default
healthGates:
  check:
    command: npm run dot-mohist
`, 'utf-8');

      const workflow = loadWorkflow(tempDir);
      expect(typeof workflow).not.toBe('string');
      expect((workflow as any).source).toContain('.mohist/workflow.yaml');

      const result = loadHealthGatePolicies(workflow as any);
      expect(result.check.command).toBe('npm run dot-mohist');
    });

    it('maps checks.buildTest to check health gate when healthGates.check is absent', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: check
checks:
  buildTest:
    command: npm run ci-test
    timeout: 600000
    autoFix: true
    maxFixAttempts: 3
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.check.command).toBe('npm run ci-test');
      expect(result.check.timeout).toBe(600000);
      expect(result.check.autoFix).toBe(true);
      expect(result.check.maxFixAttempts).toBe(3);
      expect(result.check.enabled).toBe(true);
      expect(result.check.fallbackReaction.type).toBe('escalate');
      expect(result.check.fallbackReaction.escalateTarget).toBe('build');
    });

    it('uses checks.buildTest timeout and autoFix when only some fields are present', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: check
checks:
  buildTest:
    command: npm run custom-test
    timeout: 300000
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.check.command).toBe('npm run custom-test');
      expect(result.check.timeout).toBe(300000);
      expect(result.check.autoFix).toBe(DEFAULT_CHECKS_CONFIG.buildTest.autoFix);
      expect(result.check.maxFixAttempts).toBe(DEFAULT_CHECKS_CONFIG.buildTest.maxFixAttempts);
    });

    it('explicit healthGates.check takes precedence over checks.buildTest', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: check
checks:
  buildTest:
    command: npm run ci-test
    timeout: 600000
healthGates:
  check:
    command: npm run health-check
    autoFix: false
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.check.command).toBe('npm run health-check');
      expect(result.check.autoFix).toBe(false);
      expect(result.check.timeout).toBe(DEFAULT_HEALTH_GATE_POLICIES.check.timeout);
    });

    it('does not fall back to checks.buildTest for plan gate', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: plan
checks:
  buildTest:
    command: npm run ci-test
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.plan.command).toBe('npm ci && npm run typecheck');
      expect(result.plan.command).not.toBe('npm run ci-test');
    });

    it('does not fall back to checks.buildTest for build gate', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: build
checks:
  buildTest:
    command: npm run ci-test
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.build.command).toBe('npm ci && npm run build');
      expect(result.build.command).not.toBe('npm run ci-test');
    });

    it('does not fall back to checks.buildTest for postMerge gate', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: done
checks:
  buildTest:
    command: npm run ci-test
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.postMerge.command).toBe('npm ci && npm run build && npm test');
      expect(result.postMerge.command).not.toBe('npm run ci-test');
    });
  });

  describe('fallback reaction parsing', () => {
    it('parses fallbackReaction with escalate target', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: build
healthGates:
  build:
    fallbackReaction:
      type: escalate
      escalateTarget: plan
      maxAttempts: 3
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.build.fallbackReaction.type).toBe('escalate');
      expect(result.build.fallbackReaction.escalateTarget).toBe('plan');
      expect(result.build.fallbackReaction.maxAttempts).toBe(3);
    });

    it('parses fallbackReaction with ask-user type', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: plan
healthGates:
  plan:
    fallbackReaction:
      type: ask-user
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.plan.fallbackReaction.type).toBe('ask-user');
    });

    it('falls back to defaults for fallbackReaction type', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: check
healthGates:
  check:
    fallbackReaction:
      maxAttempts: 5
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.check.fallbackReaction.type).toBe(DEFAULT_HEALTH_GATE_POLICIES.check.fallbackReaction.type);
      expect(result.check.fallbackReaction.maxAttempts).toBe(5);
    });
  });

  describe('field-by-field fallback', () => {
    it('when healthGates.check is present with only timeout, command falls back to default (not checks.buildTest)', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: check
checks:
  buildTest:
    command: npm run ci-test
healthGates:
  check:
    timeout: 400000
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.check.command).toBe('npm ci && npm run build && npm test');
      expect(result.check.timeout).toBe(400000);
    });

    it('partial healthGates.check uses defaults for missing fields and ignores checks.buildTest', () => {
      const workflow = parseYamlWorkflow(`
stages:
  - stage: check
checks:
  buildTest:
    command: npm run ci-test
healthGates:
  check:
    enabled: true
    command: npm run health
`);
      const result = loadHealthGatePolicies(workflow);
      expect(result.check.enabled).toBe(true);
      expect(result.check.command).toBe('npm run health');
      expect(result.check.timeout).toBe(DEFAULT_HEALTH_GATE_POLICIES.check.timeout);
      expect(result.check.autoFix).toBe(DEFAULT_HEALTH_GATE_POLICIES.check.autoFix);
      expect(result.check.maxFixAttempts).toBe(DEFAULT_HEALTH_GATE_POLICIES.check.maxFixAttempts);
    });
  });
});
