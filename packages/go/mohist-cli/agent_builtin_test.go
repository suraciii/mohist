package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"reflect"
	"strings"
	"testing"
)

// The built-in read model omits unset values (the Server writes no nulls), so
// an unset Model has no key at all rather than a fabricated model string.
const builtInBuilderJSON = `{"id":"builtin:mohist/builder","projectId":"proj-1","name":"mohist/builder","description":"Mohist's built-in Workflow implementation Agent.","instructions":"Build the change.","agentConfig":{"runtime":"pi"},"skills":[],"status":"active","createdAt":"","updatedAt":"","permissions":[],"effectiveExecutionConfig":{"runtime":"pi"},"executability":{"state":"executable","gaps":[]},"origin":"built-in","overridesBuiltIn":false}`

const builtInReviewerJSON = `{"id":"builtin:mohist/reviewer","projectId":"proj-1","name":"mohist/reviewer","description":"Mohist's built-in Workflow review Agent.","instructions":"Review the change.","agentConfig":{"runtime":"pi"},"skills":[],"status":"active","createdAt":"","updatedAt":"","permissions":[],"effectiveExecutionConfig":{"runtime":"pi"},"executability":{"state":"executable","gaps":[]},"origin":"built-in","overridesBuiltIn":false}`

func storedBuilderJSON(model string) string {
	return `{"id":"agent_shadow","projectId":"proj-1","name":"mohist/builder","description":"Overridden builder.","instructions":"Build the change.","agentConfig":{"runtime":"pi","model":"` + model + `"},"skills":[],"status":"active","createdAt":"2026-09-16T00:00:00Z","updatedAt":"2026-09-16T00:00:00Z","permissions":[],"effectiveExecutionConfig":{"runtime":"pi","model":"` + model + `"},"executability":{"state":"executable","gaps":[]},"origin":"project","overridesBuiltIn":true}`
}

func successEnvelope(data string) *http.Response {
	return response(http.StatusOK, `{"success":true,"data":`+data+`}`)
}

func createdEnvelope(data string) *http.Response {
	return response(http.StatusCreated, `{"success":true,"data":`+data+`}`)
}

func errorEnvelope(status int, message, code, details string) *http.Response {
	return response(status, `{"success":false,"error":"`+message+`","code":"`+code+`","details":`+details+`}`)
}

func decodeAgentBody(t *testing.T, r *http.Request) map[string]any {
	t.Helper()
	var body map[string]any
	if err := json.NewDecoder(r.Body).Decode(&body); err != nil {
		t.Fatalf("decode request body: %v", err)
	}
	return body
}

