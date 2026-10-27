// =============================================================================
//  ClashResultHandlers.cs — work with individual clash results
// =============================================================================
//  /clash/summary          per-test counts by status
//  /clash/result/status    set status of named results
//  /clash/result/assign    assign named results to someone
//  /clash/result/comment   add a comment to named results
//  /clash/result/zoom      select, highlight and zoom to one result
//  /clash/auto-group       group ungrouped results by model / level / property
//  /clash/report           write a CSV or HTML report
//
//  Results are found by DisplayName anywhere under the test, including
//  inside groups. As elsewhere in this plugin, items are re-resolved through
//  SavedItemReference right before each edit: handles taken from Children
//  go stale once the clash tree changes.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

using Autodesk.Navisworks.Api;
using Autodesk.Navisworks.Api.Clash;

using NwColor = Autodesk.Navisworks.Api.Color;

namespace NavisworksMcpAddin
{
    internal static partial class Handlers
    {
        // ════════════════════════════════════════════════════════════════════
        //  CLASH RESULTS
        // ════════════════════════════════════════════════════════════════════

        public static object ClashSummary(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            DocumentClash dc = doc.GetClash();
            string only = GetString(body, "test_name", null);

            var tests = new List<object>();
            foreach (ClashTest t in EnumerateClashTests(dc.TestsData.Value.TestsRoot))
            {
                if (only != null && !string.Equals(t.DisplayName, only, StringComparison.OrdinalIgnoreCase)) continue;

                var byStatus = Enum.GetNames(typeof(ClashResultStatus)).ToDictionary(n => n, n => 0);
                int total = 0, groups = 0, ungrouped = 0;
                foreach (SavedItem child in t.Children)
                {
                    if (child is ClashResultGroup) groups++;
                    else if (child is ClashResult) ungrouped++;
                }
                WalkResults(t, null, (r, g) => { total++; byStatus[r.Status.ToString()]++; });

                tests.Add(new
                {
                    name = t.DisplayName,
                    testStatus = t.Status.ToString(),
                    lastRun = FormatTime(t.LastRun),
                    total,
                    byStatus,
                    groups,
                    ungrouped
                });
            }
            if (only != null && tests.Count == 0) throw new InvalidOperationException($"clash test '{only}' not found");
            return new { count = tests.Count, tests };
        }

        public static object ClashResultStatusSet(Dictionary<string, object> body)
        {
            ClashResultStatus status = ParseResultStatus(GetString(body, "status", null));
            var who = new Assignee(Environment.UserName);
            return EditResults(body, (dc, r) => dc.TestsData.TestsEditResultStatus(r, status, who),
                               new { status = status.ToString() });
        }

        public static object ClashResultAssign(Dictionary<string, object> body)
        {
            string name = GetString(body, "assignee", null);
            var assignee = string.IsNullOrWhiteSpace(name) ? new Assignee() : new Assignee(name.Trim());
            return EditResults(body, (dc, r) => dc.TestsData.TestsEditResultAssignedTo(r, assignee),
                               new { assignee = string.IsNullOrWhiteSpace(name) ? "(unassigned)" : name.Trim() });
        }

        public static object ClashResultComment(Dictionary<string, object> body)
        {
            string text = GetString(body, "comment", null);
            if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("comment is required");
            CommentStatus cstatus = ParseCommentStatus(GetString(body, "comment_status", null));
            string author = GetString(body, "author", Environment.UserName);

            return EditResults(body, (dc, r) =>
            {
                var comments = r.Comments != null ? new CommentCollection(r.Comments) : new CommentCollection();
#if NAVIS_2027_OR_LATER
                // 2027 API: the author is an Assignee, no longer a plain string.
                comments.Add(new Comment(text, cstatus, new Assignee(author)));
#else
                comments.Add(new Comment(text, cstatus, author));
#endif
                dc.TestsData.TestsEditResultComments(r, comments);
            }, new { comment = text, commentStatus = cstatus.ToString(), author });
        }

