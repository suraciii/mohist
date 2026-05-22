import { describe, expect, it } from 'vitest';
import * as yaml from 'yaml';
import {
  parseWorkflowDefinitionSource,
  workflowDefinitionSourceToYaml,
} from '../../src';

describe('workflow definition source parser', () => {
  it('parses complete workflow YAML with arbitrary stage ids', () => {
    const source = yaml.parse(`
workflow:
  id: project/custom
  artifacts:
    change: "{{ openspec.changeDir }}"
  stages:
    - id: triage
      tasks:
        - id: summarize
          title: Summarize issue
          uses: custom/agent
          with:
            prompt:
              inline: "Summarize {{ issue.title }}"
      checks:
        - id: summary-exists
          title: Summary exists
          uses: custom/artifact-exists
          with:
            path: "{{ artifacts.change }}/summary.md"
`).workflow;
    const parsed = parseWorkflowDefinitionSource(source);

    expect(parsed.id).toBe('project/custom');
    expect(parsed.artifacts).toEqual({ change: '{{ openspec.changeDir }}' });
    expect(parsed.stages).toEqual([
      {
        stage: 'triage',
        tasks: [
          {
            id: 'summarize',
            title: 'Summarize issue',
            uses: 'custom/agent',
            with: {
              prompt: {
                inline: 'Summarize {{ issue.title }}',
              },
            },
          },
        ],
        checks: [
          {
            name: 'summary-exists',
            title: 'Summary exists',
            uses: 'custom/artifact-exists',
            with: {
              path: '{{ artifacts.change }}/summary.md',
            },
          },
        ],
      },
    ]);
  });

  it('round-trips semantic workflow source back to YAML', () => {
    const yamlText = workflowDefinitionSourceToYaml({
      id: 'project/custom',
      stages: [
        {
          id: 'review',
          tasks: [
            {
              id: 'review',
              title: 'Review',
              uses: 'custom/review',
              with: {
                prompt: {
                  inline: 'Review the change.',
                },
              },
            },
          ],
          checks: [
            {
              id: 'review-passed',
              title: 'Review passed',
              uses: 'custom/marker',
              onFailure: {
                retry: {
                  limit: 2,
                  task: {
                    id: 'fix-review',
                    title: 'Fix review',
                    uses: 'custom/agent',
                  },
                },
              },
            },
          ],
        },
      ],
    });

    expect(parseWorkflowDefinitionSource(yaml.parse(yamlText).workflow).id).toBe('project/custom');
    expect(yamlText).toContain('workflow:');
    expect(yamlText).toContain('onFailure:');
  });
});
