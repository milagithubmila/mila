# AGENT TASK — Build the Paradox PRT3 Crestron driver (.pkg)

> גרסת טקסט של START-HERE.html — נוחה להטמעה בצ'אט הדיספאטצ'.

## Goal
Produce the Crestron driver **`.pkg`** for the Paradox PRT3 security driver by building
it in Visual Studio 2026 on the user's machine.

## Context
- Visual Studio 2026 is installed.
- The user's repo is cloned locally and contains a folder **`Crestron SDK Folder`**
  with Crestron's Security System *IP* sample:
  `SecuritySystemDriverIP.cs`, `SecuritySystemProtocol.cs`, `SecuritySystemArea.cs`,
  `SecuritySystemZone.cs`, `SecuritySystemKeypad.cs`, `SampleTransport.cs`,
  `SendTransportData.cs`, `DriverData.json`, and a **legacy `.csproj`**.
- The legacy `.csproj` is a .NET CF 3.5 / SmartDevice project → **modern VS cannot open it**
  (shows "Unsupported"). **Do NOT open it.**

## Package contents (this ZIP)
`replace-in-CrestronSDKFolder/` — 9 files:
- Replace (overwrite existing): `SecuritySystemProtocol.cs`, `SecuritySystemArea.cs`,
  `SecuritySystemZone.cs`, `SecuritySystemDriverIP.cs`, `SampleTransport.cs`
- Add (new): `ParadoxProtocol.cs`, `Prt3Engine.cs`, `ParadoxTcpTransport.cs`,
  `Paradox_PRT3_IP.csproj`

## Steps
1. Locate `...\mila-claude-crestron-paradox-integration-t1ufg9\Crestron SDK Folder\`.
2. Copy all 9 files from `replace-in-CrestronSDKFolder/` into it (overwrite the 5, add the 4).
3. Do NOT touch: `SecuritySystemKeypad.cs`, `DriverData.json`, `SendTransportData.cs`,
   `Properties\AssemblyInfo.cs`.
4. Move the legacy `SecuritySystem_Crestron_SampleDriverModel_IP.csproj` aside
   (rename to `.csproj.old`) so only `Paradox_PRT3_IP.csproj` is used.
5. In Visual Studio: File → Open → Project/Solution → `Crestron SDK Folder\Paradox_PRT3_IP.csproj`.
6. Accept NuGet Restore (pulls `Crestron.DeviceDrivers.DevKit` + `Crestron.DeviceDrivers.ManifestUtil`).
7. Set configuration to **Release**.
8. Build → Build Solution.
9. On success, find the `.pkg` in `Crestron SDK Folder\bin\Release\`.

## Build errors → fixes
| Error | Fix |
|-------|-----|
| `namespace 'Newtonsoft' could not be found` | NuGet → install `Newtonsoft.Json` (13.x). Rebuild. |
| `.NET Framework 4.7.2 not installed` / targeting pack missing | In `Paradox_PRT3_IP.csproj` change `<TargetFramework>net472</TargetFramework>` → `net48`. Rebuild. |
| duplicate assembly attributes | csproj already has `<GenerateAssemblyInfo>false</GenerateAssemblyInfo>`; if still failing, delete the duplicate `Properties\AssemblyInfo.cs`. |
| `System.Net.Sockets` / `TcpClient` not supported on platform | **STOP and report.** Needs a `CrestronSockets`-based replacement of `ParadoxTcpTransport.cs` — request it from the developer. |
| Any other error | **STOP, copy the full Error List text, and report.** Do not guess structural fixes. |

## Success criterion
A `.pkg` file exists under `bin\Release\`. Report its path. If failed, report the full
Error List and which fixes were already applied.

## Optional config (in `SecuritySystemProtocol.cs`, #region Fields)
- `MonitoredZones` (default `{1..8}`) — zone numbers to poll
- `MonitoredAreas` (default `{1}`) — partition numbers
- `UserPin` (default `1234`) — arm/disarm code
