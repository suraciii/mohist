// Package mohistcli implements the diagnostic portion of the Mohist CLI.
package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

const (
	DefaultServerURL  = "http://localhost:3456"
	DefaultOperatorID = "mohist-cli"
	operatorIDHeader  = "X-Mohist-Operator-Id"
)

const (
	ExitOK        = 0
	ExitOperation = 1
	ExitUsage     = 2
	ExitCanceled  = 130
)

type EnvLookup func(string) (string, bool)
type ReadFile func(string) (string, error)

// Dependencies makes process boundaries explicit and keeps command tests local.
type Dependencies struct {
	HTTPClient *http.Client
	Stdout     io.Writer
	Stderr     io.Writer
	Lookup     EnvLookup
	ReadFile   ReadFile
	HomeDir    func() (string, error)
}

type Config struct {
	ServerURL     string
	OperatorToken string
	OperatorID    string
}

func defaultDependencies() Dependencies {
	return Dependencies{
		HTTPClient: http.DefaultClient,
		Stdout:     os.Stdout,
		Stderr:     os.Stderr,
		Lookup:     os.LookupEnv,
		ReadFile: func(path string) (string, error) {
			b, err := os.ReadFile(path)
			return string(b), err
		},
		HomeDir: os.UserHomeDir,
	}
}

func ResolveConfig(deps Dependencies) (Config, error) {
	if deps.Lookup == nil {
		deps.Lookup = os.LookupEnv
	}
	if deps.ReadFile == nil {
		deps.ReadFile = func(path string) (string, error) {
			b, err := os.ReadFile(path)
			return string(b), err
		}
	}
	if deps.HomeDir == nil {
		deps.HomeDir = os.UserHomeDir
	}

	cfg := Config{ServerURL: DefaultServerURL, OperatorID: DefaultOperatorID}
	if value, ok := deps.Lookup("MOHIST_SERVER_URL"); ok && strings.TrimSpace(value) != "" {
		cfg.ServerURL = strings.TrimSpace(value)
	}
	if value, ok := deps.Lookup("MOHIST_OPERATOR_ID"); ok && strings.TrimSpace(value) != "" {
		cfg.OperatorID = strings.TrimSpace(value)
	}
	if value, ok := deps.Lookup("MOHIST_OPERATOR_TOKEN"); ok && strings.TrimSpace(value) != "" {
		cfg.OperatorToken = strings.TrimSpace(value)
		return cfg, validateConfig(cfg)
	}

	path := strings.TrimSpace(lookup(deps.Lookup, "MOHIST_OPERATOR_TOKEN_PATH"))
	if path == "" {
		home, err := deps.HomeDir()
		if err != nil {
			return Config{}, errors.New("Mohist operator credential could not be resolved")
		}
		path = filepath.Join(home, ".mohist", "operator-token")
	}
	value, err := deps.ReadFile(path)
	if err != nil {
		return Config{}, errors.New("Mohist operator credential file could not be read")
	}
	cfg.OperatorToken = strings.TrimSpace(value)
	if cfg.OperatorToken == "" {
		return Config{}, errors.New("Mohist operator credential is blank")
	}
	return cfg, validateConfig(cfg)
}

func validateConfig(cfg Config) error {
	if strings.TrimSpace(cfg.ServerURL) == "" {
		return errors.New("Mohist server URL is blank")
	}
	parsed, err := url.Parse(cfg.ServerURL)
	if err != nil || (parsed.Scheme != "http" && parsed.Scheme != "https") || parsed.Host == "" {
		return errors.New("Mohist server URL must be an absolute HTTP URL")
	}
	if strings.TrimSpace(cfg.OperatorToken) == "" {
		return errors.New("Mohist operator credential is blank")
	}
	return nil
}

func lookup(lookup EnvLookup, name string) string {
	value, _ := lookup(name)
	return value
}

type usageError struct{ message string }

func (e *usageError) Error() string { return e.message }

type operationError struct{ message string }

func (e *operationError) Error() string { return e.message }

