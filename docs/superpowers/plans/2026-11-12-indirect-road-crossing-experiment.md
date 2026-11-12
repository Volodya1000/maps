# Indirect Road Crossing Experiment Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce PR #51 with reproducible evidence explaining whether the existing router discovers an indirect, model-allowed road crossing from exactly two user points.

**Architecture:** Reuse the PR #50 Python audit/rendering helpers and the existing `CableRoutePlanner.Verify --crossing` composition root. Freeze one focused Smolevichi scenario and generate its source geometry, independent direct/witness audits, original router output, route audit, and SVG maps from the pinned Belarus SQLite package. Production routing code changes only if the reproduced evidence isolates a local defect.

**Tech Stack:** Python 3.12, Shapely 2.1.2, SQLite, .NET 8, xUnit/unittest, GitHub Actions.

**Spec:** `docs/routing-crossing-experiment-strategy.md` and the PR #51 goal supplied on 2026-11-12.

## Global Constraints

- Start from `main` at merge commit `75b645b` and use database SHA-256 `2e304811624b88bcbd664f7a78d3ee915bb37c5260396b5d7d72ce2dd9f154e6`.
- Pass exactly two user points to the existing router; witness vertices are audit evidence only.
- Do not create another A* implementation or weaken Domain traversal rules.
- Keep the geographic search bounded to the confirmed Smolevichi crossing and record rejected attempts.
- Do not expand the corpus to 100–200 scenarios.

## Review Focus

- The direct segment must contain a specifically identified forbidden road contact while the witness is valid under current rules.
- Every returned alternative must be independently audited, including endpoint and coverage checks.
- The baseline artifacts must be original outputs of the final PR revision, not reconstructed output.
- The focused workflow must fail on missing/invalid evidence while preserving normal PR CI behavior.
- Any conditional production fix must have a failing-then-passing regression and must not change Domain policy.

---

### Task 1: Freeze and validate the focused scenario

**Files:**
- Create: `samples/routing-indirect-smolevichi.json`
- Create: `tools/routing_indirect_study.py`
- Modify: `tools/GeodataPreparation/tests/test_routing_crossing_pilot.py`

**Interfaces:**
- Consumes: `routing_crossing_pilot.query`, `project`, `audit`, `svg_map`, and the pinned country package.
- Produces: a deterministic scenario plus `prepare(data, output)` that validates hashes, coverage, the forbidden direct segment, and the allowed witness before routing.

- [ ] Add a failing unittest that requires exactly two user points, a direct `violation`, a witness `valid`, candidate `192543`, and no witness vertices in `points`.
- [ ] Run the focused unittest and confirm it fails because the study module/scenario is absent.
- [ ] Implement the minimal focused preparation path and frozen scenario.
- [ ] Run all routing-pilot Python tests and confirm nonzero discovery with all tests passing.
- [ ] Commit the focused scenario and pre-route audit implementation.

### Task 2: Run and report the existing router

**Files:**
- Modify: `tools/routing_indirect_study.py`
- Create: `docs/evidence/routing-indirect-smolevichi/` generated evidence files.

**Interfaces:**
- Consumes: `CableRoutePlanner.Verify --crossing <data> <scenario> <result>`.
- Produces: original `result.json`/logs, all-alternative audits, GeoJSON, before/after SVG maps, and a comparative Markdown/JSON report.

- [ ] Add a failing test for report integrity and preservation of the exact two-point invocation.
- [ ] Run the test and confirm the missing behavior fails.
- [ ] Implement bounded execution and independent post-route audit using existing helpers.
- [ ] Build Verify in Release, run the study against the pinned database, and copy only small reproducible evidence into `docs/evidence`.
- [ ] Re-run the study into a temporary directory and compare deterministic evidence fields.
- [ ] Commit the executable study and fresh evidence.

### Task 3: Conditional defect handling

**Files:**
- Modify only the minimal routing production/test files identified by reproduced diagnostics, if any.

**Interfaces:**
- Consumes: baseline result, search bounds, cell size, portal/connector evidence, and edge rejection diagnostics.
- Produces: either a documented no-defect conclusion or a local fix with a named regression test.

- [ ] If baseline succeeds with valid alternatives, document why no production change is justified and skip the remaining steps.
- [ ] If baseline misses the verified witness, isolate search-area, effective-cell, portal, connector, and nearby-edge behavior using existing diagnostics and a smaller cell-size repeat.
- [ ] For an unambiguous local defect, prove a failing regression, implement the smallest fix, rerun it green, and regenerate evidence; otherwise leave production unchanged.

### Task 4: CI, preflight, and PR publication

**Files:**
- Modify: `.github/workflows/routing-crossing-pilot.yml`
- Create: `changes/51.feature.md`

**Interfaces:**
- Consumes: the focused study CLI and the existing country-data/Verify workflow setup.
- Produces: CI evidence, a hook-clean commit, pushed branch, and one unmerged PR #51.

- [ ] Add the focused study to the existing experimental workflow and upload its evidence.
- [ ] Run focused tests, applicable `tools/check.py` gates, pre-commit, and pre-push hooks.
- [ ] Review the final diff and evidence hashes with `pr-preflight` and `verification-before-completion`.
- [ ] Commit with Conventional Commits, push `research/indirect-road-crossings`, create PR #51 without merge, and monitor required CI to completion.
