// =============================================================================
//  PropertyHandlers.cs — what properties a model has, and what values
// =============================================================================
//  /properties/names    categories and properties with item counts, data type
//                       and a sample value
//  /properties/values   distinct values of one property with counts
//
//  Both walk the items of a scope: the whole document, the current selection,
//  a saved set or some sub-model files. The walk stops at max_items or after
//  time_limit_s and says so (truncated): a full walk of a large federated
//  model is what crashed Navisworks before.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

using Autodesk.Navisworks.Api;

namespace NavisworksMcpAddin
{
    internal static partial class Handlers
    {
        // ── Scope ───────────────────────────────────────────────────────────

        private sealed class ItemScope
        {
            public ModelItemCollection Roots;
            public string Description;
        }

        // set_name > files > scope ("all" | "selection").
        private static ItemScope ResolveScope(Document doc, Dictionary<string, object> body)
        {
            string setName = GetString(body, "set_name", null);
            if (!string.IsNullOrEmpty(setName))
            {
                var items = ResolveNamedSet(doc, setName);
                if (items.Count == 0) throw new InvalidOperationException($"set '{setName}' is empty");
                return new ItemScope { Roots = items, Description = "set '" + setName + "'" };
            }

            List<string> files = GetStringList(body, "files");
            if (files.Count > 0)
            {
                var match = MatchFileNodes(doc, new Dictionary<string, object>
                {
                    ["names"] = files.Cast<object>().ToList(),
                    ["contains"] = GetBool(body, "contains", false)
                });
                if (match.Items.Count == 0)
                    throw new InvalidOperationException("no sub-model file matches: " + string.Join(", ", files));
                return new ItemScope { Roots = match.Items, Description = "files: " + string.Join(", ", files) };
            }

            string scope = (GetString(body, "scope", "all") ?? "all").Trim().ToLowerInvariant();
            if (scope == "selection")
            {
                var sel = doc.CurrentSelection.SelectedItems;
                if (sel.Count == 0) throw new InvalidOperationException("nothing is currently selected");
                return new ItemScope { Roots = new ModelItemCollection(sel), Description = "selection" };
            }
            if (scope != "all" && scope != "model")
                throw new ArgumentException($"unknown scope '{scope}'. Valid: all, selection (or pass set_name / files)");

            var roots = new ModelItemCollection();
            foreach (Model m in doc.Models)
            {
                if (m.RootItem != null) roots.Add(m.RootItem);
            }
            return new ItemScope { Roots = roots, Description = "all" };
        }

        // Bounded walk over the scope: every root with all its descendants,
        // each item once, stopping at maxItems or the time limit.
        private sealed class ScopeWalk
        {
            public int Scanned;
            public bool Truncated;
            public string StopReason;
        }

        private static IEnumerable<ModelItem> WalkScope(ItemScope scope, int maxItems, double timeLimitS, ScopeWalk stats)
        {
            var seen = new HashSet<ModelItem>();
            var clock = Stopwatch.StartNew();
            foreach (ModelItem root in scope.Roots)
            {
                foreach (ModelItem mi in root.DescendantsAndSelf)
                {
                    if (stats.Scanned >= maxItems)
                    {
                        stats.Truncated = true;
                        stats.StopReason = $"max_items {maxItems} reached";
                        yield break;
                    }
                    if (clock.Elapsed.TotalSeconds > timeLimitS)
                    {
                        stats.Truncated = true;
                        stats.StopReason = $"time_limit_s {timeLimitS:0.#} reached";
                        yield break;
                    }
                    if (!seen.Add(mi)) continue;
                    stats.Scanned++;
                    yield return mi;
                }
            }
        }

