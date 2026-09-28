package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"strconv"
	"strings"
	"time"
)

var workflowListFields = []string{"profileId", "revision", "name", "description", "sourceProvenance", "isBuiltIn"}
var workflowFields = []string{"projectId", "profileId", "revision", "name", "description", "sourceProvenance", "isBuiltIn", "definitionSource", "stages"}

// workflowSaveFields answers a create or edit with the saved Profile plus
// the validation scope that admitted it, including a skipped Action check.
var workflowSaveFields = append(append([]string{}, workflowFields...), "validation")
var workflowValidateFields = []string{"projectId", "definitionErrors", "actionErrors", "actionValidationStatus", "actionValidationSkipReason"}

// workflowToggleFields is the enable/disable answer: the Profile identity and
// its resulting availability for future selections.
var workflowToggleFields = []string{"profileId", "enabled"}
var runListFields = []string{"id", "status", "stage", "currentStage", "issueNumber"}
var runFields = []string{"id", "status", "currentStage", "stages", "issueRef", "pendingWork", "failure", "availableActions", "assignedTo", "binding"}
var artifactFields = []string{"artifactId", "path", "kind", "contentType", "size", "actionAttemptId", "recordedAt"}
var feedbackFields = []string{"id", "issueNumber", "workflowRunId", "stage", "status", "body", "createdAt", "resolution", "updatedAt"}

func parseWorkflow(args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: groupHelp("workflow")}, nil
	}
	action := args[0]
	if !contains([]string{"list", "view", "create", "edit", "delete", "validate", "enable", "disable"}, action) {
		return command{}, usage("unknown workflow command")
	}
	if action == "validate" {
		c := command{kind: "workflow-validate", catalog: workflowValidateFields}
		if discovered, ok, err := discoverLeaf(args[1:], c.kind, c.catalog, workflowValidateHelp()); ok {
			return discovered, err
		}
		return parseWorkflowInput(c, args[1:], false, false)
	}
	c := command{kind: "workflow-" + action, catalog: workflowFields}
	if action == "list" {
		c.catalog = workflowListFields
	}
	if action == "create" || action == "edit" {
		c.catalog = workflowSaveFields
	}
	if action == "enable" || action == "disable" {
		c.catalog = workflowToggleFields
	}
	if action == "edit" {
		if discovered, ok, err := discoverLeaf(args[1:], c.kind, c.catalog, workflowEditHelp()); ok {
			return discovered, err
		}
	} else if discovered, ok, err := discoverLeaf(args[1:], c.kind, c.catalog, leafHelp(c.kind, c.catalog)); ok {
		return discovered, err
	}
	start := 1
	if action == "view" || action == "delete" || action == "edit" || action == "enable" || action == "disable" {
		if len(args) <= 1 || isControlToken(args[1]) {
			return command{}, usage("profile id is required")
		}
		c.args = append(c.args, "profile", args[1])
		start = 2
	} else if action == "create" && len(args) > 1 && !strings.HasPrefix(args[1], "-") {
		c.args = append(c.args, "profile", args[1])
		start = 2
	}
	return parseWorkflowInput(c, args[start:], action == "create" || action == "edit", action == "view")
}

// workflowEditHelp documents the edit contract offline: the edit
// precondition, where the revision comes from, and what a conflict means
// for the caller's draft.
func workflowEditHelp() string {
	return "USAGE\n" +
		"    mo workflow edit <profile-id> --file <path|-> --expected-revision <token>\n" +
		"        [--project <name-or-id>] [--name <name>] [--description <text>] [flags]\n\n" +
		"Replaces a custom Workflow Profile's content for future runs. The revision\n" +
		"comes from the read that returned the content being edited (mo workflow\n" +
		"view <profile-id>); it is compared at the storage boundary. A missing or\n" +
		"stale revision fails without writing: another caller changed the Profile\n" +
		"since it was read. The --file draft is kept; re-read the Profile, compare\n" +
		"the changes, and resubmit with the new revision. The edit changes no\n" +
		"active run binding, default selection, or Issue.\n\n" +
		"JSON FIELDS\n" + strings.Join(workflowSaveFields, "\n")
}

