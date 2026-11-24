// =============================================================================
//  SearchHandlers.cs — Find Items with several conditions (AND / OR)
// =============================================================================
//  /elements/find          find items; optionally select them or save the
//                          search as a search set
//  /sets/search/create     save a search set with the same conditions
//
//  Conditions are Navisworks' own (SearchCondition), so a saved search set
//  behaves exactly like one made in the Find Items window. A condition:
//    { category, property, operator, value, negate }
//  operators: equals, not_equals, >, >=, <, <=, contains, wildcard,
//             starts_with, ends_with, has_property, not_has_property,
//             has_category, not_has_category
//  "conditions" are ANDed; "any_of" is a list of AND-groups that are ORed
//  (each group also gets the common "conditions"). The old single-condition
//  call (category / property / operator / value) still works.
//
//  Values are converted to the property's own type (found with a native
//  FindFirst): "-0.07 m" for a length, "45°" for an angle, yes/no for a
//  boolean. Equality of decimal numbers allows for rounding: equals becomes
//  a narrow range, not_equals the two sides of it.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

using Autodesk.Navisworks.Api;

namespace NavisworksMcpAddin
{
    internal static partial class Handlers
    {
        // ── Parsed input ────────────────────────────────────────────────────

        private sealed class CondSpec
        {
            public string Category, Property, Op, Value, Text;
            public bool Negate;
        }

        private sealed class SearchSpec
        {
            public Search Search;
            public List<string> Readable = new List<string>();
            public List<string> Warnings = new List<string>();
            public string Scope;
        }

        // ── /elements/find ──────────────────────────────────────────────────

        public static object ElementsFind(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            SearchSpec spec = BuildSearch(doc, body);

            int limit = Math.Max(0, GetInt(body, "limit", 50));
            int maxResults = Math.Max(1, GetInt(body, "max_results", 100000));
            double timeLimit = Math.Max(1, GetDouble(body, "time_limit_s", 60));
            bool select = GetBool(body, "select", false);
            string saveAs = GetString(body, "save_as_search_set", null);

            var found = RunSearch(doc, spec.Search, maxResults, timeLimit, out bool truncated, out string stopReason);

            if (select) doc.CurrentSelection.CopyFrom(found);

            object searchSet = null;
            if (!string.IsNullOrWhiteSpace(saveAs)) searchSet = SaveSearchSet(doc, saveAs.Trim(), spec.Search, body);

            return new
            {
                count = found.Count,
                truncated,
                stopReason,
                scope = spec.Scope,
                conditions = spec.Readable,
                warnings = spec.Warnings.Count > 0 ? spec.Warnings : null,
                sampled = Math.Min(limit, found.Count),
                items = found.Take(limit).Select(it => new
                {
                    displayName = it.DisplayName,
                    className = it.ClassDisplayName,
                    file = ContainingFileName(it)
                }).ToList(),
                selected = select,
                searchSet
            };
        }

        // ── /sets/search/create ─────────────────────────────────────────────

        public static object SearchSetCreate(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            string name = GetString(body, "name", null);
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");

            SearchSpec spec = BuildSearch(doc, body);
            object saved = SaveSearchSet(doc, name.Trim(), spec.Search, body);
            var found = RunSearch(doc, spec.Search, Math.Max(1, GetInt(body, "max_results", 100000)),
                Math.Max(1, GetDouble(body, "time_limit_s", 60)), out bool truncated, out string stopReason);

            return new
            {
                name = name.Trim(),
                matches = found.Count,
                truncated,
                stopReason,
                scope = spec.Scope,
                conditions = spec.Readable,
                warnings = spec.Warnings.Count > 0 ? spec.Warnings : null,
                saved
            };
        }

        private static object SaveSearchSet(Document doc, string name, Search search, Dictionary<string, object> body)
        {
            bool replace = GetBool(body, "replace", false);
            SavedItem existing = FindSetByName(doc.SelectionSets.RootItem, name);
            var set = new SelectionSet(new Search(search)) { DisplayName = name };
            if (existing != null)
            {
                if (!replace)
                    throw new InvalidOperationException($"a set named '{name}' already exists (pass replace=true to overwrite it)");
                GroupItem parent = existing.Parent ?? doc.SelectionSets.RootItem;
                doc.SelectionSets.ReplaceWithCopy(parent, parent.Children.IndexOf(existing), set);
                return new { name, replaced = true };
            }
            doc.SelectionSets.AddCopy(set);
            return new { name, replaced = false };
        }

