package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/url"
	"regexp"
	"strconv"
	"strings"
)

var issueListFields = []string{"number", "title", "status", "stage", "priority", "risk", "labels", "prereq", "epic", "github", "createdAt", "updatedAt"}
var issueFields = []string{"number", "title", "body", "status", "health", "projectId", "projectName", "labels", "priority", "risk", "model", "modelVariant", "agentConfig", "stageModels", "stageModelVariants", "createdAt", "updatedAt", "archivedAt", "completedAt", "approvalState", "blockedReason", "attention", "workflowRunId", "workflowStage", "workflowStatus", "workflowStageProgress", "workflowProfileId", "workflowProfileMode", "noWorkflow", "prerequisiteNumbers", "comments", "attachments", "prereq", "isDraft", "canStart", "canBeParent", "blocker", "repositoryName", "repository", "repositoryProblem", "github", "epic", "parentIssueRef", "childIssuesSummary", "children", "feedback", "watching", "muted"}
var issueResultFields = []string{"number", "title", "status", "stage", "priority", "risk", "labels", "body", "repository", "repositoryName", "prereq", "epic", "github", "workflowRunId", "createdAt", "updatedAt"}
var archiveFields = []string{"archived", "skipped", "skippedNumbers", "message"}
var epicListFields = []string{"projectId", "number", "title", "description", "priority", "status", "createdAt", "updatedAt", "progress", "pauseReason"}
var epicFields = []string{"projectId", "number", "title", "description", "priority", "status", "createdAt", "updatedAt", "linkedIssues", "progress", "nextIssueNumber", "nextIssueReason", "pauseReason"}
var membershipFields = []string{"identifier", "status", "issueNumber", "owningEpicNumber", "owningEpicTitle"}
var labelFields = []string{"key", "description", "supportedValues"}
var templateListFields = []string{"id", "name", "description", "source"}
var templateFields = []string{"id", "name", "description", "body", "source"}
var commentFields = []string{"id", "projectId", "issueNumber", "body", "createdAt", "attachments", "author", "displayName"}
var watchFields = []string{"number", "watching", "muted"}

var labelKeyPattern = regexp.MustCompile(`^[a-z0-9]([-a-z0-9]*[a-z0-9])?$`)
var ansiEscapePattern = regexp.MustCompile(`\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1b\\))`)

func parseOrganization(group string, args []string) (command, error) {
	if len(args) == 0 || (len(args) == 1 && (args[0] == "--help" || args[0] == "-h")) {
		return command{help: true, helpText: organizationHelp(group)}, nil
	}
	if args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: organizationHelp(group)}, nil
	}
	switch group {
	case "issue":
		return parseIssue(args)
	case "epic":
		return parseEpic(args)
	default:
		return parseLabel(args)
	}
}

func organizationHelp(group string) string {
	switch group {
	case "issue":
		return "USAGE\n    mo issue <action> [flags]\n\nManage Project-scoped Issues.\n\nActions: list, view, create, edit, start, done, close, reopen, archive, restore, rebase, diff, commits, logs, events, comment, prereq, template, variable, watch, github"
	case "epic":
		return "USAGE\n    mo epic <action> [flags]\n\nGroup Issues under Epics.\n\nActions: list, view, create, edit, add, remove, start, pause, resume, done, close, reopen"
	default:
		return "USAGE\n    mo label <action> [flags]\n\nManage Project label definitions.\n\nActions: list, create, edit, delete"
	}
}

func parseIssue(args []string) (command, error) {
	action := args[0]
	if !contains([]string{"list", "view", "create", "edit", "start", "done", "close", "reopen", "archive", "restore", "rebase", "diff", "commits", "logs", "events", "comment", "prereq", "template", "variable", "watch", "github"}, action) {
		return command{}, usage("unknown issue command")
	}
	if action == "comment" || action == "prereq" || action == "template" || action == "variable" || action == "watch" || action == "github" {
		return parseIssueNested(action, args[1:])
	}
	c := command{kind: "issue-" + action, catalog: issueResultFields}
	if action == "list" {
		c.catalog = issueListFields
		c.args = append(c.args, "collection", "true")
	}
	if action == "view" {
		c.catalog = issueFields
	}
	if action == "archive" && len(args) > 1 && args[1] == "--all-completed" {
		c.kind = "issue-archive-all"
		c.catalog = archiveFields
	}
	if action == "rebase" || action == "diff" || action == "commits" || action == "logs" || action == "events" {
		c.catalog = nil
	}
	start := 1
	if action != "list" && !(action == "archive" && c.kind == "issue-archive-all") {
		if len(args) <= 1 {
			return command{}, usage("issue number is required")
		}
		c.args = append(c.args, "number", args[1])
		start = 2
	}
	if action == "archive" && c.kind == "issue-archive-all" {
		start = 2
	}
	if action == "create" {
		c = command{kind: "issue-create", catalog: issueResultFields}
		start = 1
		if len(args) == 1 {
			return command{}, usage("issue title is required")
		}
		c.args = append(c.args, "title", args[1])
		start = 2
	}
	if action == "edit" && len(args) == 1 {
		return command{}, usage("issue number is required")
	}
	return parseIssueOptions(c, args[start:], action)
}