func parseWorkflowInput(c command, args []string, needsFile, view bool) (command, error) {
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--project", "--file", "--id", "--name", "--description", "--expected-revision":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--yaml":
			c.args = append(c.args, "yaml", "true")
		case "--json":
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
		case "--help", "-h":
			if c.kind == "workflow-edit" {
				return command{help: true, helpText: workflowEditHelp()}, nil
			}
			return command{help: true, helpText: leafHelp(c.kind, c.catalog)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if c.kind == "workflow-enable" || c.kind == "workflow-disable" {
		for _, flag := range []string{"file", "id", "name", "description", "yaml"} {
			if hasArg(c.args, flag) {
				return command{}, usage("--" + flag + " is not valid with mo " + strings.ReplaceAll(c.kind, "-", " "))
			}
		}
	}
	if c.kind != "workflow-edit" && hasArg(c.args, "expected-revision") {
		return command{}, usage("--expected-revision is only valid with mo workflow edit")
	}
	if needsFile && !hasArg(c.args, "file") {
		return command{}, usage("--file is required")
	}
	if hasArg(c.args, "yaml") && len(c.fields) > 0 {
		return command{}, usage("--yaml and --json are mutually exclusive")
	}
	if err := validateFields(c.fields, c.catalog, "mo "+strings.ReplaceAll(c.kind, "-", " ")); err != nil {
		return command{}, err
	}
	if view && hasArg(c.args, "yaml") {
		return c, nil
	}
	return c, nil
}

// workflowValidateHelp documents the validate contract offline: what the
// command needs, what it writes, and what each exit code means.
func workflowValidateHelp() string {
	return "USAGE\n" +
		"    mo workflow validate --file <path|-> [--project <name-or-id>] [--id <profile-id>] [flags]\n\n" +
		"Validates a Workflow Profile Definition with the selected Project's Mohist\n" +
		"Server. It reads the file locally first, then performs no writes: it never\n" +
		"creates or changes a Profile, default selection, Issue, or Run. Complete\n" +
		"validation requires a reachable Server and the caller's normal Project\n" +
		"access; when the Server or the Action catalog is unavailable, the result\n" +
		"reports the skipped scope instead of passing the Definition.\n\n" +
		"Exit codes: 0 every promised check ran and passed; 1 invalid or incomplete\n" +
		"validation; 2 local usage or file errors; 130 cancelled.\n\n" +
		"JSON FIELDS\n" + strings.Join(workflowValidateFields, "\n")
}

func parseRun(args []string) (command, error) {
	action := args[0]
	if action == "artifact" || action == "feedback" || action == "variable" {
		return parseRunNested(action, args[1:])
	}
	allowed := []string{"list", "view", "watch", "approve", "request-changes", "retry", "rerun", "pause", "resume", "stop"}
	if !contains(allowed, action) {
		return command{}, usage("unknown run command")
	}
	c := command{kind: "run-" + action, catalog: runFields}
	if action == "list" {
		c.catalog = runListFields
		c.args = append(c.args, "collection", "true")
	}
	if action == "watch" {
		c.catalog = nil
	}
	if discovered, ok, err := discoverLeaf(args[1:], c.kind, c.catalog, leafHelp(c.kind, c.catalog)); ok {
		return discovered, err
	}
	start := 1
	if action != "list" {
		if len(args) > 1 && !strings.HasPrefix(args[1], "-") {
			c.args = append(c.args, "run", args[1])
			start = 2
		}
	}
	for i := start; i < len(args); i++ {
		arg := canonicalFlag(args[i])
		switch arg {
		case "--issue", "--project", "--display-name", "--message", "--from-stage", "--interval":
			if i+1 >= len(args) {
				return command{}, usage(arg + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(arg, "--"), args[i+1])
			i++
		case "--yes":
			c.args = append(c.args, "yes", "true")
		case "--idempotency-key":
			if !contains([]string{"approve", "request-changes", "retry", "rerun", "pause", "resume", "stop"}, action) {
				return command{}, usage(arg + " is only supported for Run controls")
			}
			if i+1 >= len(args) {
				return command{}, usage(arg + " requires a value")
			}
			c.args = append(c.args, "idempotency-key", args[i+1])
			i++
		case "--json":
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
		case "--yaml":
			c.args = append(c.args, "yaml", "true")
		case "--help", "-h":
			return command{help: true, helpText: leafHelp(c.kind, c.catalog)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	if action == "stop" && !hasArg(c.args, "yes") { /* interactive confirmation is handled after target validation */
	}
	if hasArg(c.args, "yaml") && len(c.fields) > 0 {
		return command{}, usage("--yaml and --json are mutually exclusive")
	}
	if err := validateFields(c.fields, c.catalog, "mo run "+action); err != nil {
		return command{}, err
	}
	return c, nil
}

func parseRunNested(area string, args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: "USAGE\n    mo run " + area + " <action> [flags]"}, nil
	}
	action := args[0]
	c := command{kind: "run-" + area + "-" + action}
	start := 1
	if area == "variable" {
		if !contains([]string{"list", "get", "set", "unset"}, action) {
			return command{}, usage("unknown run variable command")
		}
		c.catalog = variableFields
		if discovered, ok, err := discoverLeaf(args[1:], c.kind, c.catalog, leafHelp(c.kind, c.catalog)); ok {
			return discovered, err
		}
		if action != "list" {
			if len(args) < 2 || isControlToken(args[1]) {
				return command{}, usage("variable key is required")
			}
			c.args = append(c.args, "key", args[1])
			start = 2
		}
	} else {
		if !contains([]string{"list", "view", "get"}, action) {
			return command{}, usage("unknown run " + area + " command")
		}
		c.catalog = feedbackFields
		if area == "artifact" {
			c.catalog = artifactFields
		}
		if discovered, ok, err := discoverLeaf(args[1:], c.kind, c.catalog, leafHelp(c.kind, c.catalog)); ok {
			return discovered, err
		}
		if len(args) > 1 && !strings.HasPrefix(args[1], "-") {
			c.args = append(c.args, "run", args[1])
			start = 2
		}
		if action == "get" {
			if len(args) <= start || isControlToken(args[start]) {
				return command{}, usage("artifact id is required")
			}
			c.args = append(c.args, "artifact", args[start])
			start++
		}
	}
	for i := start; i < len(args); i++ {
		switch args[i] {
		case "--issue", "--project", "--stage", "--feedback":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--value", "--value-json":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--latest", "--effective":
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), "true")
		case "--json":
			var err error
			i, err = jsonFlag(args, i, &c)
			if err != nil {
				return command{}, err
			}
		case "--help", "-h":
			return command{help: true, helpText: leafHelp(c.kind, c.catalog)}, nil
		default:
			if area == "variable" && action == "set" && i == start && !strings.HasPrefix(args[i], "-") {
				c.args = append(c.args, "value", args[i])
				continue
			}
			return command{}, usage("unknown option " + args[i])
		}
	}
	if area == "feedback" && action == "view" && hasArg(c.args, "feedback") && hasArg(c.args, "latest") {
		return command{}, usage("--feedback and --latest cannot be used together")
	}
	if area == "feedback" && action == "view" && !hasArg(c.args, "feedback") && !hasArg(c.args, "latest") {
		return command{}, usage("--feedback <id> or --latest is required")
	}
	if area == "variable" && action == "set" && start < len(args) && !strings.HasPrefix(args[start], "-") {
		c.args = append(c.args, "value", args[start])
	}
	if area == "variable" && action == "set" && hasArg(c.args, "value") == hasArg(c.args, "value-json") {
		return command{}, usage("set requires exactly one value source")
	}
	return c, validateFields(c.fields, c.catalog, "mo run "+area+" "+action)
}

func runWorkflow(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	if cmd.fieldsOnly {
		for _, field := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, field)
		}
		return ExitOK
	}
	if strings.HasPrefix(cmd.kind, "workflow-") {
		return runWorkflowProfile(ctx, deps, c, cmd)
	}
	if cmd.kind == "run-list" {
		return runList(ctx, deps, c, cmd)
	}
	if cmd.kind == "run-view" {
		return runRunView(ctx, deps, c, cmd)
	}
	if cmd.kind == "run-watch" {
		return runWatchRun(ctx, deps, c, cmd)
	}
	if strings.HasPrefix(cmd.kind, "run-artifact-") {
		return runArtifact(ctx, deps, c, cmd)
	}
	if strings.HasPrefix(cmd.kind, "run-feedback-") {
		return runFeedback(ctx, deps, c, cmd)
	}
	if strings.HasPrefix(cmd.kind, "run-variable-") {
		return runRunVariables(ctx, deps, c, cmd)
	}
	return runRunControl(ctx, deps, c, cmd)
}

