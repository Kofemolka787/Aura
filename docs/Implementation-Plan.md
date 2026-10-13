# AURA v0.12 — implementation status

The user authorized the previously discussed improvements and subsequent corrections. Implementation is complete for the development scope; the final release is verified locally. Repository/environment persistence is the final step. Native QA and commercial prerequisites are separate, explicitly unperformed items.

## v0.12 development increment

- Implemented game cards with optional per-game crosshair/OSD/monitor/color and configurable focus-loss policy; common settings remain separate from temporary overrides.
- Implemented write-ahead, boot/device-bound DPAPI game-color recovery, preserving actual gamma and raw NVIDIA DVC levels before game changes and the former active monitor before a target switch. Unrestored records are retained and block replacement.
- Implemented configurable global hotkeys and hook-free WPF OSD. PresentMon live capture consumes complete CSV rows with bounded histories, distinct process/swapchain streams, no generated-frame FPS, freshness expiry independent of a delayed sensor read, invalidation of dynamic clock/power/used-memory caches, cancellation, owned-session cleanup and temporary-file removal. Benchmark capture supplies OSD data without another AURA capture running concurrently.
- Implemented series metadata, completeness/condition/duration validation, averages and min/max ranges across all runs, and hardware sample recording while minimized. The user restarts the scene before each run.
- Added legacy settings backup, meaningful tuner labels, documentation and portable regressions.
- Native Windows QA remains unperformed. DDC/CI, ICC/HDR, cooling control, store/library integrations and a scaling renderer are future work; automatic game resolution changes are not implemented.

## Inherited v0.11 implementation

- Hardware: exact absolute Intel limits and exact NVIDIA offset in kHz, boot/device-bound DPAPI recovery before writes, verified writes, one write gate, fail-closed temperature control, invalidation of cached temperature/load before each LHM update, deterministic CPU/GPU compute checks, explicit auto-apply, extended tests/history, pending recovery using saved devices, shutdown/logout coordination and blocked exit on unconfirmed rollback. Native adapters are separated from portable orchestration for regression tests.
- Windows Tuner: embedded PowerShell runtime, protected write-ahead journal and trusted system-module path, original caller SID, exact registry/service/task/power/BCD/NTFS/reserved-storage restoration for recorded actions, errors retain recovery data, verified in-memory elevated script, installer hash/trust checks under a locked handle, preview/history/statuses and legacy snapshot visibility.
- Diagnostics: bounded concurrent pipes and cancellation/timeout cleanup, isolated ETW session names, provider-aware events/errors/grouping, separate CSV streams and valid timing metrics, long stalls, graph/history/compare, export without authorization data or device IDs, async audit and telemetry with shutdown cancellation.
- Profiles/display: foreground game selection and immutable process bindings, exact manual-profile restoration, validation and persistence of deletions, profile CRUD/import/export, independent startup/effect flags, debounced writes, exact gamma/vibrance and DRS recovery, resolution confirmation/rollback, persistent scaling baseline, hot-plug handling, shared crosshair geometry and per-monitor window placement. Dormant controls and legacy hidden UI removed; illustration/BIOS limitations labelled.
- Local auth: ASP.NET, SQLite, Argon2id, one-time random licenses and expiring login tokens, device binding, persistent signing key, revocation/logout, expiring signed grants, pinned client key, bounded offline access and periodic validation. Prototype is explicit; no default server account/key. Local service and matching HTTP client are tested.
- Build: SDK pin, solution, NuGet lockfiles, Linux/Windows CI, checked native exit codes, generated release evidence, optional Authenticode signing, expanded library replacement build, third-party notices, versioned source archives and package-license inventory.

## Evidence and external limits

Current test totals and EXE checksum belong in the generated release report, not in this plan. The release command executes catalog smoke, regression/authentication tests and PowerShell fixtures before publishing.

- Cloud Linux does not prove WPF runtime, UAC, DPAPI, NVAPI/Direct3D, sensor/driver compatibility, native system changes, hardware stability or mixed DPI. Complete `RELEASE-CHECKLIST.md` on real Windows before distribution.
- AMD PBO writes remain disabled because original limits cannot be reliably captured by the supplied adapter. Four AppX removals and disabling hibernation remain unavailable until exact recovery can be implemented. These are guarded unsupported capabilities, not passed tests.
- No hosting/domain or Authenticode certificate was supplied. The backend works locally; commercial HTTPS deployment and EXE signing have not occurred. Default output is unsigned Prototype.
- Docker build reached the official Microsoft registry but CDN image download was denied by cloud egress policy. Host service execution and HTTP integration were verified; a successful Docker run is not claimed.
- Existing v0.10 tweak snapshots are detected and preserved, not automatically executed or treated as complete trusted recovery state. v0.11 records the state immediately before its own changes.
- Reusable setup instructions preserve the existing checkout, use verified tool hashes, retain caches outside source, start local services when needed and do not assume processes survive publication.
