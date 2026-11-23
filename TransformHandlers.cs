// =============================================================================
//  TransformHandlers.cs — model position, as in Navisworks' Units and Transform
// =============================================================================
//  /model/transform/get     units, translation, rotation, scale, reflection and
//                           bounding box of one model or all of them
//  /model/transform/set     change any of them; mode "set" (like the dialog) or
//                           "add" (on top of the current position)
//  /model/transform/reset   back to the position the model had when the bridge
//                           first saw it in this document
//
//  A model's transform is not "extra" data: the IFC loader puts the file's
//  georeference there (e.g. a translation of 685198, 5379841, 434 m) and the
//  unit conversion of files in mm. Resetting to identity would throw that
//  away, so reset restores the remembered original instead.
//
//  Only models listed by /model/files can be moved: an appended .nwf expands
//  into its files (each one movable), an appended .nwd is one model whose
//  files can only be moved in the .nwf it was built from. The position is
//  stored in the open .nwf / .nwd: save the document afterwards.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Linq;

using Autodesk.Navisworks.Api;

using NavisApp = Autodesk.Navisworks.Api.Application;

namespace NavisworksMcpAddin
{
    internal static partial class Handlers
    {
        // ════════════════════════════════════════════════════════════════════
        //  MODEL TRANSFORM
        // ════════════════════════════════════════════════════════════════════

        // Original position per model, recorded before the bridge changes it.
        // Cleared whenever a document is opened, cleared or converted.
        private sealed class SavedTransform
        {
            public Units Units;
            public Transform3D Transform;
            public bool Reflected;
        }

        private static readonly Dictionary<string, SavedTransform> OriginalTransforms =
            new Dictionary<string, SavedTransform>(StringComparer.OrdinalIgnoreCase);

        internal static void ForgetOriginalTransforms()
        {
            OriginalTransforms.Clear();
        }

        private static string TransformKey(Document doc, Model m)
        {
            return (doc.FileName ?? "") + "|" + (m.FileName ?? m.SourceFileName ?? ModelLabel(m));
        }

        private static void RememberOriginal(Document doc, Model m)
        {
            string key = TransformKey(doc, m);
            if (OriginalTransforms.ContainsKey(key)) return;
            OriginalTransforms[key] = new SavedTransform
            {
                Units = m.Units,
                Transform = new Transform3D(m.Transform),
                Reflected = m.IsTransformReflected
            };
        }

        public static object ModelTransformGet(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            foreach (Model m in doc.Models) RememberOriginal(doc, m);
            if (body != null && (body.ContainsKey("index") || body.ContainsKey("name")))
            {
                int index = ResolveTopModel(doc, body);
                return DescribeTransform(doc, index);
            }

            var models = new List<object>();
            for (int i = 0; i < doc.Models.Count; i++) models.Add(DescribeTransform(doc, i));
            return new { documentUnits = doc.Units.ToString(), count = models.Count, models };
        }

        public static object ModelTransformSet(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            int index = ResolveTopModel(doc, body);
            Model model = doc.Models[index];
            RememberOriginal(doc, model);
            object before = DescribeTransform(doc, index);

            string mode = (GetString(body, "mode", "set") ?? "set").Trim().ToLowerInvariant();
            if (mode != "set" && mode != "add") throw new ArgumentException("mode must be set or add");
            bool add = mode == "add";

            double[] translation = GetVector(body, "translation");
            double? angle = body.ContainsKey("rotation_deg") ? GetDouble(body, "rotation_deg", 0) : (double?)null;
            double[] axis = GetVector(body, "rotation_axis") ?? new[] { 0.0, 0.0, 1.0 };
            double[] scale = GetVector(body, "scale");
            Units units = body.ContainsKey("units") ? ParseUnits(GetString(body, "units", null)) : model.Units;
            bool reflected = GetBool(body, "reflected", model.IsTransformReflected);
            if (translation == null && angle == null && scale == null &&
                !body.ContainsKey("units") && !body.ContainsKey("reflected"))
            {
                throw new ArgumentException("nothing to change: pass translation, rotation_deg, scale, units or reflected");
            }
            if (axis.All(v => v == 0)) throw new ArgumentException("rotation_axis must not be [0, 0, 0]");
            if (scale != null && scale.Any(v => v == 0)) throw new ArgumentException("scale must not contain 0");

            Transform3DComponents c = model.Transform.Factor();
            if (translation != null)
            {
                Vector3D t = c.Translation;
                c.Translation = add
                    ? new Vector3D(t.X + translation[0], t.Y + translation[1], t.Z + translation[2])
                    : new Vector3D(translation[0], translation[1], translation[2]);
            }
            if (angle != null)
            {
                var r = new Rotation3D(new UnitVector3D(axis[0], axis[1], axis[2]), angle.Value * Math.PI / 180.0);
                c.Rotation = add
                    ? Transform3D.Multiply(new Transform3D(c.Rotation), new Transform3D(r)).Factor().Rotation
                    : r;
            }
            if (scale != null)
            {
                Vector3D s = c.Scale;
                c.Scale = add
                    ? new Vector3D(s.X * scale[0], s.Y * scale[1], s.Z * scale[2])
                    : new Vector3D(scale[0], scale[1], scale[2]);
            }

            doc.Models.SetModelUnitsAndTransform(model, units, c.Combine(), reflected);
            return new { index, mode, before, after = DescribeTransform(doc, index), saveNeeded = true };
        }

