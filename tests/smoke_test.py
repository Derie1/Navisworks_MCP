"""
Smoke test for the MCP Bridge plugin routes added in 2.2
=========================================================
Talks to the plugin over HTTP (localhost:8765), like server_http.py does.

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
    if health.get("version") != "2.2":
        sys.exit("plugin 2.2 is not running")

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

    passed = sum(ok for _, ok in results)
    print(f"\n{passed}/{len(results)} checks passed. Output: {work}")
    for name, ok in results:
        if not ok:
            print("  failed:", name)
    sys.exit(0 if passed == len(results) else 1)


if __name__ == "__main__":
    main()
