package mohistcli

import (
	"context"
	"io"
	"net/http"
	"os"
	"strings"
	"testing"
)

// issue680OpsLeaf is one remote Operations leaf with the minimal command
// prefix needed to reach the generic flag loop.
type issue680OpsLeaf struct {
	area   string
	action string
	base   []string
}

func issue680OpsLeaves() []issue680OpsLeaf {
	return []issue680OpsLeaf{
		{area: "runner", action: "list", base: []string{"runner", "list"}},
		{area: "runner", action: "view", base: []string{"runner", "view", "r1"}},
		{area: "runner", action: "status", base: []string{"runner", "status"}},
		{area: "runner", action: "revoke", base: []string{"runner", "revoke", "r1"}},
		{area: "server", action: "status", base: []string{"server", "status"}},
		{area: "server", action: "health", base: []string{"server", "health"}},
		{area: "server", action: "info", base: []string{"server", "info"}},
		{area: "server", action: "logs", base: []string{"server", "logs"}},
		{area: "audit", action: "list", base: []string{"audit", "list"}},
		{area: "github", action: "connect", base: []string{"github", "connect", "octocat/demo"}},
		{area: "github", action: "list", base: []string{"github", "list"}},
		{area: "github", action: "view", base: []string{"github", "view", "gh-1"}},
		{area: "github", action: "update", base: []string{"github", "update", "gh-1"}},
		{area: "github", action: "enable", base: []string{"github", "enable", "gh-1"}},
		{area: "github", action: "disable", base: []string{"github", "disable", "gh-1"}},
		{area: "slack", action: "setup", base: []string{"slack", "setup"}},
		{area: "slack", action: "status", base: []string{"slack", "status"}},
		{area: "slack", action: "install-agent", base: []string{"slack", "install-agent"}},
		{area: "slack", action: "create", base: []string{"slack", "create"}},
		{area: "slack", action: "list", base: []string{"slack", "list"}},
		{area: "slack", action: "view", base: []string{"slack", "view", "s1"}},
		{area: "slack", action: "diagnostics", base: []string{"slack", "diagnostics", "s1"}},
		{area: "slack", action: "claim-owner", base: []string{"slack", "claim-owner", "s1"}},
		{area: "slack", action: "edit", base: []string{"slack", "edit", "s1"}},
		{area: "slack", action: "transfer-owner", base: []string{"slack", "transfer-owner", "s1"}},
		{area: "slack", action: "enable", base: []string{"slack", "enable", "s1"}},
		{area: "slack", action: "disable", base: []string{"slack", "disable", "s1"}},
		{area: "slack", action: "remove-binding", base: []string{"slack", "remove-binding", "s1"}},
		{area: "slack", action: "permanent-delete", base: []string{"slack", "permanent-delete", "s1"}},
		{area: "slack", action: "deliveries", base: []string{"slack", "deliveries", "s1"}},
		{area: "slack", action: "resend-delivery", base: []string{"slack", "resend-delivery", "s1"}},
		{area: "slack", action: "clear-gap", base: []string{"slack", "clear-gap", "s1"}},
		{area: "slack", action: "reconcile-create", base: []string{"slack", "reconcile-create", "s1"}},
		{area: "slack", action: "reconcile-delete", base: []string{"slack", "reconcile-delete", "s1"}},
		{area: "slack", action: "message-send", base: []string{"slack", "message", "send"}},
	}
}