        public static object ClashResultZoom(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            DocumentClash dc = doc.GetClash();
            string testName = GetString(body, "test_name", null)
                ?? throw new ArgumentException("test_name is required");
            string clashName = GetString(body, "clash_name", null)
                ?? throw new ArgumentException("clash_name is required");

            SavedItemReference testRef = RequireTestRef(dc, testName);
            var refs = FindResultRefs(dc, testRef, new[] { clashName }, out List<string> missing);
            if (refs.Count == 0) throw new InvalidOperationException($"clash '{clashName}' not found in '{testName}'");
            var r = (ClashResult)dc.TestsData.ResolveReference(refs[0]);

            var items1 = ResultSide(r.Selection1, r.Item1);
            var items2 = ResultSide(r.Selection2, r.Item2);
            var both = new ModelItemCollection();
            both.AddRange(items1);
            both.AddRange(items2);
            doc.CurrentSelection.CopyFrom(both);

            if (GetBool(body, "highlight", true))
            {
                // Clash Detective colours: red = item 1, green = item 2.
                doc.Models.ResetAllTemporaryMaterials();
                doc.Models.OverrideTemporaryColor(items1, new NwColor(1, 0, 0));
                doc.Models.OverrideTemporaryColor(items2, new NwColor(0, 1, 0));
            }

            BoundingBox3D box = r.ViewBounds != null && !r.ViewBounds.IsEmpty ? r.ViewBounds : r.BoundingBox;
            if (box == null || box.IsEmpty) box = both.BoundingBox();
            // Zooming to the bare clash box puts the camera inside the
            // elements; by default leave a margin as large as the box itself.
            double padding = GetDouble(body, "padding",
                Math.Max(box.Size.X, Math.Max(box.Size.Y, box.Size.Z)));
            if (GetBool(body, "section", false))
            {
                SetSectionBox(RequireView(doc), Inflate(r.BoundingBox ?? box, padding));
            }
            ZoomTo(doc, Inflate(box, padding));

            var result = new Dictionary<string, object>
            {
                ["test_name"] = testName,
                ["clash"] = DescribeClashResult(r, null)
            };
            if (GetBool(body, "capture", false))
            {
                foreach (var kv in Capture(doc, body)) result[kv.Key] = kv.Value;
            }
            return result;
        }

        // Moves every ungrouped result of a test into a group named after a key
        // computed from its items: the model file, the level, or a property.
        public static object ClashAutoGroup(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            DocumentClash dc = doc.GetClash();
            string testName = GetString(body, "test_name", null)
                ?? throw new ArgumentException("test_name is required");
            string mode = (GetString(body, "group_by", "model") ?? "model").Trim().ToLowerInvariant();
            string side = (GetString(body, "side", mode == "model" ? "both" : "a") ?? "a").Trim().ToLowerInvariant();
            string category = GetString(body, "category", null);
            string property = GetString(body, "property", null);
            string prefix = GetString(body, "prefix", "") ?? "";
            if (mode == "property" && (string.IsNullOrEmpty(category) || string.IsNullOrEmpty(property)))
            {
                throw new ArgumentException("group_by=property needs category and property (display names)");
            }
            if (mode != "model" && mode != "level" && mode != "property")
            {
                throw new ArgumentException("group_by must be model, level or property");
            }

            // ClashGroupCreate / ClashGroupAddClashes (reused below) only look at
            // tests directly under the Clash Detective root.
            bool topLevel = dc.TestsData.Value.TestsRoot.Children.Any(si =>
                si is ClashTest t && string.Equals(t.DisplayName, testName, StringComparison.OrdinalIgnoreCase));
            if (!topLevel)
            {
                RequireTestRef(dc, testName);   // throws "not found" if it does not exist at all
                throw new InvalidOperationException("auto-grouping works only for tests at the top level of Clash Detective");
            }
            var test = (ClashTest)dc.TestsData.ResolveReference(RequireTestRef(dc, testName));

            var byKey = new Dictionary<string, List<object>>(StringComparer.OrdinalIgnoreCase);
            int skipped = 0;
            foreach (SavedItem child in test.Children)
            {
                if (!(child is ClashResult r)) continue;
                string key = GroupKey(r, mode, side, category, property);
                if (string.IsNullOrWhiteSpace(key)) { skipped++; continue; }
                key = Regex.Replace(prefix + key.Trim(), @"\s+", " ");
                if (key.Length > 100) key = key.Substring(0, 100);
                if (!byKey.TryGetValue(key, out var names)) byKey[key] = names = new List<object>();
                names.Add(r.DisplayName);
            }

            var groups = new List<object>();
            foreach (var kv in byKey.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var groupBody = new Dictionary<string, object>
                {
                    ["test_name"] = testName,
                    ["group_name"] = kv.Key,
                    ["clash_names"] = kv.Value
                };
                bool existed = false;
                try { ClashGroupCreate(groupBody); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("already exists")) { existed = true; }
                ClashGroupAddClashes(groupBody);
                groups.Add(new { group = kv.Key, clashes = kv.Value.Count, existed });
            }

            return new
            {
                test_name = testName,
                group_by = mode,
                side,
                groups = groups.Count,
                grouped = byKey.Values.Sum(v => v.Count),
                leftUngrouped = skipped,
                details = groups
            };
        }

