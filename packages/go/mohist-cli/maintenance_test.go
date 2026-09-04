package mohistcli

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestManagedRunnerUpdateVerifiesBeforeReleasingFence(t *testing.T) {
	home := t.TempDir()
	candidateRoot := filepath.Join(home, ".mohist", "releases", "runner", "candidate")
	if err := os.MkdirAll(candidateRoot, 0o700); err != nil {
		t.Fatal(err)
	}
	entrypoint := filepath.Join(candidateRoot, "dist", "cli.js")
	if err := os.MkdirAll(filepath.Dir(entrypoint), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(entrypoint, []byte("runner"), 0o700); err != nil {
		t.Fatal(err)
	}
	manifestPath := filepath.Join(candidateRoot, "identity.json")
	manifest := `{"component":"runner","version":"v1","sourceRevision":"git-1","gitHash":"git-1","treeHash":"tree-1","artifactDigest":"sha256:x","releaseId":"release-1","generation":1}`
	if err := os.WriteFile(manifestPath, []byte(manifest), 0o600); err != nil {
		t.Fatal(err)
	}
	var calls []string
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		calls = append(calls, request.Method+" "+request.URL.Path)
		if request.Method == http.MethodPost && strings.HasSuffix(request.URL.Path, "/update-interrupt") {
			var body struct{ UpdateInterruptID string `json:"updateInterruptId"` }
			if err := json.NewDecoder(request.Body).Decode(&body); err != nil {
				t.Fatal(err)
			}
			return response(http.StatusOK, `{"success":true,"data":{"status":"draining","updateInterruptId":"`+body.UpdateInterruptID+`"}}`), nil
		}
		if request.Method == http.MethodGet {
			return response(http.StatusOK, `{"success":true,"data":{"runnerId":"runner-1","component":"runner","version":"v1","sourceRevision":"git-1","buildGitHash":"git-1","treeHash":"tree-1","artifactDigest":"sha256:x","releaseId":"release-1","generation":1}}`), nil
		}
		return response(http.StatusOK, `{"success":true,"data":{"status":"cancelled"}}`), nil
	}), map[string]string{"MOHIST_SERVER_URL": "http://server", "MOHIST_TOKEN": "operator"})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.ReadFile = os.ReadFile
	deps.Execute = func(_ context.Context, name string, args []string) error {
		calls = append(calls, name+" "+strings.Join(args, " "))
		return nil
	}
	c := command{kind: "update-runner", args: []string{"runner-id", "runner-1"}}
	if code := activateManagedRelease(context.Background(), deps, "runner", c, managedRelease{Root: candidateRoot, Entrypoint: entrypoint, ManifestPath: manifestPath}, home, filepath.Join(home, ".config", "systemd", "user"), "mohist-runner.service"); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if !strings.Contains(out.String(), "Installed and started") || strings.Contains(out.String(), "success") && strings.Index(out.String(), "success") < strings.Index(strings.Join(calls, "\n"), "GET") {
		t.Fatalf("premature success: stdout=%q calls=%#v", out.String(), calls)
	}
	if len(calls) == 0 || !strings.Contains(calls[len(calls)-1], "/cancel") {
		t.Fatalf("fence was not released last: %#v", calls)
	}
}

func TestManagedUpdateMismatchRestoresPreviousUnitAndReportsIdentities(t *testing.T) {
	home := t.TempDir()
	unitDir := filepath.Join(home, ".config", "systemd", "user")
	if err := os.MkdirAll(unitDir, 0o700); err != nil {
		t.Fatal(err)
	}
	unitPath := filepath.Join(unitDir, "mohist.service")
	oldUnit := "old unit\n"
	if err := os.WriteFile(unitPath, []byte(oldUnit), 0o600); err != nil {
		t.Fatal(err)
	}
	candidateRoot := filepath.Join(home, ".mohist", "releases", "server", "candidate")
	if err := os.MkdirAll(candidateRoot, 0o700); err != nil {
		t.Fatal(err)
	}
	entrypoint := filepath.Join(candidateRoot, "Mohist.Server")
	manifestPath := filepath.Join(candidateRoot, "identity.json")
	if err := os.WriteFile(entrypoint, []byte("server"), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(manifestPath, []byte(`{"component":"server","version":"expected","sourceRevision":"expected","gitHash":"expected","treeHash":"tree","artifactDigest":"digest","releaseId":"expected","generation":1}`), 0o600); err != nil {
		t.Fatal(err)
	}
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":{"running":{"component":"server","version":"actual","sourceRevision":"actual","gitHash":"actual","treeHash":"tree","artifactDigest":"digest","releaseId":"actual","generation":1}}}`), nil
	}), map[string]string{"MOHIST_SERVER_URL": "http://server", "MOHIST_TOKEN": "operator"})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.ReadFile = os.ReadFile
	deps.Execute = func(context.Context, string, []string) error { return nil }
	if code := activateManagedRelease(context.Background(), deps, "server", command{kind: "update-server"}, managedRelease{Root: candidateRoot, Entrypoint: entrypoint, ManifestPath: manifestPath}, home, unitDir, "mohist.service"); code != ExitOperation {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	data, err := os.ReadFile(unitPath)
	if err != nil {
		t.Fatal(err)
	}
	if string(data) != oldUnit {
		t.Fatalf("unit was not restored: %q", data)
	}
	if !strings.Contains(errOut.String(), "expected=") || !strings.Contains(errOut.String(), "actual=") || !strings.Contains(errOut.String(), "mo service start server") {
		t.Fatalf("stderr=%q", errOut.String())
	}
}

