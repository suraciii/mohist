package mohistcli

import (
	"context"
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"
	"time"
)

func TestWorkflowValidateSendsSourceToServerAndExitsZeroWhenAllChecksPass(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		if r.URL.Path != "/api/projects/proj-1/workflow-profiles/validate" || r.Method != http.MethodPost {
			t.Fatalf("request=%s %s", r.Method, r.URL.Path)
		}
		return response(http.StatusOK, `{"success":true,"data":{"projectId":"proj-1","definitionErrors":[],"actionErrors":[],"actionValidationStatus":"performed","actionValidationSkipReason":null}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	deps.ReadFile = func(string) (string, error) {
		return "stages:\n  - stage: build\n    tasks: []\n    checks: []\n", nil
	}

	if code := Run(context.Background(), []string{"workflow", "validate", "--project", "proj-1", "--file", "workflow.yaml"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if out.String() != "Workflow Profile is valid.\n" || errOut.Len() != 0 {
		t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
	}
}

func TestWorkflowValidateDefinitionErrorsReportPathAndReason(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":{"projectId":"proj-1","definitionErrors":[{"path":"stages[0].not-a-valid-stage","message":"unknown field 'not-a-valid-stage'","source":"definition"}],"actionErrors":[],"actionValidationStatus":"performed","actionValidationSkipReason":null}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	deps.ReadFile = func(string) (string, error) {
		return "stages:\n  - not-a-valid-stage: true\n", nil
	}

	if code := Run(context.Background(), []string{"workflow", "validate", "--project", "proj-1", "--file", "-"}, deps); code != ExitOperation {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if out.Len() != 0 {
		t.Fatalf("stdout=%q", out.String())
	}
	for _, want := range []string{"Workflow Profile is invalid:", "stages[0].not-a-valid-stage: unknown field 'not-a-valid-stage'"} {
		if !strings.Contains(errOut.String(), want) {
			t.Fatalf("stderr=%q want %q", errOut.String(), want)
		}
	}
}

func TestWorkflowValidateSkippedActionCheckAndUnreachableServerAreIncomplete(t *testing.T) {
	skipped := response(http.StatusOK, `{"success":true,"data":{"projectId":"proj-1","definitionErrors":[],"actionErrors":[],"actionValidationStatus":"skipped","actionValidationSkipReason":"no Runner has reported an Action catalog yet"}}`)
	tests := []struct {
		name string
		fake roundTripFunc
	}{
		{name: "catalog-unavailable", fake: func(*http.Request) (*http.Response, error) { return skipped, nil }},
		{name: "server-unreachable", fake: func(*http.Request) (*http.Response, error) { return nil, errors.New("connection refused") }},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			deps, out, errOut := testDeps(test.fake, map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
			deps.ReadFile = func(string) (string, error) {
				return "stages:\n  - stage: build\n    tasks: []\n    checks: []\n", nil
			}

			code := Run(context.Background(), []string{"workflow", "validate", "--project", "proj-1", "--file", "workflow.yaml"}, deps)
			if code != ExitOperation {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			if out.Len() != 0 || !strings.Contains(errOut.String(), "Validation incomplete") {
				t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
			}
		})
	}
}

func TestWorkflowValidateJSONSelectionPreservesScopeFacts(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":{"projectId":"proj-1","definitionErrors":[],"actionErrors":[],"actionValidationStatus":"skipped","actionValidationSkipReason":"no Runner has reported an Action catalog yet"}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	deps.ReadFile = func(string) (string, error) {
		return "stages:\n  - stage: build\n    tasks: []\n    checks: []\n", nil
	}

	code := Run(context.Background(), []string{"workflow", "validate", "--project", "proj-1", "--file", "workflow.yaml", "--json", "actionValidationStatus,actionValidationSkipReason"}, deps)
	if code != ExitOperation {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	want := "{\"actionValidationSkipReason\":\"no Runner has reported an Action catalog yet\",\"actionValidationStatus\":\"skipped\"}\n"
	if out.String() != want {
		t.Fatalf("stdout=%q want %q", out.String(), want)
	}
}

const workflowSaveSkippedValidation = `{"definitionErrors":[],"actionErrors":[],"actionValidationStatus":"skipped","actionValidationSkipReason":"no Runner has reported an Action catalog yet"}`

func TestWorkflowCreatePreservesSkippedActionCheckThroughNormalAndSelectedJSON(t *testing.T) {
	normalDeps, normalOut, normalErr := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusCreated, `{"success":true,"data":{"projectId":"proj-1","profileId":"ship","name":"Ship","description":"","sourceProvenance":"verbatim","isBuiltIn":false,"definitionSource":"stages:\n  - stage: build\n"},"validation":`+workflowSaveSkippedValidation+`}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	normalDeps.ReadFile = func(string) (string, error) {
		return "stages:\n  - stage: build\n", nil
	}

	if code := Run(context.Background(), []string{"workflow", "create", "ship", "--project", "proj-1", "--file", "workflow.yaml"}, normalDeps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, normalOut.String(), normalErr.String())
	}
	for _, want := range []string{`"profileId":"ship"`, `"actionValidationStatus":"skipped"`, `"actionValidationSkipReason":"no Runner has reported an Action catalog yet"`} {
		if !strings.Contains(normalOut.String(), want) {
			t.Fatalf("normal stdout=%q want %q", normalOut.String(), want)
		}
	}
	if !strings.Contains(normalErr.String(), "Saved with the Action check skipped: no Runner has reported an Action catalog yet") {
		t.Fatalf("normal stderr=%q", normalErr.String())
	}

	selectedDeps, selectedOut, selectedErr := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusCreated, `{"success":true,"data":{"projectId":"proj-1","profileId":"ship"},"validation":`+workflowSaveSkippedValidation+`}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	selectedDeps.ReadFile = func(string) (string, error) {
		return "stages:\n  - stage: build\n", nil
	}

	if code := Run(context.Background(), []string{"workflow", "create", "ship", "--project", "proj-1", "--file", "workflow.yaml", "--json", "validation"}, selectedDeps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, selectedOut.String(), selectedErr.String())
	}
	want := `{"validation":` + workflowSaveSkippedValidation + "}\n"
	if selectedOut.String() != want {
		t.Fatalf("selected stdout=%q want %q", selectedOut.String(), want)
	}
	if selectedErr.Len() != 0 {
		t.Fatalf("selected stderr=%q", selectedErr.String())
	}
}

func TestWorkflowSaveRejectionReportsPathLocatedErrors(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusBadRequest, `{"success":false,"error":"WorkflowProfile validation failed","code":"workflow_profile_validation","details":{"definitionErrors":[{"path":"stages[0].stage","message":"stage is required","source":"definition"}],"actionErrors":[],"actionValidationStatus":"performed","actionValidationSkipReason":null}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	deps.ReadFile = func(string) (string, error) {
		return "stages:\n  - tasks: []\n", nil
	}

	if code := Run(context.Background(), []string{"workflow", "create", "broken", "--project", "proj-1", "--file", "workflow.yaml"}, deps); code != ExitOperation {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if out.Len() != 0 {
		t.Fatalf("stdout=%q", out.String())
	}
	for _, want := range []string{"workflow_profile_validation", "stages[0].stage: stage is required"} {
		if !strings.Contains(errOut.String(), want) {
			t.Fatalf("stderr=%q want %q", errOut.String(), want)
		}
	}
}

func TestWorkflowAndArtifactDiscoveryPrecedeRequiredInputs(t *testing.T) {
	tests := []struct {
		name string
		args []string
		want []string
	}{
		{name: "workflow list", args: []string{"workflow", "list", "--json"}, want: workflowListFields},
		{name: "workflow view", args: []string{"workflow", "view", "--json"}, want: workflowFields},
		{name: "workflow create", args: []string{"workflow", "create", "--json"}, want: workflowSaveFields},
		{name: "workflow edit", args: []string{"workflow", "edit", "--json"}, want: workflowSaveFields},
		{name: "workflow delete", args: []string{"workflow", "delete", "--json"}, want: workflowFields},
		{name: "workflow validate", args: []string{"workflow", "validate", "--json"}, want: workflowValidateFields},
		{name: "artifact list", args: []string{"run", "artifact", "list", "--json"}, want: artifactFields},
		{name: "artifact view", args: []string{"run", "artifact", "view", "--json"}, want: artifactFields},
		{name: "artifact get", args: []string{"run", "artifact", "get", "--json"}, want: artifactFields},
		{name: "feedback list", args: []string{"run", "feedback", "list", "--json"}, want: feedbackFields},
		{name: "feedback view", args: []string{"run", "feedback", "view", "--json"}, want: feedbackFields},
		{name: "feedback get", args: []string{"run", "feedback", "get", "--json"}, want: feedbackFields},
		{name: "variable list", args: []string{"run", "variable", "list", "--json"}, want: variableFields},
		{name: "variable get", args: []string{"run", "variable", "get", "--json"}, want: variableFields},
		{name: "variable set", args: []string{"run", "variable", "set", "--json"}, want: variableFields},
		{name: "variable unset", args: []string{"run", "variable", "unset", "--json"}, want: variableFields},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			calls := 0
			deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
				calls++
				return nil, errors.New("must not call")
			}), map[string]string{})
			code := Run(context.Background(), test.args, deps)
			want := strings.Join(test.want, "\n") + "\n"
			if code != ExitOK || calls != 0 || out.String() != want || errOut.Len() != 0 {
				t.Fatalf("code=%d calls=%d stdout=%q stderr=%q", code, calls, out.String(), errOut.String())
			}
		})
	}
}

