package mohistcli

import (
	"context"
	"net/http"
	"strings"
	"testing"
)

func TestActivityBareJSONAndInvalidLimitAreLocal(t *testing.T) {
	called := false
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		called = true
		return response(200, `{"success":true,"data":[]}`), nil
	}), map[string]string{"MOHIST_TOKEN": "token"})
	if code := Run(context.Background(), []string{"activity", "list", "--json", "--limit", "0"}, deps); code != ExitOK {
		t.Fatalf("discovery code=%d stderr=%q", code, errOut.String())
	}
	if called || !strings.Contains(out.String(), "provenance") {
		t.Fatalf("called=%v output=%q", called, out.String())
	}
	*out, *errOut = strings.Builder{}, strings.Builder{}
	if code := Run(context.Background(), []string{"activity", "list", "--limit", "0"}, deps); code != ExitUsage || called {
		t.Fatalf("validation code=%d called=%v stderr=%q", code, called, errOut.String())
	}
}

func TestActivityListUsesProjectAndEmptyResult(t *testing.T) {
	var got *http.Request
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		got = r
		return response(200, `{"success":true,"data":[]}`), nil
	}), map[string]string{"MOHIST_TOKEN": "token"})
	if code := Run(context.Background(), []string{"activity", "list", "--project", "proj", "--limit", "50"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if got == nil || got.URL.EscapedPath() != "/api/projects/proj/activity" || got.URL.RawQuery != "limit=50" || out.String() != "No activity\n" {
		t.Fatalf("request=%v output=%q", got, out.String())
	}
}

func TestActivityListExposesExecutionStateAndEvidence(t *testing.T) {
	const row = `{"success":true,"data":[{"id":"a1","provenance":"recorded","scope":"session","kind":"agent-session","time":"2026-09-25T10:00:00Z","title":"Session s1 needs verification","status":"active","executionState":"needs-verification","evidence":{"reason":"aged","observedAt":"2026-09-08T00:00:00Z"}}]}`
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(200, row), nil
	}), map[string]string{"MOHIST_TOKEN": "token"})

	if code := Run(context.Background(), []string{"activity", "list", "--project", "proj", "--json", "executionState,evidence"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if selected := out.String(); !strings.Contains(selected, `"executionState":"needs-verification"`) || !strings.Contains(selected, `"reason":"aged"`) {
		t.Fatalf("output=%q", selected)
	}

	*out, *errOut = strings.Builder{}, strings.Builder{}
	if code := Run(context.Background(), []string{"activity", "list", "--project", "proj"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	human := out.String()
	if !strings.Contains(human, "state") || !strings.Contains(human, "needs-verification") {
		t.Fatalf("output=%q", human)
	}
}

func TestRoutingCreateResolvesAgentAndPreservesPosition(t *testing.T) {
	requests := 0
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		requests++
		if r.URL.Path == "/api/projects/proj/agents" {
			return response(200, `{"success":true,"data":[{"id":"agent_1","name":"reviewer"}]}`), nil
		}
		return response(200, `{"success":true,"data":{"id":"rule_1"}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "token"})
	if code := Run(context.Background(), []string{"routing", "rule", "create", "--name", "rule", "--match", "event.type == \"x\"", "--agent", "reviewer", "--response-prompt", "respond", "--before", "rule_a", "--project", "proj"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if requests != 2 {
		t.Fatalf("requests=%d", requests)
	}
}

func TestRoutingMoveRejectsConflictingPositionsWithoutHTTP(t *testing.T) {
	called := false
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) { called = true; return nil, nil }), map[string]string{"MOHIST_TOKEN": "token"})
	if code := Run(context.Background(), []string{"routing", "rule", "move", "rule_a", "--before", "rule_b", "--after", "rule_c", "--project", "proj"}, deps); code != ExitUsage || called || !strings.Contains(errOut.String(), "mutually exclusive") {
		t.Fatalf("code=%d called=%v stderr=%q", code, called, errOut.String())
	}
}

func TestWebhookSecretIsForwardedButNeverRendered(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		if r.URL.Path != "/api/projects/proj/webhook/subscriptions" {
			t.Fatalf("path=%s", r.URL.Path)
		}
		return response(201, `{"success":true,"data":{"id":"sub_1","name":"release","secret":"server-secret","hasSecret":true}}`), nil
	}), map[string]string{"MOHIST_TOKEN": "token"})
	if code := Run(context.Background(), []string{"webhook", "subscription", "create", "release", "--target-url", "https://hooks.example", "--secret", "caller-secret", "--project", "proj"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if strings.Contains(out.String(), "secret") || strings.Contains(errOut.String(), "caller-secret") {
		t.Fatalf("secret leaked stdout=%q stderr=%q", out.String(), errOut.String())
	}
}

func TestWebhookDeleteRequiresConfirmationBeforeHTTP(t *testing.T) {
	called := false
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) { called = true; return nil, nil }), map[string]string{"MOHIST_TOKEN": "token"})
	if code := Run(context.Background(), []string{"webhook", "subscription", "delete", "sub_1", "--project", "proj"}, deps); code != ExitUsage || called || !strings.Contains(errOut.String(), "--yes") {
		t.Fatalf("code=%d called=%v stderr=%q", code, called, errOut.String())
	}
}