        public static object ClashReport(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            DocumentClash dc = doc.GetClash();
            string path = GetString(body, "path", null)
                ?? throw new ArgumentException("path is required (.csv or .html)");
            string only = GetString(body, "test_name", null);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".csv" && ext != ".html" && ext != ".htm")
            {
                throw new ArgumentException("path must end with .csv or .html");
            }
            string[] statuses = GetStringList(body, "statuses").Select(s => ParseResultStatus(s).ToString()).ToArray();

            var rows = new List<string[]>();
            var perTest = new List<object>();
            foreach (ClashTest t in EnumerateClashTests(dc.TestsData.Value.TestsRoot))
            {
                if (only != null && !string.Equals(t.DisplayName, only, StringComparison.OrdinalIgnoreCase)) continue;
                int n = 0;
                WalkResults(t, null, (r, g) =>
                {
                    if (statuses.Length > 0 && !statuses.Contains(r.Status.ToString())) return;
                    n++;
                    Point3D c = r.Center;
                    rows.Add(new[]
                    {
                        t.DisplayName, g ?? "", r.DisplayName, r.Status.ToString(),
                        r.Distance.ToString("0.###", CultureInfo.InvariantCulture),
                        AssigneeName(r.AssignedTo), r.Description ?? "",
                        ItemLabel(r.Item1) ?? "", ContainingFileName(r.Item1) ?? "",
                        ItemLabel(r.Item2) ?? "", ContainingFileName(r.Item2) ?? "",
                        c == null ? "" : string.Format(CultureInfo.InvariantCulture, "{0:0.###}; {1:0.###}; {2:0.###}", c.X, c.Y, c.Z),
                        FormatTime(r.CreatedTime) ?? "",
                        CommentsText(r.Comments)
                    });
                });
                perTest.Add(new { test = t.DisplayName, results = n });
            }
            if (only != null && perTest.Count == 0) throw new InvalidOperationException($"clash test '{only}' not found");

            string[] header =
            {
                "Test", "Group", "Clash", "Status", "Distance", "Assigned to", "Description",
                "Item 1", "Model 1", "Item 2", "Model 2", "Center (X; Y; Z)", "Created", "Comments"
            };
            EnsureDirectory(path);
            if (ext == ".csv") WriteCsv(path, header, rows);
            else WriteHtml(path, doc, header, rows);

            return new { path, rows = rows.Count, tests = perTest };
        }

        // ── Clash helpers ───────────────────────────────────────────────────

        private delegate void ResultEdit(DocumentClash dc, IClashResult result);

        // Shared body of status / assign / comment: resolve test + names, apply.
        private static object EditResults(Dictionary<string, object> body, ResultEdit edit, object applied)
        {
            var doc = RequireDocument();
            DocumentClash dc = doc.GetClash();
            string testName = GetString(body, "test_name", null)
                ?? throw new ArgumentException("test_name is required");
            List<string> names = GetStringList(body, "clash_names");
            if (names.Count == 0) throw new ArgumentException("clash_names is required (array of clash names)");