func TestWorkflowDeleteHelpPrecedesProfileAndProjectResolution(t *testing.T) {
	for _, token := range []string{"--help", "-h"} {
		t.Run(token, func(t *testing.T) {
			calls := 0
			deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
				calls++
				return nil, errors.New("must not call")
			}), map[string]string{})
			if code := Run(context.Background(), []string{"workflow", "delete", token}, deps); code != ExitOK || calls != 0 || !strings.Contains(out.String(), "USAGE") || errOut.Len() != 0 {
				t.Fatalf("code=%d calls=%d stdout=%q stderr=%q", code, calls, out.String(), errOut.String())
			}
		})
	}
}
func TestRunWatchNoCatalogJSONIsLocalUsageError(t *testing.T) {
	calls := 0
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return nil, errors.New("must not call")
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	if code := Run(context.Background(), []string{"run", "watch", "--json"}, deps); code != ExitUsage || calls != 0 || out.Len() != 0 || errOut.Len() == 0 {
		t.Fatalf("code=%d calls=%d stdout=%q stderr=%q", code, calls, out.String(), errOut.String())
	}
}

func TestWorkflowValidateHelpAndFieldDiscoveryStayOffline(t *testing.T) {
	for _, args := range [][]string{
		{"workflow", "validate", "--json"},
		{"workflow", "validate", "--help"},
	} {
		t.Run(strings.Join(args, " "), func(t *testing.T) {
			calls := 0
			deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
				calls++
				return nil, errors.New("must not call")
			}), map[string]string{})
			deps.ReadFile = func(string) (string, error) {
				t.Fatal("file must not be read")
				return "", nil
			}
			code := Run(context.Background(), args, deps)
			if code != ExitOK || calls != 0 || errOut.Len() != 0 {
				t.Fatalf("code=%d calls=%d stderr=%q", code, calls, errOut.String())
			}
			if !strings.Contains(out.String(), strings.Join(workflowValidateFields, "\n")) {
				t.Fatalf("stdout=%q", out.String())
			}
		})
	}
}

