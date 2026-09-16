package mohistcli

import (
	"context"
	"encoding/json"
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
	editAbsent := captureIssueEditBody(t, []string{"--ready"}, nil, "")
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

// newIssue679LabelHarness answers the issue-edit label pre-read GET with the
// supplied current labels and the mutation with a success envelope. Every
// request is recorded so tests can assert the one-GET/one-PATCH counter.
func newIssue679LabelHarness(currentLabels string) (*[]issue679Request, Dependencies, *strings.Builder, *strings.Builder) {
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
			if r.Method == http.MethodGet {
				return response(http.StatusOK, `{"success":true,"data":{"number":42,"labels":`+currentLabels+`}}`), nil
			}
			return response(http.StatusOK, `{"success":true,"data":{"number":42}}`), nil
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
			return "", errors.New("open " + path + ": no such file or directory")
		},
		HomeDir:          func() (string, error) { return "/home/test", nil },
		CurrentDirectory: func() string { return "/work/tree" },
		Input:            strings.NewReader(""),
	}
	return requests, deps, out, errOut
}

func issue679JSON(t *testing.T, raw string) map[string]any {
	t.Helper()
	var body map[string]any
	if err := json.Unmarshal([]byte(raw), &body); err != nil {
		t.Fatalf("body=%s is not JSON: %v", raw, err)
	}
	return body
}

