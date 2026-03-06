## ADDED Requirements

### Requirement: OpenSpec CLI Integration

The system SHALL integrate with OpenSpec CLI for spec generation.

#### Scenario: Check OpenSpec availability

- **WHEN** starting design stage
- **THEN** system SHALL check if OpenSpec CLI is installed
- **AND** system SHALL verify the minimum required version (>= v1.0.0)

#### Scenario: Auto-generate specs using OpenSpec

- **WHEN** entering design stage AND OpenSpec CLI is available
- **THEN** sub-agent SHALL automatically call `openspec propose issue-{N}`
- **AND** sub-agent SHALL follow OpenSpec workflow
- **AND** specs SHALL be stored in `openspec/changes/issue-{N}/`

#### Scenario: Use OpenSpec changes directory

- **WHEN** OpenSpec is available
- **THEN** specs SHALL be stored in openspec/changes/<name>/
- **AND** specs SHALL follow OpenSpec schema

### Requirement: Fallback to Manual Specs

The system SHALL support manual spec generation when OpenSpec is unavailable.

#### Scenario: Generate specs without OpenSpec

- **WHEN** OpenSpec CLI is not available
- **THEN** sub-agent SHALL generate specs manually
- **AND** specs SHALL be stored in specs/issue-{N}.md

#### Scenario: Document in PR body

- **WHEN** not using OpenSpec format
- **THEN** PR body SHALL explain the spec format used
- **AND** PR body SHALL include note: "**注意**: 未使用 OpenSpec 格式，specs 手动生成"

### Requirement: Spec Content Requirements

All specs SHALL include required sections regardless of format.

#### Scenario: Include required sections

- **WHEN** generating specs
- **THEN** specs SHALL include:
  - Why (motivation)
  - What Changes
  - Capabilities
  - Impact

#### Scenario: Testable requirements

- **WHEN** defining requirements in specs
- **THEN** each requirement SHALL be testable
- **AND** each requirement SHALL have at least one scenario

## Implementation Notes

### OpenSpec CLI Detection

```bash
# Check if openspec is available
if ! command -v openspec &> /dev/null; then
  echo "OpenSpec CLI not found, using manual spec generation"
  USE_OPENSPEC=false
else
  VERSION=$(openspec --version | grep -oE '[0-9]+\.[0-9]+\.[0-9]+')
  # Compare versions (simplified)
  USE_OPENSPEC=true
fi
```

### Spec Generation in Sub-agent

Sub-agent prompt should include:
```
你正在为 Issue #{N} 生成设计规范。

{% if USE_OPENSPEC %}
使用 OpenSpec CLI 生成规范：
1. 运行 `openspec propose issue-{N}`
2. 按照提示创建 proposal.md, design.md, specs/*.md, tasks.md
{% else %}
手动生成规范文件 specs/issue-{N}.md，包含：
- Why: 为什么需要这个变更
- What Changes: 变更内容
- Capabilities: 新增/修改的能力
- Impact: 影响范围
{% endif %}
```
