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

v4.15.2 provides both a portable Windows ZIP and an EXE installer.

v4.15.2 combines VM automatic pairing with I/O layout, request/export timeout, stale-response and index-hash fixes. It synchronizes build assets, cleans successful packaging stages, preserves explicit local ports and isolates SSH host keys, tunnel records and logs per VM pairing.

| Package | Download | How to run |
| --- | --- | --- |
| Portable ZIP | [Download v4.15.2 Portable.zip](https://github.com/qiap/TiaPortalSourceBridge/releases/download/v4.15.2/TiaPortalSourceBridge-4.15.2-Portable.zip) | Extract the entire archive, then double-click `WebDashboard.cmd` or `LaunchWeb.vbs` |
| Windows installer | [Download v4.15.2 Setup.exe](https://github.com/qiap/TiaPortalSourceBridge/releases/download/v4.15.2/TiaPortalSourceBridge-4.15.2-Setup.exe) | Run the installer, then open the application from the desktop or Start menu |

Both packages include the V17/V21 engines, default configuration, and runtime templates. Open your project in TIA Portal first, then select the project and PLC in the dashboard. The target computer must meet the requirements below.

[All releases and release notes](https://github.com/qiap/TiaPortalSourceBridge/releases) · See the release attachment `SHA256SUMS.txt` for ZIP and EXE hashes.

GitHub's **Code → Download ZIP** and the automatically generated **Source code** release archives contain source code, not a ready-to-run application. For portable use, download **Portable.zip** above. The EXE installer is currently unsigned.

Refreshing, switching, or closing browser tabs does not stop the background service. To stop it, use **退出服务** (Exit service) in the dashboard. Active engineering tasks finish before shutdown.

### Execution history and failure recovery

The dashboard has collapsible navigation and a horizontal navigation strip on narrower screens. **任务中心** (Task center) lists recent device generation, tag-table writes, source updates, HMI operations, simulation runs with complete target identity, and parameter archives for the selected project and PLC. Filter or search records and open the original operation without copying its ID. Scan limits and unreadable records are reported; older simulation records without a PLC identity are not assigned by guesswork. Reading history never replays an operation.

HMI, peripheral simulation, and monitoring variables use forms, with raw configuration retained under advanced settings. Monitoring still requires an explicit standard-DB absolute address; a symbolic name does not establish that the address is correct. Variable lists can be saved per project in the current browser. Result cards show status, save confirmation, available block results, and the next action, with raw evidence in an expandable section.

Text and parameter updates have a separate page, including a parameter archive picker. I/O results distinguish cached source from a fresh TIA read. Refreshing connection status does not reread source. Traces include time labels, pointer/keyboard inspection, zoom, and display pause; pausing the display does not stop acquisition. Missing or bad-quality samples remain unknown.

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

## PLCSIM Advanced V8

The V17 main engine now matches the installed Advanced runtime API version, selects the CPU-type registration overload explicitly, and handles V8 global Softbus mode without changing the mode of existing instances. Offline dispatch and native SDK contract checks pass. Local live validation is blocked by Siemens Runtime Manager startup crashing with `0xC0000005`; program download, execution, and real symbol read/write have not been validated. See [simulation details](SIMULATION.md).

## HMI page editor

The V17 classic WinCC editor supports field patches for ordinary screens. Preview uses the read snapshot; apply rechecks live pages and dependencies before importing. Queue up to 20 pages in the same HMI for one batch import workflow, one compile, and one project save. Request-scoped dependency reads are reused, with cache invalidation before import and final readback. Unchanged content skips import, compilation, and saving. The UI shows execution stages, waiting time, and stage durations. Line endpoints are synchronized with geometry and verified after compilation. Alarm and unsupported compound controls remain protected. See [scope and workflow](docs/HMI-Page-Editor.md).

## License

This repository does not currently specify a project-level open-source license. Third-party license notices remain in their respective directories. Public source availability does not imply a grant under a common open-source license.


### MCP text summaries (source update)

Text `content` now includes target identity, status and a bounded projection of structured results for text-only agents. Connection lists, simulation state/IP, reference locations, per-item import outcomes and backup paths are visible. Small source snippets remain readable; `structuredContent` is unchanged.

Summaries have an approximately 16,000-character budget and show up to 30 entries per list, with short scalar properties combined on one line per entry. Source/XML already present in full text is not repeated. Compact plain source has a 12,000-character limit; XML snippets have a 2,400-character limit, both subject to the overall budget. Omitted data is explicitly marked in text and `text_summary.truncated`; the overall budget never cuts a list row mid-line. This is separate from server-side pagination (`offset`, `limit`, `next_offset`); omitted entries must not be interpreted as absent. Use `full` for complete XML.

Failures include `diagnostic` context and conservative categories. Timeouts never trigger automatic replay, and a low-level message alone is not classified as a network mismatch or an online-session conflict. Formatting failures preserve the original execution receipt. Offline cache fallback, readable logic models and scenario execution are separate work.

### 4.11: mixed-language reads, cache provenance and readable logic

`tia_read_blocks_batch` reads 1–30 named blocks. The defaults are `read_mode: "live"` and `format: "auto"`: SCL/STL/DB use external text when available; LAD/GRAPH use XML. Failed text export explicitly falls back to the XML exported during that live read. Results and errors are reported per block; missing data never implies deletion. Requests are limited to 16 MiB total and 8 MiB per XML object.

Both `tia_read_block` and `tia_read_blocks_batch` accept `read_mode: "cache"`. This mode requires the full `plc_name`, optionally scoped by `group_path` and `unit_name`, and never connects to TIA or refreshes the index. Each result includes `source_kind: "index_cache"`, catalog and acquisition timestamps, object hash, cache state, freshness and `write_baseline: false`. Uncached, ambiguous, missing and corrupt objects are explicit failures. Known stale snapshots remain readable for analysis, labeled `known_stale`. Success covers only the requested names, not the entire project or live freshness. Live reads never silently fall back to cache; engineering writes still require live baselines.

Use `format: "readable"` for an anchored structural view: network number/title/comment, CompileUnit ID, UIds, symbols, normally open/closed and edge contacts, S/R coils, call instances/parameters and port connections. Basic contacts/coils and explicit OR nodes also produce AND/OR/NOT condition trees. Calls, edges, complex addressing and unsupported topology are not guessed into Boolean expressions. GRAPH preserves steps, transitions, attributes and connections, and renders embedded FlgNet conditions with the same model. Unsupported actions/instructions retain `OPAQUE_NODE` markers and local raw XML; gaps and malformed wiring produce explicit warnings.

The default is five networks per block. Continue with `network_offset` and `network_limit` (1–20). `view_summary` reports pagination and coverage, `view` holds the structural tree, and `readable_text` serves text-only agents. This release is an evidence-preserving reader, not a full decompiler, semantic editor, dependency-sorted importer or simulation scenario runner.

### 4.12: restricted LAD / GRAPH edits and two-project comparison

Three MCP entry points are available; this release does not add a Web page:

- `tia_read_logic_patch_plan`: preview a contact edit from a live export. Pass an existing `plan_id` to inspect the saved plan and execution receipt without connecting to TIA.
- `tia_write_logic_patch`: apply the reviewed plan with `confirm: true`.
- `tia_compare_projects`: compare selected program groups in two projects, with block, interface and network differences.

Restricted editing currently targets **V17**. Select a LAD `compile_unit_id`, or a GRAPH `sequence_index` (zero-based) plus `transition` number, and a `contact_uid` from a live read. `edit_json` may replace the direct symbolic operand using `scope` (`GlobalVariable` or `LocalVariable`) and `components`, and/or set `negated` (true means normally closed). It cannot rewrite arbitrary Boolean expressions, shared operands, edge contacts, arrays, indirect addressing, actions or network topology. V21 retains its existing write tools.

Plans in `logs/logic-plans/` bind the project, PLC, full-block fingerprint and exact edit. Applying rechecks the live baseline, backs up before importing, compiles and verifies a full readback. Only `verified_unsaved` indicates all checks passed; it is not a proof of logical safety. There is no automatic project save, PLC download, retry or rollback. Failed or interrupted writes retain their evidence and cannot replay the same plan. Query the plan to inspect results; stale baselines require a new preview.

Comparison requires `project_path_a/plc_name_a` and `project_path_b/plc_name_b`. Optional `group_path_a/group_path_b` select group roots; matching uses relative group paths and exact block names. V21 supports optional `unit_name_a/unit_name_b`. Choose `read_mode: "live"` (default) or explicit `"cache"`. Only successful, complete live catalogs establish absence. Missing cache entries, uncached sources, ambiguous identities and read failures remain unknown, never confirmed deletions.

Pages default to 10 blocks (`limit`: 1–20). Continue with `next_offset` and the returned `catalog_token`; catalog changes reject mixed pages. Each block displays up to 200 structural changes with explicit total/truncation fields; read its source for complete evidence. Hashes, read/acquisition timestamps, cache freshness and parser warnings remain attached. Reads are sequential, not an atomic two-project snapshot. Scope is PLC program blocks only, excluding HMI, hardware, cross-block impact and download reinitialization. Renames appear as remove/add. Unsupported semantics, including GRAPH control flow, retain conservative XML differences rather than an equivalence claim.

The existing V21 catalog interface does not prove container-read completeness, so one-sided absences remain unconfirmed. An empty software-unit selector compares ordinary blocks only; it never mixes other units into the same scope.

### 4.13: simulation diagnostics and condition-driven scenarios

V17 `tia_simulation_download` defaults to read-only preflight. It checks owned local instance identity/state, Softbus interface and target uniqueness, addresses, TIA online state and simulation compilation settings. `blockers` distinguish confirmed facts from unavailable checks. Preflight does not compile, modify settings or switch TIA offline; `compile_status` explicitly says compilation has not been checked. Actual download requires `dry_run:false` and `confirm:true`, compiles first and issues at most one download. No automatic retry, RUN or project save. Phase evidence, native messages, decisions and attempt state are persisted in `simulation-downloads/`; an unfinished `download_intent` requires checking actual instance state. `InterfaceRemoved` is a symptom, not proof of a NIC mismatch. V21 behavior is unchanged.

The existing `tia_simulation_peripheral_start` configuration now accepts `steps` and `watch`. A step waits until all `wait_until` equality conditions match, latches that observation, waits `delay_ms`, evaluates `checks`, then performs Bool `writes`. Checks therefore precede the same step's writes; use a following step for feedback assertions. Condition types are `bool/dint/udint/time/real`. Step timeout includes its wait and delay; total duration is also bounded. Steps execute alongside existing delayed peripheral rules, with conflicting writers rejected.

Status includes step progress, observed tick cost, change events and per-variable final/failure snapshots with timestamps and individual read errors. Exceptions, failed checks and missed timing stop subsequent writes without retry/reset. Default status shows the latest 500 events; use `event_offset`, `event_limit` (1–500) and `next_event_offset` for the complete timeline. Summaries and `.jsonl` evidence live in `simulation-runs/`, survive restarts and include final/failure snapshots. The existing Web peripheral configuration accepts the same JSON. See [SIMULATION.md](SIMULATION.md) for a sample.

20ms is the minimum requested interval, not a real-time guarantee. Use PLC-side latches/counters for one-scan pulses. Reads and failure snapshots are sequential, not atomic. Limits are 200 steps, 128 total tags and 20,000 events, with explicit stopping on overflow. Validation used fake transports and offline regression, not acceptance of the real OP275 process or physical hardware. Comparison summaries also now use compact relative paths, and logic comparison tests can run outside the repository directory.

### 4.14: Web reading, comparison, restricted editing and simulation workflows

The index opens block details with explicit cache/live provenance, network pagination, Boolean condition trees, raw XML and partial-coverage evidence. Kind filters use valid index categories. Diff adds two-project comparison and V17 restricted-contact workflows with before/after wiring, fingerprints, confirmation and durable receipt lookup.

Simulation exposes managed download preflight, blockers, explicit compilation-support opt-in, confirmed download and phase evidence. Peripheral forms edit watches and conditional steps without opening JSON; preview is required before starting the current configuration. Step statistics, paged events and per-tag failure/final snapshots have dedicated views. Task center links to these records, including native logic plans and download receipts.

Fixed same-origin routes under /api/io/workbench/ require a target token and dispatch only allowlisted operations. Mutations use persisted target-bound previews, run asynchronously, retain receipts in logs/workbench/ and never replay automatically. Losing a browser response does not establish that execution did not occur. New operations do not save the engineering project, switch TIA offline or download to physical hardware. Peripheral preview tokens are invalidated by configuration or instance changes.

Navigation is grouped with uniform icons and a tool-count badge. Shutdown is moved to a menu with confirmation; PID is in the project tooltip. I/O has source badges, empty-state actions, hidden empty pagination and less repeated path text. Secondary navigation uses underline tabs, with responsive checks from 480px to desktop widths.

### 4.15: I/O reading layout and day/night themes

The header switches between day and night themes; dark remains the default and the browser remembers your choice. Themes affect the console, not engineering data or HMI design colors.

I/O distinguishes initial, loading, failed, empty-source and empty-filter states. Filters and pagination stay hidden until useful. Connection refresh is labeled separately from data reads. Snapshot origin, acquisition/loading times and the absence of live PLC values are explicit; missing timestamp evidence stays unknown.

Compact labeled Flex filters leave space for search. The table fills remaining viewport height with internal scrolling and sticky headers. Column widths and optional columns are configurable; names and addresses can be copied. Target details retain full raw identities, while repeated adjacent device names are deduplicated only for display. References open beside the table on wide screens and below it on narrow screens. Zero explicit references do not imply unused variables. Business APIs and engineering workflows are unchanged.

Dashboard project discovery now runs in the background. Initial status requests report pending discovery instead of waiting for Openness; failures retain the previous list and report the error rather than implying projects were closed.

## v4.15.2 connection and build maintenance

- Use `scripts/Connect-VmDashboard.ps1 -LocalPort 18981` to keep a host dashboard and a VM tunnel running on separate ports; discovery respects the local port.
- Use `scripts/Start-VmDashboard.ps1 -LocalOnly -OpenBrowser` inside the VM without host pairing or SSH setup. The regular `WebDashboard.cmd` also remains available.
- Keep a separate shared tool directory for each VM. Host keys, tunnel state and logs are isolated per pairing.
- In a source checkout, `powershell -ExecutionPolicy Bypass -File scripts/Clear-BuildCache.ps1` previews eligible reproducible outputs. Add `-Apply` to remove outputs older than seven days; change retention with `-MinAgeDays`. Source, configuration, engineering data, recovery records and release packages are excluded. Linked or active outputs are skipped.
- Successful installer builds discard their staging directory; use `-KeepStaging` for inspection.
- Validation covers offline dual-engine regressions, browser tests, ZIP integrity and install/upgrade/uninstall tests. It does not certify every VM account, network adapter or physical PLC setup.