func runWorkflowProfile(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	// Validate, create, and edit preflight their complete-document --file
	// input before the Project-state lookup so a missing, permission, or
	// partial-read failure stops the command locally with ExitUsage=2,
	// without consulting cli-state.json or issuing any HTTP request.
	if cmd.kind == "workflow-validate" || cmd.kind == "workflow-create" || cmd.kind == "workflow-edit" {
		source, err := resolveTextInput(deps, cmd, "", "file")
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitUsage
		}
		if strings.TrimSpace(source) == "" {
			writeError(deps.Stderr, errors.New("--file must not be blank"))
			return ExitUsage
		}
		cmd.preflightedInput = source
	}
	// The edit precondition: the revision that came with the content being
	// replaced. It is never fetched silently and never defaulted; a missing
	// token is a usage error made before any HTTP request.
	if cmd.kind == "workflow-edit" && strings.TrimSpace(argValue(cmd.args, "expected-revision", "")) == "" {
		writeError(deps.Stderr, errors.New("--expected-revision is required; read it from 'mo workflow view <profile-id>'"))
		return ExitUsage
	}
	project, ok := resolveProject(deps, argValue(cmd.args, "project", ""))
	if !ok {
		writeError(deps.Stderr, errors.New("Run 'mo project use <name-or-id>' or pass --project <name-or-id>"))
		return ExitOperation
	}
	base := "/api/projects/" + url.PathEscape(project) + "/workflow-profiles"
	if cmd.kind == "workflow-validate" {
		body := map[string]any{"definitionSource": cmd.preflightedInput}
		if id := argValue(cmd.args, "id", ""); id != "" {
			body["profileId"] = id
		}
		data, err := c.request(ctx, http.MethodPost, base+"/validate", body)
		if err != nil {
			// Cancellation keeps its existing behavior; every other
			// failure means the promised checks did not all run, which is
			// an incomplete validation, never a valid result.
			code := operationExit(deps, ctx, err)
			if code == ExitOperation {
				fmt.Fprintln(deps.Stderr, "Validation incomplete: complete validation requires the selected Project's Mohist Server.")
			}
			return code
		}
		return renderWorkflowValidation(deps, cmd, data)
	}
	var method, path string
	var body any
	collection := false
	switch cmd.kind {
	case "workflow-list":
		method, path, collection = http.MethodGet, base, true
	case "workflow-view":
		method, path = http.MethodGet, base+"/"+url.PathEscape(argValue(cmd.args, "profile", ""))
	case "workflow-create":
		method, path = http.MethodPost, base
		body = workflowBody(cmd)
	case "workflow-edit":
		method, path = http.MethodPut, base+"/"+url.PathEscape(argValue(cmd.args, "profile", ""))
		body = workflowBody(cmd)
	case "workflow-delete":
		method, path = http.MethodDelete, base+"/"+url.PathEscape(argValue(cmd.args, "profile", ""))
	case "workflow-enable", "workflow-disable":
		method = http.MethodPost
		path = "/api/projects/" + url.PathEscape(project) + "/workflow-profile/" + strings.TrimPrefix(cmd.kind, "workflow-")
		body = map[string]any{"profileId": argValue(cmd.args, "profile", "")}
	}
	if cmd.kind == "workflow-view" && hasArg(cmd.args, "yaml") {
		data, err := c.request(ctx, method, path, nil)
		if err != nil {
			return operationExit(deps, ctx, err)
		}
		var v map[string]any
		if json.Unmarshal(data, &v) != nil {
			writeError(deps.Stderr, errors.New("error: invalid workflow response [invalid_response]"))
			return ExitOperation
		}
		fmt.Fprint(deps.Stdout, stringValueAny(v["definitionSource"]))
		return ExitOK
	}
	if cmd.kind == "workflow-create" || cmd.kind == "workflow-edit" {
		return workflowSaveRequest(ctx, deps, c, cmd, method, path, body)
	}
	return resourceRequest(ctx, deps, c, method, path, body, cmd, collection)
}

func workflowBody(cmd command) map[string]any {
	result := map[string]any{"profileId": argValue(cmd.args, "profile", argValue(cmd.args, "id", "")), "name": argValue(cmd.args, "name", ""), "description": argValue(cmd.args, "description", ""), "definitionSource": cmd.preflightedInput}
	if revision := argValue(cmd.args, "expected-revision", ""); revision != "" {
		result["expectedRevision"] = revision
	}
	return result
}

// workflowValidationIssue is one reported validation fact: where the error
// sits (a field path, possibly empty for whole-document YAML errors), what
// is wrong, and which check found it.
type workflowValidationIssue struct {
	Path    string `json:"path"`
	Message string `json:"message"`
	Source  string `json:"source"`
}

