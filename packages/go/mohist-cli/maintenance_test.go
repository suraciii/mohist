package mohistcli

import (
	"bytes"
	"context"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func writeTestSkill(t *testing.T, root, name string) {
	t.Helper()
	path := filepath.Join(root, name, "SKILL.md")
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte("---\nname: "+name+"\ndescription: test skill\n---\n\nbody\n"), 0o600); err != nil {
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