// Run executes one CLI invocation and returns its process exit code.
func Run(ctx context.Context, args []string, deps Dependencies) int {
	if ctx == nil {
		ctx = context.Background()
	}
	defaults := defaultDependencies()
	if deps.Stdout == nil {
		deps.Stdout = defaults.Stdout
	}
	if deps.Stderr == nil {
		deps.Stderr = defaults.Stderr
	}
	if deps.HTTPClient == nil {
		deps.HTTPClient = defaults.HTTPClient
	}
	if deps.Lookup == nil {
		deps.Lookup = defaults.Lookup
	}
	if deps.ReadFile == nil {
		deps.ReadFile = defaults.ReadFile
	}
	if deps.HomeDir == nil {
		deps.HomeDir = defaults.HomeDir
	}

	if err := ctx.Err(); err != nil {
		writeError(deps.Stderr, err)
		return ExitCanceled
	}
	command, err := parse(args)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitUsage
	}
	if command.help {
		fmt.Fprintln(deps.Stdout, rootUsage())
		return ExitOK
	}
	if command.fieldsOnly {
		fmt.Fprintln(deps.Stdout, strings.Join(command.catalog, "\n"))
		return ExitOK
	}

	cfg, err := ResolveConfig(deps)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	client, err := newClient(cfg, deps.HTTPClient)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	data, err := client.get(ctx, command.path)
	if err != nil {
		if errors.Is(err, context.Canceled) || errors.Is(ctx.Err(), context.Canceled) {
			writeError(deps.Stderr, err)
			return ExitCanceled
		}
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	if err := render(deps.Stdout, command, data); err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	if command.kind == "doctor" && doctorFailed(data) {
		return ExitOperation
	}
	return ExitOK
}

type command struct {
	kind, path       string
	fields, catalog  []string
	fieldsOnly, help bool
}

var diagnosisFields = []string{"workflowRunId", "status", "failure", "tasks", "dispatch", "events"}
var doctorFields = []string{"name", "status", "detail", "nextAction"}

const maxDiagnosisEvents = 200

func parse(args []string) (command, error) {
	if len(args) == 0 {
		return command{}, &usageError{message: rootUsage()}
	}
	if args[0] == "--help" || args[0] == "-h" {
		return command{help: true}, nil
	}
	if args[0] == "doctor" {
		return parseLeaf("doctor", args[1:], "/api/doctor/checks", doctorFields, "mo doctor")
	}
	if args[0] != "run" {
		return command{}, &usageError{message: "error: unknown command\n" + rootUsage()}
	}
	if len(args) < 2 || args[1] != "why" {
		return command{}, &usageError{message: "error: incomplete command\nusage: mo run why <run-ref> [--json [fields]]"}
	}
	return parseLeaf("why", args[2:], "", diagnosisFields, "mo run why <run-ref>")
}

func parseLeaf(kind string, args []string, path string, catalog []string, usage string) (command, error) {
	c := command{kind: kind, path: path, catalog: catalog}
	positionals := []string{}
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--help", "-h":
			return command{help: true}, nil
		case "--json":
			c.fieldsOnly = true
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "-") {
				c.fieldsOnly = false
				c.fields = strings.Split(args[i+1], ",")
				i++
			}
		default:
			if strings.HasPrefix(args[i], "-") {
				return command{}, &usageError{message: "error: unknown option " + args[i] + "\nusage: " + usage}
			}
			positionals = append(positionals, args[i])
		}
	}

	if kind == "why" {
		if c.fieldsOnly && len(positionals) == 0 {
			return command{kind: kind, catalog: catalog, fieldsOnly: true}, nil
		}
		if len(positionals) != 1 {
			return command{}, &usageError{message: "error: run reference is required\nusage: " + usage}
		}
		c.path = "/api/runs/" + url.PathEscape(positionals[0]) + "/diagnosis"
	} else if len(positionals) != 0 {
		return command{}, &usageError{message: "error: doctor does not accept positional arguments\nusage: " + usage}
	}
	if len(c.fields) > 0 {
		for _, field := range c.fields {
			if !contains(catalog, field) {
				return command{}, &usageError{message: fmt.Sprintf("error: unknown JSON field %q; discover fields with %s --json", field, usage)}
			}
		}
	}
	return c, nil
}

func contains(values []string, wanted string) bool {
	for _, value := range values {
		if value == wanted {
			return true
		}
	}
	return false
}

func rootUsage() string { return "usage: mo run why <run-ref> [--json [fields]] | mo doctor" }

type client struct {
	http       *http.Client
	base       *url.URL
	token      string
	operatorID string
}

func newClient(cfg Config, httpClient *http.Client) (*client, error) {
	base, err := url.Parse(strings.TrimRight(cfg.ServerURL, "/"))
	if err != nil || base.Scheme == "" || base.Host == "" {
		return nil, errors.New("Mohist server URL is invalid")
	}
	base.RawPath = ""
	return &client{http: httpClient, base: base, token: cfg.OperatorToken, operatorID: cfg.OperatorID}, nil
}