func TestAgentListShowsEffectiveAgentsWithOrigin(t *testing.T) {
	list := `[
		` + builtInBuilderJSON + `,
		{"id":"agent_planner","projectId":"proj-1","name":"mohist/planner","description":"Overriding planner.","instructions":"Plan.","agentConfig":{"runtime":"pi","model":"openai/gpt-5.6-luna"},"skills":[],"status":"active","createdAt":"2026-09-16T00:00:00Z","updatedAt":"2026-09-16T00:00:00Z","permissions":[],"effectiveExecutionConfig":{"runtime":"pi","model":"openai/gpt-5.6-luna"},"executability":{"state":"executable","gaps":[]},"origin":"project","overridesBuiltIn":true},
		{"id":"agent_explorer","projectId":"proj-1","name":"explorer","description":"Explore.","instructions":"Explore.","agentConfig":{"runtime":"pi"},"skills":[],"status":"active","createdAt":"2026-09-16T00:00:00Z","updatedAt":"2026-09-16T00:00:00Z","permissions":[],"effectiveExecutionConfig":{"runtime":"pi"},"executability":{"state":"executable","gaps":[]},"origin":"project","overridesBuiltIn":false}
	]`

	requests := 0
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		requests++
		if r.Method != http.MethodGet || r.URL.Path != "/api/projects/proj-1/agents" {
			t.Fatalf("unexpected request %s %s", r.Method, r.URL.Path)
		}
		return response(http.StatusOK, `{"success":true,"data":`+list+`}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"agent", "list", "--project", "proj-1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	expected := strings.Join([]string{
		"name  origin  runtime  model  status",
		"mohist/builder  built-in  pi  Runtime default  active",
		"mohist/planner  project (overrides built-in)  pi  openai/gpt-5.6-luna  active",
		"explorer  project  pi  Runtime default  active",
	}, "\n") + "\n"
	if out.String() != expected || errOut.Len() != 0 {
		t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
	}

	*out, *errOut = strings.Builder{}, strings.Builder{}
	if code := Run(context.Background(), []string{"agent", "list", "--project", "proj-1", "--json", "name,origin,overridesBuiltIn"}, deps); code != ExitOK {
		t.Fatalf("json code=%d stderr=%q", code, errOut.String())
	}
	var selected []map[string]any
	if err := json.Unmarshal([]byte(out.String()), &selected); err != nil {
		t.Fatalf("decode stdout %q: %v", out.String(), err)
	}
	if len(selected) != 3 ||
		selected[0]["origin"] != "built-in" || selected[0]["overridesBuiltIn"] != false ||
		selected[1]["origin"] != "project" || selected[1]["overridesBuiltIn"] != true {
		t.Fatalf("selected=%#v", selected)
	}
	if requests != 2 {
		t.Fatalf("requests=%d", requests)
	}
}

func TestAgentViewResolvesBuiltInByNameAndPresentsRuntimeDefault(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		if r.Method != http.MethodGet || r.URL.EscapedPath() != "/api/projects/proj-1/agents/by-name/mohist/reviewer" {
			t.Fatalf("unexpected request %s %s", r.Method, r.URL.EscapedPath())
		}
		return successEnvelope(builtInReviewerJSON), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"agent", "view", "mohist/reviewer", "--project", "proj-1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	expected := strings.Join([]string{
		"name: mohist/reviewer",
		"origin: built-in",
		"status: active",
		"runtime: pi",
		"model: Runtime default",
		"variant: -",
		"readiness: executable",
	}, "\n") + "\n"
	if out.String() != expected || errOut.Len() != 0 {
		t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
	}
	if strings.Contains(out.String(), "gpt") {
		t.Fatalf("view fabricated a model: %q", out.String())
	}

	*out, *errOut = strings.Builder{}, strings.Builder{}
	if code := Run(context.Background(), []string{"agent", "view", "mohist/reviewer", "--project", "proj-1", "--json", "origin,overridesBuiltIn,effectiveExecutionConfig"}, deps); code != ExitOK {
		t.Fatalf("json code=%d stderr=%q", code, errOut.String())
	}
	var selected map[string]any
	if err := json.Unmarshal([]byte(out.String()), &selected); err != nil {
		t.Fatalf("decode stdout %q: %v", out.String(), err)
	}
	if selected["origin"] != "built-in" || selected["overridesBuiltIn"] != false {
		t.Fatalf("selected=%#v", selected)
	}
	if effective, ok := selected["effectiveExecutionConfig"].(map[string]any); !ok || effective["runtime"] != "pi" || effective["model"] != nil {
		t.Fatalf("effective=%#v", selected["effectiveExecutionConfig"])
	}
}

func TestAgentEditBuiltInMaterializesOverrideWithOnlyCallerChanges(t *testing.T) {
	cases := []struct {
		name     string
		args     []string
		expected map[string]any
	}{
		{
			name: "execution flags map to agentConfig",
			args: []string{"--model", "openai/gpt-5.6-luna", "--reasoning-effort", "xhigh"},
			expected: map[string]any{
				"name":        "mohist/builder",
				"agentConfig": map[string]any{"model": "openai/gpt-5.6-luna", "reasoningEffort": "xhigh"},
			},
		},
		{
			name: "description and skills ride beside agentConfig",
			args: []string{"--description", "Custom builder", "--skills", "mohist,review", "--model", "openai/gpt-5.6-luna"},
			expected: map[string]any{
				"name":        "mohist/builder",
				"description": "Custom builder",
				"skills":      []any{"mohist", "review"},
				"agentConfig": map[string]any{"model": "openai/gpt-5.6-luna"},
			},
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			requests := 0
			deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
				requests++
				switch {
				case r.Method == http.MethodGet && r.URL.EscapedPath() == "/api/projects/proj-1/agents/by-name/mohist/builder":
					return successEnvelope(builtInBuilderJSON), nil
				case r.Method == http.MethodPost && r.URL.Path == "/api/projects/proj-1/agents/overrides":
					if body := decodeAgentBody(t, r); !reflect.DeepEqual(body, tc.expected) {
						t.Fatalf("body=%#v expected=%#v", body, tc.expected)
					}
					return createdEnvelope(storedBuilderJSON("openai/gpt-5.6-luna")), nil
				default:
					t.Fatalf("unexpected request %s %s", r.Method, r.URL.EscapedPath())
					return nil, nil
				}
			}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

			args := append([]string{"agent", "edit", "mohist/builder", "--project", "proj-1"}, tc.args...)
			if code := Run(context.Background(), args, deps); code != ExitOK {
				t.Fatalf("code=%d stderr=%q", code, errOut.String())
			}
			if requests != 2 {
				t.Fatalf("requests=%d", requests)
			}
			if !strings.Contains(errOut.String(), `Override created: built-in Agent "mohist/builder" is now overridden by Project Agent agent_shadow.`) {
				t.Fatalf("stderr=%q", errOut.String())
			}
			if !strings.Contains(out.String(), `"id":"agent_shadow"`) || !strings.Contains(out.String(), `"overridesBuiltIn":true`) {
				t.Fatalf("stdout=%q", out.String())
			}
		})
	}
}

func TestAgentEditBuiltInRejectsFlagsAnOverrideCannotCarry(t *testing.T) {
	cases := []struct {
		name string
		args []string
		want string
	}{
		{name: "clear flag", args: []string{"--clear-model"}, want: "--clear-model cannot be used when editing built-in Agent \"mohist/builder\""},
		{name: "purpose", args: []string{"--purpose", "Ops"}, want: "--purpose is not supported when editing built-in Agent \"mohist/builder\""},
		{name: "instructions", args: []string{"--instructions", "Rewrite"}, want: "--instructions is not supported when editing built-in Agent \"mohist/builder\""},
		{name: "max concurrent runs", args: []string{"--max-concurrent-runs", "2"}, want: "--max-concurrent-runs is not supported when editing built-in Agent \"mohist/builder\""},
		{name: "rename", args: []string{"--name", "other"}, want: "--name is not supported when editing built-in Agent \"mohist/builder\""},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			posts := 0
			deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
				if r.Method == http.MethodPost {
					posts++
					return nil, errors.New("override must not be posted")
				}
				if r.Method == http.MethodGet && r.URL.EscapedPath() == "/api/projects/proj-1/agents/by-name/mohist/builder" {
					return successEnvelope(builtInBuilderJSON), nil
				}
				t.Fatalf("unexpected request %s %s", r.Method, r.URL.EscapedPath())
				return nil, nil
			}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

			args := append([]string{"agent", "edit", "mohist/builder", "--project", "proj-1"}, tc.args...)
			if code := Run(context.Background(), args, deps); code != ExitUsage {
				t.Fatalf("code=%d stderr=%q", code, errOut.String())
			}
			if posts != 0 {
				t.Fatalf("posts=%d", posts)
			}
			if !strings.Contains(errOut.String(), tc.want) {
				t.Fatalf("stderr=%q want %q", errOut.String(), tc.want)
			}
		})
	}
}

func TestAgentEditOverrideConflictNamesExistingAgentAndRepair(t *testing.T) {
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		switch {
		case r.Method == http.MethodGet:
			return successEnvelope(builtInBuilderJSON), nil
		case r.Method == http.MethodPost:
			return errorEnvelope(http.StatusConflict,
				"An active Project Agent named 'mohist/builder' already overrides this built-in Agent; edit that Agent instead.",
				"agent_override_conflict",
				`{"name":"mohist/builder","agentId":"agent_9"}`), nil
		default:
			t.Fatalf("unexpected request %s %s", r.Method, r.URL.Path)
			return nil, nil
		}
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"agent", "edit", "mohist/builder", "--model", "openai/gpt-5.6-luna", "--project", "proj-1"}, deps); code != ExitOperation {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	stderr := errOut.String()
	for _, want := range []string{`Project Agent "mohist/builder" (agent_9)`, "already overrides the built-in Agent", "edit it instead with 'mo agent edit mohist/builder'", "[agent_override_conflict]"} {
		if !strings.Contains(stderr, want) {
			t.Fatalf("stderr=%q missing %q", stderr, want)
		}
	}
}

func TestAgentEditOverrideArchivedNamesArchivedAgent(t *testing.T) {
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		switch {
		case r.Method == http.MethodGet:
			return successEnvelope(builtInBuilderJSON), nil
		case r.Method == http.MethodPost:
			return errorEnvelope(http.StatusConflict,
				"An archived Project Agent named 'mohist/builder' shadows this built-in Agent; restore or rename it instead of creating an override.",
				"agent_override_archived",
				`{"name":"mohist/builder","agentId":"agent_archived"}`), nil
		default:
			t.Fatalf("unexpected request %s %s", r.Method, r.URL.Path)
			return nil, nil
		}
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"agent", "edit", "mohist/builder", "--model", "openai/gpt-5.6-luna", "--project", "proj-1"}, deps); code != ExitOperation {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	stderr := errOut.String()
	for _, want := range []string{"agent_archived", "is archived and shadows the built-in Agent", "restore or rename it", "[agent_override_archived]"} {
		if !strings.Contains(stderr, want) {
			t.Fatalf("stderr=%q missing %q", stderr, want)
		}
	}
}

func TestAgentEditStoredNameResolvesThroughByName(t *testing.T) {
	stored := `{"id":"agent_1","projectId":"proj-1","name":"reviewer","description":"Review.","instructions":"Review.","agentConfig":{"runtime":"pi","model":"old/model","reasoningEffort":"high"},"skills":[],"status":"active","createdAt":"2026-09-16T00:00:00Z","updatedAt":"2026-09-16T00:00:00Z","permissions":[],"effectiveExecutionConfig":{"runtime":"pi","model":"old/model"},"origin":"project","overridesBuiltIn":false}`

	requests := 0
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		requests++
		switch {
		case r.Method == http.MethodGet && r.URL.EscapedPath() == "/api/projects/proj-1/agents/by-name/reviewer":
			return successEnvelope(stored), nil
		case r.Method == http.MethodPatch && r.URL.Path == "/api/projects/proj-1/agents/agent_1":
			expected := map[string]any{"agentConfig": map[string]any{
				"runtime": "pi", "model": "new/model", "reasoningEffort": "high",
			}}
			if body := decodeAgentBody(t, r); !reflect.DeepEqual(body, expected) {
				t.Fatalf("body=%#v expected=%#v", body, expected)
			}
			return successEnvelope(`{"id":"agent_1","name":"reviewer"}`), nil
		default:
			t.Fatalf("unexpected request %s %s", r.Method, r.URL.EscapedPath())
			return nil, nil
		}
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"agent", "edit", "reviewer", "--model", "new/model", "--project", "proj-1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if requests != 2 || errOut.Len() != 0 {
		t.Fatalf("requests=%d stderr=%q", requests, errOut.String())
	}
}

func TestAgentStartSendsReasoningEffort(t *testing.T) {
	deps, _, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		if r.Method != http.MethodPost || r.URL.Path != "/api/projects/proj-1/agent-tasks" {
			t.Fatalf("unexpected request %s %s", r.Method, r.URL.Path)
		}
		body := decodeAgentBody(t, r)
		if body["prompt"] != "ship it" || body["reasoningEffort"] != "xhigh" {
			t.Fatalf("body=%#v", body)
		}
		if _, exists := body["runtime"]; exists {
			t.Fatalf("unset hint must stay unset: %#v", body)
		}
		return response(http.StatusAccepted, `{"success":true,"data":{"jobId":"job-1","sessionId":"sess-1"}}`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"agent", "start", "--prompt", "ship it", "--reasoning-effort", "xhigh", "--idempotency-key", "task-1", "--project", "proj-1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
}

func TestAgentStartRejectsNonCanonicalReasoningEffortBeforeRequest(t *testing.T) {
	for _, value := range []string{"none", "extreme", ""} {
		t.Run("value-"+value, func(t *testing.T) {
			requests := 0
			deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
				requests++
				return nil, errors.New("transport must not be used")
			}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

			args := []string{"agent", "start", "--prompt", "ship it", "--reasoning-effort", value, "--project", "proj-1"}
			if code := Run(context.Background(), args, deps); code != ExitUsage {
				t.Fatalf("code=%d stderr=%q", code, errOut.String())
			}
			if requests != 0 {
				t.Fatalf("requests=%d", requests)
			}
			if !strings.Contains(errOut.String(), "--reasoning-effort must be one of off, minimal, low, medium, high, xhigh, max") {
				t.Fatalf("stderr=%q", errOut.String())
			}
		})
	}
}

// TestAgentOverrideJourneyCustomizesBuilderThenViewResolvesOverride stands in
// for the user journey at the contract level: the override request materializes
// the Project Agent, and every later by-name read resolves it instead of the
// built-in.
func TestAgentOverrideJourneyCustomizesBuilderThenViewResolvesOverride(t *testing.T) {
	overridden := false
	requests := 0
	deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
		requests++
		switch {
		case r.Method == http.MethodGet && r.URL.EscapedPath() == "/api/projects/proj-1/agents/by-name/mohist/builder":
			if overridden {
				return successEnvelope(storedBuilderJSON("openai/gpt-5.6-luna")), nil
			}
			return successEnvelope(builtInBuilderJSON), nil
		case r.Method == http.MethodPost && r.URL.Path == "/api/projects/proj-1/agents/overrides":
			expected := map[string]any{
				"name":        "mohist/builder",
				"agentConfig": map[string]any{"model": "openai/gpt-5.6-luna"},
			}
			if body := decodeAgentBody(t, r); !reflect.DeepEqual(body, expected) {
				t.Fatalf("body=%#v expected=%#v", body, expected)
			}
			overridden = true
			return createdEnvelope(storedBuilderJSON("openai/gpt-5.6-luna")), nil
		default:
			t.Fatalf("unexpected request %s %s", r.Method, r.URL.EscapedPath())
			return nil, nil
		}
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"agent", "edit", "mohist/builder", "--model", "openai/gpt-5.6-luna", "--project", "proj-1"}, deps); code != ExitOK {
		t.Fatalf("edit code=%d stderr=%q", code, errOut.String())
	}
	if !strings.Contains(errOut.String(), "Override created") {
		t.Fatalf("stderr=%q", errOut.String())
	}

	*out, *errOut = strings.Builder{}, strings.Builder{}
	if code := Run(context.Background(), []string{"agent", "view", "mohist/builder", "--project", "proj-1"}, deps); code != ExitOK {
		t.Fatalf("view code=%d stderr=%q", code, errOut.String())
	}
	expectedView := strings.Join([]string{
		"name: mohist/builder",
		"origin: project (overrides built-in)",
		"status: active",
		"runtime: pi",
		"model: openai/gpt-5.6-luna",
		"variant: -",
		"readiness: executable",
	}, "\n") + "\n"
	if out.String() != expectedView || errOut.Len() != 0 {
		t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
	}

	*out, *errOut = strings.Builder{}, strings.Builder{}
	if code := Run(context.Background(), []string{"agent", "view", "mohist/builder", "--project", "proj-1", "--json", "name,origin,overridesBuiltIn,effectiveExecutionConfig"}, deps); code != ExitOK {
		t.Fatalf("json view code=%d stderr=%q", code, errOut.String())
	}
	var selected map[string]any
	if err := json.Unmarshal([]byte(out.String()), &selected); err != nil {
		t.Fatalf("decode stdout %q: %v", out.String(), err)
	}
	if selected["origin"] != "project" || selected["overridesBuiltIn"] != true {
		t.Fatalf("selected=%#v", selected)
	}
	if effective, ok := selected["effectiveExecutionConfig"].(map[string]any); !ok || effective["model"] != "openai/gpt-5.6-luna" {
		t.Fatalf("effective=%#v", selected["effectiveExecutionConfig"])
	}
	if requests != 4 {
		t.Fatalf("requests=%d", requests)
	}
}

func TestAgentListEmptyRendersNoAgents(t *testing.T) {
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return successEnvelope(`[]`), nil
	}), map[string]string{"MOHIST_OPERATOR_TOKEN": "token"})

	if code := Run(context.Background(), []string{"agent", "list", "--project", "proj-1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if out.String() != "No Agents\n" || errOut.Len() != 0 {
		t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
	}
}
