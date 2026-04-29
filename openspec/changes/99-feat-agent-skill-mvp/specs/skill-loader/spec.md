## ADDED Requirements

### Requirement: Skill 加载器扫描 .mohist/skills/ 目录

系统 SHALL 扫描当前项目的 `.mohist/skills/` 目录，识别所有子目录中的 SKILL.md 文件。每个子目录代表一个 skill，子目录名即为 skill 的默认标识符。

#### Scenario: 发现单个 skill

- **WHEN** 项目路径下存在 `.mohist/skills/analyze-codebase/SKILL.md`
- **THEN** 系统识别到名为 `analyze-codebase` 的 skill
- **AND** skill 的 `projectPath` 指向该项目的根目录

#### Scenario: 发现多个 skills

- **WHEN** 项目路径下存在 `.mohist/skills/analyze-codebase/SKILL.md` 和 `.mohist/skills/gen-tests/SKILL.md`
- **THEN** 系统识别到 2 个 skills
- **AND** 可通过 name 分别获取每个 skill

#### Scenario: skills 目录不存在

- **WHEN** 项目路径下不存在 `.mohist/skills/` 目录
- **THEN** 系统返回空列表，不报错

#### Scenario: 子目录中无 SKILL.md

- **WHEN** `.mohist/skills/` 下存在子目录 `foo/` 但其中没有 `SKILL.md`
- **THEN** 该子目录被忽略，不注册为 skill

### Requirement: SKILL.md 支持 YAML frontmatter 元数据

SKILL.md SHALL 支持 YAML frontmatter 格式，包含以下字段：

- `name`（可选，默认为目录名）：skill 的唯一标识符，kebab-case
- `description`（必需）：一句话描述 skill 的功能
- `prompt`（可选）：完整 prompt 文本。如果省略，则使用 frontmatter 之后的 Markdown 正文作为 prompt

frontmatter 之后的 Markdown 正文 SHALL 作为 skill 的 prompt 内容（当 `prompt` 字段未提供时）。

#### Scenario: 完整 frontmatter

- **WHEN** SKILL.md 内容为：
  ```
  ---
  name: analyze-codebase
  description: Analyze the codebase and create improvement issues
  prompt: Analyze the project structure, identify technical debt, and create issues for improvements.
  ---
  ```
- **THEN** 解析出 name=`analyze-codebase`，description 为对应值，prompt 为对应值

#### Scenario: 仅 frontmatter，无 prompt 字段

- **WHEN** SKILL.md 内容为：
  ```
  ---
  name: analyze-codebase
  description: Analyze the codebase
  ---
  Analyze the project and create issues.
  ```
- **THEN** prompt 使用 frontmatter 之后的 Markdown 正文 `"Analyze the project and create issues."`

#### Scenario: 无 frontmatter

- **WHEN** SKILL.md 没有 `---` 分隔的 frontmatter
- **THEN** name 默认为子目录名
- **AND** description 默认为 `"Skill: <directory-name>"`
- **AND** prompt 使用整个文件内容

#### Scenario: frontmatter 缺少 description

- **WHEN** SKILL.md 的 frontmatter 没有 `description` 字段
- **THEN** 使用子目录名作为 description

### Requirement: Skill 注册到数据库

系统 SHALL 将发现的 skill 注册到 SQLite `skills` 表，包含字段：`id`（主键）、`name`（唯一）、`project_id`、`description`、`prompt`、`dir_path`（skill 目录绝对路径）、`created_at`、`updated_at`。如果 skill 已存在（按 `name` 匹配），SHALL 更新其元数据。

#### Scenario: 首次注册 skill

- **WHEN** 系统扫描发现新 skill
- **THEN** 在 `skills` 表插入一条记录
- **AND** `name` 为 skill 的标识符

#### Scenario: skill 已存在时更新

- **WHEN** skill 的 `name` 在 `skills` 表中已存在
- **THEN** 更新该 skill 的 `description`、`prompt`、`updated_at`
- **AND** 保持 `id` 不变

### Requirement: 按项目加载 skills

系统 SHALL 支持按 projectId 查询已注册的 skills 列表，返回该项目的所有 skills。

#### Scenario: 列出项目 skills

- **WHEN** 调用 `getByProject(projectId)`
- **THEN** 返回该项目的所有已注册 skills

### Requirement: SkillService 初始化时自动加载

SkillService 在实例化时 SHALL 触发一次 skill 扫描，将当前项目 `.mohist/skills/` 下的所有 skills 注册到数据库。

#### Scenario: 服务启动自动发现 skills

- **WHEN** SkillService 被创建并传入项目路径
- **THEN** 自动扫描 `.mohist/skills/` 目录
- **AND** 所有有效 SKILL.md 被注册到数据库
