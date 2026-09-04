package mohistcli

import (
	"context"
	"errors"
	"io"
	"net/http"
	"os"
	"strings"
	"testing"
	"time"
)

type discoveryProbe struct {
	httpCalls       int
	lookupCalls     int
	readFileCalls   int
	writeFileCalls  int
	homeDirCalls    int
	executeCalls    int
	openBrowserCalls int
	inputReads      int
	nowCalls        int
	waitCalls       int
	executableCalls int
	currentDirCalls int
	eventTailCalls  int
	healthProbeCalls int
	mkdirAllCalls   int
	removeAllCalls  int
	renameCalls     int
	chmodCalls      int
}

func (p *discoveryProbe) deps(out, errOut *strings.Builder) Dependencies {
	return Dependencies{
		HTTPClient: &http.Client{Transport: roundTripFunc(func(*http.Request) (*http.Response, error) {
			p.httpCalls++
			return nil, errors.New("HTTP must not be used")
		})},
		Stdout: out,
		Stderr: errOut,
		Lookup: func(string) (string, bool) {
			p.lookupCalls++
			return "", false
		},
		ReadFile: func(string) (string, error) {
			p.readFileCalls++
			return "", errors.New("file access must not be used")
		},
		WriteFile: func(string, string, os.FileMode) error {
			p.writeFileCalls++
			return errors.New("file access must not be used")
		},
		HomeDir: func() (string, error) {
			p.homeDirCalls++
			return "", errors.New("home directory access must not be used")
		},
		Execute: func(context.Context, string, []string) error {
			p.executeCalls++
			return errors.New("process execution must not be used")
		},
		OpenBrowser: func(context.Context, string, []string) error {
			p.openBrowserCalls++
			return errors.New("browser execution must not be used")
		},
		Input: countingReader{reads: &p.inputReads},
		Now: func() time.Time {
			p.nowCalls++
			return time.Time{}
		},
		Wait: func(context.Context, time.Duration) error {
			p.waitCalls++
			return errors.New("waiting must not be used")
		},
		Executable: func() string {
			p.executableCalls++
			return "mo"
		},
		CurrentDirectory: func() string {
			p.currentDirCalls++
			return ""
		},
		EventTail: func(context.Context, string, []string, string, io.Writer) error {
			p.eventTailCalls++
			return errors.New("event stream must not be used")
		},
		HealthProbe: func(context.Context, string) error {
			p.healthProbeCalls++
			return errors.New("health probe must not be used")
		},
		MkdirAll: func(string, os.FileMode) error {
			p.mkdirAllCalls++
			return errors.New("file access must not be used")
		},
		RemoveAll: func(string) error {
			p.removeAllCalls++
			return errors.New("file access must not be used")
		},
		Rename: func(string, string) error {
			p.renameCalls++
			return errors.New("file access must not be used")
		},
		Chmod: func(string, os.FileMode) error {
			p.chmodCalls++
			return errors.New("file access must not be used")
		},
	}
}

type countingReader struct{ reads *int }

func (r countingReader) Read([]byte) (int, error) {
	*r.reads++
	return 0, io.EOF
}

func (p *discoveryProbe) assertUnused(t *testing.T) {
	t.Helper()
	if p.httpCalls != 0 || p.lookupCalls != 0 || p.readFileCalls != 0 || p.writeFileCalls != 0 ||
		p.homeDirCalls != 0 || p.executeCalls != 0 || p.openBrowserCalls != 0 || p.inputReads != 0 ||
		p.nowCalls != 0 || p.waitCalls != 0 || p.executableCalls != 0 || p.currentDirCalls != 0 ||
		p.eventTailCalls != 0 || p.healthProbeCalls != 0 || p.mkdirAllCalls != 0 || p.removeAllCalls != 0 ||
		p.renameCalls != 0 || p.chmodCalls != 0 {
		t.Fatalf("discovery used a forbidden dependency: %+v", *p)
	}
}

func TestIssue676CrossFamilyDiscoveryIsLocal(t *testing.T) {
	cases := []struct {
		name string
		args []string
		want []string
	}{
		{name: "project", args: []string{"project", "list", "--json"}, want: projectFields},
		{name: "repository", args: []string{"repo", "create", "--json"}, want: repoFields},
		{name: "workspace", args: []string{"workspace", "create", "--json"}, want: workspaceFields},
		{name: "issue without id", args: []string{"issue", "view", "--json"}, want: issueFields},
		{name: "epic without id", args: []string{"epic", "view", "--json"}, want: epicFields},
		{name: "label", args: []string{"label", "list", "--json"}, want: labelFields},
		{name: "workflow", args: []string{"workflow", "list", "--json"}, want: workflowListFields},
		{name: "run artifact", args: []string{"run", "artifact", "list", "--json"}, want: artifactFields},
		{name: "routing", args: []string{"routing", "rule", "view", "--json"}, want: routingRuleFields},
		{name: "webhook", args: []string{"webhook", "subscription", "view", "--json"}, want: webhookSubscriptionFields},
		{name: "event dead-letter", args: []string{"event", "dead-letter", "list", "--json"}, want: []string{"id", "type", "handler", "status", "attempts", "deadLetteredAt", "error"}},
		{name: "event tail", args: []string{"event", "tail", "--json"}, want: []string{"specversion", "id", "source", "type", "subject", "time", "data", "projectid", "issue", "parent", "githubrepo", "githubissue"}},
		{name: "otel", args: []string{"otel", "query", "--json"}, want: otelQueryFields},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			probe := discoveryProbe{}
			out, errOut := &strings.Builder{}, &strings.Builder{}
			if code := Run(context.Background(), tc.args, probe.deps(out, errOut)); code != ExitOK {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			if got := out.String(); got != strings.Join(tc.want, "\n")+"\n" || errOut.Len() != 0 {
				t.Fatalf("stdout=%q stderr=%q", got, errOut.String())
			}
			probe.assertUnused(t)
		})
	}
}

