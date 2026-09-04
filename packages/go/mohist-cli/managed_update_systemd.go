package mohistcli

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"path/filepath"
	"strings"
)

const managedRuntimeIdentityEnvironment = "MOHIST_RUNTIME_IDENTITY_PATH"

func captureManagedService(
	ctx context.Context,
	env managedUpdateEnvironment,
	component string,
	unitDir string,
	snapshotPath string,
	previousTarget *managedRuntimeTarget,
) (*managedServiceSnapshot, error) {
	unitName, err := managedUnitName(component)
	if err != nil {
		return nil, err
	}
	unitPath := filepath.Join(unitDir, unitName)
	unit, mode, err := env.files.ReadFile(unitPath)
	if err != nil {
		return nil, fmt.Errorf("managed %s service unit is unavailable", component)
	}
	if containsManagedInlineCredential(unit) {
		return nil, fmt.Errorf("managed %s service unit contains an inline credential; move it to an EnvironmentFile before updating", component)
	}
	absoluteUnitPath, err := filepath.Abs(unitPath)
	if err != nil {
		return nil, fmt.Errorf("managed %s service unit path is invalid", component)
	}
	fragmentPath, err := readManagedSystemdProperty(ctx, env.commands, unitName, "FragmentPath")
	if err != nil || strings.TrimSpace(fragmentPath) == "" || !sameManagedPath(absoluteUnitPath, strings.TrimSpace(fragmentPath)) {
		return nil, fmt.Errorf("managed %s service unit is not the effective systemd fragment", component)
	}
	if err := validateManagedUnitTarget(unit, previousTarget); err != nil {
		return nil, fmt.Errorf("managed %s service unit does not match its verified target", component)
	}
	if err := verifyManagedEffectiveTarget(ctx, env.commands, unitName, previousTarget); err != nil {
		return nil, fmt.Errorf("managed %s service effective target does not match its verified target", component)
	}
	active, err := readManagedServiceState(ctx, env.commands, unitName, "is-active")
	if err != nil {
		return nil, err
	}
	enabled, err := readManagedServiceState(ctx, env.commands, unitName, "is-enabled")
	if err != nil {
		return nil, err
	}
	if err := env.files.WriteFileAtomic(snapshotPath, unit, 0o600); err != nil {
		return nil, fmt.Errorf("managed %s service snapshot could not be persisted", component)
	}
	return &managedServiceSnapshot{
		Component:      component,
		UnitPath:       unitPath,
		UnitSnapshot:   snapshotPath,
		UnitMode:       mode.Perm(),
		WasActive:      active,
		WasEnabled:     enabled,
		PreviousTarget: previousTarget,
	}, nil
}

func containsManagedInlineCredential(unit []byte) bool {
	inService := false
	for _, line := range splitManagedUnitLines(unit) {
		body := string(line.body)
		trimmed := strings.TrimSpace(body)
		if strings.HasPrefix(trimmed, "[") && strings.HasSuffix(trimmed, "]") {
			inService = trimmed == "[Service]"
			continue
		}
		if !inService {
			continue
		}
		key, value, _, ok := managedUnitDirective(body)
		if !ok {
			continue
		}
		if key == "SetCredential" || key == "SetCredentialEncrypted" {
			return true
		}
		if key != "Environment" && key != "ExecStart" {
			continue
		}
		if hasManagedLineContinuation(body) {
			return true
		}
		words, err := splitManagedSystemdWords(value)
		if err != nil {
			return containsManagedCredentialMarker(value)
		}
		for index, word := range words {
			name, hasValue := managedCredentialArgument(word.value)
			if !isManagedCredentialName(name) {
				continue
			}
			if hasValue || (key == "ExecStart" && index+1 < len(words)) {
				return true
			}
		}
	}
	return false
}

