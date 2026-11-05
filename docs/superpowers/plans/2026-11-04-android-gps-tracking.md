# Android GPS Tracking Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add opt-in offline Android location, heading, camera-follow, and route-traversal progress without changing route calculation or Desktop behavior.

**Architecture:** Application owns platform-neutral location contracts, route fingerprinting, progress intervals, and the pure `RouteProgressTracker`. Android implements `LocationManager`/`SensorManager` adapters and lifecycle/permission handling. ViewModels coordinate settings and tracking; UI/Mapsui owns rendering layers and camera interaction. Desktop receives no-op platform services and hides Android-only controls.

**Tech Stack:** .NET 8, C# 12, Avalonia 11.3, ReactiveUI, Mapsui 4.1.9, Android API 23+, xUnit, Avalonia.Headless.

**Spec:** `docs/superpowers/specs/2026-11-04-android-gps-tracking-design.md`

## Global Constraints

- Do not modify A*, routing admissibility, obstacle handling, geodata, or existing route geometry.
- Do not add `INTERNET`, Google/Fused Location SDK, background location permission, or a foreground service.
- Keep `Domain` BCL-only; Android references stay in the Android composition root.
- Keep GPS state outside `RoutingOptions` and outside `PlanningSession` points.
- GPS is opt-in by default; permission is requested only after the user enables the setting.
- The first location fix must never move the camera; only the explicit location button enables follow.
- Progress is the union of credible consecutive route intervals, never a sum of projections.
- Persist settings/session atomically and reject restored progress when the route fingerprint/geometry differs.
- Do not claim Android emulator or physical-device verification unless it actually runs.

## Review Focus

- Approximate permission and revoked permission must keep the marker safe and disclose reduced quality; covered by `LocationSettingsViewModelTests.PermissionStatesAreObservable`.
- A GPS jump over an unmeasured middle section must not fill the interval; covered by `RouteProgressTrackerTests.DoesNotCountDisconnectedRouteGap`.
- A loop/self-intersection/parallel segment must select a continuous route position; covered by `RouteProgressTrackerTests.HandlesAmbiguousGeometryWithoutTeleporting`.
- A first fix and a follow update must differ in camera behavior; covered by `MapLocationTests.FirstFixDoesNotMoveCamera` and `MapLocationTests.LocationButtonEnablesFollow`.
- A changed route must invalidate persisted progress rather than silently reuse it; covered by `TrackingViewModelTests.RestoredSessionForDifferentGeometryIsRejected`.

## File Map

- Create `src/CableRoutePlanner.Application/LocationContracts.cs`: portable location, heading, feature, preference, and tracking-store contracts.
- Create `src/CableRoutePlanner.Application/RouteProgressTracker.cs`: thresholds, projection, interval union, progress state, and covered geometry.
- Create `src/CableRoutePlanner.Persistence/JsonLocationPreferencesStore.cs` and `JsonTrackingSessionStore.cs`: separate atomic JSON stores.
- Modify `src/CableRoutePlanner.Persistence/PersistenceServiceCollectionExtensions.cs`: register the two stores without changing routing settings serialization.
- Create `src/CableRoutePlanner.ViewModels/Tracking/TrackingViewModel.cs`: location state, follow state, and traversal commands.
- Modify `src/CableRoutePlanner.ViewModels/Settings/SettingsViewModel.cs`: opt-in location setting and platform status.
- Modify `src/CableRoutePlanner.ViewModels/Map/MapViewModel.cs`: expose tracking state and invalidate tracking on route/waypoint changes.
- Create `src/CableRoutePlanner.Android/AndroidLocationService.cs` and `AndroidHeadingService.cs`: Android-only providers and lifecycle-safe subscriptions.
- Modify `src/CableRoutePlanner.Android/MainActivity.cs` and `Properties/AndroidManifest.xml`: register services, lifecycle forwarding, and coarse/fine permissions.
- Modify `src/CableRoutePlanner.UI/PresentationServiceCollectionExtensions.cs`: register no-op Desktop services and tracking VM.
- Modify `src/CableRoutePlanner.UI/MapAdapters/IMapAdapter.cs`, `MapsuiMapAdapter.cs`, `MapLayerBindings.cs`: location/progress layers, camera follow, and manual-camera callbacks.
- Modify `src/CableRoutePlanner.UI/Views/MapView.axaml(.cs)`, `SettingsView.axaml(.cs)`, and `Styles/Map.axaml`: Android-only controls, settings sections, compact tracking panel, and accessible labels.
- Create/modify tests under `tests/CableRoutePlanner.Tests/Application`, `Persistence`, `ViewModels`, and `UI` for every named behavior.
- Create `changes/46.feature.md`: non-empty PR fragment; do not edit `CHANGELOG.md`.

