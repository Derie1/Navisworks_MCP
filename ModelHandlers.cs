// =============================================================================
//  ModelHandlers.cs — document / file management routes
// =============================================================================
//  /model/files       list loaded files (index, nesting, on-disk state)
//  /model/open        open a file, replacing the current document
//  /model/new         clear the document (File > New)
//  /model/append      append files
//  /model/remove      remove a loaded file by index or name
//  /model/update      reload files that changed on disk (Document.UpdateFiles)
//  /model/save        save as .nwf / .nwd in a chosen file-format version
//  /model/publish     publish .nwd with export options
//  /model/convert     open each input file and save it as .nwd
//
//  Every route that would throw away the current document refuses to run
//  while it has unsaved changes unless the caller passes discard_changes.
// =============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Autodesk.Navisworks.Api;

using NavisApp = Autodesk.Navisworks.Api.Application;

namespace NavisworksMcpAddin
{
    internal static partial class Handlers
    {
        // ════════════════════════════════════════════════════════════════════
        //  MODEL / FILE MANAGEMENT
        // ════════════════════════════════════════════════════════════════════

        public static object ModelFiles()
        {
            var doc = NavisApp.ActiveDocument
                ?? throw new InvalidOperationException("no active document");
            return DescribeDocument(doc);
        }

        public static object ModelOpen(Dictionary<string, object> body)
        {
            var doc = NavisApp.ActiveDocument
                ?? throw new InvalidOperationException("no active document");
            string path = RequireExistingFile(body, "path");
            RequireNoUnsavedChanges(doc, body);

            if (!doc.TryOpenFile(path))
            {
                throw new InvalidOperationException($"Navisworks could not open '{path}'");
            }
            return DescribeDocument(doc);
        }

        public static object ModelNew(Dictionary<string, object> body)
        {
            var doc = NavisApp.ActiveDocument
                ?? throw new InvalidOperationException("no active document");
            RequireNoUnsavedChanges(doc, body);
            doc.Clear();
            return new { cleared = true };
        }

        public static object ModelAppend(Dictionary<string, object> body)
        {
            var doc = NavisApp.ActiveDocument
                ?? throw new InvalidOperationException("no active document");

            List<string> paths = GetStringList(body, "paths");
            if (paths.Count == 0) throw new ArgumentException("paths is required (array of file paths)");
            var missing = paths.Where(p => !File.Exists(p)).ToList();
            if (missing.Count > 0)
            {
                throw new FileNotFoundException("files not found: " + string.Join("; ", missing));
            }

            int before = doc.Models.Count;
            if (!doc.TryAppendFiles(paths))
            {
                throw new InvalidOperationException("Navisworks could not append the files");
            }
            return new
            {
                appended = paths.Count,
                modelsBefore = before,
                modelsAfter = doc.Models.Count,
                document = DescribeDocument(doc)
            };
        }

        public static object ModelRemove(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            int index = ResolveModelIndex(doc, body);
            string removed = ModelLabel(doc.Models[index]);

            if (!doc.TryRemoveFile(index))
            {
                throw new InvalidOperationException($"Navisworks could not remove file #{index} ({removed})");
            }
            return new { removedIndex = index, removed, document = DescribeDocument(doc) };
        }

        public static object ModelUpdate()
        {
            var doc = RequireDocument();
            doc.UpdateFiles();
            return DescribeDocument(doc);
        }

        public static object ModelSave(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            string path = GetString(body, "path", null) ?? doc.FileName;
            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentException("path is required: the document has never been saved");
            }
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".nwf" && ext != ".nwd")
            {
                throw new ArgumentException("path must end with .nwf or .nwd");
            }
            EnsureDirectory(path);

            string label = VersionLabel(GetString(body, "version", null));
            DocumentFileVersion version = ParseFileVersion(label);

