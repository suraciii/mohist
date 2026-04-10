import type { Issue } from '../types';
import type { LlmConfig } from '../agent-runtime';
import { resolveModel } from '../agent-runtime';
import { ToolRegistry } from '../agent-runtime/tool';
import { streamText } from 'ai';
import { createReadFileTool } from '../tools/read-file';
import { createGlobTool } from '../tools/glob-tool';
import { createGrepTool } from '../tools/grep-tool';
import { execSync } from 'child_process';
import * as fs from 'fs';
import * as path from 'path';
import type { ReviewResult, DimensionResult, ReviewIssue } from '../types/workflow-results';
import { loadReviewerDefaultPrompt } from './prompt-loader';

export interface ReviewDimension {
  name: string;
  checks: string[];
  weight?: number;
}

export const REVIEWER_DEFAULT_PROMPT = `role: reviewer
name: Mohist Reviewer

description: |
  You are a Reviewer Agent for Mohist workflow.
  Your job is to review code quality and provide structured feedback.

dimensions:
  correctness:
    description: Code correctness and quality
    checks:
      - name: Logic errors
        severity: error
        details: Check for bugs, off-by-one errors, edge cases
      
      - name: Type safety
        severity: error
        details: Verify TypeScript types are correct
      
      - name: Lint violations
        severity: warning
        details: Check against project linting rules
    
    execution: |
      Review the code for logic errors and type safety.
      Run lint checks and report violations.

  complexity:
    description: Code complexity metrics
    checks:
      - name: Function length
        severity: warning
        threshold: 50 lines
        details: Functions should be concise and focused
      
      - name: Cyclomatic complexity
        severity: warning
        threshold: 10
        details: Limit branching in single function
      
      - name: Code duplication
        severity: warning
        details: Check for copy-pasted code
    
    execution: |
      Analyze code for complexity issues.
      Suggest refactoring if needed.

  test_coverage:
    description: Test coverage and quality
    checks:
      - name: Tests exist
        severity: error
        details: New code must have tests
      
      - name: Tests pass
        severity: error
        details: All tests must pass
      
      - name: Coverage adequate
        severity: warning
        threshold: 80%
        details: Code coverage should be reasonable
    
    execution: |
      Run test suite and check results.
      Verify new code has tests.
      Check coverage reports.

  security:
    description: Security best practices
    checks:
      - name: Input validation
        severity: error
        details: Validate all external inputs
      
      - name: Injection risks
        severity: error
        details: Check for SQL, command, or code injection
      
      - name: Sensitive data
        severity: warning
        details: Ensure secrets are not exposed
    
    execution: |
      Review for common security vulnerabilities.
      Check input validation and sanitization.

review_process:
  1_identify:
    action: Identify changed files
    method: Git diff or file system scan

  2_review_each:
    action: Review each file
    for_each_dimension:
      - Check criteria
      - Record issues
      - Provide reasoning

  3_run_tests:
    action: Execute test suite
    on_failure:
      - Report as correctness issue
      - Include error details

  4_aggregate:
    action: Aggregate results
    rules:
      - Any error dimension → overall fail
      - All pass → overall pass
      - Warnings only → pass with warnings

  5_suggest:
    action: Suggest fixes
    format: |
      Provide specific, actionable fix suggestions:
      - File path
      - Line number (if applicable)
      - Suggested change

output_format:
  passed: boolean
  dimensions:
    - name: string
      passed: boolean
      reasoning: string
      issues:
        - severity: error | warning
          location: string
          message: string
          suggestion: string
  overall_reasoning: string
  fix_suggestions:
    - string
`;

export interface ReviewerAgentOptions {
  llmConfig?: LlmConfig;
  defaultPrompt?: string;
}

export class ReviewerAgent {
  private llmConfig?: LlmConfig;
  private defaultPrompt: string;

