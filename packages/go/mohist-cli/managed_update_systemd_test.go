package mohistcli

import (
	"context"
	"errors"
	"io"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"testing"
)

func TestPatchManagedSystemdUnitPreservesOperatorConfiguration(t *testing.T) {
	original := "[Unit]\r\n" +
		"Description=Mohist Runner\r\n" +
		"# preserve this unit comment\r\n" +
		"\r\n" +
		"[Service]\r\n" +
		"# preserve this service comment\r\n" +
		"WorkingDirectory=/source checkout\r\n" +
		"Environment=\"KEEP=alpha beta\" \"MOHIST_RUNTIME_IDENTITY_PATH=/old/runtime-identity.json\" \"ALSO=2\"\r\n" +
		"EnvironmentFile=-%h/.config/mohist/runner.env\r\n" +
		"EnvironmentFile=-%h/.config/mohist/runner-managed.env\r\n" +
		"LoadCredential=operator-token:/run/credentials/operator-token\r\n" +
		"LimitNOFILE=1048576\r\n" +
		"ExecStart=/usr/bin/node /source/packages/runner/dist/cli.js --old\r\n" +
		"Restart=always\r\n" +
		"\r\n" +
		"[Install]\r\n" +
		"WantedBy=default.target\r\n"
	target := managedSystemdRunnerTarget()

	patched, err := patchManagedSystemdUnit([]byte(original), target)
	if err != nil {
		t.Fatal(err)
	}

	want := "[Unit]\r\n" +
		"Description=Mohist Runner\r\n" +
		"# preserve this unit comment\r\n" +
		"\r\n" +
		"[Service]\r\n" +
		"# preserve this service comment\r\n" +
		"WorkingDirectory=/managed/releases/runner\r\n" +
		"Environment=\"KEEP=alpha beta\" \"MOHIST_RUNTIME_IDENTITY_PATH=/managed/releases/runner/runtime-identity.json\" \"ALSO=2\"\r\n" +
		"EnvironmentFile=-%h/.config/mohist/runner.env\r\n" +
		"EnvironmentFile=-%h/.config/mohist/runner-managed.env\r\n" +
		"LoadCredential=operator-token:/run/credentials/operator-token\r\n" +
		"LimitNOFILE=1048576\r\n" +
		"ExecStart=\"/usr/bin/node\" \"/managed/releases/runner/dist/cli.js\"\r\n" +
		"Restart=always\r\n" +
		"\r\n" +
		"[Install]\r\n" +
		"WantedBy=default.target\r\n"
	if string(patched) != want {
		t.Fatalf("patched unit differs:\n%s", patched)
	}
}

func TestPatchManagedSystemdUnitWritesAbsoluteWorkingDirectoryWithoutQuotes(t *testing.T) {
	targets := []struct {
		name             string
		target           *managedRuntimeTarget
		workingDirectory string
	}{
		{name: "server", target: managedSystemdServerTarget(), workingDirectory: "/managed/releases/server"},
		{name: "runner", target: managedSystemdRunnerTarget(), workingDirectory: "/managed/releases/runner"},
		{
			name:             "server space and percent",
			target:           managedSystemdServerTargetAt("/managed/release root/100%/server", nil),
			workingDirectory: "/managed/release root/100%%/server",
		},
		{
			name:             "runner space and percent",
			target:           managedSystemdRunnerTargetAt("/managed/release root/100%/runner", nil),
			workingDirectory: "/managed/release root/100%%/runner",
		},
	}
	for _, test := range targets {
		t.Run(test.name, func(t *testing.T) {
			original := []byte("[Service]\nWorkingDirectory=/old\nExecStart=/old/runtime\n")

			patched, err := patchManagedSystemdUnit(original, test.target)
			if err != nil {
				t.Fatal(err)
			}

			want := "WorkingDirectory=" + test.workingDirectory + "\n"
			if !strings.Contains(string(patched), want) {
				t.Fatalf("generated WorkingDirectory is not an unquoted absolute path:\n%s", patched)
			}
			if err := validateManagedUnitTarget(patched, test.target); err != nil {
				t.Fatalf("generated unit target is invalid: %v\n%s", err, patched)
			}
		})
	}
}

func TestFormatManagedSystemdWorkingDirectory(t *testing.T) {
	tests := []struct {
		name    string
		value   string
		want    string
		wantErr bool
	}{
		{name: "plain", value: "/managed/releases/server", want: "/managed/releases/server"},
		{name: "space", value: "/managed/release root/server", want: "/managed/release root/server"},
		{name: "percent", value: "/managed/100%/server", want: "/managed/100%%/server"},
		{name: "relative", value: "managed/server", wantErr: true},
		{name: "leading space", value: " /managed/server", wantErr: true},
		{name: "trailing space", value: "/managed/server ", wantErr: true},
		{name: "double quote", value: "/managed/\"server", wantErr: true},
		{name: "single quote", value: "/managed/'server", wantErr: true},
		{name: "backslash", value: `/managed/\server`, wantErr: true},
		{name: "carriage return", value: "/managed/\rserver", wantErr: true},
		{name: "newline", value: "/managed/\nserver", wantErr: true},
		{name: "nul", value: "/managed/\x00server", wantErr: true},
	}
	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			got, err := formatManagedSystemdWorkingDirectory(test.value)
			if test.wantErr {
				if err == nil {
					t.Fatalf("unsafe WorkingDirectory value was formatted as %q", got)
				}
				return
			}
			if err != nil || got != test.want {
				t.Fatalf("formatManagedSystemdWorkingDirectory(%q) = %q, %v; want %q", test.value, got, err, test.want)
			}
			decoded, err := parseManagedSystemdWorkingDirectory(got)
			if err != nil || decoded != test.value {
				t.Fatalf("parseManagedSystemdWorkingDirectory(%q) = %q, %v; want %q", got, decoded, err, test.value)
			}
		})
	}
}

func TestParseManagedSystemdWorkingDirectoryRejectsUnsafeUnitValues(t *testing.T) {
	values := []string{
		"managed/server",
		`"/managed/server"`,
		"'/managed/server'",
		`/managed/\server`,
		"/managed/100%/server",
	}
	for _, value := range values {
		t.Run(value, func(t *testing.T) {
			if got, err := parseManagedSystemdWorkingDirectory(value); err == nil {
				t.Fatalf("unsafe WorkingDirectory value was parsed as %q", got)
			}
		})
	}
}