// workflowValidationReport is the Server's no-write validation answer. A
// skipped Action check is neither a pass nor a found error; the skip reason
// explains why the check did not run.
type workflowValidationReport struct {
	ProjectId                  string                    `json:"projectId"`
	DefinitionErrors           []workflowValidationIssue `json:"definitionErrors"`
	ActionErrors               []workflowValidationIssue `json:"actionErrors"`
	ActionValidationStatus     string                    `json:"actionValidationStatus"`
	ActionValidationSkipReason string                    `json:"actionValidationSkipReason"`
}

func decodeWorkflowValidation(raw json.RawMessage) (workflowValidationReport, bool) {
	var report workflowValidationReport
	if len(raw) == 0 || json.Unmarshal(raw, &report) != nil {
		return report, false
	}
	return report, true
}

// workflowValidationIssues lists every found error, definition checks
// first, without reusing either slice's backing array.
func workflowValidationIssues(report workflowValidationReport) []workflowValidationIssue {
	issues := make([]workflowValidationIssue, 0, len(report.DefinitionErrors)+len(report.ActionErrors))
	issues = append(issues, report.DefinitionErrors...)
	issues = append(issues, report.ActionErrors...)
	return issues
}

// workflowActionSkipReason explains a check that did not run; the Server
// supplies the reason, and the fallback keeps the fact non-empty even for
// an older or partial response.
func workflowActionSkipReason(report workflowValidationReport) string {
	if reason := strings.TrimSpace(report.ActionValidationSkipReason); reason != "" {
		return reason
	}
	return "the Action check did not run"
}

// writeValidationIssues prints each error with its field path so a
// rejection is actionable instead of a bare sentence.
func writeValidationIssues(deps Dependencies, issues []workflowValidationIssue) {
	for _, issue := range issues {
		if issue.Path == "" {
			fmt.Fprintf(deps.Stderr, "  %s\n", issue.Message)
			continue
		}
		fmt.Fprintf(deps.Stderr, "  %s: %s\n", issue.Path, issue.Message)
	}
}

// renderWorkflowValidation reports what was and was not checked. Exit 0 —
// in normal and selected JSON output alike — only when every promised
// check ran and found no error; invalid and incomplete results exit 1 with
// the per-error path and reason.
func renderWorkflowValidation(deps Dependencies, cmd command, data json.RawMessage) int {
	report, ok := decodeWorkflowValidation(data)
	if !ok {
		writeError(deps.Stderr, errors.New("error: invalid workflow validation response [invalid_response]"))
		return ExitOperation
	}
	exit := workflowValidationExitCode(report)
	if len(cmd.fields) > 0 {
		selected, err := SelectFields(data, cmd.fields, false)
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
		if code := writeJSON(deps.Stdout, json.RawMessage(selected)); code != ExitOK {
			return code
		}
		return exit
	}
	if issues := workflowValidationIssues(report); len(issues) > 0 {
		fmt.Fprintln(deps.Stderr, "Workflow Profile is invalid:")
		writeValidationIssues(deps, issues)
	} else if exit == ExitOK {
		fmt.Fprintln(deps.Stdout, "Workflow Profile is valid.")
	}
	if !strings.EqualFold(report.ActionValidationStatus, "performed") {
		fmt.Fprintf(deps.Stderr, "Validation incomplete: the Action check was skipped (%s).\n", workflowActionSkipReason(report))
	}
	return exit
}

// workflowValidationExitCode keeps the promised exit contract in one
// place: a skipped check is an unperformed promise, so it cannot exit 0.
func workflowValidationExitCode(report workflowValidationReport) int {
	if len(report.DefinitionErrors) > 0 || len(report.ActionErrors) > 0 {
		return ExitOperation
	}
	if !strings.EqualFold(report.ActionValidationStatus, "performed") {
		return ExitOperation
	}
	return ExitOK
}

// workflowSaveRequest answers a create or edit with both facts the caller
// needs: the saved content and the validation scope that admitted it. The
// Server returns validation beside the saved resource; merging them into
// one object keeps definition errors, the Action check status, and a
// skipped check's reason visible in normal and selected JSON output. The
// result never claims execution readiness.
func workflowSaveRequest(ctx context.Context, deps Dependencies, c *client, cmd command, method, path string, body any) int {
	env, err := c.requestEnvelope(ctx, method, path, body)
	if err != nil {
		code := operationExit(deps, ctx, err)
		if code == ExitOperation {
			writeSaveValidationErrors(deps, err)
			writeSaveConflictRecovery(deps, cmd, err)
		}
		return code
	}
	merged := map[string]json.RawMessage{}
	var profile map[string]json.RawMessage
	if len(env.Data) > 0 && json.Unmarshal(env.Data, &profile) == nil {
		for field, value := range profile {
			merged[field] = value
		}
	}
	if len(env.Validation) > 0 {
		merged["validation"] = env.Validation
	}
	encoded, err := json.Marshal(merged)
	if err != nil {
		writeError(deps.Stderr, errors.New("error: invalid workflow response [invalid_response]"))
		return ExitOperation
	}
	if len(cmd.fields) > 0 {
		selected, err := SelectFields(encoded, cmd.fields, false)
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
		return writeJSON(deps.Stdout, json.RawMessage(selected))
	}
	if report, ok := decodeWorkflowValidation(env.Validation); ok && !strings.EqualFold(report.ActionValidationStatus, "performed") {
		fmt.Fprintf(deps.Stderr, "Saved with the Action check skipped: %s\n", workflowActionSkipReason(report))
	}
	return writeJSON(deps.Stdout, json.RawMessage(encoded))
}

// writeSaveValidationErrors surfaces the path and reason of each error a
// save's performed checks found. The Server sends them as the rejected
// save's structured details.
func writeSaveValidationErrors(deps Dependencies, err error) {
	var operation *operationError
	if !errors.As(err, &operation) || !strings.EqualFold(operation.code, "workflow_profile_validation") {
		return
	}
	report, ok := decodeWorkflowValidation(operation.details)
	if !ok {
		return
	}
	writeValidationIssues(deps, workflowValidationIssues(report))
}

