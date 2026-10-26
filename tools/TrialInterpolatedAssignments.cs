// SPDX-License-Identifier: GPL-2.0-or-later
// Read-only fixture trial: models are opened, modified in memory and closed without saving.
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using CSiAPIv1;

class TrialInterpolatedAssignments
{
    static void Check(int code, string action)
    {
        if (code != 0) throw new InvalidOperationException(action + " returned " + code);
    }

    static int Main(string[] args)
    {
        if (args.Length != 3) return 2;
        cHelper helper = new Helper();
        cOAPI sap = helper.CreateObject(@"C:\Program Files\Computers and Structures\SAP2000 27\SAP2000.exe");
        Check(sap.ApplicationStart(), "starting SAP2000");
        try
        {
            Assembly plugin = Assembly.LoadFrom(Path.GetFullPath(args[2]));
            Type pluginType = plugin.GetType("SapQuadTransitionPlugin.cPlugin", true);
            MethodInfo capture = pluginType.GetMethod("CaptureAreaLoads", BindingFlags.Static | BindingFlags.NonPublic);
            if (capture == null) throw new MissingMethodException("CaptureAreaLoads");
            RunCase(sap.SapModel, args[0], capture, 4, new[] { 0.10, 0.20, 0.40, 0.30 }, 20.0, 2);
            RunCase(sap.SapModel, args[1], capture, 8,
                new[] { 0.10, 0.14, 0.20, 0.28, 0.34, 0.30, 0.22, 0.15 }, 6.0, 1);
            Console.WriteLine("PASS interpolated joint patterns, thickness overrides and joint offsets");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            sap.ApplicationExit(false);
        }
    }