func TestRunControlKeyedWriteRetriesLostResponseOnceWithSameKey(t *testing.T) {
	requests := 0
	keys := []string{}
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		requests++
		if r.URL.Path != "/api/workflow-runs/wr-1/retry" || r.Method != http.MethodPost {
			t.Fatalf("request=%s %s", r.Method, r.URL.Path)
		}
		keys = append(keys, r.Header.Get("Idempotency-Key"))
		if requests == 1 {
			return nil, errors.New("connection lost after submit")
		}
		return response(http.StatusOK, `{"success":true,"data":{}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"run", "retry", "wr-1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if requests != 2 {
		t.Fatalf("requests=%d", requests)
	}
	if keys[0] == "" || keys[0] != keys[1] {
		t.Fatalf("retry reused key: %v", keys)
	}
	if errOut.String() != "Idempotency-Key: "+keys[0]+"\n" {
		t.Fatalf("stderr=%q key=%q", errOut.String(), keys[0])
	}
}

func TestRunViewYamlUsesBoundDefinitionEndpoint(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		if r.URL.Path != "/api/workflow-runs/wr-1/yaml" {
			t.Fatalf("path=%q", r.URL.Path)
		}
		return response(http.StatusOK, `{"success":true,"data":{"workflowRunId":"wr-1","yaml":"stages:\n  - stage: build\n"}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"run", "view", "wr-1", "--yaml"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if !strings.Contains(out.String(), "stage: build") {
		t.Fatalf("yaml=%q", out.String())
	}
}

func TestRunStopRequiresConfirmationBeforeResolvingTarget(t *testing.T) {
	calls := 0
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return nil, errors.New("must not call")
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"run", "stop", "--issue", "42"}, deps); code != ExitOperation {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if calls != 0 || !strings.Contains(errOut.String(), "--yes") {
		t.Fatalf("calls=%d stderr=%q", calls, errOut.String())
	}
}

func TestRunControlsUseTheirOwnServerActions(t *testing.T) {
	for _, action := range []string{"approve", "request-changes", "retry", "rerun", "pause", "resume", "stop"} {
		t.Run(action, func(t *testing.T) {
			deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
				want := "/api/workflow-runs/wr-1/" + action
				if action == "rerun" {
					want = "/api/workflow-runs/wr-1/rerun-from-stage"
				}
				if r.URL.Path != want || r.Method != http.MethodPost {
					t.Fatalf("request=%s %s", r.Method, r.URL.Path)
				}
				if r.Header.Get("Idempotency-Key") == "" {
					t.Fatalf("control write sent no Idempotency-Key")
				}
				return response(http.StatusOK, `{"success":true,"data":{}}`), nil
			}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
			args := []string{"run", action, "wr-1"}
			switch action {
			case "approve":
				args = append(args, "--display-name", "reviewer")
			case "request-changes":
				args = append(args, "--message", "fix it")
			case "rerun":
				args = append(args, "--from-stage", "check")
			case "stop":
				args = append(args, "--yes")
			}
			if code := Run(context.Background(), args, deps); code != ExitOK {
				t.Fatalf("code=%d stderr=%q", code, errOut.String())
			}
		})
	}
}