// writeSaveConflictRecovery explains a rejected edit precondition. The
// draft file is untouched on disk; the only recovery is to read the
// current Profile and revision, compare it with the draft, and resubmit —
// never a silent refetch-and-overwrite.
func writeSaveConflictRecovery(deps Dependencies, cmd command, err error) {
	var operation *operationError
	if !errors.As(err, &operation) || !strings.EqualFold(operation.code, "workflow_profile_revision_conflict") {
		return
	}
	fmt.Fprintf(deps.Stderr,
		"Draft kept: your --file was not changed. Re-read 'mo workflow view %s', compare it with your draft, then resubmit with the new revision.\n",
		argValue(cmd.args, "profile", argValue(cmd.args, "id", "")))
}

func runList(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	project, ok := resolveProject(deps, argValue(cmd.args, "project", ""))
	if !ok {
		return commandFailureExit(deps, ctx, cmd, projectNotSelected())
	}
	data, err := c.request(ctx, http.MethodGet, "/api/projects/"+url.PathEscape(project)+"/issues", nil)
	if err != nil {
		return commandFailureExit(deps, ctx, cmd, err)
	}
	var issues []map[string]json.RawMessage
	if json.Unmarshal(data, &issues) != nil {
		return commandFailureExit(deps, ctx, cmd, responseShapeError("error: issue response has an invalid shape [invalid_response]"))
	}
	runs := make([]map[string]json.RawMessage, 0)
	for _, issue := range issues {
		id := stringValueRaw(issue["workflowRunId"])
		if id == "" {
			continue
		}
		stage := issue["workflowStage"]
		status := issue["workflowStatus"]
		if len(status) == 0 {
			status = issue["status"]
		}
		runs = append(runs, map[string]json.RawMessage{"id": json.RawMessage(strconv.Quote(id)), "status": status, "stage": stage, "currentStage": stage, "issueNumber": issue["number"]})
	}
	encoded, _ := json.Marshal(runs)
	if cmd.fieldsOnly {
		for _, f := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, f)
		}
		return ExitOK
	}
	if len(cmd.fields) > 0 {
		selected, _ := SelectFields(encoded, cmd.fields, true)
		return writeJSON(deps.Stdout, json.RawMessage(selected))
	}
	if len(runs) == 0 {
		fmt.Fprintln(deps.Stdout, "No workflow runs")
		return ExitOK
	}
	return writeJSON(deps.Stdout, json.RawMessage(encoded))
}

func resolveRun(ctx context.Context, deps Dependencies, c *client, cmd command) (string, int) {
	run, issue := argValue(cmd.args, "run", ""), argValue(cmd.args, "issue", "")
	if run != "" && issue != "" || run == "" && issue == "" {
		return "", commandUsageExit(deps, cmd, usage("provide exactly one Run ID or --issue <number>"))
	}
	if run != "" {
		return run, ExitOK
	}
	project, ok := resolveProject(deps, argValue(cmd.args, "project", ""))
	if !ok {
		return "", commandFailureExit(deps, ctx, cmd, projectNotSelected())
	}
	data, err := c.request(ctx, http.MethodGet, "/api/projects/"+url.PathEscape(project)+"/issues/"+url.PathEscape(issue), nil)
	if err != nil {
		return "", commandFailureExit(deps, ctx, cmd, err)
	}
	var v map[string]json.RawMessage
	if json.Unmarshal(data, &v) != nil {
		return "", commandFailureExit(deps, ctx, cmd, responseShapeError("error: issue response has an invalid shape [invalid_response]"))
	}
	id := stringValueRaw(v["workflowRunId"])
	if id == "" {
		return "", commandFailureExit(deps, ctx, cmd, &operationError{
			message:    "error: issue " + issue + " has no active workflow run [run_not_found]",
			code:       "run_not_found",
			effect:     "none",
			retrySafe:  boolPtr(true),
			nextAction: "mo issue start " + issue,
		})
	}
	return id, ExitOK
}

// projectRunStatus maps one WorkflowStatusView onto the Run field catalog.
// `run view` and the Run controls answer with the same resource, so both
// project through the same names and `--json` means the same thing on either.
func projectRunStatus(status map[string]json.RawMessage) map[string]json.RawMessage {
	return map[string]json.RawMessage{
		"id":               status["workflowRunId"],
		"status":           status["status"],
		"currentStage":     status["currentStage"],
		"stages":           status["stages"],
		"pendingWork":      status["pendingWork"],
		"failure":          status["failure"],
		"availableActions": status["availableActions"],
		"assignedTo":       status["assignedTo"],
	}
}

// projectRunControlResult maps a Run control's answer — the Run it changed —
// onto that same catalog. An answer that carries no resource (the Server could
// not read the Run back) projects to nil, and the caller keeps the historical
// empty-response behavior.
func projectRunControlResult(data []byte) map[string]json.RawMessage {
	var root map[string]json.RawMessage
	if json.Unmarshal(data, &root) != nil {
		return nil
	}
	var status map[string]json.RawMessage
	raw, ok := root["status"]
	if !ok || json.Unmarshal(raw, &status) != nil || status == nil {
		return nil
	}
	projected := projectRunStatus(status)
	projected["issueRef"] = root["issueRef"]
	return projected
}

