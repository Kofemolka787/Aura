# Third-party notices

AURA v0.10 uses the following third-party components. Their authors do not endorse AURA and provide their software under the respective licenses.

| Component | Version/use | License | Project |
|---|---|---|---|
| PresentMon | 2.5.1 x64 executable, embedded for frame-time capture | MIT | https://github.com/GameTechDev/PresentMon |
| LibreHardwareMonitorLib | 0.9.6, hardware sensor access | MPL-2.0 | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor |
| NvAPIWrapper.Net | 0.8.1.101, NVIDIA display/DRS/P-State API wrapper | LGPL-3.0 | https://github.com/falahati/NvAPIWrapper |
| Vortice.Direct3D11 / Vortice.D3DCompiler | 3.8.3, DirectCompute stability workload | MIT | https://github.com/amerkoleci/Vortice.Windows |
| PawnIO official setup | 2.2.0, official signed driver installer embedded unmodified | GPL-2.0 with the upstream linking exception | https://github.com/namazso/PawnIO |
| PawnIO Modules: RyzenSMU / IntelMSR | 0.2.11, official signed modules used by capability-checked AMD PBO and Intel RAPL adapters | LGPL-2.1-or-later | https://github.com/namazso/PawnIO.Modules |

PawnIO is never installed silently at application startup. Installation requires an explicit UI action and UAC consent. AURA loads only the bundled, hash-checked official signed module and does not request PawnIO's unrestricted/developer mode.

The full PawnIO and PawnIO Modules license texts bundled with this source distribution are located at:

- `ThirdParty/PawnIO/COPYING`
- `ThirdParty/PawnIO.Modules/COPYING`

The corresponding module sources used for the embedded binaries are included at:

- `ThirdParty/PawnIO.Modules/RyzenSMU.p`
- `ThirdParty/PawnIO.Modules/IntelMSR.p`
