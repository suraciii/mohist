import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';
import { createReadSpecTool } from '../src/tools/read-spec';

describe('createReadSpecTool', () => {
  let tmpDir: string;

  beforeEach(() => {
    tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'mohist-test-'));
  });

  afterEach(() => {
    fs.rmSync(tmpDir, { recursive: true, force: true });
  });

  function writeSpec(changeDir: string, specFile: string, content: string) {
    const fullPath = path.join(tmpDir, changeDir, specFile);
    fs.mkdirSync(path.dirname(fullPath), { recursive: true });
    fs.writeFileSync(fullPath, content);
  }

  async function executeTool(params: {
    change_path: string;
    spec_path: string;
    requirement_ref?: string;
  }) {
    const tool = createReadSpecTool({ projectPath: tmpDir });
    const parsed = tool.definition.parameters.safeParse(params);
    if (!parsed.success) {
      return `Validation error: ${parsed.error.issues.map((i) => i.message).join(', ')}`;
    }
    return tool.definition.execute(parsed.data);
  }

  const sampleSpec = `## ADDED Requirements

### Requirement: Session memory storage
The system SHALL store task execution learnings in structured session memory files.

#### Scenario: Store task learning
- **WHEN** a task completes
- **THEN** the system stores insights

### Requirement: Session memory retrieval
The system SHALL retrieve and include relevant session memories.

#### Scenario: Load memories
- **WHEN** assembling context
- **THEN** the system reads all memories`;

  it('should read and return a spec file with metadata', async () => {
    writeSpec('.mohist-specs/changes/42-test', 'specs/session-memory/spec.md', sampleSpec);

    const result = await executeTool({
      change_path: '.mohist-specs/changes/42-test',
      spec_path: 'specs/session-memory/spec.md',
    });

    expect(result).toContain('# Spec: specs/session-memory/spec.md');
    expect(result).toContain('File: .mohist-specs/changes/42-test/specs/session-memory/spec.md');
    expect(result).toContain('Requirements: 2');
    expect(result).toContain('### Requirement: Session memory storage');
    expect(result).toContain('### Requirement: Session memory retrieval');
    expect(result).toContain('**WHEN** a task completes');
  });

  it('should return error when spec file does not exist', async () => {
    const result = await executeTool({
      change_path: '.mohist-specs/changes/nonexistent',
      spec_path: 'specs/core/spec.md',
    });

    expect(result).toContain('Error: spec file not found');
  });

  it('should return error when change_path is outside project', async () => {
    const result = await executeTool({
      change_path: '../../etc',
      spec_path: 'specs/test/spec.md',
    });

    expect(result).toBe('Error: change_path is outside the project directory');
  });

  it('should return error when spec_path escapes change directory', async () => {
    const result = await executeTool({
      change_path: '.mohist-specs/changes/42-test',
      spec_path: '../../etc/passwd',
    });

    expect(result).toBe('Error: spec_path escapes the change directory');
  });

  it('should filter by requirement title', async () => {
    writeSpec('.mohist-specs/changes/42-test', 'specs/session-memory/spec.md', sampleSpec);

    const result = await executeTool({
      change_path: '.mohist-specs/changes/42-test',
      spec_path: 'specs/session-memory/spec.md',
      requirement_ref: 'Session memory storage',
    });

    expect(result).toContain('Filtered by: Session memory storage');
    expect(result).toContain('### Requirement: Session memory storage');
    expect(result).toContain('**WHEN** a task completes');
    expect(result).not.toContain('### Requirement: Session memory retrieval');
  });

  it('should return full spec when requirement_ref not found', async () => {
    writeSpec('.mohist-specs/changes/42-test', 'specs/session-memory/spec.md', sampleSpec);

    const result = await executeTool({
      change_path: '.mohist-specs/changes/42-test',
      spec_path: 'specs/session-memory/spec.md',
      requirement_ref: 'Nonexistent requirement',
    });

    expect(result).toContain('Note: requirement "Nonexistent requirement" not found');
    expect(result).toContain('### Requirement: Session memory storage');
    expect(result).toContain('### Requirement: Session memory retrieval');
  });

  it('should handle spec with no requirement sections', async () => {
    writeSpec('.mohist-specs/changes/42-test', 'specs/simple/spec.md', '# Simple Spec\n\nJust some text.');

    const result = await executeTool({
      change_path: '.mohist-specs/changes/42-test',
      spec_path: 'specs/simple/spec.md',
    });

    expect(result).toContain('# Spec: specs/simple/spec.md');
    expect(result).toContain('Requirements: 0');
    expect(result).toContain('Just some text.');
  });

  it('should reject extra parameters', () => {
    const tool = createReadSpecTool({ projectPath: tmpDir });
    const result = tool.definition.parameters.safeParse({
      change_path: 'some/path',
      spec_path: 'specs/test/spec.md',
      extra: 'not allowed',
    });

    expect(result.success).toBe(false);
  });

  it('should work with absolute change_path inside project', async () => {
    writeSpec('abs-test', 'specs/test/spec.md', '### Requirement: Test req\nContent here.');

    const result = await executeTool({
      change_path: path.join(tmpDir, 'abs-test'),
      spec_path: 'specs/test/spec.md',
    });

    expect(result).toContain('### Requirement: Test req');
    expect(result).toContain('Requirements: 1');
  });
});
