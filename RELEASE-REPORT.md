# AURA v0.11 — current development release evidence

Generated from `release-v0.11-current/release-report.json` for the executable built on 2026-10-13T06:06:04.4459122Z. This replaces the historical v0.10 report.

- SDK: 8.0.425; Windows x64 PE verified.
- EXE: 210,060,824 bytes; standalone ReadyToRun single file.
- SHA-256: `7cd73067d5295d7129351ef89070d939e802dccafa00079125cd620aae09ed72`.
- Authentication: Prototype.
- Signature: Unsigned development build.

- Aura.AuthTests: 8/8 passed; skipped 0.
- Aura.RegressionTests: 39/39 passed; skipped 0.
- PowerShell: 12 fixture checks; no native writes.
- Catalog: 82 definitions; risks 47 Safe / 28 Caution / 7 Danger; presets 25/41/61; no Danger in presets.
- Cached-sensor fixtures use the pinned LHM managed Sensor implementation on Linux without opening hardware; they prove stale cache invalidation, not native driver behavior.
- Current build: zero warnings/errors. Locked dependency restore succeeded. NuGet vulnerability audit checked six projects with no known vulnerable packages reported by the current source.
- Local ASP.NET host started and returned the expected health protocol; invalid login was rejected. Real ASP.NET HTTP pipeline and matching client exercised by the authentication test suite.
- Versioned copyleft source archive hashes verified; 48 runtime NuGet packages inventoried. Licensing files are packaged separately from the EXE.

## Unperformed and blocked checks

Native WPF behavior, UAC, DPAPI on Windows, registry/services/tasks/power/BCD, driver installation/trust, NVAPI/Direct3D, physical CPU/GPU/monitor writes, hardware stability and mixed DPI were not run in this Linux environment. Windows CI is configured but its remote run is not part of this local evidence. Follow `RELEASE-CHECKLIST.md` before distribution.

Docker build was blocked by cloud network policy at Microsoft's registry CDN; no container run is claimed. The ordinary local backend works. Commercial domain/hosting and Authenticode certificate were not supplied, so no public deployment or EXE signing occurred.

AMD PBO writes, four AppX removals without trusted MSIX recovery sources, and disabling hibernation without exact type/size recovery are explicitly unavailable. They are not counted as passed hardware/Windows tests.

The archived v0.10 tweak journal is preserved and flagged; it is not automatically trusted or executed. v0.11 can restore the values it records before its own changes.
