// Run Generate -> Apply -> open another SDB -> select -> Generate -> Undo
// against an owned SAP2000 process and disposable copies only.
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;
using CSiAPIv1;

internal sealed class TestV2ModelSwitch
{
    private sealed class Callback : cPluginCallback
    {
        public bool Finished { get; private set; }
        public int ErrorFlag { get; private set; }
        public void Finish(int flag) { Finished = true; ErrorFlag = flag; }
    }

    private readonly cSapModel model;
    private readonly string first, second;
    private readonly bool sameModel;
    private readonly bool differentUnits;
    private readonly Type dialogType;
    private readonly Timer timer = new Timer { Interval = 200 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private Form dialog;
    private Exception failure;
    private int stage;
    private int firstAppliedAreaCount;
    private string nextSelectedArea = "A1";

    private TestV2ModelSwitch(cSapModel model, string first, string second, Type dialogType,
        bool sameModel, bool differentUnits)
    {
        this.model = model;
        this.first = first;
        this.second = second;
        this.dialogType = dialogType;
        this.sameModel = sameModel;
        this.differentUnits = differentUnits;
        timer.Tick += Tick;
    }

    private static object Field(Form form, string name)
    {
        return form.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(form);
    }

    private static void Check(int code, string operation)
    {
        if (code != 0) throw new InvalidOperationException(operation + " failed: " + code);
    }

    private int AreaCount()
    {
        int count = 0; string[] names = null;
        Check(model.AreaObj.GetNameList(ref count, ref names), "count current model areas");
        return count;
    }

    private static byte[] Hash(string path)
    {
        using (var hash = SHA256.Create()) using (var file = File.OpenRead(path)) return hash.ComputeHash(file);
    }

    private void ClickGenerate(Button button)
    {
        button.PerformClick();
        if (!(bool)dialogType.GetProperty("StartRequested").GetValue(dialog))
            throw new InvalidOperationException("Generate did not accept the selected source area.");
    }

    private void Tick(object sender, EventArgs e)
    {
        if (failure != null || stage >= 6) return;
        try
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(160))
                throw new TimeoutException("Model switch workflow stalled at stage " + stage);
            if (dialog == null)
                dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(form => form.GetType() == dialogType);
            if (dialog == null) return;
            Button generate = (Button)Field(dialog, "generate");
            Button undo = (Button)Field(dialog, "undo");
            object report = Field(dialog, "qualityReport");
            if (stage == 0 && generate.Enabled)
            {
                stage = 10;
                ((Button)Field(dialog, "gmshResetDefault")).PerformClick();
                object panel = Field(dialog, "gmshPreviewOptions");
                ((TextBox)panel.GetType().GetField("maxSize", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(panel)).Text = "0.4m";
                ((CheckBox)Field(dialog, "createBackup")).Checked = false;
                ClickGenerate(generate);
                stage = 1;
                Console.WriteLine("FIRST_MODEL_GENERATE=PASS");
            }
            else if (stage == 1 && report != null && generate.Text == "Apply")
            {
                stage = 11;
                string snapshot = (string)Field(dialog, "snapshotPath");
                dialogType.GetMethod("ApplyForAutomation").Invoke(dialog, new object[0]);
                if (!(bool)dialogType.GetProperty("Applied").GetValue(dialog) || undo.Enabled ||
                    File.Exists(snapshot))
                    throw new InvalidOperationException("Apply retained an Undo command or previous-model snapshot.");
                firstAppliedAreaCount = AreaCount();
                stage = 2;
                Console.WriteLine("FIRST_MODEL_APPLY=PASS OLD_UNDO_DISABLED=PASS SNAPSHOT_RELEASED=PASS");
            }
            else if (stage == 2)
            {
                stage = 12;
                if (sameModel)
                {
                    int count = 0; string[] names = null;
                    Check(model.AreaObj.GetNameList(ref count, ref names), "list first-model generated shells");
                    nextSelectedArea = names.First(name => name != "A1");
                    Check(model.SelectObj.ClearSelection(), "clear first-model selection");
                    Check(model.AreaObj.SetSelected(nextSelectedArea, true, eItemType.Objects), "select another generated shell");
                }
                else
                {
                    Check(model.File.OpenFile(second), "open second SAP model copy");
                    if (differentUnits)
                        Check(model.SetPresentUnits(eUnits.kN_cm_C), "display temporary second model in centimetres");
                    Console.WriteLine("SECOND_MODEL_PRESENT_UNITS=" + model.GetPresentUnits() +
                        " DATABASE_UNITS=" + model.GetDatabaseUnits());
                    Check(model.SelectObj.ClearSelection(), "clear second-model selection");
                    Check(model.AreaObj.SetSelected("A1", true, eItemType.Objects), "select second-model area");
                }
                stage = 3;
                Console.WriteLine(sameModel ? "SAME_MODEL_NEW_AREA_SELECTED=PASS" : "SECOND_MODEL_OPEN_AND_SELECT=PASS");
            }
            else if (stage == 3 && !(bool)dialogType.GetProperty("Applied").GetValue(dialog) &&
                     generate.Enabled && !undo.Enabled && report == null)
            {
                if (!string.Equals(model.GetModelFilename(true), sameModel ? first : second,
                    StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SAP is not using the expected SDB.");
                string summary = ((Label)Field(dialog, "areaSelection")).Text;
                if (!summary.Contains("Areas: 1") && !summary.Contains("Aree: 1"))
                    throw new InvalidDataException("Plugin did not display the second model's selection: " + summary);
                if (differentUnits)
                {
                    object panel = Field(dialog, "gmshPreviewOptions");
                    string label = ((Label)panel.GetType().GetField("maxLabel",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(panel)).Text;
                    string expected = model.GetPresentUnits() == eUnits.kN_m_C ? "m" :
                        model.GetPresentUnits() == eUnits.kN_cm_C ? "cm" : "unsupported";
                    if (!label.Contains("(" + expected + ")"))
                        throw new InvalidDataException("Second model's effective API units were not displayed: " + label);
                    Console.WriteLine("SECOND_MODEL_UNIT_CHANGE=PASS INPUT_UNITS=" + expected +
                        " DATABASE_UNITS=" + model.GetDatabaseUnits());
                }
                stage = 13;
                ClickGenerate(generate);
                stage = 4;
                Console.WriteLine("SECOND_MODEL_SELECTION_REFRESH=PASS OLD_UNDO_DISABLED=PASS SECOND_GENERATE=PASS");
            }
            else if (stage == 4 && report != null && generate.Text == "Apply")
            {
                stage = 14;
                string snapshot = (string)Field(dialog, "snapshotPath");
                dialogType.GetMethod("ApplyForAutomation").Invoke(dialog, new object[0]);
                if (!(bool)dialogType.GetProperty("Applied").GetValue(dialog) || undo.Enabled ||
                    File.Exists(snapshot) || AreaCount() < (sameModel ? firstAppliedAreaCount : 2))
                    throw new InvalidOperationException("Second mesh was not committed or its snapshot was retained.");
                stage = 6;
                timer.Stop();
                dialog.Close();
                Console.WriteLine("SECOND_MODEL_APPLY=PASS UNDO_DISABLED=PASS SNAPSHOT_RELEASED=PASS");
            }
        }
        catch (Exception error)
        {
            failure = error;
            timer.Stop();
            if (dialog != null && !dialog.IsDisposed) dialog.Close();
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3 && args.Length != 4)
            throw new ArgumentException("Usage: TestV2ModelSwitch FIRST_COPY SECOND_COPY PLUGIN_DLL [--same-model]");
        bool sameModel = args.Length == 4 && args[3] == "--same-model";
        bool differentUnits = args.Length == 4 && args[3] == "--different-units";
        string first = Path.GetFullPath(args[0]), second = Path.GetFullPath(args[1]);
        string approved = Path.Combine(Path.GetTempPath(), "opencode") + Path.DirectorySeparatorChar;
        if (!first.StartsWith(approved, StringComparison.OrdinalIgnoreCase) ||
            !second.StartsWith(approved, StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(first) || !File.Exists(second) || !File.Exists(args[2]))
            throw new InvalidOperationException("Only existing isolated SAP model/plugin copies are allowed.");
        byte[] firstHash = Hash(first), secondHash = Hash(second);
        cHelper helper = new Helper();
        cOAPI sap = helper.CreateObject(@"C:\Program Files\Computers and Structures\SAP2000 27\SAP2000.exe");
        try
        {
            Check(sap.ApplicationStart(), "start isolated SAP2000");
            if (differentUnits)
            {
                Check(sap.SapModel.File.OpenFile(second), "open temporary second fixture for unit selection");
                Check(sap.SapModel.SetPresentUnits(eUnits.kN_cm_C), "set temporary second fixture to centimetres");
                Check(sap.SapModel.File.Save(second), "save temporary second fixture unit selection");
                secondHash = Hash(second);
            }
            Check(sap.SapModel.File.OpenFile(first), "open first SAP copy");
            Check(sap.SapModel.SelectObj.ClearSelection(), "clear first-model selection");
            Check(sap.SapModel.AreaObj.SetSelected("A1", true, eItemType.Objects), "select first-model area");
            var assembly = Assembly.LoadFrom(Path.GetFullPath(args[2]));
            Type pluginType = assembly.GetType("SapQuadTransitionPlugin.cPlugin", true);
            var test = new TestV2ModelSwitch(sap.SapModel, first, second,
                assembly.GetType("SapQuadTransitionPlugin.cPlugin+MeshOptionsDialog", true), sameModel,
                differentUnits);
            var callback = new Callback();
            string oldOptions = Environment.GetEnvironmentVariable("SAP_GMSH_V2_SETTINGS_PATH");
            string oldUpdate = Environment.GetEnvironmentVariable("SAP_QUAD_TEST_UPDATE_MANIFEST_JSON");
            Environment.SetEnvironmentVariable("SAP_GMSH_V2_SETTINGS_PATH",
                Path.Combine(Path.GetDirectoryName(first), "model-switch-settings.json"));
            Environment.SetEnvironmentVariable("SAP_QUAD_TEST_UPDATE_MANIFEST_JSON", "{}");
            try
            {
                test.timer.Start();
                pluginType.GetMethod("Main").Invoke(Activator.CreateInstance(pluginType),
                    new object[] { sap.SapModel, callback });
                if (test.failure != null) throw test.failure;
                if (test.stage != 6 || !callback.Finished || callback.ErrorFlag != 0)
                    throw new InvalidOperationException("Incomplete cross-model workflow: " + test.stage);
                if (differentUnits && sap.SapModel.GetPresentUnits() != eUnits.kN_cm_C)
                    throw new InvalidDataException("Plugin did not restore the new model's centimetre display units.");
                if (!firstHash.SequenceEqual(Hash(first)) || !secondHash.SequenceEqual(Hash(second)))
                    throw new InvalidDataException("A source model copy was unexpectedly saved.");
                Console.WriteLine((sameModel ? "V2_SAME_MODEL_REFRESH=PASS" : "V2_CROSS_MODEL_REFRESH=PASS") +
                    " ORIGINAL_COPIES_UNCHANGED=PASS");
                return 0;
            }
            finally
            {
                test.timer.Stop(); test.timer.Dispose();
                Environment.SetEnvironmentVariable("SAP_GMSH_V2_SETTINGS_PATH", oldOptions);
                Environment.SetEnvironmentVariable("SAP_QUAD_TEST_UPDATE_MANIFEST_JSON", oldUpdate);
            }
        }
        finally { try { sap.ApplicationExit(false); } catch { } }
    }
}
