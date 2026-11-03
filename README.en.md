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
| PLC source | Read and import SCL, DB, UDT, and SimaticML XML, individually or in batches |
| Project index | Browse the project tree, search content, find explicit references, and retrieve task context |
| LAD / GRAPH | Generate LAD, read and edit GRAPH models, and apply batch changes and imports |
| HMI and hardware | Work with classic HMI screens, tags, and alarms; read and modify hardware and network configurations |
| Multiple projects | Route requests by project path, with separate processes isolating projects and version-specific APIs |
| Online monitoring and simulation | Read online variables, record samples, and use PLCSIM tools; availability depends on the channel and version |
| V21 extensions | SimaticSD documents, Software Units, official cross-references, Unified HMI reading and compilation, and AutomationML |

For the current tool list and parameters, use the dashboard's **MCP tool dictionary** or the MCP `tools/list` response. See [Documentation](#documentation) for detailed guides.

## Download and run

You do not need to download the source or compile the application. Choose either package:

| Package | Download | How to run |
| --- | --- | --- |
| Portable ZIP | [Download Portable.zip](https://github.com/qiap/TiaPortalSourceBridge/releases/latest/download/TiaPortalSourceBridge-4.1.1-Portable.zip) | Extract the entire archive, then double-click `WebDashboard.cmd` or `LaunchWeb.vbs` |
| Windows installer | [Download Setup.exe](https://github.com/qiap/TiaPortalSourceBridge/releases/latest/download/TiaPortalSourceBridge-4.1.1-Setup.exe) | Run the installer, then open the application from the desktop or Start menu |

Both packages include the V17/V21 engines, default configuration, and runtime templates. Open your project in TIA Portal first, then select the project and PLC in the dashboard. The target computer must meet the requirements below.

[All releases and release notes](https://github.com/qiap/TiaPortalSourceBridge/releases) · [SHA-256 checksums](https://github.com/qiap/TiaPortalSourceBridge/releases/latest/download/SHA256SUMS.txt)

GitHub's **Code → Download ZIP** and the automatically generated **Source code** release archives contain source code, not a ready-to-run application. For portable use, download **Portable.zip** above. The EXE installer is currently unsigned.

## Requirements

- **Operating system:** Windows 10 / 11, 64-bit.
- **Runtime:** .NET Framework 4.8.
- **Engineering operations:** The matching version of TIA Portal and its Openness component.
- **User permissions:** The current Windows user must belong to the local `Siemens TIA Openness` group. Sign out and back in after changing group membership.
- **Building and testing from source:** Windows PowerShell 5.1, the .NET Framework C# compiler, and Python 3.
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