        // Bounded run: FindIncremental lets us stop at max_results / time.
        private static ModelItemCollection RunSearch(Document doc, Search search, int maxResults, double timeLimitS,
                                                     out bool truncated, out string stopReason)
        {
            var found = new ModelItemCollection();
            var clock = Stopwatch.StartNew();
            truncated = false;
            stopReason = null;
            foreach (ModelItem mi in search.FindIncremental(doc, false))
            {
                if (found.Count >= maxResults)
                {
                    truncated = true;
                    stopReason = $"max_results {maxResults} reached";
                    break;
                }
                if (clock.Elapsed.TotalSeconds > timeLimitS)
                {
                    truncated = true;
                    stopReason = $"time_limit_s {timeLimitS:0.#} reached";
                    break;
                }
                found.Add(mi);
            }
            return found;
        }

        // ── Building the Search ─────────────────────────────────────────────

        private static SearchSpec BuildSearch(Document doc, Dictionary<string, object> body)
        {
            var spec = new SearchSpec();

            List<CondSpec> common = ParseConditionList(body, "conditions");
            var groups = new List<List<CondSpec>>();
            if (body != null && body.TryGetValue("any_of", out object rawGroups) && rawGroups is List<object> gl)
            {
                int gi = 0;
                foreach (object g in gl)
                {
                    gi++;
                    List<CondSpec> group = g is List<object> lst
                        ? lst.Select((c, k) => ParseCondition(c as Dictionary<string, object>, $"any_of[{gi - 1}][{k}]")).ToList()
                        : g is Dictionary<string, object> one
                            ? new List<CondSpec> { ParseCondition(one, $"any_of[{gi - 1}]") }
                            : throw new ArgumentException("any_of must be a list of condition lists");
                    if (group.Count > 0) groups.Add(group);
                }
            }

            // Old single-condition call.
            if (common.Count == 0 && groups.Count == 0 && body != null && body.ContainsKey("value"))
            {
                common.Add(ParseCondition(new Dictionary<string, object>
                {
                    ["category"] = GetString(body, "category", "Item"),
                    ["property"] = GetString(body, "property", "Name"),
                    ["operator"] = GetString(body, "operator", "contains"),
                    ["value"] = body["value"]
                }, "condition"));
            }
            if (common.Count == 0 && groups.Count == 0)
                throw new ArgumentException("pass conditions (and/or any_of), or category/property/operator/value");

            bool ignoreCase = GetBool(body, "ignore_case", true);

            // Each OR-group = common conditions + the group's own. Some conditions
            // (not_equals on decimals) expand into alternatives -> more groups.
            var andGroups = groups.Count == 0
                ? new List<List<CondSpec>> { common }
                : groups.Select(g => common.Concat(g).ToList()).ToList();

            var typeCache = new Dictionary<string, PropInfo>(StringComparer.OrdinalIgnoreCase);
            var finalGroups = new List<List<SearchCondition>>();
            foreach (var group in andGroups)
            {
                var alternatives = new List<List<SearchCondition>> { new List<SearchCondition>() };
                foreach (CondSpec c in group)
                {
                    List<List<SearchCondition>> options = BuildConditions(doc, c, ignoreCase, typeCache, spec.Warnings);
                    alternatives = alternatives
                        .SelectMany(a => options.Select(o => a.Concat(o).ToList()))
                        .ToList();
                }
                finalGroups.AddRange(alternatives);
            }

            var search = new Search();
            ItemScope scope = ResolveSearchScope(doc, body, search);
            search.Locations = SearchLocations.DescendantsAndSelf;
            search.PruneBelowMatch = GetBool(body, "prune_below_match", true);
            if (finalGroups.Count == 1)
            {
                foreach (SearchCondition sc in finalGroups[0]) search.SearchConditions.Add(sc);
            }
            else
            {
                foreach (var g in finalGroups) search.SearchConditions.AddGroup(g);
            }

            spec.Search = search;
            spec.Scope = scope.Description;
            if (groups.Count == 0)
            {
                spec.Readable.Add(string.Join(" AND ", common.Select(c => c.Text)));
            }
            else
            {
                foreach (var g in andGroups) spec.Readable.Add(string.Join(" AND ", g.Select(c => c.Text)));
            }
            if (ignoreCase) spec.Readable.Add("(text compared ignoring case)");
            return spec;
        }

