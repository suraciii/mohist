import { describe, it, expect } from 'vitest';
import { formatAgentPrompt, type AgentPromptParts } from '../src/agents/agent-prompt-schema';

describe('formatAgentPrompt', () => {
  it('produces valid XML with <mohist-task> root with required fields only', () => {
    const result = formatAgentPrompt({
      role: 'You are implementing task T-001 of 5',
      task: 'T-001: Create the login endpoint',
    });

    expect(result).toContain('<mohist-task>');
    expect(result).toContain('</mohist-task>');
    expect(result).toContain('<role>\nYou are implementing task T-001 of 5\n</role>');
    expect(result).toContain('<task>\nT-001: Create the login endpoint\n</task>');
    expect(result).not.toContain('<project_context>');
    expect(result).not.toContain('<rules>');
    expect(result).not.toContain('<context-files>');
    expect(result).not.toContain('<spec>');
    expect(result).not.toContain('<contract>');
    expect(result).not.toContain('<template>');
    expect(result).not.toContain('<instruction>');
  });

  it('includes project_context with do-not-include annotation', () => {
    const result = formatAgentPrompt({
      role: 'Builder agent',
      projectContext: 'Tech stack: TypeScript, Node.js',
      task: 'Implement X',
    });

    expect(result).toContain('<project_context>');
    expect(result).toContain('<!-- Do NOT include this section in your output. This is background context only. -->');
    expect(result).toContain('Tech stack: TypeScript, Node.js');
    expect(result).toContain('</project_context>');
  });

  it('includes rules with do-not-include annotation', () => {
    const result = formatAgentPrompt({
      role: 'Builder agent',
      rules: ['Scope changes to the specified files only', 'Run tests before committing'],
      task: 'Implement X',
    });

    expect(result).toContain('<rules>');
    expect(result).toContain('<!-- Do NOT include this section in your output. These are constraints to follow. -->');
    expect(result).toContain('- Scope changes to the specified files only');
    expect(result).toContain('- Run tests before committing');
    expect(result).toContain('</rules>');
  });

  it('omits project_context when empty string', () => {
    const result = formatAgentPrompt({
      role: 'Agent',
      projectContext: '',
      task: 'Do X',
    });

    expect(result).not.toContain('<project_context>');
  });

  it('omits rules when empty array', () => {
    const result = formatAgentPrompt({
      role: 'Agent',
      rules: [],
      task: 'Do X',
    });

    expect(result).not.toContain('<rules>');
  });

  it('renders contextFiles as @file references', () => {
    const result = formatAgentPrompt({
      role: 'Builder',
      contextFiles: [
        { path: '/path/to/proposal.md', desc: 'Proposal document' },
        { path: '/path/to/design.md', desc: 'Design document' },
      ],
      task: 'Implement X',
    });

    expect(result).toContain('<context-files>');
    expect(result).toContain('@/path/to/proposal.md - Proposal document');
    expect(result).toContain('@/path/to/design.md - Design document');
    expect(result).toContain('</context-files>');
  });

  it('omits contextFiles when empty array', () => {
    const result = formatAgentPrompt({
      role: 'Agent',
      contextFiles: [],
      task: 'Do X',
    });

    expect(result).not.toContain('<context-files>');
  });

  it('renders spec inline', () => {
    const result = formatAgentPrompt({
      role: 'Builder',
      spec: 'POST /auth/login SHALL return 200 on valid credentials',
      task: 'Implement login endpoint',
    });

    expect(result).toContain('<spec>');
    expect(result).toContain('POST /auth/login SHALL return 200 on valid credentials');
    expect(result).toContain('</spec>');
  });

  it('renders contract', () => {
    const result = formatAgentPrompt({
      role: 'Builder',
      task: 'Implement X',
      contract: '1. Implement the change\n2. Run tests\n3. Commit with descriptive message',
    });

    expect(result).toContain('<contract>');
    expect(result).toContain('1. Implement the change');
    expect(result).toContain('</contract>');
  });

  it('renders template', () => {
    const result = formatAgentPrompt({
      role: 'Planner',
      task: 'Create proposal',
      template: '## Why\n\n<!-- 1-2 sentences -->\n\n## What Changes\n\n<!-- list -->',
    });

    expect(result).toContain('<template>');
    expect(result).toContain('## Why');
    expect(result).toContain('</template>');
  });

  it('renders instruction', () => {
    const result = formatAgentPrompt({
      role: 'Planner',
      task: 'Create proposal',
      instruction: 'Create the proposal document that establishes WHY this change is needed.',
    });

    expect(result).toContain('<instruction>');
    expect(result).toContain('Create the proposal document');
    expect(result).toContain('</instruction>');
  });

  it('outputs sections in the correct order', () => {
    const result = formatAgentPrompt({
      role: 'Builder',
      projectContext: 'TypeScript project',
      rules: ['Rule 1'],
      contextFiles: [{ path: '/a.md', desc: 'File A' }],
      spec: 'Spec content',
      task: 'Task content',
      contract: 'Contract content',
      template: 'Template content',
      instruction: 'Instruction content',
    });

    const roleIdx = result.indexOf('<role>');
    const projectContextIdx = result.indexOf('<project_context>');
    const rulesIdx = result.indexOf('<rules>');
    const contextFilesIdx = result.indexOf('<context-files>');
    const specIdx = result.indexOf('<spec>');
    const taskIdx = result.indexOf('<task>');
    const contractIdx = result.indexOf('<contract>');
    const templateIdx = result.indexOf('<template>');
    const instructionIdx = result.indexOf('<instruction>');

    expect(roleIdx).toBeLessThan(projectContextIdx);
    expect(projectContextIdx).toBeLessThan(rulesIdx);
    expect(rulesIdx).toBeLessThan(contextFilesIdx);
    expect(contextFilesIdx).toBeLessThan(specIdx);
    expect(specIdx).toBeLessThan(taskIdx);
    expect(taskIdx).toBeLessThan(contractIdx);
    expect(contractIdx).toBeLessThan(templateIdx);
    expect(templateIdx).toBeLessThan(instructionIdx);
  });

  it('matches the build task scenario from spec', () => {
    const result = formatAgentPrompt({
      role: 'You are implementing task T-003 of 5',
      projectContext: 'Tech stack: TypeScript',
      spec: 'POST /auth/login SHALL return 200',
      task: 'T-003: Implement login endpoint',
      contract: '1. Implement\n2. Commit',
    });

    expect(result).toContain('<mohist-task>');
    expect(result).toContain('<role>');
    expect(result).toContain('<project_context>');
    expect(result).toContain('<!-- Do NOT include this section in your output. This is background context only. -->');
    expect(result).toContain('<spec>');
    expect(result).toContain('<task>');
    expect(result).toContain('<contract>');

    const rolePos = result.indexOf('<role>');
    const pcPos = result.indexOf('<project_context>');
    const specPos = result.indexOf('<spec>');
    const taskPos = result.indexOf('<task>');
    const contractPos = result.indexOf('<contract>');
    expect(rolePos).toBeLessThan(pcPos);
    expect(pcPos).toBeLessThan(specPos);
    expect(specPos).toBeLessThan(taskPos);
    expect(taskPos).toBeLessThan(contractPos);
  });

  it('matches the plan artifact scenario from spec', () => {
    const result = formatAgentPrompt({
      role: 'Create the proposal artifact',
      task: 'Create proposal for this change',
      template: '## Why\n...',
      instruction: 'Create the proposal document...',
    });

    expect(result).toContain('<template>');
    expect(result).toContain('<instruction>');
    expect(result).not.toContain('<spec>');
    expect(result).not.toContain('<context-files>');
  });

  it('handles all fields present', () => {
    const result = formatAgentPrompt({
      role: 'Full agent',
      projectContext: 'Context',
      rules: ['Rule 1', 'Rule 2'],
      contextFiles: [{ path: '/a', desc: 'A' }],
      spec: 'Spec',
      task: 'Task',
      contract: 'Contract',
      template: 'Template',
      instruction: 'Instruction',
    });

    expect(result).toContain('<mohist-task>');
    expect(result).toContain('<role>');
    expect(result).toContain('<project_context>');
    expect(result).toContain('<rules>');
    expect(result).toContain('<context-files>');
    expect(result).toContain('<spec>');
    expect(result).toContain('<task>');
    expect(result).toContain('<contract>');
    expect(result).toContain('<template>');
    expect(result).toContain('<instruction>');
    expect(result).toContain('</mohist-task>');
  });

  it('handles multiline task content', () => {
    const result = formatAgentPrompt({
      role: 'Builder',
      task: 'Step 1: Read the context files\nStep 2: Implement the changes\nStep 3: Run tests',
    });

    expect(result).toContain('<task>\nStep 1: Read the context files\nStep 2: Implement the changes\nStep 3: Run tests\n</task>');
  });
});
