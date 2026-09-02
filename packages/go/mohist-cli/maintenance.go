package mohistcli

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

var skillFields = []string{"name", "description"}
var skillViewFields = []string{"name", "description", "content"}
var skillPathFields = []string{"name", "path"}

type localSkill struct {
	Name        string `json:"name"`
	Description string `json:"description"`
	Path        string `json:"-"`
}

func parseMaintenance(area string, args []string) (command, error) {
	if len(args) == 0 || (len(args) == 1 && (args[0] == "--help" || args[0] == "-h")) {
		return command{help: true, helpText: maintenanceHelp(area)}, nil
	}
	if area == "skill" {
		return parseSkill(args)
	}
	return parseInstallUpdate(area, args)
}

func parseSkill(args []string) (command, error) {
	action := args[0]
	if action != "list" && action != "view" && action != "install" && action != "path" && action != "sync" {
		return command{}, usage("unknown skill action")
	}
	if len(args) == 2 && (args[1] == "--help" || args[1] == "-h") {
		return command{help: true, helpText: skillHelp(action)}, nil
	}
	c := command{kind: "skill-" + action, catalog: skillFields}
	positionals := []string{}
	for i := 1; i < len(args); i++ {
		switch args[i] {
		case "--json":
			c.fieldsOnly = true
			if i+1 < len(args) && !strings.HasPrefix(args[i+1], "-") {
				c.fieldsOnly = false
				c.fields = strings.Split(args[i+1], ",")
				i++
			}
		case "--full", "--all", "--dry-run":
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), "true")
		case "--path", "--source", "--repo-root":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		default:
			if strings.HasPrefix(args[i], "-") {
				return command{}, usage("unknown option " + args[i])
			}
			positionals = append(positionals, args[i])
		}
	}
	c.args = append(c.args, "positionals", strings.Join(positionals, "\x00"))
	if action == "view" {
		c.catalog = skillViewFields
	}
	if action == "path" {
		c.catalog = skillPathFields
	}
	if len(c.fields) > 0 {
		if err := validateFields(c.fields, c.catalog, "mo skill "+action); err != nil {
			return command{}, err
		}
	}
	return c, nil
}

func parseInstallUpdate(area string, args []string) (command, error) {
	c := command{kind: "update-all"}
	if area == "install" {
		c.kind = "install-component"
	}
	if len(args) > 0 && contains([]string{"cli", "server", "runner", "slack"}, args[0]) {
		c.args = append(c.args, "component", args[0])
		args = args[1:]
	} else if len(args) > 0 && args[0] != "--dry-run" {
		return command{}, usage("unknown " + area + " component")
	}
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--dry-run", "--continue-after-cli-update":
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), "true")
		case "--repo-root", "--cli-path", "--listen-url", "--server-url", "--runner-root", "--unit-dir":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--help", "-h":
			return command{help: true, helpText: maintenanceHelp(area)}, nil
		default:
			return command{}, usage("unknown option " + args[i])
		}
	}
	return c, nil
}

func maintenanceHelp(area string) string {
	if area == "skill" {
		return "USAGE\n    mo skill <list|view|install|path|sync> [flags]\n\nManage coder agent skills."
	}
	if area == "install" {
		return "USAGE\n    mo install <server|runner|slack> [flags]\n\nInstall Mohist components as managed services."
	}
	return "USAGE\n    mo update [<cli|server|runner|slack>] [flags]\n\nUpdate Mohist components. CLI replacement is staged and atomic."
}

func skillHelp(action string) string {
	uses := map[string]string{"list": "mo skill list [--json [fields]]", "view": "mo skill view <name> [--full] [--all] [--json [fields]]", "install": "mo skill install [--path <path>] [--claude] [--hermes]", "path": "mo skill path <name> [--json [fields]]", "sync": "mo skill sync [--repo-root <path>] [--source <path>] [--dry-run]"}
	return "USAGE\n    " + uses[action] + "\n\nJSON FIELDS\n" + strings.Join(skillFields, "\n")
}