func parseIssueOptions(c command, args []string, action string) (command, error) {
	for i := 0; i < len(args); i++ {
		arg := args[i]
		switch arg {
		case "--project", "--stage", "--priority", "--risk", "--repo", "--parent", "--model", "--model-variant", "--workflow-profile", "--stage-models", "--stage-models-file", "--stage-model-variants", "--stage-model-variants-file", "--base-branch":
			if i+1 >= len(args) {
				return command{}, usage(arg + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(arg, "--"), args[i+1])
			i++
		case "--label":
			if i+1 >= len(args) {
				return command{}, usage("--label requires a value")
			}
			c.args = append(c.args, "label", args[i+1])
			i++
		case "--title", "--body", "--body-file":
			if i+1 >= len(args) {
				return command{}, usage(arg + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(arg, "--"), args[i+1])
			i++
		case "--all", "--archived", "--ready", "--draft", "--no-workflow", "--inherit-workflow-profile":
			c.args = append(c.args, strings.TrimPrefix(arg, "--"), "true")
		case "--json":
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
		case "--help", "-h":
			return command{help: true, helpText: leafHelp(c.kind, c.catalog)}, nil
		default:
			return command{}, usage("unknown option " + arg)
		}
	}
	if action == "list" && hasArg(c.args, "all") && hasArg(c.args, "archived") {
		return command{}, usage("--all and --archived are mutually exclusive")
	}
	if action == "create" {
		if !hasArg(c.args, "body") && !hasArg(c.args, "body-file") {
			return command{}, usage("exactly one of --body or --body-file is required")
		}
		if hasArg(c.args, "body") && hasArg(c.args, "body-file") {
			return command{}, usage("--body and --body-file are mutually exclusive")
		}
	}
	if action == "edit" {
		if hasArg(c.args, "body") && hasArg(c.args, "body-file") {
			return command{}, usage("--body and --body-file are mutually exclusive")
		}
		if hasArg(c.args, "workflow-profile") && (hasArg(c.args, "inherit-workflow-profile") || hasArg(c.args, "no-workflow")) {
			return command{}, usage("workflow profile options are mutually exclusive")
		}
		if hasArg(c.args, "ready") && hasArg(c.args, "draft") {
			return command{}, usage("--ready and --draft are mutually exclusive")
		}
	}
	if (action == "create" || action == "edit") && hasArg(c.args, "stage-models") && hasArg(c.args, "stage-models-file") {
		return command{}, usage("stage model options are mutually exclusive")
	}
	if err := validateFields(c.fields, c.catalog, "mo issue "+action); err != nil {
		return command{}, err
	}
	return c, nil
}

func parseIssueNested(area string, args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: "USAGE\n    mo issue " + area + " <action> [flags]"}, nil
	}
	action := args[0]
	c := command{kind: "issue-" + area + "-" + action}
	start := 1
	switch area {
	case "template":
		if action != "list" && action != "view" {
			return command{}, usage("unknown issue template command")
		}
		c.catalog = templateListFields
		if action == "view" {
			c.catalog = templateFields
			if len(args) < 2 {
				return command{}, usage("template name is required")
			}
			c.args = append(c.args, "name", args[1])
			start = 2
		}
	case "comment":
		if action != "create" || len(args) < 2 {
			return command{}, usage("issue number is required")
		}
		c.catalog = commentFields
		c.args = append(c.args, "number", args[1])
		start = 2
	case "prereq":
		if (action != "add" && action != "remove") || len(args) < 3 {
			return command{}, usage("issue and prerequisite numbers are required")
		}
		c.catalog = issueResultFields
		c.args = append(c.args, "number", args[1], "prereq-number", args[2])
		start = 3
	case "watch":
		if len(args) < 2 {
			return command{}, usage("issue number is required")
		}
		c.args = append(c.args, "number", args[1])
		start = 2
		if action == "list" {
			c.catalog = watchFields
		} else if action != "add" && action != "remove" {
			return command{}, usage("unknown issue watch command")
		}
	case "github":
		if len(args) < 2 {
			return command{}, usage("issue number is required")
		}
		c.args = append(c.args, "number", args[1])
		start = 2
		if action == "link" {
			if len(args) < 3 {
				return command{}, usage("GitHub issue reference is required")
			}
			c.args = append(c.args, "github-ref", args[2])
			start = 3
		}
		c.catalog = issueFields
	case "variable":
		if len(args) < 2 || (action != "list" && action != "get" && action != "set" && action != "unset") {
			return command{}, usage("issue number and valid variable action are required")
		}
		c.args = append(c.args, "number", args[1])
		start = 2
		c.catalog = variableFields
		if action != "list" {
			if len(args) <= start {
				return command{}, usage("variable key is required")
			}
			c.args = append(c.args, "key", args[start])
			start++
		}
		if action == "set" && start < len(args) && !strings.HasPrefix(args[start], "-") {
			c.args = append(c.args, "value", args[start])
			start++
		}
	}
	for i := start; i < len(args); i++ {
		switch args[i] {
		case "--project", "--agent", "--display-name", "--body", "--body-file", "--stage", "--value-json":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--json":
			if c.catalog == nil {
				return command{}, usage("--json is not supported for this command")
			}
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
		case "--help", "-h":
			return command{help: true, helpText: leafHelp(c.kind, c.catalog)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if area == "comment" && !hasArg(c.args, "body") && !hasArg(c.args, "body-file") {
		return command{}, usage("comment body is required")
	}
	if area == "comment" && hasArg(c.args, "body") && hasArg(c.args, "body-file") {
		return command{}, usage("--body and --body-file are mutually exclusive")
	}
	if area == "variable" && action == "set" && hasArg(c.args, "value") == hasArg(c.args, "value-json") {
		return command{}, usage("set requires exactly one value source")
	}
	if area == "github" && action == "link" {
		parts := strings.Split(argValue(c.args, "github-ref", ""), "#")
		if len(parts) != 2 || strings.Count(parts[0], "/") != 1 {
			return command{}, usage("GitHub issue reference must be owner/repo#number")
		}
		n, err := strconv.Atoi(parts[1])
		if err != nil || n <= 0 {
			return command{}, usage("GitHub issue number must be positive")
		}
	}
	if area == "watch" && action != "list" && !hasArg(c.args, "agent") {
		return command{}, usage("--agent is required")
	}
	return c, validateFields(c.fields, c.catalog, "mo issue "+area+" "+action)
}

func parseEpic(args []string) (command, error) {
	action := args[0]
	c := command{kind: "epic-" + action, catalog: epicFields}
	start := 1
	if !contains([]string{"list", "view", "create", "edit", "add", "remove", "start", "pause", "resume", "done", "close", "reopen"}, action) {
		return command{}, usage("unknown epic command")
	}
	if action == "list" {
		c.catalog = epicListFields
	} else if action == "create" {
		if len(args) < 2 {
			return command{}, usage("epic title is required")
		}
		c.args = append(c.args, "title", args[1])
		start = 2
	} else {
		if len(args) < 2 {
			return command{}, usage("epic number is required")
		}
		c.args = append(c.args, "number", args[1])
		start = 2
	}
	if action == "add" || action == "remove" {
		if len(args) < 3 {
			return command{}, usage("epic and issue numbers are required")
		}
		c.args = append(c.args, "issue", args[2])
		start = 3
		c.catalog = membershipFields
	}
	for i := start; i < len(args); i++ {
		switch args[i] {
		case "--project", "--description", "--description-file", "--priority", "--title":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--json":
			if c.catalog == nil {
				return command{}, usage("--json is not supported for this command")
			}
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
		case "--help", "-h":
			return command{help: true, helpText: leafHelp(c.kind, c.catalog)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if action == "create" && hasArg(c.args, "description") && hasArg(c.args, "description-file") {
		return command{}, usage("--description and --description-file are mutually exclusive")
	}
	if action == "edit" && !hasArg(c.args, "title") && !hasArg(c.args, "description") && !hasArg(c.args, "description-file") && !hasArg(c.args, "priority") {
		return command{}, usage("at least one field is required to edit an epic")
	}
	return c, validateFields(c.fields, c.catalog, "mo epic "+action)
}

func parseLabel(args []string) (command, error) {
	action := args[0]
	c := command{kind: "label-" + action, catalog: nil}
	if action == "list" {
		c.catalog = labelFields
	} else {
		if len(args) < 2 {
			return command{}, usage("label key is required")
		}
		c.args = append(c.args, "key", args[1])
	}
	if !contains([]string{"list", "create", "edit", "delete"}, action) {
		return command{}, usage("unknown label command")
	}
	start := 1
	if action != "list" {
		start = 2
	}
	for i := start; i < len(args); i++ {
		switch args[i] {
		case "--project", "--description", "--supported-values":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--json":
			if c.catalog == nil {
				return command{}, usage("--json is not supported for this command")
			}
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
		case "--help", "-h":
			return command{help: true, helpText: leafHelp(c.kind, c.catalog)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if action == "create" && !hasArg(c.args, "description") {
		return command{}, usage("--description is required")
	}
	if (action == "create" || action == "edit") && !labelKeyPattern.MatchString(argValue(c.args, "key", "")) {
		return command{}, usage("label key must contain lowercase letters, numbers, and hyphens")
	}
	if action == "edit" && !hasArg(c.args, "description") && !hasArg(c.args, "supported-values") {
		return command{}, usage("at least one field is required to edit a label")
	}
	return c, validateFields(c.fields, c.catalog, "mo label "+action)
}

func runOrganization(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	project, ok := resolveProject(deps, argValue(cmd.args, "project", ""))
	if !ok {
		writeError(deps.Stderr, errors.New("Run 'mo project use <name-or-id>' or pass --project <name-or-id>"))
		return ExitOperation
	}
	base := "/api/projects/" + url.PathEscape(project)
	if cmd.kind == "issue-edit" && hasArg(cmd.args, "label") {
		return issueEditWithLabels(ctx, deps, c, cmd, base)
	}
	path, method, body, collection, err := organizationRequest(cmd, base, deps)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitUsage
	}
	if cmd.kind == "issue-watch-add" || cmd.kind == "issue-watch-remove" {
		return runWatch(ctx, deps, c, cmd, base, method, path, body)
	}
	data, err := c.request(ctx, method, path, body)
	if err != nil {
		return operationExit(deps, ctx, err)
	}
	if cmd.kind == "issue-variable-get" {
		return printVariableValue(deps, data, argValue(cmd.args, "key", ""))
	}
	if cmd.kind == "epic-add" || cmd.kind == "epic-remove" {
		data, err = unwrapResults(data)
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
	}
	if cmd.fieldsOnly {
		for _, f := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, f)
		}
		return ExitOK
	}
	if len(cmd.fields) > 0 {
		selected, e := SelectFields(data, cmd.fields, collection)
		if e != nil {
			writeError(deps.Stderr, e)
			return ExitOperation
		}
		return writeJSON(deps.Stdout, json.RawMessage(selected))
	}
	if collection {
		var v []any
		if json.Unmarshal(data, &v) != nil {
			writeError(deps.Stderr, errors.New("error: response has an invalid shape [invalid_response]"))
			return ExitOperation
		}
		if len(v) == 0 {
			fmt.Fprintln(deps.Stdout, emptyMessage(cmd.kind))
			return ExitOK
		}
	}
	if len(data) == 0 || string(data) == "null" || string(data) == "{}" {
		fmt.Fprintln(deps.Stdout, "OK")
		return ExitOK
	}
	if cmd.kind == "issue-view" {
		if err := renderIssueView(deps.Stdout, data); err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
		return ExitOK
	}
	var v any
	if json.Unmarshal(data, &v) != nil {
		writeError(deps.Stderr, errors.New("error: response has an invalid shape [invalid_response]"))
		return ExitOperation
	}
	return writeJSON(deps.Stdout, v)
}

func renderIssueView(out interface{ Write([]byte) (int, error) }, data json.RawMessage) error {
	var issue map[string]json.RawMessage
	if err := json.Unmarshal(data, &issue); err != nil || issue == nil {
		return errors.New("error: response has an invalid shape [invalid_response]")
	}

	writeIssueField(out, "Number", issue["number"])
	writeIssueField(out, "Title", issue["title"])
	writeIssueField(out, "Status", issue["status"])
	if workflow := issueWorkflow(issue); workflow != "" {
		fmt.Fprintf(out, "Workflow: %s\n", workflow)
	}
	writeIssueField(out, "Priority", issue["priority"])
	if repository := firstRawValue(issue, "repositoryName", "repository"); repository != "" {
		fmt.Fprintf(out, "Repository: %s\n", repository)
	}
	writeIssueField(out, "Blocker", issue["blocker"])

	if body := rawText(issue["body"]); body != "" {
		fmt.Fprintf(out, "Body:\n%s\n", body)
	}

	for _, field := range issueFields {
		if count, ok := collectionCount(issue[field]); ok && count > 0 {
			fmt.Fprintf(out, "%s: %d\n", issueFieldLabel(field), count)
		}
	}
	return nil
}

func writeIssueField(out interface{ Write([]byte) (int, error) }, label string, raw json.RawMessage) {
	if value := rawText(raw); value != "" {
		fmt.Fprintf(out, "%s: %s\n", label, value)
	}
}

func issueWorkflow(issue map[string]json.RawMessage) string {
	parts := []string{}
	for _, field := range []string{"workflowStatus", "workflowStage", "workflowRunId", "workflowProfileId", "workflowProfileMode"} {
		if value := rawText(issue[field]); value != "" {
			parts = append(parts, issueFieldLabel(field)+"="+value)
		}
	}
	if len(parts) == 0 {
		if value := rawText(issue["noWorkflow"]); value == "true" {
			return "none"
		}
	}
	return strings.Join(parts, ", ")
}

func firstRawValue(issue map[string]json.RawMessage, fields ...string) string {
	for _, field := range fields {
		if value := rawText(issue[field]); value != "" {
			return value
		}
	}
	return ""
}

func rawText(raw json.RawMessage) string {
	if len(raw) == 0 || string(raw) == "null" {
		return ""
	}
	var value any
	if json.Unmarshal(raw, &value) != nil {
		return ""
	}
	if value == nil {
		return ""
	}
	if text, ok := value.(string); ok {
		return sanitizeIssueText(text)
	}
	if object, ok := value.(map[string]any); ok {
		return issueObjectText(object)
	}
	if list, ok := value.([]any); ok {
		return fmt.Sprintf("%d items", len(list))
	}
	return sanitizeIssueText(display(value))
}

func issueObjectText(object map[string]any) string {
	parts := []string{}
	for _, field := range []string{"name", "displayName", "fullName", "reason", "title", "number", "status", "url"} {
		if value, ok := object[field]; ok && value != nil {
			text := sanitizeIssueText(display(value))
			if text != "" {
				parts = append(parts, issueFieldLabel(field)+"="+text)
			}
		}
	}
	if len(parts) == 0 {
		return "available"
	}
	return strings.Join(parts, ", ")
}

func rawCollection(raw json.RawMessage) ([]any, bool) {
	if len(raw) == 0 || string(raw) == "null" {
		return nil, false
	}
	var values []any
	if json.Unmarshal(raw, &values) != nil || values == nil {
		return nil, false
	}
	return values, true
}

func collectionCount(raw json.RawMessage) (int, bool) {
	values, ok := rawCollection(raw)
	return len(values), ok
}

func issueFieldLabel(field string) string {
	var words []string
	for _, part := range strings.Split(field, "_") {
		if part == "" {
			continue
		}
		words = append(words, strings.ToUpper(part[:1])+part[1:])
	}
	if len(words) == 1 {
		return words[0]
	}
	return strings.Join(words, " ")
}

func sanitizeIssueText(value string) string {
	value = ansiEscapePattern.ReplaceAllString(value, "")
	var clean strings.Builder
	for _, char := range value {
		if char == '\n' || char == '\t' || char == '\r' || char >= 0x20 {
			clean.WriteRune(char)
		}
	}
	return clean.String()
}

func operationExit(deps Dependencies, ctx context.Context, err error) int {
	writeError(deps.Stderr, err)
	if errors.Is(err, context.Canceled) || errors.Is(ctx.Err(), context.Canceled) {
		return ExitCanceled
	}
	return ExitOperation
}
func emptyMessage(kind string) string {
	switch {
	case strings.HasSuffix(kind, "issue-list"):
		return "No issues"
	case kind == "epic-list":
		return "No epics"
	case kind == "label-list":
		return "No labels"
	case kind == "issue-template-list":
		return "No issue templates"
	default:
		return "No results"
	}
}

func organizationRequest(cmd command, base string, deps Dependencies) (string, string, any, bool, error) {
	n := url.PathEscape(argValue(cmd.args, "number", ""))
	issue := base + "/issues/" + n
	epic := base + "/epics/"
	key := url.PathEscape(argValue(cmd.args, "key", ""))
	action := cmd.kind
	switch action {
	case "issue-list":
		q := url.Values{}
		for _, k := range []string{"stage", "priority", "repo", "parent", "epic"} {
			if v := argValue(cmd.args, k, ""); v != "" {
				if k == "repo" {
					k = "repository"
				}
				q.Add(k, v)
			}
		}
		for _, l := range valuesFor(cmd.args, "label") {
			q.Add("label", l)
		}
		if hasArg(cmd.args, "archived") {
			q.Set("archived", "true")
		}
		if hasArg(cmd.args, "all") {
			q.Set("all", "true")
		}
		return base + "/issues" + querySuffix(q), http.MethodGet, nil, true, nil
	case "issue-view":
		return issue, http.MethodGet, nil, false, nil
	case "issue-create":
		b := map[string]any{"title": argValue(cmd.args, "title", ""), "body": inputValue(deps, cmd, "body", "body-file"), "labels": labelMap(valuesFor(cmd.args, "label")), "priority": argValue(cmd.args, "priority", ""), "model": argValue(cmd.args, "model", ""), "modelVariant": argValue(cmd.args, "model-variant", ""), "risk": argValue(cmd.args, "risk", ""), "isDraft": true}
		if hasArg(cmd.args, "ready") {
			b["isDraft"] = false
		}
		if hasArg(cmd.args, "parent") {
			if argValue(cmd.args, "parent", "") == "none" {
				b["parentIssueNumber"] = nil
			} else {
				b["parentIssueNumber"] = numberValue(argValue(cmd.args, "parent", ""))
			}
		}
		for _, k := range []string{"workflow-profile", "repo", "stage-models", "stage-model-variants"} {
			if v := argValue(cmd.args, k, ""); v != "" {
				target := map[string]string{"workflow-profile": "workflowProfileId", "repo": "repositoryName", "stage-models": "stageModels", "stage-model-variants": "stageModelVariants"}[k]
				b[target] = v
			}
		}
		if hasArg(cmd.args, "no-workflow") {
			b["noWorkflow"] = true
		}
		return base + "/issues", http.MethodPost, b, false, nil
	case "issue-edit":
		b := map[string]any{}
		for _, k := range []string{"title", "priority", "risk", "model", "model-variant", "repo"} {
			if hasArg(cmd.args, k) {
				target := map[string]string{"model-variant": "modelVariant", "repo": "repositoryName"}[k]
				if target == "" {
					target = k
				}
				b[target] = argValue(cmd.args, k, "")
			}
		}
		if hasArg(cmd.args, "body") || hasArg(cmd.args, "body-file") {
			b["body"] = inputValue(deps, cmd, "body", "body-file")
		}
		if hasArg(cmd.args, "parent") {
			v := argValue(cmd.args, "parent", "")
			if v == "none" {
				b["parentIssueNumber"] = nil
			} else {
				b["parentIssueNumber"] = numberValue(v)
			}
		}
		if hasArg(cmd.args, "ready") {
			b["isDraft"] = false
		}
		if hasArg(cmd.args, "draft") {
			b["isDraft"] = true
		}
		if hasArg(cmd.args, "no-workflow") {
			b["noWorkflow"] = true
		}
		if hasArg(cmd.args, "workflow-profile") {
			b["workflowProfileId"] = argValue(cmd.args, "workflow-profile", "")
		}
		if hasArg(cmd.args, "inherit-workflow-profile") {
			b["workflowProfileId"] = nil
			b["noWorkflow"] = false
		}
		if hasArg(cmd.args, "label") {
			return issue, http.MethodGet, nil, false, errors.New("label edits require current Issue resolution")
		}
		return issue, http.MethodPatch, b, false, nil
	case "issue-start", "issue-done", "issue-close", "issue-reopen", "issue-restore":
		return issue + "/" + strings.TrimPrefix(action, "issue-"), http.MethodPost, map[string]any{}, false, nil
	case "issue-archive":
		return issue + "/archive", http.MethodPost, map[string]any{}, false, nil
	case "issue-archive-all":
		return base + "/issues/archive-completed", http.MethodPost, map[string]any{}, false, nil
	case "issue-rebase":
		return issue + "/rebase", http.MethodPost, map[string]any{"baseBranch": argValue(cmd.args, "base-branch", "")}, false, nil
	case "issue-diff", "issue-commits", "issue-logs", "issue-events":
		return issue + "/" + strings.TrimPrefix(action, "issue-"), http.MethodGet, nil, false, nil
	case "issue-prereq-add":
		return issue + "/prerequisites", http.MethodPost, map[string]any{"prerequisiteNumber": numberValue(argValue(cmd.args, "prereq-number", ""))}, false, nil
	case "issue-prereq-remove":
		return issue + "/prerequisites/" + url.PathEscape(argValue(cmd.args, "prereq-number", "")), http.MethodDelete, nil, false, nil
	case "issue-comment-create":
		b := map[string]any{"body": inputValue(deps, cmd, "body", "body-file")}
		if hasArg(cmd.args, "display-name") {
			b["displayName"] = strings.TrimSpace(argValue(cmd.args, "display-name", ""))
		}
		return issue + "/comments", http.MethodPost, b, false, nil
	case "issue-template-list":
		return "/api/issue-templates?projectId=" + url.QueryEscape(strings.TrimPrefix(base, "/api/projects/")), http.MethodGet, nil, true, nil
	case "issue-template-view":
		return "/api/issue-templates/" + argValue(cmd.args, "name", "") + "?projectId=" + url.QueryEscape(strings.TrimPrefix(base, "/api/projects/")), http.MethodGet, nil, false, nil
	case "issue-watch-list":
		return issue, http.MethodGet, nil, false, nil
	case "issue-watch-add", "issue-watch-remove":
		return issue + "/watch", map[string]string{"issue-watch-add": http.MethodPost, "issue-watch-remove": http.MethodDelete}[action], nil, false, nil
	case "issue-github-sync", "issue-github-unlink":
		return issue + "/github/" + strings.TrimPrefix(action, "issue-github-"), http.MethodPost, map[string]any{}, false, nil
	case "issue-github-link":
		p := strings.Split(argValue(cmd.args, "github-ref", ""), "#")
		return issue + "/github/link", http.MethodPost, map[string]any{"repository": p[0], "number": numberValue(p[1])}, false, nil
	case "issue-variable-list", "issue-variable-get":
		p := issue + "/variables"
		if stage := argValue(cmd.args, "stage", ""); stage != "" {
			p += "?stage=" + url.QueryEscape(stage)
		}
		return p, http.MethodGet, nil, false, nil
	case "issue-variable-set", "issue-variable-unset":
		p := issue + "/variables"
		value := any(nil)
		if action == "issue-variable-set" {
			if hasArg(cmd.args, "value-json") {
				if json.Unmarshal([]byte(argValue(cmd.args, "value-json", "")), &value) != nil {
					return "", "", nil, false, errors.New("invalid JSON value")
				}
			} else {
				value = argValue(cmd.args, "value", "")
			}
		}
		nested := nestedVariableValue(argValue(cmd.args, "key", ""), value)
		b := map[string]any{"vars": nested}
		if stage := argValue(cmd.args, "stage", ""); stage != "" {
			b = map[string]any{"stages": map[string]any{stage: map[string]any{"vars": nested}}}
		}
		return p, http.MethodPatch, b, false, nil
	case "label-list":
		return base + "/labels/catalog", http.MethodGet, nil, true, nil
	case "label-create":
		return base + "/labels/catalog", http.MethodPost, map[string]any{"key": argValue(cmd.args, "key", ""), "description": argValue(cmd.args, "description", ""), "supportedValues": csvValues(argValue(cmd.args, "supported-values", ""))}, false, nil
	case "label-edit":
		b := map[string]any{}
		for _, k := range []string{"description", "supported-values"} {
			if hasArg(cmd.args, k) {
				target := k
				if k == "supported-values" {
					target = "supportedValues"
				}
				b[target] = csvValues(argValue(cmd.args, k, ""))
				if k == "description" {
					b[target] = argValue(cmd.args, k, "")
				}
			}
		}
		return base + "/labels/catalog/" + key, http.MethodPatch, b, false, nil
	case "label-delete":
		return base + "/labels/catalog/" + key, http.MethodDelete, nil, false, nil
	case "epic-list":
		return epic, http.MethodGet, nil, true, nil
	case "epic-create":
		return epic, http.MethodPost, map[string]any{"title": argValue(cmd.args, "title", ""), "description": inputValue(deps, cmd, "description", "description-file"), "priority": argValue(cmd.args, "priority", "")}, false, nil
	case "epic-view":
		return epic + url.PathEscape(argValue(cmd.args, "number", "")), http.MethodGet, nil, false, nil
	case "epic-edit":
		b := map[string]any{}
		for _, k := range []string{"title", "priority"} {
			if hasArg(cmd.args, k) {
				b[k] = argValue(cmd.args, k, "")
			}
		}
		if hasArg(cmd.args, "description") || hasArg(cmd.args, "description-file") {
			b["description"] = inputValue(deps, cmd, "description", "description-file")
		}
		return epic + url.PathEscape(argValue(cmd.args, "number", "")), http.MethodPatch, b, false, nil
	case "epic-add":
		return epic + url.PathEscape(argValue(cmd.args, "number", "")) + "/issues", http.MethodPost, map[string]any{"issueNumber": numberValue(argValue(cmd.args, "issue", ""))}, false, nil
	case "epic-remove":
		return epic + url.PathEscape(argValue(cmd.args, "number", "")) + "/issues/" + url.PathEscape(argValue(cmd.args, "issue", "")), http.MethodDelete, nil, false, nil
	default:
		if strings.HasPrefix(action, "epic-") {
			return epic + url.PathEscape(argValue(cmd.args, "number", "")) + "/" + strings.TrimPrefix(action, "epic-"), http.MethodPost, map[string]any{}, false, nil
		}
		return "", "", nil, false, errors.New("unsupported organization command")
	}
}

func issueEditWithLabels(ctx context.Context, deps Dependencies, c *client, cmd command, base string) int {
	number := url.PathEscape(argValue(cmd.args, "number", ""))
	data, err := c.request(ctx, http.MethodGet, base+"/issues/"+number, nil)
	if err != nil {
		return operationExit(deps, ctx, err)
	}
	var current map[string]json.RawMessage
	if json.Unmarshal(data, &current) != nil {
		return operationExit(deps, ctx, errors.New("error: issue response has an invalid shape [invalid_response]"))
	}
	labels := map[string]any{}
	if raw, ok := current["labels"]; ok {
		_ = json.Unmarshal(raw, &labels)
	}
	for _, value := range valuesFor(cmd.args, "label") {
		if strings.HasPrefix(value, "-") {
			delete(labels, strings.TrimPrefix(value, "-"))
		} else {
			parts := strings.SplitN(value, "=", 2)
			if len(parts) == 2 {
				labels[parts[0]] = parts[1]
			}
		}
	}
	b := map[string]any{"labels": labels}
	if hasArg(cmd.args, "title") {
		b["title"] = argValue(cmd.args, "title", "")
	}
	if hasArg(cmd.args, "body") || hasArg(cmd.args, "body-file") {
		b["body"] = inputValue(deps, cmd, "body", "body-file")
	}
	if hasArg(cmd.args, "priority") {
		b["priority"] = argValue(cmd.args, "priority", "")
	}
	if hasArg(cmd.args, "risk") {
		b["risk"] = argValue(cmd.args, "risk", "")
	}
	if hasArg(cmd.args, "parent") {
		v := argValue(cmd.args, "parent", "")
		if v == "none" {
			b["parentIssueNumber"] = nil
		} else {
			b["parentIssueNumber"] = numberValue(v)
		}
	}
	data, err = c.request(ctx, http.MethodPatch, base+"/issues/"+number, b)
	if err != nil {
		return operationExit(deps, ctx, err)
	}
	if len(cmd.fields) > 0 {
		selected, e := SelectFields(data, cmd.fields, false)
		if e != nil {
			writeError(deps.Stderr, e)
			return ExitOperation
		}
		return writeJSON(deps.Stdout, json.RawMessage(selected))
	}
	return writeJSON(deps.Stdout, json.RawMessage(data))
}

func runWatch(ctx context.Context, deps Dependencies, c *client, cmd command, base, method, path string, body any) int {
	data, e := c.request(ctx, http.MethodGet, base+"/agents?all=true", nil)
	if e != nil {
		return operationExit(deps, ctx, e)
	}
	var agents []map[string]any
	if json.Unmarshal(data, &agents) != nil {
		return operationExit(deps, ctx, errors.New("error: invalid agent response [invalid_response]"))
	}
	ref := argValue(cmd.args, "agent", "")
	id := ref
	for _, a := range agents {
		if a["name"] == ref || a["id"] == ref {
			if v, ok := a["id"].(string); ok {
				id = v
			}
		}
	}
	body = map[string]any{"agentId": id}
	data, e = c.request(ctx, method, path, body)
	if e != nil {
		return operationExit(deps, ctx, e)
	}
	if len(cmd.fields) > 0 {
		selected, err := SelectFields(data, cmd.fields, false)
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
		return writeJSON(deps.Stdout, json.RawMessage(selected))
	}
	return writeJSON(deps.Stdout, json.RawMessage(data))
}
func unwrapResults(data json.RawMessage) (json.RawMessage, error) {
	var v struct {
		Results []json.RawMessage `json:"results"`
	}
	if json.Unmarshal(data, &v) != nil || len(v.Results) != 1 {
		return nil, errors.New("error: response has an invalid shape [invalid_response]")
	}
	return v.Results[0], nil
}
func querySuffix(q url.Values) string {
	if s := q.Encode(); s != "" {
		return "?" + s
	}
	return ""
}
func numberValue(v string) any {
	n, e := strconv.Atoi(v)
	if e == nil {
		return n
	}
	return v
}
func csvValues(v string) any {
	if v == "" {
		return nil
	}
	parts := strings.Split(v, ",")
	out := make([]string, 0, len(parts))
	for _, p := range parts {
		out = append(out, strings.TrimSpace(p))
	}
	return out
}
func labelMap(values []string) map[string]string {
	result := map[string]string{}
	for _, value := range values {
		parts := strings.SplitN(value, "=", 2)
		if len(parts) == 2 {
			result[parts[0]] = parts[1]
		}
	}
	return result
}