            SavedItemReference testRef = RequireTestRef(dc, testName);
            var refs = FindResultRefs(dc, testRef, names, out List<string> missing);

            int updated = 0;
            foreach (SavedItemReference r in refs)
            {
                if (dc.TestsData.ResolveReference(r) is IClashResult fresh)
                {
                    edit(dc, fresh);
                    updated++;
                }
            }
            return new { test_name = testName, updated, notFound = missing, applied };
        }

        private static SavedItemReference RequireTestRef(DocumentClash dc, string name)
        {
            foreach (ClashTest t in EnumerateClashTests(dc.TestsData.Value.TestsRoot))
            {
                if (string.Equals(t.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return dc.TestsData.CreateReference(t);
                }
            }
            throw new InvalidOperationException($"clash test '{name}' not found");
        }

        // References to results with the given names anywhere under the test.
        private static List<SavedItemReference> FindResultRefs(
            DocumentClash dc, SavedItemReference testRef, IEnumerable<string> names, out List<string> missing)
        {
            var wanted = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var refs = new List<SavedItemReference>();
            var test = (ClashTest)dc.TestsData.ResolveReference(testRef);

            WalkResults(test, null, (r, g) =>
            {
                if (wanted.Contains(r.DisplayName) && found.Add(r.DisplayName))
                {
                    refs.Add(dc.TestsData.CreateReference(r));
                }
            });
            missing = wanted.Where(n => !found.Contains(n)).ToList();
            return refs;
        }

        // Visit every ClashResult under node; group = name of the enclosing group.
        private static void WalkResults(GroupItem node, string group, Action<ClashResult, string> visit)
        {
            foreach (SavedItem child in node.Children)
            {
                if (child is ClashResult r) visit(r, group);
                else if (child is ClashResultGroup g) WalkResults(g, g.DisplayName, visit);
                else if (child is GroupItem gi) WalkResults(gi, group, visit);
            }
        }

        private static object DescribeClashResult(ClashResult r, string group)
        {
            Point3D c = r.Center;
            return new
            {
                name = r.DisplayName,
                status = r.Status.ToString(),
                distance = r.Distance,
                group,
                assignedTo = AssigneeName(r.AssignedTo),
                description = string.IsNullOrEmpty(r.Description) ? null : r.Description,
                created = FormatTime(r.CreatedTime),
                comments = r.Comments?.Count ?? 0,
                center = c == null ? null : new[] { c.X, c.Y, c.Z },
                item1 = ItemLabel(r.Item1),
                model1 = ContainingFileName(r.Item1),
                item2 = ItemLabel(r.Item2),
                model2 = ContainingFileName(r.Item2)
            };
        }

        // Clash items are often unnamed geometry nodes (IFC); name them after
        // the nearest named ancestor, with its class: "Wall-042 (IfcWall)".
        private static string ItemLabel(ModelItem item)
        {
            for (ModelItem cur = item; cur != null; cur = cur.Parent)
            {
                if (cur.HasModel) break;
                if (!string.IsNullOrWhiteSpace(cur.DisplayName))
                {
                    string cls = cur.ClassDisplayName;
                    return string.IsNullOrEmpty(cls) ? cur.DisplayName : $"{cur.DisplayName} ({cls})";
                }
            }
            return item?.ClassDisplayName;
        }

        private static ModelItemCollection ResultSide(ModelItemCollection selection, ModelItem item)
        {
            if (selection != null && selection.Count > 0) return selection;
            var c = new ModelItemCollection();
            if (item != null) c.Add(item);
            return c;
        }

        private static string GroupKey(ClashResult r, string mode, string side, string category, string property)
        {
            string KeyFor(ModelItem item)
            {
                if (item == null) return null;
                switch (mode)
                {
                    case "model": return ContainingFileName(item);
                    case "level": return LevelOf(item);
                    default: return PropertyOnItemOrAncestors(item, category, property);
                }
            }

            string a = KeyFor(r.Item1), b = KeyFor(r.Item2);
            switch (side)
            {
                case "b": return b;
                case "both":
                    if (a == null && b == null) return null;
                    return $"{a ?? "?"} × {b ?? "?"}";
                default: return a;
            }
        }

        // Level heuristic: the nearest ancestor that looks like a storey/level
        // node (IFC: IfcBuildingStorey; Revit NWC: level layers), else an
        // Element/Level-style property.
        private static readonly Regex LevelClass = new Regex(
            "storey|level|layer|geschoss|ebene|étage|этаж", RegexOptions.IgnoreCase);

        private static string LevelOf(ModelItem item)
        {
            for (ModelItem cur = item?.Parent; cur != null; cur = cur.Parent)
            {
                if (cur.HasModel) break;
                if (LevelClass.IsMatch(cur.ClassDisplayName ?? "")) return cur.DisplayName;
            }
            return PropertyOnItemOrAncestors(item, "Element", "Level")
                ?? PropertyOnItemOrAncestors(item, "Item", "Layer");
        }

        private static string PropertyOnItemOrAncestors(ModelItem item, string category, string property)
        {
            for (ModelItem cur = item; cur != null; cur = cur.Parent)
            {
                DataProperty p = cur.PropertyCategories.FindPropertyByDisplayName(category, property);
                if (p != null)
                {
                    string v;
                    try { v = p.Value.ToDisplayString(); } catch { v = null; }
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
                if (cur.HasModel) break;
            }
            return null;
        }

        private static ClashResultStatus ParseResultStatus(string s)
        {
            if (!string.IsNullOrWhiteSpace(s) && Enum.TryParse(s.Trim(), true, out ClashResultStatus v)) return v;
            throw new ArgumentException($"unknown status '{s}'. Valid: New, Active, Reviewed, Approved, Resolved.");
        }

        private static CommentStatus ParseCommentStatus(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return CommentStatus.New;
            if (Enum.TryParse(s.Trim(), true, out CommentStatus v)) return v;
            throw new ArgumentException($"unknown comment_status '{s}'. Valid: New, Active, Approved, Resolved.");
        }

        private static string AssigneeName(Assignee a)
        {
            return a != null && a.Assigned && !string.IsNullOrEmpty(a.DisplayName) ? a.DisplayName : null;
        }

        private static string FormatTime(DateTime? t)
        {
            return t?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        private static string CommentsText(CommentCollection comments)
        {
            if (comments == null || comments.Count == 0) return "";
            return string.Join(" | ", comments.Cast<Comment>().Select(c =>
                $"{FormatTime(c.CreationDate)} {c.Author}: {c.Body}".Trim()));
        }

        private static void WriteCsv(string path, string[] header, List<string[]> rows)
        {
            string Cell(string v) => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", header.Select(Cell)));
            foreach (var row in rows) sb.AppendLine(string.Join(",", row.Select(Cell)));
            // BOM so Excel opens UTF-8 (umlauts, Cyrillic) correctly.
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static void WriteHtml(string path, Document doc, string[] header, List<string[]> rows)
        {
            string E(string v) => WebUtility.HtmlEncode(v ?? "");
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><title>Clash report</title><style>")
              .Append("body{font:13px Segoe UI,Arial,sans-serif;margin:24px;color:#222}")
              .Append("table{border-collapse:collapse;width:100%}th,td{border:1px solid #ccc;padding:4px 6px;vertical-align:top}")
              .Append("th{background:#f0f0f0;text-align:left;position:sticky;top:0}tr:nth-child(even){background:#fafafa}")
              .Append("</style></head><body>")
              .Append("<h1>Clash report</h1><p>")
              .Append(E(doc.Title)).Append(" &middot; ").Append(E(doc.FileName))
              .Append(" &middot; ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture))
              .Append(" &middot; ").Append(rows.Count).Append(" results</p><table><tr>");
            foreach (string h in header) sb.Append("<th>").Append(E(h)).Append("</th>");
            sb.Append("</tr>");
            foreach (var row in rows)
            {
                sb.Append("<tr>");
                foreach (string v in row) sb.Append("<td>").Append(E(v)).Append("</td>");
                sb.Append("</tr>");
            }
            sb.Append("</table></body></html>");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
