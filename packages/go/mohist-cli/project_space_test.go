package mohistcli

import (
	"context"
	"io"
	"net/http"
	"os"
	"strings"
	"testing"
)

func projectSpaceDeps(t *testing.T, transport http.RoundTripper, files map[string]string) (Dependencies, *strings.Builder, *strings.Builder) {
	t.Helper()
	out, errOut := &strings.Builder{}, &strings.Builder{}
	return Dependencies{
		HTTPClient: &http.Client{Transport: transport},
		Stdout:     out,
		Stderr:     errOut,
		Lookup: func(name string) (string, bool) {
			if name == "MOHIST_TOKEN" {
				return "token", true
			}
			if name == "MOHIST_SERVER_URL" {
				return "http://server", true
			}
			return "", false
		},
		ReadFile: func(path string) (string, error) {
			value, ok := files[path]
			if !ok {
				return "", io.EOF
			}
			return value, nil
		},
		WriteFile:        func(path, value string, _ os.FileMode) error { files[path] = value; return nil },
		HomeDir:          func() (string, error) { return "/home/test", nil },
		CurrentDirectory: func() string { return "/work/tree" },
	}, out, errOut
}

func TestProjectSpaceRepoCreateRequest(t *testing.T) {
	var got *http.Request
	deps, out, errOut := projectSpaceDeps(t, roundTripFunc(func(r *http.Request) (*http.Response, error) {
		got = r
		return response(200, `{"success":true,"data":{"name":"origin"}}`), nil
	}), map[string]string{})

	if code := Run(context.Background(), []string{"repo", "create", "origin", "--git-url", "git@example.com:repo.git", "--project", "proj"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if got == nil || got.Method != http.MethodPost || got.URL.EscapedPath() != "/api/projects/proj/repositories" {
		t.Fatalf("request=%v", got)
	}
}

func TestProjectSpaceResolutionFailurePrecedesRepositoryMutation(t *testing.T) {
	calls := 0
	deps, _, errOut := projectSpaceDeps(t, roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return response(500, `{"success":false,"error":"must not call","code":"test"}`), nil
	}), map[string]string{})

	if code := Run(context.Background(), []string{"repo", "delete", "origin"}, deps); code != ExitOperation {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if calls != 0 || !strings.Contains(errOut.String(), "mo project use") {
		t.Fatalf("calls=%d stderr=%q", calls, errOut.String())
	}
}

func TestWorkspaceCreateUsesNearestProjectStateAndRepositories(t *testing.T) {
	var got *http.Request
	files := map[string]string{"/work/tree/.mohist/cli-state.json": `{"activeProjectId":"nearest"}`}
	deps, _, errOut := projectSpaceDeps(t, roundTripFunc(func(r *http.Request) (*http.Response, error) {
		got = r
		return response(200, `{"success":true,"data":{"name":"ws"}}`), nil
	}), files)

	if code := Run(context.Background(), []string{"workspace", "create", "ws", "--repo", "server"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if got == nil || got.URL.EscapedPath() != "/api/projects/nearest/workspaces" {
		t.Fatalf("request=%v", got)
	}
}

func TestProjectVariableSetRejectsAmbiguousValueLocally(t *testing.T) {
	calls := 0
	deps, _, _ := projectSpaceDeps(t, roundTripFunc(func(*http.Request) (*http.Response, error) {
		calls++
		return response(500, `{}`), nil
	}), map[string]string{})

	if code := Run(context.Background(), []string{"project", "variable", "set", "a.b", "text", "--value-json", "true", "--project", "proj"}, deps); code != ExitUsage {
		t.Fatalf("code=%d", code)
	}
	if calls != 0 {
		t.Fatalf("validation issued %d requests", calls)
	}
}