        // Scope of a search: whole document (dynamic, like Find Items with
        // nothing selected) or fixed items: selection, set, files.
        private static ItemScope ResolveSearchScope(Document doc, Dictionary<string, object> body, Search search)
        {
            bool fixedScope = !string.IsNullOrEmpty(GetString(body, "set_name", null))
                || GetStringList(body, "files").Count > 0
                || string.Equals((GetString(body, "scope", "all") ?? "").Trim(), "selection", StringComparison.OrdinalIgnoreCase);
            if (!fixedScope)
            {
                string s = (GetString(body, "scope", "all") ?? "all").Trim().ToLowerInvariant();
                if (s != "all" && s != "model")
                    throw new ArgumentException($"unknown scope '{s}'. Valid: all, selection (or pass set_name / files)");
                search.Selection.SelectAll();
                return new ItemScope { Description = "all" };
            }
            ItemScope scope = ResolveScope(doc, body);
            search.Selection.CopyFrom(scope.Roots);
            return scope;
        }

        private static List<CondSpec> ParseConditionList(Dictionary<string, object> body, string key)
        {
            var list = new List<CondSpec>();
            if (body == null || !body.TryGetValue(key, out object raw) || raw == null) return list;
            if (raw is Dictionary<string, object> one) { list.Add(ParseCondition(one, key)); return list; }
            if (!(raw is List<object> items)) throw new ArgumentException($"{key} must be a list of conditions");
            int i = 0;
            foreach (object o in items)
            {
                list.Add(ParseCondition(o as Dictionary<string, object>, $"{key}[{i}]"));
                i++;
            }
            return list;
        }

        private static CondSpec ParseCondition(Dictionary<string, object> c, string where)
        {
            if (c == null) throw new ArgumentException($"{where}: a condition must be an object");
            var spec = new CondSpec
            {
                Category = GetString(c, "category", null),
                Property = GetString(c, "property", null),
                Op = NormalizeOp(GetString(c, "operator", null) ?? (c.ContainsKey("value") ? "equals" : "has_property")),
                Value = RawToString(c.TryGetValue("value", out object v) ? v : null),
                Negate = GetBool(c, "negate", false)
            };
            if (string.IsNullOrWhiteSpace(spec.Category))
                throw new ArgumentException($"{where}: category is required (see list_property_names)");
            bool categoryOnly = spec.Op == "has_category" || spec.Op == "not_has_category";
            if (!categoryOnly && string.IsNullOrWhiteSpace(spec.Property))
                throw new ArgumentException($"{where}: property is required (see list_property_names)");
            bool needsValue = !(categoryOnly || spec.Op == "has_property" || spec.Op == "not_has_property");
            if (needsValue && spec.Value == null)
                throw new ArgumentException($"{where}: value is required for operator {spec.Op}");

            string target = categoryOnly ? spec.Category : spec.Category + "/" + spec.Property;
            spec.Text = (spec.Negate ? "NOT " : "") + target + " " + OpSymbol(spec.Op)
                        + (needsValue ? " '" + spec.Value + "'" : "");
            return spec;
        }

        private static string RawToString(object v)
        {
            if (v == null) return null;
            if (v is double d) return d.ToString("R", CultureInfo.InvariantCulture);
            if (v is bool b) return b ? "true" : "false";
            return v.ToString();
        }