func TestServiceCommandsUseUserSystemdAndJournalctl(t *testing.T) {
	tests := []struct {
		name       string
		args       []string
		output     string
		wantOutput string
		wantName   string
		wantArgs   []string
	}{
		{name: "start", args: []string{"service", "start", "runner"}, wantName: "systemctl", wantArgs: []string{"--user", "start", "mohist-runner.service"}},
		{name: "stop", args: []string{"service", "stop", "server"}, wantName: "systemctl", wantArgs: []string{"--user", "stop", "mohist.service"}},
		{name: "restart", args: []string{"service", "restart", "slack"}, wantName: "systemctl", wantArgs: []string{"--user", "restart", "mohist-slack.service"}},
		{name: "status", args: []string{"service", "status", "runner"}, output: "ActiveState=active\n", wantOutput: "ActiveState=active\n", wantName: "systemctl", wantArgs: []string{"--user", "show", "--no-pager", "--property=Id,ActiveState,SubState,Result,ExecMainStatus", "mohist-runner.service"}},
		{name: "logs", args: []string{"service", "logs", "server", "--lines", "25", "--follow"}, output: "server log\n", wantOutput: "server log\n", wantName: "journalctl", wantArgs: []string{"--user", "-u", "mohist.service", "--no-pager", "-n", "25", "-f"}},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			deps, out, errOut := testDeps(nil, map[string]string{})
			var gotName string
			var gotArgs []string
			deps.Execute = func(_ context.Context, name string, args []string) error {
				gotName, gotArgs = name, args
				return nil
			}
			deps.ExecuteOutput = func(_ context.Context, name string, args []string) (string, error) {
				gotName, gotArgs = name, args
				return test.output, nil
			}
			if code := Run(context.Background(), test.args, deps); code != ExitOK {
				t.Fatalf("code=%d stderr=%q", code, errOut.String())
			}
			if gotName != test.wantName || strings.Join(gotArgs, "\x00") != strings.Join(test.wantArgs, "\x00") {
				t.Fatalf("command=%s %#v, want %s %#v", gotName, gotArgs, test.wantName, test.wantArgs)
			}
			wantOutput := "OK\n"
			if test.wantOutput != "" {
				wantOutput = test.wantOutput
			}
			if out.String() != wantOutput {
				t.Fatalf("stdout=%q", out.String())
			}
		})
	}
}

func TestServiceDryRunDoesNotInvokeServiceManager(t *testing.T) {
	deps, out, errOut := testDeps(nil, map[string]string{})
	called := false
	deps.Execute = func(context.Context, string, []string) error { called = true; return nil }
	deps.ExecuteOutput = func(context.Context, string, []string) (string, error) { called = true; return "", nil }
	if code := Run(context.Background(), []string{"service", "restart", "runner", "--dry-run"}, deps); code != ExitOK {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if called || out.String() != "Dry run: restart runner\n" {
		t.Fatalf("called=%v stdout=%q", called, out.String())
	}
}

func TestServiceUninstallReportsCleanupFailure(t *testing.T) {
	home := t.TempDir()
	deps, _, errOut := testDeps(nil, map[string]string{})
	deps.HomeDir = func() (string, error) { return home, nil }
	var commands [][]string
	deps.Execute = func(_ context.Context, name string, args []string) error {
		commands = append(commands, append([]string{name}, args...))
		return nil
	}
	deps.RemoveAll = func(path string) error { return errors.New("cleanup failed: " + path) }
	if code := Run(context.Background(), []string{"service", "uninstall", "runner"}, deps); code != ExitOperation {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if !strings.Contains(errOut.String(), "remove managed service file") || !strings.Contains(errOut.String(), "cleanup failed") {
		t.Fatalf("stderr=%q", errOut.String())
	}
	if len(commands) != 2 || strings.Join(commands[0], " ") != "systemctl --user stop mohist-runner.service" || strings.Join(commands[1], " ") != "systemctl --user disable mohist-runner.service" {
		t.Fatalf("commands=%#v", commands)
	}
}

func TestInstallUpdateLockContentionReturnsStableResultBeforeSideEffects(t *testing.T) {
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		t.Fatal("unexpected enrollment request")
		return nil, nil
	}), map[string]string{"MOHIST_SERVER_URL": "http://server", "MOHIST_TOKEN": "operator-token"})
	deps.HomeDir = func() (string, error) { return t.TempDir(), nil }
	deps.AcquireUserTransactionLock = func(string) (func(), bool, error) { return nil, false, nil }
	writes, executes := 0, 0
	deps.WriteFile = func(string, string, os.FileMode) error { writes++; return nil }
	deps.Execute = func(context.Context, string, []string) error { executes++; return nil }
	if code := Run(context.Background(), []string{"install", "runner"}, deps); code != ExitOperation {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if errOut.String() != "update_in_progress\n" {
		t.Fatalf("stderr=%q", errOut.String())
	}
	if writes != 0 || executes != 0 {
		t.Fatalf("side effects on contention: writes=%d executes=%d", writes, executes)
	}
}

