// =============================================================================
//  ViewpointHandlers.cs — Saved Viewpoints window and the camera
// =============================================================================
//  /viewpoints/list            viewpoints and folders with path and type
//  /viewpoints/save            current view as a viewpoint (in a folder);
//                              with_overrides also stores hidden items and
//                              colour / transparency overrides
//  /viewpoints/goto            like clicking it: camera, section and, when
//                              stored, hidden items and colours
//  /viewpoints/update          the viewpoint takes the current view (Update)
//  /viewpoints/get             full camera of a viewpoint or of the current view
//  /viewpoints/current         same, current view
//  /viewpoints/camera          change camera settings (current view or saved)
//  /viewpoints/folder/create   folder (with parents)
//  /viewpoints/rename          viewpoint or folder
//  /viewpoints/move            into another folder / to another position
//  /viewpoints/delete          viewpoint, or folder (with contents if recursive)
//
//  Paths work as for sets: "Коллизии/Этаж 1/Узел 3" or just the name.
//  Camera: looking down -Z with +Y up, turned by Viewpoint.Rotation; lengths
//  in document units, angles in degrees.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Navisworks.Api;

namespace NavisworksMcpAddin
{
    internal static partial class Handlers
    {
        private static bool IsViewpointFolder(SavedItem si) =>
            si is GroupItem && !(si is SavedViewpoint) && !(si is SavedViewpointAnimation);

        private static SavedTree ViewpointTree(Document doc) => new SavedTree
        {
            Root = () => doc.SavedViewpoints.RootItem,
            Add = (g, i) => doc.SavedViewpoints.AddCopy(g, i),
            IsFolder = IsViewpointFolder,
            What = "viewpoint",
            ListTool = "list_viewpoints"
        };

        private static string ViewpointType(SavedItem si)
        {
            if (si is SavedViewpoint) return "viewpoint";
            if (si is SavedViewpointAnimation) return "animation";
            return IsViewpointFolder(si) ? "folder" : si.GetType().Name;
        }

        private static SetLoc LocateViewpoint(Document doc, string name, bool viewpointOnly)
        {
            SetLoc loc = LocateSaved(ViewpointTree(doc), name);
            if (viewpointOnly && !(loc.Item is SavedViewpoint))
                throw new InvalidOperationException($"'{loc.Path}' is a {ViewpointType(loc.Item)}, not a viewpoint");
            return loc;
        }

        // ── /viewpoints/list ────────────────────────────────────────────────

        public static object ViewpointsList()
        {
            var doc = RequireDocument();
            SavedItem current = null;
            try { current = doc.SavedViewpoints.CurrentSavedViewpoint; } catch { }
            string currentPath = null;

            var list = new List<object>();
            void Walk(GroupItem group, string prefix)
            {
                foreach (SavedItem si in group.Children)
                {
                    string path = prefix + si.DisplayName;
                    if (current != null && si.Guid != Guid.Empty && si.Guid == current.Guid) currentPath = path;
                    if (si is SavedViewpoint sv)
                    {
                        list.Add(new
                        {
                            name = path,
                            type = "viewpoint",
                            folder = prefix.TrimEnd('/'),
                            hiddenItems = sv.ContainsVisibilityOverrides,
                            colorOverrides = sv.ContainsAppearanceOverrides
                        });
                    }
                    else if (si is GroupItem g)
                    {
                        list.Add(new { name = path, type = ViewpointType(si), folder = prefix.TrimEnd('/'),
                                       hiddenItems = (bool?)null, colorOverrides = (bool?)null });
                        Walk(g, path + "/");
                    }
                }
            }
            Walk(doc.SavedViewpoints.RootItem, "");
            return new { count = list.Count, current = currentPath, viewpoints = list };
        }

        // ── /viewpoints/save ────────────────────────────────────────────────

