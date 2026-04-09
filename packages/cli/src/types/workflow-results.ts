import type { PrdJson } from '../artifacts/change-artifacts-manager';

export interface ReviewIssue {
  severity: 'error' | 'warning';
  location: string;
  message: string;
  suggestion?: string;
}

export interface DimensionResult {
  name: string;
  passed: boolean;
  reasoning: string;
  issues?: ReviewIssue[];
}

export interface ReviewResult {
  passed: boolean;
  dimensions: DimensionResult[];
  overallReasoning: string;
  duration: number;
  fixSuggestions?: string[];
}

export interface PlanResult {
  success: boolean;
  changePath: string;
  artifacts: {
    proposal: string;
    design: string;
    specs: Array<{ name: string; content: string }>;
    prd: PrdJson | null;
  };
  iterations: number;
  duration: number;
  selfReviewNotes?: string;
  error?: string;
}