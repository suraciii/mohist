package mohistcli

import (
	"errors"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"testing"
)

var doctorCommandOccurrence = regexp.MustCompile(`\bmo[[:space:]]+([A-Za-z0-9_-]+([[:space:]]+[A-Za-z0-9_-]+)*([[:space:]]+--help)?)([[:space:]]|[.,;:!?)]|$)`)

func TestDoctorGuidanceCommandsResolveToCLILeaves(t *testing.T) {
	_, file, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatal("resolve test file path")
	}
	file, err := filepath.Abs(file)
	if err != nil {
		t.Fatalf("resolve absolute test file path: %v", err)
	}
	repoRoot := filepath.Clean(filepath.Join(filepath.Dir(file), "..", "..", ".."))
	sourcePath := filepath.Join(repoRoot, "packages", "server", "src", "Mohist.Server", "SystemInfo", "DoctorCheckService.cs")
	source, err := os.ReadFile(sourcePath)
	if errors.Is(err, os.ErrNotExist) {
		t.Skipf("Doctor guidance source is missing: %s", sourcePath)
	}
	if err != nil {
		t.Fatalf("read Doctor guidance source %s: %v", sourcePath, err)
	}

	matches := doctorCommandOccurrence.FindAllStringSubmatch(string(source), -1)
	if len(matches) == 0 {
		t.Fatal("DoctorCheckService.cs contains no mo command recommendations")
	}
	for _, match := range matches {
		candidate := strings.Fields(match[1])
		args, ok := resolveDoctorCommand(candidate)
		if !ok {
			t.Errorf("DoctorCheckService.cs command %q does not resolve to a CLI leaf", strings.Join(candidate, " "))
			continue
		}
		t.Run(strings.Join(args, " "), func(t *testing.T) {
			assertCommandHelpNamesLeaf(t, args)
		})
	}

	assertCommandHelpNamesLeaf(t, []string{"doctor"})
}

func resolveDoctorCommand(candidate []string) ([]string, bool) {
	path := commandPath(candidate)
	if len(path) == 0 {
		return nil, false
	}
	if _, err := parse(path); err != nil || !helpNamesLeaf(path) {
		return nil, false
	}
	return append([]string(nil), path...), true
}

func commandPath(args []string) []string {
	for i, arg := range args {
		if strings.HasPrefix(arg, "-") {
			return args[:i]
		}
	}
	return args
}

func helpNamesLeaf(path []string) bool {
	if len(path) == 0 {
		return false
	}
	help, err := parse(append(append([]string(nil), path...), "--help"))
	if err != nil || !help.help {
		return false
	}
	usagePrefix := "mo " + strings.Join(path, " ")
	for _, line := range strings.Split(help.helpText, "\n") {
		line = strings.TrimSpace(line)
		if line == usagePrefix || strings.HasPrefix(line, usagePrefix+" [") {
			return true
		}
	}
	return false
}

func TestDoctorGuidanceRejectsRemovedProjectCommand(t *testing.T) {
	if _, ok := resolveDoctorCommand([]string{"project", "set-verification-command"}); ok {
		t.Fatal("removed Project command resolved to a CLI leaf")
	}
}

func assertCommandHelpNamesLeaf(t *testing.T, args []string) {
	t.Helper()
	if _, err := parse(args); err != nil {
		t.Fatalf("parse(%q): %v", args, err)
	}
	path := commandPath(args)
	if !helpNamesLeaf(path) {
		t.Fatalf("help for %q does not name the intended command leaf %q", args, "mo "+strings.Join(path, " "))
	}
}