func validateManagedUnitTarget(unit []byte, target *managedRuntimeTarget) error {
	if err := validateManagedTarget(target); err != nil {
		return err
	}
	expectedCommand, _, err := managedExecStart(target)
	if err != nil {
		return err
	}
	expectedWords, err := splitManagedSystemdWords(expectedCommand)
	if err != nil {
		return err
	}
	wantIdentity, err := managedTargetIdentityPath(target)
	if err != nil {
		return err
	}

	inService := false
	serviceSections := 0
	workingDirectoryMatches := 0
	execMatches := 0
	identityMatches := 0
	for _, line := range splitManagedUnitLines(unit) {
		body := string(line.body)
		trimmed := strings.TrimSpace(body)
		if strings.HasPrefix(trimmed, "[") && strings.HasSuffix(trimmed, "]") {
			inService = trimmed == "[Service]"
			if inService {
				serviceSections++
			}
			continue
		}
		if !inService {
			continue
		}
		key, value, _, ok := managedUnitDirective(body)
		if !ok || hasManagedLineContinuation(body) {
			continue
		}
		switch key {
		case "WorkingDirectory":
			workingDirectory, pathErr := parseManagedSystemdWorkingDirectory(value)
			if pathErr != nil {
				return pathErr
			}
			if workingDirectory == target.WorkingDirectory {
				workingDirectoryMatches++
			}
		case "ExecStart":
			words, wordErr := splitManagedSystemdWords(value)
			if wordErr != nil {
				return wordErr
			}
			if managedWordsEqual(words, expectedWords) {
				execMatches++
			}
		case "Environment":
			words, wordErr := splitManagedSystemdWords(value)
			if wordErr != nil {
				return wordErr
			}
			for _, word := range words {
				if word.value == managedRuntimeIdentityEnvironment+"="+wantIdentity {
					identityMatches++
				}
			}
		}
	}
	if serviceSections != 1 || workingDirectoryMatches != 1 || execMatches != 1 || identityMatches != 1 {
		return errors.New("managed service unit target is not exact")
	}
	return nil
}

func managedWordsEqual(left, right []managedWordSpan) bool {
	if len(left) != len(right) {
		return false
	}
	for index := range left {
		if left[index].value != right[index].value {
			return false
		}
	}
	return true
}

func managedCredentialArgument(value string) (string, bool) {
	argument := strings.TrimLeft(value, "-")
	name, _, hasValue := strings.Cut(argument, "=")
	name = strings.ToUpper(strings.ReplaceAll(name, "-", "_"))
	return name, hasValue
}

func isManagedCredentialName(name string) bool {
	if name == "" || strings.HasSuffix(name, "_PATH") || strings.HasSuffix(name, "_FILE") {
		return false
	}
	for _, marker := range []string{"TOKEN", "PASSWORD", "PASSPHRASE", "SECRET", "CREDENTIAL", "PRIVATE_KEY", "API_KEY", "ACCESS_KEY"} {
		if strings.Contains(name, marker) {
			return true
		}
	}
	return false
}

func containsManagedCredentialMarker(value string) bool {
	upper := strings.ToUpper(strings.ReplaceAll(value, "-", "_"))
	for _, marker := range []string{"TOKEN=", "PASSWORD=", "PASSPHRASE=", "SECRET=", "CREDENTIAL=", "PRIVATE_KEY=", "API_KEY=", "ACCESS_KEY="} {
		if strings.Contains(upper, marker) {
			return true
		}
	}
	return false
}

