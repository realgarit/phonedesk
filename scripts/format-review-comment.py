#!/usr/bin/env python3
"""Format Semgrep JSON + AI review text into one PR comment body.

Usage: format-review-comment.py <semgrep.json> <ai-review.txt>
Prints the formatted comment to stdout.
"""
import json
import sys


def single_line(value, limit=240):
    text = " ".join(str(value).split()).replace("`", "'")
    return text[:limit]


def format_semgrep_error(error):
    if not isinstance(error, dict):
        return single_line(error)

    spans = error.get("spans")
    first_span = spans[0] if isinstance(spans, list) and spans else {}
    path = error.get("path")
    if not path and isinstance(first_span, dict):
        path = first_span.get("file")

    error_type = error.get("type") or error.get("level") or "scanner error"
    location = f" in `{single_line(path)}`" if path else ""
    message = f": {single_line(error['message'])}" if error.get("message") else ""
    return f"{single_line(error_type)}{location}{message}"


def format_semgrep(path):
    with open(path) as f:
        data = json.load(f)
    results = data.get("results", [])
    errors = data.get("errors", [])
    if errors:
        lines = ["Semgrep reported scanner errors; this review is not clean:"]
        lines.extend(f"- {format_semgrep_error(error)}" for error in errors[:5])
        if len(errors) > 5:
            lines.append(f"- ...and {len(errors) - 5} more scanner error(s).")
        return "\n".join(lines)
    if not results:
        return "No Semgrep findings."
    lines = []
    for r in results:
        path_ = r.get("path", "?")
        line = r.get("start", {}).get("line", "?")
        severity = r.get("extra", {}).get("severity", "?")
        check_id = r.get("check_id", "?")
        message = r.get("extra", {}).get("message", "").strip()
        lines.append(f"- **{path_}:{line}** ({severity}, `{check_id}`) - {message}")
    return "\n".join(lines)


def format_ai_review(path):
    with open(path) as f:
        text = f.read().strip()
    return text if text else "No findings."


def main():
    semgrep_path, ai_review_path = sys.argv[1], sys.argv[2]
    semgrep_section = format_semgrep(semgrep_path)
    ai_section = format_ai_review(ai_review_path)
    print("## Automated review\n")
    print("### Semgrep (deterministic)\n")
    print(semgrep_section)
    print()
    print("### AI review (security + code quality)\n")
    print(ai_section)


if __name__ == "__main__":
    main()
