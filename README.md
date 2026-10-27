# Navisworks MCP Bridge

> **Fork of [Aitology/Navisworks_MCP](https://github.com/Aitology/Navisworks_MCP)** by Muhammad Osama.
> Changes in this fork: builds for Navisworks 2026 and 2027, installation as an
> Autodesk bundle via `INSTALL.BAT`, the `get_model_tree` tool, and in 2.2 the
> model-file, view and clash-result tools (open / append / remove / save .nwf and
> .nwd in a chosen version, view capture, section box, clash status / assignee /
> comments, auto-grouping, reports).

> Control Autodesk Navisworks from Claude — model files, clash detection and grouping, selection sets, search sets, viewpoints, properties, view capture and sectioning, color overrides — 61 tools through the Model Context Protocol.

<p align="center">
  <img src="docs/architecture.svg" alt="Architecture diagram" width="100%">
</p>

## What this is

A two-piece bridge that lets any MCP-compatible client (Claude Desktop, Claude Code, Cline, Continue, etc.) drive a running instance of Navisworks Manage or Simulate.

| Piece | Lives in | Talks |
|---|---|---|
| **`MCPBridge.dll`** | Inside Navisworks as a .NET addin | Hosts an HTTP server on `localhost:8765` |
| **`server_http.py`** | Standalone Python process spawned by the MCP client | Translates MCP `tools/call` → JSON HTTP `POST` |

The Python side has zero coupling to Navisworks — it just speaks HTTP. The C# side has zero coupling to MCP — it just exposes a tiny REST surface. Either can be replaced independently.

## Why a two-piece design (instead of COM)

The earlier COM-based server had four hard problems:

1. **ProgID year matching** — `Navisworks.Application.2026` vs `2027`, etc.
2. **Same-Windows-user-session** required for `GetActiveObject` to find the running instance.
3. **COM registration** broken by Windows updates and Navisworks repair installs.
4. **Limited API surface** — COM exposes ~20% of what NwApi can do (no clash, no search sets, no overrides).

Running an addin **inside** Navisworks sidesteps every one of these. Full NwApi access. No registration. No version detection. No `pywin32`.

## Prerequisites

Install these once on the machine that runs Navisworks:

| Software | Purpose | Where to get it |
|---|---|---|
| **Windows 10/11 (64-bit)** | OS | — |
| **Navisworks Manage** or **Simulate** 2026 / 2027 | The host application. Freedom is **not** supported (no .NET API). | autodesk.com |
| **Python 3.10+** | To run `server_http.py` | [python.org/downloads](https://www.python.org/downloads/) |
| **MCP Python SDK** | The Python server depends on it | `pip install mcp` |
| **An MCP client** | Claude Desktop, Claude Code, Cline, Continue, etc. | claude.ai/download (or any) |
| **.NET SDK 8+** | Only to build `MCPBridge.dll` from source (it targets .NET Framework 4.8) | [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download) |

## Install

### 1. Install the plugin

Close Navisworks and double-click **`INSTALL.BAT`**. It copies
`release\MCPBridge.bundle\` to `%ProgramData%\Autodesk\ApplicationPlugins\MCPBridge.bundle\`.
Navisworks Manage/Simulate 2026 and 2027 load the bundle on startup and pick
the DLL built for their API (`Contents\2026\` = Nw23, `Contents\2027\` = Nw24,
see `PackageContents.xml`).

```bat
INSTALL.BAT              :: install / update
INSTALL.BAT /uninstall   :: remove the plugin
```

Notes:

- Navisworks does **not** scan `%APPDATA%\Autodesk\Navisworks <Product> <Year>\Plugins\`. Plugins load from `<install dir>\Plugins\<Name>\<Name>.dll` or from bundles in `ApplicationPlugins` — this project uses the bundle.
- The installer removes old loose-folder installs (`Plugins\MCPBridge`, `Plugins\NavisworksMcpAddin` in Program Files or `%APPDATA%`). Next to the bundle they would load the same plugin twice and fight over port 8765. Removing one from Program Files may need *Run as administrator*.
- It refuses to run while Navisworks is open (the DLL is locked) and strips the downloaded-file mark so .NET can load the DLL.

### 2. Install the Python MCP SDK

```powershell
pip install mcp
```

### 3. Verify the addin loads

1. Start Navisworks.
2. Switch to the **AI companion** ribbon tab and click **MCP Bridge**. The bridge does **not** start on its own — this click starts it.
3. A dialog confirms `Status: listening on http://localhost:8765/`. Clicking again shows the status and restarts the listener if it was down.

Or test from PowerShell with no MCP client involved:

```powershell
Invoke-RestMethod http://localhost:8765/health
# ok      : True
# version : 2.2
# port    : 8765
# routes  : 57
```

### 4. Wire into Claude Desktop

`server_http.py` is installed with the bundle. Edit `%APPDATA%\Claude\claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "navisworks": {
      "command": "python",
      "args": ["C:\\ProgramData\\Autodesk\\ApplicationPlugins\\MCPBridge.bundle\\Contents\\server_http.py"]
    }
  }
}
```

Restart Claude Desktop. In Navisworks, click **AI companion → MCP Bridge** so the bridge listens. The Navisworks tools appear in the tool picker — `health_check`, `list_model_files`, `create_clash_test`, etc. Restart it again after installing a new version so it picks up the new tool list.

For other MCP clients (Claude Code, Cline, Continue, etc.) the config schema is the same — point `command` + `args` at `server_http.py`.

## Build from source

One project, one build per Navisworks release. `NavisworksVersion` picks the API to compile against and the output folder:

```powershell
dotnet build NavisworksMcpAddin.csproj -c Release -p:NavisworksVersion=2026   # -> bin\Release\2026\
dotnet build NavisworksMcpAddin.csproj -c Release -p:NavisworksVersion=2027   # -> bin\Release\2027\
```

Pass `-p:NavisworksInstallDir="..."` too if Navisworks is not in `C:\Program Files\Autodesk\Navisworks Manage <Year>`. Where the APIs differ, the 2027 build defines `NAVIS_2027_OR_LATER` (e.g. a clash comment's author is an `Assignee` in 2027, a string in 2026).

Each build folder holds `MCPBridge.dll` plus the ribbon files: `en-US\MCPBridge.xaml` (also copied as `de-DE\`) and `Images\MCPBridge_16/32.png`. To ship a build, copy the whole `bin\Release\<Year>\` folder to `release\MCPBridge.bundle\Contents\<Year>\`, copy `server_http.py` into `release\MCPBridge.bundle\Contents\`, bump the version in `PackageContents.xml`, and run `INSTALL.BAT`.

### Smoke test

`tests/smoke_test.py` exercises the model-file, view and clash-result routes against a running Navisworks (2026 or 2027) over HTTP:

```powershell
python tests\smoke_test.py <work_dir> <a1.ifc> <a2.ifc> <b1.ifc> <b2.ifc>
```

It replaces the open document — start Navisworks with an empty document. It writes only into `<work_dir>`; the IFC files are read (the first one gets its modification time touched). Use copies, not live project files.

## How a tool call flows end-to-end

<p align="center">
  <img src="docs/connection-flow.svg" alt="Connection flow" width="100%">
</p>

1. The MCP client sends a JSON-RPC `tools/call` over **stdio** to `server_http.py`.
2. `server_http.py` looks the tool name up in `ROUTES`, remaps any user-facing argument names (e.g. `test_name` → `name`), and **POSTs** the JSON body to `http://localhost:8765/<route>` from a worker thread, so long calls don't block the stdio connection.
3. The addin receives the request on a worker thread, then marshals the actual API call onto the **Navisworks UI thread** using `SynchronizationContext.Send`. The Navisworks API is single-threaded; this is the only safe way to call it.
4. The handler runs the NwApi call, builds an anonymous result object, and returns it.
5. The addin serializes that to JSON and writes it back as the HTTP response.
6. `server_http.py` wraps it in an MCP `CallToolResult` and writes that to stdout. A result carrying `image_png_base64` (view captures) becomes an MCP image.
7. The MCP client renders the result in the chat.

If the handler throws, the addin returns HTTP 500 with `{error, detail, path}`, and `server_http.py` surfaces that body verbatim with `isError: true` — no information loss.

Most calls answer in milliseconds and time out after 30 s. Opening, appending, refreshing and saving models, running clash tests, auto-grouping and reports may take up to an hour on large projects.

## Available tools (61)

Argument names below are the **MCP tool argument names** (what the LLM sees). The addin's HTTP body uses some shorter aliases — `server_http.py` does the rename.

### Document

| Tool | Purpose |
|---|---|
| `health_check` | Liveness probe. Returns `{ok, version, port, routes}`. |
| `get_document_info` | Title, filename, units, model count, per-model file paths. |
| `get_model_statistics` | Element counts grouped by class display name. |
| `get_model_tree` | Tree of sub-model files (.nwd/.nwc/.ifc/...) in the open document, flat in pre-order with a depth per node. Does not descend into geometry. |

### Model files

| Tool | Required args | Purpose |
|---|---|---|
| `list_model_files` | — | Loaded files with index, whether the file and its source (e.g. the IFC) exist on disk, and modification times. An appended .nwf expands into its files; an appended .nwd is one entry whose `contains` lists the files it was built from. |
| `open_document` | `path` | Open a file, replacing the current document. |
| `new_document` | — | Clear the document (File > New). |
| `append_files` | `paths[]` | Append IFC, NWC, NWD, NWF, RVT, DWG, ... files. |
| `remove_file` | `index` or `name` | Remove a loaded file (index from `list_model_files`). |
| `update_files` | — | Reload every file that changed on disk (Navisworks *Refresh*). |
| `save_document` | — | Save as .nwf (references the sources) or .nwd (all embedded). Without `path`, saves over the current file. `version`: `current` or a year from 2015 (2016–2025 share one format). Saving .nwd keeps the open document as it is; `keep_open=false` makes the .nwd the open document instead (its files collapse into one model). |
| `publish_nwd` | `path` | Publish with options: version, embedded external references, without hidden items, without object properties, title/author/comments. Works only while an .nwd is the open document. |
| `convert_to_nwd` | `paths[]` | Open each file and save it as `<name>.nwd` (next to it or in `output_dir`); reopens the original document afterwards. |

`open_document`, `new_document` and `convert_to_nwd` refuse to throw away unsaved changes unless `discard_changes=true`.

### View

| Tool | Required args | Purpose |
|---|---|---|
| `capture_view` | — | Render the current view and return it as an image (optionally save the PNG to `save_path`). |
| `zoom_to_elements` | — | Zoom to the current selection or to `set_name`. |
| `set_section_box` | — | Section box around the current selection or `set_name` (with `padding`), and zoom to it. |
| `clear_section` | — | Switch sectioning off. |
| `get_section` | — | The view's sectioning as Navisworks ClipPlaneSet JSON. |

Zoom and section box warn when the items span kilometres — usually files in different coordinate systems (one georeferenced, one around 0,0,0).

### Clash detection

| Tool | Required args | Purpose |
|---|---|---|
| `list_clash_tests` | — | All clash tests with status and result count. |
| `create_clash_test` | `name` | Create a Hard / Clearance / Duplicate test. Optionally attach `selectionA` / `selectionB` to populate sides. |
| `set_clash_selections` | `test_name` | Attach selection-sets / search-sets to A and/or B on an existing test (preserves history). |
| `run_clash_test` | — | Run one test by `test_name`, or all tests if omitted. |
| `get_clash_results` | `test_name` | Results with status, distance, group, assignee, centre, both items and their model files. |
| `get_clash_summary` | — | Per test: last run, total, counts by status, groups. |
| `delete_clash_test` | `test_name` | Remove a test by name. |

### Clash results

| Tool | Required args | Purpose |
|---|---|---|
| `set_clash_result_status` | `test_name`, `clash_names[]`, `status` | Status of individual results, also inside groups. |
| `assign_clashes` | `test_name`, `clash_names[]`, `assignee` | Assign results; an empty assignee unassigns. |
| `add_clash_comment` | `test_name`, `clash_names[]`, `comment` | Add a comment (author defaults to the Windows user). |
| `zoom_to_clash` | `test_name`, `clash_name` | Select both items, colour item 1 red and item 2 green, zoom; optional section box and image (`capture`). |
| `auto_group_clashes` | `test_name` | Group all ungrouped results of a top-level test by `model` (file of item A/B), `level` (nearest storey/level node) or `property` (category + property). |
| `export_clash_report` | `path` | Report as .csv (Excel) or .html, optionally only some statuses. |

### Clash groups

| Tool | Required args | Purpose |
|---|---|---|
| `create_clash_group` | `test_name`, `group_name` | Create an empty clash group inside an existing test. Refuses duplicate names within the same test. |
| `list_clash_groups` | `test_name` | All groups in a test, with each group's status and child clash names. |
| `add_clashes_to_group` | `test_name`, `group_name`, `clash_names[]` | Move ungrouped clash results into an existing group. Per-clash interleaved copy-then-remove avoids the Navisworks GUID-uniqueness constraint. |
| `set_clash_group_status` | `test_name`, `group_name`, `status` | Mark a group (and propagate to its child clashes) as `New`, `Active`, `Reviewed`, `Approved`, or `Resolved`. Uses `TestsEditResultStatus` under the hood. |
| `delete_clash_group` | `test_name`, `group_name` | Remove a group (and its child results) from a test. |

### Selection sets / search sets

| Tool | Required args | Purpose |
|---|---|---|
| `list_sets` | — | Every saved selection set and search set. |
| `create_selection_set` | `name` | Save the current viewport selection as a named set. |
| `get_selection_set_items` | `name` | Items in a named set (search sets re-evaluated live). |
| `create_search_set` | `name`, `value` | Property-based saved search. Operators: `equals`, `contains`, `startsWith`, `endsWith`. |
| `execute_search_set` | `name` | Re-run a saved search and return match count. |
| `delete_set` | `name` | Remove a selection or search set. |

### Find / properties / selection

| Tool | Required args | Purpose |
|---|---|---|
| `find_items` | `value` | Single-condition property search. |
| `find_items_by_name` | `pattern` | Substring match on element display names. |
| `select_all` / `deselect_all` / `invert_selection` | — | Bulk selection ops. |
| `select_from_set` | `name` | Replace current selection with a named set's contents. |
| `get_current_selection` | — | First N selected items + total count. |
| `get_element_properties` | — (or `name`) | All property tabs/values for the first selected (or named) item. |

### Color / visibility

| Tool | Required args | Purpose |
|---|---|---|
| `color_elements` | `r, g, b` | Permanent color override (0–255 RGB). |
| `set_transparency` | — | 0.0 opaque … 1.0 fully transparent. |
| `hide_elements` / `unhide_all` / `isolate_elements` | — | Visibility ops on the current selection. |
| `reset_overrides` | — | Clear every color/transparency override and unhide everything. |

### Viewpoints

| Tool | Required args | Purpose |
|---|---|---|
| `list_viewpoints` | — | All saved viewpoints (recursive through groups). |
| `save_viewpoint` | `name` | Save current camera as a named viewpoint. |
| `goto_viewpoint` | `name` | Navigate to a saved viewpoint. |
| `delete_viewpoint` | `name` | Remove a saved viewpoint. |
| `get_current_viewpoint` | — | Current camera position, projection, focal distance. |

The complete route list lives in `MCPBridgePlugin.cs` (`Dispatch`); the tool definitions in `server_http.py` (`TOOLS`, `ROUTES`). Planned tools are in [ROADMAP.md](ROADMAP.md).

## Troubleshooting

**The MCP Bridge button doesn't appear in Navisworks.**
Check that `C:\ProgramData\Autodesk\ApplicationPlugins\MCPBridge.bundle\` exists with `PackageContents.xml` and `Contents\<Year>\MCPBridge.dll` — if not, run `INSTALL.BAT` with Navisworks closed. A copy in `%APPDATA%\Autodesk\Navisworks ... \Plugins\` is never loaded. Then look at `%TEMP%\navisworks_mcp_addin.log`: a `Plugin DLL loaded` line appears on every start that loaded it.

**`/health` returns connection refused.**
The bridge starts only when you click **AI companion → MCP Bridge** in Navisworks — do that once after every Navisworks start. If it still refuses, check `%TEMP%\navisworks_mcp_addin.log` for `Listener failed to start`.

**A call hangs and Navisworks shows a dialog.**
Some Navisworks operations open a modal dialog (missing files when opening, file errors). While it is open the UI thread — and with it every bridge call — waits. Click it away in Navisworks.

**Port 8765 already in use.**
Edit the `Port` constant near the top of `MCPBridgePlugin.cs`, rebuild, reinstall. Then update `ADDIN_BASE` in `server_http.py` to match.

**HTTP 500 on every call.**
Check `%TEMP%\navisworks_mcp_addin.log`. Most often the message is `no active document` — open a model file and retry.

**A zoom or capture shows the model as a dot.**
The loaded files use different coordinate systems (e.g. one georeferenced IFC, one around 0,0,0) — the tool's `warning` says so. Clashes between such files are never found either.

**`add_clashes_to_group` returns "Contains an item whose GUID is already present in the group".**
This indicates the handler tried to add a clash copy while the original was still in the test tree — which can't happen with the shipping code (it removes the original first). If you see it after a code change, check that the order in `ClashGroupAddClashes` is still `CreateCopy → TestsRemoveAt → TestsAddCopy`.

**`set_clash_group_status` returns "Object is Read-Only".**
The handler is trying to assign `Status` directly on a resolved `ClashResultGroup`, which is read-only. Use `dc.TestsData.TestsEditResultStatus(group, status, assignee)` instead — the shipping `ClashGroupSetStatus` already does this.

**Clash groups disappear after restart.**
Navisworks doesn't auto-save clash data. After grouping or statusing clashes via MCP, save the `.nwf` / `.nwd` (`save_document`, or `File → Save`) before closing — otherwise the groups are lost on reload.

**Claude shows the navisworks server but no tools list it.**
The Python server probably crashed on startup. Run it manually to see the error:
```powershell
python C:\ProgramData\Autodesk\ApplicationPlugins\MCPBridge.bundle\Contents\server_http.py --test
```
This runs the self-test (no MCP client needed) and prints the addin's `/health` plus document info to stderr.

**Two MCP clients can't share the bridge at the same time.**
The HTTP listener accepts one request at a time and serializes them onto the UI thread. Two clients work fine — they queue. But if you have, say, both Claude Desktop and Cline configured to talk to the same Navisworks, the plugin will service them round-robin. There is no per-client isolation.

## Logs

| File | What it contains |
|---|---|
| `%TEMP%\navisworks_mcp_addin.log` | Every request the addin received, plus exception detail. Append-only. |
| stderr of `server_http.py` | Python-side errors (rare — most issues end up in the addin log). MCP clients usually surface this in their dev console. |

## Project layout

```
Navisworks_MCP/
├── README.md                    ← you are here
├── ROADMAP.md                   ← planned tools
├── INSTALL.BAT                  ← installs release\MCPBridge.bundle into ApplicationPlugins
├── NavisworksMcpAddin.csproj    ← one project, -p:NavisworksVersion=2026|2027
├── NavisworksMcpAddin.sln
├── MCPBridgePlugin.cs           ← ribbon command, HTTP listener, router, JSON, base handlers
├── en-US/MCPBridge.xaml         ← "AI companion" ribbon tab layout
├── Images/MCPBridge_16/32.png   ← ribbon button icons
├── ModelHandlers.cs             ← partial Handlers — open / append / remove / save / publish / convert
├── ViewHandlers.cs              ← partial Handlers — capture, zoom, section box
├── ClashGroupHandlers.cs        ← partial Handlers — clash groups
├── ClashResultHandlers.cs       ← partial Handlers — clash result status / assignee / comments, zoom, auto-group, report
├── server_http.py               ← Python MCP server (HTTP edition)
├── tests/
│   └── smoke_test.py            ← end-to-end check against a running Navisworks
├── release/
│   └── MCPBridge.bundle/        ← what INSTALL.BAT installs
│       ├── PackageContents.xml  ← Nw23 → Contents\2026, Nw24 → Contents\2027
│       └── Contents/
│           ├── 2026/               ← MCPBridge.dll, en-US/ + de-DE/MCPBridge.xaml, Images/
│           ├── 2027/               ← same, built against the 2027 API
│           └── server_http.py
├── docs/
│   ├── architecture.svg
│   └── connection-flow.svg
└── (legacy, from the upstream project — not used by the bundle install)
    ├── Install-Addin.ps1 / Diagnose-Addin.ps1   ← target %APPDATA%\...\Plugins, which Navisworks doesn't scan
    ├── MCPBridge.addin
    └── Navisworks MCP Release v2.0.0.zip
```

## Contributing

The plugin's design rules:

- **Zero NuGet runtime dependencies.** JSON parsing and serialization are hand-rolled (`Json` class). The reason is that Navisworks loads its own copy of various BCL assemblies in-process; pulling in `System.Text.Json` or Newtonsoft would mean fighting version conflicts at load time. The serializer handles strings, numbers, bools, enums, dictionaries, sequences and anonymous objects — format dates as strings yourself.
- **Ribbon and start.** `MCPBridgePlugin` is a `CommandHandlerPlugin` so it gets its own "AI companion" tab (an `AddInPlugin` lands on the fixed "Tool add-ins 1" tab). Navisworks builds that tab from the XAML without creating the plugin, so the listener starts on the first click of the button — by design. (An `EventWatcherPlugin`, created at startup, could start it automatically.)
- **Every handler is single-threaded.** Add new handlers as static methods on `Handlers` and dispatch them through `RunOnUi` in `MCPBridgePlugin.Dispatch`.
- **Throw on error.** The dispatcher converts exceptions into HTTP 500 + JSON. Don't swallow.
- **Don't trigger Navisworks dialogs.** A modal dialog blocks the UI thread and every later bridge call until someone clicks it. Check preconditions first and throw instead (see `ModelPublish`).
- **Use `SavedItemReference` for clash items.** Direct `ClashTest` / `ClashResult` / `ClashResultGroup` references obtained from `Children` go stale after iteration or after any mutating `Tests…` call. Capture a `SavedItemReference` and re-resolve before each operation. See `ClashSetSelections` and `ClashGroupAddClashes` for the pattern.

### Clash-API quirks worth knowing

These show up the moment you write a new clash handler. All three are settled in the shipping code:

- **GUID uniqueness.** A `ClashResult` has a stable GUID tied to its underlying clash pair, and the entire test tree refuses to contain two items with the same GUID. When "moving" a clash into a group, **remove the original first**, then add the orphan copy — never the other way around. `CreateCopy()` yields an orphan with a fresh GUID that survives the remove.
- **Read-only resolved handles.** Items returned by `ResolveReference` reject direct property setters (`group.Status = …` throws "Object is Read-Only"). Use the dedicated `DocumentClashTests` method for the mutation you want — `TestsEditResultStatus`, `TestsEditResultAssignedTo`, `TestsEditResultComments`, … `ClashResultGroup` implements `IClashResult`, so the same calls work on groups and individual results.
- **Edit methods are typed, not generic.** `TestsEditTestFromCopy(ClashTest, ClashTest)` only edits tests, not groups or results. There's no generic `EditFromCopy` overload. When in doubt, reflection-list the methods on `DocumentClashTests` — that's how the existing code base was discovered.

### Document-API quirks worth knowing

- **`SaveFile` to .nwd swaps the open document** for that .nwd (all files collapse into one model). `ExportToNwd` writes the same file and leaves the document alone — `save_document` uses it by default.
- **Publishing needs an open .nwd.** From an .nwf, `PublishFile` pops "You may only publish NWD files".
- **File-format versions:** `DocumentFileVersion` values are raw format numbers; 2016–2025 are all 448, 2026 is 451. `NwdExportOptions.FileVersion` takes that number as `int`.

### Adding a new MCP tool

1. A static method on `Handlers` — in the partial-class file for its area (`ModelHandlers.cs`, `ViewHandlers.cs`, `ClashResultHandlers.cs`, …) — and a `case` for its route in `MCPBridgePlugin.cs` `Dispatch`. Update the route list in the file header and the `Routes` constant.
2. A `Tool(...)` entry in `server_http.py` `TOOLS` and the matching `ROUTES` line (plus `LONG_ROUTES` if it can take long).
3. Build for 2026 and 2027, copy both DLLs and `server_http.py` into `release\MCPBridge.bundle`, run `INSTALL.BAT`, restart Navisworks and the MCP client.

## License

Aitology
