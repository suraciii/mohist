import type { Issue } from '../types';
import type { ChangeArtifactsManager, PlanResult } from '../workflow/workflow-controller';
import type { LlmConfig } from '../agent-runtime';
import { resolveModel } from '../agent-runtime';
import { ToolRegistry } from '../agent-runtime/tool';
import { streamText } from 'ai';
import { createReadFileTool } from '../tools/read-file';
import { createGlobTool } from '../tools/glob-tool';
import { createGrepTool } from '../tools/grep-tool';

const DEFAULT_MAX_ITERATIONS = 3;

export const PLANNER_DEFAULT_PROMPT = `role: planner
name: Mohist Planner
description: |
  You are a Planner Agent for Mohist workflow.
  Your job is to create comprehensive design artifacts for a software change.

steps:
  1_explore:
    action: Explore the codebase
    details: |
      - Read existing code to understand patterns
      - Identify relevant files and components
      - Understand the architecture

  2_analyze:
    action: Analyze requirements
    details: |
      - Read the issue title and description
      - Identify what needs to be built
      - Clarify ambiguities if needed

  3_design:
    action: Create design artifacts
    artifacts:
      proposal.md:
        sections:
          - Problem: What problem does this solve?
          - Solution: High-level approach
          - Impact: Expected outcomes
          - Timeline: Rough estimate
      
      design.md:
        sections:
          - Overview: Architecture summary
          - Decisions: Key technical choices
          - Components: Main parts and interactions
          - Risks: Potential issues
      
      specs/:
        format: |
          ## ADDED Requirements
          
          ### Requirement: {capability}
          
          #### Scenario: {scenario}
          - **GIVEN** {context}
          - **WHEN** {action}
          - **THEN** {expected outcome}
      
      prd.json:
        format: |
          {
            "project": "project-name",
            "description": "...",
            "tasks": [
              {
                "id": "T-001",
                "title": "...",
                "description": "...",
                "acceptanceCriteria": [...]
              }
            ]
          }

  4_review:
    action: Self-review the design
    criteria:
      completeness:
        check: Are all requirements covered?
        severity: error if missing
      
      consistency:
        check: Does it align with existing patterns?
        severity: warning if different
      
      feasibility:
        check: Can this be implemented?
        severity: error if impossible
      
      risks:
        check: Are risks identified?
        severity: warning if missing

  5_fix:
    action: Fix identified issues
    condition: If any review criteria failed
    max_iterations: 3

output_format:
  final_artifacts:
    - proposal.md
    - design.md
    - specs/*.md
    - prd.json
  
  review_summary: |
    Provide a brief summary of the self-review:
    - Number of issues found and fixed
    - Any remaining concerns
    - Confidence level (high/medium/low)
`;

export interface PlannerAgentOptions {
  llmConfig?: LlmConfig;
  artifactManager: ChangeArtifactsManager;
  defaultPrompt?: string;
}

export interface CodebaseInfo {
  keyFiles: string[];
  patterns: string[];
  architecture: string;
}

export class PlannerAgent {
  private llmConfig?: LlmConfig;
  private artifactManager: ChangeArtifactsManager;
  private defaultPrompt: string;

  constructor(options: PlannerAgentOptions) {
    this.llmConfig = options.llmConfig;
    this.artifactManager = options.artifactManager;
    this.defaultPrompt = options.defaultPrompt ?? PLANNER_DEFAULT_PROMPT;
  }