func TestPatchManagedSystemdUnitInsertsMissingIdentityBeforeExecStart(t *testing.T) {
	original := "[Unit]\nDescription=Mohist Server\n\n" +
		"[Service]\nWorkingDirectory=/source\nEnvironment=KEEP=1\nExecStart=/source/server\nRestart=always\n"
	target := managedSystemdServerTarget()

	patched, err := patchManagedSystemdUnit([]byte(original), target)
	if err != nil {
		t.Fatal(err)
	}

	want := "[Unit]\nDescription=Mohist Server\n\n" +
		"[Service]\nWorkingDirectory=/managed/releases/server\nEnvironment=KEEP=1\n" +
		"Environment=\"MOHIST_RUNTIME_IDENTITY_PATH=/managed/releases/server/runtime-identity.json\"\n" +
		"ExecStart=\"/managed/releases/server/Mohist.Server\"\nRestart=always\n"
	if string(patched) != want {
		t.Fatalf("patched unit differs:\n%s", patched)
	}
}

func TestPatchManagedSystemdUnitRejectsAmbiguousManagedDirectives(t *testing.T) {
	tests := map[string]string{
		"working directory": "[Service]\nWorkingDirectory=/a\nWorkingDirectory=/b\nExecStart=/old\n",
		"entrypoint":        "[Service]\nWorkingDirectory=/a\nExecStart=/old\nExecStart=/other\n",
		"runtime identity": "[Service]\nWorkingDirectory=/a\n" +
			"Environment=MOHIST_RUNTIME_IDENTITY_PATH=/one\n" +
			"Environment=MOHIST_RUNTIME_IDENTITY_PATH=/two\nExecStart=/old\n",
	}
	for name, unit := range tests {
		t.Run(name, func(t *testing.T) {
			if _, err := patchManagedSystemdUnit([]byte(unit), managedSystemdServerTarget()); err == nil {
				t.Fatal("ambiguous managed directive was accepted")
			}
		})
	}
}

func TestCaptureManagedServiceRejectsInlineCredentialEnvironment(t *testing.T) {
	tests := []struct {
		name        string
		secret      string
		environment string
		execStart   string
		unit        string
	}{
		{name: "MOHIST_TOKEN", secret: "mohist-token-secret-value", environment: "MOHIST_TOKEN=mohist-token-secret-value"},
		{name: "MOHIST_OPERATOR_TOKEN", secret: "operator-token-secret-value", environment: "MOHIST_OPERATOR_TOKEN=operator-token-secret-value"},
		{name: "MOHIST_ENROLLMENT_TOKEN", secret: "enrollment-token-secret-value", environment: "MOHIST_ENROLLMENT_TOKEN=enrollment-token-secret-value"},
		{name: "CUSTOM_SECRET", secret: "custom-secret-value", environment: "CUSTOM_SECRET=custom-secret-value"},
		{name: "api token argument", secret: "api-token-secret-value", execStart: "/old/server/Mohist.Server --api-token=api-token-secret-value"},
		{name: "password argument", secret: "password-secret-value", execStart: "/old/server/Mohist.Server --password password-secret-value"},
		{name: "continued Environment", unit: "[Service]\nWorkingDirectory=/old/server\nEnvironment=SAFE=value \\\n+ continued\nExecStart=/old/server/Mohist.Server\n"},
		{name: "continued ExecStart", unit: "[Service]\nWorkingDirectory=/old/server\nExecStart=/old/server/Mohist.Server \\\n+ --safe-argument\n"},
		{name: "SetCredential", secret: "literal-credential-value", unit: "[Service]\nWorkingDirectory=/old/server\nSetCredential=api:literal-credential-value\nExecStart=/old/server/Mohist.Server\n"},
		{name: "SetCredentialEncrypted", secret: "encrypted-credential-value", unit: "[Service]\nWorkingDirectory=/old/server\nSetCredentialEncrypted=api:encrypted-credential-value\nExecStart=/old/server/Mohist.Server\n"},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			files := newManagedSystemdTestFiles()
			commands := newManagedSystemdTestCommands()
			unitPath := filepath.Join("/units", "mohist.service")
			snapshotPath := filepath.Join("/runtime", "snapshots", "server.service")
			execStart := test.execStart
			if execStart == "" {
				execStart = "/old/server/Mohist.Server"
			}
			environment := ""
			if test.environment != "" {
				environment = "Environment=\"" + test.environment + "\"\n"
			}
			unit := []byte(test.unit)
			if len(unit) == 0 {
				unit = []byte("[Service]\nWorkingDirectory=/old/server\n" + environment + "ExecStart=" + execStart + "\n")
			}
			files.seed(unitPath, unit, 0o640)

			snapshot, err := captureManagedService(
				context.Background(),
				managedUpdateEnvironment{files: files, commands: commands},
				"server",
				"/units",
				snapshotPath,
				managedSystemdServerTarget(),
			)
			if err == nil || snapshot != nil {
				t.Fatalf("capture = %#v, %v; want fail closed", snapshot, err)
			}
			if test.secret != "" && strings.Contains(err.Error(), test.secret) {
				t.Fatalf("capture error leaked inline credential: %v", err)
			}
			if _, ok := files.files[filepath.Clean(snapshotPath)]; ok {
				t.Fatal("inline credential unit was persisted as a snapshot")
			}
			if got := files.files[filepath.Clean(unitPath)]; string(got.value) != string(unit) || got.mode != 0o640 {
				t.Fatalf("unit changed after rejected capture: mode=%o value=%q", got.mode, got.value)
			}
			if len(commands.calls) != 0 {
				t.Fatalf("systemd was accessed after inline credential detection: %#v", commands.calls)
			}
		})
	}
}

