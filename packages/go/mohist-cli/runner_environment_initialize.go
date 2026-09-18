package mohistcli

import (
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

const runnerEnvironmentServiceUnit = "mohist-runner.service"

// initializeRunnerEnvironment migrates a pre-snapshot installation without
// restarting the current Runner. The measured process remains the source of
// truth; the terminal environment is deliberately not consulted.
func initializeRunnerEnvironment(ctx context.Context, deps Dependencies, cmd command) int {
	paths, err := runnerEnvironmentPaths(deps)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	home, err := deps.HomeDir()
	if err != nil || strings.TrimSpace(home) == "" {
		writeError(deps.Stderr, errors.New("home directory is unavailable"))
		return ExitOperation
	}
	unitPath := filepath.Join(home, ".config", "systemd", "user", runnerEnvironmentServiceUnit)
	originalUnit, err := deps.ReadFile(unitPath)
	if err != nil {
		writeError(deps.Stderr, errors.New("managed Runner service unit is unavailable"))
		return ExitOperation
	}
	if err := verifyRunnerEnvironmentFragment(ctx, deps, unitPath); err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}

	pid, measured, err := readRunnerProcessSnapshot(ctx, deps)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}

	activeContent, activeErr := deps.ReadFile(paths["active"])
	activePresent := activeErr == nil
	if activeErr != nil && !errors.Is(activeErr, os.ErrNotExist) {
		writeError(deps.Stderr, fmt.Errorf("active Runner environment snapshot could not be read: %w", activeErr))
		return ExitOperation
	}
	if activePresent {
		activeVersion, err := validateRunnerEnvironmentSnapshotContent(activeContent)
		if err != nil {
			writeError(deps.Stderr, fmt.Errorf("active Runner environment snapshot is invalid: %w", err))
			return ExitOperation
		}
		if activeVersion != measured.Version {
			writeError(deps.Stderr, errors.New("active Runner environment snapshot does not match the running process; refusing to replace it"))
			return ExitOperation
		}
	}

	patchedUnit, unitChanged, err := ensureRunnerEnvironmentFile([]byte(originalUnit))
	if err != nil {
		writeError(deps.Stderr, fmt.Errorf("Runner service unit could not be initialized: %w", err))
		return ExitOperation
	}
	activeWritten := false
	rollback := func(cause error) int {
		recoveryContext := context.Background()
		rollbackErrors := []string{}
		if unitChanged {
			if writeErr := deps.WriteFileAtomic(unitPath, []byte(originalUnit), 0o600); writeErr != nil {
				rollbackErrors = append(rollbackErrors, "service unit")
			} else if reloadErr := deps.Execute(recoveryContext, "systemctl", []string{"--user", "daemon-reload"}); reloadErr != nil {
				rollbackErrors = append(rollbackErrors, "systemd reload")
			}
		}
		if activeWritten && !activePresent {
			if removeErr := deps.RemoveAll(paths["active"]); removeErr != nil {
				rollbackErrors = append(rollbackErrors, "active snapshot")
			}
		}
		if len(rollbackErrors) > 0 {
			writeError(deps.Stderr, fmt.Errorf("%w; initialization rollback failed for %s", cause, strings.Join(rollbackErrors, ", ")))
		} else {
			writeError(deps.Stderr, fmt.Errorf("%w; initialization rolled back", cause))
		}
		return ExitOperation
	}

	if !activePresent {
		if err := deps.WriteFileAtomic(paths["active"], []byte(measured.Content), 0o600); err != nil {
			writeError(deps.Stderr, fmt.Errorf("Runner environment snapshot could not be written: %w", err))
			return ExitOperation
		}
		activeWritten = true
	}
	if unitChanged {
		if err := deps.WriteFileAtomic(unitPath, patchedUnit, 0o600); err != nil {
			return rollback(fmt.Errorf("Runner service unit could not be written: %w", err))
		}
		if err := deps.Execute(ctx, "systemctl", []string{"--user", "daemon-reload"}); err != nil {
			return rollback(fmt.Errorf("Runner service daemon-reload failed: %w", err))
		}
	}

	verifiedPID, verified, err := readRunnerProcessSnapshot(ctx, deps)
	if err != nil {
		return rollback(fmt.Errorf("Runner process environment could not be verified: %w", err))
	}
	if verifiedPID != pid || verified.Version != measured.Version {
		return rollback(errors.New("Runner process environment changed during initialization"))
	}

	if len(cmd.fields) > 0 {
		return writeRunnerEnvironmentFields(deps, map[string]any{
			"initializedVersion":   measured.Version,
			"initializedVariables": measured.Variables,
			"processId":            pid,
			"status":               "initialized",
		}, cmd.fields)
	}
	fmt.Fprintf(deps.Stdout, "Initialized Runner environment %s from process %d.\n", measured.Version, pid)
	if unitChanged {
		fmt.Fprintln(deps.Stdout, "Added the managed environment file to the Runner unit without restarting it.")
	} else {
		fmt.Fprintln(deps.Stdout, "Runner unit already loads the managed environment file.")
	}
	return ExitOK
}