func TestInstallRunnerPersistsEnabledAgentRuntimes(t *testing.T) {
	home := t.TempDir()
	repoRoot := t.TempDir()
	runnerRoot := filepath.Join(home, "custom runner")
	files := map[string]string{}
	modes := map[string]os.FileMode{}
	var commands [][]string
	var enrollmentRequests int
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		enrollmentRequests++
		if request.Method != http.MethodPost || request.URL.Path != "/api/runners/enrollment-tokens" || request.URL.Host != "managed-server" {
			t.Fatalf("unexpected enrollment request: %s %s", request.Method, request.URL.Path)
		}
		return response(http.StatusOK, `{"success":true,"data":{"token":"enrollment-token"}}`), nil
	}), map[string]string{"MOHIST_SERVER_URL": "http://server", "MOHIST_TOKEN": "operator-token"})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.CurrentDirectory = func() string { return repoRoot }
	deps.WriteFile = func(path, value string, mode os.FileMode) error {
		files[path] = value
		modes[path] = mode
		return nil
	}
	deps.Execute = func(_ context.Context, name string, args []string) error {
		commands = append(commands, append([]string{name}, args...))
		return nil
	}

	code := Run(context.Background(), []string{
		"install", "runner", "--repo-root", repoRoot,
		"--server-url", "https://managed-server", "--runner-id", "runner-stable", "--runner-root", runnerRoot,
		"--enabled-agent-runtimes", " OpenCode,pi,opencode ",
	}, deps)
	if code != ExitOK {
		t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
	}
	if enrollmentRequests != 1 {
		t.Fatalf("enrollment requests = %d", enrollmentRequests)
	}
	environmentPath := filepath.Join(home, ".config", "mohist", "runner.env")
	if files[environmentPath] != "ENABLED_AGENT_RUNTIMES=pi,opencode\n" {
		t.Fatalf("environment file = %q", files[environmentPath])
	}
	if modes[environmentPath] != 0o600 {
		t.Fatalf("environment file mode = %o", modes[environmentPath])
	}
	enrollmentTokenPath := filepath.Join(runnerRoot, runnerEnrollmentTokenFile)
	if files[enrollmentTokenPath] != "enrollment-token\n" {
		t.Fatalf("enrollment bootstrap = %q", files[enrollmentTokenPath])
	}
	if modes[enrollmentTokenPath] != 0o600 {
		t.Fatalf("enrollment bootstrap mode = %o", modes[enrollmentTokenPath])
	}
	managedEnvironmentPath := filepath.Join(home, ".config", "mohist", "runner-managed.env")
	wantManagedEnvironment := "SERVER_URL=\"https://managed-server\"\nRUNNER_ID=\"runner-stable\"\nRUNNER_ROOT=\"" + runnerRoot + "\"\n"
	if files[managedEnvironmentPath] != wantManagedEnvironment {
		t.Fatalf("managed environment = %q, want %q", files[managedEnvironmentPath], wantManagedEnvironment)
	}
	if modes[managedEnvironmentPath] != 0o600 {
		t.Fatalf("managed environment mode = %o", modes[managedEnvironmentPath])
	}
	unitPath := filepath.Join(home, ".config", "systemd", "user", "mohist-runner.service")
	unit := files[unitPath]
	if !strings.Contains(unit, "EnvironmentFile=-%h/.config/mohist/runner.env\n") {
		t.Fatalf("unit does not reference managed environment file: %q", unit)
	}
	if !strings.Contains(unit, "EnvironmentFile=-%h/.config/mohist/runner-managed.env\n") {
		t.Fatalf("unit does not reference the managed connection environment: %q", unit)
	}
	if !strings.Contains(unit, "ExecStart=node packages/runner/dist/cli.js\n") {
		t.Fatalf("unit does not use the built Runner CLI entrypoint: %q", unit)
	}
	if strings.Contains(unit, "operator-token") || strings.Contains(unit, "enrollment-token") {
		t.Fatalf("unit leaked a credential: %q", unit)
	}
	if strings.Contains(out.String(), "enrollment-token") || strings.Contains(errOut.String(), "enrollment-token") {
		t.Fatalf("command output leaked the enrollment token: stdout=%q stderr=%q", out, errOut)
	}
	wantCommands := [][]string{
		{"systemctl", "--user", "daemon-reload"},
		{"systemctl", "--user", "enable", "mohist-runner.service"},
		{"systemctl", "--user", "restart", "mohist-runner.service"},
	}
	if len(commands) != len(wantCommands) {
		t.Fatalf("commands = %#v", commands)
	}
	for index := range wantCommands {
		if strings.Join(commands[index], "\x00") != strings.Join(wantCommands[index], "\x00") {
			t.Fatalf("command %d = %#v, want %#v", index, commands[index], wantCommands[index])
		}
		if strings.Contains(strings.Join(commands[index], " "), "enrollment-token") {
			t.Fatalf("command %d leaked the enrollment token: %#v", index, commands[index])
		}
	}
}

func TestInstallRunnerEnabledAgentRuntimesValidation(t *testing.T) {
	tests := []struct {
		name string
		args []string
		want string
	}{
		{name: "empty", args: []string{"install", "runner", "--enabled-agent-runtimes", ""}, want: "must be a non-empty"},
		{name: "empty member", args: []string{"install", "runner", "--enabled-agent-runtimes", "pi,"}, want: "must be a non-empty"},
		{name: "unknown", args: []string{"install", "runner", "--enabled-agent-runtimes", "pi,codex"}, want: "unknown Runtime"},
		{name: "line injection", args: []string{"install", "runner", "--enabled-agent-runtimes", "pi\nINJECTED=value"}, want: "unknown Runtime"},
		{name: "missing value", args: []string{"install", "runner", "--enabled-agent-runtimes"}, want: "requires a value"},
		{name: "server scope", args: []string{"install", "server", "--enabled-agent-runtimes", "pi"}, want: "only valid with mo install runner"},
		{name: "update scope", args: []string{"update", "runner", "--enabled-agent-runtimes", "pi"}, want: "only valid with mo install runner"},
		{name: "duplicate flag", args: []string{"install", "runner", "--enabled-agent-runtimes", "pi", "--enabled-agent-runtimes", "opencode"}, want: "only once"},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			calls := 0
			deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
				calls++
				return nil, errors.New("must not call")
			}), map[string]string{})
			if code := Run(context.Background(), test.args, deps); code != ExitUsage {
				t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
			}
			if calls != 0 {
				t.Fatalf("HTTP calls = %d", calls)
			}
			if !strings.Contains(errOut.String(), test.want) {
				t.Fatalf("stderr = %q, want %q", errOut.String(), test.want)
			}
		})
	}
}