func TestCaptureManagedServiceAllowsCredentialEnvironmentFiles(t *testing.T) {
	files := newManagedSystemdTestFiles()
	commands := newManagedSystemdTestCommands()
	unitPath := filepath.Join("/units", "mohist-runner.service")
	snapshotPath := filepath.Join("/runtime", "snapshots", "runner.service")
	oldTarget := managedSystemdRunnerTargetAt("/old/runner", nil)
	unit := []byte("[Service]\nWorkingDirectory=/old/runner\nEnvironment=MOHIST_RUNTIME_IDENTITY_PATH=/old/runner/runtime-identity.json\nEnvironmentFile=-%h/.config/mohist/runner.env\nEnvironmentFile=-/run/credentials/MOHIST_OPERATOR_TOKEN\nExecStart=/usr/bin/node /old/runner/dist/cli.js\n")
	files.seed(unitPath, unit, 0o640)
	commands.active = true
	commands.enabled = true
	commands.properties["FragmentPath"] = unitPath + "\n"
	setManagedSystemdEffectiveTarget(commands, oldTarget)

	snapshot, err := captureManagedService(
		context.Background(),
		managedUpdateEnvironment{files: files, commands: commands},
		"runner",
		"/units",
		snapshotPath,
		oldTarget,
	)
	if err != nil {
		t.Fatal(err)
	}
	if snapshot == nil {
		t.Fatal("EnvironmentFile-only unit did not produce a snapshot")
	}
	persisted, ok := files.files[filepath.Clean(snapshotPath)]
	if !ok || string(persisted.value) != string(unit) || persisted.mode != 0o600 {
		t.Fatalf("snapshot mode/content = %o/%q", persisted.mode, persisted.value)
	}
	requireManagedSystemdCalls(t, commands.calls, [][]string{
		{"--user", "show", "mohist-runner.service", "--property=FragmentPath", "--value"},
		{"--user", "show", "mohist-runner.service", "--property=WorkingDirectory", "--value"},
		{"--user", "show", "mohist-runner.service", "--property=ExecStart", "--value"},
		{"--user", "show", "mohist-runner.service", "--property=Environment", "--value"},
		{"--user", "is-active", "mohist-runner.service"},
		{"--user", "is-enabled", "mohist-runner.service"},
	})
}

func TestCaptureManagedServiceRequiresExactFragmentPath(t *testing.T) {
	tests := []struct {
		name         string
		fragmentPath string
		showFailure  bool
		wantSuccess  bool
	}{
		{name: "exact managed fragment", fragmentPath: "/managed-units/mohist.service\n", wantSuccess: true},
		{name: "foreign fragment", fragmentPath: "/usr/lib/systemd/user/mohist.service\n"},
		{name: "empty fragment", fragmentPath: "\n"},
		{name: "fragment query failure", showFailure: true},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			files := newManagedSystemdTestFiles()
			commands := newManagedSystemdTestCommands()
			oldTarget := managedSystemdServerTargetAt("/old/server", nil)
			unitDir := filepath.Join("/managed-units", ".")
			unitPath := filepath.Join(unitDir, "mohist.service")
			snapshotPath := filepath.Join("/runtime", "snapshots", "server-fragment.service")
			unit := []byte("[Service]\nWorkingDirectory=/old/server\nEnvironment=MOHIST_RUNTIME_IDENTITY_PATH=/old/server/runtime-identity.json\nExecStart=/old/server/Mohist.Server\n")
			files.seed(unitPath, unit, 0o640)
			commands.active = true
			commands.enabled = false
			commands.properties["FragmentPath"] = test.fragmentPath
			setManagedSystemdEffectiveTarget(commands, oldTarget)
			fragmentCommand := []string{"--user", "show", "mohist.service", "--property=FragmentPath", "--value"}
			if test.showFailure {
				commands.failures[strings.Join(fragmentCommand, "\x00")] = 1
			}

			snapshot, err := captureManagedService(
				context.Background(),
				managedUpdateEnvironment{files: files, commands: commands},
				"server",
				unitDir,
				snapshotPath,
				oldTarget,
			)
			if test.wantSuccess {
				if err != nil || snapshot == nil {
					t.Fatalf("capture = %#v, %v", snapshot, err)
				}
				persisted, ok := files.files[filepath.Clean(snapshotPath)]
				if !ok || string(persisted.value) != string(unit) || persisted.mode != 0o600 {
					t.Fatalf("snapshot mode/content = %o/%q", persisted.mode, persisted.value)
				}
				requireManagedSystemdCalls(t, commands.calls, [][]string{
					fragmentCommand,
					{"--user", "show", "mohist.service", "--property=WorkingDirectory", "--value"},
					{"--user", "show", "mohist.service", "--property=ExecStart", "--value"},
					{"--user", "show", "mohist.service", "--property=Environment", "--value"},
					{"--user", "is-active", "mohist.service"},
					{"--user", "is-enabled", "mohist.service"},
				})
				return
			}

			if err == nil || snapshot != nil {
				t.Fatalf("capture = %#v, %v; want fail closed", snapshot, err)
			}
			if _, ok := files.files[filepath.Clean(snapshotPath)]; ok {
				t.Fatal("unowned fragment was persisted as a snapshot")
			}
			if got := files.files[filepath.Clean(unitPath)]; string(got.value) != string(unit) || got.mode != 0o640 {
				t.Fatalf("unit changed after rejected fragment: mode=%o value=%q", got.mode, got.value)
			}
			if !commands.active || commands.enabled {
				t.Fatalf("service state changed after rejected fragment: active=%t enabled=%t", commands.active, commands.enabled)
			}
			if managedSystemdHasMutatingCall(commands.calls) {
				t.Fatalf("systemd mutation after rejected fragment: %#v", commands.calls)
			}
			fragmentCalls := 0
			for _, call := range commands.calls {
				if call.Name == "systemctl" && strings.Join(call.Args, "\x00") == strings.Join(fragmentCommand, "\x00") {
					fragmentCalls++
				}
			}
			if fragmentCalls != 1 {
				t.Fatalf("FragmentPath query count = %d, calls = %#v", fragmentCalls, commands.calls)
			}
		})
	}
}

