package mohistcli

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/url"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

var skillFields = []string{"name", "description"}
var skillViewFields = []string{"name", "description", "content"}
var skillPathFields = []string{"name", "path"}

const runnerEnvironmentFile = "%h/.config/mohist/runner.env"
const runnerManagedEnvironmentFile = "%h/.config/mohist/runner-managed.env"
const runnerEnrollmentTokenFile = "enrollment-token"

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
	component := argValue(c.args, "component", "")
	enabledAgentRuntimesSeen := false
	for i := 0; i < len(args); i++ {
		switch args[i] {
		case "--dry-run", "--continue-after-cli-update":
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), "true")
		case "--repo-root", "--cli-path", "--listen-url", "--server-url", "--runner-id", "--runner-root", "--unit-dir":
			if i+1 >= len(args) {
				return command{}, usage(args[i] + " requires a value")
			}
			c.args = append(c.args, strings.TrimPrefix(args[i], "--"), args[i+1])
			i++
		case "--enabled-agent-runtimes":
			if area != "install" || component != "runner" {
				return command{}, usage("--enabled-agent-runtimes is only valid with mo install runner")
			}
			if enabledAgentRuntimesSeen {
				return command{}, usage("--enabled-agent-runtimes may be specified only once")
			}
			if i+1 >= len(args) || strings.HasPrefix(args[i+1], "-") {
				return command{}, usage("--enabled-agent-runtimes requires a value")
			}
			runtimes, err := normalizeEnabledAgentRuntimes(args[i+1])
			if err != nil {
				return command{}, usage(err.Error())
			}
			c.args = append(c.args, "enabled-agent-runtimes", runtimes)
			enabledAgentRuntimesSeen = true
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
		return "USAGE\n    mo install <server|slack> [flags]\n    mo install runner [--enabled-agent-runtimes <list>] [flags]\n\nInstall Mohist components as managed services. Runner Runtime values are pi and opencode."
	}
	return "USAGE\n    mo update [<cli|server|runner|slack>] [flags]\n\nUpdate Mohist components. CLI replacement is staged and atomic."
}

