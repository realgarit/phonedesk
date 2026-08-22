#!/usr/bin/env python3
"""Validate the Semgrep scanner boundary before review results are consumed."""

import argparse
from pathlib import Path


def validate_run(exit_code: int, output_exists: bool) -> tuple[bool, str]:
    if exit_code not in (0, 1):
        return False, f"Semgrep scanner failed with exit code {exit_code}."
    if not output_exists:
        return False, "Semgrep scanner produced no output file."
    return True, "Semgrep output is available for review."


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--exit-code", type=int, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    valid, message = validate_run(args.exit_code, args.output.is_file())
    print(message)
    return 0 if valid else 1


if __name__ == "__main__":
    raise SystemExit(main())
