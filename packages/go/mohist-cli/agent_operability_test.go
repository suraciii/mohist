package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"strings"
	"testing"
	"time"
)

func TestIssueStartSendsCallerKeyHeaderWithoutStderrNoise(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		if r.Method != http.MethodPost || r.URL.Path != "/api/projects/proj-1/issues/42/start" {
			t.Fatalf("request=%s %s", r.Method, r.URL.Path)
		}
		if r.Header.Get("Idempotency-Key") != "caller-key" {
			t.Fatalf("idempotency key=%q", r.Header.Get("Idempotency-Key"))
		}
		return response(http.StatusOK, `{"success":true,"data":{"workflowRunId":"wr-1"}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"issue", "start", "42", "--project", "proj-1", "--idempotency-key", "caller-key"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if !strings.Contains(out.String(), "wr-1") || errOut.Len() != 0 {
		t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
	}
}

func TestIssueStartGeneratesKeyPrintsItToStderrAndSendsIt(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		if r.Header.Get("Idempotency-Key") != "1758777600123456789" {
			t.Fatalf("generated key=%q", r.Header.Get("Idempotency-Key"))
		}
		return response(http.StatusOK, `{"success":true,"data":{"workflowRunId":"wr-1"}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	deps.Now = func() time.Time { return time.Unix(1758777600, 123456789) }

	if code := Run(context.Background(), []string{"issue", "start", "42", "--project", "proj-1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if errOut.String() != "Idempotency-Key: 1758777600123456789\n" {
		t.Fatalf("stderr=%q", errOut.String())
	}
}

func TestIssueStartRetriesLostResponseOnceWithSameKey(t *testing.T) {
	requests := 0
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		requests++
		if r.Header.Get("Idempotency-Key") != "start-1" {
			t.Fatalf("attempt %d key=%q", requests, r.Header.Get("Idempotency-Key"))
		}
		if requests == 1 {
			return nil, errors.New("connection lost after submit")
		}
		return response(http.StatusOK, `{"success":true,"data":{"workflowRunId":"wr-9"}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"issue", "start", "42", "--project", "proj-1", "--idempotency-key", "start-1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if requests != 2 || !strings.Contains(out.String(), "wr-9") {
		t.Fatalf("requests=%d stdout=%q", requests, out.String())
	}
}

func TestIdempotencyKeyIsRejectedOnNonKeyedLeaves(t *testing.T) {
	for _, args := range [][]string{
		{"issue", "view", "42", "--idempotency-key", "k"},
		{"issue", "list", "--idempotency-key", "k"},
		{"run", "view", "wr-1", "--idempotency-key", "k"},
		{"run", "list", "--idempotency-key", "k"},
		{"run", "watch", "wr-1", "--idempotency-key", "k"},
		{"run", "artifact", "list", "wr-1", "--idempotency-key", "k"},
	} {
		t.Run(strings.Join(args, " "), func(t *testing.T) {
			calls := 0
			deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
				calls++
				return nil, errors.New("must not call")
			}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

			if code := Run(context.Background(), args, deps); code != ExitUsage {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			if calls != 0 || out.Len() != 0 || !strings.Contains(errOut.String(), "--idempotency-key") {
				t.Fatalf("calls=%d stdout=%q stderr=%q", calls, out.String(), errOut.String())
			}
		})
	}
}

func TestIdempotencyKeyReusedFailureProjectsServerEnvelope(t *testing.T) {
	envelope := `{"success":false,"error":"key k1 already names a different request","code":"idempotency_key_reused","details":null,"effect":"none","retrySafe":false,"nextAction":"mo run request-changes wr-1 --idempotency-key <new-key>"}`
	t.Run("structured", func(t *testing.T) {
		deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
			if r.Header.Get("Idempotency-Key") != "k1" {
				t.Fatalf("caller key=%q", r.Header.Get("Idempotency-Key"))
			}
			return response(http.StatusConflict, envelope), nil
		}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

		if code := Run(context.Background(), []string{"run", "request-changes", "wr-1", "--message", "tighten", "--idempotency-key", "k1", "--json", "status"}, deps); code != ExitOperation {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		if out.Len() != 0 {
			t.Fatalf("stdout=%q", out.String())
		}
		var failure map[string]any
		if err := json.Unmarshal([]byte(errOut.String()), &failure); err != nil {
			t.Fatalf("stderr=%q: %v", errOut.String(), err)
		}
		if failure["code"] != "idempotency_key_reused" || failure["effect"] != "none" || failure["retrySafe"] != false {
			t.Fatalf("failure=%v", failure)
		}
		if failure["nextAction"] != "mo run request-changes wr-1 --idempotency-key <new-key>" {
			t.Fatalf("nextAction=%v", failure["nextAction"])
		}
		if failure["message"] != "key k1 already names a different request" {
			t.Fatalf("message=%v", failure["message"])
		}
	})
	t.Run("human", func(t *testing.T) {
		deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
			return response(http.StatusConflict, envelope), nil
		}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

		if code := Run(context.Background(), []string{"run", "request-changes", "wr-1", "--message", "tighten", "--idempotency-key", "k1"}, deps); code != ExitOperation {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		if out.Len() != 0 ||
			!strings.Contains(errOut.String(), "error: key k1 already names a different request [idempotency_key_reused]") ||
			!strings.Contains(errOut.String(), "hint: mo run request-changes wr-1 --idempotency-key <new-key>") {
			t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
		}
	})
}

func TestKeyedWriteTransportFailureStatesUnknownEffectAndSameKeyRecovery(t *testing.T) {
	t.Run("structured", func(t *testing.T) {
		deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
			return nil, errors.New("connection refused")
		}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

		if code := Run(context.Background(), []string{"run", "retry", "wr-1", "--idempotency-key", "k1", "--json", "status"}, deps); code != ExitOperation {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		if out.Len() != 0 {
			t.Fatalf("stdout=%q", out.String())
		}
		var failure map[string]any
		if err := json.Unmarshal([]byte(errOut.String()), &failure); err != nil {
			t.Fatalf("stderr=%q: %v", errOut.String(), err)
		}
		if failure["code"] != "service_unavailable" || failure["effect"] != "unknown" || failure["retrySafe"] != true {
			t.Fatalf("failure=%v", failure)
		}
		if failure["nextAction"] != "mo run retry wr-1 --idempotency-key k1" {
			t.Fatalf("nextAction=%v", failure["nextAction"])
		}
	})
	t.Run("human", func(t *testing.T) {
		deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
			return nil, errors.New("connection refused")
		}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

		if code := Run(context.Background(), []string{"run", "retry", "wr-1", "--idempotency-key", "k1"}, deps); code != ExitOperation {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		if out.Len() != 0 ||
			!strings.Contains(errOut.String(), "error: Mohist Server request failed [service_unavailable]") ||
			!strings.Contains(errOut.String(), "hint: mo run retry wr-1 --idempotency-key k1") {
			t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
		}
	})
}

func TestRunViewProjectsDecisionFieldsAndAnnouncesActions(t *testing.T) {
	detail := `{"success":true,"data":{"issueRef":{"projectId":"proj-1","number":42},"status":{"workflowRunId":"wr-1","status":"failed","currentStage":"build","stages":["build"],"pendingWork":{"attemptId":"a-1"},"failure":{"stage":"build","reason":"tests failed"},"availableActions":["retry","rerun"],"assignedTo":"runner-1"}}}`
	t.Run("selected field", func(t *testing.T) {
		deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
			if r.URL.Path != "/api/workflow-runs/wr-1" {
				t.Fatalf("path=%q", r.URL.Path)
			}
			return response(http.StatusOK, detail), nil
		}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

		if code := Run(context.Background(), []string{"run", "view", "wr-1", "--json", "availableActions"}, deps); code != ExitOK {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		if out.String() != `{"availableActions":["retry","rerun"]}`+"\n" || errOut.Len() != 0 {
			t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
		}
	})
	t.Run("human announces actions once", func(t *testing.T) {
		deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
			return response(http.StatusOK, detail), nil
		}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

		if code := Run(context.Background(), []string{"run", "view", "wr-1"}, deps); code != ExitOK {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		if !strings.Contains(out.String(), `"assignedTo":"runner-1"`) || errOut.String() != "Available actions: retry, rerun\n" {
			t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
		}
	})
	t.Run("human stays silent without actions", func(t *testing.T) {
		deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
			return response(http.StatusOK, `{"success":true,"data":{"issueRef":{"projectId":"proj-1","number":42},"status":{"workflowRunId":"wr-1","status":"running","currentStage":"build"}}}`), nil
		}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

		if code := Run(context.Background(), []string{"run", "view", "wr-1"}, deps); code != ExitOK {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		if errOut.Len() != 0 || !strings.Contains(out.String(), `"currentStage":"build"`) {
			t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
		}
	})
}

func TestJSONSelectedParseFailureReportsStructuredUsageError(t *testing.T) {
	t.Run("issue family", func(t *testing.T) {
		calls := 0
		deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
			calls++
			return nil, errors.New("must not call")
		}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

		if code := Run(context.Background(), []string{"issue", "view", "42", "--json", "unknown"}, deps); code != ExitUsage {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		var failure map[string]any
		if err := json.Unmarshal([]byte(errOut.String()), &failure); err != nil {
			t.Fatalf("stderr=%q: %v", errOut.String(), err)
		}
		if calls != 0 || out.Len() != 0 || failure["code"] != "usage_error" || failure["effect"] != "none" || failure["retrySafe"] != false {
			t.Fatalf("calls=%d stdout=%q failure=%v", calls, out.String(), failure)
		}
	})
	t.Run("other families keep the prose diagnostic", func(t *testing.T) {
		deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
			return nil, errors.New("must not call")
		}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

		if code := Run(context.Background(), []string{"workflow", "view", "prof-1", "--json", "unknown"}, deps); code != ExitUsage {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		if out.Len() != 0 || strings.HasPrefix(errOut.String(), "{") || !strings.Contains(errOut.String(), "unknown JSON field") {
			t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
		}
	})
}