func runMaintenance(ctx context.Context, deps Dependencies, c command) int {
	if strings.HasPrefix(c.kind, "skill-") {
		return runSkill(ctx, deps, c)
	}
	return runInstallUpdate(ctx, deps, c)
}

func skillRoot(deps Dependencies) string {
	if value, ok := deps.Lookup("MOHIST_SKILLS_DIR"); ok && strings.TrimSpace(value) != "" {
		return value
	}
	if home, err := deps.HomeDir(); err == nil && home != "" {
		path := filepath.Join(home, ".mohist", "cli", "skill-data")
		if directoryExists(path) {
			return path
		}
	}
	if executable := deps.Executable(); executable != "" {
		path := filepath.Join(filepath.Dir(executable), "skill-data")
		if directoryExists(path) {
			return path
		}
	}
	path := filepath.Join(deps.CurrentDirectory(), "packages", "go", "mohist-cli", "skill-data")
	if directoryExists(path) {
		return path
	}
	return ""
}

func directoryExists(path string) bool { info, err := os.Stat(path); return err == nil && info.IsDir() }

func canonicalGoCLIPath(repoRoot string) string {
	return filepath.Join(repoRoot, "packages", "go", "mohist-cli")
}

func canonicalGoSkillDataPath(repoRoot string) string {
	return filepath.Join(canonicalGoCLIPath(repoRoot), "skill-data")
}

func discoverSkills(root string) ([]localSkill, error) {
	if root == "" {
		return nil, errors.New("no packaged skill assets found; run 'mo update' or 'scripts/install-mo.sh'")
	}
	entries, err := os.ReadDir(root)
	if err != nil {
		return nil, err
	}
	result := []localSkill{}
	for _, entry := range entries {
		if !entry.IsDir() {
			continue
		}
		path := filepath.Join(root, entry.Name(), "SKILL.md")
		data, err := os.ReadFile(path)
		if err != nil {
			continue
		}
		name, description, ok := frontmatter(string(data))
		if ok {
			result = append(result, localSkill{Name: name, Description: description, Path: filepath.Dir(path)})
		}
	}
	sort.Slice(result, func(i, j int) bool { return result[i].Name < result[j].Name })
	return result, nil
}

func frontmatter(content string) (string, string, bool) {
	lines := strings.Split(strings.ReplaceAll(content, "\r\n", "\n"), "\n")
	if len(lines) == 0 || lines[0] != "---" {
		return "", "", false
	}
	var name, description string
	for _, line := range lines[1:] {
		if line == "---" {
			return name, description, name != "" && description != ""
		}
		key, value, ok := strings.Cut(line, ":")
		if !ok {
			continue
		}
		switch strings.TrimSpace(key) {
		case "name":
			name = strings.TrimSpace(value)
		case "description":
			description = strings.TrimSpace(value)
		}
	}
	return "", "", false
}

