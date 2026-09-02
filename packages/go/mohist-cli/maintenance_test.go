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
