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
var runFields = []string{"id", "status", "currentStage", "stages", "issueRef"}
var runControlFields = []string{"workflowRunId", "approved", "requested-changes", "retried", "rerun", "rerunFromStage", "paused", "resumed", "stopped", "status", "stage", "issueRef", "decidedBy", "displayName"}
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
		return parseWorkflowInput(command{kind: "workflow-validate"}, args[1:], false, false)
	}
	c := command{kind: "workflow-" + action, catalog: workflowFields}
	if action == "list" {
		c.catalog = workflowListFields
	}
	start := 1
	if action == "view" || action == "delete" || action == "edit" {
		if len(args) <= 1 {
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
	start := 1
	if action != "list" {
		if len(args) > 1 && !strings.HasPrefix(args[1], "-") {
			c.args = append(c.args, "run", args[1])
			start = 2
		}
	}
	for i := start; i < len(args); i++ {
		switch args[i] {
		case "--issue", "--project", "--display-name", "--message", "--from-stage", "--interval":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--yes":
			c.args = append(c.args, "yes", "true")
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
		if action != "list" {
			if len(args) < 2 {
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
		if len(args) > 1 && !strings.HasPrefix(args[1], "-") {
			c.args = append(c.args, "run", args[1])
			start = 2
		}
		if action == "get" {
			if len(args) <= start {
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
	if cmd.kind == "workflow-validate" {
		return validateWorkflowFile(deps, cmd)
	}
	project, ok := resolveProject(deps, argValue(cmd.args, "project", ""))
	if !ok {
		writeError(deps.Stderr, errors.New("Run 'mo project use <name-or-id>' or pass --project <name-or-id>"))
		return ExitOperation
	}
	base := "/api/projects/" + url.PathEscape(project) + "/workflow-profiles"
	if cmd.kind == "workflow-validate" {
		return validateWorkflowFile(deps, cmd)
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
		body = workflowBody(deps, cmd)
	case "workflow-edit":
		method, path = http.MethodPut, base+"/"+url.PathEscape(argValue(cmd.args, "profile", ""))
		body = workflowBody(deps, cmd)
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

func workflowBody(deps Dependencies, cmd command) map[string]any {
	result := map[string]any{"profileId": argValue(cmd.args, "profile", argValue(cmd.args, "id", "")), "name": argValue(cmd.args, "name", ""), "description": argValue(cmd.args, "description", ""), "definitionSource": inputValue(deps, cmd, "", "file")}
	return result
}

func validateWorkflowFile(deps Dependencies, cmd command) int {
	source := inputValue(deps, cmd, "", "file")
	if strings.TrimSpace(source) == "" {
		writeError(deps.Stderr, errors.New("workflow definition is empty"))
		return ExitOperation
	}
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
		writeError(deps.Stderr, errors.New("Run 'mo project use <name-or-id>' or pass --project <name-or-id>"))
		return ExitOperation
	}
	data, err := c.request(ctx, http.MethodGet, "/api/projects/"+url.PathEscape(project)+"/issues", nil)
	if err != nil {
		return operationExit(deps, ctx, err)
	}
	var issues []map[string]json.RawMessage
	if json.Unmarshal(data, &issues) != nil {
		writeError(deps.Stderr, errors.New("error: issue response has an invalid shape [invalid_response]"))
		return ExitOperation
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
		writeError(deps.Stderr, errors.New("provide exactly one Run ID or --issue <number>"))
		return "", ExitUsage
	}
	if run != "" {
		return run, ExitOK
	}
	project, ok := resolveProject(deps, argValue(cmd.args, "project", ""))
	if !ok {
		writeError(deps.Stderr, errors.New("Run 'mo project use <name-or-id>' or pass --project <name-or-id>"))
		return "", ExitOperation
	}
	data, err := c.request(ctx, http.MethodGet, "/api/projects/"+url.PathEscape(project)+"/issues/"+url.PathEscape(issue), nil)
	if err != nil {
		return "", operationExit(deps, ctx, err)
	}
	var v map[string]json.RawMessage
	if json.Unmarshal(data, &v) != nil {
		writeError(deps.Stderr, errors.New("error: issue response has an invalid shape [invalid_response]"))
		return "", ExitOperation
	}
	id := stringValueRaw(v["workflowRunId"])
	if id == "" {
		writeError(deps.Stderr, errors.New("issue has no active workflow run"))
		return "", ExitOperation
	}
	return id, ExitOK
}

func runRunView(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	run, code := resolveRun(ctx, deps, c, cmd)
	if code != 0 {
		return code
	}
	if hasArg(cmd.args, "yaml") {
		data, e := c.request(ctx, http.MethodGet, "/api/workflow-runs/"+url.PathEscape(run)+"/yaml", nil)
		if e != nil {
			return operationExit(deps, ctx, e)
		}
		var v map[string]any
		if json.Unmarshal(data, &v) != nil {
			writeError(deps.Stderr, errors.New("error: invalid YAML response [invalid_response]"))
			return ExitOperation
		}
		fmt.Fprintln(deps.Stdout, stringValueAny(v["yaml"]))
		return ExitOK
	}
	data, e := c.request(ctx, http.MethodGet, "/api/workflow-runs/"+url.PathEscape(run), nil)
	if e != nil {
		return operationExit(deps, ctx, e)
	}
	var root map[string]json.RawMessage
	if json.Unmarshal(data, &root) != nil {
		writeError(deps.Stderr, errors.New("error: invalid run response [invalid_response]"))
		return ExitOperation
	}
	status := map[string]json.RawMessage{}
	_ = json.Unmarshal(root["status"], &status)
	projected := map[string]json.RawMessage{"id": status["workflowRunId"], "status": status["status"], "currentStage": status["currentStage"], "stages": status["stages"], "issueRef": root["issueRef"]}
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
	return writeJSON(deps.Stdout, json.RawMessage(enc))
}

func runRunControl(ctx context.Context, deps Dependencies, c *client, cmd command) int {
	if cmd.kind == "run-request-changes" && strings.TrimSpace(argValue(cmd.args, "message", "")) == "" {
		writeError(deps.Stderr, errors.New("--message is required and must not be empty"))
		return ExitOperation
	}
	if cmd.kind == "run-rerun" && hasArg(cmd.args, "from-stage") && strings.TrimSpace(argValue(cmd.args, "from-stage", "")) == "" {
		writeError(deps.Stderr, errors.New("--from-stage is required and must not be empty"))
		return ExitOperation
	}
	if cmd.kind == "run-stop" && !hasArg(cmd.args, "yes") {
		writeError(deps.Stderr, errors.New("--yes is required to confirm this irreversible action"))
		return ExitOperation
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
	data, e := c.request(ctx, http.MethodPost, "/api/workflow-runs/"+url.PathEscape(run)+"/"+pathAction, body)
	if e != nil {
		return operationExit(deps, ctx, e)
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
	if len(data) == 0 || string(data) == "null" || string(data) == "{}" {
		fmt.Fprintln(deps.Stdout, "OK")
		return ExitOK
	}
	return writeJSON(deps.Stdout, json.RawMessage(data))
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
		return operationExit(deps, ctx, e)
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
		return operationExit(deps, ctx, e)
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
		return operationExit(deps, ctx, e)
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
		return operationExit(deps, ctx, e)
	}
	if cmd.kind == "run-feedback-view" && hasArg(cmd.args, "latest") {
		var records []map[string]json.RawMessage
		if json.Unmarshal(data, &records) != nil || len(records) == 0 {
			writeError(deps.Stderr, errors.New("no feedback records found"))
			return ExitOperation
		}
		id := stringValueRaw(records[0]["id"])
		if id == "" {
			writeError(deps.Stderr, errors.New("feedback response has an invalid shape [invalid_response]"))
			return ExitOperation
		}
		path = "/api/projects/" + url.PathEscape(project) + "/issues/" + url.PathEscape(number) + "/feedback/" + url.PathEscape(id)
		data, e = c.request(ctx, http.MethodGet, path, nil)
		if e != nil {
			return operationExit(deps, ctx, e)
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
					writeError(deps.Stderr, errors.New("invalid JSON value"))
					return ExitUsage
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
		return operationExit(deps, ctx, e)
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
