using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Security;
using SAP2000v1;

class FastCallback : cPluginCallback, CSiAPIv1.cPluginCallback { public int ErrorFlag{get;private set;}=-1;public bool Finished{get;private set;}public void Finish(int value){ErrorFlag=value;Finished=true;} }
public class RunTest01Fast
{
    const string ParentFixtureGroup="SQM_PARENT_FIXTURE_GROUP";
    const string ParentFixtureLoad="SQM_PARENT_FIXTURE_LOAD";
    static string parentFixtureMaterial;
    static string parentFixtureDesignProcedure="Concrete Shell Design";
    static bool parentEdgeFixture;
    static double[][] parentFixtureEdge;
    static double[] FixtureXYZ(cSapModel model,string name){double x=0,y=0,z=0;RequireFixture(model.PointObj.GetCoordCartesian(name,ref x,ref y,ref z,"Global"),"point coordinate");return new[]{x,y,z};}
    static double FixtureDistance(double[] a,double[] b){return Math.Sqrt(Enumerable.Range(0,3).Sum(i=>(a[i]-b[i])*(a[i]-b[i])));}
    static double FixtureChildEdgeLength(cSapModel model,string area,int face)
    {
        int count=0;string[] names=null;RequireFixture(model.AreaObj.GetPoints(area,ref count,ref names),"child edge points");
        if(face<1||face>count)throw new InvalidDataException("Invalid child face "+face);
        double[] a=FixtureXYZ(model,names[face-1]),b=FixtureXYZ(model,names[face%count]);
        return FixtureDistance(a,b);
    }
    static void RequireFixture(int code,string action){if(code!=0)throw new InvalidOperationException("Parent-child fixture "+action+" returned "+code);}
    static void AssignParentFixture(cSapModel model,string parent)
    {
        RequireFixture(model.GroupDef.SetGroup(ParentFixtureGroup),"create group");
        RequireFixture(model.AreaObj.SetGroupAssign(parent,ParentFixtureGroup,false,eItemType.Objects),"area group");
        RequireFixture(model.LoadPatterns.Add(ParentFixtureLoad,eLoadPatternType.Other,0,true),"load pattern");
        RequireFixture(model.AreaObj.SetLoadUniform(parent,ParentFixtureLoad,3.75,6,true,"Global",eItemType.Objects),"uniform load");
        RequireFixture(model.AreaObj.SetLoadSurfacePressure(parent,ParentFixtureLoad,-1,1.5,"",true,eItemType.Objects),"pressure");
        double[] vector={0,0,0};
        RequireFixture(model.AreaObj.SetSpring(parent,1,725,1,"",-1,1,3,true,ref vector,0,true,"Local",eItemType.Objects),"area spring");
        double[] modifiers=null;
        RequireFixture(model.AreaObj.GetModifiers(parent,ref modifiers),"get modifiers");
        if(modifiers==null||modifiers.Length!=10)throw new InvalidOperationException("Parent modifiers missing.");
        modifiers[0]=.72;
        RequireFixture(model.AreaObj.SetModifiers(parent,ref modifiers,eItemType.Objects),"modifiers");
        RequireFixture(model.AreaObj.SetLocalAxes(parent,12.5,eItemType.Objects),"axes");
        RequireFixture(model.AreaObj.SetMass(parent,23,true,eItemType.Objects),"mass");
        RequireFixture(model.AreaObj.SetEdgeConstraint(parent,true,eItemType.Objects),"edge constraint");
        parentEdgeFixture=Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_EDGE_FIXTURE")=="1";
        if(parentEdgeFixture)
        {
            int vertices=0;string[] pointNames=null;
            RequireFixture(model.AreaObj.GetPoints(parent,ref vertices,ref pointNames),"source edge points");
            parentFixtureEdge=new[]{FixtureXYZ(model,pointNames[0]),FixtureXYZ(model,pointNames[1])};
            double[] edgeVector={0,0,0};
            RequireFixture(model.AreaObj.SetSpring(parent,1,455,1,"",1,1,3,true,
                ref edgeVector,0,false,"Local",eItemType.Objects),"edge spring");
            RequireFixture(model.AreaObj.SetLoadSurfacePressure(parent,ParentFixtureLoad,1,2.25,"",
                false,eItemType.Objects),"edge pressure");
        }
        int materialCount=0;string[] materialNames=null;
        RequireFixture(model.PropMaterial.GetNameList(ref materialCount,ref materialNames),"list materials");
        if(materialCount<1||materialNames==null)throw new InvalidOperationException("No fixture material.");
        parentFixtureMaterial=materialNames[0];
        RequireFixture(model.AreaObj.SetMaterialOverwrite(parent,parentFixtureMaterial,eItemType.Objects),"material overwrite");
        RequireFixture(model.AreaObj.SetMatTemp(parent,26.5,"",eItemType.Objects),"material temperature");
        Console.WriteLine("PARENT_FIXTURE_ASSIGNED="+parent);
    }
    static void VerifyParentFixture(cSapModel model,string[] areas)
    {
        if(areas.Length<1)throw new InvalidOperationException("No child shells for fixture verification.");
        double edgeSpringLength=0,edgePressureLength=0;
        foreach(string area in areas)
        {
            int groupCount=0,springCount=0,loadCount=0,pressureCount=0;
            string[] groups=null,objects=null,patterns=null,systems=null,links=null,springSystems=null,patternNames=null;
            int[] dirs=null,pressureFaces=null,springTypes=null,simpleTypes=null,springFaces=null,axisTypes=null,springDirs=null;
            double[] values=null,stiffness=null,vecX=null,vecY=null,vecZ=null,springAngles=null,modifiers=null;
            bool[] outward=null;
            RequireFixture(model.AreaObj.GetGroupAssign(area,ref groupCount,ref groups),"check groups "+area);
            RequireFixture(model.AreaObj.GetSpring(area,ref springCount,ref springTypes,ref stiffness,
                ref simpleTypes,ref links,ref springFaces,ref axisTypes,ref springDirs,ref outward,
                ref vecX,ref vecY,ref vecZ,ref springSystems,ref springAngles),"check springs "+area);
            RequireFixture(model.AreaObj.GetModifiers(area,ref modifiers),"check modifiers "+area);
            RequireFixture(model.AreaObj.GetLoadUniform(area,ref loadCount,ref objects,ref patterns,
                ref systems,ref dirs,ref values,eItemType.Objects),"check uniform load "+area);
            double uniformValue=values!=null&&values.Length>0?values[0]:double.NaN;
            string uniformPattern=patterns!=null&&patterns.Length>0?patterns[0]:"";
            objects=null;patterns=null;values=null;
            RequireFixture(model.AreaObj.GetLoadSurfacePressure(area,ref pressureCount,ref objects,
                ref patterns,ref pressureFaces,ref values,ref patternNames,eItemType.Objects),"check pressure "+area);
            bool bottomPressure=false;
            for(int i=0;i<pressureCount;i++)
            {
                if(pressureFaces[i]==-1&&Math.Abs(values[i]-1.5)<1e-7&&patterns[i]==ParentFixtureLoad)
                    bottomPressure=true;
                if(pressureFaces[i]>0&&Math.Abs(values[i]-2.25)<1e-7&&patterns[i]==ParentFixtureLoad)
                    edgePressureLength+=FixtureChildEdgeLength(model,area,pressureFaces[i]);
            }
            for(int i=0;i<springCount;i++)if(springFaces[i]>0&&Math.Abs(stiffness[i]-455)<1e-7)
                edgeSpringLength+=FixtureChildEdgeLength(model,area,springFaces[i]);
            bool bottomSpring=Enumerable.Range(0,springCount).Any(i=>springFaces[i]==-1&&Math.Abs(stiffness[i]-725)<1e-7);
            double angle=0,mass=0;bool advanced=false,edge=false;
            RequireFixture(model.AreaObj.GetLocalAxes(area,ref angle,ref advanced),"check axes "+area);
            RequireFixture(model.AreaObj.GetMass(area,ref mass),"check mass "+area);
            RequireFixture(model.AreaObj.GetEdgeConstraint(area,ref edge),"check edge "+area);
            string material=null,temperaturePattern=null;
            double temperature=0;
            RequireFixture(model.AreaObj.GetMaterialOverwrite(area,ref material),"check material overwrite "+area);
            RequireFixture(model.AreaObj.GetMatTemp(area,ref temperature,ref temperaturePattern),"check material temperature "+area);
            if(groups==null||!groups.Contains(ParentFixtureGroup)||!bottomSpring||modifiers==null||Math.Abs(modifiers[0]-.72)>1e-7||
                loadCount!=1||!bottomPressure||Math.Abs(uniformValue-3.75)>1e-7||uniformPattern!=ParentFixtureLoad||
                Math.Abs(temperature-26.5)>1e-7||
                material!=parentFixtureMaterial||Math.Abs(angle-12.5)>1e-7||Math.Abs(mass-23)>1e-7||!edge)
                throw new InvalidDataException("Parent-child assignments changed on "+area);
        }
        if(parentEdgeFixture&&!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_UNDO"))&&
            Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_UNDO")=="1")
        {
            // The restored parent has just one edge spring and one edge pressure.
            if(areas.Length==1) { Console.WriteLine("EDGE_FIXTURE_PARENT_RESTORED=PASS"); return; }
        }
        if(parentEdgeFixture)
        {
            double original=FixtureDistance(parentFixtureEdge[0],parentFixtureEdge[1]);
            if(Math.Abs(edgeSpringLength-original)>1e-5*original||
                Math.Abs(edgePressureLength-original)>1e-5*original)
                throw new InvalidDataException("Parent edge assignments do not cover the entire mesh boundary: "+
                    edgeSpringLength+" / "+edgePressureLength+" expected "+original);
            Console.WriteLine("PARENT_EDGE_ASSIGNMENTS_PASS length="+original+
                " spring="+edgeSpringLength+" pressure="+edgePressureLength);
        }
        Console.WriteLine("PARENT_CHILD_ASSIGNMENTS_PASS shells="+areas.Length);
    }
    static void AssignDesignFixture(cSapModel model,string parent)
    {
        parentFixtureDesignProcedure=Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_DESIGN_VALUE")??"Concrete Shell Design";
        int version=0,records=0;string[] fields=null,data=null;
        RequireFixture(model.DatabaseTables.GetTableForEditingArray("Area Design Procedures","",
            ref version,ref fields,ref records,ref data),"read design procedures");
        int area=Array.IndexOf(fields,"Area"),design=Array.IndexOf(fields,"DesignProc");
        if(area<0||design<0)throw new InvalidDataException("Design procedure table lacks fields.");
        string[] updated=new string[(records+1)*fields.Length];
        if(data!=null)Array.Copy(data,updated,Math.Min(data.Length,records*fields.Length));
        int offset=records*fields.Length;
        updated[offset+area]=parent;
        updated[offset+design]=parentFixtureDesignProcedure;
        RequireFixture(model.DatabaseTables.SetTableForEditingArray("Area Design Procedures",ref version,
            ref fields,records+1,ref updated),"stage design fixture");
        int fatal=0,errors=0,warnings=0,information=0;string log="";
        RequireFixture(model.DatabaseTables.ApplyEditedTables(true,ref fatal,ref errors,
            ref warnings,ref information,ref log),"apply design fixture");
        if(fatal!=0||errors!=0||warnings!=0)throw new InvalidDataException("Design fixture import: "+log);
        int afterVersion=0,afterRecords=0;string[] afterFields=null,afterData=null;
        RequireFixture(model.DatabaseTables.GetTableForEditingArray("Area Design Procedures","",
            ref afterVersion,ref afterFields,ref afterRecords,ref afterData),"check parent design procedure");
        Console.WriteLine("PARENT_DESIGN_TABLE rows="+afterRecords+" values="+string.Join("|",Enumerable.Range(0,afterRecords).Select(i=>afterData[i*afterFields.Length+area]+":"+afterData[i*afterFields.Length+design])));
        if(!Enumerable.Range(0,afterRecords).Any(i=>afterData[i*afterFields.Length+area]==parent&&
            afterData[i*afterFields.Length+design]==parentFixtureDesignProcedure))
            throw new InvalidOperationException("SAP2000 did not retain the requested design procedure on the parent fixture.");
        Console.WriteLine("PARENT_DESIGN_PROCEDURE_ASSIGNED="+parent+" VALUE="+parentFixtureDesignProcedure);
    }
    static void VerifyDesignFixture(cSapModel model,string[] areas)
    {
        int version=0,records=0;string[] fields=null,data=null;
        RequireFixture(model.DatabaseTables.GetTableForEditingArray("Area Design Procedures","",
            ref version,ref fields,ref records,ref data),"read child design procedures");
        int area=Array.IndexOf(fields,"Area"),design=Array.IndexOf(fields,"DesignProc");
        Console.WriteLine("CHILD_DESIGN_TABLE rows="+records+" values="+string.Join("|",Enumerable.Range(0,records).Select(i=>data[i*fields.Length+area]+":"+data[i*fields.Length+design])));
        foreach(string child in areas)
        {
            if(!Enumerable.Range(0,records).Any(i=>data[i*fields.Length+area]==child&&
                data[i*fields.Length+design]==parentFixtureDesignProcedure))
                throw new InvalidDataException("Design procedure missing on child "+child);
        }
        Console.WriteLine("PARENT_CHILD_DESIGN_PROCEDURE_PASS shells="+areas.Length);
    }
    static object[] PluginArguments(object plugin,cSapModel model,FastCallback callback,int ownedPid)
    {
        Type requested=plugin.GetType().GetMethod("Main").GetParameters()[0].ParameterType.GetElementType();
        if(requested!=typeof(CSiAPIv1.cSapModel))return new object[]{model,callback};
        if(ownedPid<=0)throw new InvalidOperationException("xAPI plugin test requires an isolated SAP process.");
        CSiAPIv1.cHelper helper=new CSiAPIv1.Helper();
        CSiAPIv1.cOAPI sap=helper.GetObjectProcess("CSI.SAP2000.API.SapObject",ownedPid);
        if(sap==null||sap.SapModel==null||
            !string.Equals(sap.SapModel.GetModelFilename(true),model.GetModelFilename(true),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("xAPI test attached to another SAP model.");
        return new object[]{sap.SapModel,(CSiAPIv1.cPluginCallback)callback};
    }
    static cOAPI persistentSap;
    static int persistentSapPid;
    static string atomicId;
    static string activePluginPath;
    static bool test11;
    static bool test11Opening;
    static bool test28CircularFrames;
    [StructLayout(LayoutKind.Sequential)]struct RECT{public int Left,Top,Right,Bottom;}
    delegate bool EnumProc(IntPtr h,IntPtr p);[DllImport("user32.dll")]static extern bool EnumWindows(EnumProc c,IntPtr p);[DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr h,EnumProc c,IntPtr p);[DllImport("user32.dll")]static extern int GetWindowText(IntPtr h,StringBuilder t,int n);[DllImport("user32.dll")]static extern int GetClassName(IntPtr h,StringBuilder t,int n);[DllImport("user32.dll")]static extern bool SetWindowText(IntPtr h,string t);[DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);[DllImport("user32.dll")]static extern bool PostMessage(IntPtr h,uint m,IntPtr w,IntPtr l);[DllImport("user32.dll")]static extern bool IsWindowEnabled(IntPtr h);[DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr h,out RECT r);[DllImport("user32.dll")]static extern bool SetForegroundWindow(IntPtr h);[DllImport("user32.dll")]static extern bool ShowWindow(IntPtr h,int command);[DllImport("user32.dll")]static extern bool SetCursorPos(int x,int y);[DllImport("user32.dll")]static extern void mouse_event(uint flags,uint dx,uint dy,uint data,UIntPtr extra);const uint Click=0xF5,SelectString=0x14D,SetCurSel=0x14E,TabGetItemRect=0x130A,MouseDown=0x0002,MouseUp=0x0004;static string failure;static bool requestUndo;static bool useFast=true;static bool verifyTest02;static bool verifyAllQuads;static bool atomicExample;static bool circularHoles;static bool enclosedHoles;static bool generalSymmetry;static bool test06;static bool test07;static bool test08;static bool test09;static int test07Plane;static int symmetryAxisIndex=-1;static double symmetryX=.15;static double test06HoleRadius=.1;static string holeDiameter="0.04";static string holeGroupName="FORO";static string openingGroupName="apertura";static string meshSize="0.01";static string errorPath;static string screenshotPath;static string screenshotView;static string workflowProfile="General Purpose";static string[] selectedAreas={"1","2","3"};static string[] selectedPoints={"11","12","13","14"};static string[] selectedFrames=new string[0];
    static string Text(IntPtr h){var b=new StringBuilder(1024);GetWindowText(h,b,b.Capacity);return b.ToString();}
    static string ClassName(IntPtr h){var b=new StringBuilder(128);GetClassName(h,b,b.Capacity);return b.ToString();}
    static List<IntPtr> Windows(string title){var r=new List<IntPtr>();EnumWindows((h,p)=>{if(Text(h)==title)r.Add(h);return true;},IntPtr.Zero);return r;}
    static List<IntPtr> AllWindows(){var r=new List<IntPtr>();EnumWindows((h,p)=>{r.Add(h);return true;},IntPtr.Zero);return r;}
    static IntPtr Child(IntPtr parent,string text){IntPtr r=IntPtr.Zero;EnumChildWindows(parent,(h,p)=>{if(Text(h)==text){r=h;return false;}return true;},IntPtr.Zero);return r;}
    static bool SelectCombo(IntPtr combo,string value){IntPtr text=Marshal.StringToHGlobalUni(value);try{return SendMessage(combo,SelectString,new IntPtr(-1),text).ToInt64()>=0;}finally{Marshal.FreeHGlobal(text);}}
    static bool SelectProfileTab(IntPtr parent,string profile)
    {
        string[] profiles={"Reinforced Concrete","Steel Structures","General Purpose"};int index=Array.IndexOf(profiles,profile);if(index<0)return false;IntPtr tab=IntPtr.Zero;EnumChildWindows(parent,(h,p)=>{if(ClassName(h).IndexOf("SysTabControl32",StringComparison.OrdinalIgnoreCase)>=0){tab=h;return false;}return true;},IntPtr.Zero);if(tab==IntPtr.Zero)return false;IntPtr memory=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(RECT)));try{Marshal.StructureToPtr(new RECT(),memory,false);if(SendMessage(tab,TabGetItemRect,new IntPtr(index),memory)==IntPtr.Zero)return false;RECT item=(RECT)Marshal.PtrToStructure(memory,typeof(RECT));RECT window;if(!GetWindowRect(tab,out window))return false;MouseClick(window.Left+(item.Left+item.Right)/2,window.Top+(item.Top+item.Bottom)/2);Thread.Sleep(150);return true;}finally{Marshal.FreeHGlobal(memory);}
    }
    static string ChildTexts(IntPtr parent){var r=new List<string>();EnumChildWindows(parent,(h,p)=>{string t=Text(h);if(!string.IsNullOrWhiteSpace(t))r.Add(t);return true;},IntPtr.Zero);return string.Join(" | ",r);}
    static void Automate()
    {
        const string title="SAP2000 Quad Transition Mesh 1.0.87";IntPtr main=IntPtr.Zero;for(int i=0;i<600&&main==IntPtr.Zero;i++){Thread.Sleep(100);foreach(IntPtr w in Windows(title))if(Child(w,"Generate")!=IntPtr.Zero){main=w;break;}}
        if(main!=IntPtr.Zero){bool selected=workflowProfile=="General Purpose";for(int attempt=0;attempt<3&&!selected;attempt++){if(!SelectProfileTab(main,workflowProfile))break;selected=ChildTexts(main).Contains(workflowProfile);}if(!selected){failure="profile: "+workflowProfile;return;}}
        if(main!=IntPtr.Zero)Console.WriteLine("dialog-options="+ChildTexts(main));
        if(main==IntPtr.Zero){failure="window";return;}IntPtr size=Child(main,"0.1");if(size==IntPtr.Zero){failure="size";return;}SetWindowText(size,meshSize);if(generalSymmetry){IntPtr option=Child(main,"Enforce exact symmetry");if(option==IntPtr.Zero||!IsWindowEnabled(option)){failure="symmetry option: "+ChildTexts(main);return;}SendMessage(option,Click,IntPtr.Zero,IntPtr.Zero);if(symmetryAxisIndex>=0){IntPtr axisCombo=IntPtr.Zero;EnumChildWindows(main,(h,p)=>{if(Text(h).StartsWith("Axis "))axisCombo=h;return axisCombo==IntPtr.Zero;},IntPtr.Zero);if(axisCombo==IntPtr.Zero||SendMessage(axisCombo,SetCurSel,new IntPtr(symmetryAxisIndex),IntPtr.Zero).ToInt64()<0){failure="symmetry axis";return;}}}if(enclosedHoles){IntPtr option=Child(main,"Generate holes enclosed by selected frames or shell edges");if(option==IntPtr.Zero||!IsWindowEnabled(option)){failure="opening option: "+ChildTexts(main);return;}SendMessage(option,Click,IntPtr.Zero,IntPtr.Zero);Thread.Sleep(100);bool selected=false;EnumChildWindows(main,(h,p)=>{if(ClassName(h).IndexOf("COMBOBOX",StringComparison.OrdinalIgnoreCase)>=0&&IsWindowEnabled(h)&&!selected&&SelectCombo(h,openingGroupName))selected=true;return !selected;},IntPtr.Zero);if(!selected){failure="opening group: "+ChildTexts(main);SendMessage(Child(main,"Cancel"),Click,IntPtr.Zero,IntPtr.Zero);return;}}if(circularHoles){IntPtr option=Child(main,"Generate circular O-grid holes");IntPtr diameter=Child(main,"0.05");if(option==IntPtr.Zero||diameter==IntPtr.Zero){failure="circular-hole controls: "+ChildTexts(main);return;}SendMessage(option,Click,IntPtr.Zero,IntPtr.Zero);Thread.Sleep(100);bool selected=false;EnumChildWindows(main,(h,p)=>{if(ClassName(h).IndexOf("COMBOBOX",StringComparison.OrdinalIgnoreCase)>=0&&IsWindowEnabled(h)&&!selected&&SelectCombo(h,holeGroupName))selected=true;return !selected;},IntPtr.Zero);if(!selected){failure="hole group";return;}SetWindowText(diameter,holeDiameter);}if(useFast){IntPtr fast=Child(main,"FAST - bulk insertion through SAP2000 tables");if(fast==IntPtr.Zero){failure="fast";return;}SendMessage(fast,Click,IntPtr.Zero,IntPtr.Zero);}Console.WriteLine("stage=automation-generate");SendMessage(Child(main,"Generate"),Click,IntPtr.Zero,IntPtr.Zero);
        for(int i=0;i<12000;i++){Thread.Sleep(100);if(File.Exists(errorPath)){failure=File.ReadAllText(errorPath);return;}foreach(IntPtr w in Windows(title)){IntPtr ok=Child(w,"OK");if(ok!=IntPtr.Zero){failure=Text(w)+" | "+ChildTexts(w);SendMessage(ok,Click,IntPtr.Zero,IntPtr.Zero);Thread.Sleep(300);foreach(IntPtr x in Windows(title)){IntPtr cancel=Child(x,"Cancel");if(cancel!=IntPtr.Zero)SendMessage(cancel,Click,IntPtr.Zero,IntPtr.Zero);}return;}IntPtr apply=Child(w,"Apply");bool completed=ChildTexts(w).Contains("Mesh completed. Area elements created:");if(completed){if(!requestUndo){SendMessage(apply,Click,IntPtr.Zero,IntPtr.Zero);return;}IntPtr undo=Child(w,"Undo mesh");if(undo!=IntPtr.Zero&&IsWindowEnabled(undo)){Console.WriteLine("stage=undo-click");if(!PostMessage(undo,Click,IntPtr.Zero,IntPtr.Zero)){failure="PostMessage undo failed";return;}for(int j=0;j<6000;j++){Thread.Sleep(100);if(File.Exists(errorPath)){failure=File.ReadAllText(errorPath);return;}if(Child(w,"Original objects and selection restored. Ready to generate again.")!=IntPtr.Zero){Console.WriteLine("stage=undo-complete");PostMessage(Child(w,"Cancel"),Click,IntPtr.Zero,IntPtr.Zero);return;}}failure="undo timeout";return;}}}}failure="timeout";
    }
    static void EnableRegularMesh()
    {
        const string title="SAP2000 Quad Transition Mesh 1.0.87";
        for(int attempt=0;attempt<300;attempt++)
        {
            Thread.Sleep(20);
            foreach(IntPtr window in Windows(title))
            {
                IntPtr option=Child(window,"Prefer regular meshes aligned with principal orthogonal axes");
                if(option!=IntPtr.Zero&&IsWindowEnabled(option)){SendMessage(option,Click,IntPtr.Zero,IntPtr.Zero);return;}
            }
        }
        failure="regular mesh option";
    }
    static void EnableStressRefinement()
    {
        const string title="SAP2000 Quad Transition Mesh 1.0.87";
        for(int attempt=0;attempt<300;attempt++)
        {
            Thread.Sleep(20);
            foreach(IntPtr window in Windows(title))
            {
                IntPtr regular=Child(window,"Prefer regular meshes aligned with principal orthogonal axes");
                IntPtr refinement=Child(window,"Refine near stress concentrations and geometry changes");
                if(regular!=IntPtr.Zero&&refinement!=IntPtr.Zero&&IsWindowEnabled(regular)&&IsWindowEnabled(refinement))
                {
                    SendMessage(regular,Click,IntPtr.Zero,IntPtr.Zero);Thread.Sleep(50);
                    if(IsWindowEnabled(refinement)){failure="refinement enabled while regular is selected";return;}
                    SendMessage(regular,Click,IntPtr.Zero,IntPtr.Zero);Thread.Sleep(50);
                    if(!IsWindowEnabled(refinement)){failure="refinement not re-enabled after clearing regular";return;}
                    SendMessage(refinement,Click,IntPtr.Zero,IntPtr.Zero);Thread.Sleep(50);
                    Console.WriteLine("mutual-option-state regular-enabled="+IsWindowEnabled(regular));
                    if(IsWindowEnabled(regular))
                    {failure="regular/refinement mutual exclusion";return;}
                    return;
                }
            }
        }
        failure="stress refinement option";
    }
    static void VerifyInstalledUi()
    {
        const string title="SAP2000 Quad Transition Mesh 1.0.87";
        for(int attempt=0;attempt<600;attempt++)
        {
            Thread.Sleep(100);
            foreach(IntPtr window in Windows(title))
            {
                IntPtr language=Child(window,"English");
                if(language==IntPtr.Zero||Child(window,"Prefer regular meshes aligned with principal orthogonal axes")==IntPtr.Zero||
                    Child(window,"Version 1.0.87 beta")==IntPtr.Zero){failure="installed UI controls: "+ChildTexts(window);return;}
                if(!SelectCombo(language,"Italiano")){failure="Italian language option";return;}Thread.Sleep(100);
                IntPtr cancel=Child(window,"Cancel");if(cancel==IntPtr.Zero)cancel=Child(window,"Annulla");if(cancel==IntPtr.Zero){failure="installed UI cancel button";return;}
                Console.WriteLine("installed-plugin-ui=PASS title="+title+" languages=English/Italiano");
                SendMessage(cancel,Click,IntPtr.Zero,IntPtr.Zero);return;
            }
        }
        failure="installed plugin window";
    }
    public static void VerifyInstalledPlugin(string source,string pluginPath)
    {
        StartPersistentSap();failure=null;cSapModel model=persistentSap.SapModel;if(model.File.OpenFile(source)!=0)throw new Exception("open");
        model.SelectObj.ClearSelection();model.AreaObj.SetSelected("2",true,eItemType.Objects);
        var thread=new Thread(VerifyInstalledUi);thread.SetApartmentState(ApartmentState.STA);thread.Start();
        object plugin=Assembly.LoadFrom(pluginPath).CreateInstance("SapQuadTransitionPlugin.cPlugin");cPluginCallback callback=new FastCallback();object[] parameters={model,callback};
        plugin.GetType().GetMethod("Main").Invoke(plugin,parameters);if(!thread.Join(TimeSpan.FromSeconds(90)))throw new Exception("installed UI verification timeout");
        if(failure!=null)throw new Exception(failure);if(!callback.Finished||callback.ErrorFlag!=0)throw new Exception("callback "+callback.ErrorFlag);
    }
    public static void Run(string source,string output,string pluginPath){meshSize="0.01";useFast=true;RunCore(source,output,pluginPath,false);}
    public static void RunUndo(string source,string output,string pluginPath){meshSize="0.01";useFast=true;RunCore(source,output,pluginPath,true);}
    public static void RunUndoSize(string source,string output,string pluginPath,string size){meshSize=size;useFast=true;RunCore(source,output,pluginPath,true);}
    public static void RunStandard(string source,string output,string pluginPath){meshSize="0.01";useFast=false;RunCore(source,output,pluginPath,false);}
    public static void RunTest02(string source,string output,string pluginPath){meshSize="0.25";useFast=false;verifyTest02=true;selectedAreas=new[]{"1","2"};selectedPoints=new string[0];RunCore(source,output,pluginPath,false);}
    public static void RunTest02Fast(string source,string output,string pluginPath){meshSize="0.25";useFast=true;verifyTest02=true;selectedAreas=new[]{"1","2"};selectedPoints=new string[0];RunCore(source,output,pluginPath,false);}
    public static void RunTest03Fast(string source,string output,string pluginPath){meshSize="0.25";useFast=true;verifyAllQuads=true;selectedAreas=new[]{"1","2","3","6","7","8"};selectedPoints=new string[0];RunCore(source,output,pluginPath,false);}
    public static void RunTest05(string source,string output,string pluginPath){meshSize="0.03";useFast=true;circularHoles=true;selectedAreas=new[]{"1"};selectedPoints=new string[0];RunCore(source,output,pluginPath,false);}
    public static void RunSymmetricTest05(string source,string output,string pluginPath){meshSize="0.03";useFast=true;generalSymmetry=true;selectedAreas=new[]{"1"};selectedPoints=new string[0];RunCore(source,output,pluginPath,false);}
    public static void RunSymmetricHolesTest05(string source,string output,string pluginPath){meshSize="0.03";useFast=true;generalSymmetry=true;circularHoles=true;selectedAreas=new[]{"1"};selectedPoints=new string[0];RunCore(source,output,pluginPath,false);}
    public static void RunSymmetricAxisHoleTest06(string source,string output,string pluginPath){meshSize="0.1";holeDiameter="0.05";test06HoleRadius=.025;symmetryX=0;useFast=true;generalSymmetry=true;circularHoles=true;test06=true;selectedAreas=new[]{"2"};selectedPoints=new string[0];RunCore(source,output,pluginPath,false);}
    public static void RunSymmetricHolesTest07Area1(string source,string output,string pluginPath){meshSize="0.03";holeDiameter="0.03";holeGroupName="foro";symmetryAxisIndex=2;symmetryX=.15;useFast=true;generalSymmetry=true;circularHoles=true;test07=true;test07Plane=1;selectedAreas=new[]{"1"};selectedPoints=new string[0];RunCore(source,output,pluginPath,false);}
    public static void RunSymmetricHolesTest07Area2(string source,string output,string pluginPath){meshSize="0.03";holeDiameter="0.03";holeGroupName="foro";symmetryAxisIndex=2;symmetryX=.15;useFast=true;generalSymmetry=true;circularHoles=true;test07=true;test07Plane=2;selectedAreas=new[]{"2"};selectedPoints=new string[0];RunCore(source,output,pluginPath,false);}
    public static void RunHolesTest08(string source,string output,string pluginPath){meshSize="0.01";holeDiameter="0.03";holeGroupName="foro";useFast=true;circularHoles=true;test08=true;selectedAreas=new[]{"1","2","3","4"};selectedPoints=new string[0];RunCore(source,output,pluginPath,false);}
    public static void RunSymmetricHolesTest08(string source,string output,string pluginPath){meshSize="0.01";holeDiameter="0.03";holeGroupName="foro";symmetryAxisIndex=0;symmetryX=0;useFast=true;generalSymmetry=true;circularHoles=true;test08=true;selectedAreas=new[]{"1","2","3","4"};selectedPoints=new string[0];RunCore(source,output,pluginPath,false);}
    public static void RunTest09(string source,string output,string pluginPath){meshSize="0.08";useFast=true;enclosedHoles=true;openingGroupName="apertura";test09=true;selectedAreas=new[]{"1","2","3","4"};selectedPoints=new string[0];selectedFrames=new[]{"1","2","3","4"};RunCore(source,output,pluginPath,false);}
    public static void RunTest28CircularFrames(string source,string output,string pluginPath)
    {
        meshSize="0.08";useFast=true;atomicExample=false;circularHoles=true;enclosedHoles=false;generalSymmetry=false;
        test28CircularFrames=true;holeGroupName="foro";holeDiameter="0.20";workflowProfile="General Purpose";
        selectedAreas=new[]{"2","3","4","5"};selectedPoints=new string[0];selectedFrames=new[]{"1","2","3","4"};
        RunCore(source,output,pluginPath,false);
    }
    public static void RunTest10Regular(string source,string output,string pluginPath,bool split)
    {
        meshSize="0.1";useFast=true;generalSymmetry=true;symmetryAxisIndex=-1;symmetryX=0;enclosedHoles=true;
        openingGroupName="ALL";selectedAreas=split?new[]{"1","2","3","4"}:new[]{"1"};selectedPoints=new string[0];selectedFrames=new[]{"6","7","8","9"};
        var regularThread=new Thread(EnableRegularMesh);regularThread.SetApartmentState(ApartmentState.STA);regularThread.Start();RunCore(source,output,pluginPath,false);regularThread.Join();
    }
    public static void RunTest10Stress(string source,string output,string pluginPath)
    {
        meshSize="0.1";useFast=true;generalSymmetry=true;symmetryAxisIndex=-1;symmetryX=0;enclosedHoles=true;
        openingGroupName="ALL";selectedAreas=new[]{"1"};selectedPoints=new string[0];selectedFrames=new[]{"6","7","8","9"};
        var stressThread=new Thread(EnableStressRefinement);stressThread.SetApartmentState(ApartmentState.STA);stressThread.Start();RunCore(source,output,pluginPath,false);stressThread.Join();
    }
    public static void RunTest11(string source,string output,string pluginPath,bool opening,bool symmetry)
    {
        meshSize="0.1";useFast=true;test11=true;test11Opening=opening;generalSymmetry=symmetry;symmetryAxisIndex=-1;symmetryX=0;
        circularHoles=true;holeGroupName="bulloni";holeDiameter="0.05";enclosedHoles=opening;openingGroupName="ALL";
        selectedAreas=new[]{"1"};selectedPoints=new string[0];selectedFrames=new[]{"6","7","8","9"};RunCore(source,output,pluginPath,false);
    }
    public static void RunAtomicExample(string source,string output,string pluginPath,string[] areas,string[] points,string[] frames,string size,string circularGroup,string diameter,string openingGroup,bool symmetry,bool fast,string profile,string view,string screenshot)
    {
        atomicId=Path.GetFileNameWithoutExtension(source);
        failure=null;useFast=fast;verifyTest02=verifyAllQuads=test06=test07=test08=test09=false;atomicExample=true;circularHoles=!string.IsNullOrEmpty(circularGroup);enclosedHoles=!string.IsNullOrEmpty(openingGroup);generalSymmetry=symmetry;symmetryAxisIndex=-1;meshSize=size;holeGroupName=circularGroup;holeDiameter=diameter;openingGroupName=openingGroup;workflowProfile=profile;selectedAreas=areas;selectedPoints=points??new string[0];selectedFrames=frames??new string[0];screenshotView=view;screenshotPath=screenshot;RunCore(source,output,pluginPath,Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_UNDO")=="1");
    }
    public static void StartPersistentSap()
    {
        if(persistentSap!=null&&persistentSap.SapModel!=null)return;cHelper helper=new Helper();
        foreach(Process process in Process.GetProcessesByName("SAP2000").OrderByDescending(item=>item.WorkingSet64))
            try{persistentSap=helper.GetObjectProcess("CSI.SAP2000.API.SapObject",process.Id);if(persistentSap!=null&&persistentSap.SapModel!=null)return;}catch{persistentSap=null;}
        persistentSap=helper.CreateObject(@"C:\Program Files\Computers and Structures\SAP2000 27\SAP2000.exe");
        if(persistentSap==null||persistentSap.ApplicationStart()!=0||persistentSap.SapModel==null){persistentSap=null;throw new Exception("SAP2000 could not be started for the batch.");}
    }
    public static void StartIsolatedPersistentSap()
    {
        if(persistentSap!=null)throw new InvalidOperationException("This worker already owns a SAP2000 instance.");
        using(var startupMutex=new Mutex(false,"Local\\SAP2000QuadMesherRegressionStartup"))
        {
            startupMutex.WaitOne();
            try
            {
                cHelper helper=new Helper();
                var existing=new HashSet<int>(Process.GetProcessesByName("SAP2000").Select(process=>process.Id));
                persistentSap=helper.CreateObject(@"C:\Program Files\Computers and Structures\SAP2000 27\SAP2000.exe");
                if(persistentSap==null||persistentSap.ApplicationStart()!=0||persistentSap.SapModel==null)
                {
                    if(persistentSap!=null)try{persistentSap.ApplicationExit(false);}catch{}
                    persistentSap=null;
                    throw new Exception("SAP2000 could not be started for the isolated regression worker.");
                }
                int[] owned=null;
                var identification=Stopwatch.StartNew();
                do
                {
                    owned=Process.GetProcessesByName("SAP2000").Select(process=>process.Id)
                        .Where(id=>!existing.Contains(id)).ToArray();
                    if(owned.Length==1)break;
                    Thread.Sleep(250);
                }while(identification.Elapsed<TimeSpan.FromSeconds(12));
                if(owned.Length!=1)
                {
                    try{persistentSap.ApplicationExit(false);}catch{}
                    persistentSap=null;
                    throw new InvalidOperationException("Cannot identify the isolated SAP2000 process for xAPI: " +
                        "existing="+string.Join(",",existing)+" new="+string.Join(",",owned));
                }
                persistentSapPid=owned[0];
            }
            finally { startupMutex.ReleaseMutex(); }
        }
    }
    public static void AttachPersistentSap(int processId)
    {
        if(persistentSap!=null)throw new InvalidOperationException("This worker already holds a SAP2000 instance.");
        using(Process process=Process.GetProcessById(processId))
        {
            if(!process.ProcessName.Equals("SAP2000",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The supplied process is not SAP2000.");
            cHelper helper=new Helper();
            cOAPI attached=helper.GetObjectProcess("CSI.SAP2000.API.SapObject",processId);
            if(attached==null||attached.SapModel==null||process.HasExited)
                throw new InvalidOperationException("The supplied SAP2000 process is not attachable.");
            persistentSap=attached;
            persistentSapPid=processId;
        }
    }
    public static void DetachPersistentSap()
    {
        // The application belongs to the user; do not call ApplicationExit.
        persistentSap=null;
        persistentSapPid=0;
    }
    public static void StopIsolatedPersistentSap()
    {
        cOAPI sap=persistentSap;persistentSap=null;persistentSapPid=0;
        if(sap!=null)try{sap.ApplicationExit(false);}catch{}
    }
    public static void RunAtomicExampleWithTimeout(string source,string output,string pluginPath,string[] areas,string[] points,string[] frames,string size,string circularGroup,string diameter,string openingGroup,bool symmetry,bool fast,string profile,string view,string screenshot,int timeoutSeconds)
    {
        Exception failure=null;var worker=new Thread(()=>{try{RunAtomicExample(source,output,pluginPath,areas,points,frames,size,circularGroup,diameter,openingGroup,symmetry,fast,profile,view,screenshot);}catch(Exception exception){failure=exception;}});worker.SetApartmentState(ApartmentState.STA);worker.Start();if(!worker.Join(TimeSpan.FromSeconds(timeoutSeconds))){persistentSap=null;throw new TimeoutException("Atomic example exceeded the timeout of "+timeoutSeconds+" seconds; the batch was stopped with SAP2000 left open.");}if(failure!=null)throw failure;
    }
    static void VerifyRestoredSelection(cSapModel model){int count=0;int[] types=null;string[] names=null;if(model.SelectObj.GetSelected(ref count,ref types,ref names)!=0)throw new Exception("selection read");var actual=new HashSet<string>();for(int i=0;i<count;i++)actual.Add(types[i]+":"+names[i]);var expected=atomicExample?new HashSet<string>(selectedAreas.Select(name=>"5:"+name).Concat(selectedPoints.Select(name=>"1:"+name)).Concat(selectedFrames.Select(name=>"2:"+name))):new HashSet<string>(new[]{"5:1","5:2","5:3","1:11","1:12","1:13","1:14"});if(!actual.SetEquals(expected))throw new Exception("selection restore: expected="+string.Join(",",expected)+" actual="+string.Join(",",actual));Console.WriteLine("selection-restored="+count);}
    static void VerifyTest02Conformity(cSapModel model)
    {
        int areaCount=0;string[] areas=null;if(model.AreaObj.GetNameList(ref areaCount,ref areas)!=0)throw new Exception("area list");
        var horizontal=new HashSet<string>();var vertical=new HashSet<string>();int quads=0;
        foreach(string area in areas)
        {
            int count=0;string[] points=null;if(model.AreaObj.GetPoints(area,ref count,ref points)!=0)throw new Exception("area points "+area);
            if(count!=4)throw new Exception("non-quadrilateral area "+area+" points="+count);
            quads++;
            var xyz=new double[count][];
            for(int i=0;i<count;i++){double x=0,y=0,z=0;if(model.PointObj.GetCoordCartesian(points[i],ref x,ref y,ref z,"Global")!=0)throw new Exception("point "+points[i]);xyz[i]=new[]{x,y,z};}
            bool isHorizontal=true,isVertical=true;for(int i=0;i<count;i++){isHorizontal&=Math.Abs(xyz[i][2])<1e-6;isVertical&=Math.Abs(xyz[i][0]-2)<1e-6;}
            for(int i=0;i<count;i++)
            {
                int j=(i+1)%count;
                bool onIntersection=Math.Abs(xyz[i][0]-2)<1e-6&&Math.Abs(xyz[j][0]-2)<1e-6&&Math.Abs(xyz[i][2])<1e-6&&Math.Abs(xyz[j][2])<1e-6&&
                    Math.Min(xyz[i][1],xyz[j][1])>=1-1e-6&&Math.Max(xyz[i][1],xyz[j][1])<=2+1e-6;
                if(!onIntersection)continue;
                string key=string.CompareOrdinal(points[i],points[j])<0?points[i]+":"+points[j]:points[j]+":"+points[i];
                if(isHorizontal)horizontal.Add(key);if(isVertical)vertical.Add(key);
            }
        }
        if(horizontal.Count==0||!horizontal.SetEquals(vertical))throw new Exception("nonconforming TEST02 edge: horizontal="+string.Join(",",horizontal)+" vertical="+string.Join(",",vertical));
        Console.WriteLine("test02-conforming-segments="+horizontal.Count+" quads="+quads);
    }
    static void VerifyAllAreasAreQuads(cSapModel model)
    {
        int areaCount=0;string[] areas=null;if(model.AreaObj.GetNameList(ref areaCount,ref areas)!=0)throw new Exception("area list");
        var elements=new List<Tuple<string[],double[][]>>();var horizontalEdges=new HashSet<string>();
        foreach(string area in areas)
        {
            int count=0;string[] points=null;if(model.AreaObj.GetPoints(area,ref count,ref points)!=0||count!=4)throw new Exception("non-quadrilateral area "+area+" points="+count);
            var xyz=new double[4][];for(int i=0;i<4;i++){double x=0,y=0,z=0;if(model.PointObj.GetCoordCartesian(points[i],ref x,ref y,ref z,"Global")!=0)throw new Exception("point "+points[i]);xyz[i]=new[]{x,y,z};}
            elements.Add(Tuple.Create(points,xyz));
            if(Array.TrueForAll(xyz,p=>Math.Abs(p[2])<1e-6))for(int i=0;i<4;i++)horizontalEdges.Add(EdgeKey(points[i],points[(i+1)%4]));
        }
        int constrained=0;
        foreach(var element in elements)
        {
            if(Array.TrueForAll(element.Item2,p=>Math.Abs(p[2])<1e-6))continue;
            for(int i=0;i<4;i++)
            {
                int j=(i+1)%4;if(Math.Abs(element.Item2[i][2])>=1e-6||Math.Abs(element.Item2[j][2])>=1e-6)continue;
                double x=(element.Item2[i][0]+element.Item2[j][0])*0.5,y=(element.Item2[i][1]+element.Item2[j][1])*0.5;
                bool covered=elements.Exists(candidate=>Array.TrueForAll(candidate.Item2,p=>Math.Abs(p[2])<1e-6)&&PointInQuad(x,y,candidate.Item2));
                if(covered){constrained++;string key=EdgeKey(element.Item1[i],element.Item1[j]);if(!horizontalEdges.Contains(key))throw new Exception("nonconforming TEST03 base edge "+key);}
            }
        }
        if(constrained==0)throw new Exception("no TEST03 shell-edge constraints found");
        Console.WriteLine("all-quad-areas="+areaCount+" conforming-base-segments="+constrained);
    }
    static void VerifyCircularHoles(cSapModel model)
    {
        double[][] centers={new[]{0.1,0.2},new[]{0.2,0.2},new[]{0.1,0.1},new[]{0.2,0.1}};int[] chords=new int[centers.Length];int areaCount=0;string[] areas=null;if(model.AreaObj.GetNameList(ref areaCount,ref areas)!=0)throw new Exception("area list");
        foreach(string area in areas){int count=0;string[] points=null;if(model.AreaObj.GetPoints(area,ref count,ref points)!=0||count!=4)throw new Exception("non-quadrilateral area "+area+" points="+count);var xyz=new double[4][];for(int i=0;i<4;i++){double x=0,y=0,z=0;if(model.PointObj.GetCoordCartesian(points[i],ref x,ref y,ref z,"Global")!=0)throw new Exception("point "+points[i]);xyz[i]=new[]{x,y,z};}for(int h=0;h<centers.Length;h++){double cx=0,cy=0;for(int i=0;i<4;i++){cx+=xyz[i][0];cy+=xyz[i][1];double radius=Math.Sqrt(Math.Pow(xyz[i][0]-centers[h][0],2)+Math.Pow(xyz[i][1]-centers[h][1],2));if(radius<0.0195)throw new Exception("node "+points[i]+" inside chordal hole "+h+" at "+xyz[i][0]+","+xyz[i][1]+" radius="+radius);}cx/=4;cy/=4;if(Math.Sqrt(Math.Pow(cx-centers[h][0],2)+Math.Pow(cy-centers[h][1],2))<0.0195)throw new Exception("shell centroid inside circular hole "+h);for(int i=0;i<4;i++){int j=(i+1)%4;double r1=Math.Sqrt(Math.Pow(xyz[i][0]-centers[h][0],2)+Math.Pow(xyz[i][1]-centers[h][1],2));double r2=Math.Sqrt(Math.Pow(xyz[j][0]-centers[h][0],2)+Math.Pow(xyz[j][1]-centers[h][1],2));if(r1>=0.0195&&r1<=0.020001&&r2>=0.0195&&r2<=0.020001)chords[h]++;}}}
        for(int h=0;h<chords.Length;h++)if(chords[h]<8)throw new Exception("incomplete circular boundary "+h+" chords="+chords[h]);Console.WriteLine("test05-all-quads="+areaCount+" circular-chords="+string.Join(",",chords));
    }
    static void VerifyExactSymmetry(cSapModel model)
    {
        int areaCount=0;string[] areas=null;if(model.AreaObj.GetNameList(ref areaCount,ref areas)!=0)throw new Exception("area list");var elements=new HashSet<string>();
        foreach(string area in areas){int count=0;string[] points=null;if(model.AreaObj.GetPoints(area,ref count,ref points)!=0||(count!=4&&(count!=3||Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_EXAMPLES")!="1")))throw new Exception("non-quadrilateral area "+area);var coordinates=new List<string>();for(int i=0;i<count;i++){double x=0,y=0,z=0;model.PointObj.GetCoordCartesian(points[i],ref x,ref y,ref z,"Global");coordinates.Add(Math.Round(x,8)+":"+Math.Round(y,8)+":"+Math.Round(z,8));}coordinates.Sort();elements.Add(string.Join("|",coordinates));}
        foreach(string element in elements){var reflected=element.Split('|').Select(value=>{string[] p=value.Split(':');return Math.Round(2*symmetryX-double.Parse(p[0]),8)+":"+p[1]+":"+p[2];}).OrderBy(value=>value);if(!elements.Contains(string.Join("|",reflected)))throw new Exception("missing reflected shell for "+element);}Console.WriteLine((Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_EXAMPLES")=="1"?"exact-symmetric-shells=":"exact-symmetric-quads=")+areaCount);
    }
    static void SelectTest06Boundary(cSapModel model)
    {
        int count=0;string[] names=null;if(model.PointObj.GetNameList(ref count,ref names)!=0)throw new Exception("point list");int selected=0;
        foreach(string name in names){double x=0,y=0,z=0;if(model.PointObj.GetCoordCartesian(name,ref x,ref y,ref z,"Global")!=0)throw new Exception("point "+name);if(Math.Abs(y)<1e-7&&x>=-.5000001&&x<=.5000001&&(Math.Abs(z-1)<1e-7||Math.Abs(z-2)<1e-7)){if(model.PointObj.SetSelected(name,true,eItemType.Objects)!=0)throw new Exception("select point "+name);selected++;}}
        if(selected!=46)throw new Exception("TEST06 boundary selection count="+selected);Console.WriteLine("test06-selected-boundary-points="+selected);
    }
    static void VerifyTest06(cSapModel model)
    {
        int pointCount=0;string[] pointNames=null;if(model.PointObj.GetNameList(ref pointCount,ref pointNames)!=0)throw new Exception("point list");var bottom=new SortedSet<double>();var top=new SortedSet<double>();
        foreach(string name in pointNames){double x=0,y=0,z=0;model.PointObj.GetCoordCartesian(name,ref x,ref y,ref z,"Global");if(Math.Abs(y)>1e-7||x<-.5000001||x>.5000001)continue;if(Math.Abs(z-1)<1e-7)bottom.Add(Math.Round(x,8));if(Math.Abs(z-2)<1e-7)top.Add(Math.Round(x,8));}
        if(bottom.Count!=11||top.Count!=35)throw new Exception("TEST06 constrained edges changed: bottom="+bottom.Count+" top="+top.Count);
        int areaCount=0;string[] areas=null;if(model.AreaObj.GetNameList(ref areaCount,ref areas)!=0)throw new Exception("area list");int chords=0;
        foreach(string area in areas){int count=0;string[] points=null;if(model.AreaObj.GetPoints(area,ref count,ref points)!=0||count!=4)throw new Exception("non-quadrilateral area "+area);var xyz=new double[4][];for(int i=0;i<4;i++){double x=0,y=0,z=0;model.PointObj.GetCoordCartesian(points[i],ref x,ref y,ref z,"Global");xyz[i]=new[]{x,y,z};double radius=Math.Sqrt(x*x+(z-1.5)*(z-1.5));if(Math.Abs(y)<1e-7&&x>=-.500001&&x<=.500001&&z>=.999999&&z<=2.000001&&radius<test06HoleRadius-1e-6)throw new Exception("node inside TEST06 hole "+points[i]);}for(int i=0;i<4;i++){int j=(i+1)%4;double r1=Math.Sqrt(xyz[i][0]*xyz[i][0]+Math.Pow(xyz[i][2]-1.5,2));double r2=Math.Sqrt(xyz[j][0]*xyz[j][0]+Math.Pow(xyz[j][2]-1.5,2));if(Math.Abs(xyz[i][1])<1e-7&&Math.Abs(xyz[j][1])<1e-7&&Math.Abs(r1-test06HoleRadius)<1e-6&&Math.Abs(r2-test06HoleRadius)<1e-6)chords++;}}
        if(chords<16)throw new Exception("incomplete TEST06 circular boundary chords="+chords);Console.WriteLine("test06-all-quads="+areaCount+" bottom=10 top=34 circular-chords="+chords);
    }
    static void VerifyTest07(cSapModel model)
    {
        double[][] centers={new[]{.07345342114567757,.0700727871156414},new[]{.07345342114567757,.1500727871156414},new[]{.07345342114567757,.2300727871156414},new[]{.225,.07},new[]{.225,.15},new[]{.225,.23}};int[] chords=new int[centers.Length];int areaCount=0;string[] areas=null;if(model.AreaObj.GetNameList(ref areaCount,ref areas)!=0)throw new Exception("area list");
        foreach(string area in areas){int count=0;string[] points=null;if(model.AreaObj.GetPoints(area,ref count,ref points)!=0||count!=4)throw new Exception("non-quadrilateral area "+area);var xyz=new double[4][];for(int i=0;i<4;i++){double x=0,y=0,z=0;model.PointObj.GetCoordCartesian(points[i],ref x,ref y,ref z,"Global");xyz[i]=new[]{x,y,z};}for(int h=0;h<centers.Length;h++)for(int i=0;i<4;i++){int j=(i+1)%4;double a1=test07Plane==1?xyz[i][1]:xyz[i][2],a2=test07Plane==1?xyz[j][1]:xyz[j][2];double fixed1=test07Plane==1?xyz[i][2]:xyz[i][1],fixed2=test07Plane==1?xyz[j][2]:xyz[j][1];double r1=Math.Sqrt(Math.Pow(xyz[i][0]-centers[h][0],2)+Math.Pow(a1-centers[h][1],2));double r2=Math.Sqrt(Math.Pow(xyz[j][0]-centers[h][0],2)+Math.Pow(a2-centers[h][1],2));if(Math.Abs(fixed1-(test07Plane==1?0:.3))<1e-7&&Math.Abs(fixed2-(test07Plane==1?0:.3))<1e-7&&Math.Abs(r1-.015)<.002&&Math.Abs(r2-.015)<.002)chords[h]++;}}
        if(chords.Any(value=>value<8))throw new Exception("incomplete TEST07 holes: "+string.Join(",",chords));Console.WriteLine("test07-area="+test07Plane+" all-quads="+areaCount+" circular-chords="+string.Join(",",chords));
    }
    static void VerifyTest08(cSapModel model)
    {
        int areaCount=0;string[] areas=null;if(model.AreaObj.GetNameList(ref areaCount,ref areas)!=0)throw new Exception("area list");int chords=0;var chordEdges=new HashSet<string>();var signatures=new HashSet<string>();
        foreach(string area in areas){int count=0;string[] points=null;if(model.AreaObj.GetPoints(area,ref count,ref points)!=0||count!=4)throw new Exception("non-quadrilateral area "+area);var xyz=new double[4][];var signature=new List<string>();for(int i=0;i<4;i++){double x=0,y=0,z=0;model.PointObj.GetCoordCartesian(points[i],ref x,ref y,ref z,"Global");xyz[i]=new[]{x,y,z};signature.Add(Math.Round(x,8)+":"+Math.Round(y,8)+":"+Math.Round(z,8));double radius=Math.Sqrt(x*x+Math.Pow(z-.1,2));if(Math.Abs(y)<1e-7&&radius<.014999)throw new Exception("node inside TEST08 hole "+points[i]);}signature.Sort();signatures.Add(string.Join("|",signature));for(int i=0;i<4;i++){int j=(i+1)%4;double r1=Math.Sqrt(xyz[i][0]*xyz[i][0]+Math.Pow(xyz[i][2]-.1,2));double r2=Math.Sqrt(xyz[j][0]*xyz[j][0]+Math.Pow(xyz[j][2]-.1,2));if(Math.Abs(xyz[i][1])<1e-7&&Math.Abs(xyz[j][1])<1e-7&&Math.Abs(r1-.015)<1e-6&&Math.Abs(r2-.015)<1e-6)chordEdges.Add(EdgeKey(points[i],points[j]));}}
        if(generalSymmetry)foreach(string element in signatures){var reflectedZ=element.Split('|').Select(value=>{string[] p=value.Split(':');return p[0]+":"+p[1]+":"+Math.Round(.2-double.Parse(p[2]),8);}).OrderBy(value=>value);if(!signatures.Contains(string.Join("|",reflectedZ)))throw new Exception("missing TEST08 shell reflected across z=0.1 for "+element);}
        chords=chordEdges.Count;if(chords<16)throw new Exception("incomplete TEST08 circular boundary chords="+chords);Console.WriteLine("test08-all-quads="+areaCount+" circular-chords="+chords);
    }
    static void VerifyTest09(cSapModel model)
    {
        double[][] opening={new[]{-.11903929710388184,1.4736244678497314},new[]{-.5242794752120972,.7137991189956665},new[]{.271004319190979,.3997379243373871},new[]{.09877729415893555,1.301397442817688}};
        int areaCount=0;string[] areas=null;if(model.AreaObj.GetNameList(ref areaCount,ref areas)!=0)throw new Exception("area list");int inside=0;var constrained=new int[4];
        foreach(string area in areas){int count=0;string[] points=null;if(model.AreaObj.GetPoints(area,ref count,ref points)!=0||count!=4)throw new Exception("non-quadrilateral area "+area);var xyz=new double[4][];double cx=0,cz=0;for(int i=0;i<4;i++){double x=0,y=0,z=0;model.PointObj.GetCoordCartesian(points[i],ref x,ref y,ref z,"Global");xyz[i]=new[]{x,y,z};cx+=x;cz+=z;}cx/=4;cz/=4;if(PointInPolygon(cx,cz,opening))inside++;for(int i=0;i<4;i++){int j=(i+1)%4;for(int frame=0;frame<4;frame++)if(OnSegment2D(xyz[i][0],xyz[i][2],opening[frame],opening[(frame+1)%4])&&OnSegment2D(xyz[j][0],xyz[j][2],opening[frame],opening[(frame+1)%4]))constrained[frame]++;}}
        if(inside!=0)throw new Exception("TEST09 opening contains "+inside+" shell centroids");if(constrained.Any(value=>value==0))throw new Exception("TEST09 missing constrained frame edges: "+string.Join(",",constrained));Console.WriteLine("test09-all-quads="+areaCount+" frame-segments="+string.Join(",",constrained));
    }
    static void VerifyTest28CircularFrames(cSapModel model)
    {
        int areaCount=0;string[] areas=null;if(model.AreaObj.GetNameList(ref areaCount,ref areas)!=0)throw new Exception("area list");int chords=0;
        foreach(string area in areas)
        {
            int count=0;string[] points=null;if(model.AreaObj.GetPoints(area,ref count,ref points)!=0||count!=4)throw new Exception("non-quadrilateral area "+area);
            var xyz=new double[4][];for(int i=0;i<4;i++){double x=0,y=0,z=0;model.PointObj.GetCoordCartesian(points[i],ref x,ref y,ref z,"Global");xyz[i]=new[]{x,y,z};double radius=Math.Sqrt(x*x+Math.Pow(z-.5,2));if(radius<.099999)throw new Exception("node inside 0.20 m circular hole: "+points[i]+" radius="+radius);}
            for(int i=0;i<4;i++){int j=(i+1)%4;double first=Math.Sqrt(xyz[i][0]*xyz[i][0]+Math.Pow(xyz[i][2]-.5,2));double second=Math.Sqrt(xyz[j][0]*xyz[j][0]+Math.Pow(xyz[j][2]-.5,2));if(Math.Abs(first-.1)<1e-6&&Math.Abs(second-.1)<1e-6)chords++;}
        }
        if(chords<32)throw new Exception("incomplete 0.20 m circular boundary: chords="+chords);
        VerifyFrameCoverage(model,new[]{"1","2","3","4"});
        VerifyFourAreaSeams(model,0,.5,-.5,.5,0,1,"TEST28 circular/frame");
        Console.WriteLine("test28-circular-frame-fallback=PASS quads="+areaCount+" circular-chords="+chords);
    }
    static void VerifyFrameCoverage(cSapModel model,string[] frames)
    {
        int areaCount=0;string[] areas=null;if(model.AreaObj.GetNameList(ref areaCount,ref areas)!=0)throw new Exception("area list");
        foreach(string frame in frames)
        {
            string firstName="",secondName="";if(model.FrameObj.GetPoints(frame,ref firstName,ref secondName)!=0)throw new Exception("frame "+frame);
            double ax=0,ay=0,az=0,bx=0,by=0,bz=0;model.PointObj.GetCoordCartesian(firstName,ref ax,ref ay,ref az,"Global");model.PointObj.GetCoordCartesian(secondName,ref bx,ref by,ref bz,"Global");var first=new[]{ax,az};var second=new[]{bx,bz};double dx=bx-ax,dz=bz-az,lengthSquared=dx*dx+dz*dz;var intervals=new List<double[]>();
            foreach(string area in areas){int count=0;string[] points=null;if(model.AreaObj.GetPoints(area,ref count,ref points)!=0||count!=4)continue;for(int i=0;i<4;i++){int j=(i+1)%4;double x1=0,y1=0,z1=0,x2=0,y2=0,z2=0;model.PointObj.GetCoordCartesian(points[i],ref x1,ref y1,ref z1,"Global");model.PointObj.GetCoordCartesian(points[j],ref x2,ref y2,ref z2,"Global");if(!OnSegment2D(x1,z1,first,second)||!OnSegment2D(x2,z2,first,second))continue;double t1=((x1-ax)*dx+(z1-az)*dz)/lengthSquared,t2=((x2-ax)*dx+(z2-az)*dz)/lengthSquared;intervals.Add(new[]{Math.Min(t1,t2),Math.Max(t1,t2)});}}
            double covered=0;foreach(double[] interval in intervals.OrderBy(value=>value[0])){if(interval[0]>covered+1e-5)throw new Exception("frame "+frame+" mesh-edge gap at "+covered);covered=Math.Max(covered,interval[1]);}if(covered<1-1e-5)throw new Exception("frame "+frame+" incomplete mesh-edge coverage: "+covered);
            Console.WriteLine("frame-congruence="+frame+" segments="+intervals.Count+" coverage=PASS");
        }
    }
    static void VerifyTest11(cSapModel model)
    {
        double[][] centers={new[]{-1.0/3,.125},new[]{-1.0/3,.875},new[]{1.0/3,.875},new[]{1.0/3,.125}};
        double[][] rectangle={new[]{-1.0/6,.25},new[]{1.0/6,.25},new[]{1.0/6,.75},new[]{-1.0/6,.75}};
        int areaCount=0;string[] areas=null;if(model.AreaObj.GetNameList(ref areaCount,ref areas)!=0)throw new Exception("TEST11 area list");
        var signatures=new HashSet<string>();var chords=new int[4];var constrained=new int[4];int insideRectangle=0;
        foreach(string area in areas)
        {
            int count=0;string[] points=null;if(model.AreaObj.GetPoints(area,ref count,ref points)!=0||count!=4)throw new Exception("TEST11 non-quadrilateral area "+area);
            var xyz=new double[4][];var signature=new List<string>();double cx=0,cz=0;
            for(int i=0;i<4;i++){double x=0,y=0,z=0;model.PointObj.GetCoordCartesian(points[i],ref x,ref y,ref z,"Global");xyz[i]=new[]{x,y,z};cx+=x;cz+=z;signature.Add(Math.Round(x,8)+":"+Math.Round(y,8)+":"+Math.Round(z,8));}
            cx/=4;cz/=4;if(cx>-1.0/6+1e-7&&cx<1.0/6-1e-7&&cz>.25+1e-7&&cz<.75-1e-7)insideRectangle++;
            signature.Sort();signatures.Add(string.Join("|",signature));
            for(int i=0;i<4;i++){int j=(i+1)%4;for(int hole=0;hole<4;hole++){double r1=Math.Sqrt(Math.Pow(xyz[i][0]-centers[hole][0],2)+Math.Pow(xyz[i][2]-centers[hole][1],2));double r2=Math.Sqrt(Math.Pow(xyz[j][0]-centers[hole][0],2)+Math.Pow(xyz[j][2]-centers[hole][1],2));if(r1>=.0225&&r1<=.025001&&r2>=.0225&&r2<=.025001)chords[hole]++;if(r1<.02249)throw new Exception("TEST11 node inside circular hole "+hole+" radius="+r1);}for(int frame=0;frame<4;frame++)if(OnSegment2D(xyz[i][0],xyz[i][2],rectangle[frame],rectangle[(frame+1)%4])&&OnSegment2D(xyz[j][0],xyz[j][2],rectangle[frame],rectangle[(frame+1)%4]))constrained[frame]++;}
        }
        if(chords.Any(value=>value<8))throw new Exception("TEST11 incomplete O-grid boundaries: "+string.Join(",",chords));
        if(constrained.Any(value=>value==0))throw new Exception("TEST11 missing frame constraints: "+string.Join(",",constrained));
        if(test11Opening!=(insideRectangle==0))throw new Exception("TEST11 frame-region occupancy mismatch: shells="+insideRectangle+" opening="+test11Opening);
        if(generalSymmetry)foreach(string element in signatures){string reflectedX=string.Join("|",element.Split('|').Select(value=>{string[] p=value.Split(':');return Math.Round(-double.Parse(p[0]),8)+":"+p[1]+":"+p[2];}).OrderBy(value=>value));string reflectedZ=string.Join("|",element.Split('|').Select(value=>{string[] p=value.Split(':');return p[0]+":"+p[1]+":"+Math.Round(1-double.Parse(p[2]),8);}).OrderBy(value=>value));if(!signatures.Contains(reflectedX)||!signatures.Contains(reflectedZ))throw new Exception("TEST11 missing double-symmetry counterpart");}
        Console.WriteLine("test11-all-quads="+areaCount+" circular-chords="+string.Join(",",chords)+" frame-segments="+string.Join(",",constrained)+" frame-region-shells="+insideRectangle+" double-symmetry="+generalSymmetry);
    }
    static bool PointInPolygon(double x,double y,double[][] polygon){bool inside=false;for(int i=0,j=polygon.Length-1;i<polygon.Length;j=i++)if((polygon[i][1]>y)!=(polygon[j][1]>y)&&x<(polygon[j][0]-polygon[i][0])*(y-polygon[i][1])/(polygon[j][1]-polygon[i][1])+polygon[i][0])inside=!inside;return inside;}
    static bool OnSegment2D(double x,double y,double[] first,double[] second){double cross=(x-first[0])*(second[1]-first[1])-(y-first[1])*(second[0]-first[0]);if(Math.Abs(cross)>1e-5)return false;return x>=Math.Min(first[0],second[0])-1e-6&&x<=Math.Max(first[0],second[0])+1e-6&&y>=Math.Min(first[1],second[1])-1e-6&&y<=Math.Max(first[1],second[1])+1e-6;}
    static double AtomicHoleRadius(cSapModel model)
    {
        Type pluginType=Assembly.LoadFrom(activePluginPath).GetType("SapQuadTransitionPlugin.cPlugin");
        const BindingFlags flags=BindingFlags.NonPublic|BindingFlags.Static;
        string unit=(string)pluginType.GetMethod("LengthUnit",flags).Invoke(null,new object[]{model.GetPresentUnits()});
        object[] args={holeDiameter,unit,0.0};
        if(!(bool)pluginType.GetMethod("TryParseLength",flags).Invoke(null,args))throw new Exception("Invalid atomic hole diameter: "+holeDiameter);
        return (double)args[2]*.5;
    }
    static void VerifyAtomicExample(cSapModel model)
    {
        int areaCount=0;string[] areas=null;if(model.AreaObj.GetNameList(ref areaCount,ref areas)!=0)throw new Exception("area list");var connectedPoints=new HashSet<string>(StringComparer.OrdinalIgnoreCase);double maxEdge=0;
        foreach(string area in areas){int count=0;string[] names=null;if(model.AreaObj.GetPoints(area,ref count,ref names)!=0||count<3)throw new Exception("invalid example area "+area);if(atomicId.StartsWith("nTEST",StringComparison.Ordinal)&&count!=4&&Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_EXAMPLES")!="1")throw new Exception("non-quadrilateral atomic example area "+area);for(int i=0;i<count;i++){connectedPoints.Add(names[i]);double ax=0,ay=0,az=0,bx=0,by=0,bz=0;model.PointObj.GetCoordCartesian(names[i],ref ax,ref ay,ref az,"Global");model.PointObj.GetCoordCartesian(names[(i+1)%count],ref bx,ref by,ref bz,"Global");maxEdge=Math.Max(maxEdge,Math.Sqrt((ax-bx)*(ax-bx)+(ay-by)*(ay-by)+(az-bz)*(az-bz)));}}
        foreach(string point in selectedPoints??new string[0])if(!connectedPoints.Contains(point)&&!(atomicId=="nTEST030"&&point=="3"&&enclosedHoles)){
            if(Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_ALLOW_UNCONNECTED_SELECTED_POINTS")=="1"){
                double x=0,y=0,z=0;
                if(model.PointObj.GetCoordCartesian(point,ref x,ref y,ref z,"Global")!=0)
                    throw new Exception("unconnected selected point was removed: "+point);
            }else throw new Exception("selected point is not connected to the atomic mesh: "+point);
        }
        int unconnectedFrames=0;
        foreach(string frame in selectedFrames??new string[0]){string first="",second="";if(model.FrameObj.GetPoints(frame,ref first,ref second)!=0)throw new Exception("selected frame missing after mesh: "+frame);if(!connectedPoints.Contains(first)||!connectedPoints.Contains(second)){if(Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_ALLOW_UNCONNECTED_SELECTED_FRAMES")!="1")throw new Exception("selected frame endpoint is not connected to the atomic mesh: "+frame);unconnectedFrames++;}}
        if(unconnectedFrames>0)Console.WriteLine("atomic-preexisting-or-unmeshed-frame-endpoints="+unconnectedFrames);
        int pointCount=0;string[] pointNames=null;if(model.PointObj.GetNameList(ref pointCount,ref pointNames)!=0)throw new Exception("point list");int frameCount=0;string[] frameNames=null;if(model.FrameObj.GetNameList(ref frameCount,ref frameNames)!=0)throw new Exception("frame list");Console.WriteLine("atomic-metrics shells="+areaCount+" nodes="+pointCount+" connected-nodes="+connectedPoints.Count+" frames="+frameCount+" max-edge="+maxEdge.ToString("0.######",System.Globalization.CultureInfo.InvariantCulture)+" selected-points="+(selectedPoints??new string[0]).Length+" selected-frames="+(selectedFrames??new string[0]).Length);if(atomicId.StartsWith("nTEST",StringComparison.Ordinal)&&Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_EXAMPLES")!="1")Console.WriteLine("atomic-all-quads="+areaCount);
        if(atomicId=="nTEST006")VerifyAtomicCircularVoids(model,new[]{new[]{0.0,0.0,1.0}},AtomicHoleRadius(model));
        else if(atomicId=="nTEST013")VerifyAtomicRectangleOccupancy(model,.65,1.35,.45,1.05,true,"retained closed frame");
        else if(atomicId=="nTEST015"){symmetryX=0;VerifyExactSymmetry(model);}
        else if(atomicId=="nTEST016")VerifyAtomicDisjointRegions(model,1.4,2.0);
        else if(atomicId=="nTEST017"){VerifyAtomicDisjointRegions(model,1.4,1.9);VerifyAtomicCircularVoids(model,new[]{new[]{.7,0.0,.55},new[]{2.6,0.0,.55}},.12);}
        else if(atomicId=="nTEST018")VerifyAtomicRectangleOccupancy(model,.7,1.3,.7,1.3,false,"intersecting-frame opening");
        else if(atomicId=="nTEST019")VerifyAtomicRectangleOccupancy(model,.6,1.4,.65,1.35,false,"mixed frame/shell-edge opening",true);
        else if(atomicId=="nTEST020")VerifyAtomicRectangleOccupancy(model,.6,1.4,.6,1.4,false,"shell-edge opening",true);
        else if(atomicId=="nTEST025"){VerifyAtomicCircularVoids(model,new[]{new[]{0.0,0.0,1.5}},.15);symmetryX=0;VerifyExactSymmetry(model);}
        else if(atomicId=="nTEST026")VerifyAtomicTest07Holes(model);
        else if(atomicId=="nTEST027")VerifyFourAreaSeams(model,0,.1,-.1,.1,0,.2,"TEST08");
        else if(atomicId=="nTEST028"||atomicId=="nTEST035"||atomicId=="nTEST036")VerifyFourAreaSeams(model,0,1,-1,1,0,2,"TEST09");
        else if(atomicId=="nTEST033")VerifyImmutableSelectedEdges(model);
        else if(atomicId=="nTEST034"){VerifyImmutableSelectedEdges(model);if(Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_EXAMPLES")!="1")VerifyRegularHorizontalAlignment(model);symmetryX=0;VerifyExactSymmetry(model);}
    }
    static void VerifyAtomicTest07Holes(cSapModel model)
    {
        int groupCount=0;int[] groupTypes=null;string[] groupNames=null;
        if(model.GroupDef.GetAssignments("foro",ref groupCount,ref groupTypes,ref groupNames)!=0)throw new Exception("nTEST026 hole-center group missing");
        var centers=new List<double[]>();
        for(int i=0;i<groupCount;i++)if(groupTypes[i]==1){double x=0,y=0,z=0;if(model.PointObj.GetCoordCartesian(groupNames[i],ref x,ref y,ref z,"Global")!=0)throw new Exception("hole center "+groupNames[i]);centers.Add(new[]{x,y,z});}
        if(centers.Count!=12)throw new Exception("nTEST026 expected 12 hole centers, found "+centers.Count);
        var chords=centers.Select(_=>new Dictionary<string,int>()).ToArray();
        var points=new Dictionary<string,double[]>(StringComparer.OrdinalIgnoreCase);
        int areaCount=0;string[] areas=null;if(model.AreaObj.GetNameList(ref areaCount,ref areas)!=0)throw new Exception("nTEST026 shell list");
        foreach(string area in areas){
            int count=0;string[] names=null;if(model.AreaObj.GetPoints(area,ref count,ref names)!=0||
                (count!=4&&(count!=3||Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_EXAMPLES")!="1")))
                throw new Exception("nTEST026 invalid shell "+area+" corners="+count);
            var xyz=new double[count][];
            for(int i=0;i<count;i++){
                if(!points.TryGetValue(names[i],out xyz[i])){double x=0,y=0,z=0;if(model.PointObj.GetCoordCartesian(names[i],ref x,ref y,ref z,"Global")!=0)throw new Exception("nTEST026 node "+names[i]);xyz[i]=new[]{x,y,z};points[names[i]]=xyz[i];}
            }
            for(int h=0;h<centers.Count;h++){
                double[] center=centers[h];bool horizontal=Math.Abs(center[2])<1e-7;
                if(!xyz.All(p=>Math.Abs(horizontal?p[2]:p[1]-(center[1]))<1e-7))continue;
                for(int i=0;i<count;i++){
                    double[] first=xyz[i],second=xyz[(i+1)%count];
                    double firstRadius=Math.Sqrt(Math.Pow(first[0]-center[0],2)+Math.Pow((horizontal?first[1]:first[2])-(horizontal?center[1]:center[2]),2));
                    double secondRadius=Math.Sqrt(Math.Pow(second[0]-center[0],2)+Math.Pow((horizontal?second[1]:second[2])-(horizontal?center[1]:center[2]),2));
                    if(firstRadius<.0092||secondRadius<.0092)throw new Exception("nTEST026 node inside chordal hole "+h+" area="+area+" radii="+firstRadius+"/"+secondRadius);
                    if(firstRadius>=.0092&&firstRadius<=.01001&&secondRadius>=.0092&&secondRadius<=.01001){
                        string key=EdgeKey(names[i],names[(i+1)%count]);
                        int uses;chords[h].TryGetValue(key,out uses);chords[h][key]=uses+1;
                    }
                }
            }
        }
        int[] freeEdges=chords.Select(edges=>edges.Values.Count(uses=>uses==1)).ToArray();
        if(freeEdges.Any(count=>count<8))throw new Exception("nTEST026 incomplete 0.020 m hole boundaries: "+string.Join(",",freeEdges));
        Console.WriteLine("atomic-nTEST026-hole-chords="+string.Join(",",freeEdges)+" diameter=0.020");
    }
    static void VerifyFourAreaSeams(cSapModel model,double verticalX,double horizontalZ,double minX,double maxX,double minZ,double maxZ,string label)
    {
        VerifySharedLine(model,true,verticalX,minZ,maxZ,label+" vertical seam");
        VerifySharedLine(model,false,horizontalZ,minX,maxX,label+" horizontal seam");
    }
    static void VerifySharedLine(cSapModel model,bool vertical,double fixedCoordinate,double minimum,double maximum,string label)
    {
        int areaCount=0;string[] areas=null;if(model.AreaObj.GetNameList(ref areaCount,ref areas)!=0)throw new Exception("area list");
        var negative=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var positive=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string area in areas)
        {
            int count=0;string[] names=null;if(model.AreaObj.GetPoints(area,ref count,ref names)!=0||
                count!=4&&(count!=3||Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_EXAMPLES")!="1"))
                throw new Exception("non-quadrilateral area "+area);
            var coordinates=new double[count][];for(int i=0;i<count;i++){double x=0,y=0,z=0;if(model.PointObj.GetCoordCartesian(names[i],ref x,ref y,ref z,"Global")!=0)throw new Exception("point "+names[i]);coordinates[i]=new[]{x,y,z};}
            double side=coordinates.Average(point=>vertical?point[0]:point[2])-fixedCoordinate;if(Math.Abs(side)<1e-8)continue;
            for(int i=0;i<count;i++)
            {
                int j=(i+1)%count;double firstFixed=vertical?coordinates[i][0]:coordinates[i][2],secondFixed=vertical?coordinates[j][0]:coordinates[j][2];
                double firstParameter=vertical?coordinates[i][2]:coordinates[i][0],secondParameter=vertical?coordinates[j][2]:coordinates[j][0];
                if(Math.Abs(firstFixed-fixedCoordinate)>1e-7||Math.Abs(secondFixed-fixedCoordinate)>1e-7||
                    Math.Min(firstParameter,secondParameter)<minimum-1e-7||Math.Max(firstParameter,secondParameter)>maximum+1e-7)continue;
                (side<0?negative:positive).Add(EdgeKey(names[i],names[j]));
            }
        }
        if(negative.Count==0||positive.Count==0||!negative.SetEquals(positive))
            throw new Exception(label+" is nonconforming: negative="+negative.Count+" positive="+positive.Count+
                " only-negative="+string.Join(",",negative.Except(positive).Take(8))+" only-positive="+string.Join(",",positive.Except(negative).Take(8)));
        Console.WriteLine("atomic-shared-seam="+label+" segments="+negative.Count+" common-node-names=PASS");
    }
    static void VerifyImmutableSelectedEdges(cSapModel model)
    {
        var expected=new HashSet<string>(selectedPoints??new string[0],StringComparer.OrdinalIgnoreCase);var actual=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int pointCount=0;string[] names=null;if(model.PointObj.GetNameList(ref pointCount,ref names)!=0)throw new Exception("point list");
        foreach(string name in names){double x=0,y=0,z=0;if(model.PointObj.GetCoordCartesian(name,ref x,ref y,ref z,"Global")!=0)throw new Exception("point "+name);if(Math.Abs(y)<1e-7&&x>=-.5000001&&x<=.5000001&&(Math.Abs(z-1)<1e-7||Math.Abs(z-2)<1e-7))actual.Add(name);}
        if(!actual.SetEquals(expected))throw new Exception("selected edge discretization changed: expected="+expected.Count+" actual="+actual.Count+" added="+string.Join(",",actual.Except(expected))+" missing="+string.Join(",",expected.Except(actual)));
        Console.WriteLine("atomic-immutable-selected-edges=PASS bottom=11 top=35 total="+actual.Count);
    }
    static void VerifyRegularHorizontalAlignment(cSapModel model)
    {
        int areaCount=0;string[] areas=null;if(model.AreaObj.GetNameList(ref areaCount,ref areas)!=0)throw new Exception("area list");
        var coordinates=new Dictionary<string,double[]>(StringComparer.OrdinalIgnoreCase);var edges=new HashSet<string>(StringComparer.OrdinalIgnoreCase);int interiorEdges=0,horizontalEdges=0,verticalEdges=0;
        foreach(string area in areas)
        {
            int count=0;string[] names=null;if(model.AreaObj.GetPoints(area,ref count,ref names)!=0||count!=4)throw new Exception("non-quadrilateral area "+area);
            for(int i=0;i<count;i++)
            {
                string first=names[i],second=names[(i+1)%count],key=EdgeKey(first,second);if(!edges.Add(key))continue;
                foreach(string name in new[]{first,second})if(!coordinates.ContainsKey(name)){double x=0,y=0,z=0;if(model.PointObj.GetCoordCartesian(name,ref x,ref y,ref z,"Global")!=0)throw new Exception("point "+name);coordinates[name]=new[]{x,y,z};}
                double[] a=coordinates[first],b=coordinates[second];if(a[2]<=1.0000001||a[2]>=1.9999999||b[2]<=1.0000001||b[2]>=1.9999999)continue;
                interiorEdges++;if(Math.Abs(a[2]-b[2])<1e-7&&Math.Abs(a[0]-b[0])>1e-7)horizontalEdges++;if(Math.Abs(a[0]-b[0])<1e-7&&Math.Abs(a[2]-b[2])>1e-7)verticalEdges++;
            }
        }
        double ratio=interiorEdges==0?0:(double)(horizontalEdges+verticalEdges)/interiorEdges;
        if(horizontalEdges<100||verticalEdges<100||ratio<.80)throw new Exception("regular principal-axis alignment insufficient: horizontal="+horizontalEdges+" vertical="+verticalEdges+" total="+interiorEdges);
        Console.WriteLine("atomic-regular-axis-edges horizontal="+horizontalEdges+" vertical="+verticalEdges+" total="+interiorEdges+" ratio="+ratio.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture));
    }
    static List<double[][]> AtomicElements(cSapModel model)
    {
        int areaCount=0;string[] areas=null;if(model.AreaObj.GetNameList(ref areaCount,ref areas)!=0)throw new Exception("area list");var elements=new List<double[][]>();foreach(string area in areas){int count=0;string[] names=null;if(model.AreaObj.GetPoints(area,ref count,ref names)!=0)throw new Exception("area points "+area);var xyz=new double[count][];for(int i=0;i<count;i++){double x=0,y=0,z=0;if(model.PointObj.GetCoordCartesian(names[i],ref x,ref y,ref z,"Global")!=0)throw new Exception("point "+names[i]);xyz[i]=new[]{x,y,z};}elements.Add(xyz);}return elements;
    }
    static void VerifyAtomicRectangleOccupancy(cSapModel model,double minX,double maxX,double minZ,double maxZ,bool expected,string label,bool hostPlaneOnly=false)
    {
        int inside=0;foreach(double[][] element in AtomicElements(model)){double x=element.Average(point=>point[0]),y=element.Average(point=>point[1]),z=element.Average(point=>point[2]);if((!hostPlaneOnly||Math.Abs(y)<1e-7)&&x>minX+1e-6&&x<maxX-1e-6&&z>minZ+1e-6&&z<maxZ-1e-6)inside++;}if(expected!=(inside>0))throw new Exception(label+" occupancy mismatch: shells="+inside);Console.WriteLine("atomic-region "+label+" shells="+inside+" expected="+(expected?"filled":"empty"));
    }
    static void VerifyAtomicDisjointRegions(cSapModel model,double left,double right)
    {
        int crossing=0;foreach(double[][] element in AtomicElements(model)){double min=element.Min(point=>point[0]),max=element.Max(point=>point[0]);if(min<left-1e-7&&max>right+1e-7)crossing++;double center=element.Average(point=>point[0]);if(center>left+1e-7&&center<right-1e-7)crossing++;}if(crossing!=0)throw new Exception("shells bridge disjoint regions: "+crossing);Console.WriteLine("atomic-disjoint-regions=PASS gap="+left.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+".."+right.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture));
    }
    static void VerifyAtomicCircularVoids(cSapModel model,double[][] centers,double radius)
    {
        int violations=0;double minimumNode=double.PositiveInfinity,minimumCentroid=double.PositiveInfinity;foreach(double[][] element in AtomicElements(model))foreach(double[] center in centers){foreach(double[] point in element){double distance=Math.Sqrt(Math.Pow(point[0]-center[0],2)+Math.Pow(point[2]-center[2],2));minimumNode=Math.Min(minimumNode,distance);if(distance<radius*.975)violations++;}double x=element.Average(point=>point[0]),z=element.Average(point=>point[2]);double centroidDistance=Math.Sqrt(Math.Pow(x-center[0],2)+Math.Pow(z-center[2],2));minimumCentroid=Math.Min(minimumCentroid,centroidDistance);if(centroidDistance<radius*.9)violations++;}if(violations!=0)throw new Exception("circular void violations="+violations+" minimum-node-radius="+minimumNode.ToString("0.######",System.Globalization.CultureInfo.InvariantCulture)+" minimum-centroid-radius="+minimumCentroid.ToString("0.######",System.Globalization.CultureInfo.InvariantCulture));Console.WriteLine("atomic-circular-voids=PASS count="+centers.Length+" nominal-radius="+radius.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+" minimum-node-radius="+minimumNode.ToString("0.######",System.Globalization.CultureInfo.InvariantCulture));
    }
    static string EdgeKey(string first,string second){return string.CompareOrdinal(first,second)<0?first+":"+second:second+":"+first;}
    static bool PointInQuad(double x,double y,double[][] polygon){bool inside=false;for(int i=0,j=polygon.Length-1;i<polygon.Length;j=i++){double xi=polygon[i][0],yi=polygon[i][1],xj=polygon[j][0],yj=polygon[j][1];double dx=xj-xi,dy=yj-yi,t=(dx*dx+dy*dy)==0?0:((x-xi)*dx+(y-yi)*dy)/(dx*dx+dy*dy);t=Math.Max(0,Math.Min(1,t));double qx=xi+t*dx,qy=yi+t*dy;if(Math.Sqrt((x-qx)*(x-qx)+(y-qy)*(y-qy))<1e-6)return true;if((yi>y)!=(yj>y)&&x<(xj-xi)*(y-yi)/(yj-yi)+xi)inside=!inside;}return inside;}
    static void RunCore(string source,string output,string pluginPath,bool undo)
    {
        activePluginPath=pluginPath;
        failure=null;requestUndo=undo;errorPath=Path.Combine(Path.GetTempPath(),"sap_quad_automation_error_"+Guid.NewGuid().ToString("N")+".txt");
        string test28Boundary=test28CircularFrames?Path.Combine(Path.GetTempPath(),"test28-circular-frame-boundary.csv"):null;Environment.SetEnvironmentVariable("SAP_QUAD_AUTOMATION_ERROR",errorPath);Environment.SetEnvironmentVariable("SAP_QUAD_FAILED_BOUNDARY",test28Boundary??(atomicExample&&!string.IsNullOrEmpty(screenshotPath)?screenshotPath.Replace("-result.svg","-failed-boundary.csv"):null));Environment.SetEnvironmentVariable("SAP_QUAD_BOUNDARY_TRACE",test28Boundary??(atomicExample&&!string.IsNullOrEmpty(screenshotPath)?screenshotPath.Replace("-result.svg","-boundary.csv"):null));cOAPI sap=persistentSap;bool ownsSap=sap==null;
        try
        {
            int ownedPid=ownsSap?0:persistentSapPid;
            if(ownsSap){
                using(var startupMutex=new Mutex(false,"Local\\SAP2000QuadMesherRegressionStartup")){
                    startupMutex.WaitOne();
                    try{
                        var existingSap=new HashSet<int>(Process.GetProcessesByName("SAP2000").Select(process=>process.Id));
                        cHelper h=new Helper();sap=h.CreateObject(@"C:\Program Files\Computers and Structures\SAP2000 27\SAP2000.exe");
                        if(sap==null||sap.ApplicationStart()!=0)throw new Exception("start");
                        int[] newPids=Process.GetProcessesByName("SAP2000").Select(process=>process.Id).Where(id=>!existingSap.Contains(id)).ToArray();
                        if(newPids.Length!=1)throw new InvalidOperationException("Could not identify the isolated SAP process.");
                        ownedPid=newPids[0];
                        string sapPidFile=Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SAP_PID_FILE");
                        if(!string.IsNullOrEmpty(sapPidFile))File.WriteAllLines(sapPidFile,newPids.Select(id=>id.ToString()));
                    }finally{startupMutex.ReleaseMutex();}
                }
            }cSapModel m=sap.SapModel;
            if(m.File.OpenFile(source)!=0)throw new Exception("open");if(m.File.Save(output)!=0)throw new Exception("copy");
            bool parentFixture=atomicExample&&Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_PARENT_FIXTURE")=="1";
            if(parentFixture){if(selectedAreas.Length!=1)throw new InvalidOperationException("Fixture requires one parent area.");AssignParentFixture(m,selectedAreas[0]);}
            bool designFixture=parentFixture&&Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_DESIGN_FIXTURE")=="1";
            if(designFixture)AssignDesignFixture(m,selectedAreas[0]);
            if(atomicExample&&!string.IsNullOrEmpty(screenshotPath))RenderDetailedModelDiagram(m,screenshotPath.Replace("-result.svg","-source.svg"),screenshotView,"Source geometry",true);
            m.SelectObj.ClearSelection();foreach(string a in selectedAreas)m.AreaObj.SetSelected(a,true,eItemType.Objects);foreach(string p in selectedPoints)m.PointObj.SetSelected(p,true,eItemType.Objects);foreach(string f in selectedFrames)m.FrameObj.SetSelected(f,true,eItemType.Objects);if(test06)SelectTest06Boundary(m);
            if(atomicExample){
                Environment.SetEnvironmentVariable("SAP_QUAD_ATOMIC_PROFILE",workflowProfile);
                Environment.SetEnvironmentVariable("SAP_QUAD_ATOMIC_SIZE",meshSize);
                Environment.SetEnvironmentVariable("SAP_QUAD_ATOMIC_HOLE_GROUP",holeGroupName);
                Environment.SetEnvironmentVariable("SAP_QUAD_ATOMIC_DIAMETER",holeDiameter);
                Environment.SetEnvironmentVariable("SAP_QUAD_ATOMIC_OPENING_GROUP",openingGroupName);
                Environment.SetEnvironmentVariable("SAP_QUAD_ATOMIC_SYMMETRY",generalSymmetry?"1":"0");
                Environment.SetEnvironmentVariable("SAP_QUAD_ATOMIC_FAST",useFast?"1":"0");
            }
            Thread thread=null;
            if(!atomicExample){thread=new Thread(Automate);thread.SetApartmentState(ApartmentState.STA);thread.Start();}
            object plugin=Assembly.LoadFrom(pluginPath).CreateInstance("SapQuadTransitionPlugin.cPlugin");FastCallback cb=new FastCallback();object[] par=PluginArguments(plugin,m,cb,ownedPid);
            Console.WriteLine("stage=plugin");plugin.GetType().GetMethod("Main").Invoke(plugin,par);Console.WriteLine("stage=plugin-returned");
            if(thread!=null&&!thread.Join(TimeSpan.FromMinutes(2)))throw new Exception("automation thread did not stop");
            Console.WriteLine("stage=automation-returned failure="+failure);if(failure!=null)throw new Exception("automation/plugin: "+failure);
            bool gmshV2Example=atomicExample&&Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_EXAMPLES")=="1";
            eUnits originalUnits=m.GetPresentUnits(),databaseUnits=m.GetDatabaseUnits();
            string sapVersion="";double sapVersionNumber=0;Version parsedVersion;
            bool normalized=m.GetVersion(ref sapVersion,ref sapVersionNumber)==0&&
                Version.TryParse(sapVersion,out parsedVersion)&&parsedVersion>=new Version(27,2)&&
                originalUnits!=databaseUnits;
            if(normalized&&m.SetPresentUnits(databaseUnits)!=0)throw new Exception("cannot normalize SAP verification units");
            try
            {
                if(!cb.Finished||cb.ErrorFlag!=0)throw new Exception("callback "+cb.ErrorFlag+(File.Exists(errorPath)?": "+File.ReadAllText(errorPath):""));if(undo)VerifyRestoredSelection(m);if(verifyTest02)VerifyTest02Conformity(m);if(verifyAllQuads)VerifyAllAreasAreQuads(m);if(atomicExample&&!undo)VerifyAtomicExample(m);if(parentFixture){int fixtureAreaCount=0;string[] fixtureAreas=null;RequireFixture(m.AreaObj.GetNameList(ref fixtureAreaCount,ref fixtureAreas),"list fixture shells");VerifyParentFixture(m,fixtureAreas??new string[0]);if(designFixture)VerifyDesignFixture(m,fixtureAreas??new string[0]);}if(circularHoles&&!atomicExample&&!test06&&!test07&&!test08&&!test11&&!test28CircularFrames)VerifyCircularHoles(m);if(test06)VerifyTest06(m);if(test07)VerifyTest07(m);if(test08)VerifyTest08(m);if(test09)VerifyTest09(m);if(test11)VerifyTest11(m);if(test28CircularFrames)VerifyTest28CircularFrames(m);if(generalSymmetry&&!atomicExample)VerifyExactSymmetry(m);
                if(gmshV2Example&&!undo){int actual=0,quads=0,triangles=0;string[] shellNames=null;if(m.AreaObj.GetNameList(ref actual,ref shellNames)!=0)throw new Exception("V2 shell list");foreach(string shell in shellNames){int vertexCount=0;string[] joints=null;if(m.AreaObj.GetPoints(shell,ref vertexCount,ref joints)!=0)throw new Exception("V2 shell points");if(vertexCount==4)quads++;else if(vertexCount==3)triangles++;}Console.WriteLine("V2_EXAMPLE_RESULT areas="+actual+" quads="+quads+" triangles="+triangles);}
            }
            finally { if(normalized&&m.SetPresentUnits(originalUnits)!=0)throw new Exception("cannot restore SAP verification units"); }
            if(m.File.Save(output)!=0)throw new Exception("save");Console.WriteLine("saved="+output+" undo="+undo);
            if(!string.IsNullOrEmpty(screenshotPath)){RenderDetailedModelDiagram(m,screenshotPath,screenshotView,"Generated mesh",false);Console.WriteLine("diagram="+screenshotPath);screenshotPath=null;}
        }
        finally
        {
            Environment.SetEnvironmentVariable("SAP_QUAD_AUTOMATION_ERROR",null);
            Environment.SetEnvironmentVariable("SAP_QUAD_FAILED_BOUNDARY",null);
            Environment.SetEnvironmentVariable("SAP_QUAD_BOUNDARY_TRACE",null);
            foreach(string name in new[]{"PROFILE","SIZE","HOLE_GROUP","DIAMETER","OPENING_GROUP","SYMMETRY","FAST","REGULAR","REFINED"})
                Environment.SetEnvironmentVariable("SAP_QUAD_ATOMIC_"+name,null);
            if(ownsSap&&sap!=null)try{sap.ApplicationExit(false);}catch{}
        }
    }
    static void CaptureSapWindow(string path,string view)
    {
        Process process=Process.GetProcessesByName("SAP2000").OrderByDescending(item=>item.StartTime).FirstOrDefault();if(process==null)throw new Exception("SAP2000 window not found for screenshot");IntPtr window=process.MainWindowHandle;if(window==IntPtr.Zero)throw new Exception("SAP2000 main window handle not found");ShowWindow(window,3);SetForegroundWindow(window);Thread.Sleep(300);RECT rect;if(!GetWindowRect(window,out rect))throw new Exception("SAP2000 window bounds unavailable");MouseClick(rect.Right-15,rect.Top+87);Thread.Sleep(250);MouseClick(rect.Left+390,rect.Top+87);Thread.Sleep(250);MouseClick(rect.Left+(string.Equals(view,"3d",StringComparison.OrdinalIgnoreCase)?506:552),rect.Top+60);Thread.Sleep(350);MouseClick(rect.Left+100,rect.Bottom-150);System.Windows.Forms.SendKeys.SendWait("{HOME}");Thread.Sleep(400);SetCursorPos((rect.Left+rect.Right)/2,(rect.Top+rect.Bottom)/2);for(int index=0;index<7;index++){mouse_event(0x0800,0,0,120,UIntPtr.Zero);Thread.Sleep(60);}Thread.Sleep(700);Directory.CreateDirectory(Path.GetDirectoryName(path));using(var bitmap=new Bitmap(rect.Right-rect.Left,rect.Bottom-rect.Top)){using(Graphics graphics=Graphics.FromImage(bitmap))graphics.CopyFromScreen(rect.Left,rect.Top,0,0,bitmap.Size);bitmap.Save(path,ImageFormat.Png);}
    }
    static void MouseClick(int x,int y){SetCursorPos(x,y);mouse_event(MouseDown,0,0,0,UIntPtr.Zero);mouse_event(MouseUp,0,0,0,UIntPtr.Zero);}
    static void RenderModelDiagram(cSapModel model,string path,string view,string title)
    {
        int pointCount=0;string[] pointNames=null;if(model.PointObj.GetNameList(ref pointCount,ref pointNames)!=0)throw new Exception("diagram point list");var points=new Dictionary<string,double[]>();foreach(string name in pointNames){double x=0,y=0,z=0;if(model.PointObj.GetCoordCartesian(name,ref x,ref y,ref z,"Global")!=0)throw new Exception("diagram point "+name);points[name]=new[]{x,y,z};}
        int areaCount=0;string[] areaNames=null;if(model.AreaObj.GetNameList(ref areaCount,ref areaNames)!=0)throw new Exception("diagram area list");var polygons=new List<double[][]>();foreach(string area in areaNames){int count=0;string[] names=null;if(model.AreaObj.GetPoints(area,ref count,ref names)!=0)continue;polygons.Add(names.Select(name=>ProjectDiagram(points[name],view)).ToArray());}
        int frameCount=0;string[] frameNames=null;model.FrameObj.GetNameList(ref frameCount,ref frameNames);var frames=new List<double[][]>();foreach(string frame in frameNames??new string[0]){string first="",second="";if(model.FrameObj.GetPoints(frame,ref first,ref second)==0&&points.ContainsKey(first)&&points.ContainsKey(second))frames.Add(new[]{ProjectDiagram(points[first],view),ProjectDiagram(points[second],view)});}
        var all=polygons.SelectMany(item=>item).Concat(frames.SelectMany(item=>item)).ToArray();if(all.Length==0)return;double minX=all.Min(p=>p[0]),maxX=all.Max(p=>p[0]),minY=all.Min(p=>p[1]),maxY=all.Max(p=>p[1]);const int width=1200,height=720,margin=70;double scale=Math.Min((width-2.0*margin)/Math.Max(maxX-minX,1e-9),(height-2.0*margin-40)/Math.Max(maxY-minY,1e-9));Func<double[],PointF> map=p=>new PointF((float)(margin+(p[0]-minX)*scale),(float)(height-margin-(p[1]-minY)*scale));Directory.CreateDirectory(Path.GetDirectoryName(path));
        var svg=new StringBuilder();svg.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1200\" height=\"720\" viewBox=\"0 0 1200 720\">");svg.AppendLine("<rect width=\"1200\" height=\"720\" fill=\"#f8fafc\"/>");svg.AppendLine("<text x=\"40\" y=\"42\" font-family=\"Arial\" font-size=\"25\" font-weight=\"700\" fill=\"#0f172a\">"+SecurityElement.Escape(title)+"</text>");foreach(double[][] polygon in polygons){string coordinates=string.Join(" ",polygon.Select(point=>{PointF p=map(point);return p.X.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+","+p.Y.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture);}));svg.AppendLine("<polygon points=\""+coordinates+"\" fill=\"#bfdbfe\" fill-opacity=\"0.72\" stroke=\"#2563eb\" stroke-width=\"1.15\"/>");}foreach(double[][] frame in frames){PointF a=map(frame[0]),b=map(frame[1]);svg.AppendLine(string.Format(System.Globalization.CultureInfo.InvariantCulture,"<line x1=\"{0:0.###}\" y1=\"{1:0.###}\" x2=\"{2:0.###}\" y2=\"{3:0.###}\" stroke=\"#f97316\" stroke-width=\"4\" stroke-linecap=\"round\"/>",a.X,a.Y,b.X,b.Y));}svg.AppendLine("</svg>");File.WriteAllText(path,svg.ToString(),Encoding.UTF8);
        using(var bitmap=new Bitmap(width,height))using(Graphics graphics=Graphics.FromImage(bitmap)){graphics.SmoothingMode=SmoothingMode.AntiAlias;graphics.Clear(Color.FromArgb(248,250,252));using(var titleFont=new Font("Arial",18,FontStyle.Bold))using(var titleBrush=new SolidBrush(Color.FromArgb(15,23,42)))graphics.DrawString(title,titleFont,titleBrush,40,18);using(var fill=new SolidBrush(Color.FromArgb(185,219,254)))using(var edgePen=new Pen(Color.FromArgb(37,99,235),1.2f))foreach(double[][] polygon in polygons){PointF[] mapped=polygon.Select(map).ToArray();graphics.FillPolygon(fill,mapped);graphics.DrawPolygon(edgePen,mapped);}using(var framePen=new Pen(Color.FromArgb(249,115,22),4)){framePen.StartCap=framePen.EndCap=LineCap.Round;foreach(double[][] frame in frames)graphics.DrawLine(framePen,map(frame[0]),map(frame[1]));}bitmap.Save(Path.ChangeExtension(path,".png"),ImageFormat.Png);}
    }
    static void RenderDetailedModelDiagram(cSapModel model,string path,string view,string title,bool source)
    {
        int pointCount=0;string[] pointNames=null;if(model.PointObj.GetNameList(ref pointCount,ref pointNames)!=0)throw new Exception("diagram point list");
        var points=new Dictionary<string,double[]>();foreach(string name in pointNames??new string[0]){double x=0,y=0,z=0;if(model.PointObj.GetCoordCartesian(name,ref x,ref y,ref z,"Global")!=0)throw new Exception("diagram point "+name);points[name]=new[]{x,y,z};}
        int areaCount=0;string[] areaNames=null;if(model.AreaObj.GetNameList(ref areaCount,ref areaNames)!=0)throw new Exception("diagram area list");
        var polygons=new List<Tuple<string,double[][]>>();foreach(string area in areaNames??new string[0]){int count=0;string[] names=null;if(model.AreaObj.GetPoints(area,ref count,ref names)!=0)continue;polygons.Add(Tuple.Create(area,names.Select(name=>ProjectDiagram(points[name],view)).ToArray()));}
        int frameCount=0;string[] frameNames=null;model.FrameObj.GetNameList(ref frameCount,ref frameNames);var frames=new List<Tuple<string,double[],double[]>>();foreach(string frame in frameNames??new string[0]){string first="",second="";if(model.FrameObj.GetPoints(frame,ref first,ref second)==0&&points.ContainsKey(first)&&points.ContainsKey(second))frames.Add(Tuple.Create(frame,ProjectDiagram(points[first],view),ProjectDiagram(points[second],view)));}
        var projected=points.ToDictionary(item=>item.Key,item=>ProjectDiagram(item.Value,view));var all=polygons.SelectMany(item=>item.Item2).Concat(frames.SelectMany(item=>new[]{item.Item2,item.Item3})).Concat(projected.Values).ToArray();if(all.Length==0)return;
        double minX=all.Min(p=>p[0]),maxX=all.Max(p=>p[0]),minY=all.Min(p=>p[1]),maxY=all.Max(p=>p[1]);const int width=1200,height=720,left=70,right=70,top=105,bottom=60;double scale=Math.Min((width-left-right)/(Math.Max(maxX-minX,1e-9)),(height-top-bottom)/(Math.Max(maxY-minY,1e-9)));double usedWidth=(maxX-minX)*scale,usedHeight=(maxY-minY)*scale;double offsetX=left+(width-left-right-usedWidth)*.5,offsetY=top+(height-top-bottom-usedHeight)*.5;Func<double[],PointF> map=p=>new PointF((float)(offsetX+(p[0]-minX)*scale),(float)(offsetY+usedHeight-(p[1]-minY)*scale));
        var selectedPointSet=new HashSet<string>(selectedPoints??new string[0],StringComparer.OrdinalIgnoreCase);var selectedFrameSet=new HashSet<string>(selectedFrames??new string[0],StringComparer.OrdinalIgnoreCase);var selectedAreaSet=new HashSet<string>(selectedAreas??new string[0],StringComparer.OrdinalIgnoreCase);Directory.CreateDirectory(Path.GetDirectoryName(path));
        var invariant=System.Globalization.CultureInfo.InvariantCulture;Func<float,string> number=value=>value.ToString("0.###",invariant);var svg=new StringBuilder();svg.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"1200\" height=\"720\" viewBox=\"0 0 1200 720\">");svg.AppendLine("<rect width=\"1200\" height=\"720\" rx=\"20\" fill=\"#f8fafc\"/>");svg.AppendLine("<rect x=\"24\" y=\"18\" width=\"1152\" height=\"66\" rx=\"14\" fill=\"#0f172a\"/>");svg.AppendLine("<text x=\"52\" y=\"59\" font-family=\"Segoe UI,Arial\" font-size=\"27\" font-weight=\"700\" fill=\"white\">"+SecurityElement.Escape(title)+"</text>");svg.AppendLine("<text x=\"1145\" y=\"57\" text-anchor=\"end\" font-family=\"Segoe UI,Arial\" font-size=\"16\" fill=\"#cbd5e1\">shells "+areaCount+"  |  nodes "+pointCount+"  |  frames "+frameCount+"</text>");
        string shellOpacity=source?"0.72":(areaCount>1000?"0.16":"0.55");float ordinaryRadius=source?3.4f:(pointCount>2000?.55f:(pointCount>1000?.8f:(pointCount>500?1.1f:1.8f)));
        foreach(var item in polygons){string coordinates=string.Join(" ",item.Item2.Select(point=>{PointF p=map(point);return number(p.X)+","+number(p.Y);}));string fill=source?(selectedAreaSet.Contains(item.Item1)?"#bae6fd":"#e2e8f0"):"#dbeafe";svg.AppendLine("<polygon points=\""+coordinates+"\" fill=\""+fill+"\" fill-opacity=\""+shellOpacity+"\" stroke=\"#2563eb\" stroke-width=\"1.2\" stroke-linejoin=\"round\"/>");}
        foreach(var frame in frames){PointF a=map(frame.Item2),b=map(frame.Item3);string color=selectedFrameSet.Contains(frame.Item1)?"#ea580c":"#f59e0b";svg.AppendLine("<line x1=\""+number(a.X)+"\" y1=\""+number(a.Y)+"\" x2=\""+number(b.X)+"\" y2=\""+number(b.Y)+"\" stroke=\""+color+"\" stroke-width=\"4\" stroke-linecap=\"round\"/>");if(source){float x=(a.X+b.X)/2+6,y=(a.Y+b.Y)/2-6;svg.AppendLine("<text x=\""+number(x)+"\" y=\""+number(y)+"\" font-family=\"Segoe UI,Arial\" font-size=\"14\" font-weight=\"700\" fill=\"#9a3412\">"+SecurityElement.Escape(frame.Item1)+"</text>");}}
        foreach(var item in projected){PointF p=map(item.Value);bool selected=selectedPointSet.Contains(item.Key);float radius=selected?5f:ordinaryRadius;string fill=selected?"#7c3aed":"#0f172a";svg.AppendLine("<circle cx=\""+number(p.X)+"\" cy=\""+number(p.Y)+"\" r=\""+number(radius)+"\" fill=\""+fill+"\" stroke=\"white\" stroke-width=\"0.8\"/>");if(source&&pointCount<=40)svg.AppendLine("<text x=\""+number(p.X+7)+"\" y=\""+number(p.Y-7)+"\" font-family=\"Segoe UI,Arial\" font-size=\"13\" fill=\"#334155\">"+SecurityElement.Escape(item.Key)+"</text>");}
        svg.AppendLine("<g font-family=\"Segoe UI,Arial\" font-size=\"14\" fill=\"#334155\"><circle cx=\"72\" cy=\"686\" r=\"4\" fill=\"#0f172a\"/><text x=\"84\" y=\"691\">SAP2000 node</text><circle cx=\"190\" cy=\"686\" r=\"5\" fill=\"#7c3aed\"/><text x=\"203\" y=\"691\">selected node</text><line x1=\"330\" y1=\"686\" x2=\"365\" y2=\"686\" stroke=\"#ea580c\" stroke-width=\"4\"/><text x=\"376\" y=\"691\">frame</text><rect x=\"455\" y=\"679\" width=\"28\" height=\"14\" fill=\"#dbeafe\" stroke=\"#2563eb\"/><text x=\"493\" y=\"691\">shell</text></g>");svg.AppendLine("</svg>");File.WriteAllText(path,svg.ToString(),Encoding.UTF8);
        using(var bitmap=new Bitmap(width,height))using(Graphics graphics=Graphics.FromImage(bitmap)){graphics.SmoothingMode=SmoothingMode.AntiAlias;graphics.Clear(Color.FromArgb(248,250,252));using(var header=new SolidBrush(Color.FromArgb(15,23,42)))graphics.FillRectangle(header,24,18,1152,66);using(var titleFont=new Font("Segoe UI",20,FontStyle.Bold))using(var white=new SolidBrush(Color.White))graphics.DrawString(title,titleFont,white,52,35);using(var shellFill=new SolidBrush(Color.FromArgb(source?205:(areaCount>1000?45:140),219,234,254)))using(var shellPen=new Pen(Color.FromArgb(37,99,235),1.2f))foreach(var polygon in polygons){PointF[] mapped=polygon.Item2.Select(map).ToArray();graphics.FillPolygon(shellFill,mapped);graphics.DrawPolygon(shellPen,mapped);}using(var framePen=new Pen(Color.FromArgb(234,88,12),4)){framePen.StartCap=framePen.EndCap=LineCap.Round;foreach(var frame in frames)graphics.DrawLine(framePen,map(frame.Item2),map(frame.Item3));}foreach(var item in projected){PointF p=map(item.Value);bool selected=selectedPointSet.Contains(item.Key);float radius=selected?5f:ordinaryRadius;using(var brush=new SolidBrush(selected?Color.FromArgb(124,58,237):Color.FromArgb(15,23,42)))graphics.FillEllipse(brush,p.X-radius,p.Y-radius,radius*2,radius*2);}bitmap.Save(Path.ChangeExtension(path,".png"),ImageFormat.Png);}
    }
    static double[] ProjectDiagram(double[] point,string view){if(string.Equals(view,"3d",StringComparison.OrdinalIgnoreCase))return new[]{point[0]-.58*point[1],point[2]+.3*(point[0]+point[1])};if(string.Equals(view,"xy",StringComparison.OrdinalIgnoreCase))return new[]{point[0],point[1]};return new[]{point[0],point[2]};}
}