        private static string NormalizeOp(string op)
        {
            string o = (op ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");
            switch (o)
            {
                case "=": case "==": case "equals": case "equal": case "eq": return "equals";
                case "!=": case "<>": case "not_equals": case "notequals": case "not_equal": case "ne": return "not_equals";
                case ">": case "gt": case "greater_than": return ">";
                case ">=": case "ge": case "gte": case "greater_or_equal": return ">=";
                case "<": case "lt": case "less_than": return "<";
                case "<=": case "le": case "lte": case "less_or_equal": return "<=";
                case "contains": case "~": return "contains";
                case "wildcard": case "like": return "wildcard";
                case "startswith": case "starts_with": return "starts_with";
                case "endswith": case "ends_with": return "ends_with";
                case "has_property": case "defined": case "exists": return "has_property";
                case "not_has_property": case "undefined": case "not_exists": return "not_has_property";
                case "has_category": return "has_category";
                case "not_has_category": return "not_has_category";
                default:
                    throw new ArgumentException($"unknown operator '{op}'. Valid: equals, not_equals, >, >=, <, <=, " +
                        "contains, wildcard, starts_with, ends_with, has_property, not_has_property, has_category, not_has_category");
            }
        }

        private static string OpSymbol(string op)
        {
            switch (op)
            {
                case "equals": return "=";
                case "not_equals": return "!=";
                default: return op;
            }
        }

        // ── One condition -> SearchConditions ───────────────────────────────
        // Returns alternatives (OR) of AND-lists; usually one list of one.

        private sealed class PropInfo
        {
            public bool Found;
            public bool ByDisplayName;
            public string Category, Property; // exact names to use
            public VariantDataType Type;
        }

        private static List<List<SearchCondition>> BuildConditions(Document doc, CondSpec c, bool ignoreCase,
            Dictionary<string, PropInfo> cache, List<string> warnings)
        {
            List<List<SearchCondition>> One(SearchCondition sc) =>
                new List<List<SearchCondition>> { new List<SearchCondition> { c.Negate ? sc.Negate() : sc } };

            if (c.Op == "has_category" || c.Op == "not_has_category")
            {
                string catName = ResolveCategoryName(doc, c.Category, out bool byDisplay);
                if (catName == null) warnings.Add($"no item has category '{c.Category}'");
                SearchCondition sc = byDisplay || catName == null
                    ? SearchCondition.HasCategoryByDisplayName(catName ?? c.Category)
                    : SearchCondition.HasCategoryByName(catName);
                if (c.Op == "not_has_category") sc = sc.Negate();
                return One(sc);
            }

            PropInfo info = LookupProperty(doc, c.Category, c.Property, cache);
            if (!info.Found)
                warnings.Add($"no item in the document has property '{c.Category}/{c.Property}' (check list_property_names)");

            SearchCondition baseCond = !info.Found || info.ByDisplayName
                ? SearchCondition.HasPropertyByDisplayName(info.Category ?? c.Category, info.Property ?? c.Property)
                : SearchCondition.HasPropertyByName(info.Category, info.Property);

            switch (c.Op)
            {
                case "has_property": return One(baseCond);
                case "not_has_property": return One(baseCond.Negate());
            }

            bool isString = !info.Found || info.Type == VariantDataType.DisplayString;
            if (c.Op == "contains" || c.Op == "wildcard" || c.Op == "starts_with" || c.Op == "ends_with")
            {
                if (!isString)
                    throw new ArgumentException($"'{c.Category}/{c.Property}' is {info.Type}, not text: " +
                        "use equals, not_equals, >, >=, <, <=");
                SearchCondition sc;
                switch (c.Op)
                {
                    case "contains": sc = baseCond.DisplayStringContains(c.Value); break;
                    case "starts_with": sc = baseCond.DisplayStringWildcard(c.Value + "*"); break;
                    case "ends_with": sc = baseCond.DisplayStringWildcard("*" + c.Value); break;
                    default: sc = baseCond.DisplayStringWildcard(c.Value); break;
                }
                if (ignoreCase) sc = sc.IgnoreStringValueCase();
                return One(sc);
            }

            VariantDataType type = info.Found ? info.Type : VariantDataType.DisplayString;
            bool isDecimal = type == VariantDataType.Double || type == VariantDataType.DoubleLength
                || type == VariantDataType.DoubleArea || type == VariantDataType.DoubleVolume
                || type == VariantDataType.DoubleAngle;

            if (isDecimal && (c.Op == "equals" || c.Op == "not_equals"))
            {
                // Stored decimals carry noise (-0.069999999999999243 for -0.07):
                // compare within a small tolerance instead of exactly.
                double v = ParseNumber(c.Value, c);
                double eps = Math.Max(1e-6, Math.Abs(v) * 1e-9);
                VariantData lo = MakeValue(type, (v - eps).ToString("R", CultureInfo.InvariantCulture), c);
                VariantData hi = MakeValue(type, (v + eps).ToString("R", CultureInfo.InvariantCulture), c);
                bool wantEqual = (c.Op == "equals") != c.Negate;
                if (wantEqual)
                {
                    return new List<List<SearchCondition>> { new List<SearchCondition> {
                        baseCond.CompareWith(SearchConditionComparison.NumericGreaterThanOrEqual, lo),
                        baseCond.CompareWith(SearchConditionComparison.NumericLessThanOrEqual, hi) } };
                }
                return new List<List<SearchCondition>> {
                    new List<SearchCondition> { baseCond.CompareWith(SearchConditionComparison.NumericLessThan, lo) },
                    new List<SearchCondition> { baseCond.CompareWith(SearchConditionComparison.NumericGreaterThan, hi) } };
            }

            VariantData value = MakeValue(type, c.Value, c);
            SearchConditionComparison cmp;
            switch (c.Op)
            {
                case "equals": cmp = SearchConditionComparison.Equal; break;
                case "not_equals": cmp = SearchConditionComparison.NotEqual; break;
                case ">": cmp = SearchConditionComparison.NumericGreaterThan; break;
                case ">=": cmp = SearchConditionComparison.NumericGreaterThanOrEqual; break;
                case "<": cmp = SearchConditionComparison.NumericLessThan; break;
                case "<=": cmp = SearchConditionComparison.NumericLessThanOrEqual; break;
                default: throw new ArgumentException($"operator {c.Op} is not supported here");
            }
            if ((cmp != SearchConditionComparison.Equal && cmp != SearchConditionComparison.NotEqual)
                && !(isDecimal || type == VariantDataType.Int32 || type == VariantDataType.Int64
                     || type == VariantDataType.Nat32 || type == VariantDataType.Nat64 || type == VariantDataType.DateTime))
            {
                throw new ArgumentException($"'{c.Category}/{c.Property}' is {type}: {c.Op} needs a number or date property");
            }
            SearchCondition cond = baseCond.CompareWith(cmp, value);
            if (ignoreCase && type == VariantDataType.DisplayString) cond = cond.IgnoreStringValueCase();
            return One(cond);
        }

        // Find the property's exact names and its type with a native search,
        // falling back to a short case-insensitive walk for loosely typed names.
        private static PropInfo LookupProperty(Document doc, string category, string property, Dictionary<string, PropInfo> cache)
        {
            string key = category + "\u0001" + property;
            if (cache.TryGetValue(key, out PropInfo hit)) return hit;

            var info = new PropInfo { Category = category, Property = property };
            foreach (bool byDisplay in new[] { true, false })
            {
                ModelItem first = FindFirstWith(doc, byDisplay
                    ? SearchCondition.HasPropertyByDisplayName(category, property)
                    : SearchCondition.HasPropertyByName(category, property));
                if (first == null) continue;
                DataProperty p = byDisplay
                    ? first.PropertyCategories.FindPropertyByDisplayName(category, property)
                    : first.PropertyCategories.FindPropertyByName(category, property);
                if (p == null) continue;
                info.Found = true;
                info.ByDisplayName = byDisplay;
                info.Type = p.Value.DataType;
                cache[key] = info;
                return info;
            }

            // Names given in other letter case: look at the first items.
            int seen = 0;
            foreach (Model m in doc.Models)
            {
                if (m.RootItem == null) continue;
                foreach (ModelItem mi in m.RootItem.DescendantsAndSelf)
                {
                    if (++seen > 5000) break;
                    foreach (PropertyCategory cat in mi.PropertyCategories)
                    {
                        if (!NameMatches(category, cat.DisplayName, cat.Name)) continue;
                        foreach (DataProperty p in cat.Properties)
                        {
                            if (!NameMatches(property, p.DisplayName, p.Name)) continue;
                            info.Found = true;
                            info.ByDisplayName = true;
                            info.Category = cat.DisplayName;
                            info.Property = p.DisplayName;
                            info.Type = p.Value.DataType;
                            cache[key] = info;
                            return info;
                        }
                    }
                }
                if (seen > 5000) break;
            }
            cache[key] = info;
            return info;
        }

        private static string ResolveCategoryName(Document doc, string category, out bool byDisplay)
        {
            byDisplay = true;
            if (FindFirstWith(doc, SearchCondition.HasCategoryByDisplayName(category)) != null) return category;
            byDisplay = false;
            if (FindFirstWith(doc, SearchCondition.HasCategoryByName(category)) != null) return category;
            byDisplay = true;
            return null;
        }

        private static ModelItem FindFirstWith(Document doc, SearchCondition cond)
        {
            var s = new Search();
            s.Selection.SelectAll();
            s.Locations = SearchLocations.DescendantsAndSelf;
            s.SearchConditions.Add(cond);
            try { return s.FindFirst(doc, false); }
            catch { return null; }
        }

        // ── Values ──────────────────────────────────────────────────────────

        private static readonly Regex NumberRx =
            new Regex(@"^\s*([-+]?(?:\d+(?:[.,]\d*)?|[.,]\d+)(?:[eE][-+]?\d+)?)", RegexOptions.Compiled);

        private static double ParseNumber(string text, CondSpec c)
        {
            Match m = NumberRx.Match(text ?? "");
            if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double d))
                throw new ArgumentException($"'{c.Category}/{c.Property}' is a number: '{text}' is not one");
            return d;
        }