func TestCaptureManagedServiceRejectsUnitAndPointerArgumentMismatch(t *testing.T) {
	files := newManagedSystemdTestFiles()
	commands := newManagedSystemdTestCommands()
	unitPath := filepath.Join("/units", "mohist.service")
	snapshotPath := filepath.Join("/runtime", "snapshots", "server-args.service")
	previousTarget := managedSystemdServerTargetAt("/old/server", []string{"--mode", "verified"})
	unit := []byte("[Service]\nWorkingDirectory=/old/server\nEnvironment=MOHIST_RUNTIME_IDENTITY_PATH=/old/server/runtime-identity.json\nExecStart=/old/server/Mohist.Server --mode drifted\n")
	files.seed(unitPath, unit, 0o640)
	commands.properties["FragmentPath"] = unitPath + "\n"
	setManagedSystemdEffectiveTarget(commands, previousTarget)

	snapshot, err := captureManagedService(
		context.Background(), managedUpdateEnvironment{files: files, commands: commands},
		"server", "/units", snapshotPath, previousTarget,
	)
	if err == nil || snapshot != nil {
		t.Fatalf("capture = %#v, %v; want argument mismatch failure", snapshot, err)
	}
	if _, ok := files.files[filepath.Clean(snapshotPath)]; ok {
		t.Fatal("argument-mismatched unit was persisted as a snapshot")
	}
	requireManagedSystemdCalls(t, commands.calls, [][]string{
		{"--user", "show", "mohist.service", "--property=FragmentPath", "--value"},
	})
}

func TestCaptureManagedServicePreservesTrackedNonSecretArguments(t *testing.T) {
	files := newManagedSystemdTestFiles()
	commands := newManagedSystemdTestCommands()
	unitPath := filepath.Join("/units", "mohist-runner.service")
	snapshotPath := filepath.Join("/runtime", "snapshots", "runner-args.service")
	arguments := []string{"--log-level", "debug", "--config", "/etc/mohist/runner.json"}
	previousTarget := managedSystemdRunnerTargetAt("/old/runner", arguments)
	unit := []byte("[Service]\nWorkingDirectory=/old/runner\nEnvironment=MOHIST_RUNTIME_IDENTITY_PATH=/old/runner/runtime-identity.json\nExecStart=/usr/bin/node /old/runner/dist/cli.js --log-level debug --config /etc/mohist/runner.json\n")
	files.seed(unitPath, unit, 0o640)
	commands.active = true
	commands.enabled = true
	commands.properties["FragmentPath"] = unitPath + "\n"
	setManagedSystemdEffectiveTarget(commands, previousTarget)

	snapshot, err := captureManagedService(
		context.Background(), managedUpdateEnvironment{files: files, commands: commands},
		"runner", "/units", snapshotPath, previousTarget,
	)
	if err != nil || snapshot == nil {
		t.Fatalf("capture = %#v, %v", snapshot, err)
	}
	if strings.Join(snapshot.PreviousTarget.Arguments, "\x00") != strings.Join(arguments, "\x00") {
		t.Fatalf("tracked arguments = %#v", snapshot.PreviousTarget.Arguments)
	}
	persisted, ok := files.files[filepath.Clean(snapshotPath)]
	if !ok || string(persisted.value) != string(unit) || persisted.mode != 0o600 {
		t.Fatalf("snapshot mode/content = %o/%q", persisted.mode, persisted.value)
	}
	requireManagedSystemdCalls(t, commands.calls, [][]string{
		{"--user", "show", "mohist-runner.service", "--property=FragmentPath", "--value"},
		{"--user", "show", "mohist-runner.service", "--property=WorkingDirectory", "--value"},
		{"--user", "show", "mohist-runner.service", "--property=ExecStart", "--value"},
		{"--user", "show", "mohist-runner.service", "--property=Environment", "--value"},
		{"--user", "is-active", "mohist-runner.service"},
		{"--user", "is-enabled", "mohist-runner.service"},
	})
}

func TestCaptureManagedServiceRejectsEffectiveArgumentOverride(t *testing.T) {
	files := newManagedSystemdTestFiles()
	commands := newManagedSystemdTestCommands()
	unitPath := filepath.Join("/units", "mohist-runner.service")
	snapshotPath := filepath.Join("/runtime", "snapshots", "runner.service")
	oldTarget := managedSystemdRunnerTargetAt("/old/runner", []string{"--mode", "managed"})
	unit := []byte("[Service]\nWorkingDirectory=/old/runner\nEnvironment=MOHIST_RUNTIME_IDENTITY_PATH=/old/runner/runtime-identity.json\nExecStart=/usr/bin/node /old/runner/dist/cli.js --mode managed\n")
	files.seed(unitPath, unit, 0o640)
	commands.active = true
	commands.enabled = true
	commands.properties["FragmentPath"] = unitPath + "\n"
	setManagedSystemdEffectiveTarget(commands, oldTarget)
	commands.properties["ExecStart"] = "{ path=/usr/bin/node ; argv[]=/usr/bin/node /old/runner/dist/cli.js --mode overridden ; ignore_errors=no ; start_time=[n/a] ; stop_time=[n/a] ; pid=0 ; code=(null) ; status=0/0 }\n"

	snapshot, err := captureManagedService(
		context.Background(), managedUpdateEnvironment{files: files, commands: commands},
		"runner", "/units", snapshotPath, oldTarget,
	)
	if err == nil || !strings.Contains(err.Error(), "effective target") {
		t.Fatalf("capture = %#v, error = %v", snapshot, err)
	}
	if files.Exists(snapshotPath) {
		t.Fatal("argument-overridden unit was persisted as a snapshot")
	}
}

func TestParseManagedEffectiveExecStartRejectsMultipleCommands(t *testing.T) {
	property := "{ path=/one ; argv[]=/one ; ignore_errors=no } { path=/two ; argv[]=/two ; ignore_errors=no }"
	if _, err := parseManagedEffectiveExecStart(property); err == nil {
		t.Fatal("multiple effective ExecStart commands were accepted")
	}
}

func TestVerifyManagedEffectiveTargetTreatsWorkingDirectoryAsScalarPath(t *testing.T) {
	target := managedSystemdServerTargetAt("/managed/release root/server", nil)
	commands := newManagedSystemdTestCommands()
	setManagedSystemdEffectiveTarget(commands, target)

	if err := verifyManagedEffectiveTarget(context.Background(), commands, "mohist.service", target); err != nil {
		t.Fatalf("unquoted scalar WorkingDirectory was rejected: %v", err)
	}

	invalid := []string{
		`"` + target.WorkingDirectory + "\"\n",
		" " + target.WorkingDirectory + "\n",
		target.WorkingDirectory + " \n",
	}
	for _, value := range invalid {
		commands.properties["WorkingDirectory"] = value
		if err := verifyManagedEffectiveTarget(context.Background(), commands, "mohist.service", target); err == nil {
			t.Fatalf("invalid effective WorkingDirectory %q was accepted", value)
		}
	}
}

