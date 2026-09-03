#!/usr/bin/env python3
"""Reconstruct the xUnit collection timeline from an L1 test TRX file.

Usage: python3 scripts/analyze-spectests-trx.py <run.trx> [source-root]
"""

import glob
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict
from datetime import datetime


NS = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
DEFAULT_SOURCE_ROOT = "packages/server/tests/Mohist.Server.Tests"


def duration_seconds(value: str) -> float:
    hours, minutes, seconds = value.split(":")
    return int(hours) * 3600 + int(minutes) * 60 + float(seconds)


def collection_memberships(source_root: str) -> dict[str, str | None]:
    collections: dict[str, str | None] = {}
    source_files = glob.glob(f"{source_root}/**/*.cs", recursive=True)

    for path in source_files:
        with open(path, encoding="utf-8") as source_file:
            source = source_file.read()
        for match in re.finditer(
            r'\[Collection\("([^"]+)"\)\]\s*(?:\[[^\]]*\]\s*)*'
            r"public\s+(?:sealed\s+)?(?:abstract\s+)?class\s+(\w+)",
            source,
        ):
            collections[match.group(2)] = match.group(1)

    inherited_collection = collections.get("WorkflowGrainSpecs")
    for path in source_files:
        with open(path, encoding="utf-8") as source_file:
            source = source_file.read()
        for match in re.finditer(
            r"class\s+(\w+)\s*:\s*(?:[\w.]+\.)?WorkflowGrainSpecs",
            source,
        ):
            collections.setdefault(match.group(1), inherited_collection)

    return collections


def main() -> None:
    if len(sys.argv) not in (2, 3):
        raise SystemExit(
            "usage: analyze-spectests-trx.py <run.trx> [source-root]"
        )

    source_root = sys.argv[2] if len(sys.argv) == 3 else DEFAULT_SOURCE_ROOT
    root = ET.parse(sys.argv[1]).getroot()
    class_by_test_id = {
        definition.get("id"): definition.find(NS + "TestMethod").get("className")
        for definition in root.iter(NS + "UnitTest")
    }
    collections = collection_memberships(source_root)

    rows: list[tuple[str, float, float]] = []
    outcomes: Counter[str] = Counter()
    for result in root.iter(NS + "UnitTestResult"):
        outcome = result.get("outcome", "Unknown")
        outcomes[outcome] += 1
        if (
            outcome != "NotExecuted"
            and result.get("startTime")
            and result.get("duration")
        ):
            class_name = class_by_test_id.get(result.get("testId"), "?").split(".")[-1]
            rows.append(
                (
                    class_name,
                    datetime.fromisoformat(result.get("startTime")).timestamp(),
                    duration_seconds(result.get("duration")),
                )
            )

    if not rows:
        raise SystemExit("trx contains no executed test results")

    timeline_start = min(start for _, start, _ in rows)
    aggregate = defaultdict(lambda: [float("inf"), 0.0, 0.0, 0])
    classes = defaultdict(lambda: [0.0, 0])
    for class_name, start, duration in rows:
        collection = collections.get(class_name) or f"(default) {class_name}"
        values = aggregate[collection]
        values[0] = min(values[0], start - timeline_start)
        values[1] = max(values[1], start - timeline_start + duration)
        values[2] += duration
        values[3] += 1
        classes[(collection, class_name)][0] += duration
        classes[(collection, class_name)][1] += 1

    test_window = max(values[1] for values in aggregate.values())
    outcome_text = " ".join(
        f"{outcome}={count}" for outcome, count in sorted(outcomes.items())
    )
    duration_sum = sum(duration for _, _, duration in rows)
    print(
        f"test-window {test_window:.1f}s | {outcome_text} | "
        f"sum(dur) {duration_sum:.1f}s"
    )
    print(f'{"collection":42} {"cost":>7} {"tests":>5} {"span":>15}')
    for collection, values in sorted(
        aggregate.items(), key=lambda item: -item[1][2]
    )[:30]:
        print(
            f"{collection:42} {values[2]:6.1f}s {values[3]:5} "
            f"{values[0]:6.1f}->{values[1]:6.1f}s"
        )

    print("\nslowest classes:")
    for (collection, class_name), values in sorted(
        classes.items(), key=lambda item: -item[1][0]
    )[:30]:
        print(f"  {values[0]:6.1f}s {values[1]:4}  {collection} / {class_name}")

    print("\nlast to finish:")
    for collection, values in sorted(
        aggregate.items(), key=lambda item: item[1][1]
    )[-8:]:
        print(f"  finish {values[1]:5.1f}s  {collection}")


if __name__ == "__main__":
    main()
