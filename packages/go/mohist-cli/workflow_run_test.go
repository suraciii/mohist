package mohistcli

import (
	"context"
	"encoding/json"
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
	deps.Input = strings.NewReader("stages:\n  - not-a-valid-stage: true\n")

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
		{name: "run view", args: []string{"run", "view", "--json"}, want: runFields},
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

func TestRunViewSelectedBindingReadsActualRunBinding(t *testing.T) {
	detail := `{"success":true,"data":{"issueRef":{"projectId":"proj-1","number":42},"status":{"workflowRunId":"wr-1","status":"running","currentStage":"build"}}}`
	bindingData := `{"workflowRunId":"wr-1","projectId":"proj-1","issueNumber":42,"status":"running","workflowProfileId":"spec/workflow","explicitWorkflowProfileId":null,"createdAt":"2026-09-27T00:00:00Z","startedAt":"2026-09-27T00:01:00Z","definition":{"available":true,"source":"run-snapshot","reason":null,"content":{"stages":[{"stage":"build","tasks":[{"id":"build","uses":"spec/task"}],"checks":[]}]}}}`
	binding := `{"success":true,"data":` + bindingData + `}`

	t.Run("selected binding returns the structured read", func(t *testing.T) {
		paths := []string{}
		deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
			paths = append(paths, r.Method+" "+r.URL.Path)
			switch r.URL.Path {
			case "/api/workflow-runs/wr-1":
				return response(http.StatusOK, detail), nil
			case "/api/workflow-runs/wr-1/binding":
				return response(http.StatusOK, binding), nil
			default:
				t.Fatalf("unexpected path=%q", r.URL.Path)
				return nil, nil
			}
		}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

		if code := Run(context.Background(), []string{"run", "view", "wr-1", "--json", "binding"}, deps); code != ExitOK {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		if len(paths) != 2 || paths[0] != "GET /api/workflow-runs/wr-1" || paths[1] != "GET /api/workflow-runs/wr-1/binding" {
			t.Fatalf("paths=%v", paths)
		}
		if want := `{"binding":` + bindingData + `}` + "\n"; out.String() != want {
			t.Fatalf("stdout=%q want=%q", out.String(), want)
		}
	})

	t.Run("default view stays concise without the binding", func(t *testing.T) {
		requests := 0
		deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
			requests++
			if r.URL.Path != "/api/workflow-runs/wr-1" {
				t.Fatalf("path=%q", r.URL.Path)
			}
			return response(http.StatusOK, detail), nil
		}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

		if code := Run(context.Background(), []string{"run", "view", "wr-1"}, deps); code != ExitOK {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		if requests != 1 || strings.Contains(out.String(), "binding") {
			t.Fatalf("requests=%d stdout=%q", requests, out.String())
		}
	})

	t.Run("mixed selection keeps status facts and binding together", func(t *testing.T) {
		deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
			switch r.URL.Path {
			case "/api/workflow-runs/wr-1":
				return response(http.StatusOK, detail), nil
			case "/api/workflow-runs/wr-1/binding":
				return response(http.StatusOK, binding), nil
			default:
				t.Fatalf("unexpected path=%q", r.URL.Path)
				return nil, nil
			}
		}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

		if code := Run(context.Background(), []string{"run", "view", "wr-1", "--json", "id,binding"}, deps); code != ExitOK {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		var projected map[string]any
		if err := json.Unmarshal([]byte(out.String()), &projected); err != nil {
			t.Fatalf("stdout=%q: %v", out.String(), err)
		}
		if projected["id"] != "wr-1" {
			t.Fatalf("id=%v", projected["id"])
		}
		bindingField, ok := projected["binding"].(map[string]any)
		if !ok || bindingField["workflowProfileId"] != "spec/workflow" {
			t.Fatalf("binding=%v", projected["binding"])
		}
		definition, ok := bindingField["definition"].(map[string]any)
		if !ok || definition["available"] != true || definition["source"] != "run-snapshot" {
			t.Fatalf("definition=%v", bindingField["definition"])
		}
	})

	t.Run("binding read failure fails the command without stdout", func(t *testing.T) {
		deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
			switch r.URL.Path {
			case "/api/workflow-runs/wr-1":
				return response(http.StatusOK, detail), nil
			case "/api/workflow-runs/wr-1/binding":
				return response(http.StatusNotFound, `{"success":false,"error":"Workflow run 'wr-1' not found","code":"not_found"}`), nil
			default:
				t.Fatalf("unexpected path=%q", r.URL.Path)
				return nil, nil
			}
		}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

		if code := Run(context.Background(), []string{"run", "view", "wr-1", "--json", "binding"}, deps); code != ExitOperation {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		if out.Len() != 0 || !strings.Contains(errOut.String(), "not_found") {
			t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
		}
	})
}

func TestRunViewBindingReportsUnavailableDefinitionWithReason(t *testing.T) {
	detail := `{"success":true,"data":{"issueRef":{"projectId":"proj-1","number":42},"status":{"workflowRunId":"wr-1","status":"stopped","currentStage":"build"}}}`
	bindingData := `{"workflowRunId":"wr-1","projectId":"proj-1","issueNumber":42,"status":"stopped","workflowProfileId":"spec/workflow","explicitWorkflowProfileId":null,"createdAt":"2026-09-27T00:00:00Z","startedAt":"2026-09-27T00:01:00Z","definition":{"available":false,"source":null,"reason":"no-snapshot","content":null}}`
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		switch r.URL.Path {
		case "/api/workflow-runs/wr-1":
			return response(http.StatusOK, detail), nil
		case "/api/workflow-runs/wr-1/binding":
			return response(http.StatusOK, `{"success":true,"data":`+bindingData+`}`), nil
		default:
			t.Fatalf("unexpected path=%q", r.URL.Path)
			return nil, nil
		}
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"run", "view", "wr-1", "--json", "binding"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	var projected map[string]any
	if err := json.Unmarshal([]byte(out.String()), &projected); err != nil {
		t.Fatalf("stdout=%q: %v", out.String(), err)
	}
	binding, ok := projected["binding"].(map[string]any)
	if !ok {
		t.Fatalf("stdout=%q", out.String())
	}
	if binding["status"] != "stopped" || binding["workflowProfileId"] != "spec/workflow" {
		t.Fatalf("binding=%v", binding)
	}
	definition, ok := binding["definition"].(map[string]any)
	if !ok || definition["available"] != false || definition["reason"] != "no-snapshot" || definition["content"] != nil {
		t.Fatalf("definition=%v", binding["definition"])
	}
}

func TestRunControlSelectedBindingAnswersThroughSharedCatalog(t *testing.T) {
	controlResult := `{"success":true,"data":{"issueRef":{"projectId":"proj-1","number":42},"status":{"workflowRunId":"wr-1","status":"paused","currentStage":"build"},"workflowProfileId":"spec/workflow"}}`
	bindingData := `{"workflowRunId":"wr-1","projectId":"proj-1","issueNumber":42,"status":"paused","workflowProfileId":"spec/workflow","definition":{"available":true,"source":"run-snapshot","reason":null,"content":{"stages":[]}}}`
	paths := []string{}
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		paths = append(paths, r.Method+" "+r.URL.Path)
		switch r.URL.Path {
		case "/api/workflow-runs/wr-1/pause":
			return response(http.StatusOK, controlResult), nil
		case "/api/workflow-runs/wr-1/binding":
			return response(http.StatusOK, `{"success":true,"data":`+bindingData+`}`), nil
		default:
			t.Fatalf("unexpected path=%q", r.URL.Path)
			return nil, nil
		}
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"run", "pause", "wr-1", "--json", "status,binding"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(paths) != 2 || paths[0] != "POST /api/workflow-runs/wr-1/pause" || paths[1] != "GET /api/workflow-runs/wr-1/binding" {
		t.Fatalf("paths=%v", paths)
	}
	var projected map[string]any
	if err := json.Unmarshal([]byte(out.String()), &projected); err != nil {
		t.Fatalf("stdout=%q: %v", out.String(), err)
	}
	if projected["status"] != "paused" {
		t.Fatalf("status=%v", projected["status"])
	}
	binding, ok := projected["binding"].(map[string]any)
	if !ok || binding["workflowRunId"] != "wr-1" {
		t.Fatalf("binding=%v", projected["binding"])
	}
}

func TestWorkflowEditRequiresExpectedRevisionBeforeAnyRequest(t *testing.T) {
	calls := 0
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return nil, errors.New("must not call")
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	deps.ReadFile = func(string) (string, error) {
		return "stages:\n  - stage: build\n    tasks: []\n    checks: []\n", nil
	}

	if code := Run(context.Background(), []string{"workflow", "edit", "ship", "--project", "proj-1", "--file", "workflow.yaml"}, deps); code != ExitUsage {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if calls != 0 || out.Len() != 0 || !strings.Contains(errOut.String(), "--expected-revision is required") {
		t.Fatalf("calls=%d stdout=%q stderr=%q", calls, out.String(), errOut.String())
	}
}

func TestWorkflowEditSendsExpectedRevisionWithSavedContent(t *testing.T) {
	var body map[string]any
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		if r.Method != http.MethodPut || r.URL.Path != "/api/projects/proj-1/workflow-profiles/ship" {
			t.Fatalf("request=%s %s", r.Method, r.URL.Path)
		}
		raw, err := io.ReadAll(r.Body)
		if err != nil {
			t.Fatalf("read body: %v", err)
		}
		if err := json.Unmarshal(raw, &body); err != nil {
			t.Fatalf("body=%q: %v", raw, err)
		}
		return response(http.StatusOK, `{"success":true,"data":{"projectId":"proj-1","profileId":"ship","revision":"rev-2","definitionSource":"stages:\n  - stage: build\n"},"validation":`+workflowSaveSkippedValidation+`}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	deps.ReadFile = func(string) (string, error) {
		return "stages:\n  - stage: build\n", nil
	}

	if code := Run(context.Background(), []string{"workflow", "edit", "ship", "--project", "proj-1", "--file", "workflow.yaml", "--expected-revision", "rev-1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if body["expectedRevision"] != "rev-1" || body["definitionSource"] != "stages:\n  - stage: build\n" {
		t.Fatalf("body=%v", body)
	}
	if !strings.Contains(out.String(), `"revision":"rev-2"`) {
		t.Fatalf("stdout=%q", out.String())
	}
}

func TestWorkflowEditRevisionConflictKeepsDraftAndExplainsRecovery(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusConflict, `{"success":false,"error":"WorkflowProfile 'ship' in project 'proj-1' was changed since revision 'rev-1' was read; current revision is 'rev-9'.","code":"workflow_profile_revision_conflict","details":{"currentRevision":"rev-9"}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	deps.ReadFile = func(string) (string, error) {
		return "stages:\n  - stage: build\n", nil
	}

	if code := Run(context.Background(), []string{"workflow", "edit", "ship", "--project", "proj-1", "--file", "workflow.yaml", "--expected-revision", "rev-1"}, deps); code != ExitOperation {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if out.Len() != 0 {
		t.Fatalf("stdout=%q", out.String())
	}
	for _, want := range []string{"workflow_profile_revision_conflict", "Draft kept", "mo workflow view ship"} {
		if !strings.Contains(errOut.String(), want) {
			t.Fatalf("stderr=%q want %q", errOut.String(), want)
		}
	}
}

func TestWorkflowCreateRejectsExpectedRevisionFlag(t *testing.T) {
	calls := 0
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return nil, errors.New("must not call")
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	deps.ReadFile = func(string) (string, error) {
		return "stages:\n  - stage: build\n", nil
	}

	if code := Run(context.Background(), []string{"workflow", "create", "ship", "--project", "proj-1", "--file", "workflow.yaml", "--expected-revision", "rev-1"}, deps); code != ExitUsage {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if calls != 0 || !strings.Contains(errOut.String(), "--expected-revision is only valid with mo workflow edit") {
		t.Fatalf("calls=%d stderr=%q", calls, errOut.String())
	}
}

func TestWorkflowEditHelpAndFieldDiscoveryStayOffline(t *testing.T) {
	tests := []struct {
		args []string
		want []string
	}{
		{args: []string{"workflow", "edit", "ship", "--json"}, want: []string{strings.Join(workflowSaveFields, "\n")}},
		{args: []string{"workflow", "edit", "--help"}, want: []string{"USAGE", "--expected-revision", strings.Join(workflowSaveFields, "\n")}},
		{args: []string{"workflow", "edit", "ship", "-h"}, want: []string{"USAGE", "--expected-revision", strings.Join(workflowSaveFields, "\n")}},
	}
	for _, test := range tests {
		t.Run(strings.Join(test.args, " "), func(t *testing.T) {
			calls := 0
			deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
				calls++
				return nil, errors.New("must not call")
			}), map[string]string{})
			deps.ReadFile = func(string) (string, error) {
				t.Fatal("file must not be read")
				return "", nil
			}
			code := Run(context.Background(), test.args, deps)
			if code != ExitOK || calls != 0 || errOut.Len() != 0 {
				t.Fatalf("code=%d calls=%d stderr=%q", code, calls, errOut.String())
			}
			for _, want := range test.want {
				if !strings.Contains(out.String(), want) {
					t.Fatalf("stdout=%q want %q", out.String(), want)
				}
			}
		})
	}
}