func TestInstallComponentRunnerRejectsEmptyEnrollmentTokenBeforeSideEffects(t *testing.T) {
	writes := 0
	executes := 0
	deps, _, errOut := testDeps(nil, map[string]string{})
	deps.CurrentDirectory = func() string { return "/repo" }
	deps.WriteFile = func(string, string, os.FileMode) error {
		writes++
		return nil
	}
	deps.Execute = func(context.Context, string, []string) error {
		executes++
		return nil
	}

	code := installComponent(context.Background(), deps, "runner", command{}, "", "")

	if code != ExitOperation {
		t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
	}
	if writes != 0 || executes != 0 {
		t.Fatalf("side effects before enrollment: writes=%d executes=%d", writes, executes)
	}
	if !strings.Contains(errOut.String(), "runner enrollment token is required") {
		t.Fatalf("stderr = %q", errOut.String())
	}
}

func TestInstallRunnerWithoutRuntimeFlagDoesNotCreateEnvironmentFile(t *testing.T) {
	home := t.TempDir()
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":{"token":"enrollment-token"}}`), nil
	}), map[string]string{"MOHIST_SERVER_URL": "http://server", "MOHIST_TOKEN": "operator-token"})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.CurrentDirectory = func() string { return t.TempDir() }
	deps.Execute = func(context.Context, string, []string) error { return nil }

	if code := Run(context.Background(), []string{"install", "runner"}, deps); code != ExitOK {
		t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
	}
	environmentPath := filepath.Join(home, ".config", "mohist", "runner.env")
	if _, err := os.Stat(environmentPath); !os.IsNotExist(err) {
		t.Fatalf("environment file was created without the flag: %v", err)
	}
	unitPath := filepath.Join(home, ".config", "systemd", "user", "mohist-runner.service")
	unit, err := os.ReadFile(unitPath)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(unit), "EnvironmentFile=-%h/.config/mohist/runner.env\n") {
		t.Fatalf("unit does not tolerate the absent environment file: %q", unit)
	}
}

func TestInstallRunnerBootstrapWriteFailureHasNoSystemdEffects(t *testing.T) {
	home := t.TempDir()
	var commands [][]string
	deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		return response(http.StatusOK, `{"success":true,"data":{"token":"enrollment-token"}}`), nil
	}), map[string]string{"MOHIST_SERVER_URL": "http://server", "MOHIST_TOKEN": "operator-token"})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.CurrentDirectory = func() string { return "/repo" }
	deps.WriteFile = func(path, _ string, _ os.FileMode) error {
		if path == filepath.Join(home, ".mohist", "projects", runnerEnrollmentTokenFile) {
			return errors.New("bootstrap unavailable")
		}
		t.Fatalf("unexpected write after bootstrap failure: %s", path)
		return nil
	}
	deps.Execute = func(_ context.Context, name string, args []string) error {
		commands = append(commands, append([]string{name}, args...))
		return nil
	}

	if code := Run(context.Background(), []string{"install", "runner"}, deps); code != ExitOperation {
		t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
	}
	if len(commands) != 0 {
		t.Fatalf("systemd commands after bootstrap failure: %#v", commands)
	}
}

func TestRunnerManagedEnvironmentEscapesValuesAndRejectsLineInjection(t *testing.T) {
	got, err := runnerManagedEnvironment(`https://server/"quoted"`, "runner-1", `C:\runner path`)
	if err != nil {
		t.Fatal(err)
	}
	want := "SERVER_URL=\"https://server/\\\"quoted\\\"\"\nRUNNER_ID=\"runner-1\"\nRUNNER_ROOT=\"C:\\\\runner path\"\n"
	if got != want {
		t.Fatalf("managed environment = %q, want %q", got, want)
	}
	for _, test := range []struct {
		name       string
		serverURL  string
		runnerRoot string
	}{
		{name: "server newline", serverURL: "https://server\nINJECTED=value", runnerRoot: "/runner"},
		{name: "root newline", serverURL: "https://server", runnerRoot: "/runner\nINJECTED=value"},
		{name: "root nul", serverURL: "https://server", runnerRoot: "/runner\x00tail"},
	} {
		t.Run(test.name, func(t *testing.T) {
			if _, err := runnerManagedEnvironment(test.serverURL, "runner-1", test.runnerRoot); err == nil {
				t.Fatal("expected invalid managed environment value")
			}
		})
	}
}