  constructor(options: ReviewerAgentOptions) {
    this.llmConfig = options.llmConfig;
    this.defaultPrompt = options.defaultPrompt ?? loadReviewerDefaultPrompt();
  }

  async review(options: {
    issue: Issue;
    worktreePath: string;
    customPrompt?: string;
  }): Promise<ReviewResult> {
    const startTime = Date.now();
    const { worktreePath, customPrompt } = options;
    const activePrompt = customPrompt ?? this.defaultPrompt;

    try {
      const changedFiles = await this.getChangedFiles(worktreePath);
      const dimensions = this.extractDimensions(activePrompt);

      const dimensionResults: DimensionResult[] = [];
      let allPassed = true;
      let hasErrors = false;
      const allFixSuggestions: string[] = [];

      for (const dimension of dimensions) {
        const result = await this.reviewDimension(dimension, changedFiles, worktreePath);
        dimensionResults.push(result);

        if (!result.passed) {
          allPassed = false;
          if (result.issues?.some(i => i.severity === 'error')) {
            hasErrors = true;
          }
        }

        if (result.issues) {
          for (const issue of result.issues) {
            if (issue.suggestion) {
              allFixSuggestions.push(`[${dimension.name}] ${issue.location}: ${issue.suggestion}`);
            }
          }
        }
      }

      const testResult = await this.runTests(worktreePath);
      if (!testResult.passed) {
        hasErrors = true;
        allPassed = false;
        const testDimension: DimensionResult = {
          name: 'test_execution',
          passed: false,
          reasoning: 'Test suite failed',
          issues: testResult.issues.map(i => ({
            severity: 'error' as const,
            location: i.location,
            message: i.message,
            suggestion: i.suggestion,
          })),
        };
        dimensionResults.push(testDimension);
      }

      const duration = Date.now() - startTime;

      return {
        passed: allPassed,
        dimensions: dimensionResults,
        overallReasoning: this.generateOverallReasoning(dimensionResults, hasErrors),
        fixSuggestions: allFixSuggestions.length > 0 ? allFixSuggestions : undefined,
        duration,
      };
    } catch (error) {
      const duration = Date.now() - startTime;
      return {
        passed: false,
        dimensions: [],
        overallReasoning: error instanceof Error ? error.message : 'Review failed with unknown error',
        fixSuggestions: ['Fix the review process error'],
        duration,
      };
    }
  }

  private async getChangedFiles(worktreePath: string): Promise<string[]> {
    try {
      const output = execSync('git diff --name-only HEAD', {
        cwd: worktreePath,
        encoding: 'utf-8',
        timeout: 10000,
      });
      return output.split('\n').map(f => f.trim()).filter(Boolean);
    } catch {
      try {
        const output = execSync('git diff --name-only origin/main...HEAD', {
          cwd: worktreePath,
          encoding: 'utf-8',
          timeout: 10000,
        });
        return output.split('\n').map(f => f.trim()).filter(Boolean);
      } catch {
        return [];
      }
    }
  }

  private extractDimensions(prompt: string): ReviewDimension[] {
    const dimensions: ReviewDimension[] = [];
    const dimensionBlocks = prompt.match(/^(\w+):\s*\n\s*description:/gm);

    if (!dimensionBlocks) {
      return [
        { name: 'correctness', checks: ['Logic errors', 'Type safety', 'Lint violations'] },
        { name: 'complexity', checks: ['Function length', 'Cyclomatic complexity', 'Code duplication'] },
        { name: 'test_coverage', checks: ['Tests exist', 'Tests pass', 'Coverage adequate'] },
        { name: 'security', checks: ['Input validation', 'Injection risks', 'Sensitive data'] },
      ];
    }

    for (const block of dimensionBlocks) {
      const nameMatch = block.match(/^(\w+):/);
      if (!nameMatch) continue;

      const dimName = nameMatch[1];
      if (['role', 'name', 'description', 'steps', 'output_format', 'review_process'].includes(dimName)) {
        continue;
      }

      const checks: string[] = [];
      for (const match of prompt.matchAll(/- name: (.*?)(?:\n|$)/g)) {
        checks.push(match[1]);
      }

      dimensions.push({ name: dimName, checks: checks.length > 0 ? checks : [`${dimName} checks`] });
    }

    return dimensions.length > 0 ? dimensions : [
      { name: 'correctness', checks: ['Logic errors', 'Type safety', 'Lint violations'] },
      { name: 'complexity', checks: ['Function length', 'Cyclomatic complexity', 'Code duplication'] },
      { name: 'test_coverage', checks: ['Tests exist', 'Tests pass', 'Coverage adequate'] },
      { name: 'security', checks: ['Input validation', 'Injection risks', 'Sensitive data'] },
    ];
  }

