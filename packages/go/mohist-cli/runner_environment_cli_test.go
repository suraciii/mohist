package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

func TestRunnerEnvironmentCaptureWritesOnlyCandidateMetadata(t *testing.T) {
	home := t.TempDir()
	files := map[string]string{}
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		t.Fatal("capture must not contact Server")
		return nil, nil
	}), map[string]string{
		"PATH":   "/opt/go/bin:/tmp/injected:/opt/node/bin",
		"GOROOT": "/opt/go",
		"TOKEN":  "must-not-appear",
	})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.MkdirAll = func(string, os.FileMode) error { return nil }
	deps.WriteFileAtomic = func(path string, value []byte, _ os.FileMode) error {
		files[path] = string(value)
		return nil
	}
	deps.Now = func() time.Time { return time.Date(2026, 9, 18, 1, 2, 3, 0, time.UTC) }

	if code := Run(context.Background(), []string{"runner", "environment", "capture"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	candidatePath := filepath.Join(home, ".config", "mohist", runnerEnvironmentCandidateFileName)
	metadataPath := filepath.Join(home, ".config", "mohist", runnerEnvironmentCandidateMetaName)
	if !strings.Contains(files[candidatePath], "GOROOT=\"/opt/go\"\n") || strings.Contains(files[candidatePath], "TOKEN") || strings.Contains(files[candidatePath], "/tmp/") {
		t.Fatalf("candidate content=%q", files[candidatePath])
	}
	if strings.Contains(files[metadataPath], "/opt/go") || strings.Contains(files[metadataPath], "TOKEN") {
		t.Fatalf("candidate metadata leaked values: %q", files[metadataPath])
	}
	if !strings.Contains(out.String(), "Captured Runner environment candidate") || !strings.Contains(out.String(), "Added: GOROOT, PATH") || errOut.Len() != 0 {
		t.Fatalf("stdout=%q stderr=%q", out.String(), errOut.String())
	}
	if strings.Contains(out.String(), "/opt/go") || strings.Contains(out.String(), "/opt/go/bin") {
		t.Fatalf("raw environment value leaked in preview: %q", out.String())
	}
}

func TestRunnerEnvironmentApplyRequiresExactCandidateVersion(t *testing.T) {
	deps, _, errOut := testDeps(nil, map[string]string{"MOHIST_SERVER_URL": "http://server"})
	home := t.TempDir()
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.ReadFile = func(path string) (string, error) {
		if strings.HasSuffix(path, runnerEnvironmentCandidateMetaName) {
			return `{"version":"candidate-v1","variables":["PATH"]}`, nil
		}
		if strings.HasSuffix(path, runnerEnvironmentCandidateFileName) || strings.HasSuffix(path, "runner-environment.env") {
			return "PATH=\"/opt/bin\"\n", nil
		}
		return "", os.ErrNotExist
	}
	if code := Run(context.Background(), []string{"runner", "environment", "apply", "--version", "candidate-v2"}, deps); code != ExitUsage {
		t.Fatalf("code=%d stderr=%q", code, errOut.String())
	}
	if !strings.Contains(errOut.String(), "does not match") {
		t.Fatalf("stderr=%q", errOut.String())
	}
}

func TestRunnerEnvironmentApplyRequiresNewProcessAndTargetEnvironment(t *testing.T) {
	home := t.TempDir()
	files := map[string]string{
		filepath.Join(home, ".config", "mohist", runnerEnvironmentCandidateFileName): "PATH=\"/new/bin\"\n",
		filepath.Join(home, ".config", "mohist", runnerEnvironmentCandidateMetaName): `{"version":"candidate-v1","variables":["PATH"]}`,
		filepath.Join(home, ".config", "mohist", "runner-environment.env"):           "PATH=\"/old/bin\"\n",
	}
	deps, out, errOut := testDeps(nil, map[string]string{
		"MOHIST_SERVER_URL": "http://server",
		"MOHIST_TOKEN":      "operator-token",
		"RUNNER_ID":         "runner/pluto",
	})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.MkdirAll = func(string, os.FileMode) error { return nil }
	deps.ReadFile = func(path string) (string, error) {
		value, ok := files[path]
		if !ok {
			return "", os.ErrNotExist
		}
		return value, nil
	}
	deps.WriteFileAtomic = func(path string, value []byte, _ os.FileMode) error {
		files[path] = string(value)
		return nil
	}
	deps.RemoveAll = func(path string) error { delete(files, path); return nil }
	deps.NewID = func() string { return "11111111-1111-4111-8111-111111111111" }
	deps.Now = func() time.Time { return time.Date(2026, 9, 18, 1, 2, 3, 0, time.UTC) }
	var commands [][]string
	deps.Execute = func(_ context.Context, name string, args []string) error {
		commands = append(commands, append([]string{name}, args...))
		return nil
	}
	applyNotSettled := true
	identityCalls := 0
	deps.HTTPClient = &http.Client{Transport: roundTripFunc(func(request *http.Request) (*http.Response, error) {
		switch {
		case request.Method == http.MethodGet && request.URL.Path == "/api/runner/identity":
			identityCalls++
			generation, connection, version := "process-1", "connection-1", "old-version"
			if identityCalls >= 4 {
				generation, connection, version = "process-2", "connection-2", "candidate-v1"
			}
			return response(http.StatusOK, `{"success":true,"data":{"runnerId":"runner/pluto","status":"online","connectionState":"connected","processGeneration":"`+generation+`","connectionGeneration":"`+connection+`","environmentVersion":"`+version+`"}}`), nil
		case request.Method == http.MethodPost && request.URL.Path == "/api/runner/runner/pluto/environment/application":
			return response(http.StatusOK, `{"success":true,"data":{"runnerId":"runner/pluto","updateId":"11111111-1111-4111-8111-111111111111","status":"waiting","application":{"phase":"waiting"}}}`), nil
		case request.Method == http.MethodGet && strings.HasPrefix(request.URL.Path, "/api/runner/runner/pluto/environment/application/"):
			return response(http.StatusOK, `{"success":true,"data":{"runnerId":"runner/pluto","updateId":"11111111-1111-4111-8111-111111111111","status":"accepted","application":{"phase":"waiting","targetVersion":"candidate-v1","previousVersion":"old-version"}}}`), nil
		case request.Method == http.MethodPost && strings.HasSuffix(request.URL.Path, "/apply"):
			if applyNotSettled {
				applyNotSettled = false
				return response(http.StatusConflict, `{"success":false,"error":"Runner work has not settled","code":"environment_application_not_settled","details":{}}`), nil
			}
			return response(http.StatusOK, `{"success":true,"data":{"runnerId":"runner/pluto","updateId":"11111111-1111-4111-8111-111111111111","status":"accepted","application":{"phase":"applying"}}}`), nil
		case request.Method == http.MethodPost && strings.HasSuffix(request.URL.Path, "/confirm"):
			return response(http.StatusOK, `{"success":true,"data":{"runnerId":"runner/pluto","updateId":"11111111-1111-4111-8111-111111111111","status":"accepted","application":{"phase":"active"}}}`), nil
		default:
			t.Fatalf("unexpected request %s %s", request.Method, request.URL.String())
			return nil, nil
		}
	})}
	deps.Wait = func(context.Context, time.Duration) error { return nil }

	if code := Run(context.Background(), []string{"runner", "environment", "apply", "--version", "candidate-v1"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if len(commands) != 1 || strings.Join(commands[0], " ") != "systemctl --user restart mohist-runner.service" {
		t.Fatalf("commands=%#v", commands)
	}
	if files[filepath.Join(home, ".config", "mohist", "runner-environment.env")] != "PATH=\"/new/bin\"\n" {
		t.Fatalf("active snapshot=%q", files[filepath.Join(home, ".config", "mohist", "runner-environment.env")])
	}
	if files[filepath.Join(home, ".config", "mohist", runnerEnvironmentPreviousFileName)] != "PATH=\"/old/bin\"\n" {
		t.Fatalf("previous snapshot=%q", files[filepath.Join(home, ".config", "mohist", runnerEnvironmentPreviousFileName)])
	}
	if _, ok := files[filepath.Join(home, ".config", "mohist", runnerEnvironmentApplicationMetaName)]; ok {
		t.Fatal("application metadata was not removed after confirmation")
	}
	if _, ok := files[filepath.Join(home, ".config", "mohist", runnerEnvironmentCandidateFileName)]; ok {
		t.Fatal("candidate was not removed after confirmation")
	}
	if strings.Contains(out.String(), "operator-token") || strings.Contains(errOut.String(), "operator-token") {
		t.Fatal("credential leaked")
	}
}

func TestRunnerEnvironmentApplicationMetadataIsSanitized(t *testing.T) {
	metadata := runnerEnvironmentApplicationMetadata{UpdateID: "id", TargetVersion: "version", BaseProcessGeneration: "process", BaseConnectionGeneration: "connection"}
	data, err := json.Marshal(metadata)
	if err != nil || strings.Contains(string(data), "PATH=") {
		t.Fatalf("metadata=%s err=%v", data, err)
	}
}

func TestRunnerEnvironmentApplyResumesAfterRestartWithoutSecondRestart(t *testing.T) {
	home := t.TempDir()
	root := filepath.Join(home, ".config", "mohist")
	files := map[string]string{
		filepath.Join(root, runnerEnvironmentCandidateFileName):   "PATH=\"/new/bin\"\n",
		filepath.Join(root, runnerEnvironmentCandidateMetaName):   `{"version":"candidate-v2","variables":["PATH"]}`,
		filepath.Join(root, "runner-environment.env"):             "PATH=\"/old/bin\"\n",
		filepath.Join(root, runnerEnvironmentApplicationMetaName): `{"updateId":"22222222-2222-4222-8222-222222222222","targetVersion":"candidate-v2","baseProcessGeneration":"process-1","baseConnectionGeneration":"connection-1"}`,
	}
	deps, out, errOut := testDeps(nil, map[string]string{
		"MOHIST_SERVER_URL": "http://server",
		"MOHIST_TOKEN":      "operator-token",
		"RUNNER_ID":         "runner/pluto",
	})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.ReadFile = func(path string) (string, error) {
		value, ok := files[path]
		if !ok {
			return "", os.ErrNotExist
		}
		return value, nil
	}
	deps.WriteFileAtomic = func(path string, value []byte, _ os.FileMode) error {
		files[path] = string(value)
		return nil
	}
	deps.RemoveAll = func(path string) error { delete(files, path); return nil }
	deps.Execute = func(context.Context, string, []string) error {
		t.Fatal("recovery must not restart an already activated process")
		return nil
	}
	deps.HTTPClient = &http.Client{Transport: roundTripFunc(func(request *http.Request) (*http.Response, error) {
		switch {
		case request.Method == http.MethodGet && request.URL.Path == "/api/runner/identity":
			return response(http.StatusOK, `{"success":true,"data":{"runnerId":"runner/pluto","status":"online","connectionState":"connected","processGeneration":"process-2","connectionGeneration":"connection-2","environmentVersion":"candidate-v2"}}`), nil
		case request.Method == http.MethodGet && strings.HasSuffix(request.URL.Path, "/environment/application/22222222-2222-4222-8222-222222222222"):
			return response(http.StatusOK, `{"success":true,"data":{"runnerId":"runner/pluto","updateId":"22222222-2222-4222-8222-222222222222","status":"accepted","application":{"phase":"applying","targetVersion":"candidate-v2","previousVersion":"old-v1"}}}`), nil
		case request.Method == http.MethodPost && strings.HasSuffix(request.URL.Path, "/confirm"):
			return response(http.StatusOK, `{"success":true,"data":{"runnerId":"runner/pluto","updateId":"22222222-2222-4222-8222-222222222222","status":"accepted","application":{"phase":"active","targetVersion":"candidate-v2"}}}`), nil
		default:
			t.Fatalf("unexpected recovery request %s %s", request.Method, request.URL.String())
			return nil, nil
		}
	})}

	if code := Run(context.Background(), []string{"runner", "environment", "apply", "--version", "candidate-v2"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if _, ok := files[filepath.Join(root, runnerEnvironmentApplicationMetaName)]; ok {
		t.Fatal("application metadata was not removed after recovery")
	}
	if _, ok := files[filepath.Join(root, runnerEnvironmentCandidateFileName)]; ok {
		t.Fatal("candidate was not removed after recovery")
	}
}

func TestReadRunnerEnvironmentApplicationDoesNotHideFileErrors(t *testing.T) {
	deps, _, _ := testDeps(nil, nil)
	deps.ReadFile = func(string) (string, error) { return "", errors.New("permission denied") }
	paths := map[string]string{"application": "/protected/runner-environment-application.json"}
	_, present, err := readRunnerEnvironmentApplication(deps, paths)
	if err == nil || present || !strings.Contains(err.Error(), "could not be read") {
		t.Fatalf("present=%v err=%v", present, err)
	}
}

func TestReadRunnerEnvironmentCandidateRejectsContentHashMismatch(t *testing.T) {
	deps, _, _ := testDeps(nil, nil)
	deps.ReadFile = func(path string) (string, error) {
		if strings.HasSuffix(path, runnerEnvironmentCandidateMetaName) {
			return `{"version":"candidate-v1","variables":["PATH"],"contentHash":"wrong"}`, nil
		}
		return "PATH=\"/opt/bin\"\n", nil
	}
	paths := map[string]string{"candidate": "/runner-environment.candidate.env", "candidateMeta": "/runner-environment.candidate.json"}
	_, _, err := readRunnerEnvironmentCandidate(deps, paths)
	if err == nil || !strings.Contains(err.Error(), "does not match") {
		t.Fatalf("err=%v", err)
	}
}

func TestDiffRunnerEnvironmentSnapshotsReportsNamesOnly(t *testing.T) {
	diff := diffRunnerEnvironmentSnapshots(
		"PATH=\"/old\"\nGOROOT=\"/go\"\nJAVA_HOME=\"/java\"\n",
		"PATH=\"/new\"\nGOROOT=\"/go\"\nGOPATH=\"/work\"\n",
	)
	if strings.Join(diff.Added, ",") != "GOPATH" || strings.Join(diff.Removed, ",") != "JAVA_HOME" || strings.Join(diff.Changed, ",") != "PATH" {
		t.Fatalf("diff=%+v", diff)
	}
	if strings.Contains(formatRunnerEnvironmentNames(diff.Changed), "/new") {
		t.Fatal("snapshot value leaked from diff")
	}
}

func TestRunnerEnvironmentCheckUsesSnapshotArgvAndReportsNonZeroToolExit(t *testing.T) {
	home := t.TempDir()
	toolDir := t.TempDir()
	toolPath := filepath.Join(toolDir, "go")
	if err := os.WriteFile(toolPath, []byte("#!/bin/sh\nexit 7\n"), 0o700); err != nil {
		t.Fatal(err)
	}
	deps, out, errOut := testDeps(roundTripFunc(func(*http.Request) (*http.Response, error) {
		t.Fatal("local check must not contact Server without --report")
		return nil, nil
	}), map[string]string{
		"PATH":        toolDir,
		"USER":        "runner-user",
		"RUNNER_ROOT": filepath.Join(home, "projects"),
		"SECRET":      "must-not-appear",
	})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.ReadFile = func(path string) (string, error) {
		if strings.HasSuffix(path, "runner-environment.env") {
			return "PATH=\"" + toolDir + "\"\nGOROOT=\"/opt/go\"\n", nil
		}
		return "", os.ErrNotExist
	}
	deps.Now = func() time.Time { return time.Date(2026, 9, 18, 1, 2, 3, 0, time.UTC) }
	var called bool
	deps.ExecuteTool = func(_ context.Context, name string, args []string, directory string, environment []string) (int, time.Duration, error) {
		called = true
		if name != toolPath || strings.Join(args, " ") != "version --short" || directory != filepath.Join(home, "projects") {
			t.Fatalf("tool invocation name=%q args=%q directory=%q", name, args, directory)
		}
		joined := strings.Join(environment, "\n")
		if !strings.Contains(joined, "PATH="+toolDir) || !strings.Contains(joined, "GOROOT=/opt/go") || strings.Contains(joined, "SECRET") {
			t.Fatalf("tool environment=%q", joined)
		}
		return 7, 15 * time.Millisecond, nil
	}

	code := Run(context.Background(), []string{
		"runner", "environment", "check", "go", "--json", "outcome,resolvedPath,exitCode,durationMs", "--", "version", "--short",
	}, deps)
	if code != ExitOK || errOut.Len() != 0 || !called {
		t.Fatalf("code=%d called=%v stdout=%q stderr=%q", code, called, out.String(), errOut.String())
	}
	if !strings.Contains(out.String(), `"outcome":"failed"`) || !strings.Contains(out.String(), `"exitCode":7`) || !strings.Contains(out.String(), toolPath) {
		t.Fatalf("check output=%q", out.String())
	}
	if strings.Contains(out.String(), "must-not-appear") {
		t.Fatal("check output leaked an unrelated environment value")
	}
}

func TestRunnerEnvironmentCheckReportsNotFoundWithoutExecuting(t *testing.T) {
	home := t.TempDir()
	deps, out, errOut := testDeps(nil, map[string]string{})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.ReadFile = func(path string) (string, error) {
		if strings.HasSuffix(path, "runner-environment.env") {
			return "PATH=\"/missing\"\n", nil
		}
		return "", os.ErrNotExist
	}
	called := false
	deps.ExecuteTool = func(context.Context, string, []string, string, []string) (int, time.Duration, error) {
		called = true
		return 0, 0, nil
	}

	if code := Run(context.Background(), []string{"runner", "environment", "check", "go"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if called || !strings.Contains(out.String(), "outcome=not-found") || errOut.Len() != 0 {
		t.Fatalf("called=%v stdout=%q stderr=%q", called, out.String(), errOut.String())
	}
}

func TestRunnerEnvironmentCheckReportsTimeoutWithoutNegativeExitCode(t *testing.T) {
	home := t.TempDir()
	var requestBody string
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		if request.URL.Path == "/api/runner/identity" {
			return response(http.StatusOK, `{"success":true,"data":{"runnerId":"runner-timeout","processGeneration":"process-1","environmentVersion":"env-v1"}}`), nil
		}
		body, _ := io.ReadAll(request.Body)
		requestBody = string(body)
		return response(http.StatusOK, `{"success":true,"data":{"runnerId":"runner-timeout","status":"accepted","observation":{}}}`), nil
	}), map[string]string{
		"MOHIST_SERVER_URL":     "http://server",
		"MOHIST_OPERATOR_TOKEN": "operator-token",
		"RUNNER_ID":             "runner-timeout",
	})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.ReadFile = func(path string) (string, error) {
		if strings.HasSuffix(path, "runner-environment.env") {
			return "PATH=\"/opt/bin\"\n", nil
		}
		return "", os.ErrNotExist
	}
	deps.Now = func() time.Time { return time.Date(2026, 9, 18, 1, 2, 3, 0, time.UTC) }
	deps.ExecuteTool = func(ctx context.Context, _ string, _ []string, _ string, _ []string) (int, time.Duration, error) {
		return 0, 10 * time.Second, context.DeadlineExceeded
	}

	if code := Run(context.Background(), []string{"runner", "environment", "check", "/opt/bin/go", "--report"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if !strings.Contains(out.String(), "outcome=timed-out") || strings.Contains(requestBody, `"exitCode":-1`) {
		t.Fatalf("stdout=%q request=%q", out.String(), requestBody)
	}
}

func TestRunnerEnvironmentCaptureReportContainsOnlySanitizedCandidateMetadata(t *testing.T) {
	home := t.TempDir()
	files := map[string]string{}
	var requestBody string
	deps, out, errOut := testDeps(roundTripFunc(func(request *http.Request) (*http.Response, error) {
		body, _ := io.ReadAll(request.Body)
		requestBody = string(body)
		return response(http.StatusOK, `{"success":true,"data":{"runnerId":"runner-report","status":"accepted","observation":{}}}`), nil
	}), map[string]string{
		"MOHIST_SERVER_URL":     "http://server",
		"MOHIST_OPERATOR_TOKEN": "operator-token",
		"RUNNER_ID":             "runner-report",
		"PATH":                  "/opt/go/bin:/opt/node/bin",
		"GOROOT":                "/opt/go",
		"USER":                  "runner-user",
	})
	deps.HomeDir = func() (string, error) { return home, nil }
	deps.MkdirAll = func(string, os.FileMode) error { return nil }
	deps.ReadFile = func(path string) (string, error) {
		if value, ok := files[path]; ok {
			return value, nil
		}
		if strings.HasSuffix(path, "runner-environment.env") {
			return "PATH=\"/old/bin\"\n", nil
		}
		return "", os.ErrNotExist
	}
	deps.WriteFileAtomic = func(path string, value []byte, _ os.FileMode) error {
		files[path] = string(value)
		return nil
	}
	deps.Now = func() time.Time { return time.Date(2026, 9, 18, 1, 2, 3, 0, time.UTC) }

	if code := Run(context.Background(), []string{"runner", "environment", "capture", "--report"}, deps); code != ExitOK {
		t.Fatalf("code=%d stdout=%q stderr=%q", code, out.String(), errOut.String())
	}
	if strings.Contains(requestBody, "/opt/go") || strings.Contains(requestBody, "operator-token") || strings.Contains(requestBody, "contentHash") {
		t.Fatalf("report leaked raw candidate data: %s", requestBody)
	}
	if !strings.Contains(requestBody, `"source":"terminal"`) || !strings.Contains(requestBody, `"user":"runner-user"`) || !strings.Contains(requestBody, `"version"`) {
		t.Fatalf("report=%s", requestBody)
	}
	if errOut.Len() != 0 {
		t.Fatalf("stderr=%q", errOut.String())
	}
}