    static void RunCase(cSapModel model, string file, MethodInfo capture, int expectedPoints,
        double[] sourceThickness, double expectedPatternAtCenter, int offsetType)
    {
        Check(model.File.OpenFile(Path.GetFullPath(file)), "opening " + file);
        int areaCount = 0;
        string[] areaNames = null;
        Check(model.AreaObj.GetNameList(ref areaCount, ref areaNames), "listing source areas");
        string sourceArea = null;
        foreach (string candidate in areaNames ?? new string[0])
        {
            int candidateCount = 0;
            string[] candidatePoints = null;
            Check(model.AreaObj.GetPoints(candidate, ref candidateCount, ref candidatePoints),
                "reading candidate source connectivity");
            if (candidateCount == expectedPoints) { sourceArea = candidate; break; }
        }
        if (sourceArea == null && expectedPoints == 8)
        {
            string baseArea = null;
            string[] corners = null;
            foreach (string candidate in areaNames ?? new string[0])
            {
                int candidateCount = 0;
                string[] candidatePoints = null;
                Check(model.AreaObj.GetPoints(candidate, ref candidateCount, ref candidatePoints),
                    "reading quadrilateral fixture connectivity");
                if (candidateCount == 4) { baseArea = candidate; corners = candidatePoints; break; }
            }
            if (baseArea != null)
            {
                string baseProperty = "";
                Check(model.AreaObj.GetProperty(baseArea, ref baseProperty), "reading quadrilateral fixture property");
                var polygon = new string[8];
                double centerX = 0, centerY = 0, centerZ = 0;
                Check(model.PointObj.GetCoordCartesian(corners[0], ref centerX, ref centerY, ref centerZ, "Global"),
                    "reading fixture origin");
                centerX += 1000;
                for (int i = 0; i < 8; i++)
                {
                    double angle = 2 * Math.PI * i / 8;
                    string point = "";
                    Check(model.PointObj.AddCartesian(centerX + 10 * Math.Cos(angle), centerY + 10 * Math.Sin(angle),
                        centerZ, ref point, "JT8_POINT_" + i, "Global", false, 0),
                        "creating octagonal fixture point");
                    polygon[i] = point;
                }
                string polygonName = "";
                Check(model.AreaObj.AddByPoint(8, ref polygon, ref polygonName, baseProperty, "JT8_SOURCE"),
                    "creating octagonal fixture area");
                sourceArea = polygonName;
            }
        }
        if (string.IsNullOrEmpty(sourceArea)) throw new InvalidDataException("Fixture has no source area.");
        int count = 0;
        string[] sourcePoints = null;
        Check(model.AreaObj.GetPoints(sourceArea, ref count, ref sourcePoints), "reading source connectivity");
        if (count != expectedPoints) throw new InvalidDataException("Unexpected source point count: " + count);
        Check(model.PatternDef.SetPattern("test"), "defining source joint pattern");
        for (int i = 0; i < count; i++)
        {
            double value = expectedPatternAtCenter + i - (count - 1) / 2.0;
            Check(model.PointObj.SetPatternByXYZ(sourcePoints[i], "test", 0, 0, 0, value,
                eItemType.Objects, 0, false), "assigning source joint pattern");
        }
        double[] thicknessInput = (double[])sourceThickness.Clone();
        Check(model.AreaObj.SetThickness(sourceArea, 2, "", 0, ref thicknessInput, eItemType.Objects),
            "assigning source thickness overrides");
        double[] sourceOffsets = sourceThickness.Select(value => value * 0.1).ToArray();
        double[] offsetInput = offsetType == 2 ? (double[])sourceOffsets.Clone() : new double[0];
        string offsetPattern = offsetType == 1 ? "offset_default" : "";
        double offsetScale = offsetType == 1 ? 0.001 : 0;
        if (offsetType == 1) Check(model.PatternDef.SetPattern(offsetPattern), "defining unassigned offset pattern");
        Check(model.AreaObj.SetOffsets(sourceArea, offsetType, offsetPattern, offsetScale, ref offsetInput, eItemType.Objects),
            "assigning source joint offsets");

        double[] center = new double[3];
        foreach (string point in sourcePoints)
        {
            double x = 0, y = 0, z = 0;
            Check(model.PointObj.GetCoordCartesian(point, ref x, ref y, ref z, "Global"), "reading " + point);
            center[0] += x / count;
            center[1] += y / count;
            center[2] += z / count;
        }
        string centerName = "";
        Check(model.PointObj.AddCartesian(center[0], center[1], center[2], ref centerName,
            "JP_THICKNESS_CENTER", "Global", false, 0), "creating center point");

        object loads = capture.Invoke(null, new object[] { model, sourceArea });
        MethodInfo apply = loads.GetType().GetMethod("Apply", BindingFlags.Instance | BindingFlags.Public);
        if (apply == null) throw new MissingMethodException("AreaLoads.Apply");
        string property = "";
        Check(model.AreaObj.GetProperty(sourceArea, ref property), "reading source property");
        for (int i = 0; i < count; i++)
        {
            string[] childPoints = { sourcePoints[i], sourcePoints[(i + 1) % count], centerName };
            string child = "";
            Check(model.AreaObj.AddByPoint(3, ref childPoints, ref child, property, ""), "creating trial child");
            apply.Invoke(loads, new object[] { model, child, childPoints });
            int type = 0;
            string pattern = null;
            double scale = 0;
            double[] actual = null;
            Check(model.AreaObj.GetThickness(child, ref type, ref pattern, ref scale, ref actual),
                "reading child thickness");
            if (type != 2 || actual == null || actual.Length < 3)
                throw new InvalidDataException("Invalid child thickness assignment on " + child);
            double expectedCenterThickness = sourceThickness.Average();
            if (Math.Abs(actual[0] - sourceThickness[i]) > 1e-9 ||
                Math.Abs(actual[1] - sourceThickness[(i + 1) % count]) > 1e-9 ||
                Math.Abs(actual[2] - expectedCenterThickness) > 1e-9)
                throw new InvalidDataException("Wrong interpolated thickness on " + child + ": " + string.Join(",", actual));
            type = 0;
            pattern = null;
            scale = 0;
            actual = null;
            Check(model.AreaObj.GetOffsets(child, ref type, ref pattern, ref scale, ref actual),
                "reading child joint offsets");
            if (type != offsetType || !string.Equals(pattern ?? "", offsetPattern, StringComparison.OrdinalIgnoreCase) ||
                Math.Abs(scale - offsetScale) > 1e-12)
                throw new InvalidDataException("Invalid child joint-offset assignment on " + child +
                    ": type=" + type + " pattern='" + pattern + "' scale=" + scale +
                    " expected type=" + offsetType + " pattern='" + offsetPattern + "' scale=" + offsetScale);
            if (offsetType == 2)
            {
                if (actual == null || actual.Length < 3)
                    throw new InvalidDataException("Missing child joint-offset values on " + child);
                double expectedCenterOffset = sourceOffsets.Average();
                if (Math.Abs(actual[0] - sourceOffsets[i]) > 1e-9 ||
                    Math.Abs(actual[1] - sourceOffsets[(i + 1) % count]) > 1e-9 ||
                    Math.Abs(actual[2] - expectedCenterOffset) > 1e-9)
                    throw new InvalidDataException("Wrong interpolated joint offsets on " + child + ": " + string.Join(",", actual));
            }
        }
        double centerPattern = 0;
        Check(model.PointObj.GetPatternValue(centerName, "test", ref centerPattern), "reading center joint pattern");
        if (Math.Abs(centerPattern - expectedPatternAtCenter) > 1e-9)
            throw new InvalidDataException("Wrong center joint pattern: " + centerPattern +
                " expected " + expectedPatternAtCenter);
        Console.WriteLine("PASS " + Path.GetFileName(file) + " center pattern=" + centerPattern +
            " center thickness=" + sourceThickness.Average() + " offset type=" + offsetType);
    }
}