func runSkill(ctx context.Context, deps Dependencies, c command) int {
	if err := ctx.Err(); err != nil {
		writeError(deps.Stderr, err)
		return ExitCanceled
	}
	action := strings.TrimPrefix(c.kind, "skill-")
	if action == "sync" {
		return syncSkills(ctx, deps, argValue(c.args, "repo-root", ""), argValue(c.args, "source", ""), hasArg(c.args, "dry-run"))
	}
	if action == "install" {
		return installSkillStub(deps, c)
	}
	skills, err := discoverSkills(skillRoot(deps))
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	if c.fieldsOnly {
		fmt.Fprintln(deps.Stdout, strings.Join(c.catalog, "\n"))
		return ExitOK
	}
	if action == "list" {
		if len(c.fields) > 0 {
			return writeSelectedSkills(deps.Stdout, skills, c.fields)
		}
		for _, skill := range skills {
			fmt.Fprintf(deps.Stdout, "%s\t%s\n", skill.Name, skill.Description)
		}
		return ExitOK
	}
	name := firstPositional(c.args)
	if action == "path" {
		if name == "" {
			writeError(deps.Stderr, errors.New("skill name is required"))
			return ExitUsage
		}
		skill := findSkill(skills, name)
		if skill == nil {
			writeError(deps.Stderr, fmt.Errorf("unknown Mohist built-in skill %q", name))
			return ExitOperation
		}
		if len(c.fields) > 0 {
			return writeSelectedObject(deps.Stdout, map[string]string{"name": skill.Name, "path": skill.Path}, c.fields)
		}
		fmt.Fprintln(deps.Stdout, skill.Path)
		return ExitOK
	}
	full := hasArg(c.args, "full")
	if hasArg(c.args, "all") {
		values := []map[string]string{}
		for _, skill := range skills {
			content, e := skillContent(skill.Path, full)
			if e != nil {
				writeError(deps.Stderr, e)
				return ExitOperation
			}
			values = append(values, map[string]string{"name": skill.Name, "description": skill.Description, "content": content})
		}
		if len(c.fields) > 0 {
			return writeSelectedCollection(deps.Stdout, values, c.fields)
		}
		for i, value := range values {
			if i > 0 {
				fmt.Fprintln(deps.Stdout)
			}
			fmt.Fprintf(deps.Stdout, "## %s\n%s", value["name"], value["content"])
		}
		return ExitOK
	}
	if name == "" {
		writeError(deps.Stderr, errors.New("a built-in skill name is required unless --all is specified"))
		return ExitOperation
	}
	skill := findSkill(skills, name)
	if skill == nil {
		writeError(deps.Stderr, fmt.Errorf("unknown Mohist built-in skill %q", name))
		return ExitOperation
	}
	content, err := skillContent(skill.Path, full)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	if len(c.fields) > 0 {
		return writeSelectedObject(deps.Stdout, map[string]string{"name": skill.Name, "description": skill.Description, "content": content}, c.fields)
	}
	_, _ = io.WriteString(deps.Stdout, content)
	return ExitOK
}

func firstPositional(args []string) string {
	return strings.Split(argValue(args, "positionals", ""), "\x00")[0]
}
func findSkill(skills []localSkill, name string) *localSkill {
	for i := range skills {
		if skills[i].Name == name {
			return &skills[i]
		}
	}
	return nil
}
func skillContent(path string, full bool) (string, error) {
	data, err := os.ReadFile(filepath.Join(path, "SKILL.md"))
	if err != nil {
		return "", err
	}
	content := string(data)
	if !full {
		return content, nil
	}
	for _, folder := range []string{"references", "templates"} {
		files, _ := filepath.Glob(filepath.Join(path, folder, "*"))
		for _, file := range files {
			if info, e := os.Stat(file); e == nil && !info.IsDir() {
				data, e := os.ReadFile(file)
				if e != nil {
					return "", e
				}
				if !strings.HasSuffix(content, "\n") {
					content += "\n"
				}
				content += "\n--- " + strings.TrimPrefix(file, path+string(os.PathSeparator)) + " ---\n" + string(data)
			}
		}
	}
	return content, nil
}

func writeSelectedSkills(out io.Writer, skills []localSkill, fields []string) int {
	values := make([]map[string]json.RawMessage, 0, len(skills))
	for _, skill := range skills {
		data, _ := json.Marshal(map[string]string{"name": skill.Name, "description": skill.Description})
		var value map[string]json.RawMessage
		_ = json.Unmarshal(data, &value)
		values = append(values, value)
	}
	data, err := json.Marshal(values)
	if err != nil {
		return ExitOperation
	}
	selected, err := SelectFields(data, fields, true)
	if err != nil {
		return ExitOperation
	}
	_, err = out.Write(append(selected, '\n'))
	if err != nil {
		return ExitOperation
	}
	return ExitOK
}

func writeSelectedObject(out io.Writer, value any, fields []string) int {
	data, err := json.Marshal(value)
	if err != nil {
		return ExitOperation
	}
	selected, err := SelectFields(data, fields, false)
	if err != nil {
		return ExitOperation
	}
	_, err = out.Write(append(selected, '\n'))
	if err != nil {
		return ExitOperation
	}
	return ExitOK
}

