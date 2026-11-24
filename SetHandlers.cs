// =============================================================================
//  SetHandlers.cs — Selection Sets window: sets, search sets and folders
// =============================================================================
//  /sets/list               every set and folder with type and path
//  /sets/selection/create   selection set from the current selection
//  /sets/folder/create      folder (with parents) — "ОВ/Трубы"
//  /sets/rename             set or folder
//  /sets/move               into another folder / to another position
//  /sets/selection/update   replace / add / remove items from the selection
//  /sets/search/update      new conditions for a search set
//  /sets/info               type, folder, conditions, items
//  /sets/delete             set, or folder (with contents only if recursive)
//
//  A set is addressed by its path ("ОВ/Трубы/DN100") or by its name alone
//  (first match anywhere). Changing a set replaces it with an updated copy at
//  the same place (ReplaceWithCopy), like Update in the Sets window.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Navisworks.Api;

namespace NavisworksMcpAddin
{
    internal static partial class Handlers
    {
        // ── Paths and folders ───────────────────────────────────────────────

        private static string[] SplitSetPath(string path)
        {
            return (path ?? "").Split('/').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();
        }

        private static SavedItem FindSavedItemByPath(GroupItem root, string path)
        {
            string[] parts = SplitSetPath(path);
            if (parts.Length == 0) return null;
            GroupItem cur = root;
            for (int i = 0; i < parts.Length; i++)
            {
                SavedItem next = cur.Children.FirstOrDefault(c =>
                    string.Equals(c.DisplayName, parts[i], StringComparison.OrdinalIgnoreCase)
                    && (i == parts.Length - 1 || c is GroupItem));
                if (next == null) return null;
                if (i == parts.Length - 1) return next;
                cur = (GroupItem)next;
            }
            return null;
        }

        private static bool IsFolder(SavedItem si) => si is GroupItem && !(si is SelectionSet);

        // Where a set or folder sits: its parent, index there and full path.
        // Found by walking the tree from the root, so it does not depend on
        // SavedItem.Parent or on equality of SavedItem wrappers.
        private sealed class SetLoc
        {
            public SavedItem Item;
            public GroupItem Parent;
            public int Index;
            public string Path;
            public string Folder => Path.Contains("/") ? Path.Substring(0, Path.LastIndexOf('/')) : "";
        }

        // A tree of saved items (Sets or Saved Viewpoints): how to read its
        // root, add to it and tell a folder from an item.
        private sealed class SavedTree
        {
            public Func<GroupItem> Root;
            public Action<GroupItem, SavedItem> Add;
            public Func<SavedItem, bool> IsFolder;
            public string What;      // "set", "viewpoint"
            public string ListTool;  // "list_sets", "list_viewpoints"
        }

        private static SavedTree SetsTree(Document doc) => new SavedTree
        {
            Root = () => doc.SelectionSets.RootItem,
            Add = (g, i) => doc.SelectionSets.AddCopy(g, i),
            IsFolder = IsFolder,
            What = "set",
            ListTool = "list_sets"
        };

        private static SetLoc LocateSet(Document doc, string name) => LocateSaved(SetsTree(doc), name);

        private static GroupItem GetSetFolder(Document doc, string path, bool create, out string realPath)
            => GetSavedFolder(SetsTree(doc), path, create, out realPath);

        private static SetLoc LocateSaved(SavedTree tree, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");
            GroupItem root = tree.Root();
            string[] parts = SplitSetPath(name);

            if (parts.Length > 1)
            {
                GroupItem cur = root;
                string prefix = "";
                for (int p = 0; p < parts.Length; p++)
                {
                    int idx = -1;
                    for (int k = 0; k < cur.Children.Count; k++)
                    {
                        SavedItem c = cur.Children[k];
                        if (string.Equals(c.DisplayName, parts[p], StringComparison.OrdinalIgnoreCase)
                            && (p == parts.Length - 1 || c is GroupItem)) { idx = k; break; }
                    }
                    if (idx < 0) break;
                    SavedItem hit = cur.Children[idx];
                    if (p == parts.Length - 1)
                        return new SetLoc { Item = hit, Parent = cur, Index = idx, Path = prefix + hit.DisplayName };
                    prefix += hit.DisplayName + "/";
                    cur = (GroupItem)hit;
                }
            }

            string wanted = name.Trim();
            SetLoc Dfs(GroupItem grp, string prefix)
            {
                for (int k = 0; k < grp.Children.Count; k++)
                {
                    SavedItem c = grp.Children[k];
                    if (string.Equals(c.DisplayName, wanted, StringComparison.OrdinalIgnoreCase))
                        return new SetLoc { Item = c, Parent = grp, Index = k, Path = prefix + c.DisplayName };
                    if (c is GroupItem)  // folders, and viewpoint animations too
                    {
                        SetLoc r = Dfs((GroupItem)c, prefix + c.DisplayName + "/");
                        if (r != null) return r;
                    }
                }
                return null;
            }
            return Dfs(root, "")
                ?? throw new InvalidOperationException($"{tree.What} or folder '{name}' not found (see {tree.ListTool})");
        }

