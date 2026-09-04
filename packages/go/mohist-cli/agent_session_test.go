package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"reflect"
	"strings"
	"testing"
	"time"
)

func TestAgentEditReplacesNestedConfigWithSetAndClearOperations(t *testing.T) {
	requests := 0
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		requests++
		switch {
		case r.Method == http.MethodGet && r.URL.Path == "/api/projects/proj-1/agents/agent_1":
			return response(http.StatusOK, `{"success":true,"data":{"id":"agent_1","name":"reviewer","agentConfig":{"runtime":"opencode","model":"openai/gpt-5.6-luna","variant":"xhigh","type":"legacy","compaction":{"enabled":true}}}}`), nil
		case r.Method == http.MethodPatch && r.URL.Path == "/api/projects/proj-1/agents/agent_1":
			var body map[string]any
			if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
				t.Fatalf("decode body: %v", err)
			}
			expected := map[string]any{"agentConfig": map[string]any{
				"runtime": "pi", "model": "openai/gpt-5.6-luna", "reasoningEffort": "xhigh",
			}}
			if !reflect.DeepEqual(body, expected) {
				t.Fatalf("body=%#v expected=%#v", body, expected)
			}
			return response(http.StatusOK, `{"success":true,"data":{"id":"agent_1","name":"reviewer"}}`), nil
		default:
			t.Fatalf("unexpected request %s %s", r.Method, r.URL.Path)
			return nil, nil
		}
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	args := []string{
		"agent", "edit", "agent_1", "--project", "proj-1",
		"--runtime", "pi", "--model", "openai/gpt-5.6-luna",
		"--clear-variant", "--reasoning-effort", "xhigh",
	}
	if code := Run(context.Background(), args, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if requests != 2 {
		t.Fatalf("requests=%d", requests)
	}
}

func TestAgentEditClearPreservesUntouchedNestedConfig(t *testing.T) {
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		switch r.Method {
		case http.MethodGet:
			return response(http.StatusOK, `{"success":true,"data":{"id":"agent_1","name":"reviewer","agentConfig":{"runtime":"pi","model":"openai/gpt-5.6-luna","reasoningEffort":"xhigh"}}}`), nil
		case http.MethodPatch:
			var body map[string]any
			if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
				t.Fatalf("decode body: %v", err)
			}
			expected := map[string]any{"agentConfig": map[string]any{
				"runtime": "pi", "reasoningEffort": "xhigh",
			}}
			if !reflect.DeepEqual(body, expected) {
				t.Fatalf("body=%#v expected=%#v", body, expected)
			}
			return response(http.StatusOK, `{"success":true,"data":{"id":"agent_1","name":"reviewer"}}`), nil
		default:
			t.Fatalf("unexpected request %s %s", r.Method, r.URL.Path)
			return nil, nil
		}
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"agent", "edit", "agent_1", "--project", "proj-1", "--clear-model"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
}

func TestAgentEditClearingFinalConfigFieldSendsNull(t *testing.T) {
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		switch r.Method {
		case http.MethodGet:
			return response(http.StatusOK, `{"success":true,"data":{"id":"agent_1","name":"reviewer","agentConfig":{"variant":"xhigh"}}}`), nil
		case http.MethodPatch:
			var body map[string]any
			if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
				t.Fatalf("decode body: %v", err)
			}
			value, exists := body["agentConfig"]
			if !exists || value != nil || len(body) != 1 {
				t.Fatalf("body=%#v", body)
			}
			return response(http.StatusOK, `{"success":true,"data":{"id":"agent_1","name":"reviewer"}}`), nil
		default:
			t.Fatalf("unexpected request %s %s", r.Method, r.URL.Path)
			return nil, nil
		}
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"agent", "edit", "agent_1", "--project", "proj-1", "--clear-variant"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
}

func TestAgentEditRejectsSetAndClearBeforeRequest(t *testing.T) {
	requests := 0
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		requests++
		return nil, errors.New("unexpected request")
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	code := Run(context.Background(), []string{
		"agent", "edit", "agent_1", "--project", "proj-1",
		"--reasoning-effort", "xhigh", "--clear-reasoning-effort",
	}, deps)
	if code != ExitUsage || requests != 0 {
		t.Fatalf("code=%d requests=%d stderr=%q", code, requests, errOut.String())
	}
	if !strings.Contains(errOut.String(), "--reasoning-effort cannot be used with --clear-reasoning-effort") {
		t.Fatalf("stderr=%q", errOut.String())
	}
}