func TestRunnerMaintenanceWithoutRuntimeFlagPreservesEnvironmentFile(t *testing.T) {
	for _, args := range [][]string{{"install", "runner"}, {"update", "runner"}} {
		t.Run(strings.Join(args, " "), func(t *testing.T) {
			home := t.TempDir()
			repoRoot := t.TempDir()
			dist := filepath.Join(repoRoot, "packages", "runner", "dist")
			if err := os.MkdirAll(dist, 0o700); err != nil {
				t.Fatal(err)
			}
			if err := os.WriteFile(filepath.Join(dist, "cli.js"), []byte("runner"), 0o600); err != nil {
				t.Fatal(err)
			}
			if err := os.WriteFile(filepath.Join(repoRoot, "packages", "runner", "package.json"), []byte(`{"name":"runner"}`), 0o600); err != nil {
				t.Fatal(err)
			}
			environmentPath := filepath.Join(home, ".config", "mohist", "runner.env")
			if err := os.MkdirAll(filepath.Dir(environmentPath), 0o700); err != nil {
				t.Fatal(err)
			}
			if err := os.WriteFile(environmentPath, []byte("ENABLED_AGENT_RUNTIMES=opencode\n"), 0o600); err != nil {
				t.Fatal(err)
			}
			deps, _, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
				return response(http.StatusOK, `{"success":true,"data":{"token":"enrollment-token"}}`), nil
			}), map[string]string{"MOHIST_SERVER_URL": "http://server", "MOHIST_TOKEN": "operator-token"})
			deps.HomeDir = func() (string, error) { return home, nil }
			deps.CurrentDirectory = func() string { return repoRoot }
			deps.Execute = func(context.Context, string, []string) error { return nil }

			if code := Run(context.Background(), append(args, "--repo-root", repoRoot), deps); code != ExitOK {
				t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
			}
			contents, err := os.ReadFile(environmentPath)
			if err != nil {
				t.Fatal(err)
			}
			if string(contents) != "ENABLED_AGENT_RUNTIMES=opencode\n" {
				t.Fatalf("environment file changed to %q", contents)
			}
		})
	}
}

