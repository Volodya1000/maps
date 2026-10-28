"""Select optional PR artifacts without disabling required verification jobs."""

import json
import os


def full_maps(event_name, labels):
    return event_name == "pull_request" and (
        "ci:skip-full-maps" not in labels or "ci:full-maps" in labels
    )


if __name__ == "__main__":
    enabled = full_maps(
        os.environ.get("EVENT_NAME", ""),
        json.loads(os.environ.get("PR_LABELS", "null")) or [],
    )
    print("full_maps=" + str(enabled).lower())