func TestAgentEditRejectsUnknownClearFlagBeforeRequest(t *testing.T) {
	requests := 0
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		requests++
		return nil, errors.New("unexpected request")
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	code := Run(context.Background(), []string{
		"agent", "edit", "agent_1", "--project", "proj-1",
		"--runtime", "pi", "--clear-variantx",
	}, deps)
	if code != ExitUsage || requests != 0 {
		t.Fatalf("code=%d requests=%d stderr=%q", code, requests, errOut.String())
	}
	if !strings.Contains(errOut.String(), "unknown option --clear-variantx") {
		t.Fatalf("stderr=%q", errOut.String())
	}
}

func TestAgentLaunchPreservesWorkspaceAndParentContext(t *testing.T) {
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		if r.URL.Path != "/api/projects/proj-1/agents/agent_1/sessions" {
			t.Fatalf("path=%q", r.URL.Path)
		}
		if r.Header.Get("Idempotency-Key") != "launch-1" {
			t.Fatalf("idempotency key=%q", r.Header.Get("Idempotency-Key"))
		}
		return response(http.StatusOK, `{"success":true,"data":{"jobId":"job-1","sessionId":"sess-1","workspaceId":"ws-1","targetId":"issue-42"}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"agent", "launch", "agent_1", "--prompt", "inspect", "--workspace", "ws-1", "--issue", "42", "--idempotency-key", "launch-1", "--project", "proj-1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
}

func TestSessionFollowupRetriesWithSameKey(t *testing.T) {
	attempts := 0
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		attempts++
		if r.URL.Path != "/api/projects/proj-1/agent-sessions/sess-1/followup" {
			t.Fatalf("path=%q", r.URL.Path)
		}
		if r.Header.Get("Idempotency-Key") != "follow-1" {
			t.Fatalf("key=%q", r.Header.Get("Idempotency-Key"))
		}
		if attempts == 1 {
			return nil, errors.New("response lost")
		}
		return response(http.StatusOK, `{"success":true,"data":{"sessionId":"sess-1","inputId":"input-1","turnId":"turn-1","status":"accepted"}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"session", "followup", "sess-1", "--text", "continue", "--idempotency-key", "follow-1", "--project", "proj-1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if attempts != 2 || !strings.Contains(out.String(), "input-1") {
		t.Fatalf("attempts=%d stdout=%q", attempts, out.String())
	}
}

func TestSessionScheduleValidatesFutureRFC3339AndSendsVerbatim(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		if r.URL.Path != "/api/projects/proj-1/agent-sessions/sess-1/schedules" {
			t.Fatalf("path=%q", r.URL.Path)
		}
		return response(http.StatusOK, `{"success":true,"data":{"scheduleId":"sch-1","status":"scheduled","dueAt":"2030-01-02T03:04:05+08:00","text":"later"}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	deps.Now = func() time.Time { return time.Date(2029, time.January, 1, 0, 0, 0, 0, time.UTC) }

	if code := Run(context.Background(), []string{"session", "schedule", "create", "sess-1", "--at", "2030-01-02T03:04:05+08:00", "--text", "later", "--idempotency-key", "schedule-1", "--project", "proj-1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if !strings.Contains(out.String(), "sch-1") || errOut.Len() != 0 {
		t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
	}
}

func TestAgentSubscriptionCreateResolvesAgentAndIsIdempotent(t *testing.T) {
	requests := 0
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		requests++
		switch {
		case r.Method == http.MethodGet && r.URL.Path == "/api/projects/proj-1/agents/agent_1":
			return response(http.StatusOK, `{"success":true,"data":{"id":"agent_1","name":"reviewer"}}`), nil
		case r.Method == http.MethodPost && r.URL.Path == "/api/projects/proj-1/agents/agent_1/subscriptions":
			if r.Header.Get("Idempotency-Key") != "sub-1" {
				t.Fatalf("key=%q", r.Header.Get("Idempotency-Key"))
			}
			return response(http.StatusOK, `{"success":true,"data":{"id":"sub-1","name":"notify","match":"event.type == x","responsePrompt":"reply","continue":false,"status":"active"}}`), nil
		default:
			t.Fatalf("unexpected request %s %s", r.Method, r.URL.Path)
			return nil, nil
		}
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"agent", "subscription", "create", "agent_1", "--name", "notify", "--match", "event.type == x", "--response-prompt", "reply", "--idempotency-key", "sub-1", "--project", "proj-1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if requests != 2 {
		t.Fatalf("requests=%d", requests)
	}
}

func TestSessionStopRequiresKeyAndCancellationIsPreserved(t *testing.T) {
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return nil, context.Canceled
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})
	if code := Run(context.Background(), []string{"session", "stop", "sess-1", "--project", "proj-1"}, deps); code != ExitUsage {
		t.Fatalf("missing key code=%d", code)
	}
	if code := Run(context.Background(), []string{"session", "stop", "sess-1", "--idempotency-key", "stop-1", "--project", "proj-1"}, deps); code != ExitCanceled {
		t.Fatalf("cancel code=%d stderr=%q", code, errOut.String())
	}
}