### Task 1: Lock down portable location and progress behavior with failing tests

**Files:**
- Create: `tests/CableRoutePlanner.Tests/Application/RouteProgressTrackerTests.cs`
- Create: `tests/CableRoutePlanner.Tests/Application/LocationContractsTests.cs`
- Modify: `tests/CableRoutePlanner.Tests/CableRoutePlanner.Tests.csproj` only if new files are not picked up automatically.

**Interfaces:**
- Produces the exact observable contracts for `LocationMeasurement`, `RouteProgressTracker`, `RouteProgressState`, `ProgressInterval`, and `HeadingSmoother` that later tasks implement.

- [ ] **Step 1: Write failing tracker tests** for a two-point route, multi-segment route, full/partial/start-middle traversal, the 200 m + last 300 m key case, reverse and repeated traversal, zero/duplicate segments, pause/resume, stale/out-of-order/low-accuracy measurements, stationary jitter, speed outlier, signal loss, and disconnected GPS gap.
- [ ] **Step 2: Add failing geometry tests** for a closed loop, self-intersection, parallel neighboring legs, and covered-geometry extraction with no duplicate coordinates.
- [ ] **Step 3: Add failing heading tests** for 0/90/180/270 degrees, 359→1 wraparound, map rotation, display rotation, unavailable/unreliable sensor, and smoothing.
- [ ] **Step 4: Run `dotnet test tests/CableRoutePlanner.Tests/CableRoutePlanner.Tests.csproj --filter FullyQualifiedName~RouteProgressTrackerTests` and record the expected missing-type failures.

### Task 2: Implement the pure Application progress engine

**Files:**
- Create: `src/CableRoutePlanner.Application/LocationContracts.cs`
- Create: `src/CableRoutePlanner.Application/RouteProgressTracker.cs`
- Modify: `src/CableRoutePlanner.Application/CableRoutePlanner.Application.csproj` only if required by compilation.

**Interfaces:**
- `LocationMeasurement(GeoCoordinate Coordinate, double AccuracyMetres, DateTimeOffset TimestampUtc, double? SpeedMetresPerSecond, bool IsApproximate)`.
- `RouteProgressConfiguration(MaxAccuracyMetres, MaxMeasurementAge, MaxConsecutiveGap, MaxSpeedMetresPerSecond, ProjectionToleranceMetres, StationaryDistanceMetres)`.
- `RouteProgressTracker.Begin(RouteAlternative route)`, `Restore(RouteProgressSnapshot snapshot, RouteAlternative route)`, `Pause()`, `Resume()`, `Finish()`, and `Accept(LocationMeasurement measurement, DateTimeOffset nowUtc)` returning `RouteProgressState`.
- `RouteProgressState` exposes route length, covered/remaining metres, coverage percentage, merged `ProgressInterval` values, covered geometry, signal quality, and tracking status.
- `HeadingSmoother.Update(double headingDegrees, bool reliable, double? displayRotationDegrees)` returns nullable smoothed heading and normalizes angle deltas across 0/360.

- [ ] **Step 1: Implement route cumulative lengths and projection candidates** using local equirectangular metre coordinates derived from `GeoCoordinate`; ignore zero-length segments while preserving route geometry indices.
- [ ] **Step 2: Implement measurement validation** for finite accuracy, maximum age, monotonic timestamps, explicit pause/resume continuity reset, stationary jitter, and optional speed/maximum-gap rejection.
- [ ] **Step 3: Implement continuity-aware candidate selection** using previous route position, neighboring segment distance, route-distance delta, and direction; never bridge a rejected/disconnected measurement.
- [ ] **Step 4: Implement normalized interval union**, remaining intervals, percentage clamped to 0–100, and covered geometry interpolation along the original route polyline.
- [ ] **Step 5: Implement route fingerprint/snapshot restore** with exact geometry comparison/fingerprint and rejection of mismatched routes.
- [ ] **Step 6: Implement `HeadingSmoother` and run the Task 1 tests until green.**
- [ ] **Step 7: Run the focused Application test command and verify no routing test changed.**

### Task 3: Add isolated persistence and tracking ViewModel tests

**Files:**
- Create: `tests/CableRoutePlanner.Tests/Persistence/LocationPersistenceTests.cs`
- Create: `tests/CableRoutePlanner.Tests/ViewModels/TrackingViewModelTests.cs`
- Create: `tests/CableRoutePlanner.Tests/ViewModels/LocationSettingsViewModelTests.cs`

**Interfaces:**
- Defines fake `ILocationService`, `IHeadingService`, `ILocationPreferencesStore`, and `ITrackingSessionStore` behavior needed by ViewModels.

