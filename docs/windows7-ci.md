# Windows 7 CI experiment

The repository does not currently claim Windows 7 support. The modern Desktop target is `net8.0`, and `docs/windows7-decision.md` requires a separately built and verified legacy payload before any compatibility claim.

The manual workflow `.github/workflows/windows7-compatibility.yml` is deliberately fail-closed. It runs only from `workflow_dispatch` and requires all of the following GitHub Actions secrets:

- `WIN7_BASE_IMAGE_URL` and `WIN7_BASE_IMAGE_SHA256`: a legally obtained private Windows 7 SP1 x64 qcow2 image;
- `WIN7_PAYLOAD_IMAGE_URL` and `WIN7_PAYLOAD_IMAGE_SHA256`: a private read-only ISO containing the compatible Desktop build, test data, and an auto-starting guest agent.

The guest agent must execute the real GUI smoke checks (Avalonia initialization, Mapsui/offline SQLite, routing, input, offline mode, exception capture), then write `win7-smoke-result.json` and `win7-screenshot.png` to the attached FAT32 volume labelled `WIN7RESULTS`. The JSON must have schema `maps.win7.smoke.v1`, the current commit SHA, `guest_shutdown: true`, and true results for W7-01 through W7-09. The host reads that volume only after QEMU exits and validates the complete structure; a single serial marker is not sufficient. The workflow uses `-accel kvm`, `-nic none`, a writable overlay, and never falls back to TCG or Wine. Base, overlay, payload and results images remain under the runner temporary directory and are never uploaded as artifacts; only logs, JSON diagnostics and the screenshot are uploaded.

The workflow also contains an independent KVM feasibility job. It applies the same udev permission rule used by the repository's Android emulator workflow and starts a real QEMU process with `-accel kvm`; it does not boot Windows and does not claim application compatibility.

The `tools/Windows7Prototype` project is the independent managed-compatibility check. It targets `net48` and initializes Avalonia 11, Mapsui's Avalonia control and an in-memory SQLite connection without copying the production UI or routing code. Its successful build proves only that the managed dependency graph can compile against the .NET Framework reference assemblies; native DLL loading and Windows 7 GUI behavior still require the guest run.

The initial local feasibility experiment in this development environment produced:

```text
/dev/kvm: absent
qemu-system-x86_64: not installed
qemu-img: not installed
Windows 7 image: not provided
net48 Desktop payload: not present
```

Therefore no real Windows 7 boot or GUI result is claimed yet. Until a trusted runner exposes KVM and the private image/payload contract is provisioned, the workflow reports `INFRA_ERROR` and uploads `win7-environment.json` and `win7-smoke-result.json`; it does not report a compatibility PASS.
