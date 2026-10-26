// Copyright (C) 2026 Leonardo Bandini
// SPDX-License-Identifier: GPL-2.0-or-later
// Additional permission for the separately installed SAP2000 API:
// see SAP2000-LINKING-EXCEPTION.md.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
#if GMSH_V2
using CSiAPIv1;
#else
using SAP2000v1;
#endif

namespace SapQuadTransitionPlugin
{
    // The CSV contains every nonempty editable SAP model table before meshing,
    // including area/joint assignments, loads, patterns, constraints and
    // overwrites. Base64 fields allow arbitrary SAP text inside a CSV record.
    internal sealed class ModelCsvSnapshot
    {
        private sealed class Table
        {
            internal string Key;
            internal int Version;
            internal string[] Fields;
            internal string[] Data;
            internal int Records { get { return Data.Length / Fields.Length; } }
        }

        private sealed class SnapshotData
        {
            internal List<Table> Tables;
            internal Dictionary<string, double[]> JointCoordinates;
            internal List<DesignOverwrite> DesignOverwrites;
        }

        private sealed class DesignOverwrite
        {
            internal string Area;
            internal string Code;
            internal int Item;
            internal double Value;
        }

        internal readonly string Path;
        internal readonly string[] UnpreservedDesignTables;
        private readonly string modelPath;
        private readonly byte[] digest;
        internal bool WasApplied { get; private set; }
        internal bool RestoreAttempted { get; private set; }

        internal void MarkApplied() { WasApplied = true; }

        private ModelCsvSnapshot(string path, string modelPath, byte[] digest, string[] unpreservedDesignTables)
        {
            Path = path;
            this.modelPath = modelPath;
            this.digest = digest;
            UnpreservedDesignTables = unpreservedDesignTables;
        }

        private static void Check(int result, string operation, string api = null)
        {
            if (result != 0) throw new InvalidOperationException("SAP2000 error while " + operation +
                (api == null ? "" : " [API: " + api + "]") + ": code " + result);
        }

        private static T WithAllLoadPatterns<T>(cSapModel model, Func<T> operation)
        {
            int previousCount = 0, count = 0;
            string[] previous = null, all = null;
            Check(model.DatabaseTables.GetLoadPatternsSelectedForDisplay(ref previousCount, ref previous),
                "reading selected load patterns before snapshot", "DatabaseTables.GetLoadPatternsSelectedForDisplay");
            Check(model.LoadPatterns.GetNameList(ref count, ref all), "listing load patterns for snapshot", "LoadPatterns.GetNameList");
            if (count < 0 || (all ?? new string[0]).Length != count ||
                previousCount < 0 || (previous ?? new string[0]).Length < previousCount)
                throw new InvalidDataException("SAP2000 load pattern selection has an invalid shape: available=" +
                    count + "/" + (all == null ? -1 : all.Length) + ", selected=" + previousCount +
                    "/" + (previous == null ? -1 : previous.Length) + ".");
            // Some SAP builds return one blank sentinel even when the selected
            // count is zero. Only the reported count is significant.
            string[] original = (previous ?? new string[0]).Take(previousCount).ToArray();
            string[] selected = (string[])(all ?? new string[0]).Clone();
            Check(model.DatabaseTables.SetLoadPatternsSelectedForDisplay(ref selected),
                "including all load patterns in snapshot tables", "DatabaseTables.SetLoadPatternsSelectedForDisplay");
            try { return operation(); }
            finally
            {
                string[] restore = (string[])original.Clone();
                Check(model.DatabaseTables.SetLoadPatternsSelectedForDisplay(ref restore),
                    "restoring the previous load pattern table selection", "DatabaseTables.SetLoadPatternsSelectedForDisplay");
            }
        }

        private static string B64(string value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
        }

        private static string UnB64(string value)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }

        private static void RecordUndoMethod(string method)
        {
            string path = Environment.GetEnvironmentVariable("SAP_QUAD_UNDO_TRACE");
            if (!string.IsNullOrEmpty(path)) File.AppendAllText(path, method + Environment.NewLine);
        }