func runRunView(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	run, code := resolveRun(ctx, deps, c, cmd)
	if code != 0 {
		return code
	}
	if hasArg(cmd.args, "yaml") {
		data, e := c.request(ctx, http.MethodGet, "/api/workflow-runs/"+url.PathEscape(run)+"/yaml", nil)
		if e != nil {
			return commandFailureExit(deps, ctx, cmd, e)
		}
		var v map[string]any
		if json.Unmarshal(data, &v) != nil {
			return commandFailureExit(deps, ctx, cmd, responseShapeError("error: invalid YAML response [invalid_response]"))
		}
		fmt.Fprintln(deps.Stdout, stringValueAny(v["yaml"]))
		return ExitOK
	}
	data, e := c.request(ctx, http.MethodGet, "/api/workflow-runs/"+url.PathEscape(run), nil)
	if e != nil {
		if !contains(cmd.fields, "binding") {
			return commandFailureExit(deps, ctx, cmd, e)
		}
		// A binding read is authoritative even when historical Run state is
		// no longer decodable and the ordinary detail route answers 404.
		binding, bindingErr := fetchRunBinding(ctx, c, run)
		if bindingErr != nil {
			return commandFailureExit(deps, ctx, cmd, bindingErr)
		}
		var bindingFields map[string]json.RawMessage
		_ = json.Unmarshal(binding, &bindingFields)
		fallback := map[string]json.RawMessage{
			"id":       json.RawMessage(strconv.Quote(run)),
			"issueRef": json.RawMessage("null"),
			"binding":  binding,
			"status":   bindingFields["status"],
		}
		encoded, _ := json.Marshal(fallback)
		selected, selectErr := SelectFields(encoded, cmd.fields, false)
		if selectErr != nil {
			return commandFailureExit(deps, ctx, cmd, selectErr)
		}
		return writeJSON(deps.Stdout, json.RawMessage(selected))
	}
	var root map[string]json.RawMessage
	if json.Unmarshal(data, &root) != nil {
		return commandFailureExit(deps, ctx, cmd, responseShapeError("error: invalid run response [invalid_response]"))
	}
	status := map[string]json.RawMessage{}
	_ = json.Unmarshal(root["status"], &status)
	projected := projectRunStatus(status)
	projected["issueRef"] = root["issueRef"]
	// `binding` is the on-demand actual-binding read: only a caller that
	// selects the field pays for it, and the ordinary status stays concise.
	if contains(cmd.fields, "binding") {
		binding, e := fetchRunBinding(ctx, c, run)
		if e != nil {
			return commandFailureExit(deps, ctx, cmd, e)
		}
		projected["binding"] = binding
	}
	enc, _ := json.Marshal(projected)
	if cmd.fieldsOnly {
		for _, f := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, f)
		}
		return ExitOK
	}
	if len(cmd.fields) > 0 {
		s, _ := SelectFields(enc, cmd.fields, false)
		return writeJSON(deps.Stdout, json.RawMessage(s))
	}
	if actions := availableActions(status["availableActions"]); len(actions) > 0 {
		// The default rendering is one JSON object; the permitted controls
		// travel as one compact hint line on stderr so stdout stays
		// machine-readable.
		fmt.Fprintln(deps.Stderr, "Available actions: "+strings.Join(actions, ", "))
	}
	return writeJSON(deps.Stdout, json.RawMessage(enc))
}

// fetchRunBinding reads the on-demand Run binding projection: the
// start-time facts and the complete semantic definition the Run actually
// bound, never the Profile's current content. It is fetched only when a
// caller explicitly selects the `binding` field.
func fetchRunBinding(ctx context.Context, c *client, run string) (json.RawMessage, error) {
	data, e := c.request(ctx, http.MethodGet, "/api/workflow-runs/"+url.PathEscape(run)+"/binding", nil)
	if e != nil {
		return nil, e
	}
	var binding map[string]json.RawMessage
	if json.Unmarshal(data, &binding) != nil || binding == nil {
		return nil, responseShapeError("error: invalid run binding response [invalid_response]")
	}
	return data, nil
}

// availableActions decodes the read model's permitted Run controls; a
// missing or malformed field means no known actions.
func availableActions(raw json.RawMessage) []string {
	var actions []string
	if json.Unmarshal(raw, &actions) != nil {
		return nil
	}
	return actions
}

func runRunControl(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	if cmd.kind == "run-request-changes" && strings.TrimSpace(argValue(cmd.args, "message", "")) == "" {
		return commandFailureExit(deps, ctx, cmd, usage("--message is required and must not be empty"))
	}
	if cmd.kind == "run-rerun" && hasArg(cmd.args, "from-stage") && strings.TrimSpace(argValue(cmd.args, "from-stage", "")) == "" {
		return commandFailureExit(deps, ctx, cmd, usage("--from-stage is required and must not be empty"))
	}
	if cmd.kind == "run-stop" && !hasArg(cmd.args, "yes") {
		return commandFailureExit(deps, ctx, cmd, usage("--yes is required to confirm this irreversible action"))
	}
	run, code := resolveRun(ctx, deps, c, cmd)
	if code != 0 {
		return code
	}
	action := strings.TrimPrefix(cmd.kind, "run-")
	pathAction := action
	body := map[string]any{}
	if action == "request-changes" {
		body["message"] = argValue(cmd.args, "message", "")
		body["displayName"] = argValue(cmd.args, "display-name", "")
	}
	if action == "approve" {
		body["displayName"] = argValue(cmd.args, "display-name", "")
	}
	if action == "rerun" && hasArg(cmd.args, "from-stage") {
		pathAction = "rerun-from-stage"
		body["stage"] = argValue(cmd.args, "from-stage", "")
	}
	key := argValue(cmd.args, "idempotency-key", "")
	if key == "" {
		key = fmt.Sprintf("%d", deps.Now().UnixNano())
		// The generated key reaches stderr before the request so a lost
		// response is still recoverable; stdout stays the result channel.
		fmt.Fprintln(deps.Stderr, "Idempotency-Key: "+key)
	}
	data, e := c.requestHeaders(ctx, http.MethodPost, "/api/workflow-runs/"+url.PathEscape(run)+"/"+pathAction, body, map[string]string{"Idempotency-Key": key}, true)
	if e != nil {
		return commandFailureExit(deps, ctx, cmd, keyedRetryHint(e, runControlNextAction(cmd, key)))
	}
	if cmd.fieldsOnly {
		for _, f := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, f)
		}
		return ExitOK
	}
	if len(cmd.fields) > 0 {
		projected := projectRunControlResult(data)
		// The Run field catalog is shared with `run view`, so a control that
		// selects `binding` answers with the same on-demand read. When the
		// control response carries no readable Run, the fetched binding is
		// still reported rather than collapsing to null.
		if contains(cmd.fields, "binding") {
			binding, e := fetchRunBinding(ctx, c, run)
			if e != nil {
				return commandFailureExit(deps, ctx, cmd, e)
			}
			if projected == nil {
				var root map[string]json.RawMessage
				if json.Unmarshal(data, &root) == nil {
					projected = root
				}
			}
			if projected != nil {
				projected["binding"] = binding
			}
		}
		if projected != nil {
			enc, _ := json.Marshal(projected)
			s, _ := SelectFields(enc, cmd.fields, false)
			return writeJSON(deps.Stdout, json.RawMessage(s))
		}
		s, _ := SelectFields(data, cmd.fields, false)
		return writeJSON(deps.Stdout, json.RawMessage(s))
	}
	fmt.Fprintln(deps.Stdout, "OK")
	return ExitOK
}