func TestUpdateRunnerPreservesCredentialFiles(t *testing.T) {
	home := t.TempDir()
	repoRoot := t.TempDir()
	runnerDist := filepath.Join(repoRoot, "packages", "runner", "dist")
	if err := os.MkdirAll(runnerDist, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(runnerDist, "cli.js"), []byte("new runner"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(repoRoot, "packages", "runner", "package.json"), []byte(`{"name":"runner"}`), 0o600); err != nil {
		t.Fatal(err)
	}
	runnerRoot := filepath.Join(home, ".mohist", "projects")
	enrollmentTokenPath := filepath.Join(runnerRoot, runnerEnrollmentTokenFile)
	credentialPath := filepath.Join(runnerRoot, "credential")
	managedEnvironmentPath := filepath.Join(home, ".config", "mohist", "runner-managed.env")
	if err := os.MkdirAll(runnerRoot, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(enrollmentTokenPath, []byte("pending-enrollment\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(credentialPath, []byte("machine-credential\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Dir(managedEnvironmentPath), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(managedEnvironmentPath, []byte("SERVER_URL=\"https://managed\"\nRUNNER_ROOT=\"/custom\"\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	deps, out, errOut := testDeps(nil, map[string]string{})
	deps.HomeDir = func() (string, error) { return home, nil }
	var commands [][]string
	deps.Execute = func(_ context.Context, name string, args []string) error {
		commands = append(commands, append([]string{name}, args...))
		return nil
	}

	if code := Run(context.Background(), []string{"update", "runner", "--repo-root", repoRoot}, deps); code != ExitOK {
		t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
	}
	for path, want := range map[string]string{
		enrollmentTokenPath:    "pending-enrollment\n",
		credentialPath:         "machine-credential\n",
		managedEnvironmentPath: "SERVER_URL=\"https://managed\"\nRUNNER_ROOT=\"/custom\"\n",
	} {
		contents, err := os.ReadFile(path)
		if err != nil {
			t.Fatal(err)
		}
		if string(contents) != want {
			t.Fatalf("%s changed to %q", path, contents)
		}
	}
	combined := out.String() + errOut.String()
	for _, command := range commands {
		combined += strings.Join(command, " ")
	}
	if strings.Contains(combined, "pending-enrollment") || strings.Contains(combined, "machine-credential") {
		t.Fatalf("update leaked credentials: %q", combined)
	}
}

func TestUpdateRunnerStagesAbsoluteCandidateWithManifest(t *testing.T) {
	home := t.TempDir()
	repoRoot := t.TempDir()
	dist := filepath.Join(repoRoot, "packages", "runner", "dist")
	if err := os.MkdirAll(dist, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dist, "cli.js"), []byte("runner"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(repoRoot, "packages", "runner", "package.json"), []byte(`{"name":"runner"}`), 0o600); err != nil {
		t.Fatal(err)
	}
	deps, _, errOut := testDeps(nil, map[string]string{})
	deps.HomeDir = func() (string, error) { return home, nil }
	var commands [][]string
	deps.Execute = func(_ context.Context, name string, args []string) error {
		commands = append(commands, append([]string{name}, args...))
		return nil
	}
	if code := Run(context.Background(), []string{"update", "runner", "--repo-root", repoRoot}, deps); code != ExitOK {
		t.Fatalf("exit code=%d stderr=%q", code, errOut.String())
	}
	unitPath := filepath.Join(home, ".config", "systemd", "user", "mohist-runner.service")
	unit, err := os.ReadFile(unitPath)
	if err != nil {
		t.Fatal(err)
	}
	text := string(unit)
	if strings.Contains(text, repoRoot) || !strings.Contains(text, "MOHIST_RUNTIME_IDENTITY_PATH=") || !strings.Contains(text, "ExecStart=") {
		t.Fatalf("unit contains non-managed paths or no manifest: %q", text)
	}
	if !strings.Contains(text, ".mohist/releases/runner/") || !strings.Contains(text, "/dist/cli.js") {
		t.Fatalf("unit does not point to installed runner candidate: %q", text)
	}
	if len(commands) != 4 || commands[0][0] != "npm" || commands[1][2] != "daemon-reload" || commands[2][2] != "enable" {
		t.Fatalf("commands=%#v", commands)
	}
	manifestPath := strings.TrimPrefix(strings.Split(text, "MOHIST_RUNTIME_IDENTITY_PATH=")[1], "")
	manifestPath = strings.TrimSpace(strings.Split(manifestPath, "\n")[0])
	manifest, err := os.ReadFile(manifestPath)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(manifest), `"component": "runner"`) || !strings.Contains(string(manifest), `"generation": 1`) {
		t.Fatalf("manifest=%q", manifest)
	}
}

func TestUpdateRunnerMissingCanonicalEntrypointDoesNotInvokeSystemd(t *testing.T) {
	home := t.TempDir()
	repoRoot := t.TempDir()
	if err := os.MkdirAll(filepath.Join(repoRoot, "packages", "runner", "dist"), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(repoRoot, "packages", "runner", "package.json"), []byte(`{"name":"runner"}`), 0o600); err != nil {
		t.Fatal(err)
	}
	deps, _, errOut := testDeps(nil, map[string]string{})
	deps.HomeDir = func() (string, error) { return home, nil }
	systemdCalls := 0
	deps.Execute = func(_ context.Context, name string, _ []string) error {
		if name == "systemctl" {
			systemdCalls++
		}
		return nil
	}
	if code := Run(context.Background(), []string{"update", "runner", "--repo-root", repoRoot}, deps); code != ExitOperation {
		t.Fatalf("exit code=%d stderr=%q", code, errOut.String())
	}
	if systemdCalls != 0 || !strings.Contains(errOut.String(), "canonical runner entrypoint") {
		t.Fatalf("systemdCalls=%d stderr=%q", systemdCalls, errOut.String())
	}
}

func TestUpdateServerPublishesInstalledCandidateWithManifest(t *testing.T) {
	home := t.TempDir()
	repoRoot := t.TempDir()
	project := filepath.Join(repoRoot, "packages", "server", "src", "Mohist.Server")
	if err := os.MkdirAll(project, 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(project, "Mohist.Server.csproj"), []byte("<Project />"), 0o600); err != nil {
		t.Fatal(err)
	}
	deps, _, errOut := testDeps(nil, map[string]string{})
	deps.HomeDir = func() (string, error) { return home, nil }
	var commands [][]string
	deps.Execute = func(_ context.Context, name string, args []string) error {
		commands = append(commands, append([]string{name}, args...))
		if name == "dotnet" {
			for i := 0; i+1 < len(args); i++ {
				if args[i] == "-o" {
					return os.WriteFile(filepath.Join(args[i+1], "Mohist.Server"), []byte("server"), 0o700)
				}
			}
		}
		return nil
	}
	if code := Run(context.Background(), []string{"update", "server", "--repo-root", repoRoot}, deps); code != ExitOK {
		t.Fatalf("exit code=%d stderr=%q", code, errOut.String())
	}
	unit, err := os.ReadFile(filepath.Join(home, ".config", "systemd", "user", "mohist.service"))
	if err != nil {
		t.Fatal(err)
	}
	text := string(unit)
	if strings.Contains(text, repoRoot) || !strings.Contains(text, ".mohist/releases/server/") || !strings.Contains(text, "MOHIST_RUNTIME_IDENTITY_PATH=") {
		t.Fatalf("unit=%q", text)
	}
	if len(commands) != 4 || commands[0][0] != "dotnet" || commands[0][1] != "publish" {
		t.Fatalf("commands=%#v", commands)
	}
}

func TestInstallHelpDocumentsRunnerRuntimeSelection(t *testing.T) {
	deps, out, errOut := testDeps(nil, map[string]string{})
	if code := Run(context.Background(), []string{"install", "runner", "--help"}, deps); code != ExitOK {
		t.Fatalf("exit code = %d, stderr = %s", code, errOut.String())
	}
	if !strings.Contains(out.String(), "mo install runner [--enabled-agent-runtimes <list>]") {
		t.Fatalf("help = %q", out.String())
	}
}

func writeTestSkill(t *testing.T, root, name string) {
	t.Helper()
	path := filepath.Join(root, name, "SKILL.md")
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatal(err)
	}
	writeTestSkillContent(t, path, "body")
}

func writeTestSkillContent(t *testing.T, path, body string) {
	t.Helper()
	if err := os.WriteFile(path, []byte("---\nname: "+filepath.Base(filepath.Dir(path))+"\ndescription: test skill\n---\n\n"+body+"\n"), 0o600); err != nil {
		t.Fatal(err)
	}
}

func TestSkillListUsesLocalAssetsAndFieldSelection(t *testing.T) {
	root := t.TempDir()
	writeTestSkill(t, root, "mohist")
	writeTestSkill(t, root, "mohist-explore")
	var stdout, stderr bytes.Buffer
	code := Run(context.Background(), []string{"skill", "list", "--json", "name"}, Dependencies{
		Stdout: &stdout,
		Stderr: &stderr,
		Lookup: func(name string) (string, bool) {
			if name == "MOHIST_SKILLS_DIR" {
				return root, true
			}
			return "", false
		},
		HomeDir:          func() (string, error) { return t.TempDir(), nil },
		Executable:       func() string { return filepath.Join(t.TempDir(), "mo") },
		CurrentDirectory: func() string { return t.TempDir() },
	})
	if code != ExitOK {
		t.Fatalf("exit code = %d, stderr = %s", code, stderr.String())
	}
	if got := stdout.String(); !strings.Contains(got, `"name":"mohist"`) || strings.Contains(got, "description") {
		t.Fatalf("unexpected JSON: %s", got)
	}
}

func TestSkillInstallUsesPrivateFileMode(t *testing.T) {
	root := t.TempDir()
	writeTestSkill(t, root, "mohist")
	var stdout, stderr bytes.Buffer
	var writtenPath string
	var writtenMode os.FileMode
	code := Run(context.Background(), []string{"skill", "install", "--path", root}, Dependencies{
		Stdout: &stdout,
		Stderr: &stderr,
		Lookup: func(name string) (string, bool) {
			if name == "MOHIST_SKILLS_DIR" {
				return root, true
			}
			return "", false
		},
		HomeDir:          func() (string, error) { return root, nil },
		CurrentDirectory: func() string { return root },
		Executable:       func() string { return filepath.Join(root, "mo") },
		WriteFile:        func(path, value string, mode os.FileMode) error { writtenPath, writtenMode = path, mode; return nil },
	})
	if code != ExitOK {
		t.Fatalf("exit code = %d, stderr = %s", code, stderr.String())
	}
	if !strings.HasSuffix(writtenPath, filepath.Join(".agents", "skills", "mohist", "SKILL.md")) || writtenMode != 0o600 {
		t.Fatalf("path/mode = %s/%o", writtenPath, writtenMode)
	}
}

func TestUpdateCLIFailureKeepsExistingExecutable(t *testing.T) {
	root := t.TempDir()
	target := filepath.Join(root, "mo")
	if err := os.WriteFile(target, []byte("old"), 0o755); err != nil {
		t.Fatal(err)
	}
	var stdout, stderr bytes.Buffer
	code := Run(context.Background(), []string{"update", "cli", "--repo-root", root, "--cli-path", target}, Dependencies{
		Stdout:           &stdout,
		Stderr:           &stderr,
		Lookup:           func(string) (string, bool) { return "", false },
		HomeDir:          func() (string, error) { return root, nil },
		CurrentDirectory: func() string { return root },
		Executable:       func() string { return target },
		Execute:          func(context.Context, string, []string) error { return errors.New("build failed") },
	})
	if code != ExitOperation {
		t.Fatalf("exit code = %d", code)
	}
	data, err := os.ReadFile(target)
	if err != nil {
		t.Fatal(err)
	}
	if string(data) != "old" {
		t.Fatalf("target changed to %q", data)
	}
	if _, err := os.Stat(target + ".tmp"); !os.IsNotExist(err) {
		t.Fatalf("temporary binary remains: %v", err)
	}
}

func TestUpdateCLIUsesCanonicalGoAssets(t *testing.T) {
	root := t.TempDir()
	goCLI := canonicalGoCLIPath(root)
	writeTestSkill(t, canonicalGoSkillDataPath(root), "target-skill")
	writeTestSkill(t, filepath.Join(root, "packages", "cli", "skill-data"), "wrong-skill")
	target := filepath.Join(t.TempDir(), "mo")
	if err := os.WriteFile(target, []byte("old"), 0o755); err != nil {
		t.Fatal(err)
	}
	home := t.TempDir()
	writeTestSkill(t, filepath.Join(home, ".mohist", "cli", "skill-data"), "old-skill")
	var stdout, stderr bytes.Buffer
	var buildName string
	var buildArgs []string
	code := Run(context.Background(), []string{"update", "cli", "--repo-root", root, "--cli-path", target}, Dependencies{
		Stdout:           &stdout,
		Stderr:           &stderr,
		HomeDir:          func() (string, error) { return home, nil },
		CurrentDirectory: func() string { return filepath.Join(t.TempDir(), "caller") },
		Executable:       func() string { return target },
		Execute: func(_ context.Context, name string, args []string) error {
			buildName, buildArgs = name, append([]string(nil), args...)
			for i := 0; i+1 < len(args); i++ {
				if args[i] == "-o" {
					return os.WriteFile(args[i+1], []byte("new"), 0o600)
				}
			}
			return errors.New("missing build output")
		},
	})
	if code != ExitOK {
		t.Fatalf("exit code = %d, stderr = %s", code, stderr.String())
	}
	if buildName != "go" || len(buildArgs) < 2 || buildArgs[0] != "-C" || buildArgs[1] != goCLI {
		t.Fatalf("build command = %s %v", buildName, buildArgs)
	}
	data, err := os.ReadFile(target)
	if err != nil {
		t.Fatal(err)
	}
	if string(data) != "new" {
		t.Fatalf("binary = %q", data)
	}
	if _, err := os.Stat(filepath.Join(home, ".mohist", "cli", "skill-data", "target-skill", "SKILL.md")); err != nil {
		t.Fatalf("target skill was not synchronized: %v", err)
	}
	if _, err := os.Stat(filepath.Join(home, ".mohist", "cli", "skill-data", "wrong-skill")); !os.IsNotExist(err) {
		t.Fatalf("non-canonical skill source was used: %v", err)
	}
}

func TestUpdateCLIMissingSkillTreePreservesInstallation(t *testing.T) {
	root := t.TempDir()
	target := filepath.Join(t.TempDir(), "mo")
	if err := os.WriteFile(target, []byte("old"), 0o755); err != nil {
		t.Fatal(err)
	}
	home := t.TempDir()
	managed := filepath.Join(home, ".mohist", "cli", "skill-data")
	writeTestSkill(t, managed, "old-skill")
	code := Run(context.Background(), []string{"update", "cli", "--repo-root", root, "--cli-path", target}, Dependencies{
		HomeDir:          func() (string, error) { return home, nil },
		CurrentDirectory: func() string { return root },
		Executable:       func() string { return target },
		Execute: func(_ context.Context, _ string, args []string) error {
			for i := 0; i+1 < len(args); i++ {
				if args[i] == "-o" {
					return os.WriteFile(args[i+1], []byte("new"), 0o600)
				}
			}
			return errors.New("missing build output")
		},
	})
	if code != ExitOperation {
		t.Fatalf("exit code = %d", code)
	}
	assertInstallationPreserved(t, target, managed, "old-skill")
}

func TestUpdateCLIRenameFailurePreservesInstallation(t *testing.T) {
	root := t.TempDir()
	writeTestSkill(t, canonicalGoSkillDataPath(root), "new-skill")
	target := filepath.Join(t.TempDir(), "mo")
	if err := os.WriteFile(target, []byte("old"), 0o755); err != nil {
		t.Fatal(err)
	}
	home := t.TempDir()
	managed := filepath.Join(home, ".mohist", "cli", "skill-data")
	writeTestSkill(t, managed, "old-skill")
	var failTarget string
	code := Run(context.Background(), []string{"update", "cli", "--repo-root", root, "--cli-path", target}, Dependencies{
		HomeDir:          func() (string, error) { return home, nil },
		CurrentDirectory: func() string { return root },
		Executable:       func() string { return target },
		Execute: func(_ context.Context, _ string, args []string) error {
			for i := 0; i+1 < len(args); i++ {
				if args[i] == "-o" {
					return os.WriteFile(args[i+1], []byte("new"), 0o600)
				}
			}
			return errors.New("missing build output")
		},
		Rename: func(old, new string) error {
			if failTarget == "" && new == managed {
				failTarget = new
				return errors.New("managed tree rename failed")
			}
			return os.Rename(old, new)
		},
	})
	if code != ExitOperation || failTarget != managed {
		t.Fatalf("code = %d, failed target = %q", code, failTarget)
	}
	assertInstallationPreserved(t, target, managed, "old-skill")
}

func TestUpdateCLIFinalBinaryRenameFailurePreservesInstallation(t *testing.T) {
	root := t.TempDir()
	writeTestSkill(t, canonicalGoSkillDataPath(root), "new-skill")
	target := filepath.Join(t.TempDir(), "mo")
	if err := os.WriteFile(target, []byte("old"), 0o755); err != nil {
		t.Fatal(err)
	}
	home := t.TempDir()
	managed := filepath.Join(home, ".mohist", "cli", "skill-data")
	writeTestSkill(t, managed, "old-skill")
	failed := false
	code := Run(context.Background(), []string{"update", "cli", "--repo-root", root, "--cli-path", target}, Dependencies{
		HomeDir:          func() (string, error) { return home, nil },
		CurrentDirectory: func() string { return root },
		Executable:       func() string { return target },
		Execute: func(_ context.Context, _ string, args []string) error {
			for i := 0; i+1 < len(args); i++ {
				if args[i] == "-o" {
					return os.WriteFile(args[i+1], []byte("new"), 0o600)
				}
			}
			return errors.New("missing build output")
		},
		Rename: func(old, new string) error {
			if new == target && !failed {
				failed = true
				return errors.New("binary rename failed")
			}
			return os.Rename(old, new)
		},
	})
	if code != ExitOperation {
		t.Fatalf("code = %d", code)
	}
	assertInstallationPreserved(t, target, managed, "old-skill")
}

func TestUpdateCLIInvalidSkillTreePreservesInstallation(t *testing.T) {
	root := t.TempDir()
	skillData := canonicalGoSkillDataPath(root)
	path := filepath.Join(skillData, "broken", "SKILL.md")
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte("not valid skill frontmatter"), 0o600); err != nil {
		t.Fatal(err)
	}
	target := filepath.Join(t.TempDir(), "mo")
	if err := os.WriteFile(target, []byte("old"), 0o755); err != nil {
		t.Fatal(err)
	}
	home := t.TempDir()
	managed := filepath.Join(home, ".mohist", "cli", "skill-data")
	writeTestSkill(t, managed, "old-skill")
	code := Run(context.Background(), []string{"update", "cli", "--repo-root", root, "--cli-path", target}, Dependencies{
		HomeDir:          func() (string, error) { return home, nil },
		CurrentDirectory: func() string { return root },
		Executable:       func() string { return target },
		Execute: func(_ context.Context, _ string, args []string) error {
			for i := 0; i+1 < len(args); i++ {
				if args[i] == "-o" {
					return os.WriteFile(args[i+1], []byte("new"), 0o600)
				}
			}
			return errors.New("missing build output")
		},
	})
	if code != ExitOperation {
		t.Fatalf("exit code = %d", code)
	}
	assertInstallationPreserved(t, target, managed, "old-skill")
}

func assertInstallationPreserved(t *testing.T, binary, skills, skillName string) {
	t.Helper()
	data, err := os.ReadFile(binary)
	if err != nil {
		t.Fatal(err)
	}
	if string(data) != "old" {
		t.Fatalf("binary changed to %q", data)
	}
	if _, err := os.Stat(filepath.Join(skills, skillName, "SKILL.md")); err != nil {
		t.Fatalf("managed skills changed: %v", err)
	}
}