func patchManagedSystemdUnit(unit []byte, target *managedRuntimeTarget) ([]byte, error) {
	if err := validateManagedTarget(target); err != nil {
		return nil, err
	}
	workingDirectory, err := formatManagedSystemdWorkingDirectory(target.WorkingDirectory)
	if err != nil {
		return nil, err
	}
	execStart, _, err := managedExecStart(target)
	if err != nil {
		return nil, err
	}
	identityPath, err := managedTargetIdentityPath(target)
	if err != nil {
		return nil, err
	}
	identityAssignment, err := quoteManagedSystemdValue(managedRuntimeIdentityEnvironment + "=" + identityPath)
	if err != nil {
		return nil, err
	}

	lines := splitManagedUnitLines(unit)
	serviceSections := 0
	inService := false
	workingDirectoryLine := -1
	execStartLine := -1
	identityLine := -1
	identityWord := managedWordSpan{}
	identityMatches := 0

	for index, line := range lines {
		body := string(line.body)
		trimmed := strings.TrimSpace(body)
		if strings.HasPrefix(trimmed, "[") && strings.HasSuffix(trimmed, "]") {
			inService = trimmed == "[Service]"
			if inService {
				serviceSections++
			}
			continue
		}
		if !inService {
			continue
		}
		key, value, valueOffset, ok := managedUnitDirective(body)
		if !ok {
			continue
		}
		switch key {
		case "WorkingDirectory":
			if workingDirectoryLine >= 0 || hasManagedLineContinuation(body) {
				return nil, errors.New("managed service unit must contain one simple WorkingDirectory directive")
			}
			workingDirectoryLine = index
		case "ExecStart":
			if execStartLine >= 0 || hasManagedLineContinuation(body) {
				return nil, errors.New("managed service unit must contain one simple ExecStart directive")
			}
			execStartLine = index
		case "Environment":
			if !strings.Contains(value, managedRuntimeIdentityEnvironment) {
				continue
			}
			if hasManagedLineContinuation(body) && strings.Contains(value, managedRuntimeIdentityEnvironment) {
				return nil, errors.New("managed runtime identity environment must not use a continued directive")
			}
			words, wordErr := splitManagedSystemdWords(value)
			if wordErr != nil {
				return nil, errors.New("managed service unit contains an invalid Environment directive")
			}
			for _, word := range words {
				if strings.HasPrefix(word.value, managedRuntimeIdentityEnvironment+"=") {
					identityMatches++
					identityLine = index
					identityWord = managedWordSpan{
						start: valueOffset + word.start,
						end:   valueOffset + word.end,
					}
				}
			}
		}
	}

	if serviceSections != 1 {
		return nil, errors.New("managed service unit must contain exactly one [Service] section")
	}
	if workingDirectoryLine < 0 {
		return nil, errors.New("managed service unit must contain one WorkingDirectory directive")
	}
	if execStartLine < 0 {
		return nil, errors.New("managed service unit must contain one ExecStart directive")
	}
	if identityMatches > 1 {
		return nil, errors.New("managed service unit contains multiple runtime identity settings")
	}

	workingPrefix := managedDirectivePrefix(string(lines[workingDirectoryLine].body))
	execPrefix := managedDirectivePrefix(string(lines[execStartLine].body))
	lines[workingDirectoryLine].body = []byte(workingPrefix + "WorkingDirectory=" + workingDirectory)
	lines[execStartLine].body = []byte(execPrefix + "ExecStart=" + execStart)
	if identityMatches == 1 {
		body := lines[identityLine].body
		lines[identityLine].body = bytes.Join([][]byte{
			body[:identityWord.start],
			[]byte(identityAssignment),
			body[identityWord.end:],
		}, nil)
	} else {
		ending := lines[execStartLine].ending
		if len(ending) == 0 {
			ending = managedUnitNewline(lines)
		}
		identity := managedUnitLine{
			body:   []byte(execPrefix + "Environment=" + identityAssignment),
			ending: ending,
		}
		lines = append(lines, managedUnitLine{})
		copy(lines[execStartLine+1:], lines[execStartLine:])
		lines[execStartLine] = identity
	}

	var patched bytes.Buffer
	for _, line := range lines {
		patched.Write(line.body)
		patched.Write(line.ending)
	}
	return patched.Bytes(), nil
}