  async plan(options: {
    issue: Issue;
    worktreePath: string;
    prompt?: string;
  }): Promise<PlanResult> {
    const { issue, worktreePath, prompt } = options;
    const activePrompt = prompt ?? this.defaultPrompt;

    const changeDir = this.artifactManager.getChangeDir(issue.number) || this.artifactManager.createChangeDir(issue.number, issue.title);
    if (!changeDir) {
      return {
        success: false,
        artifacts: { proposal: '', design: '', specs: [], prd: null },
        selfReviewNotes: 'Failed to create change directory',
        iterations: 0,
      };
    }

    try {
      const codebaseInfo = await this.exploreCodebase(worktreePath, issue);
      const artifacts = await this.generateArtifacts(issue, codebaseInfo, changeDir);

      let iterations = 0;
      let currentArtifacts = artifacts;
      const maxIterations = this.extractMaxIterations(activePrompt);

      while (iterations < maxIterations) {
        const reviewResult = await this.selfReview(currentArtifacts, activePrompt);

        if (reviewResult.passed) {
          return {
            success: true,
            artifacts: {
              proposal: currentArtifacts.proposal,
              design: currentArtifacts.design,
              specs: Array.from(currentArtifacts.specs.entries()).map(([name, content]) => ({
                name,
                content,
              })),
              prd: currentArtifacts.prd,
            },
            selfReviewNotes: reviewResult.summary,
            iterations: iterations + 1,
          };
        }

        currentArtifacts = await this.fixIssues(currentArtifacts, reviewResult.issues, changeDir);
        iterations++;
      }

      return {
        success: true,
        artifacts: {
          proposal: currentArtifacts.proposal,
          design: currentArtifacts.design,
          specs: Array.from(currentArtifacts.specs.entries()).map(([name, content]) => ({
            name,
            content,
          })),
          prd: currentArtifacts.prd,
        },
        selfReviewNotes: `Max iterations (${maxIterations}) reached. Some issues may remain.`,
        iterations,
      };
    } catch (error) {
      return {
        success: false,
        artifacts: { proposal: '', design: '', specs: [], prd: null },
        selfReviewNotes: error instanceof Error ? error.message : 'Unknown error during planning',
        iterations: 0,
      };
    }
  }

  private async exploreCodebase(worktreePath: string, issue: Issue): Promise<CodebaseInfo> {
    const model = resolveModel(this.llmConfig);
    const toolRegistry = this.buildExploreToolRegistry(worktreePath);

    const explorationPrompt = `Explore the codebase to understand the existing patterns relevant to this issue:

Issue #${issue.number}: ${issue.title}
${issue.body ? `\nDescription:\n${issue.body}\n` : ''}

Please identify:
1. Key files and components that are relevant
2. Existing patterns and conventions
3. Overall architecture

Be concise - provide a summary of your findings.`;

    const result = await streamText({
      model,
      system: 'You are an exploration agent. Analyze the codebase and provide key findings.',
      messages: [{ role: 'user', content: explorationPrompt }],
      tools: toolRegistry.toToolSet(),
    });

    const findings = await result.text;

    return this.parseCodebaseFindings(findings);
  }

  private buildExploreToolRegistry(cwd: string): ToolRegistry {
    const registry = new ToolRegistry();
    registry.register(createReadFileTool({ projectPath: cwd }));
    registry.register(createGlobTool({ projectPath: cwd }));
    registry.register(createGrepTool({ projectPath: cwd }));
    return registry;
  }

  private parseCodebaseFindings(findings: string): CodebaseInfo {
    const keyFiles: string[] = [];
    const patterns: string[] = [];
    let architecture = 'Unknown';

    const fileMatch = findings.match(/files?[:\s]+([^\n]+)/i);
    if (fileMatch) {
      keyFiles.push(...fileMatch[1].split(/[,;\n]/).map(f => f.trim()).filter(Boolean));
    }

    const patternMatch = findings.match(/patterns?[:\s]+([^\n]+)/i);
    if (patternMatch) {
      patterns.push(...patternMatch[1].split(/[,;\n]/).map(p => p.trim()).filter(Boolean));
    }

    const archMatch = findings.match(/architecture[:\s]+([^\n]+)/i);
    if (archMatch) {
      architecture = archMatch[1].trim();
    }

    return { keyFiles, patterns, architecture };
  }

