package mohistcli

import (
	"context"
	"errors"
	"io"
	"net/http"
	"strings"
	"testing"
)

// issue679Request captures one HTTP request observed by the stage-model
// contract harness so tests can assert method, route, and exact JSON body.
type issue679Request struct {
	method string
	path   string
	body   string
}

// newIssue679Harness wires a Dependencies that records every request and
// answers with one successful Issue envelope. The same response is returned
// to the issue-edit label pre-flight GET and to the final mutation so the
// label path can be exercised without a second transport.
func newIssue679Harness(files map[string]string, input string) (*[]issue679Request, Dependencies, *strings.Builder, *strings.Builder) {
	requests := &[]issue679Request{}
	out, errOut := &strings.Builder{}, &strings.Builder{}
	deps := Dependencies{
		HTTPClient: &http.Client{Transport: roundTripFunc(func(r *http.Request) (*http.Response, error) {
			body := ""
			if r.Body != nil {
				data, err := io.ReadAll(r.Body)
				if err != nil {
					return nil, err
				}
				body = string(data)
			}
			*requests = append(*requests, issue679Request{method: r.Method, path: r.URL.Path, body: body})
			return response(http.StatusOK, `{"success":true,"data":{"number":42,"labels":{}}}`), nil
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
			return "", errors.New("open " + path + ": no such file or directory")
		},
		HomeDir:          func() (string, error) { return "/home/test", nil },
		CurrentDirectory: func() string { return "/work/tree" },
		Input:            strings.NewReader(input),
	}
	return requests, deps, out, errOut
}

// captureIssueCreateBody runs one successful create and returns the exact
// request body.
func captureIssueCreateBody(t *testing.T, extraArgs []string, files map[string]string, input string) string {
	t.Helper()
	requests, deps, out, errOut := newIssue679Harness(files, input)
	args := append([]string{"issue", "create", "Title", "--body", "b", "--project", "proj"}, extraArgs...)
	if code := Run(context.Background(), args, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(*requests) != 1 || (*requests)[0].method != http.MethodPost {
		t.Fatalf("requests=%+v", *requests)
	}
	return (*requests)[0].body
}

// captureIssueEditBody runs one successful non-label edit and returns the
// exact PATCH body.
func captureIssueEditBody(t *testing.T, extraArgs []string, files map[string]string, input string) string {
	t.Helper()
	requests, deps, out, errOut := newIssue679Harness(files, input)
	args := append([]string{"issue", "edit", "42", "--project", "proj"}, extraArgs...)
	if code := Run(context.Background(), args, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(*requests) != 1 || (*requests)[0].method != http.MethodPatch {
		t.Fatalf("requests=%+v", *requests)
	}
	return (*requests)[0].body
}

// TestIssue679CreateSendsStageModelObjects proves the create request carries
// stage models and variants as JSON objects, never as the JSON strings the
// Server's Dictionary<string,string> DTOs would silently drop.
func TestIssue679CreateSendsStageModelObjects(t *testing.T) {
	requests, deps, out, errOut := newIssue679Harness(nil, "")
	code := Run(context.Background(), []string{
		"issue", "create", "Title", "--body", "b", "--project", "proj",
		"--stage-models", `{"plan":"m"}`,
		"--stage-model-variants", `{"plan":"v"}`,
	}, deps)
	if code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(*requests) != 1 || (*requests)[0].method != http.MethodPost || (*requests)[0].path != "/api/projects/proj/issues" {
		t.Fatalf("requests=%+v", *requests)
	}
	body := (*requests)[0].body
	for _, want := range []string{`"stageModels":{"plan":"m"}`, `"stageModelVariants":{"plan":"v"}`} {
		if !strings.Contains(body, want) {
			t.Fatalf("body=%s missing %s", body, want)
		}
	}
	if strings.Contains(body, `"stageModels":"`) || strings.Contains(body, `"stageModelVariants":"`) {
		t.Fatalf("stage models were forwarded as strings: %s", body)
	}
}

// TestIssue679EditSendsStageModelObjects proves the generic edit path emits
// the same object shape in its PATCH body.
func TestIssue679EditSendsStageModelObjects(t *testing.T) {
	requests, deps, out, errOut := newIssue679Harness(nil, "")
	code := Run(context.Background(), []string{
		"issue", "edit", "42", "--project", "proj",
		"--stage-models", `{"plan":"m"}`,
		"--stage-model-variants", `{"plan":"v"}`,
	}, deps)
	if code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(*requests) != 1 || (*requests)[0].method != http.MethodPatch || (*requests)[0].path != "/api/projects/proj/issues/42" {
		t.Fatalf("requests=%+v", *requests)
	}
	body := (*requests)[0].body
	for _, want := range []string{`"stageModels":{"plan":"m"}`, `"stageModelVariants":{"plan":"v"}`} {
		if !strings.Contains(body, want) {
			t.Fatalf("body=%s missing %s", body, want)
		}
	}
	if strings.Contains(body, `"stageModels":"`) || strings.Contains(body, `"stageModelVariants":"`) {
		t.Fatalf("stage models were forwarded as strings: %s", body)
	}
}

// TestIssue679EditWithLabelKeepsStageModelObjects proves the label pre-flight
// path does not accept a stage-model map and then drop it: the single PATCH
// still carries both objects alongside the merged labels.
func TestIssue679EditWithLabelKeepsStageModelObjects(t *testing.T) {
	requests, deps, out, errOut := newIssue679Harness(nil, "")
	code := Run(context.Background(), []string{
		"issue", "edit", "42", "--project", "proj", "--label", "plan=fast",
		"--stage-models", `{"plan":"m"}`,
		"--stage-model-variants", `{"plan":"v"}`,
	}, deps)
	if code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(*requests) != 2 {
		t.Fatalf("requests=%+v", *requests)
	}
	if (*requests)[0].method != http.MethodGet || (*requests)[1].method != http.MethodPatch {
		t.Fatalf("requests=%+v", *requests)
	}
	body := (*requests)[1].body
	for _, want := range []string{`"stageModels":{"plan":"m"}`, `"stageModelVariants":{"plan":"v"}`, `"plan":"fast"`} {
		if !strings.Contains(body, want) {
			t.Fatalf("PATCH body=%s missing %s", body, want)
		}
	}
	if strings.Contains(body, `"stageModels":"`) || strings.Contains(body, `"stageModelVariants":"`) {
		t.Fatalf("stage models were forwarded as strings: %s", body)
	}
}

// TestIssue679StageModelCarrierIdentity proves the inline value, a file path,
// and stdin produce byte-identical request bodies for both fields on both
// create and edit.
func TestIssue679StageModelCarrierIdentity(t *testing.T) {
	fields := []struct {
		name, key, inline, file, value string
	}{
		{name: "stage-models", key: "stageModels", inline: "stage-models", file: "stage-models-file", value: `{"plan":"m"}`},
		{name: "stage-model-variants", key: "stageModelVariants", inline: "stage-model-variants", file: "stage-model-variants-file", value: `{"plan":"v"}`},
	}
	files := map[string]string{"/tmp/model.json": ""}
	capture := map[string]func(*testing.T, []string, map[string]string, string) string{
		"create": captureIssueCreateBody,
		"edit":   captureIssueEditBody,
	}
	for action, run := range capture {
		for _, field := range fields {
			t.Run(action+"/"+field.name, func(t *testing.T) {
				files["/tmp/model.json"] = field.value
				inline := run(t, []string{"--" + field.inline, field.value}, nil, "")
				fromFile := run(t, []string{"--" + field.file, "/tmp/model.json"}, files, "")
				fromStdin := run(t, []string{"--" + field.file, "-"}, nil, field.value)
				if inline != fromFile || fromFile != fromStdin {
					t.Fatalf("carrier bodies differ:\n inline=%s\n file=%s\n stdin=%s", inline, fromFile, fromStdin)
				}
				if want := `"` + field.key + `":` + field.value; !strings.Contains(inline, want) {
					t.Fatalf("body=%s missing %s", inline, want)
				}
			})
		}
	}
}

// TestIssue679StageModelRejectionMatrix proves every malformed or non-string
// input fails locally with ExitUsage, zero HTTP, and a diagnostic naming the
// originating flag, for both fields, both carriers, and both actions.
func TestIssue679StageModelRejectionMatrix(t *testing.T) {
	values := []struct{ name, value string }{
		{name: "invalid-json", value: "not json"},
		{name: "top-level-null", value: "null"},
		{name: "top-level-array", value: "[]"},
		{name: "top-level-scalar-string", value: `"scalar"`},
		{name: "top-level-scalar-number", value: "123"},
		{name: "member-null", value: `{"plan":null}`},
		{name: "member-bool", value: `{"plan":true}`},
		{name: "member-number", value: `{"plan":1}`},
		{name: "member-array", value: `{"plan":[]}`},
		{name: "member-object", value: `{"plan":{}}`},
	}
	fields := []struct{ name, flag string }{
		{name: "stage-models", flag: "stage-models"},
		{name: "stage-model-variants", flag: "stage-model-variants"},
	}
	actions := []struct {
		name string
		base []string
	}{
		{name: "create", base: []string{"issue", "create", "Title", "--body", "b", "--project", "proj"}},
		{name: "edit", base: []string{"issue", "edit", "42", "--project", "proj"}},
	}
	for _, action := range actions {
		for _, field := range fields {
			for _, value := range values {
				t.Run(action.name+"/"+field.name+"/"+value.name, func(t *testing.T) {
					requests, deps, out, errOut := newIssue679Harness(nil, "")
					args := append(append([]string{}, action.base...), "--"+field.flag, value.value)
					if code := Run(context.Background(), args, deps); code != ExitUsage {
						t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
					}
					if len(*requests) != 0 {
						t.Fatalf("HTTP requests were issued: %+v", *requests)
					}
					if !strings.Contains(errOut.String(), "--"+field.flag) {
						t.Fatalf("stderr=%q does not name --%s", errOut.String(), field.flag)
					}
				})
			}
		}
	}
}

// TestIssue679StageModelFileDiagnosticNamesFileFlag proves the strict parse
// error names the -file carrier when that is the originating flag.
func TestIssue679StageModelFileDiagnosticNamesFileFlag(t *testing.T) {
	requests, deps, out, errOut := newIssue679Harness(map[string]string{"/tmp/bad.json": `{"plan":null}`}, "")
	code := Run(context.Background(), []string{
		"issue", "edit", "42", "--project", "proj", "--stage-models-file", "/tmp/bad.json",
	}, deps)
	if code != ExitUsage {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(*requests) != 0 {
		t.Fatalf("HTTP requests were issued: %+v", *requests)
	}
	if !strings.Contains(errOut.String(), "--stage-models-file") {
		t.Fatalf("stderr=%q does not name --stage-models-file", errOut.String())
	}
}

// TestIssue679StageModelMutualExclusion proves inline/file conflicts and
// multiple stdin carriers are local usage errors with zero HTTP.
func TestIssue679StageModelMutualExclusion(t *testing.T) {
	cases := []struct {
		name string
		args []string
	}{
		{name: "create-models-inline-and-file", args: []string{"issue", "create", "Title", "--body", "b", "--project", "proj", "--stage-models", "{}", "--stage-models-file", "/tmp/model.json"}},
		{name: "edit-models-inline-and-file", args: []string{"issue", "edit", "42", "--project", "proj", "--stage-models", "{}", "--stage-models-file", "/tmp/model.json"}},
		{name: "create-variants-inline-and-file", args: []string{"issue", "create", "Title", "--body", "b", "--project", "proj", "--stage-model-variants", "{}", "--stage-model-variants-file", "/tmp/model.json"}},
		{name: "edit-variants-inline-and-file", args: []string{"issue", "edit", "42", "--project", "proj", "--stage-model-variants", "{}", "--stage-model-variants-file", "/tmp/model.json"}},
		{name: "create-body-stdin-and-models-stdin", args: []string{"issue", "create", "Title", "--body-file", "-", "--project", "proj", "--stage-models-file", "-"}},
		{name: "create-body-stdin-and-variants-stdin", args: []string{"issue", "create", "Title", "--body-file", "-", "--project", "proj", "--stage-model-variants-file", "-"}},
		{name: "edit-models-stdin-and-variants-stdin", args: []string{"issue", "edit", "42", "--project", "proj", "--stage-models-file", "-", "--stage-model-variants-file", "-"}},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			requests, deps, out, errOut := newIssue679Harness(nil, "")
			if code := Run(context.Background(), tc.args, deps); code != ExitUsage {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			if len(*requests) != 0 {
				t.Fatalf("HTTP requests were issued: %+v", *requests)
			}
			if errOut.Len() == 0 {
				t.Fatalf("expected a usage diagnostic")
			}
		})
	}
}

// TestIssue679EmptyObjectPresenceVsAbsence proves the nil-able map tracks
// presence: an explicit "{}" is sent while an absent flag sends nothing.
func TestIssue679EmptyObjectPresenceVsAbsence(t *testing.T) {
	createPresent := captureIssueCreateBody(t, []string{"--stage-models", "{}", "--stage-model-variants", "{}"}, nil, "")
	for _, want := range []string{`"stageModels":{}`, `"stageModelVariants":{}`} {
		if !strings.Contains(createPresent, want) {
			t.Fatalf("create body=%s missing %s", createPresent, want)
		}
	}
	createAbsent := captureIssueCreateBody(t, nil, nil, "")
	if strings.Contains(createAbsent, "stageModels") || strings.Contains(createAbsent, "stageModelVariants") {
		t.Fatalf("create body=%s sent an absent stage map", createAbsent)
	}
	editPresent := captureIssueEditBody(t, []string{"--stage-models", "{}", "--stage-model-variants", "{}"}, nil, "")
	for _, want := range []string{`"stageModels":{}`, `"stageModelVariants":{}`} {
		if !strings.Contains(editPresent, want) {
			t.Fatalf("edit body=%s missing %s", editPresent, want)
		}
	}
	editAbsent := captureIssueEditBody(t, nil, nil, "")
	if strings.Contains(editAbsent, "stageModels") || strings.Contains(editAbsent, "stageModelVariants") {
		t.Fatalf("edit body=%s sent an absent stage map", editAbsent)
	}
}

// TestIssue679ResolveStageModelMapPresence pins the nil-able presence contract
// at the resolver boundary: absent is nil, an explicit empty object is a
// non-nil empty map.
func TestIssue679ResolveStageModelMapPresence(t *testing.T) {
	deps := Dependencies{
		ReadFile: func(string) (string, error) { return "", errors.New("unexpected read") },
		Input:    strings.NewReader(""),
	}
	if m, err := resolveStageModelMap(deps, command{}, "stage-models", "stage-models-file"); err != nil || m != nil {
		t.Fatalf("absent: map=%v err=%v", m, err)
	}
	if m, err := resolveStageModelMap(deps, command{args: []string{"stage-models", "{}"}}, "stage-models", "stage-models-file"); err != nil || m == nil || len(m) != 0 {
		t.Fatalf("empty object: map=%v err=%v", m, err)
	}
	if m, err := resolveStageModelMap(deps, command{args: []string{"stage-models", `{"plan":"m"}`}}, "stage-models", "stage-models-file"); err != nil || m["plan"] != "m" {
		t.Fatalf("populated: map=%v err=%v", m, err)
	}
}