type envelope struct {
	Success bool            `json:"success"`
	Data    json.RawMessage `json:"data"`
	Error   string          `json:"error"`
	Code    string          `json:"code"`
}

func (c *client) get(ctx context.Context, path string) (json.RawMessage, error) {
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, c.base.String()+path, nil)
	if err != nil {
		return nil, &operationError{message: "error: request could not be created [request_error]"}
	}
	req.Header.Set("Authorization", "Bearer "+c.token)
	req.Header.Set(operatorIDHeader, c.operatorID)
	req.Header.Set("Accept", "application/json")

	resp, err := c.http.Do(req)
	if err != nil {
		return nil, &operationError{message: "error: Mohist Server request failed [service_unavailable]"}
	}
	defer resp.Body.Close()
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, &operationError{message: "error: Mohist Server response could not be read [response_error]"}
	}

	var result envelope
	if err := json.Unmarshal(body, &result); err != nil {
		return nil, &operationError{message: responseStatusError(resp.StatusCode)}
	}
	if resp.StatusCode < 200 || resp.StatusCode >= 300 || !result.Success {
		code := result.Code
		if code == "" {
			code = statusCodeName(resp.StatusCode)
		}
		message := result.Error
		if message == "" {
			message = "Mohist Server request failed"
		}
		return nil, &operationError{message: "error: " + message + " [" + code + "]"}
	}
	if len(result.Data) == 0 || string(result.Data) == "null" {
		return nil, &operationError{message: "error: Mohist Server returned no data [invalid_response]"}
	}
	return result.Data, nil
}

func responseStatusError(status int) string {
	if status == http.StatusUnauthorized || status == http.StatusForbidden {
		return "error: Mohist Server authentication failed [" + statusCodeName(status) + "]"
	}
	if status == http.StatusNotFound {
		return "error: Mohist Server could not find the requested resource [not_found]"
	}
	return "error: Mohist Server returned malformed JSON (HTTP " + fmt.Sprint(status) + ") [invalid_response]"
}

func statusCodeName(status int) string {
	switch status {
	case http.StatusUnauthorized:
		return "unauthorized"
	case http.StatusForbidden:
		return "forbidden"
	case http.StatusNotFound:
		return "not_found"
	default:
		return "service_error"
	}
}

func render(out io.Writer, c command, data json.RawMessage) error {
	if c.kind == "why" {
		if err := validateDiagnosis(data); err != nil {
			return err
		}
	} else if c.kind == "doctor" {
		if _, err := decodeDoctorChecks(data); err != nil {
			return err
		}
	}
	if len(c.fields) > 0 {
		return project(out, data, c.fields, c.kind == "doctor")
	}
	if c.kind == "doctor" {
		return renderDoctor(out, data)
	}
	return renderDiagnosis(out, data)
}

func renderDoctor(out io.Writer, data json.RawMessage) error {
	checks, err := decodeDoctorChecks(data)
	if err != nil {
		return err
	}
	for _, check := range checks {
		fmt.Fprintf(out, "name: %s\nstatus: %s\ndetail: %s\n", check.Name, check.Status, check.Detail)
		if check.Status == "fail" && check.NextAction != nil && *check.NextAction != "" {
			fmt.Fprintln(out, "next action: "+*check.NextAction)
		}
		fmt.Fprintln(out)
	}
	return nil
}

type doctorCheck struct {
	Name       string
	Status     string
	Detail     string
	NextAction *string
}

func decodeDoctorChecks(data json.RawMessage) ([]doctorCheck, error) {
	var rawChecks []json.RawMessage
	if err := json.Unmarshal(data, &rawChecks); err != nil || rawChecks == nil {
		return nil, errors.New("error: Doctor response has an invalid shape [invalid_response]")
	}

	checks := make([]doctorCheck, 0, len(rawChecks))
	for _, rawCheck := range rawChecks {
		var fields map[string]json.RawMessage
		if err := json.Unmarshal(rawCheck, &fields); err != nil || fields == nil {
			return nil, errors.New("error: Doctor response has an invalid shape [invalid_response]")
		}

		name, ok := doctorStringField(fields, "name")
		if !ok {
			return nil, errors.New("error: Doctor response has an invalid shape [invalid_response]")
		}
		status, ok := doctorStringField(fields, "status")
		if !ok || (status != "ok" && status != "fail") {
			return nil, errors.New("error: Doctor response has an invalid shape [invalid_response]")
		}
		detail, ok := doctorStringField(fields, "detail")
		if !ok {
			return nil, errors.New("error: Doctor response has an invalid shape [invalid_response]")
		}
		nextAction, ok := doctorNullableStringField(fields, "nextAction")
		if !ok {
			return nil, errors.New("error: Doctor response has an invalid shape [invalid_response]")
		}
		checks = append(checks, doctorCheck{Name: name, Status: status, Detail: detail, NextAction: nextAction})
	}
	return checks, nil
}

