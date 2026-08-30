---
name: winui-feature-testing
description: Operate and verify WinUI desktop application features through Windows UI Automation, including first-use and repeated-use flows, secondary windows, compact layouts, process-survival checks, and evidence-driven diagnosis of Microsoft.UI.Xaml crashes. Use for end-to-end WinUI feature testing or when a UI action builds successfully but fails at runtime.
---

# WinUI Feature Testing

Verify the behavior the user actually performs against the same executable they run. A successful build or unit-test pass does not exercise deferred XAML loading, window activation, layout transitions, or control event wiring.

## Workflow

1. Read repository instructions and identify the build, test, publish, and restart paths.
2. Preserve unrelated work. Record the executable path, process ID, start time, mode, and exact interaction being tested.
3. Build and run existing tests before feature automation, but treat them as prerequisites rather than UI verification.
4. Operate the application through UI Automation. Prefer stable `AutomationId` values over glyph text, coordinates, or control order.
5. Assert observable outcomes: the process remains alive, the expected window/control appears, state changes as intended, and close/reopen or repeated invocation works.
6. Exercise materially different paths such as compact/full modes or first/subsequent invocation when the implementation branches there.
7. Correlate any crash with its exact PID and timestamp. Do not diagnose from an older Event Viewer entry merely because the module and offset match.
8. After a fix, rebuild the deployed artifact, repeat the failing interaction, run regression tests, and follow repository restart instructions.

Use [scripts/Test-WinUIFeature.ps1](scripts/Test-WinUIFeature.ps1) for repeatable control invocation and window/process assertions. Run it with `-ListControls` first when the target lacks a known `AutomationId`.

Typical use against an existing process:

```powershell
& scripts/Test-WinUIFeature.ps1 -ProcessId 9204 -WindowTitle 'AI Core Monitor' `
  -AutomationId 'SettingsButton' -ExpectedWindowTitle 'AI Core Monitor Settings' `
  -CloseExpectedWindow -InvokeCount 2
```

To launch a disposable test instance, use `-ExecutablePath` and add `-StopLaunchedProcess`. Omit that switch when the tested instance should remain running.

For `Microsoft.UI.Xaml.dll`, `0xc000027b`, XAML parse failures, native access violations, or failures that occur only on first interaction, read [references/winui-crash-triage.md](references/winui-crash-triage.md).

## Test Design

Add feature automation when the interaction is stable enough to test and a regression would otherwise escape unit tests. Keep automation outside production code unless the repository already has a UI-test project. Give controls explicit `AutomationId`/`x:Name` where useful, and assert behavior rather than visual timing.

Avoid fixed sleeps as the primary synchronization mechanism. Poll for process/window/control state with a bounded timeout. Do not stop an existing user process unless the task authorizes it; the helper only stops a process when `-StopLaunchedProcess` is explicitly supplied.

When a one-off diagnostic becomes a durable test, adapt the helper into the repository's test framework and ensure CI/environment requirements are explicit: interactive desktop session, matching architecture, and a deployed WinUI runtime or self-contained build.