        public static object ModelTransformReset(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            int index = ResolveTopModel(doc, body);
            Model model = doc.Models[index];
            object before = DescribeTransform(doc, index);

            if (!OriginalTransforms.TryGetValue(TransformKey(doc, model), out SavedTransform original))
            {
                return new { index, changed = false, note = "not changed through the bridge since the document was opened", before };
            }
            doc.Models.SetModelUnitsAndTransform(model, original.Units, new Transform3D(original.Transform), original.Reflected);
            return new { index, changed = true, before, after = DescribeTransform(doc, index), saveNeeded = true };
        }

        // ── Transform helpers ───────────────────────────────────────────────

        private static object DescribeTransform(Document doc, int index)
        {
            Model m = doc.Models[index];
            Transform3D t = m.Transform;
            Transform3DComponents c = t.Factor();
            AxisAndAngleResult r = c.Rotation.ToAxisAndAngle();
            double angle = r.Angle * 180.0 / Math.PI;
            BoundingBox3D box = m.RootItem?.BoundingBox();

            return new
            {
                index,
                name = ModelLabel(m),
                units = m.Units.ToString(),
                hasTransform = !t.IsIdentity() || m.IsTransformReflected,
                translation = Round(c.Translation.X, c.Translation.Y, c.Translation.Z),
                rotation = new
                {
                    angleDeg = Math.Round(angle, 6),
                    axis = Math.Abs(angle) < 1e-9 ? new[] { 0.0, 0.0, 1.0 } : Round(r.Axis.X, r.Axis.Y, r.Axis.Z)
                },
                scale = Round(c.Scale.X, c.Scale.Y, c.Scale.Z),
                reflected = m.IsTransformReflected,
                // In document units, after the transform: a file far from the
                // others here is the usual sign of a shifted insertion point.
                box = box == null || box.IsEmpty ? null : new
                {
                    min = Round(box.Min.X, box.Min.Y, box.Min.Z),
                    max = Round(box.Max.X, box.Max.Y, box.Max.Z),
                    center = Round(box.Center.X, box.Center.Y, box.Center.Z)
                }
            };
        }

        private static double[] Round(double x, double y, double z)
        {
            return new[] { Math.Round(x, 6), Math.Round(y, 6), Math.Round(z, 6) };
        }

        // Like ResolveModelIndex, but a name that is only found inside an .nwd
        // gets a hint instead of "not found".
        private static int ResolveTopModel(Document doc, Dictionary<string, object> body)
        {
            try
            {
                return ResolveModelIndex(doc, body);
            }
            catch (InvalidOperationException) when (GetString(body, "name", null) is string name)
            {
                for (int i = 0; i < doc.Models.Count; i++)
                {
                    List<string> inside = ContainedFiles(doc.Models[i]);
                    if (inside != null && inside.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException(
                            $"'{name}' is inside '{ModelLabel(doc.Models[i])}', an .nwd: its files cannot be moved one by one. " +
                            "Move the whole .nwd, or open the .nwf it was built from and move the file there.");
                    }
                }
                throw;
            }
        }

        // [x, y, z] from a JSON array; a single number means the same value on
        // all three axes (handy for a uniform scale). null when absent.
        private static double[] GetVector(Dictionary<string, object> body, string key)
        {
            if (body == null || !body.TryGetValue(key, out object raw) || raw == null) return null;
            if (raw is double d) return new[] { d, d, d };
            if (raw is System.Collections.IEnumerable seq && !(raw is string))
            {
                var values = new List<double>();
                foreach (object o in seq)
                {
                    if (o is double v) values.Add(v);
                    else throw new ArgumentException($"{key} must contain numbers");
                }
                if (values.Count == 3) return values.ToArray();
            }
            throw new ArgumentException($"{key} must be [x, y, z] (or one number for all axes)");
        }

        private static Units ParseUnits(string s)
        {
            if (!string.IsNullOrWhiteSpace(s) && Enum.TryParse(s.Trim(), true, out Units u)) return u;
            throw new ArgumentException(
                $"unknown units '{s}'. Valid: {string.Join(", ", Enum.GetNames(typeof(Units)))}");
        }
    }
}