func verifyRunnerEnvironmentFragment(ctx context.Context, deps Dependencies, unitPath string) error {
	output, err := deps.ExecuteOutput(ctx, "systemctl", []string{"--user", "show", runnerEnvironmentServiceUnit, "--property=FragmentPath", "--value"})
	if err != nil || !sameManagedPath(strings.TrimSpace(output), unitPath) {
		return errors.New("managed Runner service unit is not the effective systemd fragment")
	}
	return nil
}

type runnerProcessSnapshot struct {
	Version   string
	Content   string
	Variables []string
}

func readRunnerProcessSnapshot(ctx context.Context, deps Dependencies) (int, runnerProcessSnapshot, error) {
	pid, err := readRunnerProcessID(ctx, deps)
	if err != nil {
		return 0, runnerProcessSnapshot{}, err
	}
	content, err := deps.ReadFile(filepath.Join("/proc", strconv.Itoa(pid), "environ"))
	if err != nil {
		return 0, runnerProcessSnapshot{}, errors.New("Runner process environment is unavailable")
	}
	lookup, err := parseRunnerProcessEnvironment(content)
	if err != nil {
		return 0, runnerProcessSnapshot{}, err
	}
	snapshot, present, err := captureRunnerEnvironment(lookup)
	if err != nil {
		return 0, runnerProcessSnapshot{}, err
	}
	if !present || snapshot.Version == "" {
		return 0, runnerProcessSnapshot{}, errors.New("Runner process has no usable allowlisted environment")
	}
	return pid, runnerProcessSnapshot{Version: snapshot.Version, Content: snapshot.Content, Variables: snapshot.Variables}, nil
}

func readRunnerProcessID(ctx context.Context, deps Dependencies) (int, error) {
	value, err := deps.ExecuteOutput(ctx, "systemctl", []string{"--user", "show", runnerEnvironmentServiceUnit, "--property=MainPID", "--value"})
	if err != nil {
		return 0, errors.New("Runner process identity could not be observed")
	}
	fields := strings.Fields(value)
	if len(fields) != 1 {
		return 0, errors.New("Runner process identity is invalid")
	}
	pid, err := strconv.Atoi(fields[0])
	if err != nil || pid <= 1 {
		return 0, errors.New("Runner service is not running")
	}
	return pid, nil
}

func parseRunnerProcessEnvironment(content string) (EnvLookup, error) {
	values := map[string]string{}
	for _, entry := range strings.Split(content, "\x00") {
		if entry == "" {
			continue
		}
		name, value, ok := strings.Cut(entry, "=")
		if !ok || name == "" {
			continue
		}
		if name != "TMPDIR" {
			if _, allowlisted := runnerEnvironmentAllowlistedNames[name]; !allowlisted {
				continue
			}
		}
		if _, duplicate := values[name]; duplicate {
			return nil, fmt.Errorf("Runner process environment contains duplicate %s", name)
		}
		values[name] = value
	}
	return func(name string) (string, bool) {
		value, ok := values[name]
		return value, ok
	}, nil
}

var runnerEnvironmentAllowlistedNames = func() map[string]struct{} {
	values := make(map[string]struct{}, len(runnerEnvironmentAllowlist))
	for _, name := range runnerEnvironmentAllowlist {
		values[name] = struct{}{}
	}
	return values
}()

func validateRunnerEnvironmentSnapshotContent(content string) (string, error) {
	assignments := runnerEnvironmentAssignments(content)
	if len(assignments) == 0 {
		return "", errors.New("snapshot is empty")
	}
	for name := range assignments {
		if _, ok := runnerEnvironmentAllowlistedNames[name]; !ok {
			return "", fmt.Errorf("snapshot contains unsupported variable %s", name)
		}
	}
	lookup := func(name string) (string, bool) {
		value, ok := assignments[name]
		return value, ok
	}
	snapshot, present, err := captureRunnerEnvironment(lookup)
	if err != nil {
		return "", err
	}
	if !present {
		return "", errors.New("snapshot contains no usable allowlisted values")
	}
	return snapshot.Version, nil
}

