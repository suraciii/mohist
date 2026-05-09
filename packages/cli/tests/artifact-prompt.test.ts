import { describe, it, expect } from 'vitest';
import {
  buildArtifactPrompt,
  buildSelfReviewPrompt,
  buildReviewerPrompt,
  buildExplorePrompt,
  buildReviewSelfCheckPrompt,
  buildAutoFixPrompt,
  buildReVerifyPrompt,
  buildConflictResolutionPrompt,
  type ArtifactType,
} from '../src/agents/artifact-prompt';
import type { Issue } from '../src/types';
import type { AgentConfig } from '../src/workflow/workflow-loader';
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

const mockAgentConfig: AgentConfig = {
  context: 'Tech stack: TypeScript, Node.js. Build: npm run build. Test: npm test.',
  rules: {
    plan: ['Keep changes scoped to the issue'],
    review: ['Check error handling', 'Verify type safety'],
  },
};

describe('buildArtifactPrompt', () => {
  const changeDir = '/tmp/test-change';

  const artifactTypes: ArtifactType[] = ['proposal', 'specs', 'design', 'tasks'];

  it.each(artifactTypes)('should produce <mohist-task> envelope for %s artifact', (type) => {
    const result = buildArtifactPrompt(type, mockIssue, changeDir);

    expect(result).toContain('<mohist-task>');
    expect(result).toContain('</mohist-task>');
  });

  it.each(artifactTypes)('should include <role> for %s artifact', (type) => {
    const result = buildArtifactPrompt(type, mockIssue, changeDir);

    expect(result).toContain('<role>');
    expect(result).toContain(`Create the ${type} artifact`);
    expect(result).toContain('</role>');
  });

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

  it.each(artifactTypes)('should include <task> section for %s artifact', (type) => {
    const result = buildArtifactPrompt(type, mockIssue, changeDir);

    expect(result).toContain('<task>');
    expect(result).toContain('</task>');
    expect(result).toContain(`Create the ${type} artifact`);
  });

  it.each(artifactTypes)('should include <context-files> or <contract> for dependencies and output for %s artifact', (type) => {
    const result = buildArtifactPrompt(type, mockIssue, changeDir);

    expect(result).toContain(`<contract>`);
    expect(result).toContain(`</contract>`);
    expect(result).toContain(changeDir);
  });

  it.each(artifactTypes)('should include <template> section for %s artifact', (type) => {
    const result = buildArtifactPrompt(type, mockIssue, changeDir);

    expect(result).toContain('<template>');
    expect(result).toContain('</template>');
  });

  it.each(artifactTypes)('should include <instruction> section for %s artifact', (type) => {
    const instructionPath = path.join(__dirname, '..', 'src', 'agents', 'prompts', 'artifacts', `${type}.md`);
    const instruction = fs.readFileSync(instructionPath, 'utf-8');

    const result = buildArtifactPrompt(type, mockIssue, changeDir);

    expect(result).toContain('<instruction>');
    expect(result).toContain('</instruction>');
    expect(result).toContain(instruction.slice(0, 100));
  });

  it('should have correct output file mapping', () => {
    const outputMapping: Record<ArtifactType, string> = {
      proposal: 'proposal.md',
      specs: 'specs/',
      design: 'design.md',
      tasks: 'tasks.json',
    };

    for (const type of artifactTypes) {
      const result = buildArtifactPrompt(type, mockIssue, changeDir);
      expect(result).toContain(path.join(changeDir, outputMapping[type]));
    }
  });

  it('should list only existing dependencies as <context-files>', () => {
    const tmpDir = fs.mkdtempSync('/tmp/mohist-test-deps-');
    try {
      fs.writeFileSync(path.join(tmpDir, 'proposal.md'), '# Proposal');
      fs.mkdirSync(path.join(tmpDir, 'specs'), { recursive: true });

      const result = buildArtifactPrompt('design', mockIssue, tmpDir);

      expect(result).toContain('<context-files>');
      expect(result).toContain(path.join(tmpDir, 'proposal.md'));
      expect(result).toContain(path.join(tmpDir, 'specs'));
    } finally {
      fs.rmSync(tmpDir, { recursive: true, force: true });
    }
  });

  it('should not include <context-files> for proposal (first artifact)', () => {
    const result = buildArtifactPrompt('proposal', mockIssue, changeDir);

    expect(result).not.toContain('<context-files>');
  });

  it('should not include <context-files> when no previous artifacts exist', () => {
    const tmpDir = fs.mkdtempSync('/tmp/mohist-test-nodeps-');
    try {
      const result = buildArtifactPrompt('design', mockIssue, tmpDir);

      expect(result).not.toContain('<context-files>');
    } finally {
      fs.rmSync(tmpDir, { recursive: true, force: true });
    }
  });

  it('should handle issue without body', () => {
    const issueNoBody: Issue = {
      ...mockIssue,
      body: undefined,
    };

    const result = buildArtifactPrompt('proposal', issueNoBody, changeDir);

    expect(result).toContain('Issue #42');
    expect(result).toContain('Add authentication');
    expect(result).toContain('<task>');
  });

  it('should throw for non-existent artifact type', () => {
    expect(() =>
      buildArtifactPrompt('nonexistent' as ArtifactType, mockIssue, changeDir)
    ).toThrow('File not found');
  });

  it('should include <project_context> when agentConfig is provided', () => {
    const result = buildArtifactPrompt('proposal', mockIssue, changeDir, mockAgentConfig);

    expect(result).toContain('<project_context>');
    expect(result).toContain('Tech stack: TypeScript');
    expect(result).toContain('</project_context>');
  });

  it('should not include <project_context> when agentConfig is omitted', () => {
    const result = buildArtifactPrompt('proposal', mockIssue, changeDir);

    expect(result).not.toContain('<project_context>');
  });
});

