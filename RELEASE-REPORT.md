# AURA v0.10 — automated release report

Date: 2026-09-20

## Passed

- Release build: 0 errors, 0 warnings.
- Windows tweak catalog: 82 definitions; 47 Safe, 28 Caution, 7 Danger.
- Performance presets: Low 25, Medium 41, Ultra 61; zero Danger entries in any preset.
- Tweak validation: unknown IDs rejected; Fast Startup / Disable Hibernation conflict detected.
- Restore generation: registry/service/task snapshots restored without recreating deleted backup files.
- XAML: 98 event bindings resolved to code-behind handlers.
- Navigation: eight visible sections; Monitor Pro, duplicate Profiles, phone control and hidden Resolution page absent.
- Single-file publish: Windows x64 GUI executable with embedded PawnIO setup, RyzenSMU, IntelMSR and PresentMon resources.

## Embedded component hashes

| Component | SHA-256 |
|---|---|
| PawnIO setup 2.2.0 | `1f519a22e47187f70a1379a48ca604981c4fcf694f4e65b734aaa74a9fba3032` |
| RyzenSMU 0.2.11 | `301d9ca397108e09f31bfbd5ac4c9bb4f352a5de68532c32db3ba7ddcde93450` |
| IntelMSR 0.2.11 | `d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f` |
| PresentMon 2.5.1 | `9bec3083069f58f911e6a512f4806db51a27bd096103087bc1d05ef54c80a191` |

## Release artifact

- `Aura.exe`: 209,651,224 bytes.
- SHA-256: `4cf19028f99103c4b3474d3d4d4bf015c238f468922c0e8375a648701a6e83d7`.

## Still requires physical Windows QA

WPF compilation and pure-code smoke tests cannot exercise real monitor gamma/scaling, NVAPI overclocking, PawnIO driver installation, CPU firmware locks, UAC, UEFI restart or Windows registry/service changes from the Linux builder. Follow `RELEASE-CHECKLIST.md` on test Windows PCs before public sale.
