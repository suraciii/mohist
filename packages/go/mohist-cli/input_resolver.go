package mohistcli

import (
	"fmt"
	"io"
)

// resolveTextInput returns the exact text from the selected carrier. valueFlag
// is the short-text flag (empty for complete-document input such as --file);
// fileFlag is the file/stdin flag. When valueFlag is set and present, its exact
// argument is returned with no error. When the fileFlag is "-", stdin is
// consumed exactly once and returned as-is, including any trailing newlines.
// Otherwise the fileFlag value is treated as a path and read through
// deps.ReadFile. The returned string is the raw, untrimmed content; the helper
// performs no whitespace or newline normalization and never substitutes a
// fallback value when a read fails.
//
// A read failure returns an error whose message identifies the originating
// flag and the carrier source ("-", "stdin", or the quoted path). Callers must
// treat the error as a local usage failure (ExitUsage=2) and stop before any
// Project-state lookup, HTTP call, or local target write. A read that returns
// partial bytes together with an error is treated as a failure: the partial
// bytes are discarded.
//
// stdin is consumed at most once per command so the resolved value can be
// reused across pre-flight and mutation paths without producing a different
// payload.
func resolveTextInput(deps Dependencies, cmd command, valueFlag, fileFlag string) (string, error) {
	if valueFlag != "" && hasArg(cmd.args, valueFlag) {
		return argValue(cmd.args, valueFlag, ""), nil
	}
	if fileFlag == "" {
		return "", nil
	}
	raw := argValue(cmd.args, fileFlag, "")
	if !hasArg(cmd.args, fileFlag) {
		return "", nil
	}
	flag := "--" + fileFlag
	if raw == "-" {
		data, err := io.ReadAll(deps.Input)
		if err != nil {
			return "", fmt.Errorf("error: could not read %s - from stdin: %v", flag, err)
		}
		return string(data), nil
	}
	data, err := deps.ReadFile(raw)
	if err != nil {
		return "", fmt.Errorf("error: could not read %s %q: %v", flag, raw, err)
	}
	return data, nil
}