func writeSelectedCollection(out io.Writer, value any, fields []string) int {
	data, err := json.Marshal(value)
	if err != nil {
		return ExitOperation
	}
	selected, err := SelectFields(data, fields, true)
	if err != nil {
		return ExitOperation
	}
	_, err = out.Write(append(selected, '\n'))
	if err != nil {
		return ExitOperation
	}
	return ExitOK
}

func installSkillStub(deps Dependencies, c command) int {
	if hasArg(c.args, "hermes") && (hasArg(c.args, "claude") || argValue(c.args, "path", "") != "") {
		writeError(deps.Stderr, errors.New("--hermes cannot be combined with --claude or --path"))
		return ExitOperation
	}
	base := argValue(c.args, "path", "")
	if base == "" {
		base = deps.CurrentDirectory()
	}
	folder := filepath.Join(".agents", "skills")
	if hasArg(c.args, "claude") {
		folder = filepath.Join(".claude", "skills")
	}
	if hasArg(c.args, "hermes") {
		home, _ := deps.HomeDir()
		if value, ok := deps.Lookup("HERMES_HOME"); ok && value != "" {
			home = value
		}
		if home == "" {
			writeError(deps.Stderr, errors.New("home directory could not be resolved"))
			return ExitOperation
		}
		base, folder = home, filepath.Join("skills")
	}
	path := filepath.Join(base, folder, "mohist", "SKILL.md")
	entry := findSkillFromRoot(skillRoot(deps), "mohist")
	if entry == nil {
		writeError(deps.Stderr, errors.New("built-in entry skill 'mohist' is missing"))
		return ExitOperation
	}
	if err := deps.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	existing := false
	if _, err := os.Stat(path); err == nil {
		existing = true
	}
	text := "---\nname: mohist\ndescription: " + entry.Description + "\n---\n\nThis Mohist-managed discovery stub keeps local agent skill installs lightweight and version-matched.\n\nRun `mo skill view mohist` to view the full guidance packaged with this Mohist CLI.\n"
	if err := deps.WriteFile(path, text, 0o600); err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	fmt.Fprintf(deps.Stdout, "Installed Mohist built-in skills to %s\n- mohist: %s\n", filepath.Dir(filepath.Dir(path)), map[bool]string{true: "updated", false: "created"}[existing])
	return ExitOK
}

func findSkillFromRoot(root, name string) *localSkill {
	skills, err := discoverSkills(root)
	if err != nil {
		return nil
	}
	return findSkill(skills, name)
}

func syncSkills(ctx context.Context, deps Dependencies, repoRoot, source string, dryRun bool) int {
	if source == "" {
		if repoRoot == "" {
			repoRoot = deps.CurrentDirectory()
		}
		source = canonicalGoSkillDataPath(repoRoot)
	}
	home, err := deps.HomeDir()
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	target := filepath.Join(home, ".mohist", "cli", "skill-data")
	fmt.Fprintf(deps.Stdout, "Syncing skill data: %s -> %s\n", source, target)
	if dryRun {
		fmt.Fprintln(deps.Stdout, "Dry run: would synchronize managed skill assets atomically.")
		return ExitOK
	}
	return syncDirectory(ctx, deps, source, target, "SKILL.md")
}

func syncDirectory(ctx context.Context, deps Dependencies, source, target, required string) int {
	if !directoryExists(source) {
		writeError(deps.Stderr, fmt.Errorf("source skill-data directory %q is missing", source))
		return ExitOperation
	}
	temp, err := stageDirectory(ctx, deps, source, target, required)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	if err := commitStaged(ctx, deps, []stagedArtifact{{temp: temp, target: target}}); err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	fmt.Fprintf(deps.Stdout, "Synchronized managed skill assets to %s\n", target)
	return ExitOK
}

type stagedArtifact struct {
	temp   string
	target string
}

func stageDirectory(ctx context.Context, deps Dependencies, source, target, _ string) (string, error) {
	parent := filepath.Dir(target)
	if err := deps.MkdirAll(parent, 0o700); err != nil {
		return "", err
	}
	temp, err := os.MkdirTemp(parent, "skill-data.tmp-")
	if err != nil {
		return "", err
	}
	if err := copyTree(ctx, source, temp, deps); err != nil {
		_ = deps.RemoveAll(temp)
		return "", err
	}
	prepared, err := discoverSkills(temp)
	if err != nil {
		_ = deps.RemoveAll(temp)
		return "", err
	}
	if len(prepared) == 0 {
		_ = deps.RemoveAll(temp)
		return "", errors.New("prepared skill-data contains no skill assets")
	}
	return temp, nil
}

