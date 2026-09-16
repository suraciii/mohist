package mohistcli

import (
	"fmt"
	"io"
)

// Carrier presence is distinct from content: an empty path is still a read
// attempt, while an empty successful read may deliberately clear a field.
func resolveTextInput(deps Dependencies, cmd command, valueFlag, fileFlag string) (string, error) {
	if valueFlag != "" && hasArg(cmd.args, valueFlag) {
		return argValue(cmd.args, valueFlag, ""), nil
	}
	if fileFlag == "" || !hasArg(cmd.args, fileFlag) {
		return "", nil
	}
	raw := argValue(cmd.args, fileFlag, "")
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