// issue680ValidArgs returns a command line that exercises at least one mapped
// flag for a leaf and satisfies its parser-level requirements.
func issue680ValidArgs(leaf issue680OpsLeaf) []string {
	additions := map[string][]string{
		"runner.list":            {"--project", "proj", "--scope", "global"},
		"runner.view":            {"--project", "proj"},
		"runner.status":          {"--project", "proj"},
		"runner.revoke":          {"--project", "proj"},
		"audit.list":             {"--kind", "k", "--since", "s", "--limit", "1"},
		"github.connect":         {"--project", "proj", "--approver", "alice"},
		"github.list":            {"--project", "proj"},
		"github.view":            {"--project", "proj"},
		"github.update":          {"--project", "proj", "--approver", "alice"},
		"github.enable":          {"--project", "proj"},
		"github.disable":         {"--project", "proj"},
		"slack.status":           {"--workspace-team", "T1"},
		"slack.install-agent":    {"--project", "proj"},
		"slack.create":           {"--project", "proj"},
		"slack.list":             {"--project", "proj"},
		"slack.view":             {"--project", "proj"},
		"slack.diagnostics":      {"--project", "proj"},
		"slack.claim-owner":      {"--project", "proj"},
		"slack.edit":             {"--project", "proj"},
		"slack.transfer-owner":   {"--project", "proj"},
		"slack.enable":           {"--project", "proj"},
		"slack.disable":          {"--project", "proj"},
		"slack.remove-binding":   {"--project", "proj"},
		"slack.permanent-delete": {"--project", "proj", "--yes"},
		"slack.deliveries":       {"--project", "proj"},
		"slack.resend-delivery":  {"--project", "proj"},
		"slack.clear-gap":        {"--project", "proj"},
		"slack.reconcile-create": {"--project", "proj"},
		"slack.reconcile-delete": {"--project", "proj"},
		"slack.message-send": {
			"--project", "proj", "--workspace", "W1", "--conversation", "C1",
			"--reply-to", "R1", "--connection", "K1", "--session", "S1",
			"--triggering-message", "M1", "--dispatch-ref", "D1", "--text", "hello",
		},
	}
	args := append([]string{}, leaf.base...)
	return append(args, additions[leaf.area+"."+leaf.action]...)
}

func TestIssue680OperationsLeavesParseDocumentedFlags(t *testing.T) {
	for _, leaf := range issue680OpsLeaves() {
		t.Run(leaf.area+"."+leaf.action, func(t *testing.T) {
			args := issue680ValidArgs(leaf)
			cmd, err := parse(args)
			if err != nil {
				t.Fatalf("parse(%v): %v", args, err)
			}
			wantKind := "ops-" + leaf.area + "-" + leaf.action
			if cmd.kind != wantKind {
				t.Fatalf("kind=%q, want %q", cmd.kind, wantKind)
			}
		})
	}
	cmd, err := parse([]string{"notification", "setup", "--health-base", "http://hermes"})
	if err != nil || cmd.kind != "ops-notification" {
		t.Fatalf("notification parse command=%+v err=%v", cmd, err)
	}
}

func TestIssue680OperationsLeavesRejectUnknownFlags(t *testing.T) {
	covered := map[string]bool{}
	for area, actions := range operationsFlags {
		for action := range actions {
			covered[area+"."+action] = true
		}
	}
	// notification.setup is a local leaf covered by
	// TestIssue680NotificationSetupRejectsUnknownFlags.
	delete(covered, "notification.setup")
	for _, leaf := range issue680OpsLeaves() {
		key := leaf.area + "." + leaf.action
		if !covered[key] {
			t.Fatalf("test leaf %s is not in operationsFlags", key)
		}
		delete(covered, key)
	}
	if len(covered) > 0 {
		t.Fatalf("operationsFlags leaves have no reject case: %v", covered)
	}

	for _, leaf := range issue680OpsLeaves() {
		t.Run(leaf.area+"."+leaf.action, func(t *testing.T) {
			type reject struct {
				name string
				args []string
			}
			wrongLeafBoolean := "--clear-approvers"
			if leaf.area == "github" && leaf.action == "update" {
				wrongLeafBoolean = "--yes"
			}
			if leaf.area == "runner" && leaf.action == "view" {
				wrongLeafBoolean = "--yes"
			}
			if leaf.area == "github" && leaf.action == "list" {
				wrongLeafBoolean = "--follow"
			}
			cases := []reject{
				{name: "unknown-bogus", args: append(append([]string{}, leaf.base...), "--bogus")},
				{name: "wrong-leaf-boolean", args: append(append([]string{}, leaf.base...), wrongLeafBoolean)},
			}
			if leaf.area == "github" && leaf.action == "connect" {
				cases = append(cases, reject{name: "no-op-repo", args: append(append([]string{}, leaf.base...), "--repo", "name")})
			}
			for _, tc := range cases {
				t.Run(tc.name, func(t *testing.T) {
					probe := discoveryProbe{}
					out, errOut := &strings.Builder{}, &strings.Builder{}
					code := Run(context.Background(), tc.args, probe.deps(out, errOut))
					flag := tc.args[len(tc.args)-1]
					if len(tc.args) >= 2 && tc.args[len(tc.args)-2] == "--repo" {
						flag = "--repo"
					}
					if code != ExitUsage || out.Len() != 0 {
						t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
					}
					if !strings.Contains(errOut.String(), "unknown option "+flag) {
						t.Fatalf("stderr=%q, want unknown option %s", errOut.String(), flag)
					}
					if !strings.Contains(errOut.String(), "USAGE") {
						t.Fatalf("stderr=%q, want leaf USAGE block", errOut.String())
					}
					probe.assertUnused(t)
				})
			}
		})
	}
}

