package mohistcli

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"path/filepath"
	"sort"
	"strings"
)

var runnerEnvironmentAllowlist = []string{
	"PATH",
	"DOTNET_ROOT",
	"DOTNET_ROOT_X64",
	"GOROOT",
	"GOPATH",
	"JAVA_HOME",
	"ANDROID_HOME",
	"ANDROID_SDK_ROOT",
	"M2_HOME",
	"GRADLE_HOME",
	"NVM_BIN",
	"NVM_PATH",
}

type runnerEnvironmentSnapshot struct {
	Version   string
	Content   string
	Variables []string
}

// captureRunnerEnvironment turns the invoking process environment into the
// small, deterministic file that systemd loads for the Runner. A missing
// allowlisted environment is valid and produces an empty snapshot.
func captureRunnerEnvironment(lookup EnvLookup) (runnerEnvironmentSnapshot, bool, error) {
	if lookup == nil {
		return runnerEnvironmentSnapshot{}, false, errors.New("runner environment lookup is unavailable")
	}

	values := map[string]string{}
	for _, name := range runnerEnvironmentAllowlist {
		value, ok := lookup(name)
		if !ok || value == "" {
			continue
		}
		if name == "PATH" {
			filtered := filterRunnerPath(value, lookupValue(lookup, "TMPDIR"))
			if filtered != "" {
				values[name] = filtered
			}
			continue
		}
		if strings.ContainsAny(value, "\r\n\x00") {
			return runnerEnvironmentSnapshot{}, false, fmt.Errorf("runner environment variable %s contains an unsupported control character", name)
		}
		if !filepath.IsAbs(value) {
			return runnerEnvironmentSnapshot{}, false, fmt.Errorf("runner environment variable %s must be an absolute path", name)
		}
		values[name] = filepath.Clean(value)
	}
	if len(values) == 0 {
		return runnerEnvironmentSnapshot{}, false, nil
	}

	names := make([]string, 0, len(values))
	for name := range values {
		names = append(names, name)
	}
	sort.Strings(names)

	canonical := strings.Builder{}
	content := strings.Builder{}
	for _, name := range names {
		value := values[name]
		canonical.WriteString(name)
		canonical.WriteByte('=')
		canonical.WriteString(value)
		canonical.WriteByte('\n')
		assignment, err := systemdEnvironmentAssignment(name, value)
		if err != nil {
			return runnerEnvironmentSnapshot{}, false, err
		}
		content.WriteString(assignment)
	}
	digest := sha256.Sum256([]byte(canonical.String()))
	return runnerEnvironmentSnapshot{
		Version:   hex.EncodeToString(digest[:]),
		Content:   content.String(),
		Variables: names,
	}, true, nil
}

func lookupValue(lookup EnvLookup, name string) string {
	value, _ := lookup(name)
	return value
}

func filterRunnerPath(value, tmpDir string) string {
	seen := map[string]struct{}{}
	entries := make([]string, 0)
	for _, raw := range strings.Split(value, ":") {
		entry := raw
		if entry == "" || !filepath.IsAbs(entry) {
			continue
		}
		entry = filepath.Clean(entry)
		if runnerTemporaryPath(entry, tmpDir) {
			continue
		}
		if _, exists := seen[entry]; exists {
			continue
		}
		seen[entry] = struct{}{}
		entries = append(entries, entry)
	}
	return strings.Join(entries, ":")
}

func runnerTemporaryPath(path, tmpDir string) bool {
	for _, prefix := range []string{"/tmp/", "/var/tmp/", "/run/user/"} {
		if strings.HasPrefix(path, prefix) {
			return true
		}
	}
	if tmpDir == "" || !filepath.IsAbs(tmpDir) {
		return false
	}
	tmpDir = strings.TrimRight(filepath.Clean(tmpDir), string(filepath.Separator)) + string(filepath.Separator)
	return strings.HasPrefix(path, tmpDir)
}
