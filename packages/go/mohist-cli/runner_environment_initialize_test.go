package mohistcli

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestRunnerEnvironmentInitializeUsesProcessEnvironmentAndDoesNotRestart(t *testing.T) {
	home := t.TempDir()
	unitPath := filepath.Join(home, ".config", "systemd", "user", runnerEnvironmentServiceUnit)
	unit := "[Unit]\nDescription=Runner\n\n[Service]\nWorkingDirectory=/managed\nEnvironment=RUNNER_ID=runner-pluto\nExecStart=/usr/bin/node /managed/dist/cli.js\n"
	files := map[string]string{
		unitPath:             unit,
		"/proc/4242/environ": "PATH=/old/bin:/old/bin:/tmp/injected\x00DOTNET_ROOT=/dotnet\x00SECRET=hidden\x00",
		filepath.Join(home, ".config", "mohist", "runner-environment.env"): "", // populated by the command
	}
	delete(files, filepath.Join(home, ".config", "mohist", "runner-environment.env"))
	deps, out, errOut := testDeps(nil, map[string]string{"PATH": "/terminal/go/bin", "DOTNET_ROOT": "/terminal/dotnet"})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.ReadFile = func(path string) (string, error) {
		value, ok := files[path]
		if !ok {
			return "", os.ErrNotExist
		}
		return value, nil
	}
	deps.WriteFile = func(path, value string, _ os.FileMode) error {
		files[path] = value
		return nil
	}
	deps.WriteFileAtomic = func(path string, value []byte, _ os.FileMode) error {
		files[path] = string(value)
		return nil
	}
	deps.RemoveAll = func(path string) error {
		delete(files, path)
		return nil
	}
	deps.ExecuteOutput = func(_ context.Context, name string, args []string) (string, error) {
		if name != "systemctl" {
			t.Fatalf("unexpected output command %s", name)
		}
		joined := strings.Join(args, " ")
		switch {
		case strings.Contains(joined, "FragmentPath"):
			return unitPath + "\n", nil
		case strings.Contains(joined, "MainPID"):
			return "4242\n", nil
		default:
			t.Fatalf("unexpected output args %v", args)
			return "", nil
		}
	}
	var commands []string
	deps.Execute = func(_ context.Context, name string, args []string) error {
		commands = append(commands, strings.Join(append([]string{name}, args...), " "))
		return nil
	}

	code := Run(context.Background(), []string{"runner", "environment", "initialize", "--json", "initializedVersion,initializedVariables,processId"}, deps)
	if code != ExitOK || errOut.Len() != 0 {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	active := files[filepath.Join(home, ".config", "mohist", "runner-environment.env")]
	if active != "DOTNET_ROOT=\"/dotnet\"\nPATH=\"/old/bin\"\n" {
		t.Fatalf("active snapshot=%q", active)
	}
	if !strings.Contains(files[unitPath], "EnvironmentFile=-%h/.config/mohist/runner-environment.env\n") {
		t.Fatalf("unit=%q", files[unitPath])
	}
	if strings.Contains(files[unitPath], "terminal") || strings.Contains(out.String(), "terminal") || strings.Contains(out.String(), "hidden") {
		t.Fatalf("terminal or secret value leaked: unit=%q output=%q", files[unitPath], out.String())
	}
	if len(commands) != 1 || commands[0] != "systemctl --user daemon-reload" {
		t.Fatalf("commands=%v", commands)
	}
	if strings.Contains(strings.Join(commands, "\n"), "restart") {
		t.Fatal("initialization restarted the Runner")
	}
	if !strings.Contains(out.String(), `"initializedVariables":["DOTNET_ROOT","PATH"]`) || !strings.Contains(out.String(), `"processId":4242`) {
		t.Fatalf("output=%q", out.String())
	}
}

func TestRunnerEnvironmentInitializeIsIdempotent(t *testing.T) {
	home := t.TempDir()
	unitPath := filepath.Join(home, ".config", "systemd", "user", runnerEnvironmentServiceUnit)
	unit := "[Service]\nWorkingDirectory=/managed\nEnvironmentFile=-%h/.config/mohist/runner-environment.env\nExecStart=/usr/bin/node /managed/dist/cli.js\n"
	active := "PATH=\"/old/bin\"\n"
	files := map[string]string{unitPath: unit, filepath.Join(home, ".config", "mohist", "runner-environment.env"): active, "/proc/4242/environ": "PATH=/old/bin\x00"}
	deps, out, errOut := testDeps(nil, map[string]string{"PATH": "/different/terminal"})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.ReadFile = func(path string) (string, error) {
		value, ok := files[path]
		if !ok {
			return "", os.ErrNotExist
		}
		return value, nil
	}
	writeCount := 0
	deps.WriteFile = func(path, value string, _ os.FileMode) error {
		writeCount++
		files[path] = value
		return nil
	}
	deps.WriteFileAtomic = func(path string, value []byte, _ os.FileMode) error {
		writeCount++
		files[path] = string(value)
		return nil
	}
	deps.RemoveAll = func(string) error { return nil }
	deps.ExecuteOutput = func(_ context.Context, _ string, args []string) (string, error) {
		if strings.Contains(strings.Join(args, " "), "FragmentPath") {
			return unitPath, nil
		}
		return "4242", nil
	}
	commands := 0
	deps.Execute = func(context.Context, string, []string) error {
		commands++
		return nil
	}

	if code := Run(context.Background(), []string{"runner", "environment", "initialize"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if writeCount != 0 || commands != 0 || files[unitPath] != unit || files[filepath.Join(home, ".config", "mohist", "runner-environment.env")] != active {
		t.Fatalf("writeCount=%d commands=%d files=%v", writeCount, commands, files)
	}
	if !strings.Contains(out.String(), "already loads") || errOut.Len() != 0 {
		t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
	}
}

func TestRunnerEnvironmentInitializeRollsBackAfterReloadFailure(t *testing.T) {
	home := t.TempDir()
	unitPath := filepath.Join(home, ".config", "systemd", "user", runnerEnvironmentServiceUnit)
	original := "[Service]\nWorkingDirectory=/managed\nExecStart=/usr/bin/node /managed/dist/cli.js\n"
	files := map[string]string{unitPath: original, "/proc/4242/environ": "PATH=/old/bin\x00"}
	deps, _, errOut := testDeps(nil, nil)
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.ReadFile = func(path string) (string, error) {
		value, ok := files[path]
		if !ok {
			return "", os.ErrNotExist
		}
		return value, nil
	}
	deps.WriteFile = func(path, value string, _ os.FileMode) error {
		files[path] = value
		return nil
	}
	deps.WriteFileAtomic = func(path string, value []byte, _ os.FileMode) error {
		files[path] = string(value)
		return nil
	}
	deps.RemoveAll = func(path string) error {
		delete(files, path)
		return nil
	}
	deps.ExecuteOutput = func(_ context.Context, _ string, args []string) (string, error) {
		if strings.Contains(strings.Join(args, " "), "FragmentPath") {
			return unitPath, nil
		}
		return "4242", nil
	}
	reloads := 0
	deps.Execute = func(context.Context, string, []string) error {
		reloads++
		if reloads == 1 {
			return errors.New("reload refused")
		}
		return nil
	}

	if code := Run(context.Background(), []string{"runner", "environment", "initialize"}, deps); code != ExitOperation {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if files[unitPath] != original {
		t.Fatalf("unit was not restored: %q", files[unitPath])
	}
	if _, ok := files[filepath.Join(home, ".config", "mohist", "runner-environment.env")]; ok {
		t.Fatal("active snapshot remained after rollback")
	}
	if !strings.Contains(errOut.String(), "daemon-reload failed") || !strings.Contains(errOut.String(), "rolled back") {
		t.Fatalf("stderr=%q", errOut.String())
	}
}

func TestRunnerEnvironmentInitializeRejectsProcessReplacement(t *testing.T) {
	home := t.TempDir()
	unitPath := filepath.Join(home, ".config", "systemd", "user", runnerEnvironmentServiceUnit)
	original := "[Service]\nWorkingDirectory=/managed\nExecStart=/usr/bin/node /managed/dist/cli.js\n"
	activePath := filepath.Join(home, ".config", "mohist", "runner-environment.env")
	files := map[string]string{unitPath: original, "/proc/4242/environ": "PATH=/old/bin\x00", "/proc/4343/environ": "PATH=/old/bin\x00"}
	deps, _, errOut := testDeps(nil, nil)
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.ReadFile = func(path string) (string, error) {
		value, ok := files[path]
		if !ok {
			return "", os.ErrNotExist
		}
		return value, nil
	}
	deps.WriteFile = func(path, value string, _ os.FileMode) error { files[path] = value; return nil }
	deps.WriteFileAtomic = func(path string, value []byte, _ os.FileMode) error { files[path] = string(value); return nil }
	deps.RemoveAll = func(path string) error { delete(files, path); return nil }
	mainPIDCalls := 0
	deps.ExecuteOutput = func(_ context.Context, _ string, args []string) (string, error) {
		joined := strings.Join(args, " ")
		if strings.Contains(joined, "FragmentPath") {
			return unitPath, nil
		}
		mainPIDCalls++
		if mainPIDCalls == 1 {
			return "4242", nil
		}
		return "4343", nil
	}
	deps.Execute = func(context.Context, string, []string) error { return nil }

	if code := Run(context.Background(), []string{"runner", "environment", "initialize"}, deps); code != ExitOperation {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if files[unitPath] != original {
		t.Fatalf("unit was not restored: %q", files[unitPath])
	}
	if _, ok := files[activePath]; ok {
		t.Fatal("active snapshot remained after process replacement")
	}
	if !strings.Contains(errOut.String(), "changed during initialization") || !strings.Contains(errOut.String(), "rolled back") {
		t.Fatalf("stderr=%q", errOut.String())
	}
}

func TestParseRunnerProcessEnvironmentAppliesConfiguredTemporaryPathRule(t *testing.T) {
	lookup, err := parseRunnerProcessEnvironment("PATH=/tmp/drop:/opt/go:/opt/go\x00TMPDIR=/opt/tmp\x00GOROOT=/go\x00")
	if err != nil {
		t.Fatal(err)
	}
	snapshot, present, err := captureRunnerEnvironment(lookup)
	if err != nil || !present {
		t.Fatalf("present=%v err=%v", present, err)
	}
	if snapshot.Content != "GOROOT=\"/go\"\nPATH=\"/opt/go\"\n" {
		t.Fatalf("snapshot=%q", snapshot.Content)
	}
}

func TestEnsureRunnerEnvironmentFileRejectsNonCanonicalSnapshotPath(t *testing.T) {
	unit := []byte("[Service]\nEnvironmentFile=/other/runner-environment.env\nExecStart=/runner\n")
	if _, _, err := ensureRunnerEnvironmentFile(unit); err == nil || !strings.Contains(err.Error(), "non-canonical") {
		t.Fatalf("err=%v", err)
	}
}

func TestRunnerEnvironmentInitializeRejectsTransactionFlags(t *testing.T) {
	for _, flag := range []string{"--runner-id", "--version", "--update-id"} {
		if _, err := parse([]string{"runner", "environment", "initialize", flag, "value"}); err == nil || !strings.Contains(err.Error(), flag+" is not valid") {
			t.Fatalf("flag %s err=%v", flag, err)
		}
	}
	command, err := parse([]string{"runner", "environment", "initialize", "--json", "initializedVersion"})
	if err != nil || command.kind != "runner-environment-initialize" || len(command.fields) != 1 || command.fields[0] != "initializedVersion" {
		t.Fatalf("initialize json command=%+v err=%v", command, err)
	}
}
