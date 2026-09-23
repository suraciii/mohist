package mohistcli

import (
	"context"
	"encoding/json"
	"net/http"
	"reflect"
	"strings"
	"testing"
)

func TestSessionViewConvergenceFieldsAreDiscoverableWithoutRequest(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		t.Fatal("field discovery must not send a request")
		return nil, nil
	}), nil)

	if code := Run(context.Background(), []string{"session", "view", "--json"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	for _, field := range []string{"contextGeneration", "unresolvedPrevious", "unresolvedPreviousCount", "nextAction"} {
		if !strings.Contains(out.String(), field) {
			t.Fatalf("field %q missing from %q", field, out.String())
		}
	}
}

func TestSessionViewPreservesSupersededUnknownIdentityInSelectedJSON(t *testing.T) {
	requests := 0
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		requests++
		if r.Method != http.MethodGet || r.URL.Path != "/api/projects/proj-1/sessions/sess-1" {
			t.Fatalf("unexpected request %s %s", r.Method, r.URL.Path)
		}
		return response(http.StatusOK, `{"success":true,"data":{"id":"sess-1","activity":"idle","contextGeneration":2,"unresolvedPrevious":[{"id":"old-turn","status":"unknown","contextGeneration":1,"supersededAt":"2026-09-22T08:00:00Z"}],"unresolvedPreviousCount":1,"nextAction":"inspect_previous_execution"}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{
		"session", "view", "sess-1", "--project", "proj-1", "--json",
		"activity,contextGeneration,unresolvedPrevious,unresolvedPreviousCount,nextAction",
	}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	var actual, expected map[string]any
	if err := json.Unmarshal([]byte(out.String()), &actual); err != nil {
		t.Fatalf("decode stdout %q: %v", out.String(), err)
	}
	if err := json.Unmarshal([]byte(`{"activity":"idle","contextGeneration":2,"unresolvedPrevious":[{"id":"old-turn","status":"unknown","contextGeneration":1,"supersededAt":"2026-09-22T08:00:00Z"}],"unresolvedPreviousCount":1,"nextAction":"inspect_previous_execution"}`), &expected); err != nil {
		t.Fatal(err)
	}
	if !reflect.DeepEqual(actual, expected) {
		t.Fatalf("projection=%#v expected=%#v", actual, expected)
	}
	if requests != 1 {
		t.Fatalf("requests=%d", requests)
	}
}
