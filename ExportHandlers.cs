// =============================================================================
//  ExportHandlers.cs — properties of many items to CSV
// =============================================================================
//  /properties/export   one row per item: File, Path, Name, Class and the
//                       chosen properties (or every property of the chosen
//                       categories), values as list_property_values shows
//                       them (lengths with the document unit)
//
//  Items: the scope (whole document, selection, set, files) walked like
//  list_property_names, bounded by max_items / time_limit_s. level picks
//  which of them become rows; require_property keeps only items that have
//  a property (e.g. only real elements, not their geometry nodes).
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Autodesk.Navisworks.Api;

namespace NavisworksMcpAddin
{
    internal static partial class Handlers
    {
        private sealed class Column
        {
            public string Category, Property;   // as asked (display or internal name)
            public string Header;
        }

        public static object PropertiesExport(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            string path = GetString(body, "path", null);
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required (a .csv file)");
            path = Path.GetFullPath(path);
            if (!string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("path must end with .csv");
            if (File.Exists(path) && !GetBool(body, "overwrite", false))
                throw new InvalidOperationException($"{path} exists (pass overwrite=true)");

            ItemScope scope = ResolveScope(doc, body);
            int maxItems = Math.Max(1, GetInt(body, "max_items", 50000));
            double timeLimit = Math.Max(1, GetDouble(body, "time_limit_s", 120));
            int maxColumns = Math.Max(1, GetInt(body, "max_columns", 200));
            string level = (GetString(body, "level", "all") ?? "all").Trim().ToLowerInvariant();
            if (level != "all" && level != "geometry" && level != "top")
                throw new ArgumentException($"unknown level '{level}'. Valid: all, geometry, top");
            string delimiter = GetString(body, "delimiter", ",");
            if (string.IsNullOrEmpty(delimiter)) delimiter = ",";
            if (delimiter == "\\t" || delimiter.Equals("tab", StringComparison.OrdinalIgnoreCase)) delimiter = "\t";

            // "Category/Property" -> (category, property)
            (string, string) SplitProp(string s, string what)
            {
                int k = (s ?? "").IndexOf('/');
                if (k <= 0 || k == s.Length - 1) throw new ArgumentException($"{what} '{s}' must be 'Category/Property'");
                return (s.Substring(0, k).Trim(), s.Substring(k + 1).Trim());
            }

            List<string> props = GetStringList(body, "properties");
            List<string> cats = GetStringList(body, "categories");
            string require = GetString(body, "require_property", null);
            (string cat, string prop)? req = string.IsNullOrWhiteSpace(require) ? ((string, string)?)null : SplitProp(require, "require_property");

            var columns = props.Select(s => { var (c, p) = SplitProp(s, "property"); return new Column { Category = c, Property = p, Header = c + "/" + p }; }).ToList();
            bool autoColumns = columns.Count == 0;
            var autoIndex = new Dictionary<string, Column>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();

            // Which items become rows.
            IEnumerable<ModelItem> source;
            var walk = new ScopeWalk();
            if (level == "top")
            {
                source = scope.Roots.Take(maxItems);
                walk.Scanned = Math.Min(scope.Roots.Count, maxItems);
                walk.Truncated = scope.Roots.Count > maxItems;
                if (walk.Truncated) walk.StopReason = $"max_items {maxItems} reached";
            }
            else
            {
                source = WalkScope(scope, maxItems, timeLimit, walk);
            }

            var rows = new List<(ModelItem item, Dictionary<string, string> values)>();
            foreach (ModelItem mi in source)
            {
                if (level == "geometry" && !mi.HasGeometry) continue;

                // Read the item's properties once: key "category\u0001property" by
                // both display and internal names.
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                bool hasRequired = req == null;
                foreach (PropertyCategory cat in mi.PropertyCategories)
                {
                    bool wantCatAuto = autoColumns && (cats.Count == 0 || cats.Any(c => NameMatches(c, cat.DisplayName, cat.Name)));
                    foreach (DataProperty p in cat.Properties)
                    {
                        if (req != null && !hasRequired && NameMatches(req.Value.cat, cat.DisplayName, cat.Name)
                            && NameMatches(req.Value.prop, p.DisplayName, p.Name)) hasRequired = true;

                        bool needed = wantCatAuto || columns.Any(col => NameMatches(col.Category, cat.DisplayName, cat.Name)
                                                                       && NameMatches(col.Property, p.DisplayName, p.Name));
                        if (!needed) continue;
                        string v = SafeDisplay(p.Value, 2000);
                        values[cat.DisplayName + "\u0001" + p.DisplayName] = v;
                        values[cat.Name + "\u0001" + p.Name] = v;

                        if (wantCatAuto)
                        {
                            string colName = cat.DisplayName + "/" + p.DisplayName;
                            if (!autoIndex.ContainsKey(colName))
                            {
                                if (autoIndex.Count >= maxColumns)
                                {
                                    if (warnings.Count == 0)
                                        warnings.Add($"more than {maxColumns} properties: the rest are left out (pass properties / categories or raise max_columns)");
                                }
                                else
                                {
                                    autoIndex[colName] = new Column { Category = cat.DisplayName, Property = p.DisplayName, Header = colName };
                                }
                            }
                        }
                    }
                }
                if (!hasRequired) continue;
                rows.Add((mi, values));
            }

            if (autoColumns)
                columns = autoIndex.Values.OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
                                          .ThenBy(c => c.Property, StringComparer.OrdinalIgnoreCase).ToList();

            string[] header = new[] { "File", "Path", "Name", "Class" }.Concat(columns.Select(c => c.Header)).ToArray();
            var sb = new StringBuilder();
            string Cell(string v)
            {
                v = v ?? "";
                bool quote = v.Contains(delimiter) || v.Contains("\"") || v.Contains("\n") || v.Contains("\r");
                return quote ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
            }
            sb.Append(string.Join(delimiter, header.Select(Cell))).Append("\r\n");
            var sample = new List<Dictionary<string, string>>();
            foreach (var (item, values) in rows)
            {
                var cells = new List<string>
                {
                    ContainingFileName(item) ?? "",
                    ItemPath(item),
                    item.DisplayName ?? "",
                    item.ClassDisplayName ?? ""
                };
                foreach (Column c in columns)
                {
                    values.TryGetValue(c.Category + "\u0001" + c.Property, out string v);
                    cells.Add(v ?? "");
                }
                sb.Append(string.Join(delimiter, cells.Select(Cell))).Append("\r\n");
                if (sample.Count < 3)
                    sample.Add(header.Zip(cells, (h, v) => new { h, v }).Where(x => x.v.Length > 0).Take(8)
                                     .ToDictionary(x => x.h, x => x.v.Length > 80 ? x.v.Substring(0, 80) + "…" : x.v));
            }

            EnsureDirectory(path);
            // BOM so Excel opens UTF-8 (umlauts, Cyrillic) correctly.
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));

