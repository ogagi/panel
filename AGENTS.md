# Repository Working Instructions

## Restart After Implementation

After completing and verifying any code or UI implementation change during a session, restart AI Core Monitor so the running widget uses the latest build.

1. Build and run the relevant tests first.
2. If verification succeeds, close only the currently running `AiCoreMonitor` process or window.
3. Run `RunWidget.cmd` from the repository root and confirm that the new process starts.
4. Report restart failures explicitly; do not claim the implementation is fully verified when the updated application did not start.

Do not restart the application for read-only inspection, explanation, planning, or documentation-only changes unless explicitly requested.