        public static object ViewpointSave(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            string name = GetString(body, "name", null);
            SplitSetName(name, GetString(body, "folder", null), out string folderPath, out string leaf);
            bool withOverrides = GetBool(body, "with_overrides", false);

            // CaptureRuntimeOverrides = the current view plus hidden items and
            // colour / transparency overrides, as "Save Viewpoint" with the
            // Hide/Required and Override Material options on.
            // It holds only the overrides ("Camera not set"): give it the
            // camera of the current view as well.
            SavedViewpoint sv;
            if (withOverrides)
            {
                sv = doc.SavedViewpoints.CaptureRuntimeOverrides();
                CopyCamera(doc.CurrentViewpoint.CreateCopy(), sv.Viewpoint);
            }
            else
            {
                sv = new SavedViewpoint(doc.CurrentViewpoint.CreateCopy());
            }
            sv.DisplayName = leaf;

            SavedTree tree = ViewpointTree(doc);
            GroupItem folder = GetSavedFolder(tree, folderPath, true, out string realFolder);
            int idx = IndexByName(folder, leaf);
            if (idx >= 0)
            {
                SavedItem existing = folder.Children[idx];
                if (!GetBool(body, "replace", false) || IsViewpointFolder(existing))
                    throw new InvalidOperationException(
                        $"'{leaf}' already exists in {(realFolder.Length == 0 ? "the root" : "'" + realFolder + "'")}" +
                        (IsViewpointFolder(existing) ? " as a folder" : " (pass replace=true, or use update_viewpoint)"));
                doc.SavedViewpoints.ReplaceWithCopy(folder, idx, sv);
            }
            else
            {
                doc.SavedViewpoints.AddCopy(folder, sv);
            }

            SetLoc saved = LocateViewpoint(doc, (realFolder.Length > 0 ? realFolder + "/" : "") + leaf, true);
            var s = (SavedViewpoint)saved.Item;
            return new
            {
                name = leaf,
                path = saved.Path,
                replaced = idx >= 0,
                hiddenItems = s.ContainsVisibilityOverrides,
                colorOverrides = s.ContainsAppearanceOverrides
            };
        }

        // ── /viewpoints/goto ────────────────────────────────────────────────

        public static object ViewpointGoto(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            SetLoc loc = LocateViewpoint(doc, GetString(body, "name", null), true);
            var sv = (SavedViewpoint)loc.Item;
            bool applyOverrides = GetBool(body, "apply_overrides", true);

            try { doc.SavedViewpoints.CurrentSavedViewpoint = sv; } catch { }
            // A viewpoint may hold only hidden items / colours and no camera.
            bool hasCamera = HasCamera(sv.Viewpoint);
            if (hasCamera) doc.CurrentViewpoint.CopyFrom(sv.Viewpoint);

            int hidden = 0, colored = 0;
            if (applyOverrides && sv.ContainsVisibilityOverrides)
            {
                ModelItemCollection h = sv.GetVisibilityOverrides().Hidden;
                doc.Models.ResetAllHidden();
                if (h != null && h.Count > 0) doc.Models.SetHidden(h, true);
                hidden = h?.Count ?? 0;
            }
            if (applyOverrides && sv.ContainsAppearanceOverrides)
            {
                colored = ApplyAppearance(doc, sv.GetAppearanceOverrides());
            }

            return new
            {
                path = loc.Path,
                hiddenApplied = sv.ContainsVisibilityOverrides && applyOverrides ? (int?)hidden : null,
                colorOverridesApplied = sv.ContainsAppearanceOverrides && applyOverrides ? (int?)colored : null,
                cameraApplied = hasCamera,
                camera = DescribeViewpoint(doc.CurrentViewpoint.CreateCopy())
            };
        }

        // Camera, section and display settings from one viewpoint into another.
        private static void CopyCamera(Viewpoint from, Viewpoint to)
        {
            string json = SafeCameraJson(from);
            if (json == null || !to.TrySetCamera(json))
            {
                to.Projection = from.Projection;
                to.Position = from.Position;
                to.Rotation = from.Rotation;
                to.HeightField = from.HeightField;
                to.AspectRatio = from.AspectRatio;
            }
            if (from.HasFocalDistance) to.FocalDistance = from.FocalDistance;
            if (from.HasWorldUpVector) to.WorldUpVector = from.WorldUpVector;
            if (from.HasLinearSpeed) to.LinearSpeed = from.LinearSpeed;
            if (from.HasAngularSpeed) to.AngularSpeed = from.AngularSpeed;
            if (from.HasLighting) to.Lighting = from.Lighting;
            if (from.HasRenderStyle) to.RenderStyle = from.RenderStyle;
            try { if (from.ClipPlanes != null) to.ClipPlanes = from.ClipPlanes; } catch { }
        }