  private async reviewDimension(
    dimension: ReviewDimension,
    changedFiles: string[],
    worktreePath: string
  ): Promise<DimensionResult> {
    if (changedFiles.length === 0) {
      return {
        name: dimension.name,
        passed: true,
        reasoning: `No changed files to review for dimension "${dimension.name}"`,
      };
    }

    const model = resolveModel(this.llmConfig);
    const toolRegistry = this.buildReviewToolRegistry(worktreePath);

    const codeSnippets = await this.getCodeSnippets(changedFiles, worktreePath);

    const prompt_text = `You are reviewing code changes for the "${dimension.name}" dimension.

Review Checks:
${dimension.checks.map(c => `- ${c}`).join('\n')}

Changed Files (first 20):
${changedFiles.slice(0, 20).join('\n')}

Code to review:
${codeSnippets.slice(0, 15000)}

For each check:
1. Identify any issues found
2. Determine severity (error/warning)
3. Provide specific location and message
4. Suggest a fix if applicable

Provide your review in this format:
passed: true/false
reasoning: Brief explanation
issues:
  - severity: error/warning
    location: file:line or "general"
    message: Issue description
    suggestion: Recommended fix (if applicable)
`;

    try {
      const result = await streamText({
        model,
        system: `You are a code reviewer focusing on the "${dimension.name}" dimension.`,
        messages: [{ role: 'user', content: prompt_text }],
        tools: toolRegistry.toToolSet(),
      });

      const reviewText = await result.text;
      return this.parseDimensionResult(dimension.name, reviewText);
    } catch (error) {
      return {
        name: dimension.name,
        passed: false,
        reasoning: error instanceof Error ? error.message : 'Review failed',
        issues: [{
          severity: 'error',
          location: 'general',
          message: 'Failed to complete dimension review',
        }],
      };
    }
  }

  private buildReviewToolRegistry(cwd: string): ToolRegistry {
    const registry = new ToolRegistry();
    registry.register(createReadFileTool({ projectPath: cwd }));
    registry.register(createGlobTool({ projectPath: cwd }));
    registry.register(createGrepTool({ projectPath: cwd }));
    return registry;
  }

  private async getCodeSnippets(files: string[], worktreePath: string): Promise<string> {
    const snippets: string[] = [];

    for (const file of files.slice(0, 30)) {
      if (!file || file.includes('package-lock') || file.includes('.lock')) continue;

      const fullPath = path.join(worktreePath, file);
      if (!fs.existsSync(fullPath)) continue;

      try {
        const stat = fs.statSync(fullPath);
        if (!stat.isFile()) continue;

        const content = fs.readFileSync(fullPath, 'utf-8');
        if (content.length > 50000) {
          snippets.push(`\n=== ${file} (truncated) ===\n${content.slice(0, 50000)}...`);
        } else {
          snippets.push(`\n=== ${file} ===\n${content}`);
        }
      } catch {
        snippets.push(`\n=== ${file} ===\n[Could not read file]`);
      }
    }

    return snippets.join('\n');
  }