  private async generateArtifacts(
    issue: Issue,
    codebaseInfo: CodebaseInfo,
    changeDir: string
  ): Promise<{ proposal: string; design: string; specs: Map<string, string>; prd: unknown }> {
    const model = resolveModel(this.llmConfig);

    const prompt_text = `You are a Planner Agent creating design artifacts for this issue:

Issue #${issue.number}: ${issue.title}
${issue.body ? '\nDescription:\n' + issue.body + '\n' : ''}

Codebase Findings:
- Key Files: ${codebaseInfo.keyFiles.join(', ') || 'None identified'}
- Patterns: ${codebaseInfo.patterns.join(', ') || 'None identified'}
- Architecture: ${codebaseInfo.architecture}

Create the following artifacts and return them as a single JSON object:

{
  "proposal": "## Why\\n\\n[Content...]\\n\\n## What Changes\\n\\n[Content...]",
  "design": "## Context\\n\\n[Content...]\\n\\n## Decisions\\n\\n[Content...]",
  "specs": [
    {"name": "capability-name", "content": "## ADDED Requirements\\n\\n### Requirement: [name]\\n\\n..."}
  ],
  "prd": {
    "project": "project-name",
    "description": "...",
    "tasks": [
      {"id": "T-001", "title": "...", "description": "...", "acceptanceCriteria": [...]}
    ]
  }
}

Return ONLY a valid JSON object with no additional text. The JSON must include:
- proposal: Markdown string with ## Why and ## What Changes sections
- design: Markdown string with ## Context, ## Goals/Non-Goals, ## Decisions, ## Risks sections
- specs: Array of {name, content} objects, each content is markdown
- prd: Object with project, description, and tasks array

Generate high-quality artifacts that cover all requirements. Be comprehensive and detailed.`;

    const result = await streamText({
      model,
      system: 'You are a Planner Agent. Return design artifacts as structured JSON. No tools needed.',
      messages: [{ role: 'user', content: prompt_text }],
    });

    const text = await result.text;
    const artifacts = this.parseArtifactsJson(text);

    await this.writeArtifactsToFiles(changeDir, artifacts);

    return {
      proposal: artifacts.proposal,
      design: artifacts.design,
      specs: new Map(artifacts.specs.map(s => [s.name, s.content])),
      prd: artifacts.prd,
    };
  }

  private parseArtifactsJson(
    text: string
  ): { proposal: string; design: string; specs: { name: string; content: string }[]; prd: unknown } {
    let jsonStr = text.trim();

    const jsonMatch = text.match(/```(?:json)?\s*([\s\S]*?)\s*```/);
    if (jsonMatch) {
      jsonStr = jsonMatch[1].trim();
    }

    try {
      const parsed = JSON.parse(jsonStr);
      return {
        proposal: parsed.proposal || '',
        design: parsed.design || '',
        specs: Array.isArray(parsed.specs) ? parsed.specs : [],
        prd: parsed.prd || null,
      };
    } catch (error) {
      console.error('Failed to parse artifacts JSON:', error);
      console.error('Raw text:', text.slice(0, 500));
      return {
        proposal: '',
        design: '',
        specs: [],
        prd: null,
      };
    }
  }

  private async writeArtifactsToFiles(
    changeDir: string,
    artifacts: { proposal: string; design: string; specs: { name: string; content: string }[]; prd: unknown }
  ): Promise<void> {
    this.artifactManager.writeArtifact(changeDir, 'proposal.md', artifacts.proposal);
    this.artifactManager.writeArtifact(changeDir, 'design.md', artifacts.design);

    for (const spec of artifacts.specs) {
      this.artifactManager.writeArtifact(changeDir, `specs/${spec.name}.md`, spec.content);
    }

    if (artifacts.prd) {
      this.artifactManager.writeArtifact(changeDir, 'prd.json', JSON.stringify(artifacts.prd, null, 2));
    }
  }

  private async selfReview(
    artifacts: { proposal: string; design: string; specs: Map<string, string>; prd: unknown },
    prompt: string
  ): Promise<{ passed: boolean; issues: string[]; summary: string }> {
    const model = resolveModel(this.llmConfig);
    const reviewCriteria = this.extractReviewCriteria(prompt);

    const prompt_text = `You are reviewing the following design artifacts:

**Proposal:**
${artifacts.proposal.slice(0, 2000)}${artifacts.proposal.length > 2000 ? '...' : ''}

**Design:**
${artifacts.design.slice(0, 2000)}${artifacts.design.length > 2000 ? '...' : ''}

**Specs:** ${Array.from(artifacts.specs.keys()).join(', ')}

**PRD:** ${JSON.stringify(artifacts.prd).slice(0, 1000)}${JSON.stringify(artifacts.prd).length > 1000 ? '...' : ''}

Review criteria: ${reviewCriteria.join(', ')}

For each criterion, determine if it is satisfied. Then provide:
1. passed: true/false
2. issues: array of specific issues found (if any)
3. summary: brief explanation

Be critical but fair.`;

    const result = await streamText({
      model,
      system: 'You are a self-review agent. Evaluate the design artifacts against the criteria.',
      messages: [{ role: 'user', content: prompt_text }],
    });

    const reviewText = await result.text;

    return this.parseReviewResult(reviewText);
  }