func TestCaptureAndActivateManagedServiceUsesSnapshotAndEffectiveProperties(t *testing.T) {
	files := newManagedSystemdTestFiles()
	commands := newManagedSystemdTestCommands()
	unitPath := filepath.Join("/units", "mohist-runner.service")
	snapshotPath := filepath.Join("/runtime", "transactions", "tx-1", "snapshots", "runner.service")
	dropInPath := filepath.Join("/units", "mohist-runner.service.d", "operator.conf")
	oldTarget := managedSystemdRunnerTargetAt("/old", nil)
	candidateTarget := managedSystemdRunnerTarget()
	original := []byte("[Service]\n# keep\nWorkingDirectory=/old\nEnvironment=MOHIST_RUNTIME_IDENTITY_PATH=/old/runtime-identity.json\nEnvironmentFile=-%h/.config/mohist/runner.env\nLoadCredential=runner:/run/runner\nLimitNOFILE=4096\nExecStart=/usr/bin/node /old/dist/cli.js\n")
	files.seed(unitPath, original, 0o640)
	files.seed(dropInPath, []byte("[Service]\nLimitNOFILE=8192\n"), 0o600)
	commands.active = true
	commands.enabled = true
	commands.properties["FragmentPath"] = unitPath + "\n"
	setManagedSystemdEffectiveTarget(commands, oldTarget)
	env := managedUpdateEnvironment{files: files, commands: commands}

	snapshot, err := captureManagedService(context.Background(), env, "runner", "/units", snapshotPath, oldTarget)
	if err != nil {
		t.Fatal(err)
	}
	if snapshot.UnitPath != unitPath || snapshot.UnitSnapshot != snapshotPath ||
		snapshot.UnitMode != 0o640 || !snapshot.WasActive || !snapshot.WasEnabled {
		t.Fatalf("snapshot = %#v", snapshot)
	}
	value, mode, err := files.ReadFile(snapshotPath)
	if err != nil {
		t.Fatal(err)
	}
	if string(value) != string(original) || mode != 0o600 {
		t.Fatalf("persisted snapshot mode/content = %o/%q", mode, value)
	}
	setManagedSystemdEffectiveTarget(commands, candidateTarget)
	started, err := activateManagedService(context.Background(), env, snapshot, candidateTarget)
	if err != nil {
		t.Fatal(err)
	}
	if !started {
		t.Fatal("successful activation did not report that unit replacement started")
	}
	activated, mode, err := files.ReadFile(unitPath)
	if err != nil {
		t.Fatal(err)
	}
	if mode != 0o640 {
		t.Fatalf("activated unit mode = %o", mode)
	}
	for _, preserved := range []string{
		"# keep\n",
		"EnvironmentFile=-%h/.config/mohist/runner.env\n",
		"LoadCredential=runner:/run/runner\n",
		"LimitNOFILE=4096\n",
	} {
		if !strings.Contains(string(activated), preserved) {
			t.Fatalf("activated unit lost %q: %s", preserved, activated)
		}
	}
	requireManagedSystemdCalls(t, commands.calls, [][]string{
		{"--user", "show", "mohist-runner.service", "--property=FragmentPath", "--value"},
		{"--user", "show", "mohist-runner.service", "--property=WorkingDirectory", "--value"},
		{"--user", "show", "mohist-runner.service", "--property=ExecStart", "--value"},
		{"--user", "show", "mohist-runner.service", "--property=Environment", "--value"},
		{"--user", "is-active", "mohist-runner.service"},
		{"--user", "is-enabled", "mohist-runner.service"},
		{"--user", "daemon-reload"},
		{"--user", "restart", "mohist-runner.service"},
		{"--user", "show", "mohist-runner.service", "--property=WorkingDirectory", "--value"},
		{"--user", "show", "mohist-runner.service", "--property=ExecStart", "--value"},
		{"--user", "show", "mohist-runner.service", "--property=Environment", "--value"},
	})
	assertManagedSystemdDropInUntouched(t, files, dropInPath)
}

func TestActivateManagedServiceAcceptsSpaceAndPercentWorkingDirectory(t *testing.T) {
	targets := []*managedRuntimeTarget{
		managedSystemdServerTargetAt("/managed/release root/100%/server", nil),
		managedSystemdRunnerTargetAt("/managed/release root/100%/runner", nil),
	}
	for _, target := range targets {
		t.Run(target.Component, func(t *testing.T) {
			files, _, env, snapshot := newManagedSystemdActivationFixtureForTarget(target)

			started, err := activateManagedService(context.Background(), env, snapshot, target)
			if err != nil || !started {
				t.Fatalf("activation = started %t, error %v", started, err)
			}
			unit, _, err := files.ReadFile(snapshot.UnitPath)
			if err != nil {
				t.Fatal(err)
			}
			if err := validateManagedUnitTarget(unit, target); err != nil {
				t.Fatalf("activated unit is invalid: %v\n%s", err, unit)
			}
		})
	}
}

func TestSpaceAndPercentActivationFailureRestoresSnapshot(t *testing.T) {
	target := managedSystemdRunnerTargetAt("/managed/release root/100%/runner", nil)
	files, commands, env, snapshot := newManagedSystemdActivationFixtureForTarget(target)
	original, _, err := files.ReadFile(snapshot.UnitSnapshot)
	if err != nil {
		t.Fatal(err)
	}
	commands.properties["WorkingDirectory"] = "/operator/override\n"

	started, activationErr := activateManagedService(context.Background(), env, snapshot, target)
	if activationErr == nil || !started {
		t.Fatalf("activation = started %t, error %v", started, activationErr)
	}
	if err := restoreManagedService(context.Background(), env, snapshot); err != nil {
		t.Fatal(err)
	}
	restored, restoredMode, err := files.ReadFile(snapshot.UnitPath)
	if err != nil {
		t.Fatal(err)
	}
	if string(restored) != string(original) || restoredMode != snapshot.UnitMode {
		t.Fatalf("restored unit mode/content = %o/%q", restoredMode, restored)
	}
}

