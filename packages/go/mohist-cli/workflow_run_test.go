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

func TestWorkflowValidateIsLocalAndReadsFile(t *testing.T) {
	calls := 0
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return nil, errors.New("must not call")
	}), map[string]string{})
	deps.ReadFile = func(string) (string, error) {
		return "stages:\n  - stage: build\n    tasks:\n      - id: build\n        uses: shell\n", nil
	}

	if code := Run(context.Background(), []string{"workflow", "validate", "--file", "workflow.yaml"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if calls != 0 || !strings.Contains(out.String(), "Workflow Profile is valid") {
		t.Fatalf("calls=%d stdout=%q", calls, out.String())
	}
}

func TestRunControlUsesDistinctEndpointsAndDoesNotRetryMutation(t *testing.T) {
	requests := 0
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		requests++
		if r.URL.Path != "/api/workflow-runs/wr-1/retry" {
			t.Fatalf("path=%q", r.URL.Path)
		}
		return nil, errors.New("connection lost after submit")
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"run", "retry", "wr-1"}, deps); code != ExitOperation {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if requests != 1 {
		t.Fatalf("mutation was retried: %d requests", requests)
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
