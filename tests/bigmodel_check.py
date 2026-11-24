"""Read-only check of the bridge on a large federated model that is already open.

    python tests\\bigmodel_check.py <output_dir>

Nothing is saved. The script changes only what Navisworks keeps in memory:
selection and hidden items (it ends with unhide_all + deselect_all, so items
that were hidden in the open model are shown afterwards). It does not touch
colours, sets, viewpoints, clash tests or files of the model. The CSV export
goes to <output_dir>. Prints the time of every call: the point is that
nothing hangs or crashes Navisworks.
"""

import json
import os
import sys
import time
import urllib.error
import urllib.request

BASE = "http://localhost:8765"
results = []


def call(path, body=None, timeout=900):
    req = urllib.request.Request(BASE + path, data=json.dumps(body or {}).encode("utf-8"),
                                 headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return json.loads(e.read().decode("utf-8"))


def step(name, path, body=None, show=None):
    t = time.time()
    res = call(path, body)
    secs = time.time() - t
    ok = "error" not in res
    note = (show(res) if ok and show else res.get("detail", "")) if isinstance(res, dict) else ""
    results.append((name, ok, secs))
    print(f"[{'OK  ' if ok else 'ERR '}] {name:<46} {secs:7.1f}s  {str(note)[:160]}")
    return res


def main():
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    out = os.path.abspath(sys.argv[1])
    os.makedirs(out, exist_ok=True)

    h = call("/health")
    print("plugin:", h)
    if not h.get("documentOpen", True):
        sys.exit("the bridge answers from a Navisworks window without an open document "
                 f"(pid {h.get('pid')}): close other Navisworks windows or start the bridge in the right one")
    info = step("get_document_info", "/document/info", None,
                lambda r: f"{os.path.basename(r.get('fileName') or '')}, {r.get('modelCount')} models, units {r.get('units')}")
    files = step("list_model_files", "/model/files", None, lambda r: f"{r.get('modelCount')} models")
    first_file = (files.get("files") or [{}])[0].get("name")

    step("get_model_statistics (limit 200000 / 30 s)", "/document/statistics", None,
         lambda r: f"{r['total']} items, {r['withGeometry']} with geometry, truncated={r['truncated']} {r.get('stopReason') or ''}")
    names = step("list_property_names (limit 20000 / 30 s)", "/properties/names", None,
                 lambda r: f"{r['itemsScanned']} items, {r['categoryCount']} categories, truncated={r['truncated']}")
    step("list_property_values Item/Type (50000 / 30 s)", "/properties/values", {"category": "Item", "property": "Type"},
         lambda r: f"{r['itemsWithProperty']} items, {r['distinctValues']} values, truncated={r['truncated']}; "
                   + ", ".join(f"{v['value']}:{v['items']}" for v in r["values"][:5]))
    step("list_property_values Item/Layer", "/properties/values", {"category": "Item", "property": "Layer"},
         lambda r: f"{r['distinctValues']} values, truncated={r['truncated']}")

    # Native searches over the whole model.
    step("find_items Item/Type contains 'Wall'", "/elements/find",
         {"conditions": [{"category": "Item", "property": "Type", "operator": "contains", "value": "Wall"}], "limit": 3},
         lambda r: f"{r['count']} found, truncated={r['truncated']}")
    step("find_items has_property Item/Name (max 100000)", "/elements/find",
         {"conditions": [{"category": "Item", "property": "Name", "operator": "has_property"}], "limit": 0,
          "prune_below_match": False},
         lambda r: f"{r['count']} found, truncated={r['truncated']} {r.get('stopReason') or ''}")
    step("find_items_by_name contains 'E0'", "/elements/find-by-name", {"pattern": "E0", "limit": 3},
         lambda r: f"{r['count']} found, truncated={r['truncated']}")
    step("find_items_by_name exact (nothing)", "/elements/find-by-name", {"pattern": "no-such-name-xyz", "match": "exact"},
         lambda r: f"{r['count']} found")

    # Selection and visibility (memory only).
    step("select_all", "/selection/select-all", None, lambda r: f"{r['selected']} roots")
    step("invert_selection", "/selection/invert", None, lambda r: f"{r['selected']} selected")
    if first_file:
        step(f"select_files {first_file[:30]}", "/files/select", {"names": [first_file]}, lambda r: f"{r['selected']} selected")
        step("hide_unselected (keep that file)", "/elements/hide-unselected", None,
             lambda r: f"{r['hiddenBranches']} branches hidden")
        step("capture_view (only that file)", "/view/capture", {"width": 800},
             lambda r: f"{r.get('width')}x{r.get('height')}")
        step("unhide_children (that file)", "/elements/unhide-children", None, lambda r: f"{r['unhidden']}")
        step("hide_elements (that file)", "/elements/hide", None, lambda r: f"{r['hidden']}")
        step("unhide_all", "/elements/unhide-all")
        step("statistics of that file", "/document/statistics", {"files": [first_file]},
             lambda r: f"{r['total']} items, truncated={r['truncated']}")
        step("export_properties that file (Item/Name, Item/Type)", "/properties/export",
             {"path": os.path.join(out, "first_file.csv"), "files": [first_file],
              "properties": ["Item/Name", "Item/Type", "Item/Layer"], "overwrite": True},
             lambda r: f"{r['rows']} rows, {r['bytes']} bytes, truncated={r['truncated']}")
    step("export_properties whole model, max 20000", "/properties/export",
         {"path": os.path.join(out, "model_20000.csv"), "properties": ["Item/Name", "Item/Type"],
          "max_items": 20000, "overwrite": True},
         lambda r: f"{r['rows']} rows, truncated={r['truncated']} {r.get('stopReason') or ''}")

    # Sets and viewpoints: read only.
    sets = step("list_sets", "/sets/list", None, lambda r: f"{r['count']} sets / folders")
    first_set = next((s["name"] for s in sets.get("sets", []) if s["type"] in ("selection", "search")), None)
    if first_set:
        step(f"get_set_info {first_set[:30]}", "/sets/info", {"name": first_set, "limit": 3},
             lambda r: f"{r['type']}, {r['count']} items, truncated={r['truncated']}")
    vps = step("list_viewpoints", "/viewpoints/list", None, lambda r: f"{r['count']} viewpoints / folders")
    vlist = vps.get("viewpoints", [])
    first_vp = next((v["name"] for v in vlist if v["type"] == "viewpoint"), None)
    if first_vp:
        step(f"get_viewpoint {first_vp[:30]}", "/viewpoints/get", {"name": first_vp},
             lambda r: f"{(r.get('camera') or {}).get('projection')}, hidden {r.get('hiddenItems')}")
    # A viewpoint inside an animation, by its path.
    anims = {v["name"] for v in vlist if v["type"] == "animation"}
    in_anim = next((v["name"] for v in vlist if v["type"] == "viewpoint" and v["folder"] in anims), None)
    if in_anim:
        step(f"get_viewpoint inside an animation", "/viewpoints/get", {"name": in_anim},
             lambda r: f"{r.get('path', '')[:60]}")
    step("get_current_viewpoint", "/viewpoints/current", None,
         lambda r: f"{(r.get('camera') or {}).get('projection')} at {(r.get('camera') or {}).get('position')}")

    step("deselect_all", "/selection/deselect-all")
    step("health after all", "/health", None, lambda r: f"ok={r.get('ok')}")

    bad = [n for n, ok, _ in results if not ok]
    slow = [(n, s) for n, _, s in results if s > 30]
    print(f"\n{len(results) - len(bad)}/{len(results)} calls answered without error; "
          f"total {sum(s for _, _, s in results):.0f} s.")
    for n in bad:
        print("  error:", n)
    for n, s in slow:
        print(f"  slow: {n} {s:.0f} s")


if __name__ == "__main__":
    main()