        private static bool NameMatches(string wanted, string displayName, string internalName)
        {
            return string.Equals(wanted, displayName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(wanted, internalName, StringComparison.OrdinalIgnoreCase);
        }

        private static string SafeDisplay(VariantData v, int maxLen = 200)
        {
            string s = FormatVariant(v);
            if (s.Length > maxLen) s = s.Substring(0, maxLen) + "…";
            return s;
        }

        // A property value as text. ToDisplayString works only for string
        // values (it throws for numbers, booleans, lengths...), so read each
        // type with its own accessor. Lengths, areas and volumes carry the
        // document's length unit; angles are shown in degrees.
        internal static string FormatVariant(VariantData v)
        {
            if (v == null) return "";
            try
            {
                switch (v.DataType)
                {
                    case VariantDataType.None: return "";
                    case VariantDataType.DisplayString: return v.ToDisplayString() ?? "";
                    case VariantDataType.IdentifierString: return v.ToIdentifierString() ?? "";
                    case VariantDataType.Boolean: return v.ToBoolean() ? "Yes" : "No";
                    case VariantDataType.Int32: return v.ToInt32().ToString(Inv);
                    case VariantDataType.Int64: return v.ToInt64().ToString(Inv);
                    case VariantDataType.Nat32: return v.ToNat32().ToString(Inv);
                    case VariantDataType.Nat64: return v.ToNat64().ToString(Inv);
                    case VariantDataType.Double: return Num(v.ToDouble());
                    case VariantDataType.DoubleLength: return Num(v.ToDoubleLength()) + " " + LengthUnit();
                    case VariantDataType.DoubleArea: return Num(v.ToDoubleArea()) + " " + LengthUnit() + "²";
                    case VariantDataType.DoubleVolume: return Num(v.ToDoubleVolume()) + " " + LengthUnit() + "³";
                    case VariantDataType.DoubleAngle: return Num(v.ToDoubleAngle() * 180.0 / Math.PI) + "°";
                    case VariantDataType.DateTime: return v.ToDateTime().ToString("yyyy-MM-dd HH:mm:ss", Inv);
                    case VariantDataType.NamedConstant: return v.ToNamedConstant()?.DisplayName ?? "";
                    case VariantDataType.Point3D:
                        Point3D p = v.ToPoint3D();
                        return $"{Num(p.X)}, {Num(p.Y)}, {Num(p.Z)}";
                    case VariantDataType.Point2D:
                        Point2D q = v.ToPoint2D();
                        return $"{Num(q.X)}, {Num(q.Y)}";
                    default: return v.ToString() ?? "";
                }
            }
            catch
            {
                return "(unreadable)";
            }
        }

        private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

        private static string Num(double d)
        {
            double r = Math.Round(d, 6);
            if (r == 0) r = 0; // no "-0"
            return r.ToString("0.######", Inv);
        }

        private static string LengthUnit()
        {
            Units u;
            try { u = Autodesk.Navisworks.Api.Application.ActiveDocument.Units; }
            catch { return ""; }
            switch (u)
            {
                case Units.Meters: return "m";
                case Units.Centimeters: return "cm";
                case Units.Millimeters: return "mm";
                case Units.Kilometers: return "km";
                case Units.Micrometers: return "µm";
                case Units.Feet: return "ft";
                case Units.Inches: return "in";
                case Units.Yards: return "yd";
                case Units.Miles: return "mi";
                case Units.Mils: return "mil";
                case Units.Microinches: return "µin";
                default: return u.ToString();
            }
        }

        private static string SafeType(VariantData v)
        {
            try { return v.DataType.ToString(); } catch { return "Unknown"; }
        }

        // ── /properties/names ───────────────────────────────────────────────

        private sealed class PropStat
        {
            public string Name, InternalName, DataType, Sample;
            public int Count;
        }

        private sealed class CatStat
        {
            public string Name, InternalName;
            public int Count;
            public readonly Dictionary<string, PropStat> Props = new Dictionary<string, PropStat>();
            public readonly List<PropStat> Order = new List<PropStat>();
        }

        public static object PropertyNames(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            ItemScope scope = ResolveScope(doc, body);
            string onlyCategory = GetString(body, "category", null);
            int maxItems = Math.Max(1, GetInt(body, "max_items", 20000));
            double timeLimit = Math.Max(1, GetDouble(body, "time_limit_s", 30));

            var cats = new Dictionary<string, CatStat>();
            var order = new List<CatStat>();
            var walk = new ScopeWalk();

            foreach (ModelItem mi in WalkScope(scope, maxItems, timeLimit, walk))
            {
                foreach (PropertyCategory cat in mi.PropertyCategories)
                {
                    if (onlyCategory != null && !NameMatches(onlyCategory, cat.DisplayName, cat.Name)) continue;

                    string ckey = cat.Name + "\u0001" + cat.DisplayName;
                    if (!cats.TryGetValue(ckey, out CatStat cs))
                    {
                        cs = new CatStat { Name = cat.DisplayName, InternalName = cat.Name };
                        cats[ckey] = cs;
                        order.Add(cs);
                    }
                    cs.Count++;

                    foreach (DataProperty p in cat.Properties)
                    {
                        string pkey = p.Name + "\u0001" + p.DisplayName;
                        if (!cs.Props.TryGetValue(pkey, out PropStat ps))
                        {
                            ps = new PropStat
                            {
                                Name = p.DisplayName,
                                InternalName = p.Name,
                                DataType = SafeType(p.Value),
                                Sample = SafeDisplay(p.Value, 80)
                            };
                            cs.Props[pkey] = ps;
                            cs.Order.Add(ps);
                        }
                        ps.Count++;
                    }
                }
            }

            if (onlyCategory != null && order.Count == 0)
            {
                throw new InvalidOperationException(
                    $"no category '{onlyCategory}' in {scope.Description}" +
                    (walk.Truncated ? $" (only {walk.Scanned} items scanned: {walk.StopReason})" : ""));
            }

            return new
            {
                scope = scope.Description,
                itemsScanned = walk.Scanned,
                truncated = walk.Truncated,
                stopReason = walk.StopReason,
                categoryCount = order.Count,
                categories = order
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(c => new
                    {
                        name = c.Name,
                        internalName = c.InternalName,
                        items = c.Count,
                        properties = c.Order
                            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                            .Select(p => new
                            {
                                name = p.Name,
                                internalName = p.InternalName,
                                items = p.Count,
                                dataType = p.DataType,
                                sample = p.Sample
                            }).ToList()
                    }).ToList()
            };
        }

        // ── /properties/values ──────────────────────────────────────────────

        public static object PropertyValues(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            string category = GetString(body, "category", null)
                ?? throw new ArgumentException("category is required (see list_property_names)");
            string property = GetString(body, "property", null)
                ?? throw new ArgumentException("property is required (see list_property_names)");
            ItemScope scope = ResolveScope(doc, body);
            int maxItems = Math.Max(1, GetInt(body, "max_items", 50000));
            double timeLimit = Math.Max(1, GetDouble(body, "time_limit_s", 30));
            int maxValues = Math.Max(1, GetInt(body, "max_values", 200));

            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            var dataTypes = new HashSet<string>();
            int withProperty = 0;
            bool numeric = true;
            double min = double.MaxValue, max = double.MinValue;
            string minText = null, maxText = null;
            var walk = new ScopeWalk();

            foreach (ModelItem mi in WalkScope(scope, maxItems, timeLimit, walk))
            {
                DataProperty hit = null;
                foreach (PropertyCategory cat in mi.PropertyCategories)
                {
                    if (!NameMatches(category, cat.DisplayName, cat.Name)) continue;
                    foreach (DataProperty p in cat.Properties)
                    {
                        if (NameMatches(property, p.DisplayName, p.Name)) { hit = p; break; }
                    }
                    if (hit != null) break;
                }
                if (hit == null) continue;

                withProperty++;
                VariantData v = hit.Value;
                string text = SafeDisplay(v);
                counts.TryGetValue(text, out int n);
                counts[text] = n + 1;
                dataTypes.Add(SafeType(v));

                if (numeric)
                {
                    double d;
                    try
                    {
                        if (v.IsAnyDouble) d = v.ToAnyDouble();
                        else if (v.IsInt32) d = v.ToInt32();
                        else if (v.IsInt64) d = v.ToInt64();
                        else if (v.IsNat32) d = v.ToNat32();
                        else if (v.IsNat64) d = v.ToNat64();
                        else { numeric = false; continue; }
                    }
                    catch { numeric = false; continue; }
                    if (d < min) { min = d; minText = text; }
                    if (d > max) { max = d; maxText = text; }
                }
            }

            var values = counts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(maxValues)
                .Select(kv => new { value = kv.Key, items = kv.Value })
                .ToList();

            return new
            {
                scope = scope.Description,
                category,
                property,
                itemsScanned = walk.Scanned,
                itemsWithProperty = withProperty,
                truncated = walk.Truncated,
                stopReason = walk.StopReason,
                dataTypes = dataTypes.ToList(),
                distinctValues = counts.Count,
                valuesTruncated = counts.Count > values.Count,
                // Smallest / largest value as Navisworks displays it (with units).
                min = numeric && withProperty > 0 ? minText : null,
                max = numeric && withProperty > 0 ? maxText : null,
                values
            };
        }
    }
}