func activateManagedService(
	ctx context.Context,
	env managedUpdateEnvironment,
	snapshot *managedServiceSnapshot,
	target *managedRuntimeTarget,
) (bool, error) {
	unitName, err := validateManagedServiceSnapshot(snapshot)
	if err != nil {
		return false, err
	}
	if target == nil || target.Component != snapshot.Component {
		return false, errors.New("managed service target does not match its snapshot")
	}
	original, snapshotMode, err := env.files.ReadFile(snapshot.UnitSnapshot)
	if err != nil || snapshotMode.Perm() != 0o600 {
		return false, fmt.Errorf("managed %s service snapshot is unavailable", snapshot.Component)
	}
	current, currentMode, err := env.files.ReadFile(snapshot.UnitPath)
	if err != nil {
		return false, fmt.Errorf("managed %s service unit is unavailable", snapshot.Component)
	}
	if !bytes.Equal(current, original) || currentMode.Perm() != snapshot.UnitMode.Perm() {
		return false, fmt.Errorf("managed %s service unit changed after capture", snapshot.Component)
	}
	patched, err := patchManagedSystemdUnit(original, target)
	if err != nil {
		return false, err
	}
	if err := env.files.WriteFileAtomic(snapshot.UnitPath, patched, snapshot.UnitMode); err != nil {
		return true, fmt.Errorf("managed %s service unit could not be activated", snapshot.Component)
	}
	if err := runManagedSystemctl(ctx, env.commands, "daemon-reload"); err != nil {
		return true, fmt.Errorf("managed %s service reload failed", snapshot.Component)
	}
	if err := runManagedSystemctl(ctx, env.commands, "restart", unitName); err != nil {
		return true, fmt.Errorf("managed %s service restart failed", snapshot.Component)
	}
	if err := verifyManagedEffectiveTarget(ctx, env.commands, unitName, target); err != nil {
		return true, err
	}
	return true, nil
}

func restoreManagedService(
	ctx context.Context,
	env managedUpdateEnvironment,
	snapshot *managedServiceSnapshot,
) error {
	unitName, err := validateManagedServiceSnapshot(snapshot)
	if err != nil {
		return err
	}
	original, mode, err := env.files.ReadFile(snapshot.UnitSnapshot)
	if err != nil || mode.Perm() != 0o600 {
		return fmt.Errorf("managed %s service snapshot is unavailable", snapshot.Component)
	}
	if err := env.files.WriteFileAtomic(snapshot.UnitPath, original, snapshot.UnitMode); err != nil {
		return fmt.Errorf("managed %s service unit could not be restored", snapshot.Component)
	}
	restoreFailed := false
	if err := runManagedSystemctl(ctx, env.commands, "daemon-reload"); err != nil {
		restoreFailed = true
	}
	stateCommand := "disable"
	if snapshot.WasEnabled {
		stateCommand = "enable"
	}
	if err := runManagedSystemctl(ctx, env.commands, stateCommand, unitName); err != nil {
		restoreFailed = true
	}
	lifecycleCommand := "stop"
	if snapshot.WasActive {
		lifecycleCommand = "restart"
	}
	if err := runManagedSystemctl(ctx, env.commands, lifecycleCommand, unitName); err != nil {
		restoreFailed = true
	}
	active, err := readManagedServiceState(ctx, env.commands, unitName, "is-active")
	if err != nil || active != snapshot.WasActive {
		restoreFailed = true
	}
	enabled, err := readManagedServiceState(ctx, env.commands, unitName, "is-enabled")
	if err != nil || enabled != snapshot.WasEnabled {
		restoreFailed = true
	}
	if restoreFailed {
		return fmt.Errorf("managed %s service state was not fully restored", snapshot.Component)
	}
	return nil
}

func validateManagedServiceSnapshot(snapshot *managedServiceSnapshot) (string, error) {
	if snapshot == nil || snapshot.UnitPath == "" || snapshot.UnitSnapshot == "" {
		return "", errors.New("managed service snapshot is incomplete")
	}
	unitName, err := managedUnitName(snapshot.Component)
	if err != nil {
		return "", err
	}
	if filepath.Base(snapshot.UnitPath) != unitName || strings.Contains(snapshot.UnitPath, unitName+".d") {
		return "", fmt.Errorf("managed %s service snapshot has an invalid unit path", snapshot.Component)
	}
	return unitName, nil
}

func managedUnitName(component string) (string, error) {
	switch component {
	case "server":
		return "mohist.service", nil
	case "runner":
		return "mohist-runner.service", nil
	default:
		return "", fmt.Errorf("unsupported managed service component %q", component)
	}
}

