# Offline Belarus demo implementation plan

> **For agentic workers:** Execute inline with superpowers:executing-plans.

**Goal:** One manual run of the selected commit produces two portable Windows ZIPs and one signed Android APK with the same full Belarus map, DEM and notices.

**Architecture:** Extend `offline-packages.yml` with default `demo` mode. Preserve explicit `installers` and `geodata` modes. A reusable, manual-caller-only workflow consumes the existing single country-data/country-elevation producer artifact. Pin every checkout to the run SHA. No changes to application versions, release workflows, PR labels or ordinary CI triggers.

**Tech stack:** GitHub Actions, Python ZIP64/streaming verification, PowerShell, existing .NET publish and Android emulator smoke.

**Spec:** User request in this chat (2026-11-27).

## Constraints and review focus

- Final names: `Maps-Belarus-Windows7-x86-<sha>.zip`, `Maps-Belarus-Windows10-x64-<sha>.zip`, `Maps-Belarus-Android-<sha>.apk` (full SHA).
- Upload the actual files with `archive: false`; Windows archives contain the application at their root.
- One verified reference package; verify final archives against it, including database bytes, DEM and licenses, then run the existing six-city repository inspection against extracted Data.
- Windows startup must use extracted EXE and its default Data path without CABLEROUTE_DATA; Android must install the exact downloadable APK and wait for full-country map readiness twice.
- Hosted Windows 2022 checks cannot establish Win7 or Win10 target OS acceptance. Record actual available target checks honestly.
- Keep PR #69's country preparation/performance work independent; do not edit its CI jobs, split implementation or APK checker.

## Tasks

### 1. Final archive verification

Files: `tools/verify_offline_demo.py`, `tests/tooling/test_offline_demo.py`.

- [ ] Write and run focused failing archive tests: success for ZIP/APK, Minsk rejection, altered SQLite, lost license/DEM, invalid chunks/runtime and unsafe entries.
- [ ] Implement streaming verification/extraction against the producer package and file metadata for the summary.
- [ ] Run focused tests and lint.

### 2. Manual orchestration and platform checks

Files: `.github/workflows/offline-packages.yml`, `.github/workflows/demo-packages.yml`, `tools/Windows7Compatibility/build-win7-x86.ps1`, `tools/smoke_offline_windows.ps1`.

- [ ] Add demo mode and explicit SHA checkouts; preserve installer/publication modes.
- [ ] Reuse the Win7 builder with an explicit full-data argument and self-contained x64 publish.
- [ ] Verify and inspect extracted final archives; start their EXE with bundled Data and wait for the existing Belarus map-ready marker.
- [ ] Reuse Android signing, split assets, DEM check and two-launch API 35 full map readiness gate.
- [ ] Publish three direct artifacts only after their checks; aggregate exact size, SHA-256 and artifact URL with full source SHA.

### 3. Documentation, validation and PR

Files: `README.md`, `changes/<PR>.feature.md`.

- [ ] Explain the default manual main run, three files and offline startup.
- [ ] Run applicable repository gates and available real packaging/startup checks.
- [ ] Review the branch, commit with hooks, push and open a PR to main without labels or merge.
- [ ] Record target platform gaps and an actual manual run URL when available.
