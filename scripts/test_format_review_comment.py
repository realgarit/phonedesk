import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


SCRIPT_PATH = Path(__file__).with_name("format-review-comment.py")
SPEC = importlib.util.spec_from_file_location("format_review_comment", SCRIPT_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Could not load {SCRIPT_PATH}")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class FormatReviewCommentTests(unittest.TestCase):
    def test_scanner_errors_are_reported_instead_of_a_clean_result(self):
        with tempfile.TemporaryDirectory() as directory:
            semgrep_path = Path(directory) / "semgrep.json"
            semgrep_path.write_text(
                json.dumps(
                    {
                        "results": [],
                        "errors": [
                            {
                                "type": "PartialParsing",
                                "message": "Syntax error",
                                "path": "src/example.cs",
                            }
                        ],
                    }
                ),
                encoding="utf-8",
            )

            formatted = MODULE.format_semgrep(semgrep_path)

        self.assertIn("scanner error", formatted.lower())
        self.assertIn("src/example.cs", formatted)
        self.assertIn("PartialParsing", formatted)
        self.assertNotEqual("No Semgrep findings.", formatted)


if __name__ == "__main__":
    unittest.main()
