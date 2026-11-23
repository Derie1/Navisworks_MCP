# Navisworks MCP Bridge

> **Fork of [Aitology/Navisworks_MCP](https://github.com/Aitology/Navisworks_MCP)** by Muhammad Osama.
> Changes in this fork: builds for Navisworks 2026 and 2027, installation as an
> Autodesk bundle, the `get_model_tree` tool, and in 2.2 the model-file, view and
> clash-result tools (open / append / remove / save .nwf and .nwd in a chosen
> version, view capture, section box, clash status / assignee / comments,
> auto-grouping, reports). 2.2.1 adds `select_files` / `color_files`: whole
> sub-models by file name, without walking the geometry. 2.3.0 replaces the Python
> MCP server with a .NET one and adds an installer (EN/RU) that connects it to
> Claude Desktop, Claude Code and Codex. 2.4.0 adds the model position tools
> (Units and Transform: read, move / rotate / scale, undo).

> Control Autodesk Navisworks from Claude — model files, clash detection and grouping, selection sets, search sets, viewpoints, properties, view capture and sectioning, color overrides, model position — 66 tools through the Model Context Protocol.

<p align="center">
  <img src="docs/architecture.svg" alt="Architecture diagram" width="100%">
</p>

## What this is

A two-piece bridge that lets any MCP-compatible client (Claude Desktop, Claude Code, Codex, etc.) drive a running instance of Navisworks Manage or Simulate.

| Piece | Lives in | Talks |
|---|---|---|
| **`MCPBridge.dll`** | Inside Navisworks as a .NET Framework addin | Hosts an HTTP server on `localhost:8765` |
| **`navis_mcp_server.dll`** | Separate .NET 8 process the MCP client starts as `dotnet.exe navis_mcp_server.dll` | Translates MCP `tools/call` over stdio → JSON HTTP `POST` |

The server has zero coupling to the Navisworks API — it just speaks HTTP. The addin has zero coupling to MCP — it just exposes a tiny REST surface. Either can be replaced independently.

## Why a two-piece design (instead of COM)

The earlier COM-based server had four hard problems:

1. **ProgID year matching** — `Navisworks.Application.2026` vs `2027`, etc.
2. **Same-Windows-user-session** required for `GetActiveObject` to find the running instance.
3. **COM registration** broken by Windows updates and Navisworks repair installs.
4. **Limited API surface** — COM exposes ~20% of what NwApi can do (no clash, no search sets, no overrides).

Running an addin **inside** Navisworks sidesteps every one of these. Full NwApi access. No registration. No version detection.

## Prerequisites

| Software | Purpose | Where to get it |
|---|---|---|
| **Windows 10/11 (64-bit)** | OS | — |
| **Navisworks Manage** or **Simulate** 2026 / 2027 | The host application. Freedom is **not** supported (no .NET API). | autodesk.com |
| **.NET 8 runtime or newer** | Runs the MCP server. Navisworks 2026 / 2027 install it themselves (their Revit file loaders need it); the installer checks. | [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download) |
| **An MCP client** | Claude Desktop, Claude Code, Codex, or any other | claude.ai/download (or any) |
| **.NET SDK 8+** | Only to build from source | [dotnet.microsoft.com/download](https://dotnet.microsoft.com/download) |

No Python is needed any more (versions up to 2.2.x used a Python server).

## Install

### 1. Run the installer

Get the package `NavisworksMCP-<version>` (made by `Build-Package.ps1`, see below), unpack it, close Navisworks and run **`INSTALL.BAT`**. It asks for the language (English / Русский), then **I** to install or update, **U** to uninstall.

```bat
INSTALL.BAT          :: interactive
INSTALL.BAT I EN     :: install / update without questions (RU for Russian messages)
INSTALL.BAT U EN     :: uninstall without questions
```

What it does:

- Copies `MCPBridge.bundle\` to `%ProgramData%\Autodesk\ApplicationPlugins\MCPBridge.bundle\`. Navisworks Manage/Simulate 2026 and 2027 load the bundle on startup and pick the DLL built for their API (`Contents\2026\` = Nw23, `Contents\2027\` = Nw24, see `PackageContents.xml`). The MCP server is `Contents\Server\navis_mcp_server.dll`.
- Removes what earlier versions left behind: the Python server `Contents\server_http.py`, and loose-folder installs (`Plugins\MCPBridge`, `Plugins\NavisworksMcpAddin` in Program Files or `%APPDATA%`) that would load the plugin twice and fight over port 8765. Removing one from Program Files may need *Run as administrator*.
- Connects the server to the AI clients it finds — Claude Desktop, Claude Code, Codex — asking for each. A client that already has a `navisworks` server (e.g. the Python one) is switched over without asking. Other entries in their configs stay untouched; a copy `<config>.bak-navisworks` is kept before the first change.
- Refuses to run while Navisworks is open (the DLL is locked) and strips the downloaded-file mark so .NET can load the DLLs.
- Works while AI clients are open: a running server keeps `navis_mcp_server.dll` loaded, so the installer moves the old DLL to `%TEMP%` (the running server goes on with it) and tells you to restart the client. Such copies are deleted by the next install once free.

Navisworks does **not** scan `%APPDATA%\Autodesk\Navisworks <Product> <Year>\Plugins\`. Plugins load from `<install dir>\Plugins\<Name>\<Name>.dll` or from bundles in `ApplicationPlugins` — this project uses the bundle.

### 2. Start the bridge

1. Restart the AI client so it starts the new MCP server.
2. Start Navisworks, switch to the **AI companion** ribbon tab and click **MCP Bridge**. The bridge does **not** start on its own — this click starts it, after every Navisworks start.
3. A dialog confirms `Status: listening on http://localhost:8765/`. Clicking again shows the status and restarts the listener if it was down.

Or test from PowerShell with no MCP client involved:

```powershell
Invoke-RestMethod http://localhost:8765/health
# ok      : True
# product : Navisworks
# version : 2.4.0
# port    : 8765
# routes  : 62
```

### Connecting a client by hand

The server registers itself; the installer prints these commands too:

```bat
"C:\Program Files\dotnet\dotnet.exe" "C:\ProgramData\Autodesk\ApplicationPlugins\MCPBridge.bundle\Contents\Server\navis_mcp_server.dll" --register claude-desktop
:: also: --register claude-code | codex | all,  --unregister <client>
```

`claude-desktop` writes `mcpServers.navisworks` in `%APPDATA%\Claude\claude_desktop_config.json`; `codex` writes `[mcp_servers.navisworks]` in `%USERPROFILE%\.codex\config.toml`; `claude-code` runs `claude mcp add --scope user navisworks`. For any other MCP client: command `dotnet.exe`, argument the full path of `navis_mcp_server.dll`. The server name stays `navisworks`, so prompts and skills written for earlier versions keep working.

Environment variables of the server: `NAVIS_MCP_PORT` (8765), `NAVIS_MCP_TIMEOUT` (30 s), `NAVIS_MCP_LONG_TIMEOUT` (3600 s for opening, refreshing and saving models, clash runs, reports).

## Build from source

The plugin is one project, one build per Navisworks release. `NavisworksVersion` picks the API to compile against and the output folder:

```powershell
dotnet build NavisworksMcpAddin.csproj -c Release -p:NavisworksVersion=2026   # -> bin\Release\2026\
dotnet build NavisworksMcpAddin.csproj -c Release -p:NavisworksVersion=2027   # -> bin\Release\2027\
dotnet build server\NavisMcpServer\NavisMcpServer.csproj -c Release           # the MCP server
```

Pass `-p:NavisworksInstallDir="..."` too if Navisworks is not in `C:\Program Files\Autodesk\Navisworks Manage <Year>`. Where the APIs differ, the 2027 build defines `NAVIS_2027_OR_LATER` (e.g. a clash comment's author is an `Assignee` in 2027, a string in 2026). Each plugin build folder holds `MCPBridge.dll` plus the ribbon files: `en-US\MCPBridge.xaml` (also copied as `de-DE\`) and `Images\MCPBridge_16/32.png`.

### Install package

```powershell
.\Build-Package.ps1           # build everything and make the package
.\Build-Package.ps1 -Deploy   # also install it on this PC (INSTALL.BAT I), AI clients untouched
```

Writes `dist\NavisworksMCP-<version>\` (`INSTALL.BAT`, `RELEASE.md`, `README.md`, `MCPBridge.bundle\`) and `dist\NavisworksMCP-<version>.7z` (`.zip` without 7-Zip). `dist\` is git-ignored; archives of earlier versions stay there. The version has one source — the `Version` constant in `MCPBridgePlugin.cs`; the bundle manifest (`installer\PackageContents.xml`), the server and the release notes (`installer\RELEASE.md`) get the same number.

### Tests

```powershell
python tests\mcp_server_test.py                                            # MCP server, against a fake bridge
python tests\smoke_test.py <work_dir> <a1.ifc> <a2.ifc> <b1.ifc> <b2.ifc>   # plugin routes, against Navisworks
```

`mcp_server_test.py` needs no Navisworks: it starts a fake bridge and checks initialize, the tool list, routing and argument renames, images, errors, the product check and that a slow call does not block others. `smoke_test.py` exercises the model-file, view and clash-result routes over HTTP against a running Navisworks (2026 or 2027) with the bridge started. It replaces the open document — start Navisworks with an empty document. It writes only into `<work_dir>`; the IFC files are read (the first one gets its modification time touched). Use copies, not live project files. Python is needed for the tests only.

`INSTALL.BAT` can be pointed at a sandbox for tests: `NAVMCP_TARGET`, `NAVMCP_PROGRAMFILES`, `NAVMCP_APPDATA`, `NAVMCP_CLAUDE_CONFIG`, `NAVMCP_CODEX_CONFIG`, `NAVMCP_NO_CLIENTS=1`, `NAVMCP_NO_CLAUDE_CODE=1`, `NAVMCP_NO_RUNNING_CHECK=1` (see its header).

## How a tool call flows end-to-end

<p align="center">
  <img src="docs/connection-flow.svg" alt="Connection flow" width="100%">
</p>

1. The MCP client sends a JSON-RPC `tools/call` over **stdio** to `navis_mcp_server.dll`.
2. The server looks the tool up in the `bridge` table of `tools.json` (its plugin route, argument renames such as `test_name` → `name`, whether it may take long) and **POSTs** the JSON body to `http://localhost:8765/<route>`. Requests run concurrently, so a long call doesn't block others.
3. The addin receives the request on a worker thread, then marshals the actual API call onto the **Navisworks UI thread** using `SynchronizationContext.Send`. The Navisworks API is single-threaded; this is the only safe way to call it.
4. The handler runs the NwApi call, builds an anonymous result object, and returns it.
5. The addin serializes that to JSON and writes it back as the HTTP response.
6. The server wraps it in an MCP `CallToolResult` and writes that to stdout. A result carrying `image_png_base64` (view captures) becomes an MCP image.
7. The MCP client renders the result in the chat.

If the handler throws, the addin returns HTTP 500 with `{error, detail, path}`, and the server returns the detail with `isError: true` — no information loss. Before the first call the server checks `/health` for `product: "Navisworks"`, so another program on the port is reported as such.

Most calls answer in milliseconds and time out after 30 s. Opening, appending, refreshing and saving models, running clash tests, auto-grouping and reports may take up to an hour on large projects.

## Available tools (66)

Argument names below are the **MCP tool argument names** (what the LLM sees). The addin's HTTP body uses some shorter aliases — the server does the rename.

### Document

| Tool | Purpose |
|---|---|
| `health_check` | Liveness probe. Returns `{ok, version, port, routes}`. |
| `get_document_info` | Title, filename, units, model count, per-model file paths. |
| `get_model_statistics` | Element counts grouped by class display name. |
| `get_model_tree` | Tree of sub-model files (.nwd/.nwc/.ifc/...) in the open document, flat in pre-order with a depth per node. Does not descend into geometry. |
| `select_files` | Select whole sub-models by file name (`names[]`, as in `get_model_tree`, extension optional; `contains=true` for substrings, `add_to_selection` to extend). Walks only the file tree, so it is fast on large federated models. Returns the tree path of every match and the names not found. |
| `color_files` | Color whole sub-models by file name (`names[]` + `r`,`g`,`b`), same matching as `select_files`; leaves the selection alone. |

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

### Model position

Navisworks' **Units and Transform** dialog for the models listed by `list_model_files`.

| Tool | Required args | Purpose |
|---|---|---|
| `get_model_transform` | — | Units, translation, rotation (degrees + axis), scale, reflection and bounding box of one model (`index` / `name`) or all. A box far from the others usually means a shifted insertion point. |
| `set_model_transform` | `index` or `name` | Change translation, rotation (`rotation_deg` about `rotation_axis`, default Z), scale, units or reflection — only what is passed. `mode`: `set` (like the dialog) or `add` (on top of the current position). Returns the position before and after. |
| `reset_model_transform` | `index` or `name` | Undo the bridge's changes: back to the units and transform the model had when the bridge first read it in the open document. |

The transform already holds what the file brings: the IFC loader puts the file's **georeference** there (e.g. a translation of 685198, 5379841, 434 m) and a file in mm shows units `Millimeters`. That is why there is no "reset to zero": it would move such a model to the origin. Translations are in document units. An appended .nwf expands into its files, each movable; an appended .nwd is one model — to move a file inside it, open the .nwf it was built from. The position is stored in the open .nwf / .nwd: save it and rebuild the files that append it. Changing units rescales the model (mm read as m = 1000 times too big).

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
| `find_items_by_name` | `pattern` | Substring match on element display names. Walks every element — slow on large federated models; for whole sub-models use `select_files` / `color_files`. |
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

The complete route list lives in `MCPBridgePlugin.cs` (`Dispatch`); the tool definitions in `server\NavisMcpServer\tools.json` (`tools` for the client, `bridge` for routes and renames). Planned tools are in [ROADMAP.md](ROADMAP.md).

## Troubleshooting

**The MCP Bridge button doesn't appear in Navisworks.**
Check that `C:\ProgramData\Autodesk\ApplicationPlugins\MCPBridge.bundle\` exists with `PackageContents.xml` and `Contents\<Year>\MCPBridge.dll` — if not, run `INSTALL.BAT` with Navisworks closed. A copy in `%APPDATA%\Autodesk\Navisworks ... \Plugins\` is never loaded. Then look at `%TEMP%\navisworks_mcp_addin.log`: a `Plugin DLL loaded` line appears on every start that loaded it.

**`/health` returns connection refused.**
The bridge starts only when you click **AI companion → MCP Bridge** in Navisworks — do that once after every Navisworks start. If it still refuses, check `%TEMP%\navisworks_mcp_addin.log` for `Listener failed to start`.

**A call hangs and Navisworks shows a dialog.**
Some Navisworks operations open a modal dialog (missing files when opening, file errors). While it is open the UI thread — and with it every bridge call — waits. Click it away in Navisworks.

**Port 8765 already in use.**
Edit the `Port` constant near the top of `MCPBridgePlugin.cs`, rebuild, reinstall. Then set the environment variable `NAVIS_MCP_PORT` for the MCP server to match.

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
The server probably fails to start. Check that it runs and which version it is:
```powershell
dotnet "C:\ProgramData\Autodesk\ApplicationPlugins\MCPBridge.bundle\Contents\Server\navis_mcp_server.dll" --version
```
An error about the framework means .NET 8 or newer is missing. If the client config still runs `python ... server_http.py`, run `INSTALL.BAT` again — it switches the client to the .NET server.

**Two MCP clients can't share the bridge at the same time.**
The HTTP listener accepts one request at a time and serializes them onto the UI thread. Two clients work fine — they queue. But if you have, say, both Claude Desktop and Cline configured to talk to the same Navisworks, the plugin will service them round-robin. There is no per-client isolation.

## Logs

| File | What it contains |
|---|---|
| `%TEMP%\navisworks_mcp_addin.log` | Every request the addin received, plus exception detail. Append-only. |
| stderr of `navis_mcp_server.dll` | Server-side errors (rare — most issues end up in the addin log). MCP clients usually surface this in their dev console or MCP log. |

## Project layout

```
Navisworks_MCP/
├── README.md                    ← you are here
├── ROADMAP.md                   ← development stages, tool catalogue, known issues
├── Build-Package.ps1            ← builds everything into dist\NavisworksMCP-<version>\ + archive
├── NavisworksMcpAddin.csproj    ← the plugin, -p:NavisworksVersion=2026|2027
├── NavisworksMcpAddin.sln
├── MCPBridgePlugin.cs           ← ribbon command, HTTP listener, router, JSON, base handlers
├── en-US/MCPBridge.xaml         ← "AI companion" ribbon tab layout
├── Images/MCPBridge_16/32.png   ← ribbon button icons
├── ModelHandlers.cs             ← partial Handlers — open / append / remove / save / publish / convert
├── TransformHandlers.cs         ← partial Handlers — model position (Units and Transform)
├── ViewHandlers.cs              ← partial Handlers — capture, zoom, section box
├── ClashGroupHandlers.cs        ← partial Handlers — clash groups
├── ClashResultHandlers.cs       ← partial Handlers — clash result status / assignee / comments, zoom, auto-group, report
├── server/
│   └── NavisMcpServer/          ← the MCP server, net8.0 → navis_mcp_server.dll
│       ├── tools.json           ← tool definitions + their plugin routes (single source)
│       ├── McpServer.cs         ← stdio JSON-RPC: initialize, tools/list, tools/call
│       ├── Bridge.cs            ← HTTP client of the plugin
│       └── Registration.cs      ← --register / --unregister in AI clients
├── installer/
│   ├── INSTALL.BAT              ← installer EN/RU, copied into the package
│   ├── PackageContents.xml      ← bundle manifest template: Nw23 → Contents\2026, Nw24 → Contents\2027
│   └── RELEASE.md               ← release notes RU + EN, copied into the package
├── tests/
│   ├── mcp_server_test.py       ← MCP server against a fake bridge
│   └── smoke_test.py            ← plugin routes against a running Navisworks
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
- **Model transforms carry the file's own placement.** `Model.Transform` of a freshly loaded IFC already holds its georeference translation (and, for files in mm, `Units.Millimeters`). `Transform3D.Factor()` splits it into translation / rotation / scale (no public constructor for `Transform3DComponents` — change the one `Factor()` returns and `Combine()` it); write back with `DocumentModels.SetModelUnitsAndTransform`.
- **File-format versions:** `DocumentFileVersion` values are raw format numbers; 2016–2025 are all 448, 2026 is 451. `NwdExportOptions.FileVersion` takes that number as `int`.

### Adding a new MCP tool

1. A static method on `Handlers` — in the partial-class file for its area (`ModelHandlers.cs`, `ViewHandlers.cs`, `ClashResultHandlers.cs`, …) — and a `case` for its route in `MCPBridgePlugin.cs` `Dispatch`. Update the route list in the file header and the `Routes` constant.
2. An entry in `server\NavisMcpServer\tools.json`: under `tools` its name, description and input schema (what the AI sees), under `bridge` its route, any argument renames and `"long": true` if it can take long.
3. Run `.\Build-Package.ps1 -Deploy` (Navisworks closed), restart Navisworks and the MCP client. `python tests\mcp_server_test.py` checks that every tool has a route.

## License

Aitology