func commitStaged(ctx context.Context, deps Dependencies, artifacts []stagedArtifact) error {
	type backup struct {
		artifact stagedArtifact
		path     string
		created  bool
	}
	backups := make([]backup, len(artifacts))
	committed := make([]bool, len(artifacts))
	cleanup := func() {
		for _, artifact := range artifacts {
			_ = deps.RemoveAll(artifact.temp)
		}
		for _, item := range backups {
			if item.path != "" {
				_ = deps.RemoveAll(item.path)
			}
		}
	}
	rollback := func() {
		for i := len(artifacts) - 1; i >= 0; i-- {
			if committed[i] {
				_ = deps.RemoveAll(artifacts[i].target)
			}
		}
		for i := len(backups) - 1; i >= 0; i-- {
			if backups[i].created {
				_ = deps.Rename(backups[i].path, backups[i].artifact.target)
			}
		}
		cleanup()
	}
	for i, artifact := range artifacts {
		if _, err := os.Stat(artifact.target); err == nil {
			backupPath, err := temporaryBackupPath(artifact.target)
			if err != nil {
				rollback()
				return err
			}
			backups[i].path = backupPath
			if err := deps.Rename(artifact.target, backupPath); err != nil {
				rollback()
				return err
			}
			backups[i].created = true
		}
	}
	for i, artifact := range artifacts {
		if err := deps.Rename(artifact.temp, artifact.target); err != nil {
			rollback()
			return err
		}
		committed[i] = true
	}
	if err := ctx.Err(); err != nil {
		rollback()
		return err
	}
	cleanup()
	return nil
}

func temporaryBackupPath(target string) (string, error) {
	directory, err := os.MkdirTemp(filepath.Dir(target), ".mohist-backup-")
	if err != nil {
		return "", err
	}
	if err := os.Remove(directory); err != nil {
		return "", err
	}
	return directory, nil
}

func copyTree(ctx context.Context, source, target string, deps Dependencies) error {
	return filepath.Walk(source, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		if e := ctx.Err(); e != nil {
			return e
		}
		relative, err := filepath.Rel(source, path)
		if err != nil {
			return err
		}
		destination := filepath.Join(target, relative)
		if info.IsDir() {
			return deps.MkdirAll(destination, 0o700)
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		return deps.WriteFile(destination, string(data), 0o600)
	})
}

func runInstallUpdate(ctx context.Context, deps Dependencies, c command) int {
	component := argValue(c.args, "component", "")
	dryRun := hasArg(c.args, "dry-run")
	if component == "" && c.kind == "install-component" {
		writeError(deps.Stderr, errors.New("install component is required"))
		return ExitUsage
	}
	if dryRun {
		action := "update"
		if c.kind == "install-component" {
			action = "install"
		}
		fmt.Fprintf(deps.Stdout, "Dry run: would %s %s from source.\n", action, component)
		return ExitOK
	}
	if strings.HasPrefix(c.kind, "update-") {
		if component == "" {
			return updateAll(ctx, deps, c)
		}
		switch component {
		case "cli":
			return updateCLI(ctx, deps, argValue(c.args, "repo-root", ""), argValue(c.args, "cli-path", ""))
		case "server":
			return executeMaintenance(ctx, deps, "dotnet", "build", "Mohist.sln")
		case "runner":
			return executeMaintenance(ctx, deps, "npm", "run", "build", "-w", "packages/runner")
		case "slack":
			return executeMaintenance(ctx, deps, "go", "-C", "packages/go/mohist-slack", "build", "-o", "bin/build/mohist-slack")
		}
	}
	if component == "runner" {
		cfg, err := ResolveConfig(deps)
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
		client, err := newClient(cfg, deps.HTTPClient)
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
		data, _, err := postJSON(ctx, deps, client, "/api/runners/enrollment-tokens", map[string]any{})
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
		var token struct {
			Token string `json:"token"`
		}
		if json.Unmarshal(data, &token) != nil || token.Token == "" {
			writeError(deps.Stderr, errors.New("server returned no runner enrollment token"))
			return ExitOperation
		}
	}
	return installComponent(ctx, deps, component, c)
}

