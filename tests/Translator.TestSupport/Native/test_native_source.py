"""Behavioral checks for native causal-mutation selection."""

from pathlib import Path
import sys
import unittest

sys.path.insert(
    0, str(Path(__file__).resolve().parents[3] / "src/Win16.Runtime/Native")
)
from native_source import replace_c


class NativeMutationTests(unittest.TestCase):
    def test_mutation_survives_whitespace_and_comments(self):
        source = "result = source /* retained layout */ +\n    count;"
        self.assertEqual("result = 0;", replace_c(source, "source+count", "0"))

    def test_comment_and_string_mentions_do_not_select_a_mutation(self):
        source = '/* result = 1; */ log("result = 1;"); result = 1;'
        self.assertEqual(
            '/* result = 1; */ log("result = 1;"); result = 2;',
            replace_c(source, "result = 1;", "result = 2;"),
        )

    def test_identifier_prefixes_do_not_select_a_mutation(self):
        with self.assertRaises(ValueError):
            replace_c("long_result = 1;", "result = 1;", "result = 2;")

    def test_absent_mutation_fails(self):
        with self.assertRaises(ValueError):
            replace_c("result = 2;", "result = 1;", "result = 3;")

    def test_only_first_matching_site_changes(self):
        self.assertEqual(
            "result = 2; result = 1;",
            replace_c("result = 1; result = 1;", "result = 1;", "result = 2;"),
        )


if __name__ == "__main__":
    unittest.main()