func TestEffectiveDropInOverrideFailsAndRestoreRecoversServiceState(t *testing.T) {
	files := newManagedSystemdTestFiles()
	commands := newManagedSystemdTestCommands()
	unitPath := filepath.Join("/units", "mohist.service")
	snapshotPath := filepath.Join("/runtime", "transactions", "tx-2", "snapshots", "server.service")
	dropInPath := filepath.Join("/units", "mohist.service.d", "operator.conf")
	oldTarget := managedSystemdServerTargetAt("/old/server", nil)
	candidateTarget := managedSystemdServerTarget()
	original := []byte("[Service]\nWorkingDirectory=/old/server\nEnvironment=KEEP=secret-reference-only MOHIST_RUNTIME_IDENTITY_PATH=/old/server/runtime-identity.json\nExecStart=/old/server/Mohist.Server\n")
	files.seed(unitPath, original, 0o644)
	files.seed(dropInPath, []byte("[Service]\nWorkingDirectory=/operator/override\n"), 0o600)
	commands.active = true
	commands.enabled = false
	commands.properties["FragmentPath"] = unitPath + "\n"
	setManagedSystemdEffectiveTarget(commands, oldTarget)
	env := managedUpdateEnvironment{files: files, commands: commands}

	snapshot, err := captureManagedService(context.Background(), env, "server", "/units", snapshotPath, oldTarget)
	if err != nil {
		t.Fatal(err)
	}
	setManagedSystemdEffectiveTarget(commands, candidateTarget)
	commands.properties["WorkingDirectory"] = "/operator/override\n"
	started, activationErr := activateManagedService(context.Background(), env, snapshot, candidateTarget)
	if activationErr == nil || !strings.Contains(activationErr.Error(), "effective working directory") {
		t.Fatalf("activation error = %v", activationErr)
	}
	if !started {
		t.Fatal("effective override failure did not report that unit replacement started")
	}
	if strings.Contains(activationErr.Error(), "secret-reference-only") {
		t.Fatalf("activation error leaked unit content: %v", activationErr)
	}
	if err := restoreManagedService(context.Background(), env, snapshot); err != nil {
		t.Fatal(err)
	}
	restored, mode, err := files.ReadFile(unitPath)
	if err != nil {
		t.Fatal(err)
	}
	if string(restored) != string(original) || mode != 0o644 {
		t.Fatalf("restored unit mode/content = %o/%q", mode, restored)
	}
	if !commands.active || commands.enabled {
		t.Fatalf("restored service state active=%t enabled=%t", commands.active, commands.enabled)
	}
	wantSuffix := [][]string{
		{"--user", "daemon-reload"},
		{"--user", "disable", "mohist.service"},
		{"--user", "restart", "mohist.service"},
		{"--user", "is-active", "mohist.service"},
		{"--user", "is-enabled", "mohist.service"},
	}
	if len(commands.calls) < len(wantSuffix) {
		t.Fatalf("systemctl calls = %#v", commands.calls)
	}
	requireManagedSystemdCalls(t, commands.calls[len(commands.calls)-len(wantSuffix):], wantSuffix)
	assertManagedSystemdDropInUntouched(t, files, dropInPath)
}

func TestActivateManagedServiceReportsWhetherUnitReplacementMayHaveStarted(t *testing.T) {
	tests := []struct {
		name        string
		configure   func(*managedSystemdTestFiles, *managedSystemdTestCommands, *managedServiceSnapshot)
		wantStarted bool
	}{
		{
			name: "captured snapshot unavailable",
			configure: func(files *managedSystemdTestFiles, _ *managedSystemdTestCommands, snapshot *managedServiceSnapshot) {
				delete(files.files, filepath.Clean(snapshot.UnitSnapshot))
			},
		},
		{
			name: "unit changed after capture",
			configure: func(files *managedSystemdTestFiles, _ *managedSystemdTestCommands, snapshot *managedServiceSnapshot) {
				files.seed(snapshot.UnitPath, []byte("[Service]\nWorkingDirectory=/operator-change\nExecStart=/old/server\n"), snapshot.UnitMode)
			},
		},
		{
			name: "patch validation",
			configure: func(files *managedSystemdTestFiles, _ *managedSystemdTestCommands, snapshot *managedServiceSnapshot) {
				invalid := []byte("[Service]\nWorkingDirectory=/old/server\n")
				files.seed(snapshot.UnitSnapshot, invalid, 0o600)
				files.seed(snapshot.UnitPath, invalid, snapshot.UnitMode)
			},
		},
		{
			name: "atomic unit write",
			configure: func(files *managedSystemdTestFiles, _ *managedSystemdTestCommands, snapshot *managedServiceSnapshot) {
				files.writeFailures[filepath.Clean(snapshot.UnitPath)] = 1
			},
			wantStarted: true,
		},
		{
			name: "daemon reload",
			configure: func(_ *managedSystemdTestFiles, commands *managedSystemdTestCommands, _ *managedServiceSnapshot) {
				commands.failures[strings.Join([]string{"--user", "daemon-reload"}, "\x00")] = 1
			},
			wantStarted: true,
		},
		{
			name: "service restart",
			configure: func(_ *managedSystemdTestFiles, commands *managedSystemdTestCommands, _ *managedServiceSnapshot) {
				commands.failures[strings.Join([]string{"--user", "restart", "mohist.service"}, "\x00")] = 1
			},
			wantStarted: true,
		},
		{
			name: "effective property query",
			configure: func(_ *managedSystemdTestFiles, commands *managedSystemdTestCommands, _ *managedServiceSnapshot) {
				commands.failures[strings.Join([]string{"--user", "show", "mohist.service", "--property=WorkingDirectory", "--value"}, "\x00")] = 1
			},
			wantStarted: true,
		},
	}

	for _, test := range tests {
		t.Run(test.name, func(t *testing.T) {
			files, commands, env, snapshot, target := newManagedSystemdActivationFixture()
			test.configure(files, commands, snapshot)

			started, err := activateManagedService(context.Background(), env, snapshot, target)
			if err == nil {
				t.Fatal("activation unexpectedly succeeded")
			}
			if started != test.wantStarted {
				t.Fatalf("unit replacement started = %t, want %t (error: %v)", started, test.wantStarted, err)
			}
			if strings.Contains(err.Error(), "sensitive") {
				t.Fatalf("activation error leaked fake process or filesystem detail: %v", err)
			}
		})
	}
}

