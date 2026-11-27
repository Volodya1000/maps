# Routing quality research implementation plan

> **For agentic workers:** Execute inline with superpowers:executing-plans;
> bounded corpus work may run independently, followed by a fresh whole-branch review.

**Goal:** Reproducible 300-case Belarus research with honest deterministic analysis.
**Architecture:** Extend existing Python runners and independent auditors; no router changes.
**Tech Stack:** Python 3.12, osmium/Shapely, SQLite, existing .NET Verify, GitHub Actions.
**Spec:** `docs/superpowers/specs/2026-11-27-routing-quality-design.md`.

## Global constraints

- Preserve the frozen 88 inputs and methodology, all routing algorithm and Domain code.
- Freeze 212 new cases before execution; 170 development + 42 spatially separate holdout.
- Pin source/data/corpus/algorithm hashes, seed and execution budgets.
- Rail crossing allowed; restricted land is a documented model limitation.
- Seven result classes, independent proofs, explicit hypotheses, separate raw artifacts.
- Create PR to main and wait for CI, never merge it.

## Review focus

- A blocked direct segment cannot justify refusal or establish a search defect.
- Unknown/incomplete audit cannot be reported as an invalid or valid route.
- Holdout location or individual failure cannot influence development/backlog.
- Missing results, duplicate IDs, evidence tampering and incompatible baseline fail closed.
- Memory and fallback data must be measured, with unavailable metrics kept null.

## Task 1: Freeze OSM corpus

Files: `tools/routing_research_corpus.py`, `samples/routing-research-v1.json`,
generation receipt and focused `test_routing_research_corpus.py`.
Interface: versioned manifest containing new cases, labels/evidence/region/split,
source/provenance and generation policy. Original 88 remain referenced by hash.

- [x] Write/run selection tests RED; implement deterministic tag/category selection,
  geographic and distance diversity, de-duplication and spatial holdout; run GREEN.
- [x] Generate from pinned real PBF and current verified database, inspect quota
  coverage and freeze exact bytes before any algorithm run.

## Task 2: Evidence classification and aggregation

Files: `tools/routing_research_analysis.py`, `test_routing_research_analysis.py`.
Interface: `classify(record, scenario, witnesses)` and `summarize(...)` producing
seven-way outcomes, evidence/confidence groups and bounded Markdown/JSON.

- [x] Write/run RED cases for every classification and precedence, proof rejection,
  multiple hypotheses, held-out representatives exclusion, mismatched baselines.
- [x] Implement classification/grouping/strata/performance/cost/version comparisons;
  verify GREEN and incomplete evidence fail-closed.

## Task 3: Full experiment runner and measurements

Files: `tools/routing_research.py`, `tools/CableRoutePlanner.Verify/CrossingPilot.cs`
only if needed for process RSS; `test_routing_research.py`.
Consumes Task 1 manifest and existing frozen runner; emits raw artifacts for Task 2.

- [x] Test RED complete inventories, budgets, manifest tamper and raw/provenance checks.
- [x] Reuse runner isolation/audits; persist partial progress, seven-way reports,
  memory measurement, recoveries/independent witnesses and optional same-data baseline.
- [x] Build current Verify, execute full frozen+development+holdout baseline after
  corpus freeze, revalidate evidence and publish compact evidence to docs.

## Task 4: CI, documentation, independent review and PR

Files: `.github/workflows/routing-research.yml`, user docs, evidence, changelog.

- [x] Add bounded manually dispatched workflow retaining existing regression checks.
- [x] Document reproduction, sealed holdout policy, proof limits, metrics and prioritized
  future PRs; add own nonempty changelog fragment.
- [x] Run data/lint/desktop and hooks; independent whole-branch review, fix important
  findings with regression tests; commit using Conventional Commits.
- [x] Push feature branch and create PR #71 against main.
- [ ] Wait for final CI including full research evaluation, fix failures and deliver
  compact results with validation links. Live completion status is tracked in PR #71.
  Do not merge.