func TestIssue680NotificationSetupRejectsUnknownFlags(t *testing.T) {
	cases := []struct {
		name string
		args []string
		flag string
	}{
		{name: "legacy-platform", args: []string{"notification", "setup", "--platform", "telegram"}, flag: "--platform"},
		{name: "unknown-bogus", args: []string{"notification", "setup", "--bogus"}, flag: "--bogus"},
		{name: "wrong-leaf-boolean", args: []string{"notification", "setup", "--yes"}, flag: "--yes"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			probe := discoveryProbe{}
			out, errOut := &strings.Builder{}, &strings.Builder{}
			if code := Run(context.Background(), tc.args, probe.deps(out, errOut)); code != ExitUsage || out.Len() != 0 {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			if !strings.Contains(errOut.String(), "unknown option "+tc.flag) {
				t.Fatalf("stderr=%q, want unknown option %s", errOut.String(), tc.flag)
			}
			if !strings.Contains(errOut.String(), "USAGE") {
				t.Fatalf("stderr=%q, want leaf USAGE block", errOut.String())
			}
			probe.assertUnused(t)
		})
	}
}

func TestIssue680OperationsLeavesAcceptDocumentedFlags(t *testing.T) {
	cases := []struct {
		name   string
		args   []string
		method string
		path   string
		query  map[string]string
		body   string
	}{
		{
			name:   "runner.list scope",
			args:   []string{"runner", "list", "--project", "proj", "--scope", "global"},
			method: http.MethodGet,
			path:   "/api/projects/proj/runners",
		},
		{
			name:   "audit.list flags",
			args:   []string{"audit", "list", "--kind", "workflow", "--since", "2020", "--limit", "5"},
			method: http.MethodGet,
			path:   "/api/audit/events",
			query:  map[string]string{"kind": "workflow", "since": "2020", "limit": "5"},
		},
		{
			name:   "github.connect approver",
			args:   []string{"github", "connect", "octocat/demo", "--project", "proj", "--approver", "alice"},
			method: http.MethodPost,
			path:   "/api/projects/proj/github-connections",
			body:   `{"approvers":["alice"],"owner":"octocat","repo":"demo"}`,
		},
		{
			name:   "github.update approvers",
			args:   []string{"github", "update", "gh-1", "--project", "proj", "--approver", "alice", "--approver", "bob"},
			method: http.MethodPatch,
			path:   "/api/projects/proj/github-connections/gh-1",
			body:   `{"approvers":["alice","bob"]}`,
		},
		{
			name:   "slack.status workspace",
			args:   []string{"slack", "status", "--workspace-team", "T1"},
			method: http.MethodGet,
			path:   "/api/slack-manager/status",
			query:  map[string]string{"workspaceTeamId": "T1"},
		},
		{
			name:   "slack.permanent-delete yes",
			args:   []string{"slack", "permanent-delete", "s1", "--project", "proj", "--yes"},
			method: http.MethodPost,
			path:   "/api/projects/proj/slack-connections/s1/permanent-delete",
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			var got *http.Request
			deps, out, errOut := testDeps(roundTripFunc(func(r *http.Request) (*http.Response, error) {
				got = r
				return response(http.StatusOK, `{"success":true,"data":[]}`), nil
			}), map[string]string{"MOHIST_TOKEN": "token"})
			if code := Run(context.Background(), tc.args, deps); code != ExitOK {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			if got == nil || got.Method != tc.method || got.URL.Path != tc.path {
				t.Fatalf("request=%v, want %s %s", got, tc.method, tc.path)
			}
			for key, want := range tc.query {
				if value := got.URL.Query().Get(key); value != want {
					t.Fatalf("query %s=%q, want %q", key, value, want)
				}
			}
			if tc.body != "" {
				data, err := io.ReadAll(got.Body)
				if err != nil || string(data) != tc.body {
					t.Fatalf("body=%q err=%v, want %q", data, err, tc.body)
				}
			}
		})
	}

	t.Run("notification.setup health-base", func(t *testing.T) {
		deps, out, errOut := testDeps(nil, map[string]string{})
		var written string
		deps.HomeDir = func() (string, error) { return "/home/test", nil }
		deps.ReadFile = func(string) (string, error) { return "{}", nil }
		deps.WriteFile = func(_ string, value string, _ os.FileMode) error { written = value; return nil }
		deps.HealthProbe = func(context.Context, string) error { return nil }
		if code := Run(context.Background(), []string{"notification", "setup", "--health-base", "http://hermes"}, deps); code != ExitOK {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		if !strings.Contains(written, "http://hermes/webhooks/mohist") || !strings.Contains(out.String(), "Wrote Mohist") {
			t.Fatalf("written=%q stdout=%q", written, out.String())
		}
	})
}

func TestIssue680DiscoveryDoesNotConsumeFollowingTokenForBooleanFlags(t *testing.T) {
	t.Run("json", func(t *testing.T) {
		probe := discoveryProbe{}
		out, errOut := &strings.Builder{}, &strings.Builder{}
		args := []string{"github", "update", "gh-1", "--project", "proj", "--clear-approvers", "--json"}
		if code := Run(context.Background(), args, probe.deps(out, errOut)); code != ExitOK {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		if out.String() != strings.Join(githubFields, "\n")+"\n" || errOut.Len() != 0 {
			t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
		}
		probe.assertUnused(t)
	})

	t.Run("help", func(t *testing.T) {
		probe := discoveryProbe{}
		out, errOut := &strings.Builder{}, &strings.Builder{}
		args := []string{"github", "update", "gh-1", "--project", "proj", "--clear-approvers", "--help"}
		if code := Run(context.Background(), args, probe.deps(out, errOut)); code != ExitOK {
			t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
		}
		if !strings.Contains(out.String(), "USAGE") || errOut.Len() != 0 {
			t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
		}
		probe.assertUnused(t)
	})
}

func TestIssue680GithubUpdateClearRefusesTrailingValue(t *testing.T) {
	probe := discoveryProbe{}
	out, errOut := &strings.Builder{}, &strings.Builder{}
	args := []string{"github", "update", "gh-1", "--project", "proj", "--clear-approvers", "alice"}
	if code := Run(context.Background(), args, probe.deps(out, errOut)); code != ExitUsage {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if out.Len() != 0 {
		t.Fatalf("stdout=%q, want empty", out.String())
	}
	if !strings.Contains(errOut.String(), "unexpected argument alice") {
		t.Fatalf("stderr=%q, want unexpected argument alice", errOut.String())
	}
	if strings.Contains(errOut.String(), "unknown option --clear-approvers") {
		t.Fatalf("stderr=%q must not frame --clear-approvers as unknown", errOut.String())
	}
	if !strings.Contains(errOut.String(), "USAGE") {
		t.Fatalf("stderr=%q, want leaf USAGE block", errOut.String())
	}
	probe.assertUnused(t)
}