        private static bool HasCamera(Viewpoint vp)
        {
            try { var p = vp.Position; var r = vp.Rotation; return p != null && r != null; }
            catch { return false; }
        }

        // The viewpoint's colours replace the current ones, as when it is clicked.
        private static int ApplyAppearance(Document doc, AppearanceOverrides ov)
        {
            doc.Models.ResetAllPermanentMaterials();
            int n = 0;
            foreach (var group in ov.MaterialOverrides.Where(m => m?.Item != null)
                         .GroupBy(m => (m.Color == null ? "-" : $"{m.Color.R:R},{m.Color.G:R},{m.Color.B:R}") + "|" +
                                       (m.Transparency.HasValue ? m.Transparency.Value.ToString("R") : "-")))
            {
                MaterialOverride first = group.First();
                var items = new ModelItemCollection();
                foreach (MaterialOverride m in group) items.Add(m.Item);
                if (first.Color != null) doc.Models.OverridePermanentColor(items, first.Color);
                if (first.Transparency.HasValue) doc.Models.OverridePermanentTransparency(items, first.Transparency.Value);
                n += items.Count;
            }
            return n;
        }

        // ── /viewpoints/update ──────────────────────────────────────────────

        public static object ViewpointUpdate(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            SetLoc loc = LocateViewpoint(doc, GetString(body, "name", null), true);
            var before = DescribeViewpoint(((SavedViewpoint)loc.Item).Viewpoint);
            doc.SavedViewpoints.ReplaceFromCurrentView((SavedViewpoint)loc.Item);
            var now = (SavedViewpoint)LocateViewpoint(doc, loc.Path, true).Item;
            return new
            {
                path = loc.Path,
                before,
                after = DescribeViewpoint(now.Viewpoint),
                hiddenItems = now.ContainsVisibilityOverrides,
                colorOverrides = now.ContainsAppearanceOverrides
            };
        }

        // ── /viewpoints/get, /viewpoints/current ────────────────────────────

        public static object ViewpointGet(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            string name = GetString(body, "name", null);
            bool raw = GetBool(body, "include_camera_json", false);
            if (string.IsNullOrWhiteSpace(name))
            {
                Viewpoint cur = doc.CurrentViewpoint.CreateCopy();
                return new { source = "current view", camera = DescribeViewpoint(cur), cameraJson = raw ? SafeCameraJson(cur) : null };
            }
            SetLoc loc = LocateViewpoint(doc, name, true);
            var sv = (SavedViewpoint)loc.Item;
            int? hiddenCount = null, colorCount = null;
            try { if (sv.ContainsVisibilityOverrides) hiddenCount = sv.GetVisibilityOverrides().Hidden?.Count ?? 0; } catch { }
            try { if (sv.ContainsAppearanceOverrides) colorCount = sv.GetAppearanceOverrides().MaterialOverrides?.Count ?? 0; } catch { }
            return new
            {
                source = "saved viewpoint",
                path = loc.Path,
                folder = loc.Folder,
                camera = DescribeViewpoint(sv.Viewpoint),
                hiddenItems = hiddenCount,
                colorOverrides = colorCount,
                cameraJson = raw ? SafeCameraJson(sv.Viewpoint) : null
            };
        }

        public static object ViewpointCurrent() => ViewpointGet(new Dictionary<string, object>());

        private static string SafeCameraJson(Viewpoint vp)
        {
            try { return vp.GetCamera(); } catch { return null; }
        }

        // ── /viewpoints/camera ──────────────────────────────────────────────