describe('buildSelfReviewPrompt', () => {
  it('should include issue info and change dir', () => {
    const result = buildSelfReviewPrompt(mockIssue, '/tmp/change');

    expect(result).toContain('<mohist-task>');
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

  it('should include <project_context> when agentConfig is provided', () => {
    const result = buildSelfReviewPrompt(mockIssue, '/tmp/change', mockAgentConfig);

    expect(result).toContain('<project_context>');
    expect(result).toContain('Tech stack: TypeScript');
  });
});

describe('buildReviewerPrompt', () => {
  it('should include issue info and change dir', () => {
    const result = buildReviewerPrompt(mockIssue, '/tmp/change');

    expect(result).toContain('<mohist-task>');
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

  it('should include <project_context> when agentConfig is provided', () => {
    const result = buildReviewerPrompt(mockIssue, '/tmp/change', mockAgentConfig);

    expect(result).toContain('<project_context>');
    expect(result).toContain('Tech stack: TypeScript');
  });

  it('should include <rules> from review stage when agentConfig has review rules', () => {
    const result = buildReviewerPrompt(mockIssue, '/tmp/change', mockAgentConfig);

    expect(result).toContain('<rules>');
    expect(result).toContain('Check error handling');
    expect(result).toContain('Verify type safety');
  });
});

describe('buildReviewSelfCheckPrompt', () => {
  it('should include issue info and change dir', () => {
    const result = buildReviewSelfCheckPrompt(mockIssue, '/tmp/change');

    expect(result).toContain('<mohist-task>');
    expect(result).toContain('Issue #42');
    expect(result).toContain('/tmp/change');
    expect(result).toContain('Verify the review report');
  });

  it('should include <project_context> when agentConfig is provided', () => {
    const result = buildReviewSelfCheckPrompt(mockIssue, '/tmp/change', mockAgentConfig);

    expect(result).toContain('<project_context>');
    expect(result).toContain('Tech stack: TypeScript');
  });
});

describe('buildAutoFixPrompt', () => {
  it('should include issue info and report content', () => {
    const result = buildAutoFixPrompt(mockIssue, '/tmp/change', 'FAIL: missing tests', 'review.md');

    expect(result).toContain('<mohist-task>');
    expect(result).toContain('Issue #42');
    expect(result).toContain('/tmp/change');
    expect(result).toContain('FAIL: missing tests');
  });

  it('should describe machine-readable failed review input', () => {
    const result = buildAutoFixPrompt(mockIssue, '/tmp/change', '<promise>FAIL</promise>', 'review.md');

    expect(result).toContain('<promise>FAIL</promise>');
  });

  it('should include contract with fix-only constraint', () => {
    const result = buildAutoFixPrompt(mockIssue, '/tmp/change', 'FAIL: missing tests', 'review.md');

    expect(result).toContain('<contract>');
    expect(result).toContain('Apply ONLY the fixes described');
    expect(result).toContain('Do NOT modify review.md');
  });

  it('should include <project_context> when agentConfig is provided', () => {
    const result = buildAutoFixPrompt(mockIssue, '/tmp/change', 'report', 'review.md', mockAgentConfig);

    expect(result).toContain('<project_context>');
    expect(result).toContain('Tech stack: TypeScript');
  });
});

describe('buildReVerifyPrompt', () => {
  it('should include issue info and review content', () => {
    const result = buildReVerifyPrompt(mockIssue, '/tmp/change', 'Previous review content');

    expect(result).toContain('<mohist-task>');
    expect(result).toContain('Issue #42');
    expect(result).toContain('/tmp/change');
    expect(result).toContain('Previous review content');
    expect(result).toContain('Re-verify');
  });

  it('should require a machine-readable verdict tag in re-verified review output', () => {
    const result = buildReVerifyPrompt(mockIssue, '/tmp/change', 'Previous review content');

    expect(result).toContain('<promise>PASS</promise>');
    expect(result).toContain('<promise>FAIL</promise>');
    expect(result).toContain('final line MUST be exactly one machine-readable verdict tag');
  });

  it('should include <project_context> when agentConfig is provided', () => {
    const result = buildReVerifyPrompt(mockIssue, '/tmp/change', 'review', mockAgentConfig);

    expect(result).toContain('<project_context>');
    expect(result).toContain('Tech stack: TypeScript');
  });
});

describe('buildConflictResolutionPrompt', () => {
  it('should include issue info and conflict files', () => {
    const result = buildConflictResolutionPrompt(mockIssue, '/tmp/change', ['src/foo.ts', 'src/bar.ts']);

    expect(result).toContain('<mohist-task>');
    expect(result).toContain('Issue #42');
    expect(result).toContain('/tmp/change');
    expect(result).toContain('src/foo.ts');
    expect(result).toContain('src/bar.ts');
    expect(result).toContain('Resolve merge conflicts');
  });

  it('should include contract with conflict-only constraint', () => {
    const result = buildConflictResolutionPrompt(mockIssue, '/tmp/change', ['src/foo.ts']);

    expect(result).toContain('<contract>');
    expect(result).toContain('Apply ONLY the conflict resolution');
  });

  it('should include <project_context> when agentConfig is provided', () => {
    const result = buildConflictResolutionPrompt(mockIssue, '/tmp/change', ['src/foo.ts'], mockAgentConfig);

    expect(result).toContain('<project_context>');
    expect(result).toContain('Tech stack: TypeScript');
  });
});

describe('buildExplorePrompt', () => {
  it('should include issue number when provided', () => {
    const result = buildExplorePrompt(
      { title: 'Test', body: 'Body', number: 5 },
      '/tmp/change'
    );

    expect(result).toContain('<mohist-task>');
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

  it('should include existing proposal content when provided', () => {
    const result = buildExplorePrompt(
      { title: 'Test' },
      '/tmp/change',
      'Existing proposal content'
    );

    expect(result).toContain('Existing proposal content');
    expect(result).toContain('Update it based on your exploration');
  });

  it('should not include existing proposal section when not provided', () => {
    const result = buildExplorePrompt(
      { title: 'Test' },
      '/tmp/change'
    );

    expect(result).not.toContain('Update it based on your exploration');
  });

  it('should include <project_context> when agentConfig is provided', () => {
    const result = buildExplorePrompt(
      { title: 'Test' },
      '/tmp/change',
      null,
      mockAgentConfig
    );

    expect(result).toContain('<project_context>');
    expect(result).toContain('Tech stack: TypeScript');
  });
});