        // A value of the property's type. Lengths / areas / volumes are in
        // document units (as list_property_values shows them), angles in degrees.
        private static VariantData MakeValue(VariantDataType type, string text, CondSpec c)
        {
            switch (type)
            {
                case VariantDataType.DisplayString: return VariantData.FromDisplayString(text ?? "");
                case VariantDataType.IdentifierString: return VariantData.FromIdentifierString(text ?? "");
                case VariantDataType.Boolean:
                    switch ((text ?? "").Trim().ToLowerInvariant())
                    {
                        case "yes": case "true": case "1": case "ja": case "да": return VariantData.FromBoolean(true);
                        case "no": case "false": case "0": case "nein": case "нет": return VariantData.FromBoolean(false);
                        default: throw new ArgumentException($"'{c.Category}/{c.Property}' is yes/no: '{text}' is neither");
                    }
                case VariantDataType.Int32: return VariantData.FromInt32((int)Math.Round(ParseNumber(text, c)));
                case VariantDataType.Int64: return VariantData.FromInt64((long)Math.Round(ParseNumber(text, c)));
                case VariantDataType.Nat32: return VariantData.FromNat32((uint)Math.Max(0, Math.Round(ParseNumber(text, c))));
                case VariantDataType.Nat64: return VariantData.FromNat64((ulong)Math.Max(0, Math.Round(ParseNumber(text, c))));
                case VariantDataType.Double: return VariantData.FromDouble(ParseNumber(text, c));
                case VariantDataType.DoubleLength: return VariantData.FromDoubleLength(ParseNumber(text, c));
                case VariantDataType.DoubleArea: return VariantData.FromDoubleArea(ParseNumber(text, c));
                case VariantDataType.DoubleVolume: return VariantData.FromDoubleVolume(ParseNumber(text, c));
                case VariantDataType.DoubleAngle: return VariantData.FromDoubleAngle(ParseNumber(text, c) * Math.PI / 180.0);
                case VariantDataType.DateTime:
                    if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime dt))
                        return VariantData.FromDateTime(dt);
                    throw new ArgumentException($"'{c.Category}/{c.Property}' is a date: '{text}' is not one (use yyyy-MM-dd)");
                default:
                    throw new ArgumentException($"'{c.Category}/{c.Property}' is {type}: compare it with contains / wildcard " +
                        "on another property, or use has_property");
            }
        }
    }
}
