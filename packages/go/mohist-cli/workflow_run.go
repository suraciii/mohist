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

var workflowListFields = []string{"profileId", "name", "description", "sourceProvenance", "isBuiltIn"}
var workflowFields = []string{"projectId", "profileId", "name", "description", "sourceProvenance", "isBuiltIn", "definitionSource", "stages"}
var runListFields = []string{"id", "status", "stage", "currentStage", "issueNumber"}
var runFields = []string{"id", "status", "currentStage", "stages", "issueRef", "pendingWork", "failure", "availableActions", "assignedTo"}
var artifactFields = []string{"artifactId", "path", "kind", "contentType", "size", "actionAttemptId", "recordedAt"}
var feedbackFields = []string{"id", "issueNumber", "workflowRunId", "stage", "status", "body", "createdAt", "resolution", "updatedAt"}

func parseWorkflow(args []string) (command, error) {
	if len(args) == 0 || args[0] == "--help" || args[0] == "-h" {
		return command{help: true, helpText: groupHelp("workflow")}, nil
	}
	action := args[0]
	if !contains([]string{"list", "view", "create", "edit", "delete", "validate"}, action) {
		return command{}, usage("unknown workflow command")
	}
	if action == "validate" {
		c := command{kind: "workflow-validate"}
		if discovered, ok, err := discoverLeaf(args[1:], c.kind, c.catalog, leafHelp(c.kind, c.catalog)); ok {
			return discovered, err
		}
		return parseWorkflowInput(c, args[1:], false, false)
	}
	c := command{kind: "workflow-" + action, catalog: workflowFields}
	if action == "list" {
		c.catalog = workflowListFields
	}
	if discovered, ok, err := discoverLeaf(args[1:], c.kind, c.catalog, leafHelp(c.kind, c.catalog)); ok {
		return discovered, err
	}
	start := 1
	if action == "view" || action == "delete" || action == "edit" {
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

func parseWorkflowInput(c command, args []string, needsFile, view bool) (command, error) {
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--project", "--file", "--id", "--name", "--description":
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
			return command{help: true, helpText: leafHelp(c.kind, c.catalog)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
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
	// Workflow validate is local: resolve its text carrier before the
	// Project-state lookup so a missing, permission, or arbitrary read
	// failure stops the command locally without consulting cli-state.json
	// or issuing any HTTP request.
	if cmd.kind == "workflow-validate" {
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
		return validateWorkflowFile(deps, cmd, source)
	}
	// Create and edit also preflight their complete-document --file input
	// before Project-state lookup so a failed read fails closed locally.
	if cmd.kind == "workflow-create" || cmd.kind == "workflow-edit" {
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
	project, ok := resolveProject(deps, argValue(cmd.args, "project", ""))
	if !ok {
		writeError(deps.Stderr, errors.New("Run 'mo project use <name-or-id>' or pass --project <name-or-id>"))
		return ExitOperation
	}
	base := "/api/projects/" + url.PathEscape(project) + "/workflow-profiles"
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
	return resourceRequest(ctx, deps, c, method, path, body, cmd, collection)
}

func workflowBody(cmd command) map[string]any {
	result := map[string]any{"profileId": argValue(cmd.args, "profile", argValue(cmd.args, "id", "")), "name": argValue(cmd.args, "name", ""), "description": argValue(cmd.args, "description", ""), "definitionSource": cmd.preflightedInput}
	return result
}

func validateWorkflowFile(deps Dependencies, cmd command, source string) int {
	if strings.Contains(source, "\t") {
		writeError(deps.Stderr, errors.New("workflow definition uses tabs, which are not valid YAML indentation"))
		return ExitOperation
	}
	hasStages := false
	for _, line := range strings.Split(source, "\n") {
		if strings.TrimSpace(line) == "stages:" {
			hasStages = true
		}
	}
	if !hasStages {
		writeError(deps.Stderr, errors.New("stages: is required"))
		return ExitOperation
	}
	fmt.Fprintln(deps.Stdout, "Workflow Profile is valid.")
	return ExitOK
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
		return commandFailureExit(deps, ctx, cmd, e)
	}
	var root map[string]json.RawMessage
	if json.Unmarshal(data, &root) != nil {
		return commandFailureExit(deps, ctx, cmd, responseShapeError("error: invalid run response [invalid_response]"))
	}
	status := map[string]json.RawMessage{}
	_ = json.Unmarshal(root["status"], &status)
	projected := projectRunStatus(status)
	projected["issueRef"] = root["issueRef"]
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