// runControlNextAction rebuilds the same control invocation with the same
// key, the recovery for a keyed write whose outcome is unknown.
func runControlNextAction(cmd command, key string) string {
	parts := []string{"mo run " + strings.TrimPrefix(cmd.kind, "run-")}
	if run := argValue(cmd.args, "run", ""); run != "" {
		parts = append(parts, run)
	} else {
		parts = append(parts, "--issue", argValue(cmd.args, "issue", ""))
	}
	switch cmd.kind {
	case "run-request-changes":
		parts = append(parts, "--message", shellWord(argValue(cmd.args, "message", "")))
		if name := argValue(cmd.args, "display-name", ""); name != "" {
			parts = append(parts, "--display-name", shellWord(name))
		}
	case "run-approve":
		if name := argValue(cmd.args, "display-name", ""); name != "" {
			parts = append(parts, "--display-name", shellWord(name))
		}
	case "run-rerun":
		if hasArg(cmd.args, "from-stage") {
			parts = append(parts, "--from-stage", shellWord(argValue(cmd.args, "from-stage", "")))
		}
	case "run-stop":
		parts = append(parts, "--yes")
	}
	return strings.Join(append(parts, "--idempotency-key", shellWord(key)), " ")
}

// shellWord quotes one value for a POSIX shell. Only characters a shell leaves
// alone stay bare; everything else is single quoted, which suppresses splitting
// and expansion alike — a double quoted value would still expand $ and `.
func shellWord(value string) string {
	if value == "" {
		return "''"
	}
	for _, r := range value {
		if !strings.ContainsRune("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._:/@%+=,-", r) {
			return "'" + strings.ReplaceAll(value, "'", `'\''`) + "'"
		}
	}
	return value
}

func runWatchRun(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	run, code := resolveRun(ctx, deps, c, cmd)
	if code != 0 {
		return code
	}
	interval := 2 * time.Second
	if n, e := strconv.Atoi(argValue(cmd.args, "interval", "")); e == nil && n > 0 {
		interval = time.Duration(n) * time.Millisecond
	}
	var previous string
	for {
		data, e := c.request(ctx, http.MethodGet, "/api/workflow-runs/"+url.PathEscape(run), nil)
		if e != nil {
			if ctx.Err() != nil {
				return ExitCanceled
			}
			if e := deps.Wait(ctx, interval); e != nil {
				return ExitCanceled
			}
			continue
		}
		snapshot := watchSnapshot(run, data)
		if snapshot != previous {
			fmt.Fprintln(deps.Stdout, snapshot)
			previous = snapshot
		}
		if watchTerminal(data) {
			return ExitOK
		}
		if e := deps.Wait(ctx, interval); e != nil {
			return ExitCanceled
		}
	}
}

func watchSnapshot(run string, data json.RawMessage) string {
	var root map[string]json.RawMessage
	_ = json.Unmarshal(data, &root)
	var s map[string]json.RawMessage
	_ = json.Unmarshal(root["status"], &s)
	v := map[string]any{"id": run, "status": stringValueRaw(s["status"]), "stage": stringValueRaw(s["currentStage"])}
	b, _ := json.Marshal(v)
	return string(b)
}
func watchTerminal(data json.RawMessage) bool {
	var root map[string]json.RawMessage
	_ = json.Unmarshal(data, &root)
	var s map[string]json.RawMessage
	_ = json.Unmarshal(root["status"], &s)
	switch strings.ToLower(stringValueRaw(s["status"])) {
	case "completed", "succeeded", "stopped", "cancelled", "canceled", "failed":
		return true
	}
	return false
}

func runArtifact(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	run, code := resolveRun(ctx, deps, c, cmd)
	if code != 0 {
		return code
	}
	detail, e := c.request(ctx, http.MethodGet, "/api/workflow-runs/"+url.PathEscape(run), nil)
	if e != nil {
		return commandFailureExit(deps, ctx, cmd, e)
	}
	var root map[string]json.RawMessage
	_ = json.Unmarshal(detail, &root)
	var ref map[string]json.RawMessage
	_ = json.Unmarshal(root["issueRef"], &ref)
	project := stringValueRaw(ref["projectId"])
	number := stringValueRaw(ref["number"])
	path := "/api/projects/" + url.PathEscape(project) + "/issues/" + url.PathEscape(number) + "/workflow/artifacts"
	if cmd.kind == "run-artifact-get" {
		return c.stream(ctx, path+"/"+url.PathEscape(argValue(cmd.args, "artifact", ""))+"/content", deps.Stdout)
	}
	data, e := c.request(ctx, http.MethodGet, path, nil)
	if e != nil {
		return commandFailureExit(deps, ctx, cmd, e)
	}
	if cmd.fieldsOnly {
		for _, f := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, f)
		}
		return ExitOK
	}
	if len(cmd.fields) > 0 {
		s, _ := SelectFields(data, cmd.fields, true)
		return writeJSON(deps.Stdout, json.RawMessage(s))
	}
	return writeJSON(deps.Stdout, json.RawMessage(data))
}