func newManagedSystemdActivationFixture() (
	*managedSystemdTestFiles,
	*managedSystemdTestCommands,
	managedUpdateEnvironment,
	*managedServiceSnapshot,
	*managedRuntimeTarget,
) {
	files := newManagedSystemdTestFiles()
	commands := newManagedSystemdTestCommands()
	unitPath := filepath.Join("/units", "mohist.service")
	snapshotPath := filepath.Join("/runtime", "transactions", "tx-boundary", "snapshots", "server.service")
	original := []byte("[Service]\nWorkingDirectory=/old/server\nEnvironment=KEEP=sensitive-reference-only\nExecStart=/old/server/Mohist.Server\n")
	files.seed(unitPath, original, 0o640)
	files.seed(snapshotPath, original, 0o600)
	target := managedSystemdServerTarget()
	setManagedSystemdEffectiveTarget(commands, target)
	snapshot := &managedServiceSnapshot{
		Component:    "server",
		UnitPath:     unitPath,
		UnitSnapshot: snapshotPath,
		UnitMode:     0o640,
		WasActive:    true,
		WasEnabled:   true,
	}
	return files, commands, managedUpdateEnvironment{files: files, commands: commands}, snapshot, target
}

func newManagedSystemdActivationFixtureForTarget(target *managedRuntimeTarget) (
	*managedSystemdTestFiles,
	*managedSystemdTestCommands,
	managedUpdateEnvironment,
	*managedServiceSnapshot,
) {
	files := newManagedSystemdTestFiles()
	commands := newManagedSystemdTestCommands()
	unitName, _ := managedUnitName(target.Component)
	unitPath := filepath.Join("/units", unitName)
	snapshotPath := filepath.Join("/runtime", "transactions", "tx-special", "snapshots", target.Component+".service")
	original := []byte("[Service]\nWorkingDirectory=/old/runtime\nExecStart=/old/runtime\n")
	files.seed(unitPath, original, 0o640)
	files.seed(snapshotPath, original, 0o600)
	setManagedSystemdEffectiveTarget(commands, target)
	snapshot := &managedServiceSnapshot{
		Component: target.Component, UnitPath: unitPath, UnitSnapshot: snapshotPath,
		UnitMode: 0o640, WasActive: true, WasEnabled: true,
	}
	return files, commands, managedUpdateEnvironment{files: files, commands: commands}, snapshot
}

func managedSystemdRunnerTarget() *managedRuntimeTarget {
	return managedSystemdRunnerTargetAt("/managed/releases/runner", nil)
}

func managedSystemdRunnerTargetAt(root string, arguments []string) *managedRuntimeTarget {
	node := "/usr/bin/node"
	return &managedRuntimeTarget{
		Component:               "runner",
		Entrypoint:              filepath.Join(root, "dist", "cli.js"),
		WorkingDirectory:        root,
		Arguments:               append([]string(nil), arguments...),
		NodeExecutable:          &node,
		DependencyRoot:          &root,
		LaunchMode:              1,
		IsAbsoluteTarget:        true,
		UsesCanonicalEntrypoint: true,
	}
}

func managedSystemdServerTarget() *managedRuntimeTarget {
	return managedSystemdServerTargetAt("/managed/releases/server", nil)
}

func managedSystemdServerTargetAt(root string, arguments []string) *managedRuntimeTarget {
	return &managedRuntimeTarget{
		Component:               "server",
		Entrypoint:              filepath.Join(root, "Mohist.Server"),
		WorkingDirectory:        root,
		Arguments:               append([]string(nil), arguments...),
		LaunchMode:              0,
		IsAbsoluteTarget:        true,
		UsesCanonicalEntrypoint: true,
	}
}

func setManagedSystemdEffectiveTarget(commands *managedSystemdTestCommands, target *managedRuntimeTarget) {
	execStart, _, _ := managedExecStart(target)
	identityPath, _ := managedTargetIdentityPath(target)
	identityAssignment, _ := quoteManagedSystemdValue(managedRuntimeIdentityEnvironment + "=" + identityPath)
	commands.properties["WorkingDirectory"] = target.WorkingDirectory + "\n"
	commands.properties["ExecStart"] = strings.ReplaceAll(execStart, "%%", "%") + "\n"
	commands.properties["Environment"] = strings.ReplaceAll(identityAssignment, "%%", "%") + "\n"
}

type managedSystemdTestFile struct {
	value []byte
	mode  os.FileMode
}

type managedSystemdTestAccess struct {
	operation string
	path      string
}

type managedSystemdTestFiles struct {
	files         map[string]managedSystemdTestFile
	accesses      []managedSystemdTestAccess
	writeFailures map[string]int
}

func newManagedSystemdTestFiles() *managedSystemdTestFiles {
	return &managedSystemdTestFiles{
		files:         map[string]managedSystemdTestFile{},
		writeFailures: map[string]int{},
	}
}

func (files *managedSystemdTestFiles) seed(path string, value []byte, mode os.FileMode) {
	files.files[filepath.Clean(path)] = managedSystemdTestFile{value: append([]byte(nil), value...), mode: mode}
}

func (files *managedSystemdTestFiles) Exists(path string) bool {
	path = filepath.Clean(path)
	files.accesses = append(files.accesses, managedSystemdTestAccess{"exists", path})
	_, ok := files.files[path]
	return ok
}

func (files *managedSystemdTestFiles) ReadFile(path string) ([]byte, os.FileMode, error) {
	path = filepath.Clean(path)
	files.accesses = append(files.accesses, managedSystemdTestAccess{"read", path})
	value, ok := files.files[path]
	if !ok {
		return nil, 0, os.ErrNotExist
	}
	return append([]byte(nil), value.value...), value.mode, nil
}