func doctorStringField(fields map[string]json.RawMessage, name string) (string, bool) {
	raw, present := fields[name]
	if !present || strings.TrimSpace(string(raw)) == "null" {
		return "", false
	}
	var value string
	if json.Unmarshal(raw, &value) != nil {
		return "", false
	}
	return value, true
}

func doctorNullableStringField(fields map[string]json.RawMessage, name string) (*string, bool) {
	raw, present := fields[name]
	if !present {
		return nil, false
	}
	if strings.TrimSpace(string(raw)) == "null" {
		return nil, true
	}
	value, ok := doctorStringField(fields, name)
	if !ok {
		return nil, false
	}
	return &value, true
}

func renderDiagnosis(out io.Writer, data json.RawMessage) error {
	var diagnosis map[string]json.RawMessage
	if err := json.Unmarshal(data, &diagnosis); err != nil || diagnosis == nil {
		return errors.New("error: diagnosis response has an invalid shape [invalid_response]")
	}

	value := func(field string) any {
		var result any
		if json.Unmarshal(diagnosis[field], &result) != nil {
			return nil
		}
		return result
	}
	stringValue := func(field string) string {
		result, _ := value(field).(string)
		return result
	}

	fmt.Fprintf(out, "run: %s\n", stringValue("workflowRunId"))
	fmt.Fprintf(out, "status: %s\n", stringValue("status"))

	if failure, ok := value("failure").(map[string]any); ok && failure != nil {
		fmt.Fprintln(out, "failure:")
		printFields(out, failure, 1, []string{"reason", "stage", "taskId", "checkName", "message", "error"})
	}
	if tasks, ok := value("tasks").([]any); ok && len(tasks) > 0 {
		fmt.Fprintln(out, "tasks:")
		for _, task := range tasks {
			if taskMap, ok := task.(map[string]any); ok {
				printTask(out, taskMap)
			}
		}
	}
	if dispatch, ok := value("dispatch").(map[string]any); ok && dispatch != nil {
		fmt.Fprintln(out, "dispatch:")
		printFields(out, dispatch, 1, []string{"status", "snapshot"})
	}
	if events, ok := value("events").([]any); ok && len(events) > 0 {
		fmt.Fprintln(out, "events:")
		if len(events) > maxDiagnosisEvents {
			events = events[len(events)-maxDiagnosisEvents:]
		}
		for _, event := range events {
			if eventMap, ok := event.(map[string]any); ok {
				printEvent(out, eventMap)
			}
		}
	}
	return nil
}

func validateDiagnosis(data json.RawMessage) error {
	var diagnosis map[string]json.RawMessage
	if err := json.Unmarshal(data, &diagnosis); err != nil || diagnosis == nil {
		return errors.New("error: diagnosis response has an invalid shape [invalid_response]")
	}
	for _, field := range []string{"workflowRunId", "status", "tasks", "dispatch", "events"} {
		if _, ok := diagnosis[field]; !ok {
			return errors.New("error: diagnosis response has an invalid shape [invalid_response]")
		}
	}
	var runID, status string
	var tasks, events []json.RawMessage
	var dispatch map[string]json.RawMessage
	if json.Unmarshal(diagnosis["workflowRunId"], &runID) != nil || runID == "" ||
		json.Unmarshal(diagnosis["status"], &status) != nil || status == "" ||
		json.Unmarshal(diagnosis["tasks"], &tasks) != nil || tasks == nil ||
		json.Unmarshal(diagnosis["dispatch"], &dispatch) != nil || dispatch == nil ||
		json.Unmarshal(diagnosis["events"], &events) != nil || events == nil {
		return errors.New("error: diagnosis response has an invalid shape [invalid_response]")
	}
	return nil
}