            var missing = props.Count == 0 ? null : columns
                .Where(c => rows.All(r => !r.values.ContainsKey(c.Category + "\u0001" + c.Property)))
                .Select(c => c.Header).ToList();
            if (missing != null && missing.Count > 0)
                warnings.Add("no item has: " + string.Join(", ", missing) + " (check list_property_names)");

            return new
            {
                path,
                bytes = new FileInfo(path).Length,
                rows = rows.Count,
                columns = header.Length,
                header = header.Length > 30 ? header.Take(30).Concat(new[] { "…" }).ToArray() : header,
                scope = scope.Description,
                level,
                itemsScanned = walk.Scanned,
                truncated = walk.Truncated,
                stopReason = walk.StopReason,
                warnings = warnings.Count > 0 ? warnings : null,
                sample
            };
        }

        // "File > Level > Element" from the model root down to the item.
        private static string ItemPath(ModelItem item)
        {
            var parts = new List<string>();
            for (ModelItem cur = item; cur != null; cur = cur.Parent)
            {
                parts.Add(string.IsNullOrEmpty(cur.DisplayName) ? cur.ClassDisplayName ?? "" : cur.DisplayName);
                if (parts.Count > 64) break;
            }
            parts.Reverse();
            return string.Join(" > ", parts);
        }
    }
}
