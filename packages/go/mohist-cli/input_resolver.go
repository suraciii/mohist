package mohistcli

import (
	"encoding/json"
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

// resolveStageModelMap reads a stage-model carrier (inline value or its -file
// twin, including "-" for stdin) and validates it as a JSON object whose
// members are all strings. Presence is tracked by a nil-able map: neither flag
// supplied returns nil, while a supplied object (including "{}") returns a
// non-nil map so an explicit empty object is distinguishable from absence.
//
// json.Unmarshal into map[string]string is not a validator: it accepts a null
// member and silently yields "". Decoding into map[string]any and asserting
// each value is a string rejects JSON null, bool, number, array, and object
// members, plus malformed JSON, a top-level null, and non-object roots.
func resolveStageModelMap(deps Dependencies, cmd command, inlineFlag, fileFlag string) (map[string]string, error) {
	inline, file := hasArg(cmd.args, inlineFlag), hasArg(cmd.args, fileFlag)
	if !inline && !file {
		return nil, nil
	}
	raw, err := resolveTextInput(deps, cmd, inlineFlag, fileFlag)
	if err != nil {
		return nil, err
	}
	flag := "--" + inlineFlag
	if file {
		flag = "--" + fileFlag
	}
	var decoded map[string]any
	if err := json.Unmarshal([]byte(raw), &decoded); err != nil || decoded == nil {
		return nil, usage(flag + " must be a JSON object of string values")
	}
	out := make(map[string]string, len(decoded))
	for key, value := range decoded {
		text, ok := value.(string)
		if !ok {
			return nil, usage(flag + " must be a JSON object of string values")
		}
		out[key] = text
	}
	return out, nil
}
