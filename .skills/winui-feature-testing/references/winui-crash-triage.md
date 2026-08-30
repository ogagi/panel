# WinUI Crash Triage

Use this when a UI action crashes inside `Microsoft.UI.Xaml.dll`, especially when the feature compiles and the crash appears only when a view is first opened.

## Evidence sequence

1. Reproduce once and record the launched executable, PID, process start time, interaction, and crash time.
2. Query recent Application log entries from `Application Error`, `.NET Runtime`, and `Windows Error Reporting`. Convert hexadecimal Event Viewer PIDs before correlating them with recorded decimal PIDs.
3. Treat `0xc000027b` as a wrapper until the originating exception or HRESULT is known. WER problem signatures may expose a more useful HRESULT in another field.
4. Put a narrow `try/catch` around the closest managed boundary, such as construction of a deferred XAML view or `ShowAsync`. Log `exception.ToString()` to a known temporary path. Keep logging itself non-throwing.
5. If managed capture is insufficient, enable a local dump only for the exact executable, reproduce once, then remove the diagnostic configuration. Do not leave machine-wide dump settings behind.
6. Automate the action so each candidate fix is tested identically. Assert the target process remains alive and the expected UI appears.

## High-value hypotheses

- `InitializeComponent` loads a secondary page/window only on first use, so build-time XAML compilation is insufficient evidence.
- XAML string conversion can fail at runtime for property values. Fractional numeric literals are particularly worth checking under non-English locales. Assign fragile values as typed C# values when runtime parsing is the failure.
- An exception escaping an `async void` event handler may surface as a WinUI fail-fast with little managed context.
- The reported faulting module is often where WinUI terminates, not where the invalid value or lifecycle error originated.
- A first click that appears to do nothing followed by a crash can mean construction failed before activation; do not assume focus, z-order, or button responsiveness without evidence.

## Minimal experiment pattern

Use stage markers or a narrow exception boundary to distinguish:

1. handler entered;
2. view constructor entered;
3. `InitializeComponent` completed;
4. model values applied;
5. window/dialog activated;
6. close/teardown completed.

Reduce only the failing stage. For a XAML parse error, remove or programmatically assign suspect attributes one group at a time. For lifecycle errors, delay native-window access until the handle is valid and prove that the handle is nonzero before calling interop.

## Verification matrix

At minimum, verify:

- first invocation after process launch;
- close and second invocation;
- every layout/mode that uses a separate event path;
- process alive after open and close;
- expected window or control visible;
- no new matching Application Error event;
- published/restarted binary, not only `bin` output.

The incident that motivated this guidance produced `XamlParseException` (`0x802B000A`) at `SettingsWindow.InitializeComponent`: WinUI could not assign fractional `Slider.Minimum` values written as XAML strings on the active locale. Moving those settings to typed `double` assignments fixed both the apparent first-click failure and the later `Microsoft.UI.Xaml.dll` fail-fast.