        public static object ViewpointCamera(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            string name = GetString(body, "name", null);
            var changed = new List<string>();

            if (string.IsNullOrWhiteSpace(name))
            {
                Viewpoint vp = doc.CurrentViewpoint.CreateCopy();
                var before = DescribeViewpoint(vp);
                ApplyCamera(vp, body, changed);
                doc.CurrentViewpoint.CopyFrom(vp);
                return new { target = "current view", changed, before, after = DescribeViewpoint(doc.CurrentViewpoint.CreateCopy()) };
            }

            // A saved viewpoint: change a copy (keeps its hidden items, colours,
            // comments) and put it back in place.
            SetLoc loc = LocateViewpoint(doc, name, true);
            var copy = (SavedViewpoint)loc.Item.CreateCopy();
            var before2 = DescribeViewpoint(copy.Viewpoint);
            ApplyCamera(copy.Viewpoint, body, changed);
            doc.SavedViewpoints.ReplaceWithCopy(loc.Parent, loc.Index, copy);
            var now = (SavedViewpoint)LocateViewpoint(doc, loc.Path, true).Item;
            return new { target = loc.Path, changed, before = before2, after = DescribeViewpoint(now.Viewpoint) };
        }

        private static void ApplyCamera(Viewpoint vp, Dictionary<string, object> body, List<string> changed)
        {
            string json = GetString(body, "camera_json", null);
            if (!string.IsNullOrWhiteSpace(json))
            {
                if (!vp.TrySetCamera(json)) throw new ArgumentException("camera_json was not accepted (AutodeskCommonCamera JSON from get_viewpoint)");
                changed.Add("camera_json");
            }

            string proj = GetString(body, "projection", null);
            if (proj != null)
            {
                switch (proj.Trim().ToLowerInvariant())
                {
                    case "perspective": vp.Projection = ViewpointProjection.Perspective; break;
                    case "orthographic": case "ortho": vp.Projection = ViewpointProjection.Orthographic; break;
                    default: throw new ArgumentException($"unknown projection '{proj}'. Valid: perspective, orthographic");
                }
                changed.Add("projection");
            }

            double[] pos = GetVector(body, "position");
            if (pos != null) { vp.Position = new Point3D(pos[0], pos[1], pos[2]); changed.Add("position"); }

            double[] target = GetVector(body, "target");
            double[] dir = GetVector(body, "direction");
            if (target != null && dir != null) throw new ArgumentException("pass target or direction, not both");
            if (target != null)
            {
                Point3D p = vp.Position;
                double dx = target[0] - p.X, dy = target[1] - p.Y, dz = target[2] - p.Z;
                double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (dist < 1e-9) throw new ArgumentException("target is at the camera position");
                vp.PointAt(new Point3D(target[0], target[1], target[2]));
                if (!body.ContainsKey("focal_distance")) vp.FocalDistance = dist;
                changed.Add("target");
            }
            if (dir != null)
            {
                if (Math.Abs(dir[0]) + Math.Abs(dir[1]) + Math.Abs(dir[2]) < 1e-12) throw new ArgumentException("direction must not be zero");
                vp.AlignDirection(new Vector3D(dir[0], dir[1], dir[2]));
                changed.Add("direction");
            }

            // Keep the picture upright: default up = the world up vector, else +Z.
            double[] up = GetVector(body, "up");
            if (up != null)
            {
                vp.AlignUp(new Vector3D(up[0], up[1], up[2]));
                changed.Add("up");
            }
            else if (target != null || dir != null)
            {
                Vector3D wu = vp.HasWorldUpVector ? new Vector3D(vp.WorldUpVector.X, vp.WorldUpVector.Y, vp.WorldUpVector.Z)
                                                  : new Vector3D(0, 0, 1);
                vp.AlignUp(wu);
            }

            if (body.ContainsKey("fov_deg"))
            {
                double fov = GetDouble(body, "fov_deg", 45);
                if (fov <= 0 || fov >= 180) throw new ArgumentException("fov_deg must be between 0 and 180");
                if (vp.Projection != ViewpointProjection.Perspective)
                    throw new ArgumentException("fov_deg is for a perspective camera; use ortho_height for orthographic");
                vp.HeightField = fov * Math.PI / 180.0;
                changed.Add("fov_deg");
            }
            if (body.ContainsKey("ortho_height"))
            {
                double h = GetDouble(body, "ortho_height", 0);
                if (h <= 0) throw new ArgumentException("ortho_height must be > 0");
                if (vp.Projection != ViewpointProjection.Orthographic)
                    throw new ArgumentException("ortho_height is for an orthographic camera (projection=orthographic)");
                vp.HeightField = h;
                changed.Add("ortho_height");
            }
            if (body.ContainsKey("focal_distance"))
            {
                double f = GetDouble(body, "focal_distance", 0);
                if (f <= 0) throw new ArgumentException("focal_distance must be > 0");
                vp.FocalDistance = f;
                changed.Add("focal_distance");
            }
            if (body.ContainsKey("linear_speed")) { vp.LinearSpeed = GetDouble(body, "linear_speed", 1); changed.Add("linear_speed"); }
            if (body.ContainsKey("angular_speed")) { vp.AngularSpeed = GetDouble(body, "angular_speed", 45) * Math.PI / 180.0; changed.Add("angular_speed"); }

            string light = GetString(body, "lighting", null);
            if (light != null)
            {
                if (!Enum.TryParse(light.Replace(" ", "").Replace("_", ""), true, out ViewpointLighting l))
                    throw new ArgumentException($"unknown lighting '{light}'. Valid: None, Headlight, SceneLights, FullLights");
                vp.Lighting = l;
                changed.Add("lighting");
            }
            string style = GetString(body, "render_style", null);
            if (style != null)
            {
                if (!Enum.TryParse(style.Replace(" ", "").Replace("_", ""), true, out ViewpointRenderStyle rs))
                    throw new ArgumentException($"unknown render_style '{style}'. Valid: FullRender, Shaded, Wireframe, HiddenLine");
                vp.RenderStyle = rs;
                changed.Add("render_style");
            }
            if (changed.Count == 0)
                throw new ArgumentException("nothing to change: pass position, target or direction, up, projection, fov_deg, " +
                                            "ortho_height, focal_distance, linear_speed, angular_speed, lighting, render_style or camera_json");
        }