func TestRunWatchRetriesReadAfterReconnectAndStopsOnTerminal(t *testing.T) {
	requests := 0
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		requests++
		if requests == 1 {
			return nil, errors.New("temporary read failure")
		}
		return response(http.StatusOK, `{"success":true,"data":{"status":{"workflowRunId":"wr-1","status":"completed","currentStage":"build"}}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	deps.Wait = func(context.Context, time.Duration) error { return nil }

	if code := Run(context.Background(), []string{"run", "watch", "wr-1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if requests != 2 || !strings.Contains(out.String(), `"status":"completed"`) {
		t.Fatalf("requests=%d stdout=%q", requests, out.String())
	}
}

func TestRunWatchReturnsCancelledFromInjectedWait(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":{"status":{"workflowRunId":"wr-1","status":"running","currentStage":"build"}}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	deps.Wait = func(context.Context, time.Duration) error { return context.Canceled }

	if code := Run(context.Background(), []string{"run", "watch", "wr-1"}, deps); code != ExitCanceled {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
}

func TestRunArtifactGetStreamsRecordedBytes(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		switch r.URL.Path {
		case "/api/workflow-runs/wr-1":
			return response(http.StatusOK, `{"success":true,"data":{"issueRef":{"projectId":"proj-1","number":42}}}`), nil
		case "/api/projects/proj-1/issues/42/workflow/artifacts/a-1/content":
			return &http.Response{StatusCode: http.StatusOK, Body: io.NopCloser(strings.NewReader("artifact bytes")), Header: make(http.Header)}, nil
		default:
			t.Fatalf("unexpected path=%q", r.URL.Path)
			return nil, nil
		}
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"run", "artifact", "get", "wr-1", "a-1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if out.String() != "artifact bytes" {
		t.Fatalf("output=%q", out.String())
	}
}