func (files *managedSystemdTestFiles) WriteFileAtomic(path string, value []byte, mode os.FileMode) error {
	path = filepath.Clean(path)
	files.accesses = append(files.accesses, managedSystemdTestAccess{"write-atomic", path})
	if files.writeFailures[path] > 0 {
		files.writeFailures[path]--
		return errors.New("sensitive filesystem detail")
	}
	files.files[path] = managedSystemdTestFile{value: append([]byte(nil), value...), mode: mode.Perm()}
	return nil
}

func (files *managedSystemdTestFiles) MkdirAll(path string, _ os.FileMode) error {
	files.accesses = append(files.accesses, managedSystemdTestAccess{"mkdir", filepath.Clean(path)})
	return nil
}

func (files *managedSystemdTestFiles) RemoveAll(path string) error {
	path = filepath.Clean(path)
	files.accesses = append(files.accesses, managedSystemdTestAccess{"remove", path})
	for candidate := range files.files {
		if candidate == path || strings.HasPrefix(candidate, path+string(filepath.Separator)) {
			delete(files.files, candidate)
		}
	}
	return nil
}

func (files *managedSystemdTestFiles) Rename(from, to string) error {
	from = filepath.Clean(from)
	to = filepath.Clean(to)
	files.accesses = append(files.accesses, managedSystemdTestAccess{"rename-from", from}, managedSystemdTestAccess{"rename-to", to})
	value, ok := files.files[from]
	if !ok {
		return os.ErrNotExist
	}
	files.files[to] = value
	delete(files.files, from)
	return nil
}

func (files *managedSystemdTestFiles) WalkFiles(root string) ([]string, error) {
	root = filepath.Clean(root)
	files.accesses = append(files.accesses, managedSystemdTestAccess{"walk", root})
	paths := []string{}
	for path := range files.files {
		if strings.HasPrefix(path, root+string(filepath.Separator)) {
			relative, err := filepath.Rel(root, path)
			if err != nil {
				return nil, err
			}
			paths = append(paths, relative)
		}
	}
	sort.Strings(paths)
	return paths, nil
}

func (files *managedSystemdTestFiles) OpenLock(path string) (io.Closer, error) {
	files.accesses = append(files.accesses, managedSystemdTestAccess{"lock", filepath.Clean(path)})
	return managedSystemdTestCloser{}, nil
}

type managedSystemdTestCloser struct{}

func (managedSystemdTestCloser) Close() error { return nil }

type managedSystemdTestCommands struct {
	calls      []managedCommand
	active     bool
	enabled    bool
	properties map[string]string
	failures   map[string]int
}

func newManagedSystemdTestCommands() *managedSystemdTestCommands {
	return &managedSystemdTestCommands{
		properties: map[string]string{},
		failures:   map[string]int{},
	}
}

func (commands *managedSystemdTestCommands) Run(ctx context.Context, command managedCommand) managedCommandResult {
	command.Args = append([]string(nil), command.Args...)
	commands.calls = append(commands.calls, command)
	if ctx.Err() != nil {
		return managedCommandResult{ExitCode: -1}
	}
	if command.Name != "systemctl" || len(command.Args) < 2 || command.Args[0] != "--user" {
		return managedCommandResult{ExitCode: 127}
	}
	key := strings.Join(command.Args, "\x00")
	if commands.failures[key] > 0 {
		commands.failures[key]--
		return managedCommandResult{ExitCode: 1, Stderr: "sensitive command detail"}
	}
	switch command.Args[1] {
	case "is-active":
		if commands.active {
			return managedCommandResult{Stdout: "active\n"}
		}
		return managedCommandResult{ExitCode: 3, Stdout: "inactive\n"}
	case "is-enabled":
		if commands.enabled {
			return managedCommandResult{Stdout: "enabled\n"}
		}
		return managedCommandResult{ExitCode: 1, Stdout: "disabled\n"}
	case "daemon-reload":
		return managedCommandResult{}
	case "restart":
		commands.active = true
		return managedCommandResult{}
	case "stop":
		commands.active = false
		return managedCommandResult{}
	case "enable":
		commands.enabled = true
		return managedCommandResult{}
	case "disable":
		commands.enabled = false
		return managedCommandResult{}
	case "show":
		if len(command.Args) != 5 || !strings.HasPrefix(command.Args[3], "--property=") || command.Args[4] != "--value" {
			return managedCommandResult{ExitCode: 2}
		}
		property := strings.TrimPrefix(command.Args[3], "--property=")
		value, ok := commands.properties[property]
		if !ok {
			return managedCommandResult{ExitCode: 1}
		}
		return managedCommandResult{Stdout: value}
	default:
		return managedCommandResult{ExitCode: 2}
	}
}

func requireManagedSystemdCalls(t *testing.T, actual []managedCommand, expected [][]string) {
	t.Helper()
	if len(actual) != len(expected) {
		t.Fatalf("systemctl calls = %#v, want %#v", actual, expected)
	}
	for index := range expected {
		if actual[index].Name != "systemctl" || strings.Join(actual[index].Args, "\x00") != strings.Join(expected[index], "\x00") {
			t.Fatalf("systemctl call %d = %#v, want %#v", index, actual[index], expected[index])
		}
	}
}

func managedSystemdHasMutatingCall(calls []managedCommand) bool {
	for _, call := range calls {
		if call.Name != "systemctl" || len(call.Args) < 2 {
			continue
		}
		switch call.Args[1] {
		case "daemon-reload", "restart", "stop", "enable", "disable":
			return true
		}
	}
	return false
}

func assertManagedSystemdDropInUntouched(t *testing.T, files *managedSystemdTestFiles, dropInPath string) {
	t.Helper()
	for _, access := range files.accesses {
		if strings.Contains(filepath.ToSlash(access.path), ".service.d/") {
			t.Fatalf("drop-in was accessed by %s: %s", access.operation, access.path)
		}
	}
	value, ok := files.files[filepath.Clean(dropInPath)]
	if !ok || string(value.value) == "" {
		t.Fatalf("drop-in was removed or emptied: %s", dropInPath)
	}
}

var _ managedFileSystem = (*managedSystemdTestFiles)(nil)
var _ managedCommandRunner = (*managedSystemdTestCommands)(nil)
var _ io.Closer = managedSystemdTestCloser{}