        // ── Describe a camera ───────────────────────────────────────────────

        private static double[] Rotate(Rotation3D q, double x, double y, double z)
        {
            // Quaternion (A, B, C) = vector part, D = scalar part.
            double qx = q.A, qy = q.B, qz = q.C, qw = q.D;
            double tx = 2 * (qy * z - qz * y), ty = 2 * (qz * x - qx * z), tz = 2 * (qx * y - qy * x);
            return new[]
            {
                x + qw * tx + (qy * tz - qz * ty),
                y + qw * ty + (qz * tx - qx * tz),
                z + qw * tz + (qx * ty - qy * tx)
            };
        }

        private static double[] R6(double[] v) => v.Select(d => Math.Round(d, 6) == 0 ? 0 : Math.Round(d, 6)).ToArray();

        private static object DescribeViewpoint(Viewpoint vp)
        {
            if (!HasCamera(vp)) return new { cameraSet = false };
            double[] dir = Rotate(vp.Rotation, 0, 0, -1);
            double[] up = Rotate(vp.Rotation, 0, 1, 0);
            bool persp = vp.Projection == ViewpointProjection.Perspective;
            double? focal = vp.HasFocalDistance ? vp.FocalDistance : (double?)null;
            object section = null;
            try
            {
                ClipPlaneSet cps = vp.ClipPlanes;
                if (cps != null && cps.Enabled)
                {
                    BoundingBox3D b = cps.Box;
                    section = new
                    {
                        mode = cps.Mode.ToString(),
                        box = b == null || b.IsEmpty ? null : new
                        {
                            min = R6(new[] { b.Min.X, b.Min.Y, b.Min.Z }),
                            max = R6(new[] { b.Max.X, b.Max.Y, b.Max.Z })
                        }
                    };
                }
            }
            catch { }
            return new
            {
                position = R6(new[] { vp.Position.X, vp.Position.Y, vp.Position.Z }),
                direction = R6(dir),
                up = R6(up),
                target = focal.HasValue
                    ? R6(new[] { vp.Position.X + dir[0] * focal.Value, vp.Position.Y + dir[1] * focal.Value, vp.Position.Z + dir[2] * focal.Value })
                    : null,
                projection = persp ? "perspective" : "orthographic",
                fovDeg = persp ? Math.Round(vp.HeightField * 180.0 / Math.PI, 4) : (double?)null,
                orthoHeight = persp ? (double?)null : Math.Round(vp.HeightField, 6),
                aspectRatio = Math.Round(vp.AspectRatio, 4),
                focalDistance = focal.HasValue ? Math.Round(focal.Value, 6) : (double?)null,
                worldUp = vp.HasWorldUpVector ? R6(new[] { vp.WorldUpVector.X, vp.WorldUpVector.Y, vp.WorldUpVector.Z }) : null,
                linearSpeed = vp.HasLinearSpeed ? Math.Round(vp.LinearSpeed, 6) : (double?)null,
                angularSpeedDeg = vp.HasAngularSpeed ? Math.Round(vp.AngularSpeed * 180.0 / Math.PI, 4) : (double?)null,
                lighting = vp.HasLighting ? vp.Lighting.ToString() : null,
                renderStyle = vp.HasRenderStyle ? vp.RenderStyle.ToString() : null,
                section
            };
        }

