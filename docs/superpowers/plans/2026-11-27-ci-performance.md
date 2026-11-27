# CI performance implementation plan

> **For agentic workers:** Use superpowers:executing-plans. Execute each verification before reporting success.

**Goal:** Investigate issue #61 on real country data and reduce the PR critical path without weakening checks.
**Architecture:** Prepare country data after successful desktop/data/terrain gates, concurrently with Minsk Android. Keep full APK consumers behind the original mandatory gates. Benchmark deterministic bounded gzip chunks before selecting defaults.
**Tech Stack:** GitHub Actions, Python 3.12, .NET SDK 8.0.427.
**Spec:** https://github.com/Volodya1000/maps/issues/61

## Global constraints

Keep exact country cache keys, SHA-256/provenance validation, offline runtime, signing compatibility and both APK emulator checks. Preserve routing-benchmark labels, manual installers/publishing and .NET 6 self-contained Windows 7. Do not modify conversion semantics or claim an unmeasured improvement.

## Review focus

Failed or cancelled mandatory jobs must block full APK consumers even if geodata succeeds. Fast labels must suppress country preparation. Worker failures must propagate. Chunk names, SQLite bytes and gzip output must remain deterministic. Invalid options must fail before creating output.

## Tasks

- [ ] Collect timestamped baseline jobs/steps for cache-miss and cache-hit runs; record limitations.
- [x] Extend `tests/tooling/test_android_chunks.py`: compression levels, bounded workers, exact reconstruction, deterministic bytes, invalid controls and corrupt gzip.
- [x] Run these tests red, implement configurable gzip in `tools/split_android_map.py`, run green.
- [ ] Add reproducible benchmark entry point and measure 1/3/6/9 and 1/2/4 workers, CPU, peak RSS and reconstruction; measure actual APK sizes/install/map-ready in opt-in Android CI on the same country database.
- [x] Extend workflow contract tests, run red, split geodata readiness from full APK consumer readiness. Run green and actionlint.
- [x] Compare combined dotnet format with three calls, including deliberately introduced whitespace/style/analyzer violations. Change only if equivalence is established.
- [ ] Produce `docs/ci-performance.md` with evidence, chosen defaults, trade-offs and unverified acceptance criteria; document fast iteration and cache policy.
- [ ] Add `changes/61.feature.md`; run repository checks and hooks, review diff, commit, push and create PR. Follow CI to a concrete result.
