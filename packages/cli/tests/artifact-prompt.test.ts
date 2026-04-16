import { describe, it, expect } from 'vitest';
import {
  buildArtifactPrompt,
  buildSelfReviewPrompt,
  buildReviewerPrompt,
  buildExplorePrompt,
  type ArtifactType,
} from '../src/agents/artifact-prompt';
import type { Issue } from '../src/types';
import * as fs from 'fs';
import * as path from 'path';

const mockIssue: Issue = {
  id: 'issue-1',
  number: 42,
  title: 'Add authentication',
  body: 'We need JWT-based auth for the API.',
  stage: 'plan' as any,
  status: 'active' as any,
  projectId: 'proj-1',
  labels: [],
  createdAt: '2024-01-01T00:00:00Z',
  updatedAt: '2024-01-01T00:00:00Z',
};

describe('buildArtifactPrompt', () => {
  const changeDir = '/tmp/test-change';

  const artifactTypes: ArtifactType[] = ['proposal', 'specs', 'design', 'tasks'];

  it.each(artifactTypes)('should include issue info for %s artifact', (type) => {
    const result = buildArtifactPrompt(type, mockIssue, changeDir);

    expect(result).toContain('Issue #42');
    expect(result).toContain('Add authentication');
    expect(result).toContain('JWT-based auth');
  });

  it.each(artifactTypes)('should include change directory for %s artifact', (type) => {
    const result = buildArtifactPrompt(type, mockIssue, changeDir);

    expect(result).toContain(changeDir);
  });

  it.each(artifactTypes)('should include goal for %s artifact', (type) => {
    const result = buildArtifactPrompt(type, mockIssue, changeDir);

    expect(result).toContain(`Generate the **${type}** artifact`);
  });

  it.each(artifactTypes)('should include instruction from file for %s artifact', (type) => {
    const instructionPath = path.join(__dirname, '..', 'src', 'agents', 'prompts', 'artifacts', `${type}.md`);
    const instruction = fs.readFileSync(instructionPath, 'utf-8');

    const result = buildArtifactPrompt(type, mockIssue, changeDir);

    expect(result).toContain(instruction.slice(0, 100));
  });

  it('should include read_file tip in prompt', () => {
    const result = buildArtifactPrompt('proposal', mockIssue, changeDir);

    expect(result).toContain('read_file');
  });

  it('should handle issue without body', () => {
    const issueNoBody: Issue = {
      ...mockIssue,
      body: undefined,
    };

    const result = buildArtifactPrompt('proposal', issueNoBody, changeDir);

    expect(result).toContain('Issue #42');
    expect(result).toContain('Add authentication');
    expect(result).toContain('## Goal');
  });

  it('should throw for non-existent artifact type', () => {
    expect(() =>
      buildArtifactPrompt('nonexistent' as ArtifactType, mockIssue, changeDir)
    ).toThrow('Instruction file not found');
  });
});

describe('buildSelfReviewPrompt', () => {
  it('should include issue info and change dir', () => {
    const result = buildSelfReviewPrompt(mockIssue, '/tmp/change');

    expect(result).toContain('Issue #42');
    expect(result).toContain('/tmp/change');
    expect(result).toContain('Self-review');
  });

  it('should include self-review instruction', () => {
    const instructionPath = path.join(__dirname, '..', 'src', 'agents', 'prompts', 'artifacts', 'self-review.md');
    const instruction = fs.readFileSync(instructionPath, 'utf-8');

    const result = buildSelfReviewPrompt(mockIssue, '/tmp/change');

    expect(result).toContain(instruction.slice(0, 50));
  });
});

describe('buildReviewerPrompt', () => {
  it('should include issue info and change dir', () => {
    const result = buildReviewerPrompt(mockIssue, '/tmp/change');

    expect(result).toContain('Issue #42');
    expect(result).toContain('/tmp/change');
    expect(result).toContain('Review the implementation');
  });

  it('should include review instruction', () => {
    const instructionPath = path.join(__dirname, '..', 'src', 'agents', 'prompts', 'review.md');
    const instruction = fs.readFileSync(instructionPath, 'utf-8');

    const result = buildReviewerPrompt(mockIssue, '/tmp/change');

    expect(result).toContain(instruction.slice(0, 50));
  });
});

describe('buildExplorePrompt', () => {
  it('should include issue number when provided', () => {
    const result = buildExplorePrompt(
      { title: 'Test', body: 'Body', number: 5 },
      '/tmp/change'
    );

    expect(result).toContain('Issue #5');
    expect(result).toContain('Test');
    expect(result).toContain('Body');
  });

  it('should work without issue number', () => {
    const result = buildExplorePrompt(
      { title: 'New exploration' },
      '/tmp/change'
    );

    expect(result).toContain('Issue: New exploration');
    expect(result).not.toContain('Issue #');
  });

  it('should include existing proposal when provided', () => {
    const result = buildExplorePrompt(
      { title: 'Test' },
      '/tmp/change',
      'Existing proposal content'
    );

    expect(result).toContain('Existing Proposal');
    expect(result).toContain('Existing proposal content');
  });

  it('should not include existing proposal section when not provided', () => {
    const result = buildExplorePrompt(
      { title: 'Test' },
      '/tmp/change'
    );

    expect(result).not.toContain('Existing Proposal');
  });
});
