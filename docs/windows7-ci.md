# Windows 7 CI experiment — source of truth

The repository does not claim Windows 7 support. This document is the source of truth for the experiment and uses four statuses: `VERIFIED` (a recorded run proves the statement), `BLOCKED` (an external input is missing), `FAILED` (a run disproved the statement), and `NOT_TESTED` (no run exists). The modern Desktop target remains `net8.0`.

The workflow `.github/workflows/windows7-compatibility.yml` has two safe feasibility jobs (`test-win7-feasibility`): a locked `net48` prototype build and an independent KVM/QEMU check. They run when the label is added and repeat on later PR commits while the label remains present. These jobs do not receive Windows 7 secrets. The full guest workflow remains manual (`workflow_dispatch`) and is deliberately fail-closed; it requires all of the following GitHub Actions secrets:

- `WIN7_BASE_IMAGE_URL` and `WIN7_BASE_IMAGE_SHA256`: a legally obtained private Windows 7 SP1 x64 qcow2 image.

The payload is now generated from the checked commit by the manual workflow: the Windows runner builds the net48 prototype and guest agent with locked restore for both `win-x64` and `win-x86`, emits manifests containing SHA-256 values, and the Ubuntu runner creates a private ISO for each architecture. There is no static `WIN7_PAYLOAD_IMAGE_URL` secret.

The guest agent runs the prototype smoke checks (Avalonia window, SQLite fixture, Mapsui layer, deterministic centered viewport, red marker observed in the captured pixels, native window and screenshot), then writes `win7-prototype-result.json` and `win7-prototype-screenshot.png` to the attached FAT32 volume labelled `WIN7RESULTS`. Its schema is `maps.win7.prototype.v1`; `result_flushed` and `shutdown_requested` are written before the shutdown request, while the host treats QEMU exit code 0 as the independent guest termination evidence. It is deliberately separate from the production `maps.win7.smoke.v1` W7-01…W7-09 contract. The host validates the prototype JSON with `tools/windows7_prototype_result.py` and validates PNG structure, decoded scanlines and minimum 640x480 dimensions with `tools/windows7_result.py`. A prototype PASS never means production routing or full application compatibility. The host reads the volume only after QEMU exits. The workflow uses `-accel kvm`, `-nic none`, a writable overlay, and never falls back to TCG or Wine. Base, overlay, payload and results images remain under runner temporary storage and are never uploaded as artifacts; only diagnostics are uploaded.

The workflow also contains an independent KVM feasibility job. It applies the same udev permission rule used by the repository's Android emulator workflow and starts a real QEMU process with `-accel kvm`; it does not boot Windows and does not claim application compatibility.

The manual guest input accepts a runtime of 5–15 minutes. The job limit is 30 minutes, leaving time for QEMU installation, private-image download, checksum verification, results extraction, and diagnostics. A timeout exits non-zero before result validation, so it cannot become a PASS.

The `tools/Windows7Prototype` project is the independent managed-compatibility check. It targets `net48`, creates a deterministic SQLite file, reads a fixture point, and adds that point to a real Avalonia 11 Mapsui `MapControl` without copying the production UI or routing code. Its successful build proves only that the managed dependency graph can compile against the .NET Framework reference assemblies; native DLL loading and Windows 7 GUI behavior still require the guest run.

The guest agent implementation and payload builder are now in this PR, but a real guest run is `BLOCKED` until the owner provisions a licensed Windows 7 SP1 x64 image and configures the private base-image secrets. The base image must install .NET Framework 4.8, provide an interactive desktop session, and auto-start `run-agent.cmd` from the payload ISO. This PR does not claim Windows 7 support.

The initial local feasibility experiment in this development environment produced:

```text
/dev/kvm: absent
qemu-system-x86_64: not installed
qemu-img: not installed
Windows 7 image: not provided
net48 Desktop payload: not present
```

## Base-image preparation (owner-supplied material)

The repository does not download Windows media. The owner must use a licensed Windows 7 SP1 x64 ISO and an offline .NET Framework 4.8 installer, apply the required servicing/SHA-2 updates, enable an interactive desktop with a compatible virtual display adapter, and install a startup shortcut that searches the `WIN7RESULTS.marker` volume for the payload's `run-agent.cmd`. After first-boot setup, shut down the guest, remove temporary installation media, convert the clean disk to qcow2, calculate SHA-256, and store it privately (for example in a private object store). Configure only the URL and SHA-256 as repository secrets. The ISO, activation keys, qcow2 and writable overlays must never enter Git or normal Actions artifacts.

Therefore no real Windows 7 boot or GUI result is claimed yet. Until the base image is provisioned, the manual workflow reports `INFRA_ERROR` and preserves `win7-prototype-result.json` plus host/QEMU diagnostics; it does not report a compatibility PASS.

## Evidence matrix

| Claim | Status | Evidence |
|---|---|---|
| GitHub-hosted Ubuntu exposes `/dev/kvm` and QEMU `-accel kvm` | VERIFIED | Commit `add301f`; [feasibility run](https://github.com/Volodya1000/maps/actions/runs/40513798831) |
| Avalonia 11 + Mapsui + SQLite compile for `net48` with locked restore | VERIFIED | Commit `add301f`; [prototype job](https://github.com/Volodya1000/maps/actions/runs/40513798831/job/122222300242) |
| JSON/PNG result validation | VERIFIED | Feasibility job and validator unit tests in the same run |
| Guest agent and payload tree build on Windows | VERIFIED | [payload build job](https://github.com/Volodya1000/maps/actions/runs/40539236121/job/122312767317) |
| Ordinary Ubuntu/Windows/data/terrain/Android CI | VERIFIED | [ordinary CI run](https://github.com/Volodya1000/maps/actions/runs/40513798856) |
| Windows 7 boots in QEMU | BLOCKED | Licensed qcow2 and .NET 4.8-ready base image are not provided |
| Avalonia GUI, SkiaSharp/SQLite/HarfBuzz native loading and Mapsui renderer on Windows 7 | NOT_TESTED | Requires the base image and guest run |
| Main net8 Desktop application on Windows 7 | NOT_TESTED | Deliberately out of scope until prototype compatibility is proven |