        // ── Folders, rename, move, delete ───────────────────────────────────

        public static object ViewpointFolderCreate(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            string path = GetString(body, "path", null) ?? GetString(body, "name", null);
            if (SplitSetPath(path).Length == 0) throw new ArgumentException("path is required, e.g. 'Коллизии/Этаж 1'");
            SavedTree tree = ViewpointTree(doc);
            bool existed;
            try { GetSavedFolder(tree, path, false, out _); existed = true; }
            catch (InvalidOperationException) { existed = false; }
            GetSavedFolder(tree, path, true, out string realPath);
            return new { path = realPath, created = !existed };
        }

        public static object ViewpointRename(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            SetLoc loc = LocateViewpoint(doc, GetString(body, "name", null), false);
            string newName = (GetString(body, "new_name", null) ?? "").Trim();
            if (newName.Length == 0 || newName.Contains("/"))
                throw new ArgumentException("new_name is required and must not contain '/'");
            int clash = IndexByName(loc.Parent, newName);
            if (clash >= 0 && clash != loc.Index)
                throw new InvalidOperationException($"'{newName}' already exists in the same folder");
            doc.SavedViewpoints.EditDisplayName(loc.Item, newName);
            return new { oldPath = loc.Path, path = (loc.Folder.Length > 0 ? loc.Folder + "/" : "") + newName, type = ViewpointType(loc.Item) };
        }

        public static object ViewpointMove(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            SavedTree tree = ViewpointTree(doc);
            SetLoc loc = LocateViewpoint(doc, GetString(body, "name", null), false);
            string type = ViewpointType(loc.Item);
            string itemName = loc.Item.DisplayName;
            string oldPath = loc.Path;

            string folderPath = GetString(body, "folder", null);
            string target = folderPath == null ? loc.Folder : string.Join("/", SplitSetPath(folderPath));
            if (type == "folder" && (string.Equals(target, loc.Path, StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith(loc.Path + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"cannot move folder '{loc.Path}' into itself");

            GroupItem newParent = GetSavedFolder(tree, target, true, out string realTarget);
            bool sameParent = string.Equals(realTarget, loc.Folder, StringComparison.OrdinalIgnoreCase);
            if (!sameParent && IndexByName(newParent, itemName) >= 0)
                throw new InvalidOperationException($"'{itemName}' already exists in '{realTarget}'");

            loc = LocateViewpoint(doc, oldPath, false);
            int count = newParent.Children.Count;
            int max = sameParent ? count - 1 : count;
            int newIndex = body != null && body.ContainsKey("position")
                ? Math.Max(0, Math.Min(max, GetInt(body, "position", max)))
                : max;
            doc.SavedViewpoints.Move(loc.Parent, loc.Index, newParent, newIndex);
            return new { oldPath, path = (realTarget.Length > 0 ? realTarget + "/" : "") + itemName, position = newIndex, type };
        }

        public static object ViewpointDelete(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            SetLoc loc = LocateViewpoint(doc, GetString(body, "name", null), false);
            string type = ViewpointType(loc.Item);
            if (loc.Item is GroupItem g && !(loc.Item is SavedViewpoint) && g.Children.Count > 0 && !GetBool(body, "recursive", false))
                throw new InvalidOperationException(
                    $"{type} '{loc.Path}' is not empty ({g.Children.Count} items): pass recursive=true to delete it with its contents");
            doc.SavedViewpoints.RemoveAt(loc.Parent, loc.Index);
            return new { name = loc.Path, type, deleted = true };
        }
    }
}
