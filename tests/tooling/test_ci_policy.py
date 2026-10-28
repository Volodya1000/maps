import unittest

from tools.ci_policy import full_maps


class PreviewPolicyTests(unittest.TestCase):
    def test_each_pr_has_both_maps_by_default(self):
        self.assertTrue(full_maps("pull_request", []))

    def test_fast_label_omits_country_but_explicit_full_label_overrides_it(self):
        self.assertFalse(full_maps("pull_request", ["ci:skip-full-maps"]))
        self.assertTrue(
            full_maps("pull_request", ["ci:skip-full-maps", "ci:full-maps"])
        )

    def test_main_push_does_not_publish_pr_previews(self):
        self.assertFalse(full_maps("push", []))
