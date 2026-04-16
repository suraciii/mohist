import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { loadPromptFromFile, loadPrompt, DEFAULT_PROMPTS_DIR } from '../src/agents/prompt-loader';

describe('prompt-loader', () => {
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  describe('loadPromptFromFile', () => {
    it('should load a valid YAML prompt file', () => {
      const promptsDir = path.join(tmpDir, 'prompts');
      fs.mkdirSync(promptsDir);
      fs.writeFileSync(path.join(promptsDir, 'test.yaml'), `
role: test
name: Test Agent
description: A test agent
`);
      const result = loadPromptFromFile(path.join(promptsDir, 'test.yaml'));
      expect(result).toContain('role: test');
    });

    it('should throw error when file does not exist', () => {
      expect(() => loadPromptFromFile(path.join(tmpDir, 'nonexistent.yaml'))).toThrow('Prompt file not found');
    });

    it('should handle invalid YAML structure that cannot be parsed', () => {
      const promptsDir = path.join(tmpDir, 'prompts');
      fs.mkdirSync(promptsDir);
      fs.writeFileSync(path.join(promptsDir, 'invalid.yaml'), `
role: test
  invalid: [unclosed
`);
      expect(() => loadPromptFromFile(path.join(promptsDir, 'invalid.yaml'))).toThrow();
    });

    it('should parse and stringify YAML preserving structure', () => {
      const promptsDir = path.join(tmpDir, 'prompts');
      fs.mkdirSync(promptsDir);
      const yamlContent = `
role: planner
name: Test Planner
steps:
  step1: Do this
  step2: Do that
`;
      fs.writeFileSync(path.join(promptsDir, 'test.yaml'), yamlContent);
      const result = loadPromptFromFile(path.join(promptsDir, 'test.yaml'));
      expect(result).toContain('role: planner');
      expect(result).toContain('step1');
    });
  });

  describe('loadPrompt', () => {
    it('should throw error when file does not exist', () => {
      expect(() => loadPrompt(path.join(tmpDir, 'nonexistent.yaml'))).toThrow('Prompt file does not exist');
    });

    it('should load existing file successfully', () => {
      const promptsDir = path.join(tmpDir, 'prompts');
      fs.mkdirSync(promptsDir);
      fs.writeFileSync(path.join(promptsDir, 'test.yaml'), 'role: test\nname: Test');
      const result = loadPrompt(path.join(promptsDir, 'test.yaml'));
      expect(result).toContain('role: test');
    });
  });

  describe('DEFAULT_PROMPTS_DIR', () => {
    it('should point to valid prompts directory', () => {
      expect(DEFAULT_PROMPTS_DIR).toBeTruthy();
      expect(typeof DEFAULT_PROMPTS_DIR).toBe('string');
    });
  });
});