func runFeedback(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	run, code := resolveRun(ctx, deps, c, cmd)
	if code != 0 {
		return code
	}
	detail, e := c.request(ctx, http.MethodGet, "/api/workflow-runs/"+url.PathEscape(run), nil)
	if e != nil {
		return commandFailureExit(deps, ctx, cmd, e)
	}
	var root map[string]json.RawMessage
	_ = json.Unmarshal(detail, &root)
	var ref map[string]json.RawMessage
	_ = json.Unmarshal(root["issueRef"], &ref)
	project, number := stringValueRaw(ref["projectId"]), stringValueRaw(ref["number"])
	path := "/api/projects/" + url.PathEscape(project) + "/issues/" + url.PathEscape(number) + "/feedback"
	if cmd.kind == "run-feedback-view" && !hasArg(cmd.args, "latest") {
		path += "/" + url.PathEscape(argValue(cmd.args, "feedback", ""))
	}
	if stage := argValue(cmd.args, "stage", ""); stage != "" {
		path += "?stage=" + url.QueryEscape(stage)
	}
	data, e := c.request(ctx, http.MethodGet, path, nil)
	if e != nil {
		return commandFailureExit(deps, ctx, cmd, e)
	}
	if cmd.kind == "run-feedback-view" && hasArg(cmd.args, "latest") {
		var records []map[string]json.RawMessage
		if json.Unmarshal(data, &records) != nil || len(records) == 0 {
			return commandFailureExit(deps, ctx, cmd, &operationError{message: "error: no feedback records found [not_found]", code: "not_found", effect: "none", retrySafe: boolPtr(true)})
		}
		id := stringValueRaw(records[0]["id"])
		if id == "" {
			return commandFailureExit(deps, ctx, cmd, responseShapeError("error: feedback response has an invalid shape [invalid_response]"))
		}
		path = "/api/projects/" + url.PathEscape(project) + "/issues/" + url.PathEscape(number) + "/feedback/" + url.PathEscape(id)
		data, e = c.request(ctx, http.MethodGet, path, nil)
		if e != nil {
			return commandFailureExit(deps, ctx, cmd, e)
		}
	}
	if cmd.fieldsOnly {
		for _, f := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, f)
		}
		return ExitOK
	}
	if len(cmd.fields) > 0 {
		s, _ := SelectFields(data, cmd.fields, cmd.kind == "run-feedback-list")
		return writeJSON(deps.Stdout, json.RawMessage(s))
	}
	return writeJSON(deps.Stdout, json.RawMessage(data))
}

func runRunVariables(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	run, code := resolveRun(ctx, deps, c, cmd)
	if code != 0 {
		return code
	}
	path := "/api/workflow-runs/" + url.PathEscape(run) + "/variables"
	if hasArg(cmd.args, "effective") && (cmd.kind == "run-variable-list" || cmd.kind == "run-variable-get") {
		path += "/effective"
	}
	if cmd.kind == "run-variable-get" {
		path += "/" + url.PathEscape(argValue(cmd.args, "key", ""))
	}
	method := http.MethodGet
	var body any
	if cmd.kind == "run-variable-set" || cmd.kind == "run-variable-unset" {
		method = http.MethodPatch
		var value any
		if cmd.kind == "run-variable-set" {
			if hasArg(cmd.args, "value-json") {
				if json.Unmarshal([]byte(argValue(cmd.args, "value-json", "")), &value) != nil {
					return commandUsageExit(deps, cmd, usage("invalid JSON value"))
				}
			} else {
				value = argValue(cmd.args, "value", "")
			}
		}
		body = map[string]any{"vars": nestedVariableValue(argValue(cmd.args, "key", ""), value)}
	}
	if stage := argValue(cmd.args, "stage", ""); stage != "" {
		path += "?stage=" + url.QueryEscape(stage)
	}
	data, e := c.request(ctx, method, path, body)
	if e != nil {
		return commandFailureExit(deps, ctx, cmd, e)
	}
	if cmd.fieldsOnly {
		for _, f := range cmd.catalog {
			fmt.Fprintln(deps.Stdout, f)
		}
		return ExitOK
	}
	if len(cmd.fields) > 0 {
		s, _ := SelectFields(data, cmd.fields, false)
		return writeJSON(deps.Stdout, json.RawMessage(s))
	}
	return writeJSON(deps.Stdout, json.RawMessage(data))
}

func (c *client) stream(ctx context.Context, path string, out io.Writer) int {
	req, e := http.NewRequestWithContext(ctx, http.MethodGet, c.base.String()+path, nil)
	if e != nil {
		return ExitOperation
	}
	req.Header.Set("Accept", "application/octet-stream")
	req.Header.Set(operatorIDHeader, c.operatorID)
	if c.token != "" && (!c.machineLocal || isLoopback(c.base)) {
		req.Header.Set("Authorization", "Bearer "+c.token)
	}
	resp, e := c.http.Do(req)
	if e != nil {
		return ExitOperation
	}
	defer resp.Body.Close()
	if resp.StatusCode < 200 || resp.StatusCode >= 300 {
		return ExitOperation
	}
	if _, e = io.Copy(out, resp.Body); e != nil {
		return ExitOperation
	}
	return ExitOK
}

func stringValueRaw(raw json.RawMessage) string {
	if len(raw) == 0 {
		return ""
	}
	var s string
	if json.Unmarshal(raw, &s) == nil {
		return s
	}
	var n json.Number
	if json.Unmarshal(raw, &n) == nil {
		return n.String()
	}
	return ""
}
func stringValueAny(v any) string {
	if s, ok := v.(string); ok {
		return s
	}
	return ""
}
