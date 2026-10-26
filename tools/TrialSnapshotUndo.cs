// SPDX-License-Identifier: GPL-2.0-or-later
// Exercise the V2 CSV snapshot/Restore implementation on a disposable SAP copy.
// Do not save the test model; the caller closes its SAP process without saving.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using CSiAPIv1;

internal static class TrialSnapshotUndo
{
    private static string Hash(string file)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(file)) return BitConverter.ToString(sha.ComputeHash(stream));
    }

    private static Dictionary<string, string[]> Areas(cSapModel model)
    {
        int count = 0;
        string[] names = null;
        if (model.AreaObj.GetNameList(ref count, ref names) != 0 || names == null || count != names.Length)
            throw new InvalidOperationException("Cannot enumerate source areas.");
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names)
        {
            int vertices = 0;
            string[] points = null;
            if (model.AreaObj.GetPoints(name, ref vertices, ref points) != 0 ||
                points == null || vertices != points.Length)
                throw new InvalidOperationException("Cannot read area " + name);
            result.Add(name, points);
        }
        return result;
    }

    private static void PrintAssignmentCoverage(cSapModel model)
    {
        WithAllPatterns<object>(model, () => { PrintAssignmentCoverageCore(model); return null; });
    }

    private static void PrintAssignmentCoverageCore(cSapModel model)
    {
        int total = 0;
        string[] keys = null, names = null;
        int[] kinds = null;
        bool[] empty = null;
        if (model.DatabaseTables.GetAllTables(ref total, ref keys, ref names, ref kinds, ref empty) != 0)
            throw new InvalidOperationException("Cannot inspect snapshot table coverage.");
        for (int i = 0; i < total; i++)
        {
            if (kinds[i] == 0 || empty[i] ||
                !new[] { "Area", "Joint", "Load", "Spring", "Constraint", "Group", "Pattern",
                    "Diaphragm", "Restraint", "Mass", "Link" }.Any(word =>
                    keys[i].IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
            int version = 0, records = 0;
            string[] fields = null, data = null;
            int code = model.DatabaseTables.GetTableForEditingArray(keys[i], "", ref version,
                ref fields, ref records, ref data);
            if (code != 0) { Console.WriteLine("SCHEMA_UNREADABLE=" + keys[i] + " CODE=" + code); continue; }
            bool tracked = keys[i] == "Groups 2 - Assignments" ||
                (fields ?? new string[0]).Any(f => f.Equals("Area", StringComparison.OrdinalIgnoreCase) ||
                    f.Equals("Joint", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine("SCHEMA=" + keys[i] + " ROWS=" + records + " ASSIGNMENT_RESTORED=" + tracked +
                " FIELDS=" + string.Join("|", fields ?? new string[0]));
        }
    }

    private static T WithAllPatterns<T>(cSapModel model, Func<T> operation)
    {
        int previousCount = 0, count = 0;
        string[] previous = null, all = null;
        Require(model.DatabaseTables.GetLoadPatternsSelectedForDisplay(ref previousCount, ref previous),
            "GetLoadPatternsSelectedForDisplay");
        Require(model.LoadPatterns.GetNameList(ref count, ref all), "LoadPatterns.GetNameList");
        string[] chosen = (string[])(all ?? new string[0]).Clone();
        Require(model.DatabaseTables.SetLoadPatternsSelectedForDisplay(ref chosen),
            "SelectAllLoadPatternsForVerification");
        try { return operation(); }
        finally
        {
            string[] restore = (string[])(previous ?? new string[0]).Clone();
            Require(model.DatabaseTables.SetLoadPatternsSelectedForDisplay(ref restore),
                "RestoreLoadPatternSelectionAfterVerification");
            int finalCount = 0;
            string[] final = null;
            Require(model.DatabaseTables.GetLoadPatternsSelectedForDisplay(ref finalCount, ref final),
                "CheckRestoredLoadPatternSelection");
            Console.WriteLine("LOAD_DISPLAY_SELECTION_RESTORED=" +
                (finalCount == previousCount && (previous ?? new string[0]).SequenceEqual(final ?? new string[0])));
        }
    }

    private static void Require(int code, string action)
    {
        Console.WriteLine("FIXTURE=" + action + " CODE=" + code);
        if (code != 0) throw new InvalidOperationException("Fixture API failed: " + action + " code=" + code);
    }

    private static void AddFixtureAssignments(cSapModel model)
    {
        const string load = "SQM_UNDO_FIXTURE_LOAD";
        const string pattern = "SQM_UNDO_FIXTURE_PATTERN";
        const string constraint = "SQM_UNDO_FIXTURE_BODY";
        Require(model.LoadPatterns.Add(load, eLoadPatternType.Other, 0, true), "LoadPatterns.Add");
        Require(model.AreaObj.SetLoadUniform("2", load, 1.25, 6, true, "Global", eItemType.Objects),
            "AreaObj.SetLoadUniform");
        double[] vector = { 0, 0, 0 };
        Require(model.AreaObj.SetSpring("2", 1, 1000, 1, "", -1, 1, 3, true,
            ref vector, 0, true, "Local", eItemType.Objects), "AreaObj.SetSpring");
        double[] stiffness = { 100, 200, 300, 0, 0, 0 };
        Require(model.PointObj.SetSpring("4144", ref stiffness, eItemType.Objects, true, true),
            "PointObj.SetSpring");
        bool[] restrained = { true, false, false, false, false, false };
        Require(model.PointObj.SetRestraint("4144", ref restrained, eItemType.Objects),
            "PointObj.SetRestraint");
        double[] force = { 0, 0, -7.5, 0, 0, 0 };
        Require(model.PointObj.SetLoadForce("4144", load, ref force, true, "Global", eItemType.Objects),
            "PointObj.SetLoadForce");
        Require(model.PatternDef.SetPattern(pattern), "PatternDef.SetPattern");
        Require(model.PointObj.SetPatternByXYZ("4144", pattern, 0, 0, 0, 3.14,
            eItemType.Objects, 0, true), "PointObj.SetPatternByXYZ");
        bool[] axes = { true, true, true, false, false, false };
        Require(model.ConstraintDef.SetBody(constraint, ref axes, "Global"), "ConstraintDef.SetBody");
        string assigned = constraint;
        Require(model.PointObj.SetConstraint("4144", ref assigned, eItemType.Objects, true),
            "PointObj.SetConstraint");
    }

    private static void SelectAllLoadPatterns(cSapModel model)
    {
        int selected = 0, available = 0;
        string[] previouslySelected = null, all = null;
        Require(model.DatabaseTables.GetLoadPatternsSelectedForDisplay(ref selected, ref previouslySelected),
            "DatabaseTables.GetLoadPatternsSelectedForDisplay");
        Require(model.LoadPatterns.GetNameList(ref available, ref all), "LoadPatterns.GetNameList");
        Console.WriteLine("DISPLAY_LOAD_PATTERNS_BEFORE=" + selected + ":" +
            string.Join("|", previouslySelected ?? new string[0]) + " ALL=" + available + ":" +
            string.Join("|", all ?? new string[0]));
        Require(model.DatabaseTables.SetLoadPatternsSelectedForDisplay(ref all),
            "DatabaseTables.SetLoadPatternsSelectedForDisplay");
    }

    private static Dictionary<string, string[]> AssignmentRows(cSapModel model)
    {
        return WithAllPatterns(model, () => AssignmentRowsCore(model));
    }

    private static Dictionary<string, string[]> AssignmentRowsCore(cSapModel model)
    {
        int total = 0;
        string[] keys = null, names = null;
        int[] kinds = null;
        bool[] empty = null;
        if (model.DatabaseTables.GetAllTables(ref total, ref keys, ref names, ref kinds, ref empty) != 0)
            throw new InvalidOperationException("Assignment inventory unavailable.");
        var rows = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < total; i++)
        {
            if (kinds[i] == 0 || empty[i]) continue;
            int version = 0, count = 0;
            string[] fields = null, data = null;
            if (model.DatabaseTables.GetTableForEditingArray(keys[i], "", ref version,
                ref fields, ref count, ref data) != 0 || fields == null || data == null) continue;
            bool objectBound = keys[i] == "Groups 2 - Assignments" ||
                fields.Any(f => f.Equals("Area", StringComparison.OrdinalIgnoreCase) ||
                                f.Equals("Joint", StringComparison.OrdinalIgnoreCase));
            if (!objectBound || keys[i] == "Connectivity - Area" || keys[i] == "Joint Coordinates") continue;
            rows[keys[i]] = Enumerable.Range(0, count).Select(row =>
                string.Join("\u001f", data.Skip(row * fields.Length).Take(fields.Length).Select(v => v ?? "")))
                .OrderBy(line => line, StringComparer.Ordinal).ToArray();
        }
        return rows;
    }

    private static void ProbeSelectionGroup(cSapModel model)
    {
        Require(model.AreaObj.SetSelected("2", true, eItemType.Objects), "SelectArea2ForGroupProbe");
        foreach (string key in new[] { "Connectivity - Area", "Joint Coordinates",
            "Area Section Assignments", "Area Loads - Uniform", "Joint Loads - Force" })
            foreach (string group in new[] { "", "ALL" })
            {
                int version = 0, records = 0;
                string[] fields = null, data = null;
                int code = model.DatabaseTables.GetTableForEditingArray(key, group, ref version,
                    ref fields, ref records, ref data);
                Console.WriteLine("GROUP_FILTER key=" + key + " group=" +
                    (group.Length == 0 ? "<empty>" : group) + " code=" + code + " records=" + records);
            }
        Require(model.SelectObj.ClearSelection(), "ClearGroupProbeSelection");
    }

    private static int Main(string[] args)
    {
        if (args.Length != 4 || (args[0] != "--trial-undo" && args[0] != "--trial-assigned-undo" &&
            args[0] != "--trial-load-display" && args[0] != "--probe-selection-group") ||
            !int.TryParse(args[1], out int pid))
        {
            Console.Error.WriteLine("Usage: --trial-undo|--trial-assigned-undo|--trial-load-display|--probe-selection-group PID TEMP_COPY.sdb V2_DLL");
            return 2;
        }
        string file = Path.GetFullPath(args[2]);
        string temp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "opencode")) + Path.DirectorySeparatorChar;
        if (!file.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(file) != "Circular_Floor_Beta_Copy.sdb" || !File.Exists(file))
            throw new InvalidOperationException("Only a disposable circular-floor copy may be changed.");
        Process process = Process.GetProcessById(pid);
        if (process.ProcessName != "SAP2000" ||
            process.MainWindowTitle.IndexOf("Circular_Floor_Beta_Copy", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Test process is not displaying the requested copy.");
        cHelper helper = new Helper();
        cOAPI sap = helper.GetObjectProcess("CSI.SAP2000.API.SapObject", pid);
        if (sap == null) throw new InvalidOperationException("Cannot attach to test SAP.");
        cSapModel model = sap.SapModel;
        if (!string.Equals(Path.GetFullPath(model.GetModelFilename(true)), file, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Attached model filename differs.");
        int selected = 0;
        int[] kinds = null;
        string[] selection = null;
        if (model.SelectObj.GetSelected(ref selected, ref kinds, ref selection) != 0 || selected != 0)
            throw new InvalidOperationException("Test requires no selection/full table snapshot.");
        if (args[0] == "--probe-selection-group")
        {
            ProbeSelectionGroup(model);
            return 0;
        }
        string disk = Hash(file), guid = null, property = null;
        if (args[0] == "--trial-assigned-undo" || args[0] == "--trial-load-display")
            AddFixtureAssignments(model);
        if (args[0] == "--trial-load-display") SelectAllLoadPatterns(model);
        Dictionary<string, string[]> assignmentsBefore = AssignmentRows(model);
        Console.WriteLine("ASSIGNMENT_TABLES_BEFORE=" + assignmentsBefore.Count);
        if (model.AreaObj.GetGUID("2", ref guid) != 0 ||
            model.AreaObj.GetProperty("2", ref property) != 0)
            throw new InvalidOperationException("Source area metadata missing.");
        Dictionary<string, string[]> before = Areas(model);
        if (before.Count != 8 || before["2"].Length != 21)
            throw new InvalidOperationException("Expected eight source areas including polygon 2.");
        Type snapshotType = Assembly.LoadFrom(Path.GetFullPath(args[3])).GetType(
            "SapQuadTransitionPlugin.ModelCsvSnapshot", true);
        MethodInfo capture = snapshotType.GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo restore = snapshotType.GetMethod("Restore", BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo delete = snapshotType.GetMethod("Delete", BindingFlags.Instance | BindingFlags.NonPublic);
        if (capture == null || restore == null || delete == null)
            throw new MissingMethodException("V2 snapshot/Undo methods missing.");
        object snapshot = null;
        int result = 0;
        try
        {
            Console.WriteLine("SNAPSHOT_CAPTURE_START AREAS=8 AREA2_VERTICES=21");
            snapshot = capture.Invoke(null, new object[] { model, null });
            Console.WriteLine("SNAPSHOT_CAPTURE=PASS");
            PrintAssignmentCoverage(model);
            int removed = model.AreaObj.Delete("2", eItemType.Objects);
            Console.WriteLine("AREA2_DELETE_CODE=" + removed);
            if (removed != 0) throw new InvalidOperationException("Could not remove test area 2.");
            restore.Invoke(snapshot, new object[] { model, new string[0], new string[0], true });
            Console.WriteLine("CSV_UNDO=PASS");
        }
        catch (TargetInvocationException error)
        {
            Console.WriteLine("SNAPSHOT_OR_UNDO_ERROR=" + error.InnerException);
            result = 1;
        }
        catch (Exception error)
        {
            Console.WriteLine("SNAPSHOT_OR_UNDO_ERROR=" + error);
            result = 1;
        }
        finally
        {
            if (snapshot != null) try { delete.Invoke(snapshot, null); }
                catch (Exception error) { Console.WriteLine("SNAPSHOT_DELETE_ERROR=" + error.Message); }
            Dictionary<string, string[]> after = Areas(model);
            string newGuid = null, newProperty = null;
            model.AreaObj.GetGUID("2", ref newGuid);
            model.AreaObj.GetProperty("2", ref newProperty);
            Console.WriteLine("AFTER_AREAS=" + after.Count + " ORIGINAL_JOINTS=" + model.PointObj.Count());
            Console.WriteLine("GUID_IDENTICAL=" + (guid == newGuid) + " PROPERTY_IDENTICAL=" + (property == newProperty));
            foreach (KeyValuePair<string, string[]> area in before)
            {
                string[] vertices;
                if (!after.TryGetValue(area.Key, out vertices)) Console.WriteLine("AREA_MISSING=" + area.Key);
                else if (!area.Value.SequenceEqual(vertices)) Console.WriteLine("AREA_CHANGED=" + area.Key);
            }
            bool same = after.Count == before.Count && before.All(a =>
                after.ContainsKey(a.Key) && a.Value.SequenceEqual(after[a.Key]));
            Console.WriteLine("ALL_AREA_SEQUENCES_IDENTICAL=" + same);
            Dictionary<string, string[]> assignmentsAfter = AssignmentRows(model);
            bool assignmentsSame = assignmentsBefore.Count == assignmentsAfter.Count &&
                assignmentsBefore.All(table => assignmentsAfter.ContainsKey(table.Key) &&
                    table.Value.SequenceEqual(assignmentsAfter[table.Key]));
            foreach (KeyValuePair<string, string[]> table in assignmentsBefore)
                if (!assignmentsAfter.ContainsKey(table.Key) ||
                    !table.Value.SequenceEqual(assignmentsAfter[table.Key]))
                    Console.WriteLine("ASSIGNMENT_CHANGED=" + table.Key);
            Console.WriteLine("ALL_OBJECT_ASSIGNMENTS_IDENTICAL=" + assignmentsSame);
            Console.WriteLine("DISK_COPY_UNCHANGED=" + (disk == Hash(file)));
            if (!same || !assignmentsSame || guid != newGuid || property != newProperty) result = 1;
        }
        return result;
    }
}