func updateAll(ctx context.Context, deps Dependencies, c command) int {
	if code := updateCLI(ctx, deps, argValue(c.args, "repo-root", ""), argValue(c.args, "cli-path", "")); code != ExitOK {
		return code
	}
	for _, component := range []string{"server", "runner", "slack"} {
		if code := runInstallUpdate(ctx, deps, command{kind: "update-" + component, args: []string{"component", component}}); code != ExitOK {
			return code
		}
	}
	return ExitOK
}

func installComponent(ctx context.Context, deps Dependencies, component string, c command) int {
	units := map[string]string{"server": "mohist.service", "runner": "mohist-runner.service", "slack": "mohist-slack.service"}
	unit, ok := units[component]
	if !ok {
		writeError(deps.Stderr, errors.New("install component must be server, runner, or slack"))
		return ExitUsage
	}
	home, err := deps.HomeDir()
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	unitDir := argValue(c.args, "unit-dir", "")
	if unitDir == "" {
		unitDir = filepath.Join(home, ".config", "systemd", "user")
	}
	root := argValue(c.args, "repo-root", "")
	if root == "" {
		root = deps.CurrentDirectory()
	}
	entry := map[string]string{"server": "dotnet run --project packages/server/src/Mohist.Server/Mohist.Server.csproj", "runner": "node packages/runner/dist/index.js", "slack": "bin/build/mohist-slack"}[component]
	unitText := "[Unit]\nDescription=Mohist " + component + "\n\n[Service]\nWorkingDirectory=" + root + "\nExecStart=" + entry + "\n\n[Install]\nWantedBy=default.target\n"
	path := filepath.Join(unitDir, unit)
	if err := deps.WriteFile(path, unitText, 0o600); err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	for _, args := range [][]string{{"--user", "daemon-reload"}, {"--user", "enable", unit}, {"--user", "restart", unit}} {
		if err := deps.Execute(ctx, "systemctl", args); err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
	}
	fmt.Fprintf(deps.Stdout, "Installed and started %s\n", unit)
	return ExitOK
}

func executeMaintenance(ctx context.Context, deps Dependencies, name string, args ...string) int {
	if err := deps.Execute(ctx, name, args); err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	return ExitOK
}

func updateCLI(ctx context.Context, deps Dependencies, repoRoot, explicit string) int {
	if repoRoot == "" {
		repoRoot = deps.CurrentDirectory()
	}
	target := explicit
	if target == "" {
		target = deps.Executable()
	}
	if target == "" {
		writeError(deps.Stderr, errors.New("could not resolve mo executable path"))
		return ExitOperation
	}
	temp := target + ".tmp"
	if err := deps.MkdirAll(filepath.Dir(target), 0o700); err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	goCLI := canonicalGoCLIPath(repoRoot)
	if err := deps.Execute(ctx, "go", []string{"-C", goCLI, "build", "-tags", "netgo,osusergo", "-trimpath", "-buildvcs=false", "-o", temp, "./cmd/mo"}); err != nil {
		_ = deps.RemoveAll(temp)
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	if err := deps.Chmod(temp, 0o755); err != nil {
		_ = deps.RemoveAll(temp)
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	managedHome, err := deps.HomeDir()
	if err != nil {
		_ = deps.RemoveAll(temp)
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	managed := filepath.Join(managedHome, ".mohist", "cli", "skill-data")
	skillTemp, err := stageDirectory(ctx, deps, canonicalGoSkillDataPath(repoRoot), managed, "SKILL.md")
	if err != nil {
		_ = deps.RemoveAll(temp)
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	if err := commitStaged(ctx, deps, []stagedArtifact{{temp: skillTemp, target: managed}, {temp: temp, target: target}}); err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	return ExitOK
}