func validateManagedTarget(target *managedRuntimeTarget) error {
	if target == nil || target.Component == "" {
		return errors.New("managed service target is incomplete")
	}
	if !filepath.IsAbs(target.WorkingDirectory) || !filepath.IsAbs(target.Entrypoint) {
		return fmt.Errorf("managed %s service target paths must be absolute", target.Component)
	}
	if !target.IsAbsoluteTarget {
		return fmt.Errorf("managed %s service target is not trusted", target.Component)
	}
	if target.Component == "runner" && !target.UsesCanonicalEntrypoint {
		return errors.New("managed runner service target does not use the canonical entrypoint")
	}
	return nil
}

func managedTargetIdentityPath(target *managedRuntimeTarget) (string, error) {
	root := target.WorkingDirectory
	if target.Component == "runner" && target.DependencyRoot != nil {
		root = *target.DependencyRoot
	}
	if !filepath.IsAbs(root) {
		return "", fmt.Errorf("managed %s runtime identity root must be absolute", target.Component)
	}
	return filepath.Join(root, "runtime-identity.json"), nil
}

func managedExecStart(target *managedRuntimeTarget) (string, []string, error) {
	parts := []string{}
	switch target.LaunchMode {
	case 0:
		parts = append(parts, target.Entrypoint)
	case 1:
		if target.NodeExecutable == nil || !filepath.IsAbs(*target.NodeExecutable) {
			return "", nil, errors.New("managed runner service target requires an absolute Node executable")
		}
		parts = append(parts, *target.NodeExecutable, target.Entrypoint)
	default:
		return "", nil, fmt.Errorf("managed %s service target has an unsupported launch mode", target.Component)
	}
	parts = append(parts, target.Arguments...)
	quoted := make([]string, 0, len(parts))
	for _, part := range parts {
		value, err := quoteManagedSystemdValue(part)
		if err != nil {
			return "", nil, err
		}
		quoted = append(quoted, value)
	}
	return strings.Join(quoted, " "), parts, nil
}

