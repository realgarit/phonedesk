#!/usr/bin/env python3
"""Validate that the automated review produced a trustworthy result."""

import argparse
import json
import re
from pathlib import Path


INCONCLUSIVE_PATTERN = re.compile(
    r"session\s+limit|quota|rate\s+limit|timed?\s+out|timeout|"
    r"service\s+unavailable|ai\s+review\s+step\s+failed|"
    r"authentication|not\s+set",
    re.IGNORECASE,
)


def _model_exit_code(status_path: Path) -> tuple[bool, int | None, str]:
    if not status_path.is_file():
        return False, None, "AI review is inconclusive: model status is missing."

    try:
        status_text = status_path.read_text(encoding="utf-8")
    except OSError:
        return False, None, "AI review is inconclusive: model status cannot be read."

    values = {}
    for line in status_text.splitlines():
        if "=" in line:
            name, value = line.split("=", 1)
            values[name.strip()] = value.strip()

    raw_exit = values.get("model_exit")
    try:
        exit_code = int(raw_exit) if raw_exit is not None else None
    except ValueError:
        exit_code = None

    if exit_code is None:
        return False, None, "AI review is inconclusive: model exit code is invalid."
    return True, exit_code, ""


def validate_paths(
    semgrep_path: Path,
    review_path: Path,
    status_path: Path,
    stderr_path: Path,
) -> tuple[bool, str]:
    status_valid, model_exit, status_message = _model_exit_code(status_path)
    if not status_valid:
        return False, status_message
    if model_exit != 0:
        return (
            False,
            f"AI review is inconclusive: provider exited with code {model_exit}.",
        )

    try:
        review_text = review_path.read_text(encoding="utf-8").strip()
    except OSError:
        return False, "AI review is inconclusive: review output cannot be read."
    if not stderr_path.is_file():
        return False, "AI review is inconclusive: provider stderr is missing."
    try:
        stderr_text = stderr_path.read_text(encoding="utf-8").strip()
    except OSError:
        return False, "AI review is inconclusive: provider stderr cannot be read."
    if INCONCLUSIVE_PATTERN.search(f"{review_text}\n{stderr_text}"):
        return False, "AI review is inconclusive: provider reported a limit or runtime failure."

    try:
        semgrep_output = json.loads(semgrep_path.read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return False, "Semgrep output is invalid; review cannot be considered clean."

    if not isinstance(semgrep_output, dict):
        return False, "Semgrep output is invalid; review cannot be considered clean."

    semgrep_results = semgrep_output.get("results")
    if not isinstance(semgrep_results, list):
        return False, "Semgrep output is invalid; review cannot be considered clean."

    semgrep_errors = semgrep_output.get("errors", [])
    if semgrep_errors:
        return False, "Semgrep output reports scanner errors; review cannot be considered clean."

    if semgrep_results:
        return False, f"Semgrep reported {len(semgrep_results)} finding(s)."

    ai_findings = [line for line in review_text.splitlines() if line.startswith("- **")]
    if ai_findings:
        return False, f"AI review reported {len(ai_findings)} finding(s)."
    if review_text != "No findings.":
        return False, "AI review output is neither an exact clean result nor a finding list."

    return True, "Automated review is clean and complete."


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--semgrep", type=Path, required=True)
    parser.add_argument("--review", type=Path, required=True)
    parser.add_argument("--status", type=Path, required=True)
    parser.add_argument("--stderr", type=Path, required=True)
    args = parser.parse_args()

    valid, message = validate_paths(args.semgrep, args.review, args.status, args.stderr)
    print(message)
    return 0 if valid else 1


if __name__ == "__main__":
    raise SystemExit(main())
