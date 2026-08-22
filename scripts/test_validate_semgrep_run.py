import importlib.util
import tempfile
import unittest
from pathlib import Path


SCRIPT_PATH = Path(__file__).with_name("validate-semgrep-run.py")
SPEC = importlib.util.spec_from_file_location("validate_semgrep_run", SCRIPT_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Could not load {SCRIPT_PATH}")
MODULE = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(MODULE)


class ValidateSemgrepRunTests(unittest.TestCase):
    def test_no_findings_exit_with_output_is_valid(self):
        valid, message = MODULE.validate_run(0, True)

        self.assertTrue(valid, message)

    def test_findings_exit_with_output_is_valid_for_later_review_processing(self):
        valid, message = MODULE.validate_run(1, True)

        self.assertTrue(valid, message)

    def test_scanner_failure_is_not_replaced_with_empty_results(self):
        valid, message = MODULE.validate_run(2, False)

        self.assertFalse(valid)
        self.assertIn("scanner failed", message.lower())

    def test_missing_output_is_invalid_even_when_scanner_exit_is_zero(self):
        valid, message = MODULE.validate_run(0, False)

        self.assertFalse(valid)
        self.assertIn("output", message.lower())


if __name__ == "__main__":
    unittest.main()