func normalizeEnabledAgentRuntimes(value string) (string, error) {
	enabled := map[string]bool{}
	for _, candidate := range strings.Split(value, ",") {
		runtime := strings.ToLower(strings.TrimSpace(candidate))
		if runtime == "" {
			return "", errors.New("--enabled-agent-runtimes must be a non-empty comma-separated set of pi and opencode")
		}
		if runtime != "pi" && runtime != "opencode" {
			return "", fmt.Errorf("--enabled-agent-runtimes contains unknown Runtime %q; allowed values are pi and opencode", candidate)
		}
		enabled[runtime] = true
	}
	ordered := make([]string, 0, len(enabled))
	for _, runtime := range []string{"pi", "opencode"} {
		if enabled[runtime] {
			ordered = append(ordered, runtime)
		}
	}
	return strings.Join(ordered, ","), nil
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

type managedReleaseManifest struct {
	Component      string `json:"component"`
	Version        string `json:"version"`
	SourceRevision string `json:"sourceRevision"`
	GitHash        string `json:"gitHash"`
	TreeHash       string `json:"treeHash"`
	ArtifactDigest string `json:"artifactDigest"`
	ReleaseID      string `json:"releaseId"`
	Generation     int64  `json:"generation"`
}

type managedRelease struct {
	Root         string
	Entrypoint   string
	ManifestPath string
}

func resolveMaintenanceRepoRoot(deps Dependencies, requested string) (string, error) {
	root := strings.TrimSpace(requested)
	if root == "" {
		root = deps.CurrentDirectory()
	}
	root, err := filepath.Abs(root)
	if err != nil {
		return "", fmt.Errorf("could not resolve source root: %w", err)
	}
	info, err := os.Stat(root)
	if err != nil || !info.IsDir() {
		if err == nil {
			err = errors.New("source root is not a directory")
		}
		return "", fmt.Errorf("invalid source root %q: %w", root, err)
	}
	return root, nil
}

func buildManagedRelease(ctx context.Context, deps Dependencies, component, sourceRoot, home string) (managedRelease, error) {
	now := deps.Now()
	releaseID := fmt.Sprintf("%s-%d", component, now.UnixNano())
	releaseRoot := filepath.Join(home, ".mohist", "releases", component, releaseID)
	if err := deps.MkdirAll(releaseRoot, 0o700); err != nil {
		return managedRelease{}, err
	}

	var entrypoint string
	switch component {
	case "server":
		project := filepath.Join(sourceRoot, "packages", "server", "src", "Mohist.Server", "Mohist.Server.csproj")
		if err := deps.Execute(ctx, "dotnet", []string{"publish", project, "-c", "Release", "--no-restore", "-o", releaseRoot}); err != nil {
			_ = deps.RemoveAll(releaseRoot)
			return managedRelease{}, err
		}
		entrypoint = filepath.Join(releaseRoot, "Mohist.Server")
	case "runner":
		if err := deps.Execute(ctx, "npm", []string{"--prefix", sourceRoot, "run", "build", "--workspace", "packages/runner"}); err != nil {
			_ = deps.RemoveAll(releaseRoot)
			return managedRelease{}, err
		}
		dist := filepath.Join(sourceRoot, "packages", "runner", "dist")
		if err := copyTree(ctx, dist, filepath.Join(releaseRoot, "dist"), deps); err != nil {
			_ = deps.RemoveAll(releaseRoot)
			return managedRelease{}, err
		}
		packageJSON := filepath.Join(sourceRoot, "packages", "runner", "package.json")
		data, err := os.ReadFile(packageJSON)
		if err != nil {
			_ = deps.RemoveAll(releaseRoot)
			return managedRelease{}, err
		}
		if err := deps.WriteFile(filepath.Join(releaseRoot, "package.json"), string(data), 0o600); err != nil {
			_ = deps.RemoveAll(releaseRoot)
			return managedRelease{}, err
		}
		entrypoint = filepath.Join(releaseRoot, "dist", "cli.js")
	default:
		return managedRelease{}, fmt.Errorf("unsupported managed release component %q", component)
	}
	if info, err := os.Stat(entrypoint); err != nil || info.IsDir() {
		if err == nil {
			err = errors.New("canonical entrypoint is a directory")
		}
		_ = deps.RemoveAll(releaseRoot)
		return managedRelease{}, fmt.Errorf("canonical %s entrypoint %q is missing: %w", component, entrypoint, err)
	}

	sourceRevision := "unknown"
	if output, err := deps.ExecuteOutput(ctx, "git", []string{"-C", sourceRoot, "rev-parse", "HEAD"}); err == nil && strings.TrimSpace(output) != "" {
		sourceRevision = strings.TrimSpace(output)
	}
	treeHash := sourceRevision
	if output, err := deps.ExecuteOutput(ctx, "git", []string{"-C", sourceRoot, "write-tree"}); err == nil && strings.TrimSpace(output) != "" {
		treeHash = strings.TrimSpace(output)
	}
	digest, err := directoryDigest(releaseRoot, "")
	if err != nil {
		_ = deps.RemoveAll(releaseRoot)
		return managedRelease{}, err
	}
	manifest := managedReleaseManifest{
		Component: component, Version: releaseID, SourceRevision: sourceRevision,
		GitHash: sourceRevision, TreeHash: treeHash, ArtifactDigest: digest,
		ReleaseID: releaseID, Generation: 1,
	}
	manifestData, err := json.MarshalIndent(manifest, "", "  ")
	if err != nil {
		_ = deps.RemoveAll(releaseRoot)
		return managedRelease{}, err
	}
	manifestPath := filepath.Join(releaseRoot, "identity.json")
	if err := deps.WriteFile(manifestPath, string(manifestData)+"\n", 0o600); err != nil {
		_ = deps.RemoveAll(releaseRoot)
		return managedRelease{}, err
	}
	if err := validateManagedRelease(component, entrypoint, manifestPath); err != nil {
		_ = deps.RemoveAll(releaseRoot)
		return managedRelease{}, err
	}
	return managedRelease{Root: releaseRoot, Entrypoint: entrypoint, ManifestPath: manifestPath}, nil
}

func directoryDigest(root, excluded string) (string, error) {
	hash := sha256.New()
	err := filepath.Walk(root, func(path string, info os.FileInfo, err error) error {
		if err != nil {
			return err
		}
		if info.IsDir() {
			return nil
		}
		if path == excluded {
			return nil
		}
		data, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		_, _ = hash.Write([]byte(strings.TrimPrefix(path, root)))
		_, _ = hash.Write(data)
		return nil
	})
	if err != nil {
		return "", err
	}
	return fmt.Sprintf("sha256:%x", hash.Sum(nil)), nil
}

func validateManagedRelease(component, entrypoint, manifestPath string) error {
	if !filepath.IsAbs(entrypoint) || !filepath.IsAbs(manifestPath) {
		return errors.New("managed release paths must be absolute")
	}
	info, err := os.Stat(entrypoint)
	if err != nil || info.IsDir() {
		return fmt.Errorf("canonical %s entrypoint validation failed: %w", component, err)
	}
	data, err := os.ReadFile(manifestPath)
	if err != nil {
		return fmt.Errorf("managed %s identity manifest validation failed: %w", component, err)
	}
	var manifest managedReleaseManifest
	if err := json.Unmarshal(data, &manifest); err != nil || manifest.Component != component || manifest.Version == "" || manifest.ReleaseID == "" || manifest.Generation <= 0 || manifest.ArtifactDigest == "" || manifest.SourceRevision == "" {
		return fmt.Errorf("managed %s identity manifest is missing or corrupt", component)
	}
	digest, err := directoryDigest(filepath.Dir(manifestPath), manifestPath)
	if err != nil || digest != manifest.ArtifactDigest {
		return fmt.Errorf("managed %s identity manifest digest does not match the candidate", component)
	}
	return nil
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
		backups[i].artifact = artifact
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
	enrollmentToken := ""
	runnerServerURL := ""
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
	home, err := deps.HomeDir()
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	release, acquired, err := deps.AcquireUserTransactionLock(home)
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	if !acquired {
		writeError(deps.Stderr, errors.New("update_in_progress"))
		return ExitOperation
	}
	defer release()
	return runInstallUpdateLocked(ctx, deps, c, component, enrollmentToken, runnerServerURL)
}

func runInstallUpdateLocked(ctx context.Context, deps Dependencies, c command, component, enrollmentToken, runnerServerURL string) int {
	root, err := resolveMaintenanceRepoRoot(deps, argValue(c.args, "repo-root", ""))
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	ownsOutcome := c.outcome == nil && strings.HasPrefix(c.kind, "update-")
	if ownsOutcome {
		c.outcome = newUpdateOutcomeReporter(ctx, deps, c, root)
		c.outcome.stage(ctx, deps, "Preparing update", "CLI update started")
	}
	if ownsOutcome {
		code := runInstallUpdateLockedWithOutcome(ctx, deps, c, component, enrollmentToken, runnerServerURL, root)
		c.outcome.finish(ctx, deps, code)
		return code
	}
	return runInstallUpdateLockedWithOutcome(ctx, deps, c, component, enrollmentToken, runnerServerURL, root)
}

func runInstallUpdateLockedWithOutcome(ctx context.Context, deps Dependencies, c command, component, enrollmentToken, runnerServerURL, root string) int {
	if strings.HasPrefix(c.kind, "update-") {
		if component == "" {
			return updateAllLocked(ctx, deps, c, root)
		}
		switch component {
		case "cli":
			return updateCLI(ctx, deps, root, argValue(c.args, "cli-path", ""))
		case "server":
			return installComponent(ctx, deps, component, command{kind: c.kind, args: append(c.args, "repo-root", root), outcome: c.outcome}, "", "")
		case "runner":
			return installComponent(ctx, deps, component, command{kind: c.kind, args: append(c.args, "repo-root", root), outcome: c.outcome}, "", "")
		case "slack":
			return executeMaintenance(ctx, deps, "go", "-C", filepath.Join(root, "packages/go/mohist-slack"), "build", "-o", filepath.Join(root, "packages/go/mohist-slack/bin/build/mohist-slack"))
		}
	}
	if component == "runner" {
		cfg, err := ResolveConfig(deps)
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
		if explicit := strings.TrimSpace(argValue(c.args, "server-url", "")); explicit != "" {
			cfg.ServerURL = explicit
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
		enrollmentToken = token.Token
		runnerServerURL = cfg.ServerURL
	}
	return installComponent(ctx, deps, component, command{kind: c.kind, args: append(c.args, "repo-root", root)}, enrollmentToken, runnerServerURL)
}

func updateAll(ctx context.Context, deps Dependencies, c command) int {
	return runInstallUpdate(ctx, deps, c)
}

func updateAllLocked(ctx context.Context, deps Dependencies, c command, root string) int {
	if c.outcome != nil {
		c.outcome.stage(ctx, deps, "Updating CLI", "Updating the Mohist CLI")
	}
	if code := updateCLI(ctx, deps, root, argValue(c.args, "cli-path", "")); code != ExitOK {
		return code
	}
	for _, component := range []string{"server", "runner", "slack"} {
		if c.outcome != nil {
			c.outcome.stage(ctx, deps, "Updating "+component, "Updating "+component)
		}
		args := append([]string(nil), c.args...)
		args = append(args, "component", component, "repo-root", root)
		if code := runInstallUpdateLocked(ctx, deps, command{kind: "update-" + component, args: args, outcome: c.outcome}, component, "", ""); code != ExitOK {
			return code
		}
	}
	return ExitOK
}

func installComponent(
	ctx context.Context,
	deps Dependencies,
	component string,
	c command,
	enrollmentToken string,
	runnerServerURL string,
) int {
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
	root, err := resolveMaintenanceRepoRoot(deps, argValue(c.args, "repo-root", ""))
	if err != nil {
		writeError(deps.Stderr, err)
		return ExitOperation
	}
	release := managedRelease{}
	if strings.HasPrefix(c.kind, "update-") && (component == "server" || component == "runner") {
		if c.outcome != nil {
			c.outcome.stage(ctx, deps, "Building "+component, "Building the "+component+" candidate")
		}
		release, err = buildManagedRelease(ctx, deps, component, root, home)
		if err != nil {
			if c.outcome != nil {
				c.outcome.stage(ctx, deps, "Failed", err.Error())
			}
			fmt.Fprintf(deps.Stderr, "%v; recover with 'mo service start %s'\n", err, component)
			return ExitOperation
		}
	}
	if release.Root != "" {
		return activateManagedReleaseWithReporter(ctx, deps, component, c, release, home, unitDir, unit, c.outcome)
	}
	environmentFileLine := ""
	if component == "runner" && c.kind == "install-component" {
		if enrollmentToken == "" || runnerServerURL == "" {
			writeError(deps.Stderr, errors.New("runner enrollment token is required before installing the service"))
			return ExitOperation
		}
		runnerRoot := argValue(c.args, "runner-root", "")
		if runnerRoot == "" {
			runnerRoot = filepath.Join(home, ".mohist", "projects")
		}
		runnerID := strings.TrimSpace(argValue(c.args, "runner-id", ""))
		if runnerID == "" {
			runnerID = defaultRunnerID()
		}
		managedEnvironment, err := runnerManagedEnvironment(runnerServerURL, runnerID, runnerRoot)
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
		enrollmentTokenPath := filepath.Join(runnerRoot, runnerEnrollmentTokenFile)
		if err := deps.WriteFile(enrollmentTokenPath, enrollmentToken+"\n", 0o600); err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
		managedEnvironmentPath := filepath.Join(home, ".config", "mohist", "runner-managed.env")
		if err := deps.WriteFile(managedEnvironmentPath, managedEnvironment, 0o600); err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
		environmentFileLine = "EnvironmentFile=-" + runnerEnvironmentFile + "\n" +
			"EnvironmentFile=-" + runnerManagedEnvironmentFile + "\n"
		if runtimes := argValue(c.args, "enabled-agent-runtimes", ""); runtimes != "" {
			environmentPath := filepath.Join(home, ".config", "mohist", "runner.env")
			if err := deps.WriteFile(environmentPath, "ENABLED_AGENT_RUNTIMES="+runtimes+"\n", 0o600); err != nil {
				writeError(deps.Stderr, err)
				return ExitOperation
			}
		}
	}
	entry := map[string]string{
		"server": "dotnet run --project packages/server/src/Mohist.Server/Mohist.Server.csproj",
		"runner": "node packages/runner/dist/cli.js",
		"slack":  filepath.Join(root, "bin", "build", "mohist-slack"),
	}[component]
	workingDirectory := root
	manifestLine := ""
	if release.Root != "" {
		entry = release.Entrypoint
		workingDirectory = release.Root
		manifestLine = "Environment=MOHIST_RUNTIME_IDENTITY_PATH=" + release.ManifestPath + "\n"
	}
	if component == "runner" && release.Root != "" {
		node, err := exec.LookPath("node")
		if err != nil {
			node = "/usr/bin/node"
		}
		entry = node + " " + entry
	}
	unitText := "[Unit]\nDescription=Mohist " + component + "\n\n[Service]\nWorkingDirectory=" + workingDirectory + "\n" + environmentFileLine + manifestLine + "ExecStart=" + entry + "\n\n[Install]\nWantedBy=default.target\n"
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

type managedUpdateFence struct {
	client *client
	id     string
	runner string
}

type updateOutcomeReporter struct {
	client     *client
	jobID      string
	sourcePath string
	now        func() time.Time
	stageLogs  []cliOutcomeLog
	finished   bool
}

type cliOutcomeLog struct {
	Stage   string
	Message string
}

func newUpdateOutcomeReporter(ctx context.Context, deps Dependencies, c command, sourcePath string) *updateOutcomeReporter {
	_ = ctx
	now := deps.Now
	if now == nil {
		now = time.Now
	}
	reporter := &updateOutcomeReporter{jobID: newLocalJobID(), sourcePath: sourcePath, now: now}
	cfg, err := ResolveConfig(deps)
	if err != nil {
		return reporter
	}
	if explicit := strings.TrimSpace(argValue(c.args, "server-url", "")); explicit != "" {
		cfg.ServerURL = explicit
	}
	reporter.client, _ = newClient(cfg, deps.HTTPClient)
	return reporter
}

func newLocalJobID() string {
	id, err := newUpdateInterruptID()
	if err != nil {
		return "cli-update"
	}
	return id
}

func (r *updateOutcomeReporter) stage(ctx context.Context, deps Dependencies, stage, message string) {
	if r == nil || r.finished {
		return
	}
	r.stageLogs = append(r.stageLogs, cliOutcomeLog{Stage: stage, Message: message})
	r.post(ctx, deps, "running", "")
}

func (r *updateOutcomeReporter) finish(ctx context.Context, deps Dependencies, code int) {
	if code == ExitOK {
		r.finishStatus(ctx, deps, "succeeded", "succeeded")
		return
	}
	r.finishStatus(ctx, deps, "failed", "failed")
}

func (r *updateOutcomeReporter) finishStatus(ctx context.Context, deps Dependencies, status, outcome string) {
	if r == nil || r.finished {
		return
	}
	r.finished = true
	r.post(ctx, deps, status, outcome)
}

func (r *updateOutcomeReporter) post(ctx context.Context, deps Dependencies, status, outcome string) {
	if r.client == nil {
		return
	}
	body := map[string]any{
		"jobId":      r.jobID,
		"status":     status,
		"stage":      "Ready",
		"outcome":    nil,
		"sourcePath": r.sourcePath,
	}
	if len(r.stageLogs) > 0 {
		entry := r.stageLogs[len(r.stageLogs)-1]
		body["stage"] = entry.Stage
		body["logs"] = []map[string]string{{
			"at":      r.now().UTC().Format("2006-01-02T15:04:05.9999999Z07:00"),
			"stage":   entry.Stage,
			"message": entry.Message,
		}}
	}
	if outcome != "" {
		body["outcome"] = outcome
	}
	_, _, _ = postJSON(ctx, deps, r.client, "/api/system/update/outcome", body)
}

type managedIdentity struct {
	Component      string `json:"component"`
	Version        string `json:"version"`
	SourceRevision string `json:"sourceRevision"`
	GitHash        string `json:"gitHash"`
	TreeHash       string `json:"treeHash"`
	ArtifactDigest string `json:"artifactDigest"`
	ReleaseID      string `json:"releaseId"`
	Generation     int64  `json:"generation"`
}

func activateManagedRelease(ctx context.Context, deps Dependencies, component string, c command, candidate managedRelease, home, unitDir, unit string) int {
	return activateManagedReleaseWithReporter(ctx, deps, component, c, candidate, home, unitDir, unit, nil)
}

func activateManagedReleaseWithReporter(ctx context.Context, deps Dependencies, component string, c command, candidate managedRelease, home, unitDir, unit string, reporter *updateOutcomeReporter) int {
	if deps.RemoveAll == nil {
		deps.RemoveAll = os.RemoveAll
	}
	if deps.Rename == nil {
		deps.Rename = os.Rename
	}
	unitPath := filepath.Join(unitDir, unit)
	oldUnit, hadUnit := "", false
	if _, statErr := os.Stat(unitPath); statErr == nil {
		oldUnit, _ = deps.ReadFile(unitPath)
		hadUnit = true
	}
	currentPath := filepath.Join(home, ".mohist", "releases", component, "current")
	verifiedPath := filepath.Join(home, ".mohist", "releases", component, "verified")
	oldCurrent, oldVerified := "", ""
	if _, statErr := os.Stat(currentPath); statErr == nil {
		oldCurrent, _ = deps.ReadFile(currentPath)
	}
	if _, statErr := os.Stat(verifiedPath); statErr == nil {
		oldVerified, _ = deps.ReadFile(verifiedPath)
	}

	var fence *managedUpdateFence
	var err error
	if component == "runner" {
		if reporter != nil {
			reporter.stage(ctx, deps, "Preparing runner", "Acquiring the Runner update fence")
		}
		fence, err = beginRunnerUpdateFence(ctx, deps, c)
		if err != nil {
			writeError(deps.Stderr, err)
			return ExitOperation
		}
	}
	recover := func(cause error) int {
		if reporter != nil {
			reporter.stage(ctx, deps, "Recovering", "Restoring the previous verified release")
		}
		recoveryErr := restoreManagedUpdate(ctx, deps, unitPath, unit, oldUnit, hadUnit, currentPath, verifiedPath, oldCurrent, oldVerified)
		if recoveryErr != nil {
			cause = fmt.Errorf("%w; recovery also failed: %v", cause, recoveryErr)
		}
		if fence != nil {
			if fenceErr := cancelRunnerUpdateFence(ctx, fence); fenceErr != nil {
				cause = fmt.Errorf("%w; fence release failed: %v", cause, fenceErr)
			}
		}
		fmt.Fprintf(deps.Stderr, "%v; recover with 'mo service start %s'\n", cause, component)
		if reporter != nil {
			if recoveryErr == nil {
				reporter.finishStatus(ctx, deps, "recovered", "recovered")
			} else {
				reporter.finishStatus(ctx, deps, "failed", "failed")
			}
		}
		return ExitOperation
	}

	if hadUnit {
		if reporter != nil {
			reporter.stage(ctx, deps, "Stopping "+component, "Stopping the current "+component+" service")
		}
		if err := deps.Execute(ctx, "systemctl", []string{"--user", "stop", unit}); err != nil {
			return recover(fmt.Errorf("stop %s: %w", component, err))
		}
	}
	unitText := managedUnitText(component, candidate, c, home)
	if reporter != nil {
		reporter.stage(ctx, deps, "Activating "+component, "Switching to the candidate release")
	}
	if err := replaceManagedUnit(deps, unitPath, unitText); err != nil {
		return recover(fmt.Errorf("activate %s: %w", component, err))
	}
	for _, args := range [][]string{{"--user", "daemon-reload"}, {"--user", "enable", unit}, {"--user", "restart", unit}} {
		if reporter != nil && args[len(args)-2] == "restart" {
			reporter.stage(ctx, deps, "Restarting "+component, "Restarting the managed service")
		}
		if err := deps.Execute(ctx, "systemctl", args); err != nil {
			return recover(fmt.Errorf("%s %s: %w", args[len(args)-1], component, err))
		}
	}

	if shouldVerifyManagedIdentity(deps, component, c) {
		if reporter != nil {
			reporter.stage(ctx, deps, "Verifying runtime", "Verifying the running runtime identity")
		}
		if err := verifyManagedIdentity(ctx, deps, component, c, candidate); err != nil {
			return recover(err)
		}
	}
	if err := writeManagedPointer(deps, currentPath, candidate.Root); err != nil {
		return recover(fmt.Errorf("commit active %s target: %w", component, err))
	}
	if err := writeManagedPointer(deps, verifiedPath, candidate.Root); err != nil {
		return recover(fmt.Errorf("commit verified %s target: %w", component, err))
	}
	if fence != nil {
		if err := cancelRunnerUpdateFence(ctx, fence); err != nil {
			return recover(err)
		}
	}
	fmt.Fprintf(deps.Stdout, "Installed and started %s\n", unit)
	if reporter != nil {
		reporter.finishStatus(ctx, deps, "succeeded", "succeeded")
	}
	return ExitOK
}

func managedUnitText(component string, release managedRelease, c command, home string) string {
	environmentFileLine := ""
	if component == "runner" {
		environmentFileLine = "EnvironmentFile=-%h/.config/mohist/runner.env\nEnvironmentFile=-%h/.config/mohist/runner-managed.env\n"
	}
	entry := release.Entrypoint
	if component == "runner" {
		node, err := exec.LookPath("node")
		if err != nil {
			node = "/usr/bin/node"
		}
		entry = node + " " + entry
	}
	return "[Unit]\nDescription=Mohist " + component + "\n\n[Service]\nWorkingDirectory=" + release.Root + "\n" + environmentFileLine + "Environment=MOHIST_RUNTIME_IDENTITY_PATH=" + release.ManifestPath + "\nExecStart=" + entry + "\n\n[Install]\nWantedBy=default.target\n"
}

func replaceManagedUnit(deps Dependencies, target, value string) error {
	mkdirAll := deps.MkdirAll
	if mkdirAll == nil {
		mkdirAll = os.MkdirAll
	}
	if err := mkdirAll(filepath.Dir(target), 0o700); err != nil {
		return err
	}
	temp, err := os.CreateTemp(filepath.Dir(target), ".mohist-unit-*")
	if err != nil {
		return err
	}
	tempPath := temp.Name()
	_ = temp.Close()
	_ = os.Remove(tempPath)
	defer func() { _ = deps.RemoveAll(tempPath) }()
	if err := deps.WriteFile(tempPath, value, 0o600); err != nil {
		return err
	}
	backup := ""
	if _, err := os.Stat(target); err == nil {
		backup, err = temporaryBackupPath(target)
		if err != nil {
			return err
		}
		if err := deps.Rename(target, backup); err != nil {
			return err
		}
	}
	if err := deps.Rename(tempPath, target); err != nil {
		if backup != "" {
			_ = deps.Rename(backup, target)
		}
		return err
	}
	if backup != "" {
		_ = deps.RemoveAll(backup)
	}
	return nil
}

func restoreManagedUpdate(ctx context.Context, deps Dependencies, unitPath, unit string, oldUnit string, hadUnit bool, currentPath, verifiedPath, oldCurrent, oldVerified string) error {
	var first error
	if err := deps.Execute(ctx, "systemctl", []string{"--user", "stop", unit}); err != nil && first == nil {
		first = err
	}
	if hadUnit {
		if err := deps.WriteFile(unitPath, oldUnit, 0o600); err != nil && first == nil {
			first = err
		}
	} else if err := deps.RemoveAll(unitPath); err != nil && first == nil {
		first = err
	}
	if err := restoreManagedPointer(deps, currentPath, oldCurrent); err != nil && first == nil {
		first = err
	}
	if err := restoreManagedPointer(deps, verifiedPath, oldVerified); err != nil && first == nil {
		first = err
	}
	if err := deps.Execute(ctx, "systemctl", []string{"--user", "daemon-reload"}); err != nil && first == nil {
		first = err
	}
	if hadUnit {
		if err := deps.Execute(ctx, "systemctl", []string{"--user", "restart", unit}); err != nil && first == nil {
			first = err
		}
	}
	return first
}

func writeManagedPointer(deps Dependencies, path, value string) error {
	return deps.WriteFile(path, value+"\n", 0o600)
}

func restoreManagedPointer(deps Dependencies, path, value string) error {
	if strings.TrimSpace(value) == "" {
		return deps.RemoveAll(path)
	}
	return writeManagedPointer(deps, path, strings.TrimSpace(value))
}

func shouldVerifyManagedIdentity(deps Dependencies, component string, c command) bool {
	if component == "runner" {
		return strings.TrimSpace(argValue(c.args, "runner-id", "")) != ""
	}
	if strings.TrimSpace(argValue(c.args, "server-url", "")) != "" {
		return true
	}
	if deps.Lookup == nil {
		return false
	}
	_, ok := deps.Lookup("MOHIST_SERVER_URL")
	return ok
}

func verifyManagedIdentity(ctx context.Context, deps Dependencies, component string, c command, candidate managedRelease) error {
	cfg, err := ResolveConfig(deps)
	if err != nil {
		return fmt.Errorf("runtime identity verification unavailable: %w", err)
	}
	if explicit := strings.TrimSpace(argValue(c.args, "server-url", "")); explicit != "" {
		cfg.ServerURL = explicit
	}
	client, err := newClient(cfg, deps.HTTPClient)
	if err != nil {
		return err
	}
	data, _, err := getData(ctx, client, identityPath(component, c))
	if err != nil {
		return fmt.Errorf("runtime identity verification failed: %w", err)
	}
	var actual managedIdentity
	if component == "server" {
		var response struct {
			Running managedIdentity `json:"running"`
		}
		if err := json.Unmarshal(data, &response); err != nil {
			return fmt.Errorf("runtime identity verification returned malformed Server identity: %w", err)
		}
		actual = response.Running
	} else {
		var response struct {
			RunnerID       string `json:"runnerId"`
			BuildGitHash   string `json:"buildGitHash"`
			Component      string `json:"component"`
			Version        string `json:"version"`
			SourceRevision string `json:"sourceRevision"`
			TreeHash       string `json:"treeHash"`
			ArtifactDigest string `json:"artifactDigest"`
			ReleaseID      string `json:"releaseId"`
			Generation     int64  `json:"generation"`
		}
		if err := json.Unmarshal(data, &response); err != nil {
			return fmt.Errorf("runtime identity verification returned malformed Runner identity: %w", err)
		}
		actual = managedIdentity{Component: response.Component, Version: response.Version, SourceRevision: response.SourceRevision, GitHash: response.BuildGitHash, TreeHash: response.TreeHash, ArtifactDigest: response.ArtifactDigest, ReleaseID: response.ReleaseID, Generation: response.Generation}
		if response.RunnerID != strings.TrimSpace(argValue(c.args, "runner-id", "")) {
			return fmt.Errorf("runtime identity mismatch: expected runnerId=%q actual runnerId=%q", argValue(c.args, "runner-id", ""), response.RunnerID)
		}
	}
	var expected managedIdentity
	manifestData, readErr := deps.ReadFile(candidate.ManifestPath)
	if readErr != nil || json.Unmarshal([]byte(manifestData), &expected) != nil {
		return errors.New("candidate identity manifest could not be read during verification")
	}
	if expected != actual {
		return fmt.Errorf("runtime identity mismatch: expected=%s actual=%s", identityText(expected), identityText(actual))
	}
	return nil
}

func identityPath(component string, c command) string {
	if component == "runner" {
		return "/api/runner/identity?runnerId=" + url.QueryEscape(strings.TrimSpace(argValue(c.args, "runner-id", "")))
	}
	return "/api/system/info"
}

func identityText(identity managedIdentity) string {
	data, _ := json.Marshal(identity)
	return string(data)
}

func beginRunnerUpdateFence(ctx context.Context, deps Dependencies, c command) (*managedUpdateFence, error) {
	runnerID := strings.TrimSpace(argValue(c.args, "runner-id", ""))
	if runnerID == "" {
		return nil, nil
	}
	cfg, err := ResolveConfig(deps)
	if err != nil {
		return nil, err
	}
	if explicit := strings.TrimSpace(argValue(c.args, "server-url", "")); explicit != "" {
		cfg.ServerURL = explicit
	}
	client, err := newClient(cfg, deps.HTTPClient)
	if err != nil {
		return nil, err
	}
	id, err := newUpdateInterruptID()
	if err != nil {
		return nil, err
	}
	data, code, err := postJSON(ctx, deps, client, "/api/runner/"+url.PathEscape(runnerID)+"/update-interrupt", map[string]string{"updateInterruptId": id})
	if err != nil {
		if code == "not_found" || code == "runner_not_found" {
			return nil, nil
		}
		return nil, fmt.Errorf("Runner update fence could not be acquired: %w", err)
	}
	var response struct {
		Status            string `json:"status"`
		UpdateInterruptID string `json:"updateInterruptId"`
	}
	if json.Unmarshal(data, &response) != nil || response.UpdateInterruptID != id || response.Status != "draining" {
		return nil, fmt.Errorf("Runner update fence was superseded or not owned: expected=%s actual=%s/%s", id, response.UpdateInterruptID, response.Status)
	}
	return &managedUpdateFence{client: client, id: id, runner: runnerID}, nil
}

func cancelRunnerUpdateFence(ctx context.Context, fence *managedUpdateFence) error {
	data, _, err := postJSON(ctx, Dependencies{}, fence.client, "/api/runner/"+url.PathEscape(fence.runner)+"/update-interrupt/"+url.PathEscape(fence.id)+"/cancel", map[string]any{})
	if err != nil {
		return err
	}
	var response struct {
		Status string `json:"status"`
	}
	if json.Unmarshal(data, &response) != nil || (response.Status != "cancelled" && response.Status != "already-cancelled") {
		return fmt.Errorf("Runner update fence was not released because ownership was superseded")
	}
	return nil
}

func newUpdateInterruptID() (string, error) {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return "", err
	}
	b[6] = (b[6] & 0x0f) | 0x40
	b[8] = (b[8] & 0x3f) | 0x80
	return fmt.Sprintf("%08x-%04x-%04x-%04x-%012x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16]), nil
}

func defaultRunnerID() string {
	host, err := os.Hostname()
	if err != nil || strings.TrimSpace(host) == "" {
		return "runner-local"
	}
	return "runner-" + strings.TrimSpace(host)
}

func runnerManagedEnvironment(serverURL, runnerID, runnerRoot string) (string, error) {
	server, err := systemdEnvironmentAssignment("SERVER_URL", serverURL)
	if err != nil {
		return "", err
	}
	id, err := systemdEnvironmentAssignment("RUNNER_ID", runnerID)
	if err != nil {
		return "", err
	}
	root, err := systemdEnvironmentAssignment("RUNNER_ROOT", runnerRoot)
	if err != nil {
		return "", err
	}
	return server + id + root, nil
}

func systemdEnvironmentAssignment(name, value string) (string, error) {
	if strings.ContainsAny(value, "\r\n\x00") {
		return "", fmt.Errorf("%s contains characters that cannot be stored in the Runner environment file", name)
	}
	escaped := strings.NewReplacer(`\`, `\\`, `"`, `\"`).Replace(value)
	return name + `="` + escaped + `"` + "\n", nil
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