- [ ] **Step 1: Write failing persistence tests** for default-disabled preferences, round-trip save/reload, atomic replacement, malformed data, and tracking snapshot round-trip.
- [ ] **Step 2: Write failing settings tests** for enabling after user action, no startup permission request, saving without changing `RoutingOptions`, denial/permanent denial/approximate/provider-disabled states, and disabling stopping updates.
- [ ] **Step 3: Write failing tracking tests** for begin/pause/resume/finish, progress display, save/reload requiring explicit resume, changed route rejection, and route/waypoint change invalidation.
- [ ] **Step 4: Run the focused tests and confirm failures are due to missing stores/ViewModel contracts.**

### Task 4: Implement persistence and ViewModels

**Files:**
- Create: `src/CableRoutePlanner.Persistence/JsonLocationPreferencesStore.cs`
- Create: `src/CableRoutePlanner.Persistence/JsonTrackingSessionStore.cs`
- Modify: `src/CableRoutePlanner.Persistence/PersistenceServiceCollectionExtensions.cs`
- Create: `src/CableRoutePlanner.ViewModels/Tracking/TrackingViewModel.cs`
- Modify: `src/CableRoutePlanner.ViewModels/Settings/SettingsViewModel.cs`
- Modify: `src/CableRoutePlanner.ViewModels/Map/MapViewModel.cs`
- Modify: `src/CableRoutePlanner.UI/PresentationServiceCollectionExtensions.cs`

**Interfaces:**
- `ILocationService` reports capability, permission/provider state, and `IObservable<LocationMeasurement>`; it supports `EnableAsync`, `DisableAsync`, `Resume`, `Pause`, and `OpenSystemLocationSettings`.
- `IHeadingService` reports heading measurements and supports the same lifecycle without making GPS depend on a compass.
- `TrackingViewModel` exposes `Start`, `Pause`, `Resume`, `Finish`, `IsFollowing`, `ToggleFollow`, current `LocationDisplayState`, `RouteProgressState`, and `SetRoute(RouteAlternative?)`.

- [ ] **Step 1: Implement atomic stores** in separate paths (`location-settings.json`, `tracking-session.json`) with temporary-file write, flush/replace, validation, and explicit null/route-mismatch results.
- [ ] **Step 2: Implement fake-compatible `TrackingViewModel` state machine**; subscribe/dispose location and heading streams, feed only active sessions to the tracker, save on pause/dispose, and require explicit resume after restore.
- [ ] **Step 3: Extend `SettingsViewModel`** with `IsLocationSupported`, `ShowLocation`, permission/provider status, and enable/disable command behavior while preserving all existing routing validation and unsaved-change behavior.
- [ ] **Step 4: Connect `MapViewModel` route selection and point changes** to `TrackingViewModel.SetRoute`, clearing/inactivating progress on geometry changes without deleting a persisted session silently.
- [ ] **Step 5: Register stores/tracking in UI and no-op location/heading implementations for Desktop; run Task 3 tests and existing settings tests.**

### Task 5: Add Android providers, permissions, sensors, and lifecycle

**Files:**
- Create: `src/CableRoutePlanner.Android/AndroidLocationService.cs`
- Create: `src/CableRoutePlanner.Android/AndroidHeadingService.cs`
- Modify: `src/CableRoutePlanner.Android/MainActivity.cs`
- Modify: `src/CableRoutePlanner.Android/Properties/AndroidManifest.xml`
- Modify: `src/CableRoutePlanner.Android/CableRoutePlanner.Android.csproj` only if direct Application reference is required.

**Interfaces:**
- Android services implement the Application contracts and expose no Android type outside the Android project.

- [ ] **Step 1: Add manifest permissions** `ACCESS_FINE_LOCATION` and `ACCESS_COARSE_LOCATION`; retain the explicit removal of `INTERNET` and add manifest tests for the exact permission set.
- [ ] **Step 2: Implement permission/provider state** using `CheckSelfPermission`, `RequestPermissions` only from `EnableAsync`, provider checks, no-repeat behavior after permanent denial, and system-settings intent.
- [ ] **Step 3: Implement `LocationManager` listener** with GPS-first offline updates, coarse/approximate reporting, stale/failure state, and disposal on pause/disable.
- [ ] **Step 4: Implement `SensorManager` heading** using rotation-vector data, display rotation remapping, reliability checks, `HeadingSmoother`, and independent GPS operation when absent.
- [ ] **Step 5: Forward Activity `OnResume`/`OnPause` and register services in `AfterSetup`; run Android compile/manifest checks if SDK/workload exists.**

### Task 6: Add Mapsui location, progress, and camera-follow rendering

