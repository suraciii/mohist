package mohistcli

import (
	"context"
	"encoding/json"
	"io"
	"net/http"
	"os"
	"strings"
	"testing"
)

// issue678Captured records the single HTTP request issued by an issue create
// or edit invocation so tests can assert the payload, path, and method.
type issue678Captured struct {
	httpCalls int
	request   *http.Request
	rawBody   []byte
	body      map[string]any
}

func runIssue678Command(t *testing.T, args []string, files map[string]string, stdin string) (int, string, string, *issue678Captured) {
	t.Helper()
	captured := &issue678Captured{}
	out, errOut := &strings.Builder{}, &strings.Builder{}
	deps := Dependencies{
		HTTPClient: &http.Client{Transport: roundTripFunc(func(r *http.Request) (*http.Response, error) {
			captured.httpCalls++
			captured.request = r
			raw, err := io.ReadAll(r.Body)
			if err != nil {
				return nil, err
			}
			captured.rawBody = raw
			if len(raw) > 0 {
				_ = json.Unmarshal(raw, &captured.body)
			}
			return response(http.StatusOK, `{"success":true,"data":{"number":1,"title":"Title"}}`), nil
		})},
		Stdout: out,
		Stderr: errOut,
		Lookup: func(name string) (string, bool) {
			switch name {
			case "MOHIST_TOKEN":
				return "token", true
			case "MOHIST_SERVER_URL":
				return "http://server", true
			}
			return "", false
		},
		ReadFile: func(path string) (string, error) {
			if value, ok := files[path]; ok {
				return value, nil
			}
			return "", os.ErrNotExist
		},
		HomeDir:           func() (string, error) { return "/home/test", nil },
		CurrentDirectory:  func() string { return "/work/tree" },
		Input:             &stdinCountingReader{reader: strings.NewReader(stdin)},
		OpenManagedLock:   func(string) (io.Closer, error) { return io.NopCloser(strings.NewReader("")), nil },
		ManagedPathExists: func(string) bool { return false },
	}
	code := Run(context.Background(), args, deps)
	return code, out.String(), errOut.String(), captured
}

const issue678Envelope = "---\n" +
	"recommended_workflow: feature-flow\n" +
	"recommended_workflow_reason: Matches UI and feature scope\n" +
	"risk: high\n" +
	"---\n" +
	"## Background\n" +
	"Real body content.\n"

func TestIssue678LegalEnvelopeStripsBodyAndFillsMetadata(t *testing.T) {
	files := map[string]string{"./body.md": issue678Envelope}
	code, _, errOut, captured := runIssue678Command(t,
		[]string{"issue", "create", "Title", "--body-file", "./body.md", "--project", "proj"}, files, "")

	if code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut)
	}
	if errOut != "" {
		t.Fatalf("stderr=%q want empty", errOut)
	}
	if captured.request == nil || captured.request.Method != http.MethodPost || captured.request.URL.Path != "/api/projects/proj/issues" {
		t.Fatalf("request=%v", captured.request)
	}
	if got := captured.body["body"]; got != "## Background\nReal body content.\n" {
		t.Fatalf("body=%q", got)
	}
	if got := captured.body["workflowProfileId"]; got != "feature-flow" {
		t.Fatalf("workflowProfileId=%v", got)
	}
	if got := captured.body["risk"]; got != "high" {
		t.Fatalf("risk=%v", got)
	}
	if _, ok := captured.body["noWorkflow"]; ok {
		t.Fatalf("unexpected noWorkflow: %v", captured.body["noWorkflow"])
	}
}

func TestIssue678ExplicitFlagsOverrideFrontmatterAndNoteIt(t *testing.T) {
	files := map[string]string{"./body.md": issue678Envelope}
	code, _, errOut, captured := runIssue678Command(t,
		[]string{"issue", "create", "Title", "--body-file", "./body.md", "--project", "proj", "--workflow-profile", "other-flow", "--risk", "medium"}, files, "")

	if code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut)
	}
	want := "note: --workflow-profile 'other-flow' overrides frontmatter recommended_workflow 'feature-flow'\n" +
		"note: --risk 'medium' overrides frontmatter risk 'high'\n"
	if errOut != want {
		t.Fatalf("stderr=%q want %q", errOut, want)
	}
	if got := captured.body["workflowProfileId"]; got != "other-flow" {
		t.Fatalf("workflowProfileId=%v", got)
	}
	if got := captured.body["risk"]; got != "medium" {
		t.Fatalf("risk=%v", got)
	}
}

