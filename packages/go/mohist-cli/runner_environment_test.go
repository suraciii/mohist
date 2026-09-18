package mohistcli

import (
	"crypto/sha256"
	"encoding/hex"
	"strings"
	"testing"
)

func TestCaptureRunnerEnvironmentFiltersPathAndProducesStableVersion(t *testing.T) {
	env := map[string]string{
		"PATH":         "/opt/go/bin:/tmp/injected:/opt/go/bin:relative:/var/tmp/tool:/run/user/1000/bin:/opt/node/bin",
		"TMPDIR":       "/tmp/session",
		"GOROOT":       "/opt/go",
		"GOPATH":       "/opt/go-work",
		"MOHIST_TOKEN": "must-not-be-captured",
	}
	lookup := func(name string) (string, bool) { value, ok := env[name]; return value, ok }

	snapshot, present, err := captureRunnerEnvironment(lookup)
	if err != nil {
		t.Fatal(err)
	}
	if !present {
		t.Fatal("environment was not captured")
	}
	wantContent := "GOPATH=\"/opt/go-work\"\nGOROOT=\"/opt/go\"\nPATH=\"/opt/go/bin:/opt/node/bin\"\n"
	if snapshot.Content != wantContent {
		t.Fatalf("content = %q, want %q", snapshot.Content, wantContent)
	}
	if strings.Contains(snapshot.Content, "MOHIST_TOKEN") || strings.Contains(snapshot.Content, "/tmp/") {
		t.Fatalf("unsafe environment value was captured: %q", snapshot.Content)
	}
	canonical := "GOPATH=/opt/go-work\nGOROOT=/opt/go\nPATH=/opt/go/bin:/opt/node/bin\n"
	digest := sha256.Sum256([]byte(canonical))
	if snapshot.Version != hex.EncodeToString(digest[:]) {
		t.Fatalf("version = %q, want %q", snapshot.Version, hex.EncodeToString(digest[:]))
	}
	if len(snapshot.Variables) != 3 || snapshot.Variables[0] != "GOPATH" || snapshot.Variables[1] != "GOROOT" || snapshot.Variables[2] != "PATH" {
		t.Fatalf("variables = %#v", snapshot.Variables)
	}
}

func TestCaptureRunnerEnvironmentRejectsUnsafeToolPath(t *testing.T) {
	for name, value := range map[string]string{
		"relative": "go",
		"newline":  "/opt/go\nINJECTED=1",
		"nul":      "/opt/go\x00tail",
	} {
		t.Run(name, func(t *testing.T) {
			snapshot, present, err := captureRunnerEnvironment(func(key string) (string, bool) {
				if key == "GOROOT" {
					return value, true
				}
				return "", false
			})
			if err == nil {
				t.Fatalf("expected error, snapshot=%#v present=%v", snapshot, present)
			}
		})
	}
}

func TestCaptureRunnerEnvironmentWithoutAllowlistedValuesIsAbsent(t *testing.T) {
	snapshot, present, err := captureRunnerEnvironment(func(string) (string, bool) { return "", false })
	if err != nil {
		t.Fatal(err)
	}
	if present || snapshot.Content != "" || snapshot.Version != "" {
		t.Fatalf("snapshot = %#v, present = %v", snapshot, present)
	}
}

func TestCaptureRunnerEnvironmentRejectsUnavailableLookup(t *testing.T) {
	_, _, err := captureRunnerEnvironment(nil)
	if err == nil || !strings.Contains(err.Error(), "lookup is unavailable") {
		t.Fatal("expected lookup error")
	}
}

func TestRunnerTemporaryPathUsesConfiguredTempDirectory(t *testing.T) {
	if !runnerTemporaryPath("/tmp/session/bin", "/opt/tmp") {
		t.Fatal("system temporary path was not excluded")
	}
	if !runnerTemporaryPath("/opt/tmp/session/bin", "/opt/tmp") {
		t.Fatal("configured temporary path was not excluded")
	}
	if runnerTemporaryPath("/opt/tmp-tools/bin", "/opt/tmp") {
		t.Fatal("similar path was incorrectly excluded")
	}
}