**Files:**
- Modify: `src/CableRoutePlanner.UI/MapAdapters/IMapAdapter.cs`
- Modify: `src/CableRoutePlanner.UI/MapAdapters/MapsuiMapAdapter.cs`
- Modify: `src/CableRoutePlanner.UI/MapAdapters/MapLayerBindings.cs`
- Modify: `src/CableRoutePlanner.UI/MapAdapters/MapsuiPointerInteraction.cs` only for explicit manual-camera callbacks.
- Create: `tests/CableRoutePlanner.Tests/UI/MapLocationTests.cs`

**Interfaces:**
- Add `SetLocation(LocationDisplayState?)`, `SetProgress(RouteProgressState?)`, `CenterOnLocation()`, `IsFollowing`, and a manual-camera event to `IMapAdapter`.

- [ ] **Step 1: Write failing headless tests** for layer order, blue GPS point/white outline, accuracy circle, optional heading sector, route-progress colors, and GPS marker above route/vertices.
- [ ] **Step 2: Write failing camera tests** for first-fix no-pan, explicit center/follow, follow updates, manual pan/rotate/zoom disabling follow, repeat center, and unchanged user-point collection.
- [ ] **Step 3: Implement separate MemoryLayers** for progress, GPS accuracy/heading, and GPS point; update their existing features on each state publication and refresh on the UI thread.
- [ ] **Step 4: Implement geographic accuracy circle and heading sector** in projected coordinates without changing route geometry; hide sector for missing/unreliable heading.
- [ ] **Step 5: Implement follow camera and pointer/manual gesture cancellation** while preserving compass reset and existing add/drag/multitouch behavior.
- [ ] **Step 6: Bind TrackingViewModel state to the adapter and run focused headless tests plus existing map-camera/gesture tests.**

### Task 7: Add settings/tracking UI and accessibility behavior

**Files:**
- Modify: `src/CableRoutePlanner.UI/Views/SettingsView.axaml`, `SettingsView.axaml.cs`
- Modify: `src/CableRoutePlanner.UI/Views/MapView.axaml`, `MapView.axaml.cs`
- Modify: `src/CableRoutePlanner.UI/Styles/Map.axaml` and any required common style resource.
- Modify: `tests/CableRoutePlanner.Tests/UI/UiTests.cs` or create focused UI tests.

**Interfaces:**
- Android-only controls are visible from `IsLocationSupported`; Desktop renders no location settings/button/tracking panel.

- [ ] **Step 1: Write failing headless UI tests** for settings title/sections, Android-vs-Desktop visibility, accessible labels, compact collapsed progress row, and traversal command/state text.
- [ ] **Step 2: Rename settings heading to `Настройки`; add Android-only `Местоположение` section and preserve routing controls/validation.
- [ ] **Step 3: Add location floating button** with tooltip/automation name and connect it to `CenterOnLocation`; keep it aligned with compass and responsive bottom sheet layout.
- [ ] **Step 4: Add compact traversal panel** with start/pause/resume/finish and total/covered/remaining/percentage/status values, without overlaying a competing panel.
- [ ] **Step 5: Add permission/provider/offline-coverage status text and system-settings command; run UI tests and compiled-binding validation.**

### Task 8: Integration, regression, documentation, and verification

**Files:**
- Modify: `docs/architecture.md` and `docs/testing.md` only for the new layer/contracts and commands.
- Create: `changes/46.feature.md`
- Modify: any integration tests needed for DI and manifest composition.

- [ ] **Step 1: Add composition tests** proving Desktop resolves no-op services, Android registers real providers, routing settings remain unchanged, and GPS never becomes a planning point.
- [ ] **Step 2: Add integration coverage** for restored progress, route alternative switching, clearing/recalculation, offline/out-of-coverage status, and persistence after pause/dispose.
- [ ] **Step 3: Add the non-empty feature fragment** and update architecture/testing docs without editing `CHANGELOG.md`.
- [ ] **Step 4: Run `git diff --check`, focused tests, and applicable repository checks; run `uv run --locked python tools/check.py lint`, `desktop`, and `android` only when prerequisites are present.
- [ ] **Step 5: Run `pr-preflight` and `verification-before-completion`; report exact PASS/FAIL/NOT VERIFIED evidence, including Android emulator/device status.**
- [ ] **Step 6: Commit in small Conventional Commit units, then prepare one PR summary covering AC-01 through AC-24 and any unavailable checks.**

## Self-review coverage

The plan covers all acceptance groups: Android-only registration and UI (Tasks 4–7), opt-in preference and permission lifecycle (Tasks 3–5), marker/accuracy/heading/follow/camera behavior (Tasks 6–7), traversal lifecycle/persistence/route identity (Tasks 2–4 and 8), unique interval coverage and GPS-error rejection (Tasks 1–2), and preservation of routing/Desktop behavior (Tasks 4 and 8). The five highest-risk silent failures are pinned in Review Focus and assigned named tests. No task changes routing implementation or geodata.
