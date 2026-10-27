"""
Navisworks MCP Server (HTTP bridge edition)
============================================
Replacement for the COM-based server.py that talks to the in-process
Navisworks addin over plain HTTP instead of fighting with pywin32.

Architecture:
    Claude Desktop  --(stdio)-->  this script  --(HTTP)-->  Navisworks addin

Prerequisites:
    1. Build & install the .NET addin (see addin/README.md).
    2. Open Navisworks Manage 2026/2027 — the addin auto-starts on
       http://localhost:8765
    3. Configure Claude Desktop to launch this script (see README).

Why this is better than the COM version:
    - No ProgID, no /regserver, no version detection
    - No same-Windows-user requirement
    - Survives Windows updates and Navisworks repairs
    - 10x faster — addin runs INSIDE Navisworks, full NwApi access
"""

import asyncio
import json
import logging
import sys
import traceback
import urllib.error
import urllib.request
from typing import Any

# ── Logging to stderr ONLY — stdout is the JSON-RPC pipe ─────────────
logging.basicConfig(stream=sys.stderr, level=logging.WARNING,
                    format="%(asctime)s [%(levelname)s] %(message)s")
logging.getLogger("mcp").setLevel(logging.ERROR)
logging.getLogger("asyncio").setLevel(logging.ERROR)

from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp.types import Tool, TextContent, ImageContent, CallToolResult, ListToolsResult

ADDIN_BASE = "http://localhost:8765"
ADDIN_TIMEOUT = 30  # seconds
# Opening, saving or refreshing a federated model, running clash tests and
# writing reports can take many minutes on large projects.
LONG_TIMEOUT = 3600  # seconds
LONG_ROUTES = {
    "/model/open", "/model/append", "/model/update", "/model/save",
    "/model/publish", "/model/convert", "/clash/run", "/clash/auto-group",
    "/clash/report",
}


# ─────────────────────────────────────────────────────────────────────
# HTTP bridge helpers
# ─────────────────────────────────────────────────────────────────────

