## MODIFIED Requirements

### Requirement: 数据库扩展

系统 SHALL 扩展现有 SQLite schema。The local issue store SHALL persist issue-level start prerequisites as relationships from an Issue to its prerequisite issues, and SHALL provide reads needed to compute prerequisite delivery state and reject circular prerequisite declarations.

#### Scenario: Issues 表扩展
- **WHEN** 数据库迁移到 schema version 2
- **THEN** issues 表新增 `labels` 列（TEXT，JSON 数组）
- **AND** 现有 issues 的 labels 默认为 `[]`

#### Scenario: Comments 表创建
- **WHEN** 数据库迁移到 schema version 2
- **THEN** 创建 comments 表
- **AND** comments 表包含 id, issue_id, body, created_at 字段

#### Scenario: Start prerequisite records are persisted
- **WHEN** Issue #201 records Issue #200 as a prerequisite issue
- **THEN** the local issue store persists the relationship from Issue #201 to Issue #200
- **AND** subsequent Issue reads can return Issue #201 with that start prerequisite

#### Scenario: Circular declaration can be evaluated from stored prerequisites
- **WHEN** the system evaluates whether Issue #200 may record Issue #201 as a prerequisite issue
- **THEN** the local issue store SHALL provide enough prerequisite lookup data to detect whether the declaration would make Issue #200 require itself before start
- **AND** the store SHALL NOT require parsing issue body text to answer that question