func quoteManagedSystemdValue(value string) (string, error) {
	if value == "" || strings.ContainsAny(value, "\r\n\x00") {
		return "", errors.New("managed service target contains an invalid value")
	}
	escaped := strings.NewReplacer(`\`, `\\`, `"`, `\"`, `%`, `%%`).Replace(value)
	return `"` + escaped + `"`, nil
}

func formatManagedSystemdWorkingDirectory(value string) (string, error) {
	if !filepath.IsAbs(value) || strings.TrimSpace(value) != value ||
		strings.ContainsAny(value, "\r\n\x00\t\v\f\\\"'") {
		return "", errors.New("managed service working directory contains an invalid value")
	}
	return strings.ReplaceAll(value, "%", "%%"), nil
}

func parseManagedSystemdWorkingDirectory(value string) (string, error) {
	if strings.TrimSpace(value) != value || strings.ContainsAny(value, "\r\n\x00\t\v\f\\\"'") {
		return "", errors.New("managed service unit contains an invalid WorkingDirectory")
	}
	var decoded strings.Builder
	for index := 0; index < len(value); index++ {
		if value[index] != '%' {
			decoded.WriteByte(value[index])
			continue
		}
		if index+1 == len(value) || value[index+1] != '%' {
			return "", errors.New("managed service unit contains an unescaped WorkingDirectory specifier")
		}
		decoded.WriteByte('%')
		index++
	}
	workingDirectory := decoded.String()
	if !filepath.IsAbs(workingDirectory) {
		return "", errors.New("managed service unit WorkingDirectory is not absolute")
	}
	return workingDirectory, nil
}

func readManagedServiceState(
	ctx context.Context,
	commands managedCommandRunner,
	unitName string,
	operation string,
) (bool, error) {
	result := commands.Run(ctx, managedCommand{
		Name: "systemctl",
		Args: []string{"--user", operation, unitName},
	})
	state := strings.ToLower(strings.TrimSpace(result.Stdout))
	switch operation {
	case "is-active":
		if result.ExitCode == 0 && state == "active" {
			return true, nil
		}
		if result.ExitCode != 0 && containsManagedState([]string{"inactive", "failed", "dead", "unknown", "not-found"}, state) {
			return false, nil
		}
	case "is-enabled":
		if result.ExitCode == 0 && containsManagedState([]string{"enabled", "enabled-runtime", "linked", "linked-runtime"}, state) {
			return true, nil
		}
		if result.ExitCode != 0 && containsManagedState([]string{"disabled", "static", "indirect", "generated", "transient", "masked", "not-found"}, state) {
			return false, nil
		}
	default:
		return false, errors.New("unsupported managed service state operation")
	}
	return false, fmt.Errorf("managed service %s state could not be determined", operation)
}

func containsManagedState(values []string, value string) bool {
	for _, candidate := range values {
		if value == candidate {
			return true
		}
	}
	return false
}

func runManagedSystemctl(ctx context.Context, commands managedCommandRunner, args ...string) error {
	result := commands.Run(ctx, managedCommand{Name: "systemctl", Args: append([]string{"--user"}, args...)})
	if result.ExitCode != 0 {
		return errors.New("systemctl operation failed")
	}
	return nil
}

func verifyManagedEffectiveTarget(
	ctx context.Context,
	commands managedCommandRunner,
	unitName string,
	target *managedRuntimeTarget,
) error {
	workingDirectory, err := readManagedSystemdProperty(ctx, commands, unitName, "WorkingDirectory")
	workingDirectory = strings.TrimSuffix(workingDirectory, "\n")
	workingDirectory = strings.TrimSuffix(workingDirectory, "\r")
	if err != nil || strings.ContainsAny(workingDirectory, "\r\n\x00") || workingDirectory != target.WorkingDirectory {
		return fmt.Errorf("managed %s service effective working directory does not match the candidate", target.Component)
	}
	_, expectedArguments, err := managedExecStart(target)
	if err != nil {
		return err
	}
	execStart, err := readManagedSystemdProperty(ctx, commands, unitName, "ExecStart")
	if err != nil {
		return fmt.Errorf("managed %s service effective entrypoint could not be verified", target.Component)
	}
	effectiveArguments, err := parseManagedEffectiveExecStart(execStart)
	if err != nil || len(effectiveArguments) != len(expectedArguments) {
		return fmt.Errorf("managed %s service effective entrypoint does not match the candidate", target.Component)
	}
	for index := range expectedArguments {
		if effectiveArguments[index].value != expectedArguments[index] {
			return fmt.Errorf("managed %s service effective entrypoint does not match the candidate", target.Component)
		}
	}
	identityPath, err := managedTargetIdentityPath(target)
	if err != nil {
		return err
	}
	environment, err := readManagedSystemdProperty(ctx, commands, unitName, "Environment")
	if err != nil {
		return fmt.Errorf("managed %s service effective runtime identity could not be verified", target.Component)
	}
	words, err := splitManagedSystemdWords(strings.TrimSpace(environment))
	if err != nil {
		return fmt.Errorf("managed %s service effective runtime identity could not be verified", target.Component)
	}
	want := managedRuntimeIdentityEnvironment + "=" + identityPath
	matches := 0
	for _, word := range words {
		if word.value == want {
			matches++
		}
	}
	if matches != 1 {
		return fmt.Errorf("managed %s service effective runtime identity does not match the candidate", target.Component)
	}
	return nil
}

func parseManagedEffectiveExecStart(property string) ([]managedWordSpan, error) {
	value := strings.TrimSpace(property)
	if value == "" {
		return nil, errors.New("empty ExecStart property")
	}
	if strings.Contains(value, "argv[]=") {
		if strings.Count(value, "argv[]=") != 1 {
			return nil, errors.New("multiple effective ExecStart commands")
		}
		argumentsStart := strings.Index(value, "argv[]=") + len("argv[]=")
		argumentsEnd := strings.Index(value[argumentsStart:], " ; ignore_errors=")
		if argumentsEnd < 0 {
			return nil, errors.New("invalid effective ExecStart property")
		}
		value = value[argumentsStart : argumentsStart+argumentsEnd]
	}
	return splitManagedSystemdWords(value)
}

func readManagedSystemdProperty(
	ctx context.Context,
	commands managedCommandRunner,
	unitName string,
	property string,
) (string, error) {
	result := commands.Run(ctx, managedCommand{
		Name: "systemctl",
		Args: []string{"--user", "show", unitName, "--property=" + property, "--value"},
	})
	if result.ExitCode != 0 {
		return "", errors.New("managed service property query failed")
	}
	return result.Stdout, nil
}

type managedUnitLine struct {
	body   []byte
	ending []byte
}

func splitManagedUnitLines(unit []byte) []managedUnitLine {
	if len(unit) == 0 {
		return nil
	}
	lines := []managedUnitLine{}
	for len(unit) > 0 {
		newline := bytes.IndexByte(unit, '\n')
		if newline < 0 {
			lines = append(lines, managedUnitLine{body: append([]byte(nil), unit...)})
			break
		}
		bodyEnd := newline
		ending := []byte{'\n'}
		if newline > 0 && unit[newline-1] == '\r' {
			bodyEnd--
			ending = []byte{'\r', '\n'}
		}
		lines = append(lines, managedUnitLine{
			body:   append([]byte(nil), unit[:bodyEnd]...),
			ending: append([]byte(nil), ending...),
		})
		unit = unit[newline+1:]
	}
	return lines
}

func managedUnitNewline(lines []managedUnitLine) []byte {
	for _, line := range lines {
		if len(line.ending) > 0 {
			return append([]byte(nil), line.ending...)
		}
	}
	return []byte{'\n'}
}

func managedUnitDirective(line string) (key string, value string, valueOffset int, ok bool) {
	trimmed := strings.TrimLeft(line, " \t")
	if trimmed == "" || strings.HasPrefix(trimmed, "#") || strings.HasPrefix(trimmed, ";") {
		return "", "", 0, false
	}
	separator := strings.IndexByte(trimmed, '=')
	if separator < 0 {
		return "", "", 0, false
	}
	key = strings.TrimSpace(trimmed[:separator])
	valueOffset = len(line) - len(trimmed) + separator + 1
	return key, line[valueOffset:], valueOffset, key != ""
}

func managedDirectivePrefix(line string) string {
	return line[:len(line)-len(strings.TrimLeft(line, " \t"))]
}

func hasManagedLineContinuation(line string) bool {
	trimmed := strings.TrimRight(line, " \t")
	backslashes := 0
	for index := len(trimmed) - 1; index >= 0 && trimmed[index] == '\\'; index-- {
		backslashes++
	}
	return backslashes%2 == 1
}

type managedWordSpan struct {
	start int
	end   int
	value string
}

func splitManagedSystemdWords(value string) ([]managedWordSpan, error) {
	words := []managedWordSpan{}
	for index := 0; index < len(value); {
		for index < len(value) && (value[index] == ' ' || value[index] == '\t') {
			index++
		}
		if index == len(value) {
			break
		}
		start := index
		quote := byte(0)
		var decoded strings.Builder
		for index < len(value) {
			current := value[index]
			if quote == 0 && (current == ' ' || current == '\t') {
				break
			}
			if current == '\\' {
				index++
				if index == len(value) {
					return nil, errors.New("trailing escape")
				}
				decoded.WriteByte(value[index])
				index++
				continue
			}
			if current == '"' || current == '\'' {
				if quote == 0 {
					quote = current
					index++
					continue
				}
				if quote == current {
					quote = 0
					index++
					continue
				}
			}
			decoded.WriteByte(current)
			index++
		}
		if quote != 0 {
			return nil, errors.New("unterminated quote")
		}
		words = append(words, managedWordSpan{start: start, end: index, value: decoded.String()})
	}
	return words, nil
}
