// =============================================================================
//  ViewHandlers.cs — what the user (and Claude) sees
// =============================================================================
//  /view/capture        render the current view to PNG (returned as base64)
//  /view/zoom           zoom the camera to the current selection or a set
//  /view/section-box    section box around the selection / a set
//  /view/section-clear  switch sectioning off
//  /view/section-get    raw ClipPlaneSet JSON of the current view
// =============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

using Autodesk.Navisworks.Api;

namespace NavisworksMcpAddin
{
    internal static partial class Handlers
    {
        // ════════════════════════════════════════════════════════════════════
        //  VIEW
        // ════════════════════════════════════════════════════════════════════

        public static object ViewCapture(Dictionary<string, object> body)
        {
            return Capture(RequireDocument(), body);
        }

        // Shared by /view/capture and /clash/result/zoom. The base64 PNG goes
        // under image_png_base64; server_http.py turns it into an MCP image.
        private static Dictionary<string, object> Capture(Document doc, Dictionary<string, object> body)
        {
            View view = RequireView(doc);

            int width = Math.Max(64, Math.Min(4096, GetInt(body, "width", 1280)));
            double aspect = view.Width > 0 && view.Height > 0 ? (double)view.Height / view.Width : 0.5625;
            int height = Math.Max(64, Math.Min(4096, GetInt(body, "height", (int)Math.Round(width * aspect))));

            ImageGenerationStyle style;
            switch ((GetString(body, "style", "overlay") ?? "").Trim().ToLowerInvariant())
            {
                case "scene": style = ImageGenerationStyle.Scene; break;
                case "raytrace": style = ImageGenerationStyle.SceneUsingRayTrace; break;
                default: style = ImageGenerationStyle.ScenePlusOverlay; break;
            }

            byte[] png;
            using (Bitmap bmp = view.GenerateImage(style, width, height, true))
            {
                if (bmp == null) throw new InvalidOperationException("Navisworks returned no image");
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    png = ms.ToArray();
                }
            }

            string savePath = GetString(body, "save_path", null);
            if (!string.IsNullOrEmpty(savePath))
            {
                EnsureDirectory(savePath);
                File.WriteAllBytes(savePath, png);
            }

            return new Dictionary<string, object>
            {
                ["width"] = width,
                ["height"] = height,
                ["style"] = style.ToString(),
                ["savedTo"] = savePath,
                ["image_png_base64"] = Convert.ToBase64String(png)
            };
        }

        public static object ViewZoom(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            ModelItemCollection items = TargetItems(doc, body);
            BoundingBox3D box = Inflate(items.BoundingBox(), GetDouble(body, "padding", 0));
            if (box.IsEmpty) throw new InvalidOperationException("the items have no geometry to zoom to");

            ZoomTo(doc, box);
            return new { items = items.Count, box = BoxToJson(box), warning = SpreadWarning(doc, box) };
        }

        public static object ViewSectionBox(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            View view = RequireView(doc);
            ModelItemCollection items = TargetItems(doc, body);
            BoundingBox3D box = Inflate(items.BoundingBox(), GetDouble(body, "padding", 0));
            if (box.IsEmpty) throw new InvalidOperationException("the items have no geometry to section around");

            SetSectionBox(view, box);
            if (GetBool(body, "zoom", true)) ZoomTo(doc, box);
            return new { items = items.Count, box = BoxToJson(box), warning = SpreadWarning(doc, box) };
        }

        public static object ViewSectionClear()
        {
            var doc = RequireDocument();
            View view = RequireView(doc);

            string current = view.GetClippingPlanes() ?? "";
            // Turning every "Enabled" flag off switches off both the plane set
            // and its individual planes, whichever mode is active.
            string off = Regex.Replace(current, "\"Enabled\"\\s*:\\s*true", "\"Enabled\":false");
            view.SetClippingPlanes(off.Length > 0 ? off : "{\"Type\":\"ClipPlaneSet\",\"Version\":1,\"Enabled\":false}");
            return new { sectioning = "off" };
        }

        public static object ViewSectionGet()
        {
            var doc = RequireDocument();
            return new { clipPlaneSet = RequireView(doc).GetClippingPlanes() };
        }

        // ── View helpers ────────────────────────────────────────────────────

        private static View RequireView(Document doc)
        {
            return doc.ActiveView
                ?? throw new InvalidOperationException("no active view (Navisworks runs without a GUI?)");
        }

        // Items to act on: a named selection/search set, else the current selection.
        private static ModelItemCollection TargetItems(Document doc, Dictionary<string, object> body)
        {
            string setName = GetString(body, "set_name", null);
            ModelItemCollection items = string.IsNullOrEmpty(setName)
                ? doc.CurrentSelection.SelectedItems
                : ResolveNamedSet(doc, setName);
            if (items == null || items.Count == 0)
            {
                throw new InvalidOperationException(string.IsNullOrEmpty(setName)
                    ? "nothing is currently selected (or pass set_name)"
                    : $"set '{setName}' is empty");
            }
            return items;
        }

        private static void ZoomTo(Document doc, BoundingBox3D box)
        {
            Viewpoint vp = doc.CurrentViewpoint.CreateCopy();
            vp.ZoomBox(box);
            doc.CurrentViewpoint.CopyFrom(vp);
        }

        private static BoundingBox3D Inflate(BoundingBox3D box, double by)
        {
            if (box == null || box.IsEmpty || by == 0) return box;
            return new BoundingBox3D(
                new Point3D(box.Min.X - by, box.Min.Y - by, box.Min.Z - by),
                new Point3D(box.Max.X + by, box.Max.Y + by, box.Max.Z + by));
        }

        // Axis-aligned section box in model units, as a JSON ClipPlaneSet.
        private static void SetSectionBox(View view, BoundingBox3D box)
        {
            string json = string.Format(CultureInfo.InvariantCulture,
                "{{\"Type\":\"ClipPlaneSet\",\"Version\":1," +
                "\"OrientedBox\":{{\"Type\":\"OrientedBox3D\",\"Version\":1," +
                "\"Box\":[[{0},{1},{2}],[{3},{4},{5}]],\"Rotation\":[0,0,0]}}," +
                "\"Enabled\":true}}",
                box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z);
            view.SetClippingPlanes(json);
        }

        // Items kilometres apart usually mean files in different coordinate
        // systems (one georeferenced, one around 0,0,0): the zoom then shows
        // the model as a dot. Say so instead of leaving a puzzling picture.
        private static string SpreadWarning(Document doc, BoundingBox3D box)
        {
            double metres = Math.Max(box.Size.X, Math.Max(box.Size.Y, box.Size.Z)) * MetresPerUnit(doc.Units);
            return metres > 10000
                ? $"items span {metres / 1000:0.#} km - probably files in different coordinate systems"
                : null;
        }

        private static double MetresPerUnit(Units units)
        {
            switch (units)
            {
                case Units.Millimeters: return 0.001;
                case Units.Centimeters: return 0.01;
                case Units.Kilometers: return 1000;
                case Units.Feet: return 0.3048;
                case Units.Inches: return 0.0254;
                case Units.Yards: return 0.9144;
                case Units.Miles: return 1609.344;
                default: return 1;
            }
        }

        private static object BoxToJson(BoundingBox3D box)
        {
            return new
            {
                min = new[] { box.Min.X, box.Min.Y, box.Min.Z },
                max = new[] { box.Max.X, box.Max.Y, box.Max.Z }
            };
        }
    }
}
