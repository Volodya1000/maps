# Windows 7 CI experiment

The repository does not currently claim Windows 7 support. The modern Desktop target is `net8.0`, and `docs/windows7-decision.md` requires a separately built and verified legacy payload before any compatibility claim.

The manual workflow `.github/workflows/windows7-compatibility.yml` is deliberately fail-closed. It runs only from `workflow_dispatch` and requires all of the following GitHub Actions secrets:

- `WIN7_BASE_IMAGE_URL` and `WIN7_BASE_IMAGE_SHA256`: a legally obtained private Windows 7 SP1 x64 qcow2 image;
- `WIN7_PAYLOAD_IMAGE_URL` and `WIN7_PAYLOAD_IMAGE_SHA256`: a private read-only ISO containing the compatible Desktop build, test data, and an auto-starting guest agent.

The guest agent must execute the real GUI smoke checks (Avalonia initialization, Mapsui/offline SQLite, routing, input, offline mode, exception capture), write diagnostics and a screenshot to its private test volume, and emit `WIN7_SMOKE_PASS` on COM1 only after all checks pass. The workflow uses `-accel kvm`, `-nic none`, a writable overlay, and never falls back to TCG or Wine. The base image is not stored in Git or ordinary workflow artifacts.

The initial feasibility experiment in this development environment produced:

```text
/dev/kvm: absent
qemu-system-x86_64: not installed
qemu-img: not installed
Windows 7 image: not provided
net48 Desktop payload: not present
```

Therefore no real Windows 7 boot or GUI result is claimed yet. Until a trusted runner exposes KVM and the private image/payload contract is provisioned, the workflow reports `INFRA_ERROR` and uploads `win7-environment.json` and `win7-smoke-result.json`; it does not report a compatibility PASS.