  private parseReviewResult(text: string): { passed: boolean; issues: string[]; summary: string } {
    const passed = text.toLowerCase().includes('passed: true') || text.toLowerCase().includes('"passed": true');
    const issues: string[] = [];

    const issueMatches = text.match(/issues?[:\s]+\[?([^\]]+)\]?/gi);
    if (issueMatches) {
      for (const match of issueMatches) {
        const issueList = match.replace(/issues?[:\s]+\[?/gi, '').replace(/\]/g, '');
        issues.push(...issueList.split(/[,;]/).map(i => i.trim()).filter(Boolean));
      }
    }

    const summaryMatch = text.match(/summary[:\s]+([^\n]+(?:\n[^\n]+)*)/i);
    const summary = summaryMatch ? summaryMatch[1].trim() : 'Review completed';

    return { passed, issues, summary };
  }

  private async fixIssues(
    artifacts: { proposal: string; design: string; specs: Map<string, string>; prd: unknown },
    issues: string[],
    changeDir: string
  ): Promise<{ proposal: string; design: string; specs: Map<string, string>; prd: unknown }> {
    const model = resolveModel(this.llmConfig);

    const issuesList = issues.map((issue, i) => `${i + 1}. ${issue}`).join('\n');
    const prompt_text = `Fix the following issues in the design artifacts:

${issuesList}

Current artifacts:
- Proposal: ${artifacts.proposal.slice(0, 1000)}...
- Design: ${artifacts.design.slice(0, 1000)}...
- Specs: ${Array.from(artifacts.specs.keys()).join(', ')}

Return ONLY a valid JSON object with no additional text:

{
  "proposal": "[Updated proposal markdown]",
  "design": "[Updated design markdown]",
  "specs": [
    {"name": "capability-name", "content": "[Updated spec content]"}
  ],
  "prd": {...}
}

Fix the specific issues identified. Be thorough and address each point.`;

    const result = await streamText({
      model,
      system: 'You are a Planner Agent. Fix design artifacts and return as structured JSON. No tools needed.',
      messages: [{ role: 'user', content: prompt_text }],
    });

    const text = await result.text;
    const fixedArtifacts = this.parseArtifactsJson(text);

    await this.writeArtifactsToFiles(changeDir, fixedArtifacts);

    return {
      proposal: fixedArtifacts.proposal,
      design: fixedArtifacts.design,
      specs: new Map(fixedArtifacts.specs.map(s => [s.name, s.content])),
      prd: fixedArtifacts.prd,
    };
  }

  private extractMaxIterations(prompt: string): number {
    const match = prompt.match(/max_iterations[:\s]*(\d+)/i);
    return match ? parseInt(match[1], 10) : DEFAULT_MAX_ITERATIONS;
  }

  private extractReviewCriteria(prompt: string): string[] {
    const criteria: string[] = [];
    const completenessMatch = prompt.match(/completeness[:\s]([^\n]+)/i);
    if (completenessMatch) criteria.push('Completeness: ' + completenessMatch[1]);
    const consistencyMatch = prompt.match(/consistency[:\s]([^\n]+)/i);
    if (consistencyMatch) criteria.push('Consistency: ' + consistencyMatch[1]);
    const feasibilityMatch = prompt.match(/feasibility[:\s]([^\n]+)/i);
    if (feasibilityMatch) criteria.push('Feasibility: ' + feasibilityMatch[1]);
    const risksMatch = prompt.match(/risks[:\s]([^\n]+)/i);
    if (risksMatch) criteria.push('Risks: ' + risksMatch[1]);

    return criteria.length > 0 ? criteria : ['completeness', 'consistency', 'feasibility', 'risks'];
  }
}

export function createPlannerAgent(options: PlannerAgentOptions): PlannerAgent {
  return new PlannerAgent(options);
}