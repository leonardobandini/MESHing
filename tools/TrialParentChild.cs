// SPDX-License-Identifier: GPL-2.0-or-later
// Disposable model only: exercise the production parent's captured assignments
// on one child shell. No save; caller closes the isolated SAP process afterward.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using CSiAPIv1;

class TrialParentChild
{
    static void Check(int code, string action)
    {
        Console.WriteLine(action + "=" + code);
        if (code != 0) throw new InvalidOperationException(action + " returned " + code);
    }

    static void Main(string[] args)
    {
        if ((args.Length != 3 && args.Length != 4) || !int.TryParse(args[0], out int pid))
            throw new ArgumentException("Usage: PID TEMP_COPY.sdb V2_DLL");
        string file = Path.GetFullPath(args[1]);
        string temp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "opencode")) + Path.DirectorySeparatorChar;
        if (!file.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(file) != "Circular_Floor_Beta_Copy.sdb" || !File.Exists(file))
            throw new InvalidOperationException("Only a disposable circular-floor copy may be used.");
        Process process = Process.GetProcessById(pid);
        if (process.ProcessName != "SAP2000" ||
            process.MainWindowTitle.IndexOf("Circular_Floor_Beta_Copy", StringComparison.OrdinalIgnoreCase) < 0)
            throw new InvalidOperationException("Wrong SAP window.");
        cHelper helper = new Helper();
        cOAPI sap = helper.GetObjectProcess("CSI.SAP2000.API.SapObject", pid);
        cSapModel model = sap.SapModel;
        if (!string.Equals(Path.GetFullPath(model.GetModelFilename(true)), file, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Wrong model filename.");
        if (args.Length == 4 && (args[3] == "--probe-code" || args[3] == "--probe-set-overwrite" ||
            args[3] == "--trial-design" || args[3] == "--trial-design-transfer"))
        {
            string code = null;
            Console.WriteLine("SHELL_DESIGN_CODE_RESULT=" +
                model.DesignConcreteShell.GetCode(ref code) + " CODE=" + code);
            int procVersion = 0, procRows = 0;
            string[] procFields = null, procValues = null;
            Console.WriteLine("DESIGN_PROCEDURES_READ=" + model.DatabaseTables.GetTableForEditingArray(
                "Area Design Procedures", "", ref procVersion, ref procFields, ref procRows, ref procValues));
            if (procFields != null && procValues != null)
            {
                int nameColumn = Array.IndexOf(procFields, "Area"), designColumn = Array.IndexOf(procFields, "DesignProc");
                for (int r = 0; r < procRows && nameColumn >= 0 && designColumn >= 0; r++)
                    Console.WriteLine("DESIGN_AREA=" + procValues[r * procFields.Length + nameColumn] +
                        " PROCEDURE=" + procValues[r * procFields.Length + designColumn]);
            }
            if (args[3] == "--probe-set-overwrite")
            {
                double value = 0;
                bool probeDetermined = true;
                Console.WriteLine("BEFORE_GET_OVERWRITE=" + model.DesignConcreteShell.Eurocode_2_2004.GetOverwrite(
                    "2", 1, ref value, ref probeDetermined) + " VALUE=" + value + " DET=" + probeDetermined);
                Console.WriteLine("SET_OVERWRITE=" + model.DesignConcreteShell.Eurocode_2_2004.SetOverwrite(
                    "2", 1, 2, eItemType.Objects));
                Console.WriteLine("AFTER_GET_OVERWRITE=" + model.DesignConcreteShell.Eurocode_2_2004.GetOverwrite(
                    "2", 1, ref value, ref probeDetermined) + " VALUE=" + value + " DET=" + probeDetermined);
            }
            if (args[3] == "--trial-design")
            {
                string chosen="No Design";
                int originalVersion=procVersion;
                int areaIndex=Array.IndexOf(procFields,"Area"),designIndex=Array.IndexOf(procFields,"DesignProc");
                if(areaIndex<0||designIndex<0)throw new InvalidOperationException("Design field missing.");
                string[] altered=(string[])procValues.Clone();
                for(int row=0;row<procRows;row++)
                    if(altered[row*procFields.Length+areaIndex]=="2")
                        altered[row*procFields.Length+designIndex]=chosen;
                Check(model.DatabaseTables.SetTableForEditingArray("Area Design Procedures",ref procVersion,
                    ref procFields,procRows,ref altered),"SET_PARENT_DESIGN");
                int fatal=0,errors=0,warnings=0,info=0;
                string log="";
                Check(model.DatabaseTables.ApplyEditedTables(true,ref fatal,ref errors,ref warnings,
                    ref info,ref log),"APPLY_PARENT_DESIGN");
                Console.WriteLine("DESIGN_IMPORT fatal="+fatal+" errors="+errors+" warnings="+warnings);
                if(errors!=0||fatal!=0||warnings!=0){Console.WriteLine(log);return;}
                int afterRows=0;string[] afterFields=null,afterData=null;
                Check(model.DatabaseTables.GetTableForEditingArray("Area Design Procedures","",
                    ref originalVersion,ref afterFields,ref afterRows,ref afterData),"READ_PARENT_DESIGN");
                Console.WriteLine("DESIGN_PARENT_AFTER="+string.Join("|",Enumerable.Range(0,afterRows).Select(r=>
                    afterData[r*afterFields.Length+areaIndex]+":"+afterData[r*afterFields.Length+designIndex])));
            }
            if (args[3] == "--trial-design-transfer")
            {
                Type pluginType=Assembly.LoadFrom(Path.GetFullPath(args[2])).GetType("SapQuadTransitionPlugin.cPlugin",true);
                object captured=pluginType.GetMethod("CaptureAreaLoads",BindingFlags.Static|BindingFlags.NonPublic)
                    .Invoke(null,new object[]{model,"2"});
                var children=(System.Collections.Generic.List<string>)captured.GetType().GetField("children",
                    BindingFlags.Instance|BindingFlags.NonPublic).GetValue(captured);
                children.Add("SQM_TRIAL_CHILD");
                MethodInfo finishDesign=captured.GetType().GetMethod("Finish",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                finishDesign.Invoke(captured,new object[]{model});
                Console.WriteLine("DESIGN_TRANSFER_FINISH=PASS");
                Check(model.AreaObj.Delete("2",eItemType.Objects),"DELETE_PARENT_DESIGN");
                Check(model.AreaObj.ChangeName("SQM_TRIAL_CHILD","2"),"RENAME_DESIGN_CHILD");
                MethodInfo remap=captured.GetType().GetMethod("RemapChildName",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
                remap.Invoke(captured,new object[]{"SQM_TRIAL_CHILD","2"});
                finishDesign.Invoke(captured,new object[]{model});
                int afterVersion=0,afterRows=0;string[] afterFields=null,afterData=null;
                Check(model.DatabaseTables.GetTableForEditingArray("Area Design Procedures","",
                    ref afterVersion,ref afterFields,ref afterRows,ref afterData),"READ_COMMITTED_DESIGN");
                int colArea=Array.IndexOf(afterFields,"Area"),colDesign=Array.IndexOf(afterFields,"DesignProc");
                Console.WriteLine("DESIGN_COMMITTED_VALUES="+string.Join("|",Enumerable.Range(0,afterRows).Select(r=>
                    afterData[r*afterFields.Length+colArea]+":"+afterData[r*afterFields.Length+colDesign])));
                if(!Enumerable.Range(0,afterRows).Any(r=>afterData[r*afterFields.Length+colArea]=="2"&&
                    afterData[r*afterFields.Length+colDesign]=="No Design"))
                    throw new InvalidOperationException("Non-default area design procedure was lost on commit.");
                Console.WriteLine("NONDEFAULT_DESIGN_PROCEDURE_PASS");
            }
            return;
        }
        const string parent = "2";
        double[] modifiers = null;
        Check(model.AreaObj.GetModifiers(parent, ref modifiers), "GET_PARENT_MODIFIERS");
        Console.WriteLine("MODIFIER_COUNT=" + (modifiers == null ? -1 : modifiers.Length));
        if (modifiers == null || modifiers.Length != 10) throw new InvalidOperationException("Unexpected modifiers.");
        modifiers[0] = .72;
        Check(model.AreaObj.SetModifiers(parent, ref modifiers, eItemType.Objects), "SET_PARENT_MODIFIERS");
        double[] vector = { 0, 0, 0 };
        Check(model.AreaObj.SetSpring(parent, 1, 725, 1, "", -1, 1, 3, true,
            ref vector, 0, true, "Local", eItemType.Objects), "SET_PARENT_SPRING");
        Check(model.AreaObj.SetLocalAxes(parent, 12.5, eItemType.Objects), "SET_PARENT_AXES");
        Check(model.AreaObj.SetMass(parent, 23, true, eItemType.Objects), "SET_PARENT_MASS");
        Check(model.AreaObj.SetEdgeConstraint(parent, true, eItemType.Objects), "SET_PARENT_EDGE");
        Check(model.LoadPatterns.Add("SQM_PARENT_LOAD", eLoadPatternType.Other, 0, true), "ADD_LOAD_PATTERN");
        Check(model.AreaObj.SetLoadUniform(parent, "SQM_PARENT_LOAD", 3.75, 6, true,
            "Global", eItemType.Objects), "SET_PARENT_UNIFORM_LOAD");
        Check(model.AreaObj.SetLoadSurfacePressure(parent, "SQM_PARENT_LOAD", -1, 1.5,
            "", true, eItemType.Objects), "SET_PARENT_PRESSURE");
        int availableGroups = 0;
        string[] groups = null;
        Check(model.GroupDef.GetNameList(ref availableGroups, ref groups), "GET_GROUPS");
        string group = (groups ?? new string[0]).FirstOrDefault(g =>
            !string.Equals(g, "ALL", StringComparison.OrdinalIgnoreCase));
        if (group == null) throw new InvalidOperationException("No test group available.");
        Check(model.AreaObj.SetGroupAssign(parent, group, false, eItemType.Objects), "SET_PARENT_GROUP");
        double overwrite = 0;
        bool programDetermined = true;
        int overwriteCode = model.DesignConcreteShell.Eurocode_2_2004.GetOverwrite(parent, 1,
            ref overwrite, ref programDetermined);
        Console.WriteLine("GET_PARENT_EC2_OVERWRITE=" + overwriteCode + " VALUE=" + overwrite +
            " PROGRAM_DETERMINED=" + programDetermined);
        if (overwriteCode == 0)
            Check(model.DesignConcreteShell.Eurocode_2_2004.SetOverwrite(parent, 1,
                overwrite == 0 ? 1 : overwrite, eItemType.Objects), "SET_PARENT_EC2_OVERWRITE");
        string property = "";
        Check(model.AreaObj.GetProperty(parent, ref property), "GET_PARENT_PROPERTY");
        Type plugin = Assembly.LoadFrom(Path.GetFullPath(args[2])).GetType("SapQuadTransitionPlugin.cPlugin", true);
        MethodInfo capture = plugin.GetMethod("CaptureAreaLoads", BindingFlags.Static | BindingFlags.NonPublic);
        object assignments;
        try { assignments = capture.Invoke(null, new object[] { model, parent }); }
        catch (TargetInvocationException error) { throw error.InnerException; }
        int n = 0;
        string[] points = null;
        Check(model.AreaObj.GetPoints(parent, ref n, ref points), "GET_PARENT_POINTS");
        string[] corners = new[] { points[0], points[5], points[10], points[15] };
        string child = "";
        Check(model.AreaObj.AddByPoint(corners.Length, ref corners, ref child, property, "SQM_TRIAL_CHILD"),
            "ADD_CHILD");
        Check(model.AreaObj.SetLocalAxes(child, 12.5, eItemType.Objects), "SET_CHILD_AXES");
        Check(model.AreaObj.SetMass(child, 23, true, eItemType.Objects), "SET_CHILD_MASS");
        Check(model.AreaObj.SetEdgeConstraint(child, true, eItemType.Objects), "SET_CHILD_EDGE");
        MethodInfo apply = assignments.GetType().GetMethod("Apply", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        try { apply.Invoke(assignments, new object[] { child }); }
        catch (TargetInvocationException error) { throw error.InnerException; }
        MethodInfo finish = assignments.GetType().GetMethod("Finish", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        try { finish.Invoke(assignments, new object[] { model }); }
        catch (TargetInvocationException error) { throw error.InnerException; }
        Console.WriteLine("CAPTURE_APPLY_CHILD=PASS CHILD=" + child);
        double[] after = null;
        Check(model.AreaObj.GetModifiers(child, ref after), "GET_CHILD_MODIFIERS");
        int springCount = 0;
        int[] springTypes=null, simpleTypes=null, faces=null, axisTypes=null, dirs=null;
        double[] k=null, vecX=null, vecY=null, vecZ=null, angles=null;
        string[] link=null, systems=null;
        bool[] outward=null;
        Check(model.AreaObj.GetSpring(child, ref springCount, ref springTypes, ref k,
            ref simpleTypes, ref link, ref faces, ref axisTypes, ref dirs, ref outward,
            ref vecX, ref vecY, ref vecZ, ref systems, ref angles), "GET_CHILD_SPRING");
        double childAngle = 0, childMass = 0;
        bool advanced = false, edge = false;
        Check(model.AreaObj.GetLocalAxes(child, ref childAngle, ref advanced), "GET_CHILD_AXES");
        Check(model.AreaObj.GetMass(child, ref childMass), "GET_CHILD_MASS");
        Check(model.AreaObj.GetEdgeConstraint(child, ref edge), "GET_CHILD_EDGE");
        Console.WriteLine("CHILD_ASSIGNMENTS modifiers=" + string.Join("|", after ?? new double[0]) +
            " springs=" + springCount + " springK=" + string.Join("|", k ?? new double[0]) +
            " axes=" + childAngle + " mass=" + childMass + " edge=" + edge);
        int parentLoadCount = 0, childLoadCount = 0;
        string[] objects=null, patterns=null, loadSystems=null;
        int[] loadDirs=null;
        double[] values=null;
        Check(model.AreaObj.GetLoadUniform(parent, ref parentLoadCount, ref objects, ref patterns, ref loadSystems,
            ref loadDirs, ref values, eItemType.Objects), "GET_PARENT_UNIFORM_LOAD");
        objects=patterns=loadSystems=null;loadDirs=null;values=null;
        Check(model.AreaObj.GetLoadUniform(child, ref childLoadCount, ref objects, ref patterns, ref loadSystems,
            ref loadDirs, ref values, eItemType.Objects), "GET_CHILD_UNIFORM_LOAD");
        int parentPressureCount = 0, childPressureCount = 0;
        int[] pressureFaces=null;
        objects=patterns=null;values=null;
        string[] patternNames=null;
        Check(model.AreaObj.GetLoadSurfacePressure(parent, ref parentPressureCount, ref objects,
            ref patterns, ref pressureFaces, ref values, ref patternNames, eItemType.Objects), "GET_PARENT_PRESSURE");
        objects=patterns=patternNames=null;pressureFaces=null;values=null;
        Check(model.AreaObj.GetLoadSurfacePressure(child, ref childPressureCount, ref objects,
            ref patterns, ref pressureFaces, ref values, ref patternNames, eItemType.Objects), "GET_CHILD_PRESSURE");
        int parentGroupCount=0,childGroupCount=0;
        string[] parentGroups=null,childGroups=null;
        Check(model.AreaObj.GetGroupAssign(parent,ref parentGroupCount,ref parentGroups),"GET_PARENT_GROUPS");
        Check(model.AreaObj.GetGroupAssign(child,ref childGroupCount,ref childGroups),"GET_CHILD_GROUPS");
        Console.WriteLine("LOADS_UNIFORM parent="+parentLoadCount+" child="+childLoadCount+
            " PRESSURES parent="+parentPressureCount+" child="+childPressureCount+
            " GROUPS parent="+string.Join("|",parentGroups??new string[0])+
            " child="+string.Join("|",childGroups??new string[0]));
        double childOverwrite = 0;
        bool childProgramDetermined = true;
        if (overwriteCode == 0)
            Check(model.DesignConcreteShell.Eurocode_2_2004.GetOverwrite(child, 1,
                ref childOverwrite, ref childProgramDetermined), "GET_CHILD_EC2_OVERWRITE");
        Console.WriteLine("CHILD_EC2_OVERWRITE=" + childOverwrite + " PROGRAM_DETERMINED=" + childProgramDetermined);
        bool passed = modifiers.SequenceEqual(after ?? new double[0]) && springCount == 1 &&
            k != null && k[0] == 725 && Math.Abs(childAngle - 12.5) < 1e-7 &&
            Math.Abs(childMass - 23) < 1e-7 && edge && parentLoadCount == childLoadCount &&
            parentPressureCount == childPressureCount && parentGroupCount == childGroupCount &&
            (parentGroups ?? new string[0]).OrderBy(x => x).SequenceEqual(
                (childGroups ?? new string[0]).OrderBy(x => x)) &&
            (overwriteCode != 0 || !childProgramDetermined);
        Console.WriteLine("PARENT_TO_CHILD_MATCH=" + passed);
        if (!passed) throw new InvalidOperationException("Child assignments differ from parent.");
    }
}
