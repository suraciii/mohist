import * as fs from 'fs';
import * as path from 'path';
import * as yaml from 'yaml';

export interface PromptConfig {
  role: string;
  name: string;
  description?: string;
  steps?: Record<string, unknown>;
  criteria?: Record<string, unknown>;
  dimensions?: Record<string, unknown>;
  output_format?: Record<string, unknown>;
  [key: string]: unknown;
}

export function loadPromptFromFile(filePath: string): string {
  try {
    const content = fs.readFileSync(filePath, 'utf-8');
    const parsed = yaml.parse(content);
    if (parsed && typeof parsed === 'object') {
      return yaml.stringify(parsed);
    }
    return content;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') {
      throw new Error(`Prompt file not found: ${filePath}`);
    }
    throw new Error(`Failed to load prompt from ${filePath}: ${error instanceof Error ? error.message : String(error)}`);
  }
}

export function loadPrompt(promptPath: string): string {
  if (!fs.existsSync(promptPath)) {
    throw new Error(`Prompt file does not exist: ${promptPath}`);
  }
  return loadPromptFromFile(promptPath);
}

export function resolvePromptPath(promptsDir: string, promptFile: string): string {
  return path.join(promptsDir, promptFile);
}

export const DEFAULT_PROMPTS_DIR = path.join(__dirname, 'prompts');

export function loadDefaultPrompt(promptName: string): string {
  const promptPath = resolvePromptPath(DEFAULT_PROMPTS_DIR, promptName);
  return loadPrompt(promptPath);
}

export function loadPlannerDefaultPrompt(): string {
  return loadDefaultPrompt('planner-default.yaml');
}

export function loadPlannerSelfReviewPrompt(): string {
  return loadDefaultPrompt('planner-self-review.yaml');
}

export function loadReviewerDefaultPrompt(): string {
  return loadDefaultPrompt('reviewer-default.yaml');
}