// TestIssue679EditWithLabelsSendsOneAtomicPatch drives the full edit surface
// through the label path and proves the pre-read merges into the same request:
// exactly one GET and one PATCH, no other mutation, untouched current labels
// survive, -key removes a label, and every explicit flag lands in the body.
func TestIssue679EditWithLabelsSendsOneAtomicPatch(t *testing.T) {
	requests, deps, out, errOut := newIssue679LabelHarness(`{"keep":"yes","drop":"yes"}`)
	code := Run(context.Background(), []string{
		"issue", "edit", "42", "--project", "proj",
		"--label", "plan=fast", "--label", "-drop",
		"--workflow-profile", "wp", "--repo", "r",
		"--model", "m", "--model-variant", "v",
		"--parent", "7", "--risk", "high", "--priority", "p1", "--ready",
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
	for _, request := range *requests {
		if request.path != "/api/projects/proj/issues/42" {
			t.Fatalf("unexpected path %s", request.path)
		}
	}
	body := issue679JSON(t, (*requests)[1].body)
	labels, _ := body["labels"].(map[string]any)
	if labels["plan"] != "fast" || labels["keep"] != "yes" {
		t.Fatalf("labels=%v", body["labels"])
	}
	if _, removed := labels["drop"]; removed {
		t.Fatalf("removed label survived: %v", labels)
	}
	checks := []struct {
		key  string
		want any
	}{
		{"workflowProfileId", "wp"},
		{"repositoryName", "r"},
		{"model", "m"},
		{"modelVariant", "v"},
		{"parentIssueNumber", float64(7)},
		{"risk", "high"},
		{"priority", "p1"},
		{"isDraft", false},
	}
	for _, check := range checks {
		if body[check.key] != check.want {
			t.Fatalf("body[%q]=%v want %v (body=%s)", check.key, body[check.key], check.want, (*requests)[1].body)
		}
	}
	models, _ := body["stageModels"].(map[string]any)
	if models["plan"] != "m" {
		t.Fatalf("stageModels=%v", body["stageModels"])
	}
	variants, _ := body["stageModelVariants"].(map[string]any)
	if variants["plan"] != "v" {
		t.Fatalf("stageModelVariants=%v", body["stageModelVariants"])
	}
}

// TestIssue679NonLabelEditUsesSameBuilder proves a non-label edit also emits
// one PATCH from the shared builder, and that --inherit-workflow-profile still
// sends workflowProfileId:null with noWorkflow:false.
func TestIssue679NonLabelEditUsesSameBuilder(t *testing.T) {
	requests, deps, out, errOut := newIssue679Harness(nil, "")
	code := Run(context.Background(), []string{
		"issue", "edit", "42", "--project", "proj", "--title", "T", "--inherit-workflow-profile",
	}, deps)
	if code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(*requests) != 1 || (*requests)[0].method != http.MethodPatch {
		t.Fatalf("requests=%+v", *requests)
	}
	body := issue679JSON(t, (*requests)[0].body)
	if body["title"] != "T" {
		t.Fatalf("title=%v", body["title"])
	}
	if value, ok := body["workflowProfileId"]; !ok || value != nil {
		t.Fatalf("workflowProfileId=%v present=%v", value, ok)
	}
	if value, ok := body["noWorkflow"]; !ok || value != false {
		t.Fatalf("noWorkflow=%v present=%v", value, ok)
	}
	if _, ok := body["labels"]; ok {
		t.Fatalf("non-label edit sent labels: %s", (*requests)[0].body)
	}
}

// TestIssue679CreateLabelObject proves create sends labels as a JSON object and
// rejects every malformed set token locally with zero HTTP requests.
func TestIssue679CreateLabelObject(t *testing.T) {
	requests, deps, out, errOut := newIssue679Harness(nil, "")
	code := Run(context.Background(), []string{
		"issue", "create", "Title", "--body", "b", "--project", "proj", "--label", "plan=fast",
	}, deps)
	if code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(*requests) != 1 || (*requests)[0].method != http.MethodPost {
		t.Fatalf("requests=%+v", *requests)
	}
	body := issue679JSON(t, (*requests)[0].body)
	labels, ok := body["labels"].(map[string]any)
	if !ok || labels["plan"] != "fast" {
		t.Fatalf("labels=%v", body["labels"])
	}
}

// TestIssue679LabelTokenRejectionMatrix proves every malformed set token fails
// locally with ExitUsage and zero HTTP requests on both create and edit.
func TestIssue679LabelTokenRejectionMatrix(t *testing.T) {
	tokens := []string{"plan", "=fast", "Plan=fast", "plan=", "plan= "}
	actions := []struct {
		name string
		base []string
	}{
		{name: "create", base: []string{"issue", "create", "Title", "--body", "b", "--project", "proj"}},
		{name: "edit", base: []string{"issue", "edit", "42", "--project", "proj"}},
	}
	for _, action := range actions {
		for _, token := range tokens {
			t.Run(action.name+"/"+token, func(t *testing.T) {
				requests, deps, out, errOut := newIssue679Harness(nil, "")
				args := append(append([]string{}, action.base...), "--label", token)
				if code := Run(context.Background(), args, deps); code != ExitUsage {
					t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
				}
				if len(*requests) != 0 {
					t.Fatalf("HTTP requests were issued: %+v", *requests)
				}
			})
		}
	}
}

// TestIssue679RemoveTokenCreateRejectedEditAccepted pins the one grammar
// asymmetry: -key is create-invalid but edit-valid, and the edit removes only
// the named label.
func TestIssue679RemoveTokenCreateRejectedEditAccepted(t *testing.T) {
	createRequests, createDeps, createOut, createErr := newIssue679Harness(nil, "")
	if code := Run(context.Background(), []string{"issue", "create", "Title", "--body", "b", "--project", "proj", "--label", "-plan"}, createDeps); code != ExitUsage || len(*createRequests) != 0 {
		t.Fatalf("create code=%d requests=%+v stdout=%q stderr=%q", code, *createRequests, createOut.String(), createErr.String())
	}
	editRequests, editDeps, editOut, editErr := newIssue679LabelHarness(`{"plan":"old","keep":"yes"}`)
	if code := Run(context.Background(), []string{"issue", "edit", "42", "--project", "proj", "--label", "-plan"}, editDeps); code != ExitOK {
		t.Fatalf("edit code=%d stdout=%q stderr=%q", code, editOut.String(), editErr.String())
	}
	if len(*editRequests) != 2 || (*editRequests)[0].method != http.MethodGet || (*editRequests)[1].method != http.MethodPatch {
		t.Fatalf("edit requests=%+v", *editRequests)
	}
	body := issue679JSON(t, (*editRequests)[1].body)
	labels, _ := body["labels"].(map[string]any)
	if _, removed := labels["plan"]; removed || labels["keep"] != "yes" {
		t.Fatalf("labels=%v", body["labels"])
	}
}

// TestIssue679LabelValidationRunsAfterCarrierResolution proves an unreadable
// --body-file wins over a malformed label token so the carrier diagnostic is
// reported first with zero HTTP requests.
func TestIssue679LabelValidationRunsAfterCarrierResolution(t *testing.T) {
	requests, deps, out, errOut := newIssue679Harness(nil, "")
	code := Run(context.Background(), []string{
		"issue", "edit", "42", "--project", "proj",
		"--body-file", "./missing.md", "--label", "plan",
	}, deps)
	if code != ExitUsage {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(*requests) != 0 {
		t.Fatalf("HTTP requests were issued: %+v", *requests)
	}
	if !strings.Contains(errOut.String(), "could not read --body-file") {
		t.Fatalf("stderr=%q does not report the carrier error", errOut.String())
	}
	if strings.Contains(errOut.String(), "label") {
		t.Fatalf("stderr=%q reported the label error instead of the carrier error", errOut.String())
	}
}

// TestIssue679EmptyEditMatrix proves an edit with no editable field is rejected
// locally with ExitUsage and no config, file, or HTTP access, while --project
// and a --json selection do not count as editable fields.
func TestIssue679EmptyEditMatrix(t *testing.T) {
	cases := [][]string{
		{"issue", "edit", "42"},
		{"issue", "edit", "42", "--project", "proj"},
		{"issue", "edit", "42", "--project", "proj", "--json", "number"},
	}
	for _, args := range cases {
		t.Run(strings.Join(args, " "), func(t *testing.T) {
			probe := discoveryProbe{}
			out, errOut := &strings.Builder{}, &strings.Builder{}
			if code := Run(context.Background(), args, probe.deps(out, errOut)); code != ExitUsage {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			probe.assertUnused(t)
		})
	}
}

// TestIssue679SingleEditableFlagProceeds proves every single editable flag
// still parses and reaches the request path, so the empty-edit guard does not
// over-reject.
func TestIssue679SingleEditableFlagProceeds(t *testing.T) {
	files := map[string]string{"./body.md": "body", "./models.json": `{"plan":"m"}`, "./variants.json": `{"plan":"v"}`}
	cases := [][]string{
		{"--title", "T"},
		{"--body", "b"},
		{"--body-file", "./body.md"},
		{"--priority", "p1"},
		{"--risk", "high"},
		{"--model", "m"},
		{"--model-variant", "v"},
		{"--repo", "r"},
		{"--parent", "7"},
		{"--ready"},
		{"--draft"},
		{"--no-workflow"},
		{"--workflow-profile", "wp"},
		{"--inherit-workflow-profile"},
		{"--label", "plan=fast"},
		{"--stage-models", `{"plan":"m"}`},
		{"--stage-models-file", "./models.json"},
		{"--stage-model-variants", `{"plan":"v"}`},
		{"--stage-model-variants-file", "./variants.json"},
	}
	for _, extra := range cases {
		t.Run(strings.Join(extra, " "), func(t *testing.T) {
			requests, deps, out, errOut := newIssue679Harness(files, "")
			args := append([]string{"issue", "edit", "42", "--project", "proj"}, extra...)
			if code := Run(context.Background(), args, deps); code != ExitOK {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			if len(*requests) == 0 {
				t.Fatalf("no request was issued")
			}
		})
	}
}

// TestIssue679EditDiscoveryIsLocal is the #676 guard: bare --json and --help on
// an edit return locally without any config, file, or HTTP access even though
// the edit carries no editable field.
func TestIssue679EditDiscoveryIsLocal(t *testing.T) {
	cases := []struct {
		name string
		args []string
		want string
	}{
		{name: "bare-json", args: []string{"issue", "edit", "42", "--json"}, want: "number"},
		{name: "help", args: []string{"issue", "edit", "42", "--help"}, want: "USAGE"},
		{name: "short-help", args: []string{"issue", "edit", "42", "-h"}, want: "USAGE"},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			probe := discoveryProbe{}
			out, errOut := &strings.Builder{}, &strings.Builder{}
			if code := Run(context.Background(), tc.args, probe.deps(out, errOut)); code != ExitOK {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			if !strings.Contains(out.String(), tc.want) || errOut.Len() != 0 {
				t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
			}
			probe.assertUnused(t)
		})
	}
}

// TestIssue679StageFlagRejectedOffList proves --stage stays a list filter and
// cannot be accepted-and-ignored by create or edit.
func TestIssue679StageFlagRejectedOffList(t *testing.T) {
	cases := [][]string{
		{"issue", "edit", "42", "--project", "proj", "--stage", "verify"},
		{"issue", "create", "Title", "--body", "b", "--project", "proj", "--stage", "verify"},
	}
	for _, args := range cases {
		t.Run(strings.Join(args, " "), func(t *testing.T) {
			probe := discoveryProbe{}
			out, errOut := &strings.Builder{}, &strings.Builder{}
			if code := Run(context.Background(), args, probe.deps(out, errOut)); code != ExitUsage {
				t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
			}
			probe.assertUnused(t)
		})
	}
}