        // The folder at path, created (with its parents) if asked. "" = root.
        private static GroupItem GetSavedFolder(SavedTree tree, string path, bool create, out string realPath)
        {
            GroupItem cur = tree.Root();
            var names = new List<string>();
            foreach (string part in SplitSetPath(path))
            {
                SavedItem next = cur.Children.FirstOrDefault(c =>
                    tree.IsFolder(c) && string.Equals(c.DisplayName, part, StringComparison.OrdinalIgnoreCase));
                if (next == null)
                {
                    if (!create) throw new InvalidOperationException($"folder '{path}' not found");
                    if (cur.Children.Any(c => string.Equals(c.DisplayName, part, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException($"'{part}' in '{path}' is a {tree.What}, not a folder");
                    tree.Add(cur, new FolderItem { DisplayName = part });
                    // Objects read before the change are stale: walk again from the
                    // fresh root (one level more exists now).
                    return GetSavedFolder(tree, path, create, out realPath);
                }
                names.Add(next.DisplayName);
                cur = (GroupItem)next;
            }
            realPath = string.Join("/", names);
            return cur;
        }

        private static string SetType(SavedItem si)
        {
            if (si is SelectionSet s) return s.HasSearch ? "search" : "selection";
            return IsFolder(si) ? "folder" : si.GetType().Name;
        }

        private static int IndexByName(GroupItem g, string name)
        {
            for (int k = 0; k < g.Children.Count; k++)
                if (string.Equals(g.Children[k].DisplayName, name, StringComparison.OrdinalIgnoreCase)) return k;
            return -1;
        }

        // Put a new set into a folder, or over an existing set of that name there.
        private static object PlaceNewSet(Document doc, SelectionSet set, string folderPath, bool replace)
        {
            GroupItem folder = GetSetFolder(doc, folderPath, true, out string realFolder);
            int idx = IndexByName(folder, set.DisplayName);
            if (idx >= 0)
            {
                SavedItem existing = folder.Children[idx];
                if (!replace || IsFolder(existing))
                    throw new InvalidOperationException(
                        $"'{set.DisplayName}' already exists in {(realFolder.Length == 0 ? "the root" : "'" + realFolder + "'")}" +
                        (IsFolder(existing) ? " as a folder" : " (pass replace=true to overwrite it)"));
                doc.SelectionSets.ReplaceWithCopy(folder, idx, set);
            }
            else
            {
                doc.SelectionSets.AddCopy(folder, set);
            }
            return new
            {
                name = set.DisplayName,
                path = (realFolder.Length > 0 ? realFolder + "/" : "") + set.DisplayName,
                replaced = idx >= 0
            };
        }

        // ── /sets/list ──────────────────────────────────────────────────────

        public static object SetsList()
        {
            var doc = RequireDocument();
            var list = new List<object>();
            void Walk(GroupItem group, string prefix)
            {
                foreach (SavedItem si in group.Children)
                {
                    string path = prefix + si.DisplayName;
                    string folder = prefix.TrimEnd('/');
                    if (si is SelectionSet set)
                    {
                        list.Add(new
                        {
                            name = path,
                            type = set.HasSearch ? "search" : "selection",
                            folder,
                            // An explicit set knows its size; a search set has to run.
                            count = set.HasSearch ? (int?)null : set.ExplicitModelItems.Count
                        });
                    }
                    else if (si is GroupItem g)
                    {
                        list.Add(new { name = path, type = "folder", folder, count = (int?)g.Children.Count });
                        Walk(g, path + "/");
                    }
                }
            }
            Walk(doc.SelectionSets.RootItem, "");
            return new { count = list.Count, sets = list };
        }

        // ── /sets/selection/create ──────────────────────────────────────────

        public static object SelectionSetCreate(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            string name = GetString(body, "name", null);
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");

            var current = doc.CurrentSelection.SelectedItems;
            if (current.Count == 0) throw new InvalidOperationException("nothing is currently selected");

            SplitSetName(name, GetString(body, "folder", null), out string folder, out string leaf);
            var set = new SelectionSet(new ModelItemCollection(current)) { DisplayName = leaf };
            object placed = PlaceNewSet(doc, set, folder, GetBool(body, "replace", false));
            return new { name = name.Trim(), count = current.Count, saved = placed };
        }

        // ── /sets/folder/create ─────────────────────────────────────────────

        public static object SetFolderCreate(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            string path = GetString(body, "path", null) ?? GetString(body, "name", null);
            if (SplitSetPath(path).Length == 0) throw new ArgumentException("path is required, e.g. 'ОВ/Трубы'");
            bool existed;
            try { GetSetFolder(doc, path, false, out _); existed = true; }
            catch (InvalidOperationException) { existed = false; }
            GetSetFolder(doc, path, true, out string realPath);
            return new { path = realPath, created = !existed };
        }

        // ── /sets/rename ────────────────────────────────────────────────────

        public static object SetRename(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            SetLoc loc = LocateSet(doc, GetString(body, "name", null));
            string newName = (GetString(body, "new_name", null) ?? "").Trim();
            if (newName.Length == 0 || newName.Contains("/"))
                throw new ArgumentException("new_name is required and must not contain '/'");

            int clash = IndexByName(loc.Parent, newName);
            if (clash >= 0 && clash != loc.Index)
                throw new InvalidOperationException($"'{newName}' already exists in the same folder");

            doc.SelectionSets.EditDisplayName(loc.Item, newName);
            return new
            {
                oldPath = loc.Path,
                path = (loc.Folder.Length > 0 ? loc.Folder + "/" : "") + newName,
                type = SetType(loc.Item)
            };
        }

        // ── /sets/move ──────────────────────────────────────────────────────

        public static object SetMove(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            SetLoc loc = LocateSet(doc, GetString(body, "name", null));
            string type = SetType(loc.Item);
            string itemName = loc.Item.DisplayName;
            string oldPath = loc.Path;

            string folderPath = GetString(body, "folder", null);
            string target = folderPath == null ? loc.Folder : string.Join("/", SplitSetPath(folderPath));
            if (type == "folder" && (string.Equals(target, loc.Path, StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith(loc.Path + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"cannot move folder '{loc.Path}' into itself");

            GroupItem newParent = GetSetFolder(doc, target, true, out string realTarget);
            bool sameParent = string.Equals(realTarget, loc.Folder, StringComparison.OrdinalIgnoreCase);
            if (!sameParent && IndexByName(newParent, itemName) >= 0)
                throw new InvalidOperationException($"'{itemName}' already exists in '{realTarget}'");

            // The target folder may have just been created: locate the item again.
            loc = LocateSet(doc, oldPath);

            int count = newParent.Children.Count;
            int max = sameParent ? count - 1 : count;
            int newIndex = body != null && body.ContainsKey("position")
                ? Math.Max(0, Math.Min(max, GetInt(body, "position", max)))
                : max;

            doc.SelectionSets.Move(loc.Parent, loc.Index, newParent, newIndex);

            return new
            {
                oldPath,
                path = (realTarget.Length > 0 ? realTarget + "/" : "") + itemName,
                position = newIndex,
                type
            };
        }

        // ── /sets/selection/update ──────────────────────────────────────────

        public static object SelectionSetUpdate(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            SetLoc loc = LocateSet(doc, GetString(body, "name", null));
            if (!(loc.Item is SelectionSet old) || old.HasSearch)
                throw new InvalidOperationException($"'{loc.Path}' is a {SetType(loc.Item)}, not a selection set" +
                    (loc.Item is SelectionSet ? " (use update_search_set)" : ""));

            string mode = (GetString(body, "mode", "replace") ?? "replace").Trim().ToLowerInvariant();
            var selected = doc.CurrentSelection.SelectedItems;
            if (selected.Count == 0)
                throw new InvalidOperationException("nothing is currently selected");

            int before = old.ExplicitModelItems.Count;
            var items = new ModelItemCollection();
            switch (mode)
            {
                case "replace":
                    items.AddRange(selected);
                    break;
                case "add":
                    items.AddRange(old.ExplicitModelItems);
                    var have = new HashSet<ModelItem>(old.ExplicitModelItems);
                    foreach (ModelItem mi in selected) if (have.Add(mi)) items.Add(mi);
                    break;
                case "remove":
                    var drop = new HashSet<ModelItem>(selected);
                    foreach (ModelItem mi in old.ExplicitModelItems) if (!drop.Contains(mi)) items.Add(mi);
                    break;
                default:
                    throw new ArgumentException($"unknown mode '{mode}'. Valid: replace, add, remove");
            }

            var updated = new SelectionSet(old);
            updated.CopyFrom(items);
            doc.SelectionSets.ReplaceWithCopy(loc.Parent, loc.Index, updated);

            return new { path = loc.Path, mode, before, after = items.Count };
        }

        // ── /sets/search/update ─────────────────────────────────────────────

        public static object SearchSetUpdate(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            SetLoc loc = LocateSet(doc, GetString(body, "name", null));
            if (!(loc.Item is SelectionSet old) || !old.HasSearch)
                throw new InvalidOperationException($"'{loc.Path}' is a {SetType(loc.Item)}, not a search set" +
                    (loc.Item is SelectionSet ? " (use update_selection_set)" : ""));

            var before = DescribeSearch(doc, old.Search);
            SearchSpec spec = BuildSearch(doc, body);

            var updated = new SelectionSet(old);
            updated.CopyFrom(spec.Search);
            doc.SelectionSets.ReplaceWithCopy(loc.Parent, loc.Index, updated);

            var found = RunSearch(doc, spec.Search, Math.Max(1, GetInt(body, "max_results", 100000)),
                Math.Max(1, GetDouble(body, "time_limit_s", 60)), out bool truncated, out string stopReason);
            return new
            {
                path = loc.Path,
                before = before.Conditions,
                conditions = spec.Readable,
                scope = spec.Scope,
                warnings = spec.Warnings.Count > 0 ? spec.Warnings : null,
                matches = found.Count,
                truncated,
                stopReason
            };
        }

        // ── /sets/info ──────────────────────────────────────────────────────

        public static object SetInfo(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            SetLoc loc = LocateSet(doc, GetString(body, "name", null));
            int limit = Math.Max(0, GetInt(body, "limit", 20));

            if (IsFolder(loc.Item))
            {
                var g = (GroupItem)loc.Item;
                int sets = 0, folders = 0;
                void Count(GroupItem grp)
                {
                    foreach (SavedItem c in grp.Children)
                    {
                        if (IsFolder(c)) { folders++; Count((GroupItem)c); }
                        else sets++;
                    }
                }
                Count(g);
                return new
                {
                    path = loc.Path,
                    type = "folder",
                    folder = loc.Folder,
                    children = g.Children.Select(c => new { name = c.DisplayName, type = SetType(c) }).ToList(),
                    setsInside = sets,
                    foldersInside = folders
                };
            }

            var set = loc.Item as SelectionSet
                ?? throw new InvalidOperationException($"'{loc.Path}' is a {SetType(loc.Item)}");
            ModelItemCollection items;
            bool truncated = false;
            string stopReason = null;
            SearchDescription search = null;
            if (set.HasSearch)
            {
                search = DescribeSearch(doc, set.Search);
                items = RunSearch(doc, set.Search, Math.Max(1, GetInt(body, "max_results", 100000)),
                    Math.Max(1, GetDouble(body, "time_limit_s", 30)), out truncated, out stopReason);
            }
            else
            {
                items = new ModelItemCollection(set.ExplicitModelItems);
            }

            return new
            {
                path = loc.Path,
                type = set.HasSearch ? "search" : "selection",
                folder = loc.Folder,
                conditions = search?.Conditions,
                searchScope = search?.Scope,
                pruneBelowMatch = search?.PruneBelowMatch,
                count = items.Count,
                truncated,
                stopReason,
                files = items.Take(5000).Select(ContainingFileName).Where(f => f != null)
                    .GroupBy(f => f).Select(gr => new { file = gr.Key, items = gr.Count() }).ToList(),
                sampled = Math.Min(limit, items.Count),
                items = items.Take(limit).Select(it => new
                {
                    displayName = it.DisplayName,
                    className = it.ClassDisplayName,
                    file = ContainingFileName(it)
                }).ToList()
            };
        }

        // ── /sets/delete ────────────────────────────────────────────────────

        public static object SetDelete(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            SetLoc loc = LocateSet(doc, GetString(body, "name", null));
            string type = SetType(loc.Item);
            if (IsFolder(loc.Item) && ((GroupItem)loc.Item).Children.Count > 0 && !GetBool(body, "recursive", false))
                throw new InvalidOperationException(
                    $"folder '{loc.Path}' is not empty ({((GroupItem)loc.Item).Children.Count} items): " +
                    "pass recursive=true to delete it with its contents");

            doc.SelectionSets.RemoveAt(loc.Parent, loc.Index);
            return new { name = loc.Path, type, deleted = true };
        }

        // ── Reading a Search back ───────────────────────────────────────────

        private sealed class SearchDescription
        {
            public List<string> Conditions = new List<string>();
            public string Scope;
            public bool PruneBelowMatch;
        }

        private static SearchDescription DescribeSearch(Document doc, Search search)
        {
            var d = new SearchDescription();
            try { d.PruneBelowMatch = search.PruneBelowMatch; } catch { }

            var groups = new List<List<string>>();
            foreach (SearchCondition c in search.SearchConditions)
            {
                if (groups.Count == 0 || (c.Options & SearchConditionOptions.StartGroup) != 0)
                    groups.Add(new List<string>());
                groups[groups.Count - 1].Add(DescribeCondition(c));
            }
            d.Conditions = groups.Select(g => string.Join(" AND ", g)).ToList();
            if (d.Conditions.Count > 1)
                d.Conditions = d.Conditions.Select((g, i) => (i == 0 ? "" : "OR ") + g).ToList();

            try
            {
                int n = search.Selection.GetSelectedItems(doc).Count;
                int roots = doc.Models.Count(m => m.RootItem != null);
                d.Scope = search.Selection.IsClear || n == roots ? "all" : n + " selected items";
            }
            catch { d.Scope = "unknown"; }
            return d;
        }

        private static string DescribeCondition(SearchCondition c)
        {
            string cat = null, prop = null;
            try { cat = c.CategoryCombinedName?.DisplayName; } catch { }
            try { prop = c.PropertyCombinedName?.DisplayName; } catch { }
            string target = prop != null ? (cat ?? "?") + "/" + prop : (cat ?? "?");

            string op;
            bool withValue = true;
            switch (c.Comparison)
            {
                case SearchConditionComparison.Equal: op = "="; break;
                case SearchConditionComparison.NotEqual: op = "!="; break;
                case SearchConditionComparison.NumericGreaterThan: op = ">"; break;
                case SearchConditionComparison.NumericGreaterThanOrEqual: op = ">="; break;
                case SearchConditionComparison.NumericLessThan: op = "<"; break;
                case SearchConditionComparison.NumericLessThanOrEqual: op = "<="; break;
                case SearchConditionComparison.DisplayStringContains: op = "contains"; break;
                case SearchConditionComparison.DisplayStringWildcard: op = "wildcard"; break;
                case SearchConditionComparison.HasProperty: op = "has_property"; withValue = false; break;
                case SearchConditionComparison.NotHasProperty: op = "not_has_property"; withValue = false; break;
                case SearchConditionComparison.HasCategory: op = "has_category"; withValue = false; break;
                case SearchConditionComparison.NotHasCategory: op = "not_has_category"; withValue = false; break;
                default: op = c.Comparison.ToString(); break;
            }
            string text = target + " " + op;
            if (withValue)
            {
                string v;
                try { v = FormatVariant(c.Value); } catch { v = "?"; }
                text += " '" + v + "'";
            }
            if ((c.Options & SearchConditionOptions.NegateCondition) != 0) text = "NOT " + text;
            if ((c.Options & SearchConditionOptions.IgnoreDisplayStringValueCase) != 0) text += " (ignore case)";
            return text;
        }
    }
}
