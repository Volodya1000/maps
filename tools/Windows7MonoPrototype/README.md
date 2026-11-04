# Mono `mkbundle` feasibility prototype

This directory is an isolated experiment. It does not replace the working
Windows 7 `net48` payload and does not claim production application support.

The project reuses the deterministic Avalonia/Mapsui/SQLite smoke program from
`tools/Windows7Prototype`, targets `net48`, and requests x86 output. The build
script then asks Mono `mkbundle` to embed the managed assemblies and the Mono
runtime into `Windows7MonoPrototype.exe`.

## Build

Install the official Mono for Windows 6.12 x86 toolchain, including `mkbundle`,
and make its `bin` directory available in `PATH`. Then run from the repository
root:

```powershell
pwsh tools/Windows7MonoPrototype/build-mkbundle.ps1
```

The result is written to `artifacts/windows7-mono-x86`. Native SkiaSharp,
HarfBuzz and SQLite libraries remain beside the executable because `mkbundle`
does not automatically make every native P/Invoke library part of the bundle.

The same build can be started manually from the `Windows 7 Mono mkbundle
feasibility` GitHub Actions workflow. That workflow downloads the official Mono
6.12 x86 installer, verifies its SHA-256, extracts it into runner temporary
storage, and uploads the candidate artifact. It does not install Mono on the
tested Windows 7 machine.

## Acceptance criteria

The experiment is successful only when the generated x86 bundle starts on a
clean Windows 7 SP1 x86 VM without .NET Framework or Mono installed, opens the
Avalonia window, reads the SQLite fixture, and renders the red Mapsui marker.

Compilation on Windows Server is not evidence of Windows 7 compatibility. A
failed bundle or a native-library load error must remain a failed feasibility
result, not be hidden by changing the smoke-test contract.
