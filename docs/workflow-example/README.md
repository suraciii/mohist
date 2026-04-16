# OpenSpec Workflow Example

This directory contains example workflow configurations for the OpenSpec workflow.

## Files

- `workflow-openspec.yaml` - Full OpenSpec workflow with 4 stages
- `workflow-traditional.yaml` - Traditional Mohist workflow (backward compatible)
- `prd-example.json` - Example prd.json from a real change

## Usage

Copy the desired workflow.yaml to your project root:

```bash
cp docs/workflow-example/workflow-openspec.yaml workflow.yaml
```

Or use the traditional workflow if you prefer:

```bash
cp docs/workflow-example/workflow-traditional.yaml workflow.yaml
```

## OpenSpec Workflow (Recommended)

```yaml
name: openspec-workflow
version: "1.0"

stages:
  - name: plan
    description: Generate Change artifacts with self-review
    entry: auto  # or: mo propose <issue>
    exit: prd.json exists
    
  - name: review
    description: Human review and approval
    entry: manual
    exit: user approved
    
  - name: build
    description: Ralph-style task loop execution
    entry: auto
    exit: all tasks completed
    
  - name: check
    description: Auto tests + human acceptance + archival
    entry: auto
    exit: user approved

auto_proceed:
  enabled: true
  stages:
    - plan    # Auto-proceed if self-review passes
    - build   # Auto-proceed between tasks

approval_gates:
  - stage: review
    message: "Review Change artifacts before build"
  - stage: check
    message: "Accept implementation before archival"

ralph:
  max_retries: 3
  failure_handling:
    ac_unsatisfied: retry
    environment_error: retry
    code_dependency: ask_user
    timeout: ask_user

archival:
  enabled: true
  path: "openspec/changes/archive"
```

## Traditional Workflow

```yaml
name: traditional-workflow
version: "1.0"

stages:
  - name: plan
    description: Explore issue and create design
    entry: mo issue start <issue>
    exit: design complete
    
  - name: build
    description: Implement design
    entry: auto
    exit: implementation complete
    
  - name: check
    description: Run tests and verify
    entry: auto
    exit: tests pass

approval_gates:
  - stage: check
    message: "Review implementation"
```