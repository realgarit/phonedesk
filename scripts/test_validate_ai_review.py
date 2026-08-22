import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


SCRIPT_PATH = Path(__file__).with_name("validate-ai-review.py")
SPEC = importlib.util.spec_from_file_location("validate_ai_review", SCRIPT_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Could not load {SCRIPT_PATH}")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ValidateAiReviewTests(unittest.TestCase):
    def run_validation(self, semgrep, review, status, stderr=""):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            semgrep_path = root / "semgrep.json"
            review_path = root / "ai-review.txt"
            status_path = root / "ai-review-status.txt"
            stderr_path = root / "ai-review-stderr.txt"
            semgrep_path.write_text(json.dumps(semgrep), encoding="utf-8")
            review_path.write_text(review, encoding="utf-8")
            status_path.write_text(status, encoding="utf-8")
            stderr_path.write_text(stderr, encoding="utf-8")
            return MODULE.validate_paths(
                semgrep_path,
                review_path,
                status_path,
                stderr_path,
            )

    def test_exact_no_findings_with_successful_model_is_clean(self):
        valid, message = self.run_validation(
            {"results": []},
            "No findings.\n",
            "model_exit=0\n",
        )

        self.assertTrue(valid, message)

    def test_session_limit_is_inconclusive_even_when_provider_exits_zero(self):
        valid, message = self.run_validation(
            {"results": []},
            "You've hit your session limit · resets 12:30pm (UTC)\n",
            "model_exit=0\n",
        )

        self.assertFalse(valid)
        self.assertIn("inconclusive", message.lower())

    def test_session_limit_on_stderr_is_inconclusive_even_when_stdout_is_clean(self):
        valid, message = self.run_validation(
            {"results": []},
            "No findings.\n",
            "model_exit=0\n",
            "You've hit your session limit · resets 12:30pm (UTC)\n",
        )

        self.assertFalse(valid)
        self.assertIn("inconclusive", message.lower())

    def test_any_provider_stderr_is_inconclusive_even_when_stdout_is_clean(self):
        valid, message = self.run_validation(
            {"results": []},
            "No findings.\n",
            "model_exit=0\n",
            "fatal: transient provider failure\n",
        )

        self.assertFalse(valid)
        self.assertIn("inconclusive", message.lower())

    def test_provider_failure_is_inconclusive_even_if_output_says_no_findings(self):
        valid, message = self.run_validation(
            {"results": []},
            "No findings.\n",
            "model_exit=1\n",
        )

        self.assertFalse(valid)
        self.assertIn("exited with code", message.lower())

    def test_ai_finding_fails_validation(self):
        valid, message = self.run_validation(
            {"results": []},
            "- **src/example.cs:10** - A concrete finding.\n",
            "model_exit=0\n",
        )

        self.assertFalse(valid)
        self.assertIn("finding", message.lower())

    def test_semgrep_finding_fails_validation(self):
        valid, message = self.run_validation(
            {"results": [{"check_id": "example.rule"}]},
            "No findings.\n",
            "model_exit=0\n",
        )

        self.assertFalse(valid)
        self.assertIn("semgrep", message.lower())

    def test_semgrep_requires_results_array_and_rejects_scanner_errors(self):
        for payload in (
            {},
            {"results": None},
            {"errors": ["scanner failed"]},
            {"results": [], "errors": None},
        ):
            valid, message = self.run_validation(
                payload,
                "No findings.\n",
                "model_exit=0\n",
            )

            self.assertFalse(valid, payload)
            self.assertIn("semgrep", message.lower())


if __name__ == "__main__":
    unittest.main()
