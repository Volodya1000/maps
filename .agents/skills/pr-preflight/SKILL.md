---
name: pr-preflight
description: Prepare this repository's working tree for a pull request by reviewing its diff, selecting existing local checks, checking tests, architecture, and the required changelog fragment, and reporting verified versus unavailable checks. Do not use for an unrelated code review.
---

# PR preflight

Use the repository's checks; this workflow neither replaces CI nor dispatches
GitHub Actions.

1. Read `AGENTS.md`, `docs/development.md`, and inspect `git diff --check` plus
   the staged and unstaged diff. Identify affected areas: C# production/tests,
   routing, AXAML/styles, geodata, terrain, Android, tooling, or documentation.
2. Select only existing applicable commands from `tools/check.py`: always run
   `lint` for a proposed PR; run `desktop` for C# or test changes; add `data`,
   `terrain`, or `android` only when their documented path is affected. Run
   `uv run --locked pre-commit run --all-files` and the pre-push stage when its
   hooks apply. Do not rerun an unchanged full suite merely to duplicate CI.
3. Check the diff for a focused regression test when observable behavior
   changed. Check `docs/architecture.md` for layer direction, Domain BCL-only
   rules, routing ownership, and composition-root registrations. For UI changes,
   check `docs/ui-styling.md`; for routing changes, invoke `routing-regression`.
4. Ensure exactly this PR adds a non-empty
   `changes/<issue-or-pr>.<feature|bugfix|refactor|doc>.md` fragment. Before the
   staged hook, stage it and use the documented `CHANGELOG_BASE` when the PR is
   dependent. Do not edit `CHANGELOG.md`.
5. Produce this concise report:

   ```text
   Diff scope: ...
   Regression evidence: test / not needed / gap: ...
   Architecture and changelog: pass / issue: ...
   Checks run: command — PASS|FAIL
   Not run: command — NOT VERIFIED (concrete prerequisite or platform reason)
   PR status: READY | BLOCKED
   ```

Done means all applicable available checks pass and the report distinguishes
actual execution from unavailable Android, Windows, emulator, or other
platform evidence.
