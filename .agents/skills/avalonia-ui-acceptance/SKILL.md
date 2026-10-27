---
name: avalonia-ui-acceptance
description: Validate an Avalonia UI change involving AXAML, compiled bindings, ReactiveUI activation, templates, styles, map interaction, navigation, or Android gestures. Do not use for non-UI C# changes.
---

# Avalonia UI acceptance

First query the configured official `avalonia-docs` MCP for the specific
Avalonia API or behavior when the server is available; record the query/result.
Then read `docs/ui-styling.md` and `docs/architecture.md`. Preserve compiled
bindings and `x:DataType`, keep binding/commands/events/layout structure in
AXAML and common visuals in the documented resource/style files, and keep
ReactiveUI subscriptions scoped to activation.

Map the changed interaction to an existing headless test under
`tests/CableRoutePlanner.Tests/UI` or `ViewModels`; add a behavior-level test
only if no existing case proves it. Include navigation/resource-template
resolution, map selection or camera interaction, and touch/Android gesture
handling only when the changed path reaches them.

Run the documented local gates:

```sh
uv run --locked python tools/check.py lint
uv run --locked python tools/check.py desktop
```

Run `uv run --locked python tools/check.py android` only for Android-relevant
changes and only when its SDK/JDK/workload prerequisites exist. Report Android
device/emulator interaction as `NOT VERIFIED` unless it actually ran; headless
tests do not prove physical-device gestures.

Done when bindings compile, selected behavior tests pass, styling ownership is
preserved, the MCP outcome is recorded (or unavailable), and platform claims
match executed evidence.