func TestIssue678EqualOverridesEmitNoNote(t *testing.T) {
	files := map[string]string{"./body.md": issue678Envelope}
	code, _, errOut, captured := runIssue678Command(t,
		[]string{"issue", "create", "Title", "--body-file", "./body.md", "--project", "proj", "--workflow-profile", "feature-flow", "--risk", "high"}, files, "")

	if code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut)
	}
	if errOut != "" {
		t.Fatalf("stderr=%q want empty", errOut)
	}
	if got := captured.body["workflowProfileId"]; got != "feature-flow" {
		t.Fatalf("workflowProfileId=%v", got)
	}
	if got := captured.body["risk"]; got != "high" {
		t.Fatalf("risk=%v", got)
	}
}

func TestIssue678NoWorkflowOverridesRecommendation(t *testing.T) {
	files := map[string]string{"./body.md": issue678Envelope}
	code, _, errOut, captured := runIssue678Command(t,
		[]string{"issue", "create", "Title", "--body-file", "./body.md", "--project", "proj", "--no-workflow"}, files, "")

	if code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut)
	}
	want := "note: --no-workflow overrides frontmatter recommended_workflow 'feature-flow'\n"
	if errOut != want {
		t.Fatalf("stderr=%q want %q", errOut, want)
	}
	if got := captured.body["noWorkflow"]; got != true {
		t.Fatalf("noWorkflow=%v", got)
	}
	if _, ok := captured.body["workflowProfileId"]; ok {
		t.Fatalf("workflowProfileId should be omitted: %v", captured.body["workflowProfileId"])
	}
	if got := captured.body["risk"]; got != "high" {
		t.Fatalf("risk=%v", got)
	}
}

func TestIssue678MalformedEnvelopeWarnsAndSendsFullBody(t *testing.T) {
	malformed := "---\n" +
		"recommended_workflow: feature-flow\n" +
		"no closing delimiter here\n"
	files := map[string]string{"./body.md": malformed}
	code, _, errOut, captured := runIssue678Command(t,
		[]string{"issue", "create", "Title", "--body-file", "./body.md", "--project", "proj", "--risk", "low", "--workflow-profile", "explicit-flow"}, files, "")

	if code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut)
	}
	want := "warning: malformed YAML frontmatter in './body.md'; sending full body text without parsing metadata\n"
	if errOut != want {
		t.Fatalf("stderr=%q want %q", errOut, want)
	}
	if got := captured.body["body"]; got != malformed {
		t.Fatalf("body=%q want full malformed text", got)
	}
	if got := captured.body["risk"]; got != "low" {
		t.Fatalf("risk=%v", got)
	}
	if got := captured.body["workflowProfileId"]; got != "explicit-flow" {
		t.Fatalf("workflowProfileId=%v", got)
	}
}

func TestIssue678ColonlessEnvelopeWarnsAndSendsFullBody(t *testing.T) {
	colonless := "---\n" +
		"recommended_workflow: feature-flow\n" +
		"this line has no colon\n" +
		"---\n" +
		"Body.\n"
	files := map[string]string{"./body.md": colonless}
	code, _, errOut, captured := runIssue678Command(t,
		[]string{"issue", "create", "Title", "--body-file", "./body.md", "--project", "proj"}, files, "")

	if code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut)
	}
	if !strings.Contains(errOut, "warning: malformed YAML frontmatter") {
		t.Fatalf("stderr=%q", errOut)
	}
	if got := captured.body["body"]; got != colonless {
		t.Fatalf("body=%q", got)
	}
	if got := captured.body["workflowProfileId"]; got != nil {
		t.Fatalf("workflowProfileId should be absent, got %v", got)
	}
	if got := captured.body["risk"]; got != "" {
		t.Fatalf("risk=%v want empty", got)
	}
}

func TestIssue678InlineMalformedEnvelopeWarnsWithoutFileClause(t *testing.T) {
	malformed := "---\n" +
		"recommended_workflow: feature-flow\n"
	code, _, errOut, captured := runIssue678Command(t,
		[]string{"issue", "create", "Title", "--body", malformed, "--project", "proj"}, nil, "")

	if code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut)
	}
	want := "warning: malformed YAML frontmatter; sending full body text without parsing metadata\n"
	if errOut != want {
		t.Fatalf("stderr=%q want %q", errOut, want)
	}
	if got := captured.body["body"]; got != malformed {
		t.Fatalf("body=%q", got)
	}
}