func TestIssue676HelpIsLocalAcrossMutationLeaves(t *testing.T) {
	leaves := []struct {
		name string
		args []string
	}{
		{name: "epic create", args: []string{"epic", "create"}},
		{name: "workspace create", args: []string{"workspace", "create"}},
		{name: "workflow delete", args: []string{"workflow", "delete"}},
		{name: "event redeliver", args: []string{"event", "dead-letter", "redeliver"}},
		{name: "otel query", args: []string{"otel", "query"}},
		{name: "label list", args: []string{"label", "list"}},
		{name: "routing rule view", args: []string{"routing", "rule", "view"}},
		{name: "webhook subscription view", args: []string{"webhook", "subscription", "view"}},
	}
	for _, leaf := range leaves {
		for _, token := range []string{"--help", "-h"} {
			t.Run(leaf.name+"/"+token, func(t *testing.T) {
				probe := discoveryProbe{}
				out, errOut := &strings.Builder{}, &strings.Builder{}
				args := append(append([]string{}, leaf.args...), token)
				if code := Run(context.Background(), args, probe.deps(out, errOut)); code != ExitOK {
					t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
				}
				if !strings.Contains(out.String(), "USAGE") || errOut.Len() != 0 {
					t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
				}
				probe.assertUnused(t)
			})
		}
	}
}

func TestIssue676UnsupportedDiscoveryIsLocalUsage(t *testing.T) {
	cases := [][]string{
		{"project", "use", "--json"},
		{"workflow", "validate", "--json", "--file", "workflow.yaml"},
		{"run", "watch", "--json"},
		{"webhook", "event-types", "--json"},
		{"server", "status", "--json"},
	}
	for _, args := range cases {
		t.Run(strings.Join(args, " "), func(t *testing.T) {
			probe := discoveryProbe{}
			out, errOut := &strings.Builder{}, &strings.Builder{}
			if code := Run(context.Background(), args, probe.deps(out, errOut)); code != ExitUsage || out.Len() != 0 || errOut.Len() == 0 {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			probe.assertUnused(t)
		})
	}
}

func TestIssue676SelectedJSONPreservesProjectRequestAndProjection(t *testing.T) {
	var got *http.Request
	out, errOut := &strings.Builder{}, &strings.Builder{}
	deps := Dependencies{
		HTTPClient: &http.Client{Transport: roundTripFunc(func(r *http.Request) (*http.Response, error) {
			got = r
			return response(http.StatusOK, `{"success":true,"data":[{"id":"proj-1","name":"Demo"}]}`), nil
		})},
		Stdout: out,
		Stderr: errOut,
		Lookup: func(name string) (string, bool) {
			if name == "MOHIST_TOKEN" {
				return "token", true
			}
			return "", false
		},
	}
	if code := Run(context.Background(), []string{"project", "list", "--json", "id,name"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if got == nil || got.Method != http.MethodGet || got.URL.Path != "/api/projects" || out.String() != `[{"id":"proj-1","name":"Demo"}]`+"\n" || errOut.Len() != 0 {
		t.Fatalf("request=%v stdout=%q stderr=%q", got, out.String(), errOut.String())
	}
}

func TestIssue676SelectedJSONPreservesOtelQuery(t *testing.T) {
	var got *http.Request
	var gotBody string
	out, errOut := &strings.Builder{}, &strings.Builder{}
	deps := Dependencies{
		HTTPClient: &http.Client{Transport: roundTripFunc(func(r *http.Request) (*http.Response, error) {
			got = r
			body, err := io.ReadAll(r.Body)
			if err != nil {
				return nil, err
			}
			gotBody = string(body)
			return response(http.StatusOK, `{"columns":["value"],"rows":[[1]],"truncated":false,"truncate_reason":""}`), nil
		})},
		Stdout: out,
		Stderr: errOut,
		Lookup: func(name string) (string, bool) {
			if name == "MOHIST_TOKEN" {
				return "token", true
			}
			return "", false
		},
	}
	if code := Run(context.Background(), []string{"otel", "query", "SELECT 1", "--json", "columns,rows"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if got == nil || got.Method != http.MethodPost || got.URL.Path != "/otel/api/query" || gotBody != `{"sql":"SELECT 1"}` || out.String() != `{"columns":["value"],"rows":[[1]]}`+"\n" || errOut.Len() != 0 {
		t.Fatalf("request=%v body=%q stdout=%q stderr=%q", got, gotBody, out.String(), errOut.String())
	}
}
