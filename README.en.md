# TIA Portal Source Bridge

[简体中文](README.md) | **English**

[![Offline regression](https://github.com/qiap/TiaPortalSourceBridge/actions/workflows/checks.yml/badge.svg)](https://github.com/qiap/TiaPortalSourceBridge/actions/workflows/checks.yml)

A local bridge between **Siemens TIA Portal** and **AI-assisted engineering and automation workflows**.

Using TIA Portal Openness, the project provides PLC source import and export, project indexing, compilation diagnostics, and HMI and hardware configuration tools. Use the browser dashboard directly, or connect an AI client through MCP (Model Context Protocol).

The application runs on Windows and provides a unified entry point for separate V17 and V21 engines.

## Features

| Module | Capabilities |
| --- | --- |
| Web dashboard | View project and connection status, run engineering tasks, review backup diffs, search indexes, and inspect logs |
| Program audit | Read-only checks for explicit address overlap, multiple writes, missing template connections, and instance reuse; browse OB paths and caller/callee evidence, and export Excel reports |
| PLC source | Read and import SCL, DB, UDT, and SimaticML XML, individually or in batches |
| Cylinder execution history | Persistent generation, inspection, and recovery records with per-block results, compiler warnings, backups, and save status; inspect a failed operation before continuing or restoring it |
| Project index | Browse the project tree, search content, find explicit references, and retrieve task context |
| LAD / GRAPH | Generate LAD, read and edit GRAPH models, and apply batch changes and imports |
| HMI and hardware | Work with classic HMI screens, tags, and alarms; read and modify hardware and network configurations |
| Multiple projects | Route requests by project path, with separate processes isolating projects and version-specific APIs |
| Online monitoring and simulation | Read online variables, record samples, and use PLCSIM tools; availability depends on the channel and version |
| V21 extensions | SimaticSD documents, Software Units, official cross-references, Unified HMI reading and compilation, and AutomationML |

For the current tool list and parameters, use the dashboard's **MCP tool dictionary** or the MCP `tools/list` response. See [Documentation](#documentation) for detailed guides.

## Download and run

You do not need to download the source or compile the application. Choose either package:

Current release: **v4.10.0**. The source, V17/V21 binaries, installer and portable package share this version. See the [v4.10.0 release notes](https://github.com/qiap/TiaPortalSourceBridge/releases/tag/v4.10.0) for changes and validation scope.

| Package | Download | How to run |
| --- | --- | --- |
| Portable ZIP | [Download v4.10.0 Portable.zip](https://github.com/qiap/TiaPortalSourceBridge/releases/download/v4.10.0/TiaPortalSourceBridge-4.10.0-Portable.zip) | Extract the entire archive, then double-click `WebDashboard.cmd` or `LaunchWeb.vbs` |
| Windows installer | [Download v4.10.0 Setup.exe](https://github.com/qiap/TiaPortalSourceBridge/releases/download/v4.10.0/TiaPortalSourceBridge-4.10.0-Setup.exe) | Run the installer, then open the application from the desktop or Start menu |

Both packages include the V17/V21 engines, default configuration, and runtime templates. Open your project in TIA Portal first, then select the project and PLC in the dashboard. The target computer must meet the requirements below.

[All releases and release notes](https://github.com/qiap/TiaPortalSourceBridge/releases) · Download `SHA256SUMS.txt` from the same version's assets to verify both packages.

GitHub's **Code → Download ZIP** and the automatically generated **Source code** release archives contain source code, not a ready-to-run application. For portable use, download **Portable.zip** above. The EXE installer is currently unsigned.

Refreshing, switching, or closing browser tabs does not stop the background service. To stop it, use **退出服务** (Exit service) in the dashboard. Active engineering tasks finish before shutdown.

### v4.10.0 changes and upgrading

- Project review adds independent source snapshots, caller impact paths, structural differences, multilingual text updates and delivery exports.
- Electrical Excel reconciliation supports reviewed V17 writes. Cylinder workflows add persistent history, inspected recovery, reusable templates, batches and additional actuator types.
- New panels provide read-only online monitoring, parameter archives, GRAPH Boolean condition checks, classic HMI companion changes and managed local PLCSIM peripheral simulation. Supported scope and limitations are documented below.
- Shortcuts use a dedicated Windows launcher. Packaging checks cover Unicode paths with spaces, icons, actual shortcut launch and dashboard responses.

Exit the old dashboard service before upgrading. For portable use, **extract the complete ZIP into a new directory**; do not replace only a BAT, VBS or individual EXE. Run `创建快捷方式.bat` or `CreateShortcuts.vbs` from the new directory to recreate the desktop shortcut. Repeat after moving the directory. Retain old project configuration, backups, templates and operation records and migrate them as needed. Installer upgrades retain existing configuration.

If the shortcut creation window closes immediately, enter `cmd /k call 创建快捷方式.bat` in Explorer's address bar in the extracted directory to retain the error output. Run `WebDashboard.cmd` for startup diagnostics. Source checkouts require compilation; portable packages include the binaries.

Release checks cover both engine builds, offline regressions and packaged startup. Offline tests of new engineering writes, classic HMI changes and simulation do not establish compatibility with every TIA version or real project; use the preview and verification results documented for each workflow.

### Execution history and failure recovery

Open **工程变更记录 · 气缸** (Cylinder operation history) in the control center or execution log to revisit a task after refreshing the page or restarting the tool. These local engineering records are retained independently of the general log's latest 100 calls.

A failed cylinder operation preserves the current state. **核对当前工程** performs a read-only inspection of background activity, program blocks, I/O declarations, and backups. When the evidence is complete and unchanged, review the continue or restore plan and explicitly confirm execution. Continuing applies only the remaining steps; restoring first backs up the current state and includes affected instance DBs. Saving applies to the entire open project, including other unsaved edits.

Lost responses and timeouts trigger status lookup, never automatic write replay. Unconfirmed background activity, subsequent edits, or incomplete evidence disable automatic continuation and restoration. Older records remain viewable but may require manual handling because they lack recovery evidence. These operations modify the offline TIA project and do not download to a PLC.

### Cylinder templates and batch generation

Read the project, select an existing cylinder, and review its parameter roles before saving the template for that project. Rules are stored under `project-templates/` in the tool directory. Adding other cylinders does not invalidate the selected sample; changes to its wiring or FB interface require another review. Rules can be exported and imported, with wiring and I/O selections reviewed again in the target project.

Enter a cylinder name, instance name, array index, and unused I/O points, then add it to the batch list. Each batch supports up to 20 cylinders using the same template. The tool checks duplicate names, instances, indices, and physical I/O addresses, combines changes into one write per block, and compiles, reads back, and saves the result together. Failed batches retain their execution records. Project templates and operation history are local data excluded from source control.

### Electrical Excel I/O reconciliation

In **I/O 表与引用** (I/O tables and references), open **电气 Excel 点表对账**. Read the current PLC snapshot, upload an `.xlsx` file, and choose the sheet, header row, and address/name/type/comment columns. Address and name are required; type and comment are optional. Select native tag tables or mapped physical I/O points, then choose the software unit and table scope.

The read-only report identifies changed fields, entries listed on only one side, duplicate names/addresses, overlapping address ranges, ambiguous matches, and invalid Excel data. It retains original Excel row numbers and PLC candidate values. I/Q/M bit, byte, word, and double-word addresses are normalized, including `%`, German E/A notation, and leading zeros. Mapped points use complete symbolic names. Overlaps can be intentional aliases, and zero reference evidence does not establish that an address is unused. Target, snapshot, or column changes invalidate the displayed comparison.

Reconciliation itself is read-only. V17 can turn selected changed or Excel-only rows from one ordinary native tag table into a reviewed write plan. Execution checks every native table fingerprint again, backs up the selected table, imports, compiles, verifies the result, and saves the entire offline project. Conflicts, software units, mapped DBs and incomplete/unsupported declarations are excluded. Renaming does not automatically refactor PLC/HMI external references. Failed writes retain their records and state; restoration requires unchanged post-operation evidence. V21 retains read-only reconciliation.

The importer accepts `.xlsx` files up to 8 MB, 20 sheets, 20000 nonempty rows in total, and 128 columns; save legacy `.xls` files as `.xlsx` first. Hidden rows are included and Excel filters are not applied. Formulas, error cells, and merged data cells are explicitly marked for correction; paste them as individual values first. Comparisons use the selected snapshot, not live values, unassigned hardware channels, or external device usage. The process keeps the latest three uploads and reports in memory. Export to retain the overview, all comparison rows, and PLC candidate evidence independently of the page filter.

### Structural change review

Open **变更审查与 Diff** and select a backup to compare against the indexed source snapshot for the selected project and PLC. Structural changes cover tag names, addresses, types and comments; block interfaces and DB/UDT member order, types and start values; and program networks matched by position. Filter by area, expand before/after values, or switch to the original line diff. Backup and snapshot timestamps and SHA256 hashes remain visible.

Comparison does not connect to or write to TIA Portal. For fresh evidence, reread from TIA on the I/O page first. Missing, stale, altered or ambiguous snapshots produce an error rather than an apparent deletion. The list shows the latest 100 matching backups, including batch subdirectories. Legacy backup metadata lacks software-unit identity, so unit-scoped objects are not automatically matched.

This first version compares XML structure, ignoring export-envelope timestamps and known object IDs. Supported LAD/SCL network UIds are normalized together with their references; unknown structures retain identifiers conservatively. Networks are matched by order, so insertion or movement can produce multiple changes. Member renames appear as removal and addition. GRAPH/STL and unknown structures may retain ID noise; plain text and SimaticSD retain the original text comparison. The tool does not establish logical equivalence or predict DB offsets, download reinitialization or HMI impacts. Each source is limited to 8 MiB and the complete response to 32 MiB; exceeding a limit produces an explicit error.

### Saved snapshots, impact paths and delivery documents

The change-review tab can preserve the indexed PLC sources as independent, hash-verified snapshots and compare any two belonging to the same project and PLC. Impact paths show explicit callers resolved from either snapshot. They do not predict DB layouts, HMI absolute-address changes or download reinitialization.

The documentation ZIP contains the source catalog and XML, call-graph JSON/Mermaid, I/O and reference workbooks, translations, supported GRAPH structure diagrams, and coverage notes. Unread HMI resources are not included.

### Reviewed text and parameter migration — V17

- **Translations:** export a selected snapshot's workbook, edit `Translation`, mark selected rows `update`, then upload for review. Snapshot, source hash, original text, resource and language identities must still match. A language-coverage worksheet lists empty, missing and duplicate entries relative to languages observed in that snapshot. It does not add project languages automatically.
- **Parameters:** configure read-only monitoring using `symbol name | absolute DB address | type`, read and archive values, then select a source snapshot and the parameter archive ID in change review. Migration matches standard global DB scalars by name, type and DB number. Arrays, optimized DBs, instance DBs and unmatched fields are excluded. User-supplied symbolic names and physical offsets are not proof of the online program's identity; samples may span scan cycles.
- **Writeback:** review 1–50 ordinary PLC blocks, explicitly confirm, then back up, import, compile and verify the complete objects, including comments and start values, before saving the entire offline project. This entry point does not write tag tables or UDTs.
- **Recovery:** query the durable execution ID and request a continue/restore preview. Current blocks must still match recorded post-operation fingerprints. A new reviewed plan is created; incomplete evidence, unresolved tasks or later changes block automatic recovery. There is no automatic rollback or write replay.

### Actuator reuse and Classic WinCC companion changes — V17

Template roles support motors, valves, vacuum devices and servo peripheral enable logic alongside cylinders. They reuse existing direct FB multi-instance calls and the same verified Bool mapped-I/O and 20-device batch workflow. Specify each I/O naming suffix explicitly. This does not create drives, motion axes or safety programs, or infer interlocks.

After checking the PLC generation result, use the companion HMI panel to carry over device names and instances. Specify the full Classic HMI identity, alarm IDs, donor alarm IDs, existing 16-bit HMI trigger tags and bit numbers. Alarm text accepts `{name}`, `{instance}` and `{index}`. Optional text-list updates reuse existing single-value `Value` entries and an existing plain-text language; ranges, dynamic fields and unknown export shapes are refused. Preview, confirm, import, verify and compile before saving the entire project. PLC and HMI changes are separate operations, not an atomic transaction. HMI failure preserves its records and backups for manual review and does not undo a completed PLC operation.

Native alarm export/import requires a signed-in, unlocked TIA desktop. Explicitly verify the HMI-to-PLC trigger mapping. Real-project HMI integration has not been validated by the offline suite.

### Read-only waveforms and GRAPH conditions

The monitoring tab reads standard DB absolute addresses over S7ONLINE or S7 TCP: up to 128 variables, 50–10000 ms intervals and 30-minute runs. The UI shows the latest 500 change events; export the complete JSONL after stopping. Bad-quality values are unknown. Polling can miss short pulses and is not a scan-by-scan PLC trace.

Select an archived GRAPH block and step to view interlocks, supervisions and traceable following transitions. Global scalar normally-open/closed Boolean contact networks are evaluated using explicitly bound monitor names. Edges, comparisons, timers, local/array/indirect operands and unsupported networks remain unknown. Click the monitor's read-once action; samples expire after 10 seconds. The selected step is not automatically detected as active, and a true condition is not proof that movement is safe.

### Configurable peripheral simulation — V17

The simulation tab supports delayed Bool command-to-feedback rules, stuck feedback, timed stimuli and assertions. It accepts only tool-owned, project-bound, running local PLCSIM Advanced Softbus instances. It cannot target physical hardware. Confirm execution explicitly; duplicate feedback writers and feedback loops are rejected. Stopping ends future writes and preserves current simulation values without resetting or stopping the CPU.

Runs persist configurations, assertion results and write-intent logs. Missed deadlines fail rather than passing with late values. The UI shows the latest 500 events; completed runs support full event-log export. Fake-adapter tests verify orchestration and guards, not the actual SDK, downloaded program or machine behavior.

Snapshots, parameter archives, trace files, simulation results, HMI backups and templates are local engineering data excluded from Git. New write workflows target **V17 and Classic WinCC**; V21 retains its existing capabilities.

### Program audit

Select a project and PLC, then open **程序体检** (Program audit). Read the local source cache or read fresh sources from TIA Portal before scanning; an existing completed snapshot can also be checked directly. Reports identify the source, timestamps, coverage, and relevant blocks, networks, and variables. Filter by rule or text, or export the full report and its coverage notes to Excel, independently of the current filter.

Checks cover supported explicit I/Q/M declarations, LAD coils and call parameters, and selected STL write instructions. Template connection checks use only reviewed cylinder templates that still match the current project. Aliases and multiple writes may be intentional and require review of execution order and device requirements.

The **调用关系** (Call relationships) view uses every identified OB in the snapshot as an entry point. Select a block to inspect one discovered OB path and its incoming or outgoing call sites and instances. Unresolved calls remain visible. Multiple calls to the same identified instance DB or host-local multi-instance produce a review note; software units and local instances in different hosts remain separate. A missing OB path means no path was found within the recognized snapshot, not that a block never executes or can be deleted. Missing sources, unresolved targets, and output limits are disclosed. Excel reports add call-relationship and OB-path worksheets.

SCL checks read the full cached source and recognize supported direct assignments plus output/in-out argument writes verified against the called block's interface. These locations are grouped with LAD/STL writes. Comments, strings, and input arguments are excluded. Evidence includes the statement, line, column, and location basis in both the dashboard and Excel. Locations refer to exported text or reconstructed text within a network, not absolute TIA editor line numbers.

Complete SCL/GRAPH control flow, Temp initialization, dynamic addressing, hardware addresses, DB layouts, and HMI/external writes are not covered. Unverified calls, complex targets, and unresolved timer/edge-trigger instances have explicit coverage notes. Writes in separate branches do not necessarily conflict, and no findings does not establish logical safety. Analysis does not modify, compile, save, or download the engineering project.

The service retains up to four reports in memory. Export Excel files to keep them after restarting the tool.

## Requirements

- **Operating system:** Windows 10 / 11, 64-bit.
- **Runtime:** .NET Framework 4.8.
- **Engineering operations:** The matching version of TIA Portal and its Openness component.
- **User permissions:** The current Windows user must belong to the local `Siemens TIA Openness` group. Sign out and back in after changing group membership.
- **Building and testing from source:** Windows PowerShell 5.1, the .NET Framework C# compiler, Python 3, and Node.js 22 (UI regression checks only).
- **Optional features:** Online communication requires the relevant Siemens communication components. Simulation requires a compatible PLCSIM / PLCSIM Advanced installation.

TIA Portal, Siemens DLLs, and related software licenses are not included.

### Version compatibility

The unified entry point manages V17 and V21 projects. Projects with `.ap21` / `.als21` extensions are handled by the `engines/v21` subprocess; you do not need to start the internal engine separately.

Feature coverage differs between engines. Refer to individual tool descriptions for V21-specific interfaces and simulation capabilities. Other TIA versions, devices, and HMI types require separate validation and should not be assumed to be fully compatible. Passing offline tests does not establish that a target project compiles or runs correctly in TIA Portal.

## Run from source

Run the following commands in **PowerShell**.

### 1. Clone the repository

```powershell
git clone https://github.com/qiap/TiaPortalSourceBridge.git
cd TiaPortalSourceBridge
```

### 2. Create local configuration

Create `config.json` in the repository root. Replace the project path and PLC name with your own:

```json
{
  "tiaPortalVersion": "V17",
  "projectPath": "D:\\Projects\\Demo\\Demo.ap17",
  "plcName": "PLC_1",
  "sourceDirectory": ".\\plc-src",
  "siemensEngineeringDll": "",
  "connectionMode": "AttachOnly",
  "withUserInterface": true,
  "saveProjectAfterImport": false,
  "compileBeforeExport": false,
  "includeSystemBlocks": false,
  "continueOnError": true,
  "allowProjectUpgrade": false
}
```

Leave `siemensEngineeringDll` empty to discover the local installation automatically. This configuration attaches only to an already-open TIA instance and does not automatically save or upgrade the project.

Prepare configuration for the MCP entry point and V21 engine:

```powershell
Copy-Item config.json mcp-config.json

$v21 = Get-Content config.json -Raw | ConvertFrom-Json
$v21.tiaPortalVersion = 'V21'
$v21.projectPath = '.\SelectProject.ap21'
$v21 | ConvertTo-Json | Set-Content engines\v21\config.json -Encoding UTF8
Copy-Item engines\v21\config.json engines\v21\mcp-config.json
Copy-Item engines\v21\config.json mcp-config-v21.json
```

`SelectProject.ap21` is a placeholder for engine initialization. When working with V21, select the actual project in the dashboard or supply its full `project_path` in MCP calls. If you primarily use V21, also update the root configuration with the actual V21 project path and version.

### 3. Build and open the dashboard

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-All.ps1 -NoTrust
.\WebDashboard.cmd
```

The build compiles both engines and runs offline regression checks before updating runtime files. The dashboard opens at [http://localhost:18980](http://localhost:18980) by default.

Open the target project in TIA Portal, then select the project and PLC in the dashboard to inspect its status, build an index, or export sources.

Approve the TIA Openness access prompt when connecting for the first time. To register the executable in the allowlist, use **注册白名单** (Register allowlist) in the dashboard and approve the Windows administrator prompt. Registration may need to be repeated after updating or moving the executable.

## I/O tables, cross references, and Excel

Select a project and PLC, open **I/O 表与引用** (I/O and references), then click **重新读取** (Read again). Each click rereads tag tables and program-block references, with progress and a completion timestamp.

- View I/Q inputs and outputs, or select all tags and constants. Filter by area, tag table, name, address, or comment.
- Open a tag's references to inspect blocks, access relationships, locations, and evidence sources.
- **导出 Excel** (Export Excel) downloads an `.xlsx` with all filtered pages, reference details, and read coverage notes.
- Changing project or PLC requires a new read. Failed or incomplete reads are reported explicitly.

The view includes declared PLC tags, not unassigned hardware channels or live values. V17 shows explicit indexed references; V21 uses the official cross-reference interface. Indirect addressing and cross-unit bindings may not be fully covered. Zero references does not prove a tag is unused. Coverage and warnings are included in the workbook.

## Connect an MCP client

### Use the dashboard's background service

The dashboard starts an MCP background service automatically. For clients that support the `mcpServers` format, use the example below and replace `D:\\Tools\\TiaPortalSourceBridge` with the actual application directory:

```json
{
  "mcpServers": {
    "tia-portal": {
      "command": "D:\\Tools\\TiaPortalSourceBridge\\src\\TiaPortalSourceBridge\\bin\\Release\\net48\\TiaPortalSourceBridge.exe",
      "args": ["mcp-client"]
    }
  }
}
```

### Run independently

To use MCP without the dashboard, start the application in stdio mode:

```powershell
.\src\TiaPortalSourceBridge\bin\Release\net48\TiaPortalSourceBridge.exe mcp --config .\mcp-config.json
```

In your MCP client, use the same executable with arguments `mcp`, `--config`, and the absolute path to the configuration file. Configuration formats vary between clients.

When managing multiple projects, specify `project_path` explicitly. Retrieve the project tree or task context before reading and modifying relevant objects, rather than repeatedly exporting the entire project. See [Multi-project connections](MULTI_PROJECT.md) for connection management details.

## AI skill

The repository includes a [tia-portal skill](skills/tia-portal/SKILL.md) that guides an AI through project selection, index lookup, source changes, backups, and verification using the existing MCP tools. The skill does not replace the runtime or MCP connection and does not automatically authorize project writes.

For local Codex use, copy the entire `skills/tia-portal` folder into the skills directory (by default `~/.codex/skills/`, or `skills/` under `CODEX_HOME` when set), and configure MCP as described above. Example request:

```text
Use $tia-portal to analyze the cylinder interlocks in my selected project without modifying it.
```

The skill guides AI engineering workflows. The runtime remains independently usable as a portable ZIP or EXE installation.

## Build Windows packages

The repository includes a Windows installer build script. The build machine also needs [Inno Setup 6](https://jrsoftware.org/isdl.php):

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-Installer.ps1 `
  -CompilerPath 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
```

The EXE installer, portable ZIP, and SHA-256 checksum files are written to `artifacts/installer/`. Both packages use the same build output. The script rebuilds both engines, runs offline checks, and generates clean default configuration. It excludes the development computer's local configuration, logs, and backups.

The installer supports per-user installation, desktop and Start menu shortcuts, upgrades, and uninstallation. Upgrades preserve configuration. Uninstallation preserves configuration and user-generated logs and backups. See the [installation notes](installer/INSTALL.txt) for details.

## Documentation

The detailed guides below are currently in Chinese.

| Guide | Topics |
| --- | --- |
| [Project index](PROJECT_INDEX.md) | Index creation, content search, references, and task context |
| [Multi-project connections](MULTI_PROJECT.md) | Target selection, version isolation, connection management, and timeouts |
| [GRAPH, hardware, and HMI](CAPABILITIES.md) | Object models, generation, and import interfaces |
| [HMI import validation](HMI_VALIDATION.md) | Preflight checks, readback verification, coverage, and limitations |
| [HMI alarms](HMI_ALARMS.md) | Classic WinCC discrete alarms |
| [Batch XML import](BATCH_XML_IMPORT.md) | Batch backups, imports, compilation, and verification |
| [Online monitoring](ONLINE_README.md) | Communication channels, sampling, and simulation test interfaces |
| [Simulation](SIMULATION.md) | PLCSIM / PLCSIM Advanced workflows |
| [Process templates](PROCESS_LIBRARY.md) | Reference material and parameterized templates |
| [Performance](PERFORMANCE.md) | Batch processing, caching, and measurement methods |

These guides include version-specific validation records. Check current tool parameters alongside the documented limitations.

## Development and testing

```text
src/TiaPortalSourceBridge/  Main engine, MCP service, and Web backend
engines/v21/               V21 engine and version-specific implementations
shared/                    Modules shared by both engines
web/                       Browser dashboard
online-helper/             Online communication helper
templates/                 Runtime templates and process reference material
tests/                     Offline regression and installer lifecycle tests
scripts/                   Build and utility scripts
installer/                 Windows package definitions
```

Run the offline regression suite:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\tests\Run-Checks.ps1
```

The suite covers dual-engine protocol handling and routing, indexing, HMI validation, task reliability, and browser lifecycle behavior. It does not connect to TIA projects or write to PLCs. GitHub Actions runs the same entry point on Windows.

Report bugs or suggest features through [Issues](https://github.com/qiap/TiaPortalSourceBridge/issues), or submit improvements through a pull request. Include the TIA version, device type, reproduction steps, and sanitized logs. For changes that write to projects, describe backup, verification, and failure-handling behavior.

## Operational considerations

- Writing to an engineering project, saving it, and downloading to a simulator are separate operations. Check the tool parameters and target project. Back up important projects and validate changes on a copy first.
- Use `dry_run` where available. For example, actual classic HMI imports require both `dry_run=false` and `confirm=true`.
- After an engineering operation times out, check connection status and any delayed result before submitting the write again.
- Online reading and simulation control have different scopes and limitations. See the online monitoring and simulation guides.

## License

This repository does not currently specify a project-level open-source license. Third-party license notices remain in their respective directories. Public source availability does not imply a grant under a common open-source license.