func ensureRunnerEnvironmentFile(unit []byte) ([]byte, bool, error) {
	lines := splitManagedUnitLines(unit)
	serviceSections := 0
	inService := false
	serviceStart := -1
	execStart := -1
	found := 0
	changed := false
	resultLines := make([]managedUnitLine, 0, len(lines)+1)
	for index, line := range lines {
		body := string(line.body)
		trimmed := strings.TrimSpace(body)
		if strings.HasPrefix(trimmed, "[") && strings.HasSuffix(trimmed, "]") {
			inService = trimmed == "[Service]"
			if inService {
				serviceSections++
				serviceStart = index
			}
			resultLines = append(resultLines, line)
			continue
		}
		if !inService {
			resultLines = append(resultLines, line)
			continue
		}
		key, value, valueOffset, ok := managedUnitDirective(body)
		if !ok {
			resultLines = append(resultLines, line)
			continue
		}
		switch key {
		case "ExecStart":
			execStart = index
		case "Environment":
			if hasManagedLineContinuation(body) {
				return nil, false, errors.New("Runner unit contains an ambiguous continued Environment directive")
			}
			words, err := splitManagedSystemdWords(value)
			if err != nil {
				return nil, false, errors.New("Runner unit contains an invalid Environment directive")
			}
			remaining := make([]string, 0, len(words))
			for _, word := range words {
				name, _, hasValue := strings.Cut(word.value, "=")
				if !hasValue {
					name = word.value
				}
				if name == "" {
					remaining = append(remaining, value[word.start:word.end])
					continue
				}
				if _, allowlisted := runnerEnvironmentAllowlistedNames[name]; allowlisted {
					changed = true
					continue
				}
				remaining = append(remaining, value[word.start:word.end])
			}
			if len(remaining) == 0 && len(words) > 0 {
				continue
			}
			if len(remaining) != len(words) {
				prefix := body[:valueOffset]
				line = managedUnitLine{body: []byte(prefix + strings.Join(remaining, " ")), ending: line.ending}
			}
		case "EnvironmentFile":
			if hasManagedLineContinuation(body) && strings.Contains(value, "runner-environment") {
				return nil, false, errors.New("Runner unit contains an ambiguous environment file directive")
			}
			words, err := splitManagedSystemdWords(value)
			if err != nil {
				return nil, false, errors.New("Runner unit contains an invalid environment file directive")
			}
			for _, word := range words {
				candidate := strings.TrimPrefix(word.value, "-")
				if candidate == runnerEnvironmentSnapshotFile {
					found++
				} else if strings.Contains(candidate, "runner-environment.env") {
					return nil, false, errors.New("Runner unit contains a non-canonical environment snapshot path")
				}
			}
		}
		resultLines = append(resultLines, line)
	}
	if serviceSections != 1 || serviceStart < 0 || execStart < 0 {
		return nil, false, errors.New("Runner unit must contain one [Service] section and ExecStart")
	}
	if found > 1 {
		return nil, false, errors.New("Runner unit contains multiple managed environment snapshot directives")
	}
	if found == 0 {
		// Recalculate the insertion point after any legacy Environment lines were
		// removed. The directive still belongs immediately before ExecStart.
		execStart = -1
		inService = false
		for index, line := range resultLines {
			trimmed := strings.TrimSpace(string(line.body))
			if strings.HasPrefix(trimmed, "[") && strings.HasSuffix(trimmed, "]") {
				inService = trimmed == "[Service]"
				continue
			}
			if inService {
				key, _, _, ok := managedUnitDirective(string(line.body))
				if ok && key == "ExecStart" {
					execStart = index
					break
				}
			}
		}
		if execStart < 0 {
			return nil, false, errors.New("Runner unit ExecStart disappeared during normalization")
		}
		ending := resultLines[execStart].ending
		if len(ending) == 0 {
			ending = managedUnitNewline(resultLines)
		}
		prefix := managedDirectivePrefix(string(resultLines[execStart].body))
		inserted := managedUnitLine{
			body:   []byte(prefix + "EnvironmentFile=-" + runnerEnvironmentSnapshotFile),
			ending: ending,
		}
		resultLines = append(resultLines, managedUnitLine{})
		copy(resultLines[execStart+1:], resultLines[execStart:])
		resultLines[execStart] = inserted
		changed = true
	}

	var result strings.Builder
	for _, line := range resultLines {
		result.Write(line.body)
		result.Write(line.ending)
	}
	if !changed {
		return append([]byte(nil), unit...), false, nil
	}
	return []byte(result.String()), true, nil
}