def _post(path: str, payload: dict | None = None) -> dict:
    """Send a JSON POST to the addin and return the parsed response."""
    body = json.dumps(payload or {}).encode("utf-8")
    req = urllib.request.Request(
        f"{ADDIN_BASE}{path}",
        data=body,
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    try:
        timeout = LONG_TIMEOUT if path in LONG_ROUTES else ADDIN_TIMEOUT
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        # The plugin returns 500 with a JSON body for handler errors.
        # Surface that body verbatim instead of swallowing it as "addin_unreachable".
        try:
            return json.loads(e.read().decode("utf-8"))
        except Exception:
            return {"error": f"HTTP {e.code}", "detail": str(e)}
    except urllib.error.URLError as e:
        return {
            "error": "addin_unreachable",
            "detail": str(e.reason if hasattr(e, "reason") else e),
            "hint": (
                f"Cannot reach the Navisworks addin at {ADDIN_BASE}. "
                "Is Navisworks running with the MCP Bridge plugin installed? "
                "In Navisworks, click the 'AI companion' ribbon tab → "
                "'MCP Bridge' to start the bridge."
            ),
        }
    except Exception as e:
        return {"error": type(e).__name__, "detail": str(e)}


def _health() -> dict:
    """Quick liveness check for the addin."""
    return _post("/health")


# ─────────────────────────────────────────────────────────────────────
# MCP server
# ─────────────────────────────────────────────────────────────────────

server = Server("navisworks-mcp-bridge")

TOOLS = [
    Tool(name="health_check",
         description="Verify the Navisworks addin is running and reachable on localhost:8765.",
         inputSchema={"type": "object", "properties": {}, "required": []}),

    # ── Document ───────────────────────────────────────────────────
    Tool(name="get_document_info",
         description="Return metadata for the currently open Navisworks document.",
         inputSchema={"type": "object", "properties": {}, "required": []}),
    Tool(name="get_model_statistics",
         description="Element counts grouped by class.",
         inputSchema={"type": "object", "properties": {}, "required": []}),
    Tool(name="get_model_tree",
         description="Tree of sub-model files (.nwd/.nwc/.ifc/...) in the open document, "
                     "flat in pre-order with a depth per node. Does not descend into geometry.",
         inputSchema={"type": "object",
                      "properties": {"max_depth": {"type": "number", "default": 50}},
                      "required": []}),

    # ── Clash detection ────────────────────────────────────────────
    Tool(name="list_clash_tests",
         description="List all clash tests in the current document with their status and result counts.",
         inputSchema={"type": "object", "properties": {}, "required": []}),
    Tool(name="create_clash_test",
         description=("Create a Clash Detective test. selectionA / selectionB optionally name "
                      "selection-sets or search-sets to populate the test with on creation."),
         inputSchema={"type": "object", "properties": {
             "name":        {"type": "string"},
             "clash_type":  {"type": "string", "enum": ["Hard", "Clearance", "Duplicate"], "default": "Hard"},
             "tolerance":   {"type": "number", "default": 0.001},
             "selectionA":  {"type": "string", "description": "Name of a selection-set or search-set for side A."},
             "selectionB":  {"type": "string", "description": "Name of a selection-set or search-set for side B."},
         }, "required": ["name"]}),
    Tool(name="set_clash_selections",
         description="Attach selection-sets / search-sets to side A and/or side B of an existing clash test.",
         inputSchema={"type": "object", "properties": {
             "test_name":   {"type": "string"},
             "selectionA":  {"type": "string"},
             "selectionB":  {"type": "string"},
         }, "required": ["test_name"]}),
    Tool(name="run_clash_test",
         description="Run a clash test by name, or all tests if name omitted.",
         inputSchema={"type": "object", "properties": {
             "test_name": {"type": "string"}
         }, "required": []}),
    Tool(name="get_clash_results",
         description="Return clash results for a named test.",
         inputSchema={"type": "object", "properties": {
             "test_name":   {"type": "string"},
             "max_results": {"type": "integer", "default": 100},
         }, "required": ["test_name"]}),
    Tool(name="delete_clash_test",
         description="Delete a clash test by name.",
         inputSchema={"type": "object", "properties": {
             "test_name": {"type": "string"}
         }, "required": ["test_name"]}),
    Tool(name="create_clash_group",
     description="Create an (empty) clash group inside an existing clash test.",
     inputSchema={"type": "object", "properties": {
         "test_name":  {"type": "string"},
         "group_name": {"type": "string"},
     }, "required": ["test_name", "group_name"]}),
Tool(name="list_clash_groups",
     description="List clash groups inside a clash test, with the names of their child clash results.",
     inputSchema={"type": "object", "properties": {
         "test_name": {"type": "string"},
     }, "required": ["test_name"]}),
Tool(name="delete_clash_group",
     description="Delete a clash group (and its child results) from a clash test.",
     inputSchema={"type": "object", "properties": {
         "test_name":  {"type": "string"},
         "group_name": {"type": "string"},
     }, "required": ["test_name", "group_name"]}),
Tool(name="add_clashes_to_group",
         description=("Move existing (ungrouped) clash results from a clash test "
                      "into one of its clash groups. Already-grouped clashes are "
                      "left alone; pass them by their display name (e.g. 'Clash1')."),
         inputSchema={"type": "object", "properties": {
             "test_name":   {"type": "string"},
             "group_name":  {"type": "string"},
             "clash_names": {
                 "type": "array",
                 "items": {"type": "string"},
                 "description": "Display names of clashes to move into the group."
             },
         }, "required": ["test_name", "group_name", "clash_names"]}),
Tool(name="set_clash_group_status",
         description=("Set the review status of a clash group "
                      "(New | Active | Reviewed | Approved | Resolved)."),
         inputSchema={"type": "object", "properties": {
             "test_name":  {"type": "string"},
             "group_name": {"type": "string"},
             "status":     {"type": "string",
                            "enum": ["New", "Active", "Reviewed", "Approved", "Resolved"]},
         }, "required": ["test_name", "group_name", "status"]}),

 
    # ── Selection / Search sets ────────────────────────────────────
    Tool(name="list_sets",
         description="List all selection sets and search sets.",
         inputSchema={"type": "object", "properties": {}, "required": []}),
    Tool(name="create_selection_set",
         description="Create a selection set from the current viewport selection.",
         inputSchema={"type": "object", "properties": {
             "name": {"type": "string"},
         }, "required": ["name"]}),
    Tool(name="get_selection_set_items",
         description="List the elements stored in a named selection set.",
         inputSchema={"type": "object", "properties": {
             "name": {"type": "string"}
         }, "required": ["name"]}),
    Tool(name="create_search_set",
         description=("Create a saved search with a single property condition. "
                      "Operators: equals, contains, startsWith, endsWith."),
         inputSchema={"type": "object", "properties": {
             "name":     {"type": "string"},
             "category": {"type": "string", "default": "Item"},
             "property": {"type": "string", "default": "Name"},
             "operator": {"type": "string", "enum": ["equals", "contains", "startsWith", "endsWith"], "default": "contains"},
             "value":    {"type": "string"},
         }, "required": ["name", "value"]}),
    Tool(name="execute_search_set",
         description="Run a saved search and return the count of matching elements.",
         inputSchema={"type": "object", "properties": {
             "name": {"type": "string"},
         }, "required": ["name"]}),
    Tool(name="delete_set",
         description="Delete a selection set or search set by name.",
         inputSchema={"type": "object", "properties": {
             "name": {"type": "string"}
         }, "required": ["name"]}),

    # ── Find / Select ──────────────────────────────────────────────
    Tool(name="find_items",
         description="Single-condition property search returning matching elements.",
         inputSchema={"type": "object", "properties": {
             "category":       {"type": "string", "default": "Item"},
             "property":       {"type": "string", "default": "Name"},
             "operator":       {"type": "string", "enum": ["equals", "contains", "startsWith", "endsWith"], "default": "contains"},
             "value":          {"type": "string"},
             "limit":          {"type": "integer", "default": 50},
             "select_results": {"type": "boolean", "default": False},
         }, "required": ["value"]}),
    Tool(name="find_items_by_name",
         description="Quick search by element name (substring match).",
         inputSchema={"type": "object", "properties": {
             "pattern":        {"type": "string"},
             "limit":          {"type": "integer", "default": 50},
             "select_results": {"type": "boolean", "default": True},
         }, "required": ["pattern"]}),
    Tool(name="select_all",
         description="Select every element in the model.",
         inputSchema={"type": "object", "properties": {}, "required": []}),
    Tool(name="deselect_all",
         description="Clear the current selection.",
         inputSchema={"type": "object", "properties": {}, "required": []}),
    Tool(name="invert_selection",
         description="Invert the current selection.",
         inputSchema={"type": "object", "properties": {}, "required": []}),
    Tool(name="select_from_set",
         description="Replace the current selection with the contents of a named set.",
         inputSchema={"type": "object", "properties": {
             "name": {"type": "string"}
         }, "required": ["name"]}),
    Tool(name="get_current_selection",
         description="Return the elements currently selected in the viewport.",
         inputSchema={"type": "object", "properties": {}, "required": []}),
    Tool(name="get_element_properties",
         description="Return all property tabs and values for the first selected element (or by name).",
         inputSchema={"type": "object", "properties": {
             "name": {"type": "string"}
         }, "required": []}),

    # ── Color / Visibility ─────────────────────────────────────────
    Tool(name="color_elements",
         description="Override the color (RGB 0-255) of currently selected elements.",
         inputSchema={"type": "object", "properties": {
             "r": {"type": "integer", "minimum": 0, "maximum": 255},
             "g": {"type": "integer", "minimum": 0, "maximum": 255},
             "b": {"type": "integer", "minimum": 0, "maximum": 255},
         }, "required": ["r", "g", "b"]}),
    Tool(name="set_transparency",
         description="Override transparency (0.0 opaque .. 1.0 fully transparent) of selected elements.",
         inputSchema={"type": "object", "properties": {
             "transparency": {"type": "number", "minimum": 0, "maximum": 1, "default": 0.5},
         }, "required": []}),
    Tool(name="reset_overrides",
         description="Remove every color and transparency override and unhide everything.",
         inputSchema={"type": "object", "properties": {}, "required": []}),
    Tool(name="hide_elements",
         description="Hide the currently selected elements.",
         inputSchema={"type": "object", "properties": {}, "required": []}),
    Tool(name="unhide_all",
         description="Restore visibility of every hidden element.",
         inputSchema={"type": "object", "properties": {}, "required": []}),
    Tool(name="isolate_elements",
         description="Hide everything except the current selection.",
         inputSchema={"type": "object", "properties": {}, "required": []}),

    # ── Viewpoints ─────────────────────────────────────────────────
    Tool(name="list_viewpoints",
         description="List all saved viewpoints.",
         inputSchema={"type": "object", "properties": {}, "required": []}),
    Tool(name="save_viewpoint",
         description="Save the current camera position as a named viewpoint.",
         inputSchema={"type": "object", "properties": {
             "name": {"type": "string"}
         }, "required": ["name"]}),
    Tool(name="goto_viewpoint",
         description="Navigate to a saved viewpoint by name.",
         inputSchema={"type": "object", "properties": {
             "name": {"type": "string"}
         }, "required": ["name"]}),
    Tool(name="delete_viewpoint",
         description="Delete a saved viewpoint by name.",
         inputSchema={"type": "object", "properties": {
             "name": {"type": "string"}
         }, "required": ["name"]}),
    Tool(name="get_current_viewpoint",
         description="Return the current camera position and projection.",
         inputSchema={"type": "object", "properties": {}, "required": []}),

    # ── Model files ────────────────────────────────────────────────
    Tool(name="list_model_files",
         description=("List the files loaded in the current document with their index, whether "
                      "the file and its source (e.g. the IFC) exist on disk, and modification "
                      "times. An appended .nwf expands into its files; an appended .nwd is one "
                      "entry whose 'contains' lists the files it was built from. Use the index "
                      "with remove_file."),
         inputSchema={"type": "object", "properties": {}, "required": []}),
    Tool(name="open_document",
         description=("Open an .nwf/.nwd/.nwc or any supported source file, replacing the current "
                      "document. Refuses if the current document has unsaved changes unless "
                      "discard_changes is true. Navisworks may show a dialog (e.g. missing "
                      "files) that someone has to close."),
         inputSchema={"type": "object", "properties": {
             "path":            {"type": "string", "description": "Full path of the file to open."},
             "discard_changes": {"type": "boolean", "default": False},
         }, "required": ["path"]}),
    Tool(name="new_document",
         description="Clear the current document (File > New).",
         inputSchema={"type": "object", "properties": {
             "discard_changes": {"type": "boolean", "default": False},
         }, "required": []}),
    Tool(name="append_files",
         description="Append one or more files (IFC, NWC, NWD, NWF, RVT, DWG, ...) to the current document.",
         inputSchema={"type": "object", "properties": {
             "paths": {"type": "array", "items": {"type": "string"}, "description": "Full file paths."},
         }, "required": ["paths"]}),
    Tool(name="remove_file",
         description=("Remove a loaded file from the current document, by index or by name "
                      "(as shown by list_model_files). Prefer index when names repeat."),
         inputSchema={"type": "object", "properties": {
             "index": {"type": "integer"},
             "name":  {"type": "string", "description": "Model name or file name, e.g. 'ARC.ifc'."},
         }, "required": []}),
    Tool(name="update_files",
         description=("Reload every loaded file that changed on disk since it was loaded "
                      "(the 'Refresh' command). Returns the file list afterwards."),
         inputSchema={"type": "object", "properties": {}, "required": []}),
    Tool(name="save_document",
         description=("Save the current document as .nwf (references the source files) or "
                      ".nwd (everything embedded). Without path, saves over the current file. "
                      "version picks the file format: 'current' (default) or a year from 2015 "
                      "on, e.g. '2026' so Navisworks 2026 can open a file saved in 2027. "
                      "2016-2025 share one format. Saving .nwd keeps the open document as it "
                      "is (keep_open, default); keep_open=false makes the .nwd the open "
                      "document, its files collapsing into one model - needed before publish_nwd."),
         inputSchema={"type": "object", "properties": {
             "path":      {"type": "string", "description": "Target .nwf or .nwd path."},
             "version":   {"type": "string", "default": "current"},
             "keep_open": {"type": "boolean", "default": True},
         }, "required": []}),
    Tool(name="publish_nwd",
         description=("Publish the open .nwd to a new .nwd with export options: format version, "
                      "embedded external references, without hidden items, or without object "
                      "properties (for handing a model to third parties). Works only while an "
                      ".nwd is the open document (Navisworks restriction)."),
         inputSchema={"type": "object", "properties": {
             "path":             {"type": "string"},
             "version":          {"type": "string", "default": "current"},
             "embed_xrefs":      {"type": "boolean", "default": False},
             "exclude_hidden":   {"type": "boolean", "default": False},
             "strip_properties": {"type": "boolean", "default": False},
             "title":            {"type": "string"},
             "author":           {"type": "string"},
             "comments":         {"type": "string"},
             "allow_resave":     {"type": "boolean", "default": True},
         }, "required": ["path"]}),
    Tool(name="convert_to_nwd",
         description=("Convert files to .nwd one by one: open each file and save it as "
                      "<name>.nwd next to it or in output_dir. Replaces the current document "
                      "while running and reopens it at the end (restore_document)."),
         inputSchema={"type": "object", "properties": {
             "paths":            {"type": "array", "items": {"type": "string"}},
             "output_dir":       {"type": "string"},
             "version":          {"type": "string", "default": "current"},
             "overwrite":        {"type": "boolean", "default": False},
             "restore_document": {"type": "boolean", "default": True},
             "discard_changes":  {"type": "boolean", "default": False},
         }, "required": ["paths"]}),

    # ── View ───────────────────────────────────────────────────────
    Tool(name="capture_view",
         description=("Render the current Navisworks view and return it as an image, so you can "
                      "see the model. Optionally also save the PNG to save_path."),
         inputSchema={"type": "object", "properties": {
             "width":     {"type": "integer", "default": 1280},
             "height":    {"type": "integer", "description": "Default keeps the view's aspect ratio."},
             "style":     {"type": "string", "enum": ["overlay", "scene", "raytrace"], "default": "overlay"},
             "save_path": {"type": "string"},
         }, "required": []}),
    Tool(name="zoom_to_elements",
         description="Zoom the camera to the current selection, or to a selection/search set.",
         inputSchema={"type": "object", "properties": {
             "set_name": {"type": "string"},
             "padding":  {"type": "number", "default": 0, "description": "Extra margin in model units."},
         }, "required": []}),
    Tool(name="set_section_box",
         description="Turn on a section box around the current selection or a set, and zoom to it.",
         inputSchema={"type": "object", "properties": {
             "set_name": {"type": "string"},
             "padding":  {"type": "number", "default": 0, "description": "Extra margin in model units."},
             "zoom":     {"type": "boolean", "default": True},
         }, "required": []}),
    Tool(name="clear_section",
         description="Switch sectioning off in the current view.",
         inputSchema={"type": "object", "properties": {}, "required": []}),
    Tool(name="get_section",
         description="Return the current view's sectioning as Navisworks ClipPlaneSet JSON.",
         inputSchema={"type": "object", "properties": {}, "required": []}),

    # ── Clash results ──────────────────────────────────────────────
    Tool(name="get_clash_summary",
         description="Per clash test: last run, total results, counts by status, number of groups.",
         inputSchema={"type": "object", "properties": {
             "test_name": {"type": "string", "description": "Omit for all tests."},
         }, "required": []}),
    Tool(name="set_clash_result_status",
         description="Set the status of individual clash results (also inside groups).",
         inputSchema={"type": "object", "properties": {
             "test_name":   {"type": "string"},
             "clash_names": {"type": "array", "items": {"type": "string"}},
             "status":      {"type": "string", "enum": ["New", "Active", "Reviewed", "Approved", "Resolved"]},
         }, "required": ["test_name", "clash_names", "status"]}),
    Tool(name="assign_clashes",
         description="Assign clash results to a person or company. Empty assignee unassigns.",
         inputSchema={"type": "object", "properties": {
             "test_name":   {"type": "string"},
             "clash_names": {"type": "array", "items": {"type": "string"}},
             "assignee":    {"type": "string"},
         }, "required": ["test_name", "clash_names", "assignee"]}),
    Tool(name="add_clash_comment",
         description="Add a comment to clash results.",
         inputSchema={"type": "object", "properties": {
             "test_name":      {"type": "string"},
             "clash_names":    {"type": "array", "items": {"type": "string"}},
             "comment":        {"type": "string"},
             "comment_status": {"type": "string", "enum": ["New", "Active", "Approved", "Resolved"], "default": "New"},
             "author":         {"type": "string", "description": "Defaults to the Windows user."},
         }, "required": ["test_name", "clash_names", "comment"]}),
    Tool(name="zoom_to_clash",
         description=("Show one clash like Clash Detective does: select both items, colour item 1 "
                      "red and item 2 green, zoom to it. section=true adds a section box; "
                      "capture=true also returns an image of the view."),
         inputSchema={"type": "object", "properties": {
             "test_name":  {"type": "string"},
             "clash_name": {"type": "string"},
             "highlight":  {"type": "boolean", "default": True},
             "section":    {"type": "boolean", "default": False},
             "padding":    {"type": "number", "description": "Margin in model units; default = size of the clash box."},
             "capture":    {"type": "boolean", "default": False},
             "width":      {"type": "integer", "default": 1280},
         }, "required": ["test_name", "clash_name"]}),
    Tool(name="auto_group_clashes",
         description=("Group all ungrouped results of a top-level clash test in one go. "
                      "group_by: 'model' (file of item A/B, default both: 'A.ifc × B.ifc'), "
                      "'level' (nearest storey/level node, else Element/Level property), or "
                      "'property' (category + property display names, read from the item or "
                      "its ancestors). side: a, b or both."),
         inputSchema={"type": "object", "properties": {
             "test_name": {"type": "string"},
             "group_by":  {"type": "string", "enum": ["model", "level", "property"], "default": "model"},
             "side":      {"type": "string", "enum": ["a", "b", "both"]},
             "category":  {"type": "string"},
             "property":  {"type": "string"},
             "prefix":    {"type": "string", "description": "Text put before every group name."},
         }, "required": ["test_name"]}),
    Tool(name="export_clash_report",
         description=("Write a clash report as .csv (opens in Excel) or .html: test, group, "
                      "clash, status, distance, assignee, items and their model files, "
                      "centre point, comments."),
         inputSchema={"type": "object", "properties": {
             "path":      {"type": "string", "description": "Target .csv or .html path."},
             "test_name": {"type": "string", "description": "Omit for all tests."},
             "statuses":  {"type": "array", "items": {"type": "string"},
                           "description": "Only these statuses, e.g. ['New', 'Active']."},
         }, "required": ["path"]}),
]


# ─────────────────────────────────────────────────────────────────────
# Tool wiring
# ─────────────────────────────────────────────────────────────────────
# The MCP tool's argument names are user-facing — chosen for clarity in
# Claude's tool picker. The plugin's HTTP body uses shorter, plugin-internal
# names. PARAM_MAP rewrites the keys before posting so each side stays clean.

ROUTES: dict[str, str] = {
    "health_check":              "/health",

    "get_document_info":         "/document/info",
    "get_model_statistics":      "/document/statistics",
    "get_model_tree":            "/document/tree",

    "list_model_files":          "/model/files",
    "open_document":             "/model/open",
    "new_document":              "/model/new",
    "append_files":              "/model/append",
    "remove_file":               "/model/remove",
    "update_files":              "/model/update",
    "save_document":             "/model/save",
    "publish_nwd":               "/model/publish",
    "convert_to_nwd":            "/model/convert",

    "capture_view":              "/view/capture",
    "zoom_to_elements":          "/view/zoom",
    "set_section_box":           "/view/section-box",
    "clear_section":             "/view/section-clear",
    "get_section":               "/view/section-get",

    "get_clash_summary":         "/clash/summary",
    "set_clash_result_status":   "/clash/result/status",
    "assign_clashes":            "/clash/result/assign",
    "add_clash_comment":         "/clash/result/comment",
    "zoom_to_clash":             "/clash/result/zoom",
    "auto_group_clashes":        "/clash/auto-group",
    "export_clash_report":       "/clash/report",

    "list_clash_tests":          "/clash/list",
    "create_clash_test":         "/clash/create",
    "set_clash_selections":      "/clash/set-selections",
    "run_clash_test":            "/clash/run",
    "get_clash_results":         "/clash/results",
    "delete_clash_test":         "/clash/delete",
    "create_clash_group":        "/clash/group/create",
"list_clash_groups":         "/clash/group/list",
"delete_clash_group":        "/clash/group/delete",
  "add_clashes_to_group":      "/clash/group/add-clashes",
   "set_clash_group_status":    "/clash/group/set-status",

    "list_sets":                 "/sets/list",
    "create_selection_set":      "/sets/selection/create",
    "get_selection_set_items":   "/sets/items",
    "create_search_set":         "/sets/search/create",
    "execute_search_set":        "/sets/search/run",
    "delete_set":                "/sets/delete",

    "find_items":                "/elements/find",
    "find_items_by_name":        "/elements/find-by-name",
    "select_all":                "/selection/select-all",
    "deselect_all":              "/selection/deselect-all",
    "invert_selection":          "/selection/invert",
    "select_from_set":           "/selection/from-set",
    "get_current_selection":     "/selection/get",
    "get_element_properties":    "/elements/properties",

    "color_elements":            "/elements/color",
    "set_transparency":          "/elements/transparency",
    "reset_overrides":           "/elements/reset-overrides",
    "hide_elements":             "/elements/hide",
    "unhide_all":                "/elements/unhide-all",
    "isolate_elements":          "/elements/isolate",

    "list_viewpoints":           "/viewpoints/list",
    "save_viewpoint":            "/viewpoints/save",
    "goto_viewpoint":            "/viewpoints/goto",
    "delete_viewpoint":          "/viewpoints/delete",
    "get_current_viewpoint":     "/viewpoints/current",
}


# Tool-arg-name → plugin-body-key. Tools not listed here pass args through unchanged.
PARAM_MAP: dict[str, dict[str, str]] = {
    "create_clash_test":      {"clash_type":  "type"},
    "set_clash_selections":   {"test_name":   "name"},
    "run_clash_test":         {"test_name":   "name"},
    "get_clash_results":      {"test_name":   "name", "max_results": "limit"},
    "delete_clash_test":      {"test_name":   "name"},
    "find_items_by_name":     {"select_results": "select"},
    "find_items":             {"select_results": "select"},
}


def _remap(tool_name: str, args: dict) -> dict:
    """Rename argument keys per PARAM_MAP entry for this tool."""
    mapping = PARAM_MAP.get(tool_name)
    if not mapping:
        return args
    out = {}
    for k, v in (args or {}).items():
        out[mapping.get(k, k)] = v
    return out


def _ok(data: Any) -> CallToolResult:
    content = []
    if isinstance(data, dict) and "image_png_base64" in data:
        # capture_view / zoom_to_clash: hand the picture to the client as an image.
        data = dict(data)
        content.append(ImageContent(type="image", data=data.pop("image_png_base64"), mimeType="image/png"))
    # Plugin handler failures come back as {"error": ..., "detail": ...}.
    is_error = isinstance(data, dict) and "error" in data
    content.insert(0, TextContent(type="text", text=json.dumps(data, indent=2, ensure_ascii=False)))
    return CallToolResult(content=content, isError=is_error)


def _err(msg: str) -> CallToolResult:
    return CallToolResult(content=[TextContent(type="text", text=json.dumps({"error": msg}))], isError=True)


@server.list_tools()
async def list_tools() -> ListToolsResult:
    return ListToolsResult(tools=TOOLS)


@server.call_tool()
async def call_tool(name: str, arguments: dict) -> CallToolResult:
    try:
        route = ROUTES.get(name)
        if route is None:
            return _err(f"Unknown tool: {name}")
        # Run the blocking HTTP call off the event loop: long operations
        # (open/save/clash run) must not freeze the stdio connection.
        return _ok(await asyncio.to_thread(_post, route, _remap(name, arguments)))
    except Exception:
        return _err(traceback.format_exc())


# ─────────────────────────────────────────────────────────────────────
# Entry point
# ─────────────────────────────────────────────────────────────────────

async def main():
    print("Navisworks MCP bridge starting (HTTP mode)...", file=sys.stderr, flush=True)
    try:
        async with stdio_server() as (read, write):
            await server.run(read, write, server.create_initialization_options())
    except Exception as exc:
        print(f"[navisworks-mcp] crashed: {exc}", file=sys.stderr, flush=True)
        traceback.print_exc(file=sys.stderr)
        sys.exit(1)


def _selftest() -> None:
    """Standalone diagnostic — verifies the addin is reachable."""
    print("\n--- Navisworks MCP bridge self-test ---\n", file=sys.stderr)
    h = _health()
    print(json.dumps(h, indent=2), file=sys.stderr)
    if "error" in h:
        print("\nAddin not reachable. Checklist:", file=sys.stderr)
        print(" 1. Is Navisworks open?", file=sys.stderr)
        print(" 2. Is the MCP Bridge plugin installed?", file=sys.stderr)
        print("    Look for: %ProgramData%\\Autodesk\\ApplicationPlugins\\MCPBridge.bundle\\", file=sys.stderr)
        print(" 3. In Navisworks: 'AI companion' ribbon tab → click 'MCP Bridge' to start it.", file=sys.stderr)
        sys.exit(1)
    print("\nAddin reachable. Querying document info...\n", file=sys.stderr)
    print(json.dumps(_post("/document/info"), indent=2), file=sys.stderr)


def _print_help() -> None:
    msg = r"""
================================================================
  Navisworks MCP Bridge (HTTP edition)
================================================================
You ran this server directly. MCP servers are launched by an MCP
client (Claude Desktop), not by hand.

Useful commands:

    python server_http.py --test     # self-test (no MCP client needed)
    python server_http.py --help     # this message

To wire into Claude Desktop, edit:
    %APPDATA%\Claude\claude_desktop_config.json

    {
      "mcpServers": {
        "navisworks": {
          "command": "python",
          "args": ["E:\\MCP\\Navisworks_MCP\\server_http.py"]
        }
      }
    }

Make sure Navisworks is open with the MCP Bridge addin installed
before launching Claude Desktop.
================================================================
"""
    print(msg, file=sys.stderr)


if __name__ == "__main__":
    if "--test" in sys.argv or "-t" in sys.argv:
        _selftest()
        sys.exit(0)
    if "--help" in sys.argv or "-h" in sys.argv:
        _print_help()
        sys.exit(0)
    if sys.stdin.isatty():
        _print_help()
        sys.exit(0)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(line_buffering=True)
    asyncio.run(main())