        private static byte[] Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var input = File.OpenRead(path)) return sha.ComputeHash(input);
        }

        private static IEnumerable<string[]> CsvRows(string csv)
        {
            var row = new List<string>();
            var value = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < csv.Length; i++)
            {
                char c = csv[i];
                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < csv.Length && csv[i + 1] == '"') { value.Append('"'); i++; }
                        else quoted = false;
                    }
                    else value.Append(c);
                }
                else if (c == '"' && value.Length == 0) quoted = true;
                else if (c == ',') { row.Add(value.ToString()); value.Clear(); }
                else if (c == '\r' || c == '\n')
                {
                    if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                    row.Add(value.ToString()); value.Clear();
                    yield return row.ToArray(); row.Clear();
                }
                else if (c == '"') throw new InvalidDataException("Unexpected quote in SAP2000 table CSV.");
                else value.Append(c);
            }
            if (quoted) throw new InvalidDataException("Unclosed quoted SAP2000 table CSV field.");
            if (row.Count != 0 || value.Length != 0)
            {
                row.Add(value.ToString());
                yield return row.ToArray();
            }
        }

        internal static bool CsvRowMatchesEditingData(string[] csvRow, string[] data, int dataOffset, int width)
        {
            if (csvRow == null || data == null || width < 0 || dataOffset < 0 ||
                dataOffset + width > data.Length || csvRow.Length < width)
                return false;
            var memo = new Dictionary<long, int>();
            Func<int, int, int> countAlignments = null;
            countAlignments = (field, cell) =>
            {
                if (field == width) return cell == csvRow.Length ? 1 : 0;
                long state = ((long)field << 32) | (uint)cell;
                int cached;
                if (memo.TryGetValue(state, out cached)) return cached;
                int remainingFields = width - field - 1;
                int lastCell = csvRow.Length - remainingFields - 1;
                int matches = 0;
                var value = new StringBuilder();
                for (int end = cell; end <= lastCell; end++)
                {
                    if (end != cell) value.Append(',');
                    value.Append(csvRow[end]);
                    if (!string.Equals(value.ToString(), data[dataOffset + field], StringComparison.Ordinal))
                        continue;
                    matches += countAlignments(field + 1, end + 1);
                    if (matches > 1) break;
                }
                memo[state] = matches;
                return matches;
            };
            return countAlignments(0, 0) == 1;
        }

        internal static string[] RecoverFieldKeys(cSapModel model, string key, int version,
            int records, string[] data, int width, string groupName = "")
        {
            int csvVersion = 0, metadataVersion = 0, metadataCount = 0;
            string csv = "";
            Check(model.DatabaseTables.GetTableForEditingCSVString(key, groupName, ref csvVersion, ref csv),
                "reading CSV header for " + key, "DatabaseTables.GetTableForEditingCSVString");
            string[] keys = null, names = null, descriptions = null, units = null;
            bool[] importable = null;
            Check(model.DatabaseTables.GetAllFieldsInTable(key, ref metadataVersion, ref metadataCount,
                ref keys, ref names, ref descriptions, ref units, ref importable),
                "reading field metadata for " + key, "DatabaseTables.GetAllFieldsInTable");
            if (csvVersion != version || metadataVersion != version || keys == null ||
                keys.Length != metadataCount || data == null || data.Length != records * width)
                throw new InvalidDataException("SAP2000 table versions or array shape differ for " + key +
                    ": editingVersion=" + version + " csvVersion=" + csvVersion +
                    " metadataVersion=" + metadataVersion + " metadataCount=" + metadataCount +
                    " keyCount=" + (keys == null ? -1 : keys.Length) +
                    " width=" + width + " records=" + records +
                    " dataCount=" + (data == null ? -1 : data.Length));
            using (IEnumerator<string[]> rows = CsvRows(csv ?? "").GetEnumerator())
            {
                if (!rows.MoveNext()) throw new InvalidDataException("Empty SAP2000 table CSV: " + key);
                string[] header = rows.Current;
                if (header.Length != width) throw new InvalidDataException("CSV column count differs from editing array for " + key);
                header[0] = header[0].TrimStart('\ufeff');
                var available = new HashSet<string>(keys, StringComparer.Ordinal);
                if (header.Any(field => string.IsNullOrWhiteSpace(field) || !available.Contains(field)) ||
                    header.Distinct(StringComparer.Ordinal).Count() != width)
                    throw new InvalidDataException("CSV field keys are missing, repeated or absent from SAP2000 metadata for " + key);
                for (int record = 0; record < records; record++)
                {
                    bool hasRow = rows.MoveNext();
                    if (!hasRow || !CsvRowMatchesEditingData(rows.Current, data, record * width, width))
                        throw new InvalidDataException("CSV row cannot be aligned with the editing array for " + key +
                            " at row " + record + ": csvWidth=" + (hasRow ? rows.Current.Length : -1) +
                            " editingWidth=" + width + " csvHeader=" + string.Join("|", header));
                }
                if (rows.MoveNext() && rows.Current.Any(value => value.Length != 0))
                    throw new InvalidDataException("CSV has extra rows not in the editing array for " + key);
                return header;
            }
        }

        private static Table CompleteFilteredEditingTable(cSapModel model, string key, int editingVersion,
            int editingRecords, string[] editingFields, string[] editingData, string groupName)
        {
            int version = 0, records = 0;
            string[] groups = null, fields = null, data = null;
            int code = model.DatabaseTables.GetTableForDisplayArray(key, ref groups, groupName,
                ref version, ref fields, ref records, ref data);
            if (code != 0 || records <= editingRecords || fields == null || fields.Length == 0 ||
                data == null || data.Length != records * fields.Length)
                return null;
            if (version != editingVersion || editingFields == null || editingFields.Length == 0)
                throw new InvalidDataException("Filtered editing/display schema differs for " + key + ".");

            string[] targetFields = editingFields;
            if (targetFields.Any(string.IsNullOrWhiteSpace))
                targetFields = RecoverFieldKeys(model, key, editingVersion, editingRecords,
                    editingData, editingFields.Length, groupName);
            int metadataVersion = 0, metadataCount = 0;
            string[] metadataKeys = null, names = null, descriptions = null, units = null;
            bool[] importable = null;
            Check(model.DatabaseTables.GetAllFieldsInTable(key, ref metadataVersion, ref metadataCount,
                ref metadataKeys, ref names, ref descriptions, ref units, ref importable),
                "verifying fallback table fields for " + key, "DatabaseTables.GetAllFieldsInTable");
            if (metadataVersion != version || metadataKeys == null || importable == null ||
                metadataKeys.Length != metadataCount || importable.Length != metadataCount ||
                targetFields.Any(field => string.IsNullOrWhiteSpace(field) ||
                    Array.IndexOf(metadataKeys, field) < 0 || !importable[Array.IndexOf(metadataKeys, field)]) ||
                targetFields.Distinct(StringComparer.Ordinal).Count() != targetFields.Length)
                throw new InvalidDataException("The full display table cannot be mapped to importable fields for " + key + ".");

            int[] projection = targetFields.Select(field => Array.IndexOf(fields, field)).ToArray();
            for (int column = 0; column < projection.Length; column++)
            {
                if (projection[column] >= 0) continue;
                // SAP omits the all-blank T editing column when displaying
                // Cartesian joint coordinates; never invent it for polar points.
                if (key != "Joint Coordinates" || targetFields[column] != "T" ||
                    Array.IndexOf(fields, "CoordType") < 0 ||
                    Enumerable.Range(0, records).Any(row => !string.Equals(
                        data[row * fields.Length + Array.IndexOf(fields, "CoordType")],
                        "Cartesian", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("The full display table omits editing field " +
                        targetFields[column] + " for " + key + ".");
            }

            var projected = new string[records * targetFields.Length];
            for (int row = 0; row < records; row++)
                for (int column = 0; column < targetFields.Length; column++)
                    projected[row * targetFields.Length + column] = projection[column] < 0 ? "" :
                        data[row * fields.Length + projection[column]] ?? "";
            Table result = new Table { Key = key, Version = version, Fields = targetFields, Data = projected };
            if (key == "Connectivity - Area" || key == "Area Section Assignments" ||
                key == "Joint Coordinates")
            {
                bool joint = key == "Joint Coordinates";
                int objectCount = 0;
                string[] objectNames = null;
                Check(joint ? model.PointObj.GetNameList(ref objectCount, ref objectNames) :
                    model.AreaObj.GetNameList(ref objectCount, ref objectNames),
                    "checking full display fallback coverage for " + key,
                    joint ? "PointObj.GetNameList" : "AreaObj.GetNameList");
                int objectColumn = Array.IndexOf(targetFields, joint ? "Joint" : "Area");
                if (objectColumn < 0 || objectNames == null || objectNames.Length != objectCount ||
                    !new HashSet<string>(Enumerable.Range(0, records).Select(row =>
                        projected[row * targetFields.Length + objectColumn]),
                        StringComparer.OrdinalIgnoreCase).SetEquals(objectNames))
                    throw new InvalidDataException("The display fallback does not include every " +
                        (joint ? "joint" : "area") + " for " + key + ".");
            }
            cPlugin.TraceCancellationStage("snapshot-full-display-fallback " + key + " rows=" + records);
            return result;
        }

        private static bool IsObjectAssignmentTable(string key)
        {
            return key.EndsWith(" Assignments", StringComparison.OrdinalIgnoreCase) ||
                key.IndexOf(" Loads - ", StringComparison.OrdinalIgnoreCase) >= 0 ||
                key.StartsWith("Connectivity - ", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasMeaningfulDisplayRows(cSapModel model, string key)
        {
            int version = 0, records = 0;
            string[] groups = null, fields = null, values = null;
            int code = model.DatabaseTables.GetTableForDisplayArray(key, ref groups, "All",
                ref version, ref fields, ref records, ref values);
            return code == 0 && records > 0 && fields != null && fields.Length > 0 &&
                fields.Any(field => !string.IsNullOrWhiteSpace(field)) &&
                values != null && values.Length == records * fields.Length &&
                values.Any(value => !string.IsNullOrWhiteSpace(value));
        }

        private static Table Read(cSapModel model, string key, bool duringCapture = false)
        {
            int version = 0, records = 0;
            string[] fields = null, data = null;
            // The Interactive Database Editor can retain a selected-only filter
            // across model changes. A rollback snapshot must request all objects.
            string groupName = duringCapture ? "All" : "";
            int code = model.DatabaseTables.GetTableForEditingArray(key, groupName, ref version,
                ref fields, ref records, ref data);
            if (code != 0)
            {
                // Log capabilities, not model data: an importable table can be
                // unreadable through the editing API in a newer SAP build.
                try
                {
                    int displayVersion = 0, displayRecords = 0;
                    string[] requested = null, shown = null, displayed = null;
                    int displayCode = model.DatabaseTables.GetTableForDisplayArray(key,
                        ref requested, groupName, ref displayVersion, ref shown, ref displayRecords, ref displayed);
                    string directory = System.IO.Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData), "SAP2000QuadMesher");
                    Directory.CreateDirectory(directory);
                    File.AppendAllText(System.IO.Path.Combine(directory, "snapshot-table-diagnostics.log"),
                        DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) +
                        " key=" + key + " edit=" + code + " display=" + displayCode +
                        " displayVersion=" + displayVersion + " displayRecords=" + displayRecords +
                        " displayFields=" + string.Join("|", shown ?? new string[0]) + Environment.NewLine);
                }
                catch (Exception error)
                {
                    cPlugin.TraceCancellationStage("snapshot-table-diagnostic-failed " + key + " " + error.GetType().Name);
                }
            }
            // Unreadable global options do not belong to the area geometry.
            // Unreadable design overwrites are separately reported to the user
            // as an explicit preservation warning. Never skip connectivity,
            // loads, springs, or other object-bound assignments silently.
            if (duringCapture && code == -96 &&
                (key.StartsWith("Analysis Options", StringComparison.OrdinalIgnoreCase) ||
                 key.StartsWith("Preferences - ", StringComparison.OrdinalIgnoreCase)))
                return null;
            if (duringCapture && code == -96 &&
                ((key.StartsWith("Overwrites - ", StringComparison.OrdinalIgnoreCase) &&
                  key.IndexOf("Design", StringComparison.OrdinalIgnoreCase) >= 0) ||
                  key == "Area Design Procedures"))
                return null;
            if (duringCapture && code == 1 &&
                (string.Equals(System.IO.Path.GetExtension(model.GetModelFilename(true)), ".edb",
                    StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(System.IO.Path.GetExtension(model.GetModelFilename(true)), ".$et",
                    StringComparison.OrdinalIgnoreCase)) &&
                (key == "Story Definitions" || key == "Tower and Base Story Definitions"))
            {
                // ETABS exposes these host-wide story definitions for display,
                // but not as editable snapshot tables. Meshing does not edit
                // stories; validate their full display shape before omitting
                // them from the geometry/assignment rollback snapshot.
                int displayVersion = 0, displayRecords = 0;
                string[] requested = null, shown = null, displayed = null;
                int displayCode = model.DatabaseTables.GetTableForDisplayArray(key,
                    ref requested, groupName, ref displayVersion, ref shown, ref displayRecords, ref displayed);
                if (displayCode != 0 || displayRecords < 1 || shown == null || shown.Length == 0 ||
                    displayed == null || displayed.Length != displayRecords * shown.Length)
                    throw new InvalidDataException("ETABS story definitions are not fully readable: " + key + ".");
                cPlugin.TraceCancellationStage("snapshot-etabs-immutable-story-table " + key);
                return null;
            }
            Check(code, "reading snapshot table " + key, "DatabaseTables.GetTableForEditingArray");
            if (duringCapture && key == "Area Design Procedures" && records == 0)
            {
                int displayVersion = 0, displayRecords = 0, modelAreas = 0;
                string[] groups = null, shown = null, displayed = null, areaNames = null;
                int displayCode = model.DatabaseTables.GetTableForDisplayArray(key, ref groups, groupName,
                    ref displayVersion, ref shown, ref displayRecords, ref displayed);
                // Code 1 means SAP has no accessible design-procedure rows.
                // Mark this optional design table as unpreserved instead of
                // treating the empty editing response as a complete snapshot.
                if (displayCode == 1) return null;
                Check(displayCode, "reading visible area design procedures for the snapshot",
                    "DatabaseTables.GetTableForDisplayArray");
                Check(model.AreaObj.GetNameList(ref modelAreas, ref areaNames),
                    "checking snapshot design-procedure coverage", "AreaObj.GetNameList");
                if (displayRecords == 0)
                    return new Table { Key = key, Version = version,
                        Fields = fields ?? new string[0], Data = data ?? new string[0] };
                if (shown == null || !shown.Contains("Area") || !shown.Contains("DesignProc") ||
                    displayed == null || displayed.Length != displayRecords * shown.Length ||
                    areaNames == null || displayRecords != modelAreas ||
                    !new HashSet<string>(Enumerable.Range(0, displayRecords).Select(row =>
                        displayed[row * shown.Length + Array.IndexOf(shown, "Area")]),
                        StringComparer.OrdinalIgnoreCase).SetEquals(areaNames))
                    throw new InvalidDataException("SAP2000 returned an incomplete area design table for the snapshot.");
                cPlugin.TraceCancellationStage("snapshot-design-procedures-display-fallback " + displayRecords);
                return new Table { Key = key, Version = displayVersion, Fields = shown, Data = displayed };
            }
            if (duringCapture && code == 0 && (key == "Joint Coordinates" ||
                key == "Connectivity - Area" || key == "Area Section Assignments" ||
                records == 0))
            {
                Table full = CompleteFilteredEditingTable(model, key, version,
                    records, fields, data, groupName);
                if (full != null) return full;
            }
            if (fields != null && fields.Any(string.IsNullOrWhiteSpace))
                fields = RecoverFieldKeys(model, key, version, records, data, fields.Length, groupName);
            if (fields != null && fields.Length > 0 && fields.All(string.IsNullOrEmpty) &&
                (key == "Joint Coordinates" || key == "Connectivity - Area" || key == "Area Section Assignments"))
            {
                try
                {
                    int metadataVersion = 0, metadataCount = 0, csvVersion = 0, displayVersion = 0;
                    int displayRecords = 0;
                    string[] keys = null, names = null, descriptions = null, units = null;
                    bool[] importable = null;
                    string csv = "";
                    string[] requested = null, included = null, shown = null;
                    int metadataCode = model.DatabaseTables.GetAllFieldsInTable(key, ref metadataVersion,
                        ref metadataCount, ref keys, ref names, ref descriptions, ref units, ref importable);
                    int csvCode = model.DatabaseTables.GetTableForEditingCSVString(key, groupName, ref csvVersion, ref csv);
                    int displayCode = model.DatabaseTables.GetTableForDisplayArray(key, ref requested, groupName,
                        ref displayVersion, ref included, ref displayRecords, ref shown);
                    string diagnosticDirectory = System.IO.Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData), "SAP2000QuadMesher");
                    Directory.CreateDirectory(diagnosticDirectory);
                    File.AppendAllText(System.IO.Path.Combine(diagnosticDirectory, "snapshot-table-diagnostics.log"),
                        "BlankFields key=" + key + " editVersion=" + version + " editWidth=" + fields.Length +
                        " metadataCode=" + metadataCode + " metadataVersion=" + metadataVersion +
                        " metadataCount=" + metadataCount + " fieldKeys=" + string.Join("|", keys ?? new string[0]) +
                        " fieldNames=" + string.Join("|", names ?? new string[0]) +
                        " csvCode=" + csvCode + " csvVersion=" + csvVersion +
                        " csvHeader=" + (csv ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() +
                        " displayCode=" + displayCode + " displayFields=" + string.Join("|", included ?? new string[0]) +
                        " editFirstValues=" + string.Join("|", (data ?? new string[0]).Take(Math.Min(fields.Length, 8))) +
                        Environment.NewLine);
                }
                catch (Exception error)
                { cPlugin.TraceCancellationStage("blank-fields-diagnostic-failed " + key + " " + error.GetType().Name); }
            }
            if (fields == null || fields.Length == 0 || data == null || data.Length != records * fields.Length)
                throw new InvalidDataException("Invalid SAP2000/CSiBridge table shape: " + key);
            return new Table { Key = key, Version = version, Fields = fields, Data = data };
        }

        // Polygon connectivity is stored in several editing-table records per
        // area (four joints per record in SAP2000 27.1). The last record may
        // contain only Joint1. Never infer the object's vertices from the first
        // row or from the record count alone: verify the complete ordered list.
        private static Dictionary<string, List<string>> AreaJoints(string[] fields, string[] data,
            int records, string phase)
        {
            int areaField = Array.IndexOf(fields, "Area");
            int[] jointFields = fields.Select((name, index) => new { Name = name, Index = index,
                Match = Regex.Match(name, @"^Joint(\d+)$", RegexOptions.IgnoreCase) })
                .Where(item => item.Match.Success)
                .OrderBy(item => int.Parse(item.Match.Groups[1].Value, CultureInfo.InvariantCulture))
                .Select(item => item.Index).ToArray();
            if (areaField < 0 || jointFields.Length == 0 || data == null ||
                data.Length != (long)records * fields.Length)
                throw new InvalidDataException("Invalid area connectivity table during " + phase + ".");
            var expected = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            string currentArea = null;
            for (int row = 0; row < records; row++)
            {
                int offset = row * fields.Length;
                string area = data[offset + areaField];
                if (!string.IsNullOrEmpty(area)) currentArea = area;
                if (string.IsNullOrEmpty(currentArea))
                    throw new InvalidDataException("Connectivity row " + (row + 1) + " has no area during " + phase + ".");
                List<string> joints;
                if (!expected.TryGetValue(currentArea, out joints))
                {
                    joints = new List<string>();
                    expected.Add(currentArea, joints);
                }
                foreach (int field in jointFields)
                {
                    string joint = data[offset + field];
                    if (!string.IsNullOrEmpty(joint)) joints.Add(joint);
                }
            }
            return expected;
        }

        internal static void VerifyConnectivity(cSapModel model, string[] fields, string[] data,
            int records, string phase)
        {
            foreach (KeyValuePair<string, List<string>> area in AreaJoints(fields, data, records, phase))
            {
                int count = 0;
                string[] actual = null;
                Check(model.AreaObj.GetPoints(area.Key, ref count, ref actual),
                    "reading area connectivity " + area.Key + " during " + phase, "AreaObj.GetPoints");
                if (actual == null || count != area.Value.Count || actual.Length != count ||
                    !area.Value.SequenceEqual(actual))
                    throw new InvalidDataException("Area " + area.Key + " connectivity differs during " + phase +
                        ": expected " + string.Join("|", area.Value) + ", actual " +
                        string.Join("|", actual ?? new string[0]) + ".");
            }
        }

        private static void RestoreOriginalAreas(cSapModel model, Table connectivity, Table assignments,
            int minimumNodeCount = 3)
        {
            var expected = AreaJoints(connectivity.Fields, connectivity.Data,
                connectivity.Records, "CSV area reconstruction");
            int areaField = Array.IndexOf(connectivity.Fields, "Area");
            int guidField = Array.IndexOf(connectivity.Fields, "GUID");
            int sectionArea = Array.IndexOf(assignments.Fields, "Area");
            int sectionField = Array.IndexOf(assignments.Fields, "Section");
            if (guidField < 0 || sectionArea < 0 || sectionField < 0)
                throw new InvalidDataException("Cannot reconstruct original area GUIDs and properties.");
            var guids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int row = 0; row < connectivity.Records; row++)
            {
                int offset = row * connectivity.Fields.Length;
                string name = connectivity.Data[offset + areaField];
                string guid = connectivity.Data[offset + guidField];
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(guid))
                {
                    string previous;
                    if (guids.TryGetValue(name, out previous) && previous != guid)
                        throw new InvalidDataException("Conflicting original GUIDs for area " + name);
                    guids[name] = guid;
                }
            }
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int row = 0; row < assignments.Records; row++)
            {
                int offset = row * assignments.Fields.Length;
                string name = assignments.Data[offset + sectionArea];
                if (!string.IsNullOrEmpty(name)) properties[name] = assignments.Data[offset + sectionField];
            }
            int count = 0;
            string[] names = null;
            Check(model.AreaObj.GetNameList(ref count, ref names), "listing original areas for CSV reconstruction", "AreaObj.GetNameList");
            var present = new HashSet<string>(names ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, List<string>> area in expected)
            {
                if (area.Value.Count < 3)
                    throw new InvalidDataException("Invalid original node count for area " + area.Key);
                if (area.Value.Count < minimumNodeCount) continue;
                if (present.Contains(area.Key)) continue; // Verify its full ordered connectivity below.
                string guid, property;
                if (!guids.TryGetValue(area.Key, out guid) || string.IsNullOrEmpty(guid) ||
                    !properties.TryGetValue(area.Key, out property) || string.IsNullOrEmpty(property))
                    throw new InvalidDataException("Missing original GUID or section for area " + area.Key);
                string[] points = area.Value.ToArray();
                foreach (string point in points)
                {
                    double x = 0, y = 0, z = 0;
                    if (model.PointObj.GetCoordCartesian(point, ref x, ref y, ref z, "Global") != 0)
                        throw new InvalidDataException("Cannot reconstruct original area " + area.Key +
                            ": joint " + point + " is missing after tabular joint restoration.");
                }
                string restored = "";
                Check(model.AreaObj.AddByPoint(points.Length, ref points, ref restored, property, area.Key),
                    "recreating original area " + area.Key, "AreaObj.AddByPoint");
                if (restored != area.Key)
                    Check(model.AreaObj.ChangeName(restored, area.Key), "restoring original area name " + area.Key, "AreaObj.ChangeName");
                Check(model.AreaObj.SetGUID(area.Key, guid), "restoring original area GUID " + area.Key, "AreaObj.SetGUID");
            }
            foreach (KeyValuePair<string, List<string>> area in expected.Where(item =>
                item.Value.Count >= minimumNodeCount))
            {
                int actualCount = 0;
                string[] actual = null;
                Check(model.AreaObj.GetPoints(area.Key, ref actualCount, ref actual),
                    "reading reconstructed area " + area.Key, "AreaObj.GetPoints");
                if (actual == null || actualCount != area.Value.Count || !area.Value.SequenceEqual(actual))
                    throw new InvalidDataException("Area " + area.Key +
                        " connectivity differs after object-API reconstruction.");
            }
        }

        private static void RestoreMissingPolygonJoints(cSapModel model, SnapshotData snapshot,
            Table connectivity)
        {
            var required = new HashSet<string>(AreaJoints(connectivity.Fields, connectivity.Data,
                connectivity.Records, "polygon joint reconstruction").Where(item => item.Value.Count > 4)
                .SelectMany(item => item.Value), StringComparer.OrdinalIgnoreCase);
            int count = 0;
            string[] names = null;
            Check(model.PointObj.GetNameList(ref count, ref names), "listing joints before polygon reconstruction", "PointObj.GetNameList");
            var present = new HashSet<string>(names ?? new string[0], StringComparer.OrdinalIgnoreCase);
            foreach (string point in required.Where(point => !present.Contains(point)))
            {
                double[] coordinates;
                if (!snapshot.JointCoordinates.TryGetValue(point, out coordinates))
                    throw new InvalidDataException("Original coordinates are missing for polygon joint " + point);
                string restored = "";
                Check(model.PointObj.AddCartesian(coordinates[0], coordinates[1], coordinates[2], ref restored,
                    point, "Global", true, 0), "recreating polygon joint " + point, "PointObj.AddCartesian");
                if (!string.Equals(restored, point, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SAP2000 did not preserve reconstructed polygon joint name " +
                        point + " (created " + restored + ").");
                present.Add(point);
            }
        }

        internal static ModelCsvSnapshot Capture(cSapModel model, Action checkpoint = null)
        {
            return WithAllLoadPatterns(model, () => CaptureCore(model, checkpoint));
        }

        private static ModelCsvSnapshot CaptureCore(cSapModel model, Action checkpoint,
            bool retryFilteredTables = true)
        {
            if (checkpoint != null) checkpoint();
            int count = 0;
            string[] keys = null, names = null;
            int[] importTypes = null;
            bool[] isEmpty = null;
            Check(model.DatabaseTables.GetAllTables(ref count, ref keys, ref names, ref importTypes, ref isEmpty), "listing snapshot tables", "DatabaseTables.GetAllTables");
            var tables = new List<Table>();
            var unreadable = new List<string>();
            var skipped = new List<string>();
            var unpreservedDesign = new List<string>();
            for (int index = 0; index < count; index++)
            {
                if (checkpoint != null) checkpoint();
                if (keys[index] == "Database Format Types" || keys[index] == "Program Control" ||
                    keys[index].StartsWith("Frame Section Properties", StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add(keys[index]);
                    cPlugin.TraceCancellationStage("snapshot-global-metadata-omitted " + keys[index]);
                    continue;
                }
                bool designOverwrite = keys[index].StartsWith("Overwrites - ", StringComparison.OrdinalIgnoreCase) &&
                    keys[index].IndexOf("Design", StringComparison.OrdinalIgnoreCase) >= 0;
                if (designOverwrite && !isEmpty[index])
                {
                    unpreservedDesign.Add(keys[index]);
                    skipped.Add(keys[index]);
                    cPlugin.TraceCancellationStage("snapshot-design-overwrite-omitted " + keys[index]);
                    continue;
                }
                // After Display > Show Tables, GetAllTables can call a real
                // geometry table empty even though the display getter still
                // has every object. Always read the three rollback essentials.
                if (importTypes[index] == 0) continue;
                if (isEmpty[index] && keys[index] != "Joint Coordinates" &&
                    keys[index] != "Connectivity - Area" &&
                    keys[index] != "Area Section Assignments" &&
                    (!IsObjectAssignmentTable(keys[index]) ||
                     !HasMeaningfulDisplayRows(model, keys[index]))) continue;
                cPlugin.TraceCancellationStage("snapshot-table-start " + keys[index]);
                try
                {
                    Table table = Read(model, keys[index], true);
                    if (table == null)
                    {
                        cPlugin.TraceCancellationStage("snapshot-table-unavailable " + keys[index] + " code=-96");
                        skipped.Add(keys[index]);
                        if ((keys[index].StartsWith("Overwrites - ", StringComparison.OrdinalIgnoreCase) &&
                             keys[index].IndexOf("Design", StringComparison.OrdinalIgnoreCase) >= 0) ||
                            keys[index] == "Area Design Procedures")
                            unpreservedDesign.Add(keys[index]);
                        continue;
                    }
                    cPlugin.TraceCancellationStage("snapshot-table-complete " + keys[index]);
                    if (table.Records > 0) tables.Add(table);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error)
                {
                    // Perform the entire read-only preflight before reporting
                    // failure. SAP beta releases can expose *several* stale
                    // table keys: fixing only the first one hides the others.
                    unreadable.Add(keys[index] + " (importType=" + importTypes[index] +
                        ", empty=" + isEmpty[index] + "): " + error.Message);
                    cPlugin.TraceCancellationStage("snapshot-table-failed " + keys[index]);
                }
            }
            if (Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_DESIGN_WARNING_PROBE") == "1" &&
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SIZE")))
                unpreservedDesign.Add("Overwrites - Concrete Shell Design - Eurocode 2-2004");
            if (unreadable.Count != 0)
                throw new InvalidOperationException("SAP2000 exposes " + unreadable.Count +
                    " snapshot tables that cannot be safely captured: " + string.Join("; ", unreadable) +
                    ". The model was not changed.");
            if (skipped.Count != 0)
            {
                try
                {
                    string diagnosticDirectory = System.IO.Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.LocalApplicationData), "SAP2000QuadMesher");
                    Directory.CreateDirectory(diagnosticDirectory);
                    foreach (Table table in tables.Where(item => item.Key == "Joint Coordinates" ||
                        item.Key == "Connectivity - Area" || item.Key == "Area Section Assignments"))
                        File.AppendAllText(System.IO.Path.Combine(diagnosticDirectory, "snapshot-table-diagnostics.log"),
                            "SnapshotSchema key=" + table.Key + " version=" + table.Version +
                            " records=" + table.Records + " fields=" + string.Join("|", table.Fields) + Environment.NewLine);
                }
                catch (IOException) { }
            }
            foreach (string required in new[] { "Joint Coordinates", "Connectivity - Area", "Area Section Assignments" })
                if (!tables.Any(table => table.Key == required))
                    throw new InvalidDataException("The model snapshot is missing " + required);
            Table originalAreas = tables.First(table => table.Key == "Connectivity - Area");
            VerifyConnectivity(model, originalAreas.Fields, originalAreas.Data, originalAreas.Records, "snapshot capture");
            int modelAreaCount = 0;
            string[] modelAreaNames = null;
            Check(model.AreaObj.GetNameList(ref modelAreaCount, ref modelAreaNames),
                "validating full area table before snapshot", "AreaObj.GetNameList");
            var tableAreaNames = new HashSet<string>(AreaJoints(originalAreas.Fields, originalAreas.Data,
                originalAreas.Records, "snapshot completeness").Keys, StringComparer.OrdinalIgnoreCase);
            if (modelAreaNames == null || modelAreaCount != tableAreaNames.Count ||
                !tableAreaNames.SetEquals(modelAreaNames))
            {
                if (retryFilteredTables)
                {
                    // The SAP GUI's selected-only table view may leave editing
                    // state behind when another SDB is opened. Clear that
                    // state, then re-read *every* snapshot table, not only
                    // connectivity (assignments may have been filtered too).
                    Check(model.DatabaseTables.CancelTableEditing(),
                        "resetting filtered interactive table editing", "DatabaseTables.CancelTableEditing");
                    cPlugin.TraceCancellationStage("snapshot-filtered-area-table-retry-all");
                    return CaptureCore(model, checkpoint, false);
                }
                throw new InvalidDataException("SAP2000 returned a filtered Connectivity - Area table. " +
                    "The full model cannot be restored safely from this selection.");
            }
            // Shell design overwrites are deliberately excluded for now. Even
            // when a getter exists in the type library, SAP2000 27.1 can
            // reject both GetOverwrite and SetOverwrite for this model.
            List<DesignOverwrite> overwrites = new List<DesignOverwrite>();
            Table originalJoints = tables.First(table => table.Key == "Joint Coordinates");
            int jointName = Array.IndexOf(originalJoints.Fields, "Joint");
            if (jointName < 0)
                throw new InvalidDataException("The joint table lacks a recognized point-name column. " +
                    "SAP2000 returned: " + string.Join(" | ", originalJoints.Fields));
            int modelJointCount = 0;
            string[] modelJointNames = null;
            Check(model.PointObj.GetNameList(ref modelJointCount, ref modelJointNames),
                "validating full joint table before snapshot", "PointObj.GetNameList");
            var tableJointNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int record = 0; record < originalJoints.Records; record++)
                tableJointNames.Add(originalJoints.Data[record * originalJoints.Fields.Length + jointName]);
            if (modelJointNames == null || modelJointCount != originalJoints.Records ||
                tableJointNames.Count != modelJointCount || !tableJointNames.SetEquals(modelJointNames))
            {
                if (retryFilteredTables)
                {
                    Check(model.DatabaseTables.CancelTableEditing(),
                        "resetting filtered interactive table editing", "DatabaseTables.CancelTableEditing");
                    cPlugin.TraceCancellationStage("snapshot-filtered-joint-table-retry-all");
                    return CaptureCore(model, checkpoint, false);
                }
                throw new InvalidDataException("SAP2000 returned a filtered Joint Coordinates table. " +
                    "The full model cannot be restored safely from this selection.");
            }
            var jointCoordinates = new Dictionary<string, double[]>();
            for (int record = 0; record < originalJoints.Records; record++)
            {
                if (checkpoint != null && record % 16 == 0) checkpoint();
                string name = originalJoints.Data[record * originalJoints.Fields.Length + jointName];
                double x = 0, y = 0, z = 0;
                Check(model.PointObj.GetCoordCartesian(name, ref x, ref y, ref z, "Global"), "reading snapshot point " + name, "PointObj.GetCoordCartesian");
                jointCoordinates.Add(name, new[] { x, y, z });
            }

            string filename = model.GetModelFilename(true) ?? "";
            string directory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SAP2000QuadMesher", "snapshots");
            Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory, "quadmesh-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) +
                "-" + Guid.NewGuid().ToString("N") + ".csv");
            try
            {
                using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                {
                    writer.WriteLine("SQM_CSV_SNAPSHOT,2," + B64(filename) + "," + ((int)model.GetPresentUnits()).ToString(CultureInfo.InvariantCulture));
                    writer.WriteLine("TABLE_COUNT," + tables.Count.ToString(CultureInfo.InvariantCulture));
                    foreach (Table table in tables)
                    {
                        if (checkpoint != null) checkpoint();
                        writer.WriteLine("TABLE," + B64(table.Key) + "," + table.Version.ToString(CultureInfo.InvariantCulture) +
                            "," + table.Fields.Length.ToString(CultureInfo.InvariantCulture) + "," + table.Records.ToString(CultureInfo.InvariantCulture));
                        writer.WriteLine("FIELDS," + string.Join(",", table.Fields.Select(B64)));
                        for (int record = 0; record < table.Records; record++)
                        {
                            if (checkpoint != null && record % 128 == 0) checkpoint();
                            var encoded = new string[table.Fields.Length];
                            for (int field = 0; field < encoded.Length; field++)
                                encoded[field] = B64(table.Data[record * encoded.Length + field]);
                            writer.WriteLine("ROW," + string.Join(",", encoded));
                        }
                    }
                    writer.WriteLine("JOINT_COUNT," + jointCoordinates.Count.ToString(CultureInfo.InvariantCulture));
                    int jointIndex = 0;
                    foreach (KeyValuePair<string, double[]> joint in jointCoordinates)
                    {
                        if (checkpoint != null && jointIndex++ % 128 == 0) checkpoint();
                        writer.WriteLine("JOINT," + B64(joint.Key) + "," + string.Join(",", joint.Value
                            .Select(value => value.ToString("R", CultureInfo.InvariantCulture))));
                    }
                    writer.WriteLine("DESIGN_OVERWRITE_COUNT," + overwrites.Count.ToString(CultureInfo.InvariantCulture));
                    foreach (DesignOverwrite item in overwrites)
                        writer.WriteLine("DESIGN_OVERWRITE," + B64(item.Area) + "," + B64(item.Code) + "," +
                            item.Item.ToString(CultureInfo.InvariantCulture) + "," +
                            item.Value.ToString("R", CultureInfo.InvariantCulture));
                    writer.Flush();
                    stream.Flush(true);
                }
                return new ModelCsvSnapshot(path, filename, Hash(path),
                    unpreservedDesign.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            }
            catch
            {
                try { File.Delete(path); } catch (IOException) { }
                throw;
            }
        }

        private SnapshotData Load(cSapModel model)
        {
            if (!File.Exists(Path) || !Hash(Path).SequenceEqual(digest))
                throw new InvalidDataException("The original model CSV snapshot was changed or is missing: " + Path);
            using (var reader = new StreamReader(Path, Encoding.UTF8))
            {
                string[] header = (reader.ReadLine() ?? "").Split(',');
                if (header.Length != 4 || header[0] != "SQM_CSV_SNAPSHOT" ||
                    (header[1] != "1" && header[1] != "2") ||
                    UnB64(header[2]) != modelPath || model.GetModelFilename(true) != modelPath ||
                    header[3] != ((int)model.GetPresentUnits()).ToString(CultureInfo.InvariantCulture))
                    throw new InvalidDataException("The model does not match its original CSV snapshot.");
                string[] total = (reader.ReadLine() ?? "").Split(',');
                int count;
                if (total.Length != 2 || total[0] != "TABLE_COUNT" || !int.TryParse(total[1], out count) || count < 3)
                    throw new InvalidDataException("Invalid model snapshot table count.");
                var result = new List<Table>();
                for (int tableIndex = 0; tableIndex < count; tableIndex++)
                {
                    string[] tableHeader = (reader.ReadLine() ?? "").Split(',');
                    int version, width, rows;
                    if (tableHeader.Length != 5 || tableHeader[0] != "TABLE" ||
                        !int.TryParse(tableHeader[2], out version) || !int.TryParse(tableHeader[3], out width) ||
                        !int.TryParse(tableHeader[4], out rows) || width < 1 || rows < 1)
                        throw new InvalidDataException("Invalid model snapshot table header.");
                    string[] fields = (reader.ReadLine() ?? "").Split(',');
                    if (fields.Length != width + 1 || fields[0] != "FIELDS")
                        throw new InvalidDataException("Invalid model snapshot fields.");
                    var values = new List<string>();
                    for (int record = 0; record < rows; record++)
                    {
                        string[] row = (reader.ReadLine() ?? "").Split(',');
                        if (row.Length != width + 1 || row[0] != "ROW")
                            throw new InvalidDataException("Invalid model snapshot record.");
                        values.AddRange(row.Skip(1).Select(UnB64));
                    }
                    result.Add(new Table { Key = UnB64(tableHeader[1]), Version = version,
                        Fields = fields.Skip(1).Select(UnB64).ToArray(), Data = values.ToArray() });
                }
                string[] jointHeader = (reader.ReadLine() ?? "").Split(',');
                int jointCount;
                if (jointHeader.Length != 2 || jointHeader[0] != "JOINT_COUNT" ||
                    !int.TryParse(jointHeader[1], out jointCount) || jointCount < 1)
                    throw new InvalidDataException("Invalid snapshot joint coordinate count.");
                var joints = new Dictionary<string, double[]>();
                for (int joint = 0; joint < jointCount; joint++)
                {
                    string[] row = (reader.ReadLine() ?? "").Split(',');
                    double x, y, z;
                    if (row.Length != 5 || row[0] != "JOINT" ||
                        !double.TryParse(row[2], NumberStyles.Float, CultureInfo.InvariantCulture, out x) ||
                        !double.TryParse(row[3], NumberStyles.Float, CultureInfo.InvariantCulture, out y) ||
                        !double.TryParse(row[4], NumberStyles.Float, CultureInfo.InvariantCulture, out z))
                        throw new InvalidDataException("Invalid snapshot joint coordinates.");
                    joints.Add(UnB64(row[1]), new[] { x, y, z });
                }
                var overwrites = new List<DesignOverwrite>();
                if (header[1] == "2")
                {
                    string[] overwriteHeader = (reader.ReadLine() ?? "").Split(',');
                    int overwriteCount;
                    if (overwriteHeader.Length != 2 || overwriteHeader[0] != "DESIGN_OVERWRITE_COUNT" ||
                        !int.TryParse(overwriteHeader[1], out overwriteCount) || overwriteCount < 0)
                        throw new InvalidDataException("Invalid design overwrite count in snapshot.");
                    for (int i = 0; i < overwriteCount; i++)
                    {
                        string[] row = (reader.ReadLine() ?? "").Split(',');
                        int item;
                        double value;
                        if (row.Length != 5 || row[0] != "DESIGN_OVERWRITE" ||
                            !int.TryParse(row[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out item) ||
                            item < 1 || !double.TryParse(row[4], NumberStyles.Float,
                                CultureInfo.InvariantCulture, out value))
                            throw new InvalidDataException("Invalid design overwrite record in snapshot.");
                        overwrites.Add(new DesignOverwrite { Area = UnB64(row[1]), Code = UnB64(row[2]),
                            Item = item, Value = value });
                    }
                }
                if (reader.ReadLine() != null || result.Select(item => item.Key).Distinct().Count() != result.Count ||
                    joints.Count != result.First(item => item.Key == "Joint Coordinates").Records)
                    throw new InvalidDataException("Unexpected trailing or repeated snapshot records.");
                return new SnapshotData { Tables = result, JointCoordinates = joints,
                    DesignOverwrites = overwrites };
            }
        }

        private static void Stage(cSapModel model, Table table)
        {
            if (Environment.GetEnvironmentVariable("SAP_QUAD_DEBUG_SNAPSHOT") == "1")
                Console.WriteLine("csv-snapshot-stage=" + table.Key + " records=" + table.Records);
            int version = table.Version;
            string[] fields = table.Fields.ToArray(), values = table.Data.ToArray();
            Check(model.DatabaseTables.SetTableForEditingArray(table.Key, ref version, ref fields, table.Records, ref values),
                "restoring CSV snapshot table " + table.Key, "DatabaseTables.SetTableForEditingArray");
        }

        private static bool ExpectedBlankJointErrors(string log, int errors, Table areas)
        {
            if (errors == 0) return true;
            if (areas == null) return false;
            int areaIndex = Array.IndexOf(areas.Fields, "Area");
            int firstIndex = Array.IndexOf(areas.Fields, "Joint1");
            if (areaIndex < 0 || firstIndex < 0) return false;
            var names = new HashSet<string>();
            var expected = new HashSet<string>();
            for (int row = 0; row < areas.Records; row++)
            {
                int offset = row * areas.Fields.Length;
                string area = areas.Data[offset + areaIndex];
                bool continuation = !string.IsNullOrEmpty(area) && names.Contains(area) &&
                    !string.IsNullOrEmpty(areas.Data[offset + firstIndex]);
                for (int joint = continuation ? 2 : 4; joint <= 16; joint++)
                {
                    int field = Array.IndexOf(areas.Fields, "Joint" + joint);
                    if (field >= 0 && string.IsNullOrEmpty(areas.Data[offset + field]))
                        expected.Add((row + 1) + ":JOINT" + joint);
                }
                if (!string.IsNullOrEmpty(area)) names.Add(area);
            }
            if (errors != expected.Count) return false;
            string[] blocks = (log ?? "").Split(new[] { "*** Error Message" }, StringSplitOptions.None).Skip(1).ToArray();
            if (blocks.Length != expected.Count) return false;
            foreach (string block in blocks)
            {
                Match match = Regex.Match(block,
                    @"(?is)Table name:\s*CONNECTIVITY - AREA\s+Field name:\s*(JOINT\d+)\s+Record:\s*(\d+)\s+Invalid Joint name, joint\s+not found; joint skipped");
                if (!match.Success || !expected.Remove(match.Groups[2].Value + ":" + match.Groups[1].Value.ToUpperInvariant()))
                    return false;
            }
            return expected.Count == 0;
        }

        private static Table PadExistingTriangles(Table source)
        {
            var restored = new Table { Key = source.Key, Version = source.Version,
                Fields = source.Fields.ToArray(), Data = source.Data.ToArray() };
            int areaIndex = Array.IndexOf(restored.Fields, "Area");
            int third = Array.IndexOf(restored.Fields, "Joint3");
            int fourth = Array.IndexOf(restored.Fields, "Joint4");
            int count = Array.IndexOf(restored.Fields, "NumJoints");
            if (areaIndex < 0 || third < 0 || fourth < 0) return restored;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int row = 0; row < restored.Records; row++)
            {
                int offset = row * restored.Fields.Length;
                string name = restored.Data[offset + areaIndex];
                if (!string.IsNullOrEmpty(name) && seen.Add(name) &&
                    (count < 0 || string.IsNullOrEmpty(restored.Data[offset + count]) || restored.Data[offset + count] == "3") &&
                    !string.IsNullOrEmpty(restored.Data[offset + third]) && string.IsNullOrEmpty(restored.Data[offset + fourth]))
                    restored.Data[offset + fourth] = restored.Data[offset + third];
            }
            return restored;
        }

        private static void Apply(cSapModel model, string phase, Table originalAreas = null)
        {
            int fatal = 0, errors = 0, warnings = 0, information = 0;
            string log = "";
            int applied = model.DatabaseTables.ApplyEditedTables(true, ref fatal, ref errors, ref warnings, ref information, ref log);
            if (Environment.GetEnvironmentVariable("SAP_QUAD_DEBUG_SNAPSHOT") == "1")
                Console.WriteLine("csv-snapshot-apply-" + phase + "=" + applied + " locked=" + model.GetModelIsLocked());
            string[] lines = (log ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            bool accepted = applied == 0 && fatal == 0 && warnings == 0 &&
                (errors == 0 || ExpectedBlankJointErrors(log, errors, originalAreas));
            if (!accepted)
                throw new InvalidOperationException("Original CSV snapshot " + phase + " restore reported fatal=" + fatal +
                    ", errors=" + errors + ", warnings=" + warnings + ", code=" + applied + ": " +
                    string.Join(" ", lines.Skip(Math.Max(0, lines.Length - 30))));
        }

        private static bool IsObjectAssignment(Table table)
        {
            // An "Area" field in these tables means cross-sectional area,
            // not the name of a shell object to restore.
            if (table.Key.StartsWith("Frame Section Properties", StringComparison.OrdinalIgnoreCase) ||
                table.Key == "Rebar Sizes") return false;
            if (table.Key == "Groups 2 - Assignments") return true;
            return table.Fields.Any(field => string.Equals(field, "Area", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(field, "Joint", StringComparison.OrdinalIgnoreCase));
        }

        internal void Restore(cSapModel model, IEnumerable<string> generatedAreas, IEnumerable<string> generatedPoints,
            bool useTables)
        {
            WithAllLoadPatterns<object>(model, () =>
            {
                RestoreCore(model, generatedAreas, generatedPoints, useTables);
                return null;
            });
        }

        private void RestoreCore(cSapModel model, IEnumerable<string> generatedAreas, IEnumerable<string> generatedPoints,
            bool useTables)
        {
#if !GMSH_V2
            RestoreCoreLegacy(model, generatedAreas, generatedPoints);
#else
            if (useTables) RestoreCoreByTables(model, generatedAreas);
            else RestoreCoreLegacy(model, generatedAreas, generatedPoints);
#endif
        }

        private static bool SameRecords(Table expected, Table actual)
        {
            if (!expected.Fields.SequenceEqual(actual.Fields) || expected.Records != actual.Records) return false;
            Func<Table, string[]> rows = table => Enumerable.Range(0, table.Records).Select(row =>
                string.Join(",", table.Data.Skip(row * table.Fields.Length)
                    .Take(table.Fields.Length).Select(B64)))
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            return rows(expected).SequenceEqual(rows(actual));
        }

        private static bool SameRecordsAllowingGeneratedGuids(Table expected, Table actual)
        {
            if (!expected.Fields.SequenceEqual(actual.Fields) || expected.Records != actual.Records) return false;
            int guid = Array.IndexOf(expected.Fields, "GUID");
            if (guid < 0) return expected.Data.SequenceEqual(actual.Data);
            string[] normalized = actual.Data.ToArray();
            for (int row = 0; row < expected.Records; row++)
            {
                int index = row * expected.Fields.Length + guid;
                if (string.IsNullOrEmpty(expected.Data[index])) normalized[index] = "";
            }
            return expected.Data.SequenceEqual(normalized);
        }

        private static string RecordDifference(Table expected, Table actual)
        {
            if (!expected.Fields.SequenceEqual(actual.Fields))
                return "fields expected=" + string.Join("|", expected.Fields) +
                    " actual=" + string.Join("|", actual.Fields);
            if (expected.Records != actual.Records)
                return "records expected=" + expected.Records + " actual=" + actual.Records;
            int count = Math.Min(expected.Data.Length, actual.Data.Length);
            for (int index = 0; index < count; index++)
                if (!string.Equals(expected.Data[index], actual.Data[index], StringComparison.Ordinal))
                    return "row=" + (index / expected.Fields.Length) + " field=" +
                        expected.Fields[index % expected.Fields.Length] + " expected='" +
                        expected.Data[index] + "' actual='" + actual.Data[index] + "'";
            return "dataCount expected=" + expected.Data.Length + " actual=" + actual.Data.Length;
        }

        private static void RestoreAreaMaterialTemperatures(cSapModel model, IEnumerable<Table> tables)
        {
            Table table = tables.FirstOrDefault(item => item.Key == "Area Material Temperatures");
            if (table == null) return;
            int area = Array.IndexOf(table.Fields, "Area");
            int pattern = Array.IndexOf(table.Fields, "JtPattern");
            int temperature = Array.IndexOf(table.Fields, "Temp");
            if (area < 0 || pattern < 0 || temperature < 0)
                throw new InvalidDataException("Area Material Temperatures has an unexpected schema.");
            for (int row = 0; row < table.Records; row++)
            {
                int offset = row * table.Fields.Length;
                double value;
                if (!double.TryParse(table.Data[offset + temperature], NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value))
                    throw new InvalidDataException("Invalid area material temperature for " + table.Data[offset + area]);
                string jointPattern = table.Data[offset + pattern];
                if (jointPattern == "None") jointPattern = "";
                Check(model.AreaObj.SetMatTemp(table.Data[offset + area], value, jointPattern, eItemType.Objects),
                    "restoring area material temperature " + table.Data[offset + area], "AreaObj.SetMatTemp");
            }
        }

        private static void RestoreAreaDesignProcedures(cSapModel model, IEnumerable<Table> tables)
        {
            Table table = tables.FirstOrDefault(item => item.Key == "Area Design Procedures");
            if (table == null) return;
            model.DatabaseTables.CancelTableEditing();
            Stage(model, table);
            Apply(model, "area design procedures");
            Table restored = Read(model, table.Key);
            if (!SameRecords(table, restored))
                throw new InvalidDataException("Area design procedures differ immediately after their dedicated import: " +
                    RecordDifference(table, restored));
        }

        private static Table AppendNamedRecords(Table target, Table source, string nameField,
            ISet<string> names)
        {
            if (target.Key != source.Key || !target.Fields.SequenceEqual(source.Fields))
                throw new InvalidDataException("Cannot combine incompatible SAP2000/CSiBridge tables: " + target.Key);
            int name = Array.IndexOf(source.Fields, nameField);
            if (name < 0) throw new InvalidDataException(nameField + " field missing from " + source.Key);
            var data = target.Data.ToList();
            for (int row = 0; row < source.Records; row++)
            {
                int offset = row * source.Fields.Length;
                if (names.Contains(source.Data[offset + name]))
                    data.AddRange(source.Data.Skip(offset).Take(source.Fields.Length));
            }
            return new Table { Key = target.Key, Version = target.Version,
                Fields = target.Fields.ToArray(), Data = data.ToArray() };
        }

        private static Table WithoutNamedRecords(Table source, string nameField, ISet<string> names)
        {
            int name = Array.IndexOf(source.Fields, nameField);
            if (name < 0) throw new InvalidDataException(nameField + " field missing from " + source.Key);
            var data = new List<string>();
            string currentName = null;
            for (int row = 0; row < source.Records; row++)
            {
                int offset = row * source.Fields.Length;
                string rowName = source.Data[offset + name];
                if (!string.IsNullOrEmpty(rowName)) currentName = rowName;
                if (!names.Contains(currentName ?? ""))
                    data.AddRange(source.Data.Skip(offset).Take(source.Fields.Length));
            }
            return new Table { Key = source.Key, Version = source.Version,
                Fields = source.Fields.ToArray(), Data = data.ToArray() };
        }

        private static Table FirstNamedRecord(Table source, string nameField, string name)
        {
            int nameIndex = Array.IndexOf(source.Fields, nameField);
            if (nameIndex < 0) throw new InvalidDataException(nameField + " field missing from " + source.Key);
            for (int row = 0; row < source.Records; row++)
            {
                int offset = row * source.Fields.Length;
                if (string.Equals(source.Data[offset + nameIndex], name, StringComparison.OrdinalIgnoreCase))
                    return new Table { Key = source.Key, Version = source.Version,
                        Fields = source.Fields.ToArray(), Data = source.Data.Skip(offset)
                            .Take(source.Fields.Length).ToArray() };
            }
            throw new InvalidDataException("Object " + name + " is missing from " + source.Key);
        }

        private static Table WithoutGuids(Table source)
        {
            var result = new Table { Key = source.Key, Version = source.Version,
                Fields = source.Fields.ToArray(), Data = source.Data.ToArray() };
            int guid = Array.IndexOf(result.Fields, "GUID");
            if (guid >= 0)
                for (int row = 0; row < result.Records; row++)
                    result.Data[row * result.Fields.Length + guid] = "";
            return result;
        }

        private static void PrepareAreaNameConflicts(cSapModel model, Table originalAreas,
            ISet<string> replacedAreaNames)
        {
            Table currentAreas = PadExistingTriangles(Read(model, "Connectivity - Area"));
            int originalName = Array.IndexOf(originalAreas.Fields, "Area");
            int originalGuid = Array.IndexOf(originalAreas.Fields, "GUID");
            int currentName = Array.IndexOf(currentAreas.Fields, "Area");
            int currentGuid = Array.IndexOf(currentAreas.Fields, "GUID");
            if (originalName < 0 || originalGuid < 0 || currentName < 0 || currentGuid < 0) return;
            var expectedGuids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int row = 0; row < originalAreas.Records; row++)
            {
                int offset = row * originalAreas.Fields.Length;
                string name = originalAreas.Data[offset + originalName];
                string guid = originalAreas.Data[offset + originalGuid];
                if (!string.IsNullOrEmpty(name) && !expectedGuids.ContainsKey(name))
                    expectedGuids.Add(name, guid);
            }
            var currentGuids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int row = 0; row < currentAreas.Records; row++)
            {
                int offset = row * currentAreas.Fields.Length;
                string name = currentAreas.Data[offset + currentName];
                string guid = currentAreas.Data[offset + currentGuid];
                if (!string.IsNullOrEmpty(name) && !currentGuids.ContainsKey(name))
                    currentGuids.Add(name, guid);
            }
            string[] conflicts = currentGuids.Keys.Where(name => expectedGuids.ContainsKey(name) &&
                (replacedAreaNames.Contains(name) ||
                !string.Equals(currentGuids[name], expectedGuids[name], StringComparison.OrdinalIgnoreCase))).ToArray();
            if (conflicts.Length == 0) return;

            var used = new HashSet<string>(currentGuids.Keys, StringComparer.OrdinalIgnoreCase);
            var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string token = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
            for (int index = 0; index < conflicts.Length; index++)
            {
                string replacement = "QTR" + token + index.ToString("D4", CultureInfo.InvariantCulture);
                while (!used.Add(replacement)) replacement += "X";
                replacements.Add(conflicts[index], replacement);
            }
            for (int row = 0; row < currentAreas.Records; row++)
            {
                int offset = row * currentAreas.Fields.Length + currentName;
                string replacement;
                if (replacements.TryGetValue(currentAreas.Data[offset], out replacement))
                    currentAreas.Data[offset] = replacement;
            }
            Table currentSections = Read(model, "Area Section Assignments");
            int sectionName = Array.IndexOf(currentSections.Fields, "Area");
            if (sectionName < 0) throw new InvalidDataException("Area section table lacks the area name field.");
            for (int row = 0; row < currentSections.Records; row++)
            {
                int offset = row * currentSections.Fields.Length + sectionName;
                string replacement;
                if (replacements.TryGetValue(currentSections.Data[offset], out replacement))
                    currentSections.Data[offset] = replacement;
            }
            Check(model.DatabaseTables.CancelTableEditing(), "clearing staged tables before tabular name preparation", "DatabaseTables.CancelTableEditing");
            Stage(model, Read(model, "Joint Coordinates"));
            Stage(model, currentAreas);
            Stage(model, currentSections);
            Apply(model, "tabular area name preparation", currentAreas);
            Console.WriteLine("csv-undo-phase=name-conflicts count=" + conflicts.Length);
        }

        private static void VerifyOriginalGeometry(cSapModel model, SnapshotData snapshot, Table originalAreas)
        {
            VerifyConnectivity(model, originalAreas.Fields, originalAreas.Data, originalAreas.Records,
                "tabular CSV Undo");
            int expectedName = Array.IndexOf(originalAreas.Fields, "Area");
            int expectedGuid = Array.IndexOf(originalAreas.Fields, "GUID");
            if (expectedName < 0 || expectedGuid < 0)
                throw new InvalidDataException("Area identity fields are missing from the original snapshot.");
            var names = new HashSet<string>(AreaJoints(originalAreas.Fields, originalAreas.Data,
                originalAreas.Records, "tabular Undo area inventory").Keys, StringComparer.OrdinalIgnoreCase);
            int count = 0;
            string[] current = null;
            Check(model.AreaObj.GetNameList(ref count, ref current), "listing areas after tabular Undo", "AreaObj.GetNameList");
            if (current == null || count != names.Count || !names.SetEquals(current))
                throw new InvalidDataException("Tabular Undo left generated or missing area objects.");
            for (int row = 0; row < originalAreas.Records; row++)
            {
                int offset = row * originalAreas.Fields.Length;
                string guid = originalAreas.Data[offset + expectedGuid];
                if (string.IsNullOrEmpty(guid)) continue; // Polygon continuation row.
                string actual = null;
                Check(model.AreaObj.GetGUID(originalAreas.Data[offset + expectedName], ref actual),
                    "verifying original area GUID", "AreaObj.GetGUID");
                if (!string.Equals(guid, actual, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Area GUID changed in tabular Undo: " +
                        originalAreas.Data[offset + expectedName]);
            }
            int jointCount = 0;
            string[] jointNames = null;
            Check(model.PointObj.GetNameList(ref jointCount, ref jointNames), "listing joints after tabular Undo", "PointObj.GetNameList");
            if (jointNames == null || jointCount != snapshot.JointCoordinates.Count ||
                !new HashSet<string>(jointNames, StringComparer.OrdinalIgnoreCase)
                    .SetEquals(snapshot.JointCoordinates.Keys))
                throw new InvalidDataException("Tabular Undo left generated or missing joint objects.");
            foreach (KeyValuePair<string, double[]> joint in snapshot.JointCoordinates)
            {
                double x = 0, y = 0, z = 0;
                Check(model.PointObj.GetCoordCartesian(joint.Key, ref x, ref y, ref z, "Global"),
                    "checking tabular Undo joint " + joint.Key, "PointObj.GetCoordCartesian");
                double[] actual = { x, y, z };
                for (int axis = 0; axis < 3; axis++)
                    if (Math.Abs(actual[axis] - joint.Value[axis]) >
                        1e-8 * Math.Max(1, Math.Abs(joint.Value[axis])))
                        throw new InvalidDataException("Joint coordinates changed in tabular Undo: " + joint.Key);
            }
        }

        private void RestoreCoreByTables(cSapModel model, IEnumerable<string> generatedAreas)
        {
            SnapshotData snapshot = Load(model);
            RestoreAttempted = true;
            if (model.GetModelIsLocked())
                throw new InvalidOperationException("Unlock SAP2000 before applying the tabular Undo.");
            List<Table> tables = snapshot.Tables;
            Table joints = tables.First(item => item.Key == "Joint Coordinates");
            Table areas = tables.First(item => item.Key == "Connectivity - Area");
            Table sections = tables.First(item => item.Key == "Area Section Assignments");
            var clock = Stopwatch.StartNew();
            var originalNames = new HashSet<string>(AreaJoints(areas.Fields, areas.Data, areas.Records,
                "tabular Undo originals").Keys, StringComparer.OrdinalIgnoreCase);
            var polygonNames = new HashSet<string>(AreaJoints(areas.Fields, areas.Data, areas.Records,
                "tabular Undo polygon inventory").Where(item => item.Value.Count > 4).Select(item => item.Key),
                StringComparer.OrdinalIgnoreCase);
            Table paddedAreas = PadExistingTriangles(areas);
            Table regularOriginalAreas = WithoutNamedRecords(areas, "Area", polygonNames);
            Table regularAreas = WithoutNamedRecords(paddedAreas, "Area", polygonNames);
            Table regularSections = WithoutNamedRecords(sections, "Area", polygonNames);
            Table tabularAreas = regularAreas;
            Table tabularSections = regularSections;
            string proxyPolygon = null;
            if (tabularAreas.Records == 0 && polygonNames.Count != 0)
            {
                proxyPolygon = polygonNames.First();
                tabularAreas = FirstNamedRecord(paddedAreas, "Area", proxyPolygon);
                tabularSections = FirstNamedRecord(sections, "Area", proxyPolygon);
            }
            if (WasApplied)
            {
                PrepareAreaNameConflicts(model, areas,
                    new HashSet<string>(generatedAreas, StringComparer.OrdinalIgnoreCase));
                Table currentAreas = PadExistingTriangles(Read(model, "Connectivity - Area"));
                var currentNames = new HashSet<string>(AreaJoints(currentAreas.Fields, currentAreas.Data,
                    currentAreas.Records, "tabular Undo current areas").Keys, StringComparer.OrdinalIgnoreCase);
                var missingOriginals = new HashSet<string>(originalNames.Where(name => !currentNames.Contains(name)),
                    StringComparer.OrdinalIgnoreCase);
                if (missingOriginals.Count == 0)
                    throw new InvalidDataException("Tabular Undo found no original areas to recreate after Apply.");
                Table currentJoints = Read(model, "Joint Coordinates");
                int currentJointName = Array.IndexOf(currentJoints.Fields, "Joint");
                int originalJointName = Array.IndexOf(joints.Fields, "Joint");
                if (currentJointName < 0 || originalJointName < 0)
                    throw new InvalidDataException("Joint name field missing during tabular Undo.");
                var currentJointNames = new HashSet<string>(Enumerable.Range(0, currentJoints.Records)
                    .Select(row => currentJoints.Data[row * currentJoints.Fields.Length + currentJointName]),
                    StringComparer.OrdinalIgnoreCase);
                var missingJoints = new HashSet<string>(Enumerable.Range(0, joints.Records)
                    .Select(row => joints.Data[row * joints.Fields.Length + originalJointName])
                    .Where(name => !currentJointNames.Contains(name)), StringComparer.OrdinalIgnoreCase);
                Table addJoints = AppendNamedRecords(currentJoints, WithoutGuids(joints), "Joint", missingJoints);
                var missingTabularOriginals = new HashSet<string>(missingOriginals.Where(name =>
                    !polygonNames.Contains(name)), StringComparer.OrdinalIgnoreCase);
                Table addAreas = AppendNamedRecords(currentAreas, WithoutGuids(tabularAreas), "Area",
                    missingTabularOriginals);
                Table addSections = AppendNamedRecords(Read(model, "Area Section Assignments"), tabularSections,
                    "Area", missingTabularOriginals);
                Check(model.DatabaseTables.CancelTableEditing(), "clearing staged tables before adding original areas", "DatabaseTables.CancelTableEditing");
                Stage(model, addJoints);
                Stage(model, addAreas);
                Stage(model, addSections);
                Apply(model, "tabular original area creation", addAreas);
                VerifyConnectivity(model, regularOriginalAreas.Fields, regularOriginalAreas.Data,
                    regularOriginalAreas.Records,
                    "tabular original area creation");
            }
            Check(model.DatabaseTables.CancelTableEditing(), "clearing staged tables before tabular Undo", "DatabaseTables.CancelTableEditing");
            // Keep generated joints until their child areas have been removed.
            // Omitting referenced joints in the same transaction can make SAP
            // delete every area even when ApplyEditedTables reports success.
            Stage(model, Read(model, "Joint Coordinates"));
            Stage(model, tabularAreas);
            Stage(model, tabularSections);
            Apply(model, "tabular geometry", tabularAreas);
            if (polygonNames.Count != 0)
            {
                if (proxyPolygon != null)
                {
                    int proxyCount = 0;
                    string[] proxyNames = null;
                    Check(model.AreaObj.GetNameList(ref proxyCount, ref proxyNames),
                        "checking temporary polygon proxy", "AreaObj.GetNameList");
                    if ((proxyNames ?? new string[0]).Contains(proxyPolygon, StringComparer.OrdinalIgnoreCase))
                        Check(model.AreaObj.Delete(proxyPolygon, eItemType.Objects),
                            "removing temporary polygon proxy " + proxyPolygon, "AreaObj.Delete");
                }
                Stage(model, joints);
                Apply(model, "tabular polygon joint preparation");
                RestoreMissingPolygonJoints(model, snapshot, areas);
                RestoreOriginalAreas(model, areas, sections, 5);
                Console.WriteLine("csv-undo-phase=polygon-object-api count=" + polygonNames.Count);
            }
            Console.WriteLine("csv-undo-phase=geometry seconds=" + clock.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture));

            // Validate areas and GUIDs before restoring dependent assignments.
            try { VerifyConnectivity(model, areas.Fields, areas.Data, areas.Records, "tabular geometry phase"); }
            catch (Exception connectivityFailure)
            {
                int failedCount = 0;
                string[] failedNames = null;
                model.AreaObj.GetNameList(ref failedCount, ref failedNames);
                Table failedTable = Read(model, "Connectivity - Area");
                int failedArea = Array.IndexOf(failedTable.Fields, "Area");
                string[] tableNames = failedArea < 0 ? new string[0] : failedTable.Data
                    .Where((value, index) => index % failedTable.Fields.Length == failedArea)
                    .Where(value => !string.IsNullOrEmpty(value)).Distinct().ToArray();
                throw new InvalidDataException(connectivityFailure.Message + " Current area objects: " +
                    string.Join(",", failedNames ?? new string[0]) + ". Connectivity table areas: " +
                    string.Join(",", tableNames) + ".", connectivityFailure);
            }
            int areaCount = 0;
            string[] areaNames = null;
            Check(model.AreaObj.GetNameList(ref areaCount, ref areaNames), "listing tabular Undo areas", "AreaObj.GetNameList");
            if (areaNames == null || areaCount != originalNames.Count || !originalNames.SetEquals(areaNames))
                throw new InvalidDataException("Missing or additional areas after geometry import.");

            clock.Restart();
            bool staged = false;
            foreach (Table table in tables.Where(item => item.Key != joints.Key && item.Key != areas.Key &&
                item.Key != sections.Key && item.Key != "Area Material Temperatures" && IsObjectAssignment(item)))
            {
                Table current;
                try { current = Read(model, table.Key); }
                catch (InvalidOperationException) { current = null; }
                if (current != null && SameRecords(table, current)) continue;
                Stage(model, table);
                staged = true;
            }
            if (staged) Apply(model, "tabular object assignments");
            RestoreAreaMaterialTemperatures(model, tables);
            Console.WriteLine("csv-undo-phase=assignments seconds=" + clock.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture));

            // When an original table is imported while generated areas still
            // reference their mesh joints, SAP retains those joints. Only now,
            // with child areas removed and assignments restored, can a second
            // complete joint-table import prune their orphan point objects.
            clock.Restart();
            Stage(model, joints);
            Apply(model, "tabular orphan joint cleanup");
            Console.WriteLine("csv-undo-phase=joints seconds=" + clock.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture));

            VerifyOriginalGeometry(model, snapshot, areas);
            foreach (Table expected in tables.Where(IsObjectAssignment))
                if (!SameRecords(expected, Read(model, expected.Key)))
                {
                    Table actual = Read(model, expected.Key);
                    throw new InvalidDataException("Object assignments differ after tabular Undo: " + expected.Key +
                        ": " + RecordDifference(expected, actual));
                }
            if (!SameRecords(joints, Read(model, joints.Key)) ||
                !SameRecords(sections, Read(model, sections.Key)))
                throw new InvalidDataException("Primary table values differ after tabular Undo.");
            RecordUndoMethod("tabular");
            Console.WriteLine("csv-undo-method=tabular verified=true");
        }

        private void RestoreCoreLegacy(cSapModel model, IEnumerable<string> generatedAreas, IEnumerable<string> generatedPoints)
        {
            SnapshotData snapshot = Load(model);
            RestoreAttempted = true;
            List<Table> tables = snapshot.Tables;
            // After Apply SAP has deleted the original shell objects. Generated
            // joints can occupy their coordinates and SAP may silently merge
            // original joint names back into those joints during table import.
            // Remove only objects owned by this mesh before replaying the CSV.
            int areaCount = 0;
            string[] areaNames = null;
            Check(model.AreaObj.GetNameList(ref areaCount, ref areaNames), "listing applied mesh areas", "AreaObj.GetNameList");
            var presentAreas = new HashSet<string>(areaNames ?? new string[0]);
            foreach (string area in generatedAreas.Distinct())
                if (presentAreas.Contains(area))
                    Check(model.AreaObj.Delete(area, eItemType.Objects), "removing applied mesh area " + area, "AreaObj.Delete");
            int pointCount = 0;
            string[] pointNames = null;
            Check(model.PointObj.GetNameList(ref pointCount, ref pointNames), "listing applied mesh joints", "PointObj.GetNameList");
            var presentPoints = new HashSet<string>(pointNames ?? new string[0]);
            foreach (string point in generatedPoints.Distinct())
                if (presentPoints.Contains(point))
                {
                    Check(model.PointObj.SetSpecialPoint(point, true, eItemType.Objects), "preparing applied mesh joint removal " + point, "PointObj.SetSpecialPoint");
                    Check(model.PointObj.DeleteSpecialPoint(point, eItemType.Objects), "removing applied mesh joint " + point, "PointObj.DeleteSpecialPoint");
                }
            Check(model.PointObj.GetNameList(ref pointCount, ref pointNames), "listing joints for CSV reconstruction", "PointObj.GetNameList");
            presentPoints = new HashSet<string>(pointNames ?? new string[0]);
            foreach (KeyValuePair<string, double[]> joint in snapshot.JointCoordinates)
            {
                if (presentPoints.Contains(joint.Key)) continue;
                string restoredName = "";
                Check(model.PointObj.AddCartesian(joint.Value[0], joint.Value[1], joint.Value[2],
                    ref restoredName, joint.Key, "Global", true, 0), "recreating original joint " + joint.Key, "PointObj.AddCartesian");
                if (restoredName != joint.Key)
                    throw new InvalidDataException("SAP2000 merged original joint " + joint.Key + " with " + restoredName);
            }
            var primary = new[] { "Joint Coordinates", "Connectivity - Area", "Area Section Assignments" };
            Table originalAreas = tables.First(item => item.Key == "Connectivity - Area");
            Table originalSections = tables.First(item => item.Key == "Area Section Assignments");
            Table originalDesignProcedures = tables.FirstOrDefault(item => item.Key == "Area Design Procedures");
            Table importAreas = PadExistingTriangles(originalAreas);
            // SAP 27.2 fixes the design procedure when it creates the area, but
            // silently ignores the same assignment after AddByPoint creation.
            // Restore geometry, section and design procedure atomically.
            model.DatabaseTables.CancelTableEditing();
            Stage(model, tables.First(item => item.Key == "Joint Coordinates"));
            Stage(model, importAreas);
            Stage(model, originalSections);
            if (originalDesignProcedures != null) Stage(model, originalDesignProcedures);
            Apply(model, "geometry", importAreas);
            if (Environment.GetEnvironmentVariable("SAP_QUAD_DEBUG_SNAPSHOT") == "1")
            {
                Table jointsNow = Read(model, "Joint Coordinates");
                Table areasNow = Read(model, "Connectivity - Area");
                Console.WriteLine("csv-snapshot-after-geometry joints=" + string.Join(",", jointsNow.Data.Where((value, index) =>
                    index % jointsNow.Fields.Length == Array.IndexOf(jointsNow.Fields, "Joint"))) +
                    " areas=" + string.Join(",", areasNow.Data.Where((value, index) =>
                    index % areasNow.Fields.Length == Array.IndexOf(areasNow.Fields, "Area"))));
            }
            bool assignmentsChanged = false;
            foreach (Table table in tables.Where(item => !primary.Contains(item.Key) &&
                item.Key != "Area Material Temperatures" && item.Key != "Area Design Procedures" &&
                IsObjectAssignment(item)))
            {
                Table current;
                try { current = Read(model, table.Key); }
                catch (InvalidOperationException) { current = null; } // An assigned table may be empty after Apply.
                if (current == null || !table.Fields.SequenceEqual(current.Fields) || !table.Data.SequenceEqual(current.Data))
                {
                    Stage(model, table);
                    assignmentsChanged = true;
                }
            }
            if (assignmentsChanged) Apply(model, "assignments");
            RestoreAreaDesignProcedures(model, tables);
            RestoreAreaMaterialTemperatures(model, tables);
            foreach (string key in primary)
            {
                Table expected = tables.First(item => item.Key == key), actual = Read(model, key);
                if (actual.Records != expected.Records)
                {
                    int column = Array.IndexOf(expected.Fields, key == "Joint Coordinates" ? "Joint" : "Area");
                    string[] original = column < 0 ? new string[0] : expected.Data.Where((value, index) => index % expected.Fields.Length == column).ToArray();
                    string[] restored = column < 0 ? new string[0] : actual.Data.Where((value, index) => index % actual.Fields.Length == column).ToArray();
                    throw new InvalidDataException("Original model record count not restored in " + key +
                        ": expected " + expected.Records + ", actual " + actual.Records +
                        ", missing " + string.Join(",", original.Except(restored).Take(8)) +
                        ", extra " + string.Join(",", restored.Except(original).Take(8)));
                }
                int name = Array.IndexOf(expected.Fields, key == "Joint Coordinates" ? "Joint" : "Area");
                if (key != "Connectivity - Area" && name >= 0 &&
                    !expected.Data.Where((value, index) => index % expected.Fields.Length == name)
                    .SequenceEqual(actual.Data.Where((value, index) => index % actual.Fields.Length == name)))
                    throw new InvalidDataException("Original model object names not restored in " + key);
            }
            foreach (Table expected in tables.Where(IsObjectAssignment))
            {
                Table actual = Read(model, expected.Key);
                if (!SameRecordsAllowingGeneratedGuids(expected, actual))
                    throw new InvalidDataException("Original object assignments not restored in " + expected.Key +
                        ": " + RecordDifference(expected, actual));
            }
            VerifyConnectivity(model, originalAreas.Fields, originalAreas.Data, originalAreas.Records, "CSV Undo");
            RecordUndoMethod("legacy-object");
            Console.WriteLine("csv-undo-method=legacy-object verified=true");
        }

        internal void Delete()
        {
            try { File.Delete(Path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