func TestIssue678PlainBodyIsByteExactAndSilent(t *testing.T) {
	plain := "## Just a body\nno frontmatter at all\n"
	code, _, errOut, captured := runIssue678Command(t,
		[]string{"issue", "create", "Title", "--body", plain, "--project", "proj"}, nil, "")

	if code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut)
	}
	if errOut != "" {
		t.Fatalf("stderr=%q want empty", errOut)
	}
	if got := captured.body["body"]; got != plain {
		t.Fatalf("body=%q want %q", got, plain)
	}
	if got := captured.body["risk"]; got != "" {
		t.Fatalf("risk=%v want empty", got)
	}
	if _, ok := captured.body["workflowProfileId"]; ok {
		t.Fatalf("workflowProfileId should be absent: %v", captured.body["workflowProfileId"])
	}
}

func TestIssue678CarriersProduceIdenticalRequestBodies(t *testing.T) {
	cases := []struct {
		name  string
		body  string
		extra []string
	}{
		{name: "envelope", body: issue678Envelope, extra: []string{"--workflow-profile", "feature-flow", "--risk", "high"}},
		{name: "plain", body: "## Just a body\nno frontmatter at all\n"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			files := map[string]string{"./body.md": tc.body}
			base := []string{"issue", "create", "Title", "--project", "proj"}
			inlineArgs := append(append(append([]string{}, base...), "--body", tc.body), tc.extra...)
			fileArgs := append(append(append([]string{}, base...), "--body-file", "./body.md"), tc.extra...)
			stdinArgs := append(append(append([]string{}, base...), "--body-file", "-"), tc.extra...)

			_, _, inlineErr, inline := runIssue678Command(t, inlineArgs, nil, "")
			_, _, fileErr, file := runIssue678Command(t, fileArgs, files, "")
			_, _, stdinErr, stdin := runIssue678Command(t, stdinArgs, nil, tc.body)

			if inlineErr != "" || fileErr != "" || stdinErr != "" {
				t.Fatalf("unexpected stderr: inline=%q file=%q stdin=%q", inlineErr, fileErr, stdinErr)
			}
			if string(inline.rawBody) != string(file.rawBody) || string(file.rawBody) != string(stdin.rawBody) {
				t.Fatalf("inline=%s file=%s stdin=%s", inline.rawBody, file.rawBody, stdin.rawBody)
			}
		})
	}
}

func TestIssue678CreateRejectsInheritWorkflowProfileWithoutHTTP(t *testing.T) {
	code, out, errOut, captured := runIssue678Command(t,
		[]string{"issue", "create", "Title", "--body", "Body", "--project", "proj", "--inherit-workflow-profile"}, nil, "")

	if code != ExitUsage {
		t.Fatalf("code=%d want ExitUsage stdout=%q stderr=%q", code, out, errOut)
	}
	if captured.httpCalls != 0 {
		t.Fatalf("HTTP calls=%d want 0", captured.httpCalls)
	}
	if !strings.Contains(errOut, "--inherit-workflow-profile is not supported for issue create") {
		t.Fatalf("stderr=%q", errOut)
	}
}

func TestIssue678CreateRejectsWorkflowProfileWithNoWorkflow(t *testing.T) {
	code, out, errOut, captured := runIssue678Command(t,
		[]string{"issue", "create", "Title", "--body", "Body", "--project", "proj", "--workflow-profile", "feature-flow", "--no-workflow"}, nil, "")

	if code != ExitUsage {
		t.Fatalf("code=%d want ExitUsage stdout=%q stderr=%q", code, out, errOut)
	}
	if captured.httpCalls != 0 {
		t.Fatalf("HTTP calls=%d want 0", captured.httpCalls)
	}
	if !strings.Contains(errOut, "--workflow-profile and --no-workflow are mutually exclusive") {
		t.Fatalf("stderr=%q", errOut)
	}
}

func TestIssue678IssueEditStillAcceptsInheritWorkflowProfile(t *testing.T) {
	code, _, errOut, captured := runIssue678Command(t,
		[]string{"issue", "edit", "5", "--project", "proj", "--inherit-workflow-profile"}, nil, "")

	if code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut)
	}
	if captured.request == nil || captured.request.Method != http.MethodPatch || captured.request.URL.Path != "/api/projects/proj/issues/5" {
		t.Fatalf("request=%v", captured.request)
	}
	if value, ok := captured.body["workflowProfileId"]; !ok || value != nil {
		t.Fatalf("workflowProfileId present=%v value=%v", ok, value)
	}
	if got := captured.body["noWorkflow"]; got != false {
		t.Fatalf("noWorkflow=%v", got)
	}
}