func printTask(out io.Writer, task map[string]any) {
	label := firstString(task, "taskId", "id")
	if label == "" {
		label = "unknown"
	}
	fmt.Fprintf(out, "  %s (attempt %v)\n", label, display(task["attempt"]))
	printFields(out, task, 2, []string{"uses", "renderedWith", "workspace", "exitCode", "error", "recovery"})
}

func printEvent(out io.Writer, event map[string]any) {
	label := firstString(event, "type", "eventId")
	if label == "" {
		label = "event"
	}
	fmt.Fprintf(out, "  %s", label)
	if subject := firstString(event, "subject"); subject != "" {
		fmt.Fprintf(out, " subject=%s", subject)
	}
	if time := firstString(event, "time"); time != "" {
		fmt.Fprintf(out, " time=%s", time)
	}
	fmt.Fprintln(out)
	printFields(out, event, 2, []string{"id", "source", "specVersion", "dataContentType", "data", "extensions"})
}

func printFields(out io.Writer, object map[string]any, indent int, fields []string) {
	prefix := strings.Repeat("  ", indent)
	for _, field := range fields {
		value, present := object[field]
		if !present || value == nil || value == "" {
			continue
		}
		value = sanitizeHumanValue(value)
		if value == nil {
			continue
		}
		if nested, ok := value.(map[string]any); ok {
			fmt.Fprintln(out, prefix+field+":")
			printFields(out, nested, indent+1, sortedKeys(nested))
			continue
		}
		if list, ok := value.([]any); ok {
			fmt.Fprintf(out, "%s%s: %s\n", prefix, field, compactJSON(list))
			continue
		}
		fmt.Fprintf(out, "%s%s: %s\n", prefix, field, display(value))
	}
}

func sortedKeys(values map[string]any) []string {
	keys := make([]string, 0, len(values))
	for key := range values {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	return keys
}

func firstString(values map[string]any, fields ...string) string {
	for _, field := range fields {
		if value, ok := values[field].(string); ok && value != "" {
			return value
		}
	}
	return ""
}

func display(value any) string {
	if value == nil {
		return ""
	}
	if text, ok := value.(string); ok {
		return text
	}
	return compactJSON(value)
}

func compactJSON(value any) string {
	encoded, err := json.Marshal(value)
	if err != nil {
		return "<invalid>"
	}
	return string(encoded)
}

func isProcessScopedPath(value any) bool {
	text, ok := value.(string)
	return ok && strings.Contains(text, "/proc/") && strings.Contains(text, "/fd/")
}

func sanitizeHumanValue(value any) any {
	if isProcessScopedPath(value) {
		return nil
	}
	switch typed := value.(type) {
	case map[string]any:
		clean := make(map[string]any, len(typed))
		for key, nested := range typed {
			if sanitized := sanitizeHumanValue(nested); sanitized != nil {
				clean[key] = sanitized
			}
		}
		return clean
	case []any:
		clean := make([]any, 0, len(typed))
		for _, nested := range typed {
			if sanitized := sanitizeHumanValue(nested); sanitized != nil {
				clean = append(clean, sanitized)
			}
		}
		return clean
	default:
		return value
	}
}

func project(out io.Writer, data json.RawMessage, fields []string, collection bool) error {
	if collection {
		var values []map[string]json.RawMessage
		if err := json.Unmarshal(data, &values); err != nil || values == nil {
			return errors.New("error: response has an invalid shape [invalid_response]")
		}
		projected := make([]map[string]json.RawMessage, 0, len(values))
		for _, value := range values {
			projected = append(projected, pick(value, fields))
		}
		return json.NewEncoder(out).Encode(projected)
	}
	var value map[string]json.RawMessage
	if err := json.Unmarshal(data, &value); err != nil || value == nil {
		return errors.New("error: response has an invalid shape [invalid_response]")
	}
	return json.NewEncoder(out).Encode(pick(value, fields))
}

func pick(value map[string]json.RawMessage, fields []string) map[string]json.RawMessage {
	result := make(map[string]json.RawMessage, len(fields))
	for _, field := range fields {
		if raw, ok := value[field]; ok {
			result[field] = raw
		} else {
			result[field] = json.RawMessage("null")
		}
	}
	return result
}

func doctorFailed(data json.RawMessage) bool {
	checks, err := decodeDoctorChecks(data)
	if err != nil {
		return true
	}
	for _, check := range checks {
		if check.Status == "fail" {
			return true
		}
	}
	return false
}

func writeError(out io.Writer, err error) {
	if err != nil {
		fmt.Fprintln(out, err.Error())
	}
}
