using System;
using System.Collections.Generic;
using Autodesk.Navisworks.Api;

namespace NavisworksMcpAddin
{
    // Hide / Hide Unselected / Unhide All / Unhide Children — the same commands
    // as the Navisworks Visibility panel. All of them use Navisworks' own calls
    // (Invert, ResetAllHidden, MakeVisible) and never walk the whole model tree,
    // which is what crashed Navisworks on large federated models.
    internal static partial class Handlers
    {
        // Hide the selection or a named set.
        public static object ElementsHide(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            ModelItemCollection items = TargetItems(doc, body);
            doc.Models.SetHidden(items, true);
            return new { hidden = items.Count, source = TargetSource(body) };
        }

        // Hide everything except the selection or a named set (Hide Unselected).
        // The kept items are made visible too, so the result does not depend on
        // what was hidden before — this is also isolate_elements.
        public static object ElementsHideUnselected(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            ModelItemCollection keep = TargetItems(doc, body);

            // Invert works in place: invert a copy, not the selection or the set.
            var rest = new ModelItemCollection(keep);
            rest.Invert(doc);

            doc.Models.MakeVisible(keep);
            if (rest.Count > 0) doc.Models.SetHidden(rest, true);

            return new
            {
                kept = keep.Count,
                hiddenBranches = rest.Count,
                source = TargetSource(body)
            };
        }

        // Show everything (Unhide All).
        public static object ElementsUnhideAll()
        {
            var doc = RequireDocument();
            doc.Models.ResetAllHidden();
            return new { unhidden = "all" };
        }

        // Show the selection or a named set together with everything inside it
        // (Unhide Children): e.g. bring back one file of a hidden model.
        public static object ElementsUnhideChildren(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            ModelItemCollection items = TargetItems(doc, body);
            doc.Models.MakeVisible(items);
            return new { unhidden = items.Count, withChildren = true, source = TargetSource(body) };
        }

        private static string TargetSource(Dictionary<string, object> body)
        {
            string setName = GetString(body, "set_name", null);
            return string.IsNullOrEmpty(setName) ? "selection" : "set '" + setName + "'";
        }
    }
}