            // SaveFile to .nwd turns the open document into that .nwd: every
            // loaded file collapses into one model, and a later save of the
            // .nwf would then reference the .nwd instead of the sources.
            // ExportToNwd writes the same file but leaves the document as is.
            bool keepOpen = GetBool(body, "keep_open", true);
            bool ok = ext == ".nwd" && keepOpen
                ? doc.TryExportToNwd(path, new NwdExportOptions { FileVersion = (int)version })
                : doc.TrySaveFile(path, version);
            if (!ok)
            {
                throw new InvalidOperationException($"Navisworks could not save '{path}'");
            }
            return new
            {
                saved = path,
                version = label,
                bytes = new FileInfo(path).Length,
                openDocument = doc.FileName
            };
        }

        public static object ModelPublish(Dictionary<string, object> body)
        {
            var doc = RequireDocument();
            string path = GetString(body, "path", null)
                ?? throw new ArgumentException("path is required");
            if (!path.EndsWith(".nwd", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("path must end with .nwd");
            }
            // Navisworks publishes only from an open .nwd; from an .nwf it
            // pops a modal "You may only publish NWD files" dialog that blocks
            // the UI thread (and with it this bridge) until someone clicks OK.
            if (!string.Equals(Path.GetExtension(doc.FileName ?? ""), ".nwd", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Navisworks can publish only when the open document is an .nwd. " +
                    "Save it as .nwd with keep_open=false (or open an .nwd) first.");
            }
            EnsureDirectory(path);

            string label = VersionLabel(GetString(body, "version", null));
            var options = new NwdExportOptions
            {
                // FileVersion takes the raw format number, which is what the
                // DocumentFileVersion values are (0 = current).
                FileVersion = (int)ParseFileVersion(label),
                EmbedXrefs = GetBool(body, "embed_xrefs", false),
                ExcludeHiddenItems = GetBool(body, "exclude_hidden", false),
                PreventObjectPropertyExport = GetBool(body, "strip_properties", false)
            };
            var props = new PublishProperties
            {
                Title = GetString(body, "title", doc.Title) ?? "",
                Author = GetString(body, "author", Environment.UserName),
                Comments = GetString(body, "comments", "") ?? "",
                AllowResave = GetBool(body, "allow_resave", true)
            };

            if (!doc.TryPublishFile(path, options, props))
            {
                throw new InvalidOperationException($"Navisworks could not publish '{path}'");
            }
            return new { published = path, version = label, bytes = new FileInfo(path).Length };
        }

        // Open every input and save it next to itself (or into output_dir) as
        // .nwd. Replaces the current document; reopens it at the end when
        // restore_document is true and it had a file name.
        public static object ModelConvert(Dictionary<string, object> body)
        {
            var doc = NavisApp.ActiveDocument
                ?? throw new InvalidOperationException("no active document");

            List<string> inputs = GetStringList(body, "paths");
            if (inputs.Count == 0) throw new ArgumentException("paths is required (array of file paths)");
            RequireNoUnsavedChanges(doc, body);

            string outDir = GetString(body, "output_dir", null);
            bool overwrite = GetBool(body, "overwrite", false);
            bool restore = GetBool(body, "restore_document", true);
            string label = VersionLabel(GetString(body, "version", null));
            DocumentFileVersion version = ParseFileVersion(label);
            string original = doc.IsClear ? null : doc.FileName;

            var results = new List<object>();
            int converted = 0;
            foreach (string input in inputs)
            {
                string output = Path.Combine(outDir ?? Path.GetDirectoryName(input) ?? "",
                                             Path.GetFileNameWithoutExtension(input) + ".nwd");
                string error = null;
                if (!File.Exists(input)) error = "input not found";
                else if (File.Exists(output) && !overwrite) error = "output exists (pass overwrite=true)";
                else if (string.Equals(Path.GetFullPath(input), Path.GetFullPath(output), StringComparison.OrdinalIgnoreCase))
                    error = "input is already this .nwd";
                else
                {
                    EnsureDirectory(output);
                    if (!doc.TryOpenFile(input)) error = "open failed";
                    else if (!doc.TrySaveFile(output, version)) error = "save failed";
                }
                if (error == null) converted++;
                results.Add(new { input, output, ok = error == null, error });
            }

            bool restored = false;
            if (restore && original != null && File.Exists(original))
            {
                restored = doc.TryOpenFile(original);
            }
            else
            {
                doc.Clear();
            }

            return new
            {
                version = label,
                converted,
                failed = results.Count - converted,
                restoredDocument = restored ? original : null,
                results
            };
        }

        // ── Model helpers ───────────────────────────────────────────────────

        private static object DescribeDocument(Document doc)
        {
            var files = new List<object>();
            for (int i = 0; i < doc.Models.Count; i++)
            {
                Model m = doc.Models[i];
                string file = m.FileName ?? "";
                string source = m.SourceFileName ?? "";

                files.Add(new
                {
                    index = i,
                    name = ModelLabel(m),
                    fileName = file,
                    sourceFile = source,
                    // An appended .nwf expands into its files (one model each);
                    // an appended .nwd stays one model with the files it was
                    // built from as child nodes. Those can't be removed one by
                    // one here: open the .nwf the .nwd was saved from instead.
                    contains = ContainedFiles(m),
                    fileExists = file.Length > 0 && File.Exists(file),
                    fileModified = FileTime(file),
                    sourceExists = source.Length > 0 && File.Exists(source),
                    sourceModified = FileTime(source)
                });
            }

            return new
            {
                fileName = doc.FileName,
                title = doc.Title,
                isClear = doc.IsClear,
                isModified = doc.IsModified,
                fileVersion = doc.FileVersion.ToString(),
                modelCount = doc.Models.Count,
                files
            };
        }

        private static string ModelLabel(Model m)
        {
            string name = m.RootItem?.DisplayName;
            if (!string.IsNullOrEmpty(name)) return name;
            return Path.GetFileName(m.FileName ?? m.SourceFileName ?? "") ?? "";
        }

        // Names of the file nodes directly under a model's root (an .nwd built
        // from several files); null when the model is a single source file.
        private static List<string> ContainedFiles(Model m)
        {
            if (m.RootItem == null) return null;
            var names = new List<string>();
            foreach (ModelItem child in m.RootItem.Children)
            {
                if (IsFileNode(child)) names.Add(child.DisplayName);
            }
            return names.Count > 0 ? names : null;
        }

        // Nearest ancestor-or-self that is the root of a loaded model.
        private static string ContainingFileName(ModelItem item)
        {
            for (ModelItem cur = item; cur != null; cur = cur.Parent)
            {
                if (cur.HasModel) return cur.DisplayName;
            }
            return null;
        }

        private static string FileTime(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            return File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static int ResolveModelIndex(Document doc, Dictionary<string, object> body)
        {
            if (body != null && body.ContainsKey("index"))
            {
                int index = GetInt(body, "index", -1);
                if (index < 0 || index >= doc.Models.Count)
                {
                    throw new ArgumentException($"index {index} out of range 0..{doc.Models.Count - 1}");
                }
                return index;
            }

            string name = GetString(body, "name", null)
                ?? throw new ArgumentException("pass index or name (see list_model_files)");
            var hits = new List<int>();
            for (int i = 0; i < doc.Models.Count; i++)
            {
                Model m = doc.Models[i];
                if (string.Equals(ModelLabel(m), name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(m.FileName ?? ""), name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(Path.GetFileName(m.SourceFileName ?? ""), name, StringComparison.OrdinalIgnoreCase))
                {
                    hits.Add(i);
                }
            }
            if (hits.Count == 0) throw new InvalidOperationException($"no loaded file named '{name}'");
            if (hits.Count > 1)
            {
                throw new InvalidOperationException(
                    $"'{name}' matches files {string.Join(", ", hits)}; pass index instead");
            }
            return hits[0];
        }

        private static void RequireNoUnsavedChanges(Document doc, Dictionary<string, object> body)
        {
            if (!doc.IsClear && doc.IsModified && !GetBool(body, "discard_changes", false))
            {
                throw new InvalidOperationException(
                    "the current document has unsaved changes; save it first or pass discard_changes=true");
            }
        }

        private static string RequireExistingFile(Dictionary<string, object> body, string key)
        {
            string path = GetString(body, key, null)
                ?? throw new ArgumentException($"{key} is required");
            if (!File.Exists(path)) throw new FileNotFoundException($"file not found: {path}");
            return path;
        }

        private static void EnsureDirectory(string path)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }

        // Several releases share one format number (2016-2025 are all 448), so
        // DocumentFileVersion.ToString() can name a different year than asked
        // for; report the caller's own label instead.
        private static string VersionLabel(string s)
        {
            return string.IsNullOrWhiteSpace(s) ? "current" : s.Trim().Replace("Navisworks", "");
        }

        // "2026" / "Navisworks2026" / "current" (default) -> DocumentFileVersion
        private static DocumentFileVersion ParseFileVersion(string s)
        {
            if (string.IsNullOrWhiteSpace(s) || s.Trim().Equals("current", StringComparison.OrdinalIgnoreCase))
            {
                return DocumentFileVersion.Current;
            }
            string name = s.Trim();
            if (!name.StartsWith("Navisworks", StringComparison.OrdinalIgnoreCase)) name = "Navisworks" + name;
            if (Enum.TryParse(name, true, out DocumentFileVersion v)) return v;

            var valid = Enum.GetNames(typeof(DocumentFileVersion))
                            .Select(n => n.Replace("Navisworks", ""));
            throw new ArgumentException($"unknown version '{s}'. Valid: {string.Join(", ", valid)}");
        }

        private static List<string> GetStringList(Dictionary<string, object> body, string key)
        {
            var list = new List<string>();
            if (body == null || !body.TryGetValue(key, out object raw) || raw == null) return list;
            if (raw is string single) { list.Add(single); return list; }
            if (raw is System.Collections.IEnumerable seq)
            {
                foreach (object o in seq)
                {
                    if (o != null && o.ToString().Length > 0) list.Add(o.ToString());
                }
            }
            return list;
        }
    }
}
