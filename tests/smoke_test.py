"""
Smoke test for the MCP Bridge plugin routes added in 2.2 and 2.2.1
===================================================================
Talks to the plugin over HTTP (localhost:8765), like the MCP server does.

    python tests/smoke_test.py <work_dir> <a1.ifc> <a2.ifc> <b1.ifc> <b2.ifc>

Needs a running Navisworks (2026 or 2027) with the bridge loaded. WARNING:
it replaces the open document (new/open), so start with nothing unsaved.
All files it writes go to <work_dir>; the IFC files are only read, except
that the first one gets its modification time touched to test update_files.

Builds two sub-models (A = a1+a2, B = b1+b2) as .nwf, a master .nwf that
appends both, saves/publishes/converts .nwd, then exercises view and clash
routes on the master. Prints one line per check and a summary.
"""

import base64
import json
import os
import re
import shutil
import sys
import time
import urllib.error
import urllib.request

BASE = "http://localhost:8765"
results = []


def call(path, body=None, timeout=1800):
    req = urllib.request.Request(BASE + path, data=json.dumps(body or {}).encode("utf-8"),
                                 headers={"Content-Type": "application/json"}, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return json.loads(e.read().decode("utf-8"))


def check(name, path, body=None, expect=None, expect_error=False):
    """Call a route; expect(result) -> True/False/str decides pass or fail."""
    t = time.time()
    res = call(path, body)
    secs = time.time() - t
    if expect_error:
        ok = "error" in res
        note = res.get("detail", "") if ok else "expected an error"
    elif "error" in res:
        ok, note = False, f"{res['error']}: {res.get('detail')}"
    else:
        verdict = expect(res) if expect else True
        ok = verdict is True or (isinstance(verdict, str) and not verdict.startswith("FAIL"))
        note = verdict if isinstance(verdict, str) else ""
    results.append((name, ok))
    print(f"[{'PASS' if ok else 'FAIL'}] {name:<44} {secs:6.1f}s  {str(note)[:150]}")
    return res


def save_image(res, path):
    if "image_png_base64" in res:
        with open(path, "wb") as f:
            f.write(base64.b64decode(res["image_png_base64"]))
        return f"{res['width']}x{res['height']} -> {os.path.basename(path)}"
    return "FAIL: no image"


def close(a, b, tol=1e-3):
    return all(abs(x - y) <= tol * max(1.0, abs(x), abs(y)) for x, y in zip(a, b))


def size(box):
    return [mx - mn for mn, mx in zip(box["min"], box["max"])]


def transform_checks(j, names):
    """Model position (2.3.1+): runs on Master.nwf = the four IFC files, b2 is moved."""
    check("open_document Master.nwf (transforms)", "/model/open", {"path": j("Master.nwf"), "discard_changes": True},
          lambda r: r["modelCount"] == 4 or f"FAIL {r['modelCount']} models")
    b2 = names[3]
    allm = check("get_model_transform (all)", "/model/transform/get", None,
                 lambda r: (r["count"] == 4
                            and "; ".join(f"{m['units']} t={m['translation']}" for m in r["models"]))
                           or f"FAIL {r}")
    base = next(m for m in allm.get("models", []) if m["name"] == b2)["box"]

    def moved(r, dx, dy, dz):
        b = r["after"]["box"]
        want_min = [base["min"][0] + dx, base["min"][1] + dy, base["min"][2] + dz]
        return close(b["min"], want_min) or f"FAIL box.min {b['min']} != {want_min}"

    check("set translation [10,0,0]", "/model/transform/set", {"name": b2, "translation": [10, 0, 0]},
          lambda r: (r["after"]["translation"] == [10, 0, 0] and moved(r, 10, 0, 0)) or f"FAIL {r['after']}")
    check("add translation [0,5,0]", "/model/transform/set", {"name": b2, "mode": "add", "translation": [0, 5, 0]},
          lambda r: (r["after"]["translation"] == [10, 5, 0] and moved(r, 10, 5, 0)) or f"FAIL {r['after']}")
    check("set rotation 90 about Z", "/model/transform/set", {"name": b2, "rotation_deg": 90},
          lambda r: (abs(r["after"]["rotation"]["angleDeg"] - 90) < 1e-6 and close(r["after"]["rotation"]["axis"], [0, 0, 1])
                     and r["after"]["translation"] == [10, 5, 0]) or f"FAIL {r['after']}")
    check("add rotation 90 -> 180", "/model/transform/set", {"name": b2, "mode": "add", "rotation_deg": 90},
          lambda r: abs(r["after"]["rotation"]["angleDeg"] - 180) < 1e-6 or f"FAIL {r['after']['rotation']}")
    check("set scale 2 -> box twice as big", "/model/transform/set", {"name": b2, "scale": 2},
          lambda r: (r["after"]["scale"] == [2, 2, 2] and close(size(r["after"]["box"]), [2 * s for s in size(r["before"]["box"])]))
                    or f"FAIL {size(r['before']['box'])} -> {size(r['after']['box'])}")
    # Other units than the file's: mm -> m makes it 1000x bigger, m -> mm 1000x smaller.
    original_units = next(m for m in allm.get("models", []) if m["name"] == b2)["units"]
    new_units, factor = ("Meters", 1000.0) if original_units == "Millimeters" else ("Millimeters", 0.001)
    check(f"units {original_units} -> {new_units}: box x{factor:g}", "/model/transform/set", {"name": b2, "units": new_units},
          lambda r: (r["after"]["units"] == new_units
                     and close(size(r["after"]["box"]), [x * factor for x in size(r["before"]["box"])], 1e-2))
                    or f"FAIL {r['before']['units']}:{size(r['before']['box'])} -> {size(r['after']['box'])}")
    check("reset_model_transform -> original box and units", "/model/transform/reset", {"name": b2},
          lambda r: (r["changed"] and r["after"]["units"] == original_units
                     and close(r["after"]["box"]["min"], base["min"]) and close(r["after"]["box"]["max"], base["max"]))
                    or f"FAIL {r.get('after', r)}")
    a1 = names[0]
    geo = next(m for m in allm.get("models", []) if m["name"] == a1)["translation"]
    check("set a1 translation to 0", "/model/transform/set", {"name": a1, "translation": [0, 0, 0]},
          lambda r: r["after"]["translation"] == [0, 0, 0] or f"FAIL {r['after']}")
    check("reset a1 -> its IFC georeference is back", "/model/transform/reset", {"name": a1},
          lambda r: (close(r["after"]["translation"], geo) and f"translation {r['after']['translation']}")
                    or f"FAIL {r['after']['translation']} != {geo}")
    check("set: nothing to change -> error", "/model/transform/set", {"name": b2}, expect_error=True)
    check("set: bad units -> error", "/model/transform/set", {"name": b2, "units": "Parsecs"}, expect_error=True)
    check("set: scale 0 -> error", "/model/transform/set", {"name": b2, "scale": 0}, expect_error=True)

    check("move b2 by [10,0,0] and save Moved.nwf", "/model/transform/set", {"name": b2, "translation": [10, 0, 0]})
    check("save_document Moved.nwf", "/model/save", {"path": j("Moved.nwf")})
    check("open_document Moved.nwf", "/model/open", {"path": j("Moved.nwf"), "discard_changes": True})
    check("position kept after reopening", "/model/transform/get", {"name": b2},
          lambda r: (r["translation"] == [10, 0, 0] and close(r["box"]["min"], [base["min"][0] + 10] + base["min"][1:]))
                    or f"FAIL {r}")

    check("open_document Master_2026.nwd", "/model/open", {"path": j("Master_2026.nwd"), "discard_changes": True})
    check("file inside .nwd -> error with hint", "/model/transform/set", {"name": b2, "translation": [1, 0, 0]},
          expect_error=True)
    check("whole .nwd can be moved", "/model/transform/set", {"index": 0, "mode": "add", "translation": [0, 0, 3]},
          lambda r: close(r["after"]["box"]["min"], [r["before"]["box"]["min"][0], r["before"]["box"]["min"][1],
                                                     r["before"]["box"]["min"][2] + 3]) or f"FAIL {r}")
    check("new_document (clean up)", "/model/new", {"discard_changes": True})


def hidden_of(select_path, select_body):
    """Select something, return the hidden flag of the first selected item."""
    call(select_path, select_body)
    items = call("/selection/get").get("items", [])
    return items[0]["hidden"] if items else None


def visibility_checks(j, names):
    """Hide / Hide Unselected / Unhide All / Unhide Children (2.4.1+) on Master.nwf."""
    check("open_document Master.nwf (visibility)", "/model/open", {"path": j("Master.nwf"), "discard_changes": True},
          lambda r: r["modelCount"] == 4 or f"FAIL {r['modelCount']} models")
    a1, b1 = names[0], names[2]
    file_a1 = ("/files/select", {"names": [a1]})
    file_b1 = ("/files/select", {"names": [b1]})
    storey = ("/selection/by-name", {"pattern": "E1_OKRB", "exact": True})  # a storey inside a1

    call("/selection/deselect-all")
    check("hide_elements: nothing selected -> error", "/elements/hide", None, expect_error=True)
    check("hide_elements: unknown set -> error", "/elements/hide", {"set_name": "No such set"}, expect_error=True)

    call(*file_a1)
    check("hide_elements (file a1)", "/elements/hide", None, lambda r: r["hidden"] == 1 or f"FAIL {r}")
    check("a1 is hidden", "/selection/get", None, lambda r: r["items"][0]["hidden"] is True or f"FAIL {r}")
    check("unhide_all", "/elements/unhide-all")
    check("a1 visible after unhide_all", "/selection/get", None,
          lambda r: r["items"][0]["hidden"] is False or f"FAIL {r}")

    check("select storey E1_OKRB in a1", *storey, lambda r: r["selected"] >= 1 or f"FAIL {r}")
    check("hide the storey", "/elements/hide")
    call(*file_a1)
    check("unhide_children (file a1)", "/elements/unhide-children", None,
          lambda r: (r["unhidden"] == 1 and r["withChildren"]) or f"FAIL {r}")
    check("storey visible again", *storey,
          lambda r: (hidden_of(*storey) is False) or "FAIL storey still hidden")

    call(*file_a1)
    check("selection set 'MCP keep' = file a1", "/sets/selection/create", {"name": "MCP keep"})
    call("/selection/deselect-all")
    check("hide_unselected (set MCP keep)", "/elements/hide-unselected", {"set_name": "MCP keep"},
          lambda r: (r["kept"] == 1 and r["hiddenBranches"] >= 3 and f"{r['hiddenBranches']} branches hidden")
                    or f"FAIL {r}")
    check("b1 hidden, a1 visible", "/selection/get", None,
          lambda r: (hidden_of(*file_b1) is True and hidden_of(*file_a1) is False) or "FAIL")
    call(*file_b1)
    check("isolate_elements (selection b1) = hide_unselected", "/elements/isolate", None,
          lambda r: r["kept"] == 1 or f"FAIL {r}")
    check("now a1 hidden, b1 visible", "/selection/get", None,
          lambda r: (hidden_of(*file_a1) is True and hidden_of(*file_b1) is False) or "FAIL")
    check("reset_overrides also unhides", "/elements/reset-overrides")
    check("a1 visible after reset_overrides", "/selection/get", None,
          lambda r: hidden_of(*file_a1) is False or "FAIL")

    check("select_all (model roots)", "/selection/select-all", None,
          lambda r: r["selected"] == 4 or f"FAIL {r}")
    check("invert_selection of everything -> nothing", "/selection/invert", None,
          lambda r: r["selected"] == 0 or f"FAIL {r}")
    call(*file_a1)
    check("invert_selection of a1 -> the other 3 files", "/selection/invert", None,
          lambda r: r["selected"] == 3 or f"FAIL {r}")


def property_checks(j, names):
    """list_property_names / list_property_values (2.4.2+) on Master.nwf."""
    check("open_document Master.nwf (properties)", "/model/open", {"path": j("Master.nwf"), "discard_changes": True})
    a1 = names[0]
    allp = check("list_property_names (all)", "/properties/names", None,
                 lambda r: (r["itemsScanned"] > 0 and not r["truncated"]
                            and any(c["name"] == "Item" for c in r["categories"])
                            and f"{r['itemsScanned']} items, {r['categoryCount']} categories: "
                                + ", ".join(c["name"] for c in r["categories"][:8]))
                           or f"FAIL {str(r)[:150]}")
    total = allp.get("itemsScanned", 0)
    bad = [f"{c['name']}/{p['name']}" for c in allp.get("categories", []) for p in c["properties"]
           if p["sample"] == "(unreadable)"]
    results.append(("every sample value readable", not bad))
    print(f"[{'PASS' if not bad else 'FAIL'}] {'every sample value readable':<44}         {', '.join(bad)[:150]}")
    types = sorted({p["dataType"] for c in allp.get("categories", []) for p in c["properties"]})
    print("       data types:", ", ".join(types))
    check("get_element_properties E1_OKRB: elevation -0.07 m", "/elements/properties", {"name": "E1_OKRB"},
          lambda r: (lambda vals: (any(v.replace(" ", "") in ("-0.07m",) for v in vals) and "found -0.07 m")
                     or f"FAIL {[v for v in vals if 'm' in v][:8]}")(
              [p["value"] for c in r["categories"] for p in c["properties"]]))
    check("list_property_names files=[a1]", "/properties/names", {"files": [a1]},
          lambda r: (0 < r["itemsScanned"] < total and f"{r['itemsScanned']} of {total} items") or f"FAIL {r['itemsScanned']}")
    check("list_property_names category=Item", "/properties/names", {"category": "item"},
          lambda r: (r["categoryCount"] == 1 and any(p["name"] == "Type" for p in r["categories"][0]["properties"])
                     and ", ".join(f"{p['name']}={p['sample']}" for p in r["categories"][0]["properties"][:5]))
                    or f"FAIL {str(r)[:150]}")
    check("list_property_names max_items=10 -> truncated", "/properties/names", {"max_items": 10},
          lambda r: (r["truncated"] and r["itemsScanned"] == 10 and r["stopReason"]) or f"FAIL {r}")
    check("list_property_names unknown category -> error", "/properties/names", {"category": "No such cat"},
          expect_error=True)
    call("/selection/deselect-all")
    check("list_property_names scope=selection, nothing selected -> error", "/properties/names",
          {"scope": "selection"}, expect_error=True)
    check("list_property_names bad scope -> error", "/properties/names", {"scope": "galaxy"}, expect_error=True)

    check("list_property_values Item/Type", "/properties/values", {"category": "Item", "property": "Type"},
          lambda r: (r["itemsWithProperty"] == total and r["distinctValues"] > 1
                     and sum(v["items"] for v in r["values"]) == r["itemsWithProperty"]
                     and ", ".join(f"{v['value']}:{v['items']}" for v in r["values"][:6]))
                    or f"FAIL {str(r)[:150]}")
    call("/files/select", {"names": [a1]})
    check("list_property_values scope=selection (a1)", "/properties/values",
          {"category": "Item", "property": "Type", "scope": "selection"},
          lambda r: (0 < r["itemsWithProperty"] < total and f"{r['itemsWithProperty']} items") or f"FAIL {r}")
    check("list_property_values max_values=2", "/properties/values",
          {"category": "Item", "property": "Type", "max_values": 2},
          lambda r: (len(r["values"]) == 2 and r["valuesTruncated"]) or f"FAIL {r}")
    check("list_property_values: property missing -> error", "/properties/values", {"category": "Item"},
          expect_error=True)
    check("list_property_values unknown property -> 0 items", "/properties/values",
          {"category": "Item", "property": "No such property"},
          lambda r: r["itemsWithProperty"] == 0 or f"FAIL {r}")

    # A numeric property found in the model: min / max come back as displayed.
    num = next(((c["name"], p["name"]) for c in allp.get("categories", []) for p in c["properties"]
                if p["dataType"] in ("DoubleLength", "DoubleArea", "DoubleVolume", "Double")), None)
    if num:
        check(f"list_property_values numeric {num[0]}/{num[1]}", "/properties/values",
              {"category": num[0], "property": num[1]},
              lambda r: (r["min"] not in (None, "(unreadable)") and r["max"] not in (None, "(unreadable)")
                         and f"min {r['min']}, max {r['max']}; " + ", ".join(v["value"] for v in r["values"][:4]))
                        or f"FAIL {str(r)[:150]}")
    else:
        print("[SKIP] no numeric property in the test files")


def num(text):
    m = re.match(r"\s*([-+]?\d+(?:\.\d+)?)", text or "")
    return float(m.group(1)) if m else None


def search_checks(j, names):
    """find_items with several conditions, search sets (2.4.3+), on Master.nwf.
    Expected counts come from list_property_values over the same model."""
    check("open_document Master.nwf (search)", "/model/open", {"path": j("Master.nwf"), "discard_changes": True})
    types_r = call("/properties/values", {"category": "Item", "property": "Type"})
    types = {v["value"]: v["items"] for v in types_r["values"]}
    elev_r = call("/properties/values", {"category": "Element", "property": "Elevation"})
    elev = {v["value"]: v["items"] for v in elev_r["values"]}
    n_cov, n_map = types.get("IfcCovering", 0), types.get("IfcMappedItem", 0)
    n_proxy = types.get("IfcBuildingElementProxy", 0)
    flat = {"prune_below_match": False}

    def cnt(want):
        return lambda r: (r["count"] == want and f"{r['count']}  [{'; '.join(r['conditions'])}]") or f"FAIL {r['count']} != {want}"

    def type_is(v, op="equals"):
        return {"category": "Item", "property": "Type", "operator": op, "value": v}

    check("find Type = IfcCovering", "/elements/find", dict(flat, conditions=[type_is("IfcCovering")]), cnt(n_cov))
    check("find Type = ifccovering (ignore case)", "/elements/find", dict(flat, conditions=[type_is("ifccovering")]), cnt(n_cov))
    check("find Type = ifccovering, ignore_case=false -> 0", "/elements/find",
          dict(flat, conditions=[type_is("ifccovering")], ignore_case=False), cnt(0))
    check("find Type contains 'covering'", "/elements/find", dict(flat, conditions=[type_is("covering", "contains")]),
          lambda r: (r["count"] >= n_cov and f"{r['count']}") or f"FAIL {r['count']} < {n_cov}")
    check("find Type wildcard 'Ifc*Proxy'", "/elements/find", dict(flat, conditions=[type_is("Ifc*Proxy", "wildcard")]),
          cnt(n_proxy))
    check("find any_of IfcCovering OR IfcMappedItem", "/elements/find",
          dict(flat, any_of=[[type_is("IfcCovering")], [type_is("IfcMappedItem")]]), cnt(n_cov + n_map))
    check("find Type != IfcCovering AND has Type", "/elements/find",
          dict(flat, conditions=[type_is("IfcCovering", "not_equals")]),
          cnt(types_r["itemsWithProperty"] - n_cov))
    check("find negate (NOT Type = IfcCovering) inside files=[a1]", "/elements/find",
          dict(flat, files=[names[0]], conditions=[dict(type_is("IfcCovering"), negate=True)]),
          lambda r: (r["count"] > 0 and r["scope"].startswith("files") and f"{r['count']}") or f"FAIL {r}")

    with_elev = elev_r["itemsWithProperty"]
    n_007 = elev.get("-0.07 m", 0)
    n_pos = sum(c for v, c in elev.items() if (num(v) or 0) > 0)
    check("find Elevation = -0.07 (length, tolerance)", "/elements/find",
          dict(flat, conditions=[{"category": "Element", "property": "Elevation", "operator": "=", "value": -0.07}]),
          cnt(n_007))
    check("find Elevation = '-0.07 m' (with unit)", "/elements/find",
          dict(flat, conditions=[{"category": "Element", "property": "Elevation", "value": "-0.07 m"}]), cnt(n_007))
    check("find Elevation != -0.07", "/elements/find",
          dict(flat, conditions=[{"category": "Element", "property": "Elevation", "operator": "!=", "value": -0.07}]),
          cnt(with_elev - n_007))
    check("find Elevation > 0", "/elements/find",
          dict(flat, conditions=[{"category": "Element", "property": "Elevation", "operator": ">", "value": 0}]), cnt(n_pos))
    check("find has_property Element/Elevation", "/elements/find",
          dict(flat, conditions=[{"category": "Element", "property": "Elevation", "operator": "has_property"}]),
          cnt(with_elev))
    check("find lower-case names element/elevation", "/elements/find",
          dict(flat, conditions=[{"category": "element", "property": "elevation", "operator": "has_property"}]),
          cnt(with_elev))
    check("find unknown property -> 0 + warning", "/elements/find",
          {"conditions": [{"category": "Item", "property": "No such", "value": "x"}]},
          lambda r: (r["count"] == 0 and r["warnings"] and r["warnings"][0]) or f"FAIL {r}")
    check("find contains on a length -> error", "/elements/find",
          {"conditions": [{"category": "Element", "property": "Elevation", "operator": "contains", "value": "7"}]},
          expect_error=True)
    check("find unknown operator -> error", "/elements/find", {"conditions": [type_is("x", "almost")]}, expect_error=True)
    check("find no value -> error", "/elements/find",
          {"conditions": [{"category": "Item", "property": "Type", "operator": "equals"}]}, expect_error=True)
    check("find nothing given -> error", "/elements/find", {}, expect_error=True)
    check("find old single-condition call", "/elements/find",
          {"category": "Item", "property": "Type", "operator": "contains", "value": "Covering", "prune_below_match": False},
          lambda r: (r["count"] >= n_cov and f"{r['count']}") or f"FAIL {r}")
    check("find max_results=5 -> truncated", "/elements/find", dict(flat, conditions=[type_is("Mesh")], max_results=5),
          lambda r: (r["count"] == 5 and r["truncated"]) or f"FAIL {r}")

    check("find + select_results", "/elements/find", dict(flat, conditions=[type_is("IfcCovering")], select=True), cnt(n_cov))
    check("selection = found items", "/selection/get", None, lambda r: r["count"] == n_cov or f"FAIL {r['count']}")
    check("find + save_as_search_set 'MCP coverings'", "/elements/find",
          dict(flat, conditions=[type_is("IfcCovering")], save_as_search_set="MCP coverings"),
          lambda r: (r["searchSet"]["name"] == "MCP coverings" and not r["searchSet"]["replaced"]) or f"FAIL {r}")
    check("execute_search_set 'MCP coverings'", "/sets/search/run", {"name": "MCP coverings"},
          lambda r: r["matches"] == n_cov or f"FAIL {r}")
    check("create_search_set same name -> error", "/sets/search/create",
          {"name": "MCP coverings", "conditions": [type_is("IfcMappedItem")]}, expect_error=True)
    check("create_search_set replace=true", "/sets/search/create",
          dict(flat, name="MCP coverings", conditions=[type_is("IfcMappedItem")], replace=True),
          lambda r: (r["matches"] == n_map and r["saved"]["replaced"]) or f"FAIL {r}")
    check("create_search_set any_of + Elevation", "/sets/search/create",
          dict(flat, name="MCP or-set", any_of=[[type_is("IfcCovering")], [type_is("IfcMappedItem")]]),
          lambda r: r["matches"] == n_cov + n_map or f"FAIL {r}")
    check("create_search_set old call", "/sets/search/create",
          {"name": "MCP old", "category": "Item", "property": "Type", "operator": "equals", "value": "IfcCovering"},
          lambda r: r["matches"] > 0 or f"FAIL {r}")
    check("save_document Master_search.nwf", "/model/save", {"path": j("Master_search.nwf")})
    check("open_document Master_search.nwf", "/model/open", {"path": j("Master_search.nwf"), "discard_changes": True})
    check("OR search set survives save + reopen", "/sets/search/run", {"name": "MCP or-set"},
          lambda r: r["matches"] == n_cov + n_map or f"FAIL {r}")
    check("replaced search set survives too", "/sets/search/run", {"name": "MCP coverings"},
          lambda r: r["matches"] == n_map or f"FAIL {r}")


def set_checks(j, names):
    """Sets window (2.4.4+): folders, paths, rename, move, update, info, delete; on Master.nwf."""
    check("open_document Master.nwf (sets)", "/model/open", {"path": j("Master.nwf"), "discard_changes": True})
    a1, b1 = names[0], names[2]
    types = {v["value"]: v["items"] for v in call("/properties/values", {"category": "Item", "property": "Type"})["values"]}
    n_cov, n_map = types.get("IfcCovering", 0), types.get("IfcMappedItem", 0)
    cov = {"category": "Item", "property": "Type", "value": "IfcCovering"}
    mapped = {"category": "Item", "property": "Type", "value": "IfcMappedItem"}

    def listed():
        return {s["name"]: s for s in call("/sets/list")["sets"]}

    check("create_set_folder MCP/Трубы", "/sets/folder/create", {"path": "MCP/Трубы"},
          lambda r: (r["created"] and r["path"] == "MCP/Трубы") or f"FAIL {r}")
    check("create_set_folder again -> created=false", "/sets/folder/create", {"path": "mcp/трубы"},
          lambda r: (not r["created"] and r["path"] == "MCP/Трубы") or f"FAIL {r}")
    call("/files/select", {"names": [a1]})
    check("create_selection_set by path MCP/Трубы/A1", "/sets/selection/create", {"name": "MCP/Трубы/A1"},
          lambda r: r["saved"]["path"] == "MCP/Трубы/A1" or f"FAIL {r}")
    check("create_selection_set same path -> error", "/sets/selection/create", {"name": "MCP/Трубы/A1"},
          expect_error=True)
    call("/files/select", {"names": [b1]})
    check("create_selection_set folder=MCP name=B1 set", "/sets/selection/create", {"name": "B1 set", "folder": "MCP"},
          lambda r: r["saved"]["path"] == "MCP/B1 set" or f"FAIL {r}")
    check("list_sets shows folders and types", "/sets/list", None,
          lambda r: (lambda d: (d.get("MCP", {}).get("type") == "folder" and d.get("MCP/Трубы", {}).get("type") == "folder"
                                and d.get("MCP/Трубы/A1", {}).get("type") == "selection"
                                and d["MCP/Трубы/A1"]["folder"] == "MCP/Трубы" and d["MCP/Трубы/A1"]["count"] == 1
                                and ", ".join(f"{k} ({v['type']})" for k, v in d.items() if k.startswith("MCP")))
                     or f"FAIL {list(d)}")({s["name"]: s for s in r["sets"]}))
    check("get_set_info MCP/Трубы/A1", "/sets/info", {"name": "MCP/Трубы/A1"},
          lambda r: (r["type"] == "selection" and r["folder"] == "MCP/Трубы" and r["count"] == 1
                     and r["files"][0]["file"] == a1) or f"FAIL {r}")
    check("get_set_info by bare name A1", "/sets/info", {"name": "a1"},
          lambda r: r["path"] == "MCP/Трубы/A1" or f"FAIL {r}")
    check("get_set_info folder MCP", "/sets/info", {"name": "MCP"},
          lambda r: (r["type"] == "folder" and r["setsInside"] == 2 and r["foldersInside"] == 1) or f"FAIL {r}")
    check("get_set_info unknown -> error", "/sets/info", {"name": "MCP/nope"}, expect_error=True)

    # update_selection_set: b1 is selected
    check("update_selection_set A1 add b1", "/sets/selection/update", {"name": "MCP/Трубы/A1", "mode": "add"},
          lambda r: (r["before"] == 1 and r["after"] == 2) or f"FAIL {r}")
    check("update_selection_set A1 remove b1", "/sets/selection/update", {"name": "MCP/Трубы/A1", "mode": "remove"},
          lambda r: r["after"] == 1 or f"FAIL {r}")
    check("A1 again holds file a1 only", "/sets/info", {"name": "MCP/Трубы/A1"},
          lambda r: (r["count"] == 1 and r["files"][0]["file"] == a1) or f"FAIL {r}")
    check("update_selection_set A1 replace with b1", "/sets/selection/update", {"name": "MCP/Трубы/A1"},
          lambda r: r["after"] == 1 or f"FAIL {r}")
    check("A1 now holds b1", "/sets/info", {"name": "MCP/Трубы/A1"},
          lambda r: r["files"][0]["file"] == b1 or f"FAIL {r}")
    check("update_selection_set bad mode -> error", "/sets/selection/update", {"name": "A1", "mode": "swap"},
          expect_error=True)
    call("/files/select", {"names": [a1]})
    check("update_selection_set A1 replace with a1", "/sets/selection/update", {"name": "A1", "mode": "replace"})

    check("rename_set A1 -> A1 renamed", "/sets/rename", {"name": "MCP/Трубы/A1", "new_name": "A1 renamed"},
          lambda r: r["path"] == "MCP/Трубы/A1 renamed" or f"FAIL {r}")
    check("rename_set onto a sibling name -> error", "/sets/rename", {"name": "MCP/B1 set", "new_name": "Трубы"},
          expect_error=True)
    check("rename_set with '/' -> error", "/sets/rename", {"name": "MCP/B1 set", "new_name": "a/b"}, expect_error=True)
    check("select_from_set by path", "/selection/from-set", {"name": "MCP/Трубы/A1 renamed"},
          lambda r: r["selected"] == 1 or f"FAIL {r}")

    check("move_set B1 set into MCP/Трубы", "/sets/move", {"name": "MCP/B1 set", "folder": "MCP/Трубы"},
          lambda r: r["path"] == "MCP/Трубы/B1 set" or f"FAIL {r}")
    check("move_set B1 set to position 0", "/sets/move", {"name": "MCP/Трубы/B1 set", "position": 0},
          lambda r: r["position"] == 0 or f"FAIL {r}")
    check("B1 set is first in MCP/Трубы", "/sets/info", {"name": "MCP/Трубы"},
          lambda r: (r["children"][0]["name"] == "B1 set" and ", ".join(c["name"] for c in r["children"])) or f"FAIL {r}")
    check("move_set folder into its own sub-folder -> error", "/sets/move", {"name": "MCP", "folder": "MCP/Трубы"},
          expect_error=True)
    check("move_set B1 set to the top level", "/sets/move", {"name": "B1 set", "folder": ""},
          lambda r: r["path"] == "B1 set" or f"FAIL {r}")
    check("move_set folder Трубы into new folder Архив", "/sets/move", {"name": "MCP/Трубы", "folder": "MCP/Архив"},
          lambda r: r["path"] == "MCP/Архив/Трубы" or f"FAIL {r}")
    check("set inside a moved folder is found by its new path", "/sets/info", {"name": "MCP/Архив/Трубы/A1 renamed"},
          lambda r: r["count"] == 1 or f"FAIL {r}")

    check("create_search_set by path MCP/Поиск/Coverings", "/sets/search/create",
          {"name": "MCP/Поиск/Coverings", "conditions": [cov], "prune_below_match": False},
          lambda r: (r["matches"] == n_cov and r["saved"]["path"] == "MCP/Поиск/Coverings") or f"FAIL {r}")
    check("get_set_info search set: readable conditions", "/sets/info", {"name": "Coverings"},
          lambda r: (r["type"] == "search" and r["count"] == n_cov and r["searchScope"] == "all"
                     and "Item/Type = 'IfcCovering'" in r["conditions"][0] and r["conditions"][0]) or f"FAIL {r}")
    check("update_search_set -> IfcCovering OR IfcMappedItem", "/sets/search/update",
          {"name": "MCP/Поиск/Coverings", "any_of": [[cov], [mapped]], "prune_below_match": False},
          lambda r: (r["matches"] == n_cov + n_map and len(r["before"]) == 1) or f"FAIL {r}")
    check("get_set_info shows the OR group", "/sets/info", {"name": "Coverings"},
          lambda r: (len(r["conditions"]) == 2 and r["conditions"][1].startswith("OR ") and r["count"] == n_cov + n_map
                     and " | ".join(r["conditions"])) or f"FAIL {r}")
    check("update_search_set on a selection set -> error", "/sets/search/update",
          {"name": "B1 set", "conditions": [cov]}, expect_error=True)
    check("update_selection_set on a search set -> error", "/sets/selection/update", {"name": "Coverings"},
          expect_error=True)
    check("find_items save_as_search_set into a folder", "/elements/find",
          {"conditions": [mapped], "save_as_search_set": "Mapped", "folder": "MCP/Поиск", "prune_below_match": False},
          lambda r: (r["searchSet"]["path"] == "MCP/Поиск/Mapped" and r["count"] == n_map) or f"FAIL {r}")
    check("hide_elements by set path", "/elements/hide", {"set_name": "MCP/Поиск/Mapped"},
          lambda r: r["hidden"] == n_map or f"FAIL {r}")
    check("unhide_all", "/elements/unhide-all")

    check("delete_set non-empty folder -> error", "/sets/delete", {"name": "MCP"}, expect_error=True)
    check("save_document Master_sets.nwf", "/model/save", {"path": j("Master_sets.nwf")})
    check("open_document Master_sets.nwf", "/model/open", {"path": j("Master_sets.nwf"), "discard_changes": True})
    check("folders and sets survive save + reopen", "/sets/list", None,
          lambda r: (lambda d: (all(k in d for k in ("MCP/Архив/Трубы/A1 renamed", "MCP/Поиск/Coverings",
                                                     "MCP/Поиск/Mapped", "B1 set"))
                                and d["MCP/Поиск/Coverings"]["type"] == "search" and f"{len(d)} entries")
                     or f"FAIL {list(d)}")({s["name"]: s for s in r["sets"]}))
    check("OR search set still finds both types", "/sets/search/run", {"name": "MCP/Поиск/Coverings"},
          lambda r: r["matches"] == n_cov + n_map or f"FAIL {r}")
    check("delete_set one set by path", "/sets/delete", {"name": "MCP/Поиск/Mapped"},
          lambda r: (r["deleted"] and r["type"] == "search") or f"FAIL {r}")
    check("delete_set folder MCP recursive", "/sets/delete", {"name": "MCP", "recursive": True},
          lambda r: (r["deleted"] and r["type"] == "folder") or f"FAIL {r}")
    check("nothing of MCP left", "/sets/list", None,
          lambda r: (not any(s["name"].startswith("MCP/") or s["name"] == "MCP" for s in r["sets"])
                     and ", ".join(s["name"] for s in r["sets"])) or f"FAIL {r}")
    check("delete_set empty folder without recursive", "/sets/folder/create", {"path": "Empty"})
    check("delete_set Empty", "/sets/delete", {"name": "Empty"}, lambda r: r["deleted"] or f"FAIL {r}")


def viewpoint_checks(j, names):
    """Saved Viewpoints (2.4.5+): folders, camera, update, overrides, section; on Master.nwf."""
    check("open_document Master.nwf (viewpoints)", "/model/open", {"path": j("Master.nwf"), "discard_changes": True})
    a1, b1 = names[0], names[2]

    def cam(r):
        return r.get("camera") or {}

    def near(a, b, tol=1e-3):
        return a is not None and b is not None and close(a, b, tol)

    call("/files/select", {"names": [b1]})
    call("/view/zoom", {"padding": 0})
    cur = call("/viewpoints/current")
    p0 = cam(cur).get("position")
    check("get_current_viewpoint: full camera", "/viewpoints/current", None,
          lambda r: (all(k in cam(r) for k in ("position", "direction", "up", "projection", "fovDeg"))
                     and f"{cam(r)['projection']} fov {cam(r)['fovDeg']} dir {cam(r)['direction']}") or f"FAIL {r}")
    check("save_viewpoint MCP/Обзор/All (new folders)", "/viewpoints/save", {"name": "MCP/Обзор/All"},
          lambda r: (r["path"] == "MCP/Обзор/All" and not r["hiddenItems"]) or f"FAIL {r}")
    check("save_viewpoint same name -> error", "/viewpoints/save", {"name": "MCP/Обзор/All"}, expect_error=True)
    check("save_viewpoint replace=true", "/viewpoints/save", {"name": "All", "folder": "MCP/Обзор", "replace": True},
          lambda r: r["replaced"] or f"FAIL {r}")
    check("list_viewpoints: folders and viewpoint", "/viewpoints/list", None,
          lambda r: (lambda d: (d.get("MCP", {}).get("type") == "folder" and d.get("MCP/Обзор/All", {}).get("type") == "viewpoint"
                                and d["MCP/Обзор/All"]["folder"] == "MCP/Обзор" and ", ".join(d))
                     or f"FAIL {list(d)}")({v["name"]: v for v in r["viewpoints"]}))
    check("get_viewpoint All = the view it was saved from", "/viewpoints/get", {"name": "All"},
          lambda r: (near(cam(r)["position"], p0) and r["path"] == "MCP/Обзор/All") or f"FAIL {r}")

    # Camera of the current view
    p1 = [p0[0] + 50, p0[1] - 50, p0[2] + 20]
    check("set_camera position + direction +X", "/viewpoints/camera", {"position": p1, "direction": [1, 0, 0]},
          lambda r: (near(r["after"]["position"], p1) and near(r["after"]["direction"], [1, 0, 0])
                     and abs(r["after"]["up"][2]) > 0.99 and f"up {r['after']['up']}") or f"FAIL {r['after']}")
    tgt = [p1[0], p1[1] + 10, p1[2]]
    check("set_camera target -> direction +Y, focal 10", "/viewpoints/camera", {"target": tgt},
          lambda r: (near(r["after"]["direction"], [0, 1, 0]) and abs(r["after"]["focalDistance"] - 10) < 1e-6
                     and near(r["after"]["target"], tgt)) or f"FAIL {r['after']}")
    check("set_camera orthographic, height 50", "/viewpoints/camera", {"projection": "orthographic", "ortho_height": 50},
          lambda r: (r["after"]["projection"] == "orthographic" and abs(r["after"]["orthoHeight"] - 50) < 1e-6) or f"FAIL {r['after']}")
    check("set_camera fov on orthographic -> error", "/viewpoints/camera", {"fov_deg": 60}, expect_error=True)
    check("set_camera perspective, fov 60", "/viewpoints/camera", {"projection": "perspective", "fov_deg": 60},
          lambda r: (r["after"]["projection"] == "perspective" and abs(r["after"]["fovDeg"] - 60) < 1e-3) or f"FAIL {r['after']}")
    check("set_camera nothing -> error", "/viewpoints/camera", {}, expect_error=True)
    check("set_camera target + direction -> error", "/viewpoints/camera", {"target": tgt, "direction": [1, 0, 0]},
          expect_error=True)
    check("set_camera bad render_style -> error", "/viewpoints/camera", {"render_style": "Cartoon"}, expect_error=True)

    check("goto_viewpoint All -> camera back", "/viewpoints/goto", {"name": "MCP/Обзор/All"},
          lambda r: near(cam(r)["position"], p0) or f"FAIL {r}")
    check("current view is the viewpoint's", "/viewpoints/current", None,
          lambda r: near(cam(r)["position"], p0) or f"FAIL {cam(r)['position']} != {p0}")

    call("/viewpoints/camera", {"position": p1})
    check("update_viewpoint All from the current view", "/viewpoints/update", {"name": "All"},
          lambda r: (near(r["after"]["position"], p1) and near(r["before"]["position"], p0)) or f"FAIL {r}")
    check("set_camera on saved All: direction -Y", "/viewpoints/camera", {"name": "All", "direction": [0, -1, 0]},
          lambda r: (r["target"] == "MCP/Обзор/All" and near(r["after"]["direction"], [0, -1, 0])
                     and near(r["after"]["position"], p1)) or f"FAIL {r}")
    check("current view untouched by it", "/viewpoints/current", None,
          lambda r: not near(cam(r)["direction"], [0, -1, 0]) or f"FAIL {cam(r)}")

    # Hidden items and colours stored in a viewpoint
    call("/files/select", {"names": [a1]})
    call("/elements/hide")
    call("/files/color", {"names": [b1], "r": 255, "g": 0, "b": 0})
    check("save_viewpoint with_overrides", "/viewpoints/save", {"name": "MCP/Обзор/Hidden A1", "with_overrides": True},
          lambda r: (r["hiddenItems"] and r["colorOverrides"]) or f"FAIL {r}")
    check("get_viewpoint: stored hidden items and colours", "/viewpoints/get", {"name": "Hidden A1"},
          lambda r: ((r["hiddenItems"] or 0) >= 1 and (r["colorOverrides"] or 0) >= 1 and "position" in cam(r)
                     and f"hidden {r['hiddenItems']}, colours {r['colorOverrides']}, camera at {cam(r)['position']}")
                    or f"FAIL {r}")
    check("reset_overrides (unhide + colours off)", "/elements/reset-overrides")
    check("goto_viewpoint Hidden A1 applies them", "/viewpoints/goto", {"name": "Hidden A1"},
          lambda r: ((r["hiddenApplied"] or 0) >= 1 and (r["colorOverridesApplied"] or 0) >= 1
                     and f"hidden {r['hiddenApplied']}, coloured {r['colorOverridesApplied']}") or f"FAIL {r}")
    # Navisworks stores the hidden state on the file's top children, not on
    # the file node itself: count hidden items inside a1 / b1.
    def hidden_in(f):
        r = call("/properties/values", {"category": "Item", "property": "Hidden", "files": [f]})
        return {v["value"]: v["items"] for v in r.get("values", [])}.get("Yes", 0)
    check("a1 hidden again, b1 not", "/viewpoints/current", None,
          lambda r: (lambda ha, hb: (ha >= 1 and hb == 0 and f"hidden inside a1: {ha}, b1: {hb}")
                     or f"FAIL a1 {ha}, b1 {hb}")(hidden_in(a1), hidden_in(b1)))
    check("unhide_all", "/elements/unhide-all")
    check("reset_overrides", "/elements/reset-overrides")

    # Section box stored in a viewpoint
    call("/files/select", {"names": [b1]})
    check("set_section_box around b1", "/view/section-box", {"padding": 0.5})
    check("save_viewpoint MCP/Сечение", "/viewpoints/save", {"name": "MCP/Сечение"},
          lambda r: r["path"] == "MCP/Сечение" or f"FAIL {r}")
    check("get_viewpoint shows the section", "/viewpoints/get", {"name": "MCP/Сечение"},
          lambda r: (cam(r).get("section") is not None and str(cam(r)["section"])[:120]) or f"FAIL {cam(r)}")
    check("clear_section", "/view/section-clear")
    check("goto_viewpoint MCP/Сечение brings the section back", "/viewpoints/goto", {"name": "MCP/Сечение"})
    check("section is on", "/view/section-get", None,
          lambda r: '"Enabled":true' in (r.get("clipPlaneSet") or "").replace(" ", "") or f"FAIL {(r.get('clipPlaneSet') or '')[:160]}")
    check("clear_section", "/view/section-clear")

    # Folders, rename, move
    check("create_viewpoint_folder MCP/Архив", "/viewpoints/folder/create", {"path": "MCP/Архив"},
          lambda r: (r["created"] and r["path"] == "MCP/Архив") or f"FAIL {r}")
    check("create_viewpoint_folder again -> created=false", "/viewpoints/folder/create", {"path": "mcp/архив"},
          lambda r: (not r["created"] and r["path"] == "MCP/Архив") or f"FAIL {r}")
    check("rename_viewpoint All -> Общий вид", "/viewpoints/rename", {"name": "MCP/Обзор/All", "new_name": "Общий вид"},
          lambda r: r["path"] == "MCP/Обзор/Общий вид" or f"FAIL {r}")
    check("rename_viewpoint onto a sibling -> error", "/viewpoints/rename", {"name": "Общий вид", "new_name": "Hidden A1"},
          expect_error=True)
    check("move_viewpoint Сечение into MCP/Архив", "/viewpoints/move", {"name": "MCP/Сечение", "folder": "MCP/Архив"},
          lambda r: r["path"] == "MCP/Архив/Сечение" or f"FAIL {r}")
    check("move_viewpoint Hidden A1 to position 0", "/viewpoints/move", {"name": "Hidden A1", "position": 0},
          lambda r: r["position"] == 0 or f"FAIL {r}")
    check("Hidden A1 is first in MCP/Обзор", "/viewpoints/list", None,
          lambda r: (lambda names_: (names_[0] == "MCP/Обзор/Hidden A1" and ", ".join(names_)) or f"FAIL {names_}")(
              [v["name"] for v in r["viewpoints"] if v["folder"] == "MCP/Обзор"]))
    check("move_viewpoint folder into itself -> error", "/viewpoints/move", {"name": "MCP", "folder": "MCP/Архив"},
          expect_error=True)
    check("move_viewpoint folder Обзор into Архив", "/viewpoints/move", {"name": "MCP/Обзор", "folder": "MCP/Архив"},
          lambda r: r["path"] == "MCP/Архив/Обзор" or f"FAIL {r}")
    check("goto by the new path", "/viewpoints/goto", {"name": "MCP/Архив/Обзор/Общий вид", "apply_overrides": False},
          lambda r: near(cam(r)["position"], p1) or f"FAIL {r}")
    check("goto unknown -> error", "/viewpoints/goto", {"name": "MCP/nope"}, expect_error=True)
    check("goto a folder -> error", "/viewpoints/goto", {"name": "MCP/Архив"}, expect_error=True)

    check("save_document Master_vp.nwf", "/model/save", {"path": j("Master_vp.nwf")})
    check("open_document Master_vp.nwf", "/model/open", {"path": j("Master_vp.nwf"), "discard_changes": True})
    check("viewpoints and folders survive save + reopen", "/viewpoints/list", None,
          lambda r: (lambda d: (all(k in d for k in ("MCP/Архив/Сечение", "MCP/Архив/Обзор/Общий вид", "MCP/Архив/Обзор/Hidden A1"))
                                and d["MCP/Архив/Обзор/Hidden A1"]["hiddenItems"] and f"{len(d)} entries")
                     or f"FAIL {list(d)}")({v["name"]: v for v in r["viewpoints"]}))
    check("saved camera survives too", "/viewpoints/get", {"name": "Общий вид"},
          lambda r: (near(cam(r)["position"], p1) and near(cam(r)["direction"], [0, -1, 0])) or f"FAIL {cam(r)}")
    check("delete_viewpoint non-empty folder -> error", "/viewpoints/delete", {"name": "MCP"}, expect_error=True)
    check("delete_viewpoint one viewpoint", "/viewpoints/delete", {"name": "MCP/Архив/Сечение"},
          lambda r: (r["deleted"] and r["type"] == "viewpoint") or f"FAIL {r}")
    check("delete_viewpoint folder MCP recursive", "/viewpoints/delete", {"name": "MCP", "recursive": True},
          lambda r: (r["deleted"] and r["type"] == "folder") or f"FAIL {r}")
    check("no viewpoints left", "/viewpoints/list", None, lambda r: r["count"] == 0 or f"FAIL {r}")


def read_csv(path, delimiter=","):
    import csv
    with open(path, encoding="utf-8-sig", newline="") as f:
        return list(csv.DictReader(f, delimiter=delimiter))


def export_checks(j, names):
    """export_properties (2.4.6+) on Master.nwf; counts against list_property_values."""
    check("open_document Master.nwf (export)", "/model/open", {"path": j("Master.nwf"), "discard_changes": True})
    a1 = names[0]
    types_r = call("/properties/values", {"category": "Item", "property": "Type"})
    n_all = types_r["itemsScanned"]
    n_cov = {v["value"]: v["items"] for v in types_r["values"]}.get("IfcCovering", 0)
    n_elev = call("/properties/values", {"category": "Element", "property": "Elevation"})["itemsWithProperty"]
    out = j("export", "props.csv")

    def rows_ok(path, want, delimiter=","):
        def f(r):
            if not os.path.exists(path):
                return "FAIL no file"
            rows = read_csv(path, delimiter)
            return (len(rows) == r["rows"] == want and f"{len(rows)} rows, {r['columns']} columns, {r['bytes']} bytes") \
                or f"FAIL file {len(rows)} / answer {r['rows']} / expected {want}"
        return f

    check("export_properties Item/Type + Element/Elevation (all)", "/properties/export",
          {"path": out, "properties": ["Item/Type", "Element/Elevation"]}, rows_ok(out, n_all))
    rows = read_csv(out) if os.path.exists(out) else []
    results.append(("csv: header File, Path, Name, Class + columns",
                    bool(rows) and list(rows[0].keys()) == ["File", "Path", "Name", "Class", "Item/Type", "Element/Elevation"]))
    print(f"[{'PASS' if results[-1][1] else 'FAIL'}] {'csv: header':<44}         {list(rows[0].keys()) if rows else '-'}")
    cov_rows = sum(1 for r in rows if r["Item/Type"] == "IfcCovering")
    results.append(("csv: IfcCovering rows = list_property_values", cov_rows == n_cov))
    print(f"[{'PASS' if cov_rows == n_cov else 'FAIL'}] {'csv: IfcCovering rows':<44}         {cov_rows} vs {n_cov}")
    elev = [r["Element/Elevation"] for r in rows if r["Element/Elevation"]]
    ok = len(elev) == n_elev and "-0.07 m" in elev
    results.append(("csv: elevations with unit", ok))
    print(f"[{'PASS' if ok else 'FAIL'}] {'csv: elevations with unit':<44}         {len(elev)} values, e.g. {elev[:3]}")

    check("export again without overwrite -> error", "/properties/export",
          {"path": out, "properties": ["Item/Type"]}, expect_error=True)
    check("export require_property Element/Elevation, ';'", "/properties/export",
          {"path": j("export", "storeys.csv"), "properties": ["Element/Elevation", "Item/Name"],
           "require_property": "Element/Elevation", "delimiter": ";"},
          rows_ok(j("export", "storeys.csv"), n_elev, ";"))
    check("export level=geometry -> fewer rows", "/properties/export",
          {"path": j("export", "geom.csv"), "properties": ["Item/Type"], "level": "geometry"},
          lambda r: (0 < r["rows"] < n_all and f"{r['rows']} of {n_all}") or f"FAIL {r['rows']}")
    call("/files/select", {"names": [a1]})
    check("export level=top, scope=selection -> 1 row", "/properties/export",
          {"path": j("export", "top.csv"), "properties": ["Item/Name"], "level": "top", "scope": "selection"},
          rows_ok(j("export", "top.csv"), 1))
    check("export files=[a1], all properties of category Item", "/properties/export",
          {"path": j("export", "a1_item.csv"), "files": [a1], "categories": ["Item"]},
          lambda r: (r["columns"] > 5 and "Item/Type" in r["header"] and r["rows"] < n_all
                     and f"{r['rows']} rows, {r['columns']} columns") or f"FAIL {r}")
    check("export every property, max_columns=10 -> warning", "/properties/export",
          {"path": j("export", "wide.csv"), "max_columns": 10},
          lambda r: (r["columns"] == 14 and r["warnings"] and r["warnings"][0][:60]) or f"FAIL {r}")
    check("export max_items=7 -> truncated", "/properties/export",
          {"path": j("export", "few.csv"), "properties": ["Item/Type"], "max_items": 7},
          lambda r: (r["rows"] == 7 and r["truncated"]) or f"FAIL {r}")
    check("export unknown property -> warning", "/properties/export",
          {"path": j("export", "none.csv"), "properties": ["Item/No such"]},
          lambda r: (r["warnings"] and "no item has" in r["warnings"][0] and r["warnings"][0]) or f"FAIL {r}")
    check("export bad extension -> error", "/properties/export", {"path": j("export", "x.txt")}, expect_error=True)
    check("export bad property form -> error", "/properties/export",
          {"path": j("export", "y.csv"), "properties": ["Type"]}, expect_error=True)
    check("export bad level -> error", "/properties/export",
          {"path": j("export", "z.csv"), "level": "leaves"}, expect_error=True)


def main():
    if len(sys.argv) != 6:
        sys.exit(__doc__)
    work = os.path.abspath(sys.argv[1])
    a1, a2, b1, b2 = (os.path.abspath(p) for p in sys.argv[2:])
    os.makedirs(work, exist_ok=True)
    j = lambda *p: os.path.join(work, *p)
    names = [os.path.basename(p) for p in (a1, a2, b1, b2)]

    health = call("/health")
    print("plugin:", health)
    # The routes tested here exist since 2.2; any later version must still pass.
    version = tuple(int(x) for x in str(health.get("version", "0")).split(".")[:2])
    if version < (2, 2):
        sys.exit("plugin 2.2 or later is not running")

    # ── Model files ───────────────────────────────────────────────────
    check("new_document", "/model/new", {"discard_changes": True})
    check("append_files A", "/model/append", {"paths": [a1, a2]},
          lambda r: r["modelsAfter"] == 2 or f"FAIL models={r['modelsAfter']}")
    check("append_files missing file -> error", "/model/append", {"paths": [j("nope.ifc")]}, expect_error=True)
    check("save_document A.nwf", "/model/save", {"path": j("A.nwf")},
          lambda r: os.path.exists(j("A.nwf")) or "FAIL file missing")
    check("save_document bad extension -> error", "/model/save", {"path": j("A.txt")}, expect_error=True)

    check("new_document (no unsaved changes)", "/model/new", {})
    check("append_files B", "/model/append", {"paths": [b1, b2]})
    check("save_document B.nwf", "/model/save", {"path": j("B.nwf")})

    check("new_document", "/model/new", {"discard_changes": True})
    check("append A.nwf + B.nwf", "/model/append", {"paths": [j("A.nwf"), j("B.nwf")]})
    check("list_model_files (A.nwf + B.nwf expand to 4)", "/model/files", None,
          lambda r: (r["modelCount"] == 4 and ", ".join(f"#{f['index']} {f['name']}" for f in r["files"]))
                    or f"FAIL {r['modelCount']} models")
    check("save_document Master.nwf", "/model/save", {"path": j("Master.nwf")})
    check("save_document Master_2026.nwd keeps Master.nwf open", "/model/save",
          {"path": j("Master_2026.nwd"), "version": "2026"},
          lambda r: (r["openDocument"].lower().endswith("master.nwf") and f"{r['bytes']} bytes, open: Master.nwf")
                    or f"FAIL open document is {r['openDocument']}")
    check("still 4 models after saving .nwd", "/model/files", None,
          lambda r: r["modelCount"] == 4 or f"FAIL {r['modelCount']} models")
    check("save_document Master_2024.nwd (version 2024)", "/model/save",
          {"path": j("Master_2024.nwd"), "version": "2024"})
    check("save_document unknown version -> error", "/model/save",
          {"path": j("x.nwd"), "version": "1999"}, expect_error=True)
    check("publish_nwd from .nwf -> clear error, no dialog", "/model/publish",
          {"path": j("Master_published.nwd")}, expect_error=True)

    # remove + re-append one IFC of sub-model B (fresh index)
    files = call("/model/files")
    b_idx = next(f["index"] for f in files["files"] if f["name"] == names[3])
    check("remove_file by index (b2)", "/model/remove", {"index": b_idx},
          lambda r: (r["document"]["modelCount"] == 3 and f"removed {r['removed']}") or f"FAIL {r}")
    check("remove_file by name (b1)", "/model/remove", {"name": names[2]},
          lambda r: r["document"]["modelCount"] == 2 or f"FAIL {r['document']['modelCount']}")
    check("remove_file unknown name -> error", "/model/remove", {"name": "does-not-exist.ifc"}, expect_error=True)
    check("append b1 + b2 back", "/model/append", {"paths": [b1, b2]},
          lambda r: r["modelsAfter"] == 4 or f"FAIL {r['modelsAfter']}")

    os.utime(a1, None)  # touch: the file "changed" on disk
    check("update_files", "/model/update", None, lambda r: f"{r['modelCount']} models")

    check("open_document with unsaved changes -> error", "/model/open", {"path": j("Master.nwf")}, expect_error=True)
    check("save_document (over Master.nwf, no path)", "/model/save", {"path": j("Master.nwf")})
    check("convert_to_nwd (2 IFC)", "/model/convert",
          {"paths": [b1, b2], "output_dir": j("converted"), "version": "2026", "overwrite": True},
          lambda r: (r["converted"] == 2 and f"restored {r.get('restoredDocument')}") or f"FAIL {r}")
    check("open_document Master_2026.nwd", "/model/open", {"path": j("Master_2026.nwd")},
          lambda r: f"{r['modelCount']} model(s), fileVersion {r['fileVersion']}, "
                    f"contains {r['files'][0].get('contains')}")
    check("publish_nwd stripped (from open .nwd)", "/model/publish",
          {"path": j("Master_published.nwd"), "version": "2026", "strip_properties": True,
           "title": "MCP smoke test", "comments": "published by tests/smoke_test.py"},
          lambda r: f"{r['bytes']} bytes")
    check("open_document Master.nwf", "/model/open", {"path": j("Master.nwf")},
          lambda r: r["modelCount"] == 4 or f"FAIL {r['modelCount']} models")

    # ── View ──────────────────────────────────────────────────────────
    check("select_all", "/selection/select-all")
    check("zoom_to_elements (selection)", "/view/zoom", {"padding": 0},
          lambda r: r.get("warning") or "no spread warning")
    r = check("capture_view", "/view/capture", {"width": 1024}, lambda r: save_image(r, j("01_all.png")))
    check("select file B1 by name", "/selection/by-name", {"pattern": names[2], "exact": True},
          lambda r: r["selected"] > 0 or "FAIL nothing selected")
    check("set_section_box around B1", "/view/section-box", {"padding": 0.5})
    check("select_files B1 (no extension)", "/files/select",
          {"names": [os.path.splitext(names[2])[0]]},
          lambda r: (r["selected"] == 1 and r["matches"][0]["path"]) or f"FAIL {r}")
    check("select_files add A1, one unknown", "/files/select",
          {"names": [names[0], "does-not-exist.ifc"], "add_to_selection": True},
          lambda r: (r["selected"] == 1 and r["notFound"] == ["does-not-exist.ifc"]) or f"FAIL {r}")
    check("selection after add_to_selection", "/selection/get", None,
          lambda r: r["count"] == 2 or f"FAIL count={r['count']}")
    check("select_files unknown only -> error", "/files/select", {"names": ["does-not-exist.ifc"]},
          expect_error=True)
    check("color_files A1 + A2 red", "/files/color", {"names": [names[0], names[1]], "r": 255, "g": 0, "b": 0},
          lambda r: r["colored"] == 2 or f"FAIL {r}")
    check("color_files B1 green (contains)", "/files/color",
          {"names": [os.path.splitext(names[2])[0][:-1]], "contains": True, "r": 0, "g": 200, "b": 0},
          lambda r: r["colored"] >= 1 or f"FAIL {r}")
    check("capture_view colored", "/view/capture", {"width": 1024}, lambda r: save_image(r, j("02a_colored.png")))
    check("reset_overrides", "/elements/reset-overrides")
    check("select file B1 again", "/files/select", {"names": [names[2]]})
    check("get_section", "/view/section-get", None, lambda r: (r.get("clipPlaneSet") or "")[:140])
    check("capture_view sectioned", "/view/capture", {"width": 1024}, lambda r: save_image(r, j("02_section.png")))
    check("clear_section", "/view/section-clear")
    check("get_section after clear", "/view/section-get", None, lambda r: (r.get("clipPlaneSet") or "")[:140])

    # ── Clash ─────────────────────────────────────────────────────────
    # Everything against everything: the sample files may not overlap each
    # other, but elements inside one file always produce some clashes.
    check("select all for clash sets", "/selection/select-all")
    check("selection set SetA", "/sets/selection/create", {"name": "MCP SetA"})
    check("selection set SetB", "/sets/selection/create", {"name": "MCP SetB"})
    check("create clash test", "/clash/create",
          {"name": "MCP smoke", "type": "Hard", "tolerance": 0.01, "selectionA": "MCP SetA", "selectionB": "MCP SetB"})
    check("run clash test", "/clash/run", {"name": "MCP smoke"})
    check("get_clash_summary", "/clash/summary", {"test_name": "MCP smoke"},
          lambda r: json.dumps(r["tests"][0]["byStatus"]) + f" total={r['tests'][0]['total']}")
    res = check("get_clash_results", "/clash/results", {"name": "MCP smoke", "limit": 5},
                lambda r: f"{r['count']} results; first: {json.dumps(r['results'][0], ensure_ascii=False)[:120] if r['results'] else '-'}")
    clashes = [c["name"] for c in res.get("results", [])]
    if clashes:
        two = clashes[:2]
        check("set_clash_result_status Active", "/clash/result/status",
              {"test_name": "MCP smoke", "clash_names": two + ["NoSuchClash"], "status": "Active"},
              lambda r: (r["updated"] == len(two) and f"notFound={r['notFound']}") or f"FAIL {r}")
        check("assign_clashes", "/clash/result/assign",
              {"test_name": "MCP smoke", "clash_names": two, "assignee": "HLS team"})
        check("add_clash_comment", "/clash/result/comment",
              {"test_name": "MCP smoke", "clash_names": [two[0]], "comment": "Checked by MCP smoke test"})
        check("verify status/assignee/comment", "/clash/results", {"name": "MCP smoke", "limit": 5},
              lambda r: (lambda c: (c["status"] == "Active" and c.get("assignedTo") == "HLS team" and c["comments"] >= 1)
                         or f"FAIL {c}")(next(x for x in r["results"] if x["name"] == two[0])))
        check("zoom_to_clash + capture", "/clash/result/zoom",
              {"test_name": "MCP smoke", "clash_name": two[0], "section": True, "padding": 0.5,
               "capture": True, "width": 1024},
              lambda r: save_image(r, j("03_clash.png")))
        check("clear_section", "/view/section-clear")
        check("auto_group_clashes by model", "/clash/auto-group",
              {"test_name": "MCP smoke", "group_by": "model"},
              lambda r: f"{r['groups']} groups, {r['grouped']} grouped, {r['leftUngrouped']} left")
        check("list_clash_groups", "/clash/group/list", {"test_name": "MCP smoke"},
              lambda r: str(r)[:150])
        check("set_clash_result_status inside a group", "/clash/result/status",
              {"test_name": "MCP smoke", "clash_names": [two[-1]], "status": "Resolved"},
              lambda r: r["updated"] == 1 or f"FAIL {r}")
        check("auto_group_clashes by level (nothing left)", "/clash/auto-group",
              {"test_name": "MCP smoke", "group_by": "level"},
              lambda r: f"{r['groups']} groups, {r['leftUngrouped']} left")
    else:
        print("[SKIP] no clashes found between the two files: result-level checks skipped")
    check("export_clash_report csv", "/clash/report", {"path": j("report.csv")},
          lambda r: f"{r['rows']} rows")
    check("export_clash_report html (New+Active)", "/clash/report",
          {"path": j("report.html"), "statuses": ["New", "Active"]}, lambda r: f"{r['rows']} rows")
    check("save_document Master_clash.nwf", "/model/save", {"path": j("Master_clash.nwf")})

    if version >= (2, 4) and str(health.get("version")) != "2.4.0":
        visibility_checks(j, names)
    if "routes" in health and health["routes"] >= 66:
        property_checks(j, names)
    if version >= (2, 4) and str(health.get("version")) not in ("2.4.0", "2.4.1", "2.4.2"):
        search_checks(j, names)
    if health.get("routes", 0) >= 72:
        set_checks(j, names)
    if health.get("routes", 0) >= 78:
        viewpoint_checks(j, names)
    if health.get("routes", 0) >= 79:
        export_checks(j, names)
    transform_checks(j, names)

    passed = sum(ok for _, ok in results)
    print(f"\n{passed}/{len(results)} checks passed. Output: {work}")
    for name, ok in results:
        if not ok:
            print("  failed:", name)
    sys.exit(0 if passed == len(results) else 1)


if __name__ == "__main__":
    main()