  private parseDimensionResult(name: string, text: string): DimensionResult {
    const passed = !text.toLowerCase().includes('passed: false') && !text.toLowerCase().includes('"passed": false');
    const issues: ReviewIssue[] = [];

    const issueMatches = text.matchAll(/-\s*severity:\s*(error|warning)\s*\n\s*location:\s*([^\n]+)\s*\n\s*message:\s*([^\n]+)(?:\s*\n\s*suggestion:\s*([^\n]+))?/gi);
    for (const match of issueMatches) {
      issues.push({
        severity: match[1] as 'error' | 'warning',
        location: match[2].trim(),
        message: match[3].trim(),
        suggestion: match[4]?.trim(),
      });
    }

    const reasoningMatch = text.match(/reasoning:\s*([^\n]+(?:\n(?!\s*-)[^\n]+)*)/i);
    const reasoning = reasoningMatch ? reasoningMatch[1].trim().slice(0, 500) : (passed ? 'No issues found' : 'Issues detected');

    return { name, passed, reasoning, issues: issues.length > 0 ? issues : undefined };
  }

  private async runTests(worktreePath: string): Promise<{
    passed: boolean;
    issues: Array<{ location: string; message: string; suggestion?: string }>;
  }> {
    const packageJsonPath = path.join(worktreePath, 'package.json');

    if (!fs.existsSync(packageJsonPath)) {
      return { passed: true, issues: [] };
    }

    let packageJson: { scripts?: Record<string, string> };
    try {
      packageJson = JSON.parse(fs.readFileSync(packageJsonPath, 'utf-8'));
    } catch {
      return { passed: true, issues: [] };
    }

    const scripts = packageJson.scripts || {};

    if (scripts.test && !scripts.test.includes('no test specified')) {
      return await this.runNpmCommand('npm test', 'test', worktreePath);
    }

    if (scripts.build) {
      return await this.runNpmCommand('npm run build', 'build', worktreePath);
    }

    return { passed: true, issues: [] };
  }

  private async runNpmCommand(command: string, location: string, worktreePath: string): Promise<{
    passed: boolean;
    issues: Array<{ location: string; message: string; suggestion?: string }>;
  }> {
    try {
      execSync(command, {
        cwd: worktreePath,
        encoding: 'utf-8',
        timeout: 300000,
      });

      return { passed: true, issues: [] };
    } catch (error) {
      const stderr = error instanceof Error ? (error as any).stderr : '';
      const stdout = error instanceof Error ? (error as any).stdout : '';
      const combinedOutput = (stderr || stdout || String(error));
      const last1000 = combinedOutput.slice(-1000);

      const isExecutionError = combinedOutput.includes('command not found') ||
        combinedOutput.includes('ENOENT') ||
        combinedOutput.includes('spawn') ||
        combinedOutput.includes('npm: command not found');

      return {
        passed: false,
        issues: [{
          location,
          message: isExecutionError ? `${location} command could not be executed` : `${location} failed`,
          suggestion: last1000,
        }],
      };
    }
  }

  private generateOverallReasoning(dimensions: DimensionResult[], hasErrors: boolean): string {
    if (dimensions.length === 0) {
      return 'No dimensions were reviewed';
    }

    const passedCount = dimensions.filter(d => d.passed).length;
    const totalCount = dimensions.length;
    const errorCount = dimensions.filter(d => d.issues?.some(i => i.severity === 'error')).length;
    const warningCount = dimensions.filter(d => d.issues?.some(i => i.severity === 'warning')).length;

    if (hasErrors) {
      return `Review failed: ${errorCount} dimensions have errors. ${passedCount}/${totalCount} dimensions passed.`;
    }

    if (warningCount > 0) {
      return `Review passed with warnings: ${warningCount} dimensions have warnings. ${passedCount}/${totalCount} dimensions fully passed.`;
    }

    return `Review passed: All ${totalCount} dimensions passed successfully.`;
  }
}

export function createReviewerAgent(options: ReviewerAgentOptions): ReviewerAgent {
  return new ReviewerAgent(options);
}