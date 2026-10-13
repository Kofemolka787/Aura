# Third-party notices — AURA v0.11

The components below retain their upstream copyrights and licenses. Their authors do not endorse AURA. Runtime NuGet dependencies and package-supplied license texts are inventoried in `ThirdParty/NuGet/dependencies.json`; exact dependencies and package content hashes are in the lockfiles.

| Component | Version/use | License | Project |
|---|---|---|---|
| .NET / Windows Desktop Runtime | 8.0.31, self-contained application | MIT and runtime third-party notices | https://github.com/dotnet/runtime |
| PresentMon | 2.5.1 x64 executable, frame capture | MIT | https://github.com/GameTechDev/PresentMon |
| LibreHardwareMonitorLib | 0.9.6, sensor access | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| NvAPIWrapper.Net | 0.8.1.101, NVIDIA APIs | LGPL-3.0 | https://github.com/falahati/NvAPIWrapper |
| Vortice.Direct3D11 / D3DCompiler | 3.8.3, DirectCompute workload | MIT | https://github.com/amerkoleci/Vortice.Windows |
| PawnIO official setup | 2.2.0, unmodified driver installer | GPL-2.0 with upstream linking exception | https://github.com/namazso/PawnIO |
| PawnIO Modules: IntelMSR / RyzenSMU | 0.2.11, signed modules; Ryzen writes disabled | LGPL-2.1-or-later | https://github.com/namazso/PawnIO.Modules |
| Microsoft.Data.Sqlite | 8.0.31, local authorization database | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw / SQLite | Transitive database runtime | Apache-2.0 / public domain (see package inventory) | https://github.com/ericsink/SQLitePCL.raw |
| Konscious.Security.Cryptography.Argon2 | 1.3.1, Argon2id password hashing | MIT | https://github.com/kmaragon/Konscious.Security.Cryptography |

Full versioned source snapshots for NvAPIWrapper, LibreHardwareMonitor, PawnIO (including its pinned PawnPP submodule), and PawnIO Modules are included as archives under `ThirdParty/Sources`. `manifest.json` records their upstream commits and archive SHA-256 values. The module sources already supplied in `ThirdParty/PawnIO.Modules` were compared with the 0.2.11 tag and match. Upstream build instructions and license texts remain in the archives; these archives are data and are not compiled into AURA.

LGPL components can be modified and the application rebuilt. Extract the corresponding archive outside the application directory, build its library using the upstream instructions, and replace its NuGet package reference in `Aura.csproj` with a project reference to the modified source. Rebuild AURA with the SDK in `global.json`. Alternatively, `scripts/build-release.ps1 -Expanded` produces an application with separate DLLs for replacement and debugging. No reverse-engineering restriction is introduced for debugging modifications to LGPL libraries.

PawnIO installation requires an explicit UI action and UAC. AURA verifies the bundled installer's SHA-256 and Authenticode trust while denying write/delete access to that file; it uses only the official signed, hash-checked modules in restricted mode. Driver and module signature requirements are preserved. Source archives do not contain any AURA licensing private key or signing certificate.
