// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SapQuadTransitionPlugin
{
    // Shipped V2 Gmsh tab. Generate reads these values through GmshSettingsJson.
    internal sealed class GmshPreviewPanel
    {
        private static readonly string[] ControlledOptions = {
            "Mesh.MeshSizeMin", "Mesh.MeshSizeFactor", "Mesh.MeshSizeFromPoints",
            "Mesh.MeshSizeExtendFromBoundary", "Mesh.MeshSizeFromCurvature", "Mesh.MeshSizeFromParametricPoints",
            "Mesh.SubdivisionAlgorithm", "Mesh.AlgorithmSwitchOnFailure", "Mesh.Smoothing", "Mesh.RefineSteps", "Mesh.AnisoMax",
            "Mesh.SmoothRatio", "Mesh.RecombineMinimumQuality", "Mesh.RecombineOptimizeTopology",
            "Mesh.RecombineNodeRepositioning", "Mesh.QuadqsSizemapMethod",
            "Mesh.QuadqsTopologyOptimizationMethods", "Mesh.QuadqsRemeshingBoldness",
            "Mesh.QuadqsScalingOnTriangulation"
        };

        internal static bool ControlsOption(string name)
        {
            return Array.IndexOf(ControlledOptions, name) >= 0;
        }

        internal sealed class Values
        {
            public double MaxMeters { get; set; }
            public double MinMeters { get; set; }
            public string MaxText { get; set; }
            public string MinText { get; set; }
            public bool FavorRegularGrid { get; set; }
            public int Algorithm { get; set; }
            public int? SmoothingSteps { get; set; }
            public string RefineSteps { get; set; }
            public string AnisoMax { get; set; }
            public string SmoothRatio { get; set; }
            public bool SwitchOnFailure { get; set; }
            public bool Recombine { get; set; }
            public int Recombination { get; set; }
            public string MinimumQuality { get; set; }
            public string TopologySteps { get; set; }
            public bool NodeRepositioning { get; set; }
            public bool Optimize { get; set; }
            public int Optimizer { get; set; }
            public int SizeMap { get; set; }
            public int Topology { get; set; }
            public string Boldness { get; set; }
            public string Scaling { get; set; }
            public string Iterations { get; set; }
        }

        private readonly Func<string, string, string> localize;
        private readonly Func<string, double?> parseLength;
        private readonly Func<double, string> displayLength;
        private string units;
        private readonly Panel scroller = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        private readonly TableLayoutPanel grid = new TableLayoutPanel();
        private readonly GroupBox general = new GroupBox(), generation = new GroupBox();
        private readonly GroupBox recombination = new GroupBox(), optimization = new GroupBox();
        private readonly TextBox maxSize = new TextBox(), minSize = new TextBox();
        private readonly CheckBox favorRegularGrid = new CheckBox();
        private readonly ComboBox algorithm = new ComboBox(), recombinationMethod = new ComboBox();
        private readonly TextBox smoothingSteps = new TextBox();
        private readonly ComboBox optimizer = new ComboBox();
        private readonly CheckBox switchOnFailure = new CheckBox(), enableRecombination = new CheckBox();
        private readonly CheckBox reposition = new CheckBox(), enableOptimization = new CheckBox();
        private readonly TextBox quality = new TextBox(), topologySteps = new TextBox(), iterations = new TextBox();
        private readonly Button algorithmSettings = new Button(), optimizationSettings = new Button();
        private readonly Label maxLabel = new Label(), minLabel = new Label(), algorithmLabel = new Label();
        private readonly Label smoothingLabel = new Label();
        private readonly Label recombinationLabel = new Label(), qualityLabel = new Label();
        private readonly Label topologyLabel = new Label(), optimizerLabel = new Label(), iterationsLabel = new Label();
        private string refineSteps = "10", anisoMax = "1E33", smoothRatio = "1.8";
        private int sizeMap = 3, topology = 0; // Gmsh 4.15.2 defaults.
        private string boldness = "0.66", scaling = "0.75";
        private bool? recombinationBeforeQuasiStructured;
        private bool translating;
        internal event EventHandler SettingsChanged;

        internal GmshPreviewPanel(string units, Func<string, string, string> localize,
            Func<string, double?> parseLength, Func<double, string> displayLength)
        {
            this.units = units;
            this.localize = localize;
            this.parseLength = parseLength;
            this.displayLength = displayLength;
            maxSize.Text = displayLength(.1);
            minSize.Text = displayLength(0);
            quality.Text = "0.01"; topologySteps.Text = "5"; iterations.Text = "1";
            smoothingSteps.Text = "1";
            switchOnFailure.Checked = enableRecombination.Checked = reposition.Checked = enableOptimization.Checked = true;

            foreach (ComboBox combo in new[] { algorithm, recombinationMethod, optimizer })
                combo.DropDownStyle = ComboBoxStyle.DropDownList;
            algorithm.Items.AddRange(new object[] { "1 — MeshAdapt", "2 — Automatic", "5 — Delaunay",
                "6 — Frontal-Delaunay", "7 — BAMG", "8 — Frontal-Delaunay for Quads",
                "11 — Quasi-Structured Quad" });
            algorithm.DropDownWidth = TextRenderer.MeasureText("8 — Frontal-Delaunay for Quads", algorithm.Font).Width + 30;
            algorithm.SelectedIndex = 5;
            recombinationMethod.Items.AddRange(new object[] { "0 — Simple", "1 — Blossom",
                "2 — Simple Full-Quad", "3 — Blossom Full-Quad" });
            recombinationMethod.SelectedIndex = 0; // Existing Classic Default preset.
            optimizer.Items.AddRange(new object[] { "Laplace2D", "Relocate2D", "QuadQuasiStructured" });
            optimizer.SelectedIndex = 0;

            AddContents(general, new[] { FullRow(WithGeneralSizes()) });
            AddContents(generation, new[] { Row(algorithmLabel, WithButton(algorithm, algorithmSettings)),
                FullRow(WithSmoothingSteps()), FullRow(switchOnFailure) });
            AddContents(recombination, new[] { FullRow(WithChecks(enableRecombination, reposition)),
                Row(recombinationLabel, recombinationMethod), Row(qualityLabel, quality),
                Row(topologyLabel, topologySteps) });
            AddContents(optimization, new[] { FullRow(WithOptimizationHeader()),
                Row(optimizerLabel, WithButton(optimizer, optimizationSettings)) });

            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid.Dock = DockStyle.Top;
            grid.Padding = Padding.Empty;
            grid.Margin = Padding.Empty;
            grid.ColumnCount = 1;
            grid.RowCount = 4;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < 4; row++)
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.Controls.Add(general, 0, 0);
            grid.Controls.Add(generation, 0, 1);
            grid.Controls.Add(recombination, 0, 2);
            grid.Controls.Add(optimization, 0, 3);
            scroller.Controls.Add(grid);
            algorithm.SelectedIndexChanged += delegate { UpdateEnabled(); };
            enableRecombination.CheckedChanged += delegate { UpdateEnabled(); };
            enableOptimization.CheckedChanged += delegate { UpdateEnabled(); };
            optimizer.SelectedIndexChanged += delegate { UpdateEnabled(); };
            algorithmSettings.Click += delegate { ShowAlgorithmSettings(); };
            optimizationSettings.Click += delegate { ShowOptimizationSettings(); };
            foreach (TextBox text in new[] { maxSize, minSize, quality, topologySteps, iterations })
                text.TextChanged += delegate { NotifySettingsChanged(); };
            foreach (ComboBox combo in new[] { algorithm, recombinationMethod, optimizer })
                combo.SelectedIndexChanged += delegate { NotifySettingsChanged(); };
            smoothingSteps.TextChanged += delegate { NotifySettingsChanged(); };
            foreach (CheckBox check in new[] { favorRegularGrid, switchOnFailure,
                enableRecombination, reposition, enableOptimization })
                check.CheckedChanged += delegate { NotifySettingsChanged(); };
            Localize();
            UpdateEnabled();
        }

        internal Control Content { get { return scroller; } }
        internal void ChangeLengthUnit(string newUnit, Values physicalSizes)
        {
            if (string.Equals(units, newUnit, StringComparison.OrdinalIgnoreCase)) return;
            units = newUnit;
            Restore(physicalSizes);
            Localize();
        }
        internal TextBox MaxSize { get { return maxSize; } }
        internal TextBox MinSize { get { return minSize; } }
        internal CheckBox RegularGrid { get { return favorRegularGrid; } }
        internal bool RegularGridValue { get { return favorRegularGrid.Checked; } }
        internal ComboBox Algorithm { get { return algorithm; } }
        internal ComboBox RecombinationMethod { get { return recombinationMethod; } }
        internal ComboBox Optimizer { get { return optimizer; } }
        internal CheckBox Recombine { get { return enableRecombination; } }
        internal CheckBox Optimize { get { return enableOptimization; } }
        internal Button AlgorithmSettings { get { return algorithmSettings; } }
        internal TextBox SmoothingSteps { get { return smoothingSteps; } }
        internal int SmoothingValue { get { return SmoothingCount(); } }
        internal Button OptimizationSettings { get { return optimizationSettings; } }

        internal void ResetExceptSizes()
        {
            // Preserve the exact text the user typed, including explicit units.
            translating = true;
            try
            {
                recombinationBeforeQuasiStructured = null;
                favorRegularGrid.Checked = false;
                algorithm.SelectedIndex = 5;
                switchOnFailure.Checked = true;
                smoothingSteps.Text = "1";
                enableRecombination.Checked = true;
                recombinationMethod.SelectedIndex = 0;
                quality.Text = "0.01";
                topologySteps.Text = "5";
                reposition.Checked = true;
                enableOptimization.Checked = true;
                optimizer.SelectedIndex = 0;
                iterations.Text = "1";
                refineSteps = "10"; anisoMax = "1E33"; smoothRatio = "1.8";
                sizeMap = 3; topology = 0; boldness = "0.66"; scaling = "0.75";
                UpdateEnabled();
            }
            finally { translating = false; }
            NotifySettingsChanged();
        }

        private void NotifySettingsChanged()
        {
            if (!translating && SettingsChanged != null) SettingsChanged(this, EventArgs.Empty);
        }

        private static Control[] Row(Label label, Control input)
        {
            label.AutoSize = true;
            label.Dock = DockStyle.Fill;
            label.TextAlign = ContentAlignment.MiddleLeft;
            input.Dock = DockStyle.Fill;
            return new[] { (Control)label, input };
        }

        private static Control[] FullRow(Control control)
        {
            control.Dock = DockStyle.Fill;
            return new[] { control, (Control)null };
        }

        private Control WithSmoothingSteps()
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            smoothingLabel.AutoSize = true;
            smoothingLabel.TextAlign = ContentAlignment.MiddleLeft;
            smoothingLabel.Margin = new Padding(1, 4, 12, 0);
            smoothingSteps.Dock = DockStyle.Fill;
            smoothingSteps.Margin = new Padding(0, 1, 0, 1);
            row.Controls.Add(smoothingLabel, 0, 0);
            row.Controls.Add(smoothingSteps, 1, 0);
            return row;
        }

        private Control WithGeneralSizes()
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            foreach (Label label in new[] { maxLabel, minLabel })
            {
                label.AutoSize = true;
                label.TextAlign = ContentAlignment.MiddleLeft;
                label.Margin = new Padding(0, 4, 4, 0);
            }
            foreach (TextBox field in new[] { maxSize, minSize })
            {
                field.Dock = DockStyle.Fill;
                field.MinimumSize = new Size(55, 0);
                field.Margin = new Padding(0, 1, 8, 1);
            }
            row.Controls.Add(maxLabel, 0, 0);
            row.Controls.Add(maxSize, 1, 0);
            row.Controls.Add(minLabel, 2, 0);
            row.Controls.Add(minSize, 3, 0);
            return row;
        }

        private static Control WithChecks(CheckBox first, CheckBox second)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            first.Dock = second.Dock = DockStyle.Fill;
            first.Margin = second.Margin = new Padding(0, 2, 0, 2);
            row.Controls.Add(first, 0, 0);
            row.Controls.Add(second, 1, 0);
            return row;
        }

        private Control WithOptimizationHeader()
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            enableOptimization.AutoSize = true;
            enableOptimization.Margin = new Padding(0, 3, 12, 2);
            iterationsLabel.AutoSize = true;
            iterationsLabel.TextAlign = ContentAlignment.MiddleLeft;
            iterationsLabel.Margin = new Padding(0, 4, 6, 0);
            iterations.Dock = DockStyle.Fill;
            iterations.MinimumSize = new Size(55, 0);
            iterations.Margin = new Padding(0, 1, 0, 1);
            row.Controls.Add(enableOptimization, 0, 0);
            row.Controls.Add(iterationsLabel, 1, 0);
            row.Controls.Add(iterations, 2, 0);
            return row;
        }

        private static Control WithButton(ComboBox combo, Button button)
        {
            var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            combo.Dock = DockStyle.Fill;
            combo.Margin = new Padding(0, 2, 4, 2);
            button.AutoSize = true;
            button.Margin = Padding.Empty;
            row.Controls.Add(combo, 0, 0);
            row.Controls.Add(button, 1, 0);
            return row;
        }

        private static void AddContents(GroupBox box, Control[][] rows)
        {
            box.AutoSize = true;
            box.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            box.Dock = DockStyle.Fill;
            box.Padding = new Padding(7, 7, 7, 2);
            box.Margin = new Padding(2, 1, 2, 1);
            var body = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = rows.Length,
                Margin = Padding.Empty };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (Control[] row in rows)
            {
                int index = body.RowStyles.Count;
                body.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                row[0].Margin = new Padding(1, 2, 5, 2);
                body.Controls.Add(row[0], 0, index);
                if (row[1] != null)
                {
                    row[1].Margin = new Padding(0, 1, 0, 1);
                    body.Controls.Add(row[1], 1, index);
                }
                else body.SetColumnSpan(row[0], 2);
            }
            box.Controls.Add(body);
        }

        private void UpdateEnabled()
        {
            if (algorithm.SelectedIndex < 0 || optimizer.SelectedIndex < 0) return;
            bool quasi = AlgorithmCode == 11;
            if (quasi && !recombinationBeforeQuasiStructured.HasValue)
            {
                recombinationBeforeQuasiStructured = enableRecombination.Checked;
                enableRecombination.Checked = false;
            }
            else if (!quasi && recombinationBeforeQuasiStructured.HasValue)
            {
                enableRecombination.Checked = recombinationBeforeQuasiStructured.Value;
                recombinationBeforeQuasiStructured = null;
            }
            enableRecombination.Enabled = !quasi;
            foreach (Control control in new Control[] { recombinationMethod, quality, topologySteps, reposition,
                recombinationLabel, qualityLabel, topologyLabel })
                control.Enabled = !quasi && enableRecombination.Checked;
            algorithmSettings.Enabled = AlgorithmCode == 1 || AlgorithmCode == 7;
            optimizer.Enabled = iterations.Enabled = enableOptimization.Checked;
            optimizerLabel.Enabled = iterationsLabel.Enabled = enableOptimization.Checked;
            optimizationSettings.Enabled = enableOptimization.Checked && optimizer.SelectedIndex == 2;
        }

        private int AlgorithmCode { get { return new[] { 1, 2, 5, 6, 7, 8, 11 }[algorithm.SelectedIndex]; } }

        internal void Localize()
        {
            translating = true;
            try
            {
                TranslateChoices(algorithm, new[] {
                    localize("1 — MeshAdapt", "1 — MeshAdapt"),
                    localize("2 — Automatic", "2 — Automatico"),
                    localize("5 — Delaunay", "5 — Delaunay"),
                    localize("6 — Frontal-Delaunay", "6 — Frontal-Delaunay"),
                    localize("7 — BAMG", "7 — BAMG"),
                    localize("8 — Frontal-Delaunay for Quads", "8 — Frontal-Delaunay per quad"),
                    localize("11 — Quasi-Structured Quad", "11 — Quad quasi strutturati") });
                TranslateChoices(recombinationMethod, new[] {
                    localize("0 — Simple", "0 — Semplice"),
                    localize("1 — Blossom", "1 — Blossom"),
                    localize("2 — Simple Full-Quad", "2 — Semplice solo quad"),
                    localize("3 — Blossom Full-Quad", "3 — Blossom solo quad") });
            }
            finally { translating = false; }
            general.Text = localize("General mesh parameters", "Parametri generali della mesh");
            generation.Text = localize("Mesh Generation", "Generazione mesh");
            recombination.Text = localize("Triangle Recombination", "Ricomposizione triangoli");
            optimization.Text = localize("Mesh Optimization", "Ottimizzazione mesh");
            maxLabel.Text = localize("Max Mesh Size", "Dimensione max") + " (" + units + ")";
            minLabel.Text = localize("Min Mesh Size", "Dimensione min") + " (" + units + ")";
            favorRegularGrid.Text = localize("Prefer regular (grid-aligned) mesh when possible",
                "Preferisci mesh regolari allineate alla griglia (se possibile)");
            algorithmLabel.Text = localize("Mesh Algorithm", "Algoritmo di mesh");
            smoothingLabel.Text = localize("Smoothing steps", "Passaggi di smoothing");
            recombinationLabel.Text = localize("Recombination Method", "Metodo di ricomposizione");
            qualityLabel.Text = localize("Minimum Quad Quality", "Qualità minima quad");
            topologyLabel.Text = localize("Optimization Steps", "Passaggi di ottimizzazione");
            optimizerLabel.Text = localize("Optimization Method", "Metodo di ottimizzazione");
            iterationsLabel.Text = localize("Optimization Iterations", "Iterazioni");
            switchOnFailure.Text = localize("Automatic Algorithm Switch in Case of Failure",
                "Cambio automatico dell'algoritmo in caso di errore");
            enableRecombination.Text = localize("Enable Recombination", "Attiva ricomposizione");
            reposition.Text = localize("Allow Node Repositioning", "Riposiziona nodi");
            enableOptimization.Text = localize("Optimize Mesh", "Ottimizza mesh");
            algorithmSettings.Text = optimizationSettings.Text = localize("Settings...", "Impostazioni...");
            UpdateEnabled();
        }

        private static void TranslateChoices(ComboBox control, string[] labels)
        {
            int selected = control.SelectedIndex;
            control.Items.Clear();
            control.Items.AddRange(labels);
            control.SelectedIndex = Math.Max(0, selected);
            control.DropDownWidth = 30;
            foreach (string label in labels)
                control.DropDownWidth = Math.Max(control.DropDownWidth,
                    TextRenderer.MeasureText(label, control.Font).Width + 30);
        }

        private double Length(string text, bool allowZero, string name)
        {
            if (allowZero && Regex.IsMatch(text ?? "", @"^\s*0+(?:[.,]0+)?\s*(?:mm|cm|m|ft|in)?\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return 0;
            double? value = parseLength(text);
            if (!value.HasValue || (allowZero && value.Value <= 0))
                throw new InvalidOperationException(localize("Invalid value for ", "Valore non valido per ") + name);
            return value.Value;
        }

        private double Number(string text, string name, double minimum, bool inclusive = false, double maximum = double.PositiveInfinity)
        {
            double value;
            if (!double.TryParse((text ?? "").Trim().Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out value) || double.IsNaN(value) || double.IsInfinity(value) ||
                (inclusive ? value < minimum : value <= minimum) || value > maximum)
                throw new InvalidOperationException(localize("Invalid value for ", "Valore non valido per ") + name);
            return value;
        }

        private int PositiveInteger(string text, string name)
        {
            int value;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value <= 0)
                throw new InvalidOperationException(localize("Invalid value for ", "Valore non valido per ") + name);
            return value;
        }

        private int NonnegativeInteger(string text, string name)
        {
            int value;
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < 0)
                throw new InvalidOperationException(localize("Invalid value for ", "Valore non valido per ") + name);
            return value;
        }

        private int SmoothingCount()
        {
            int value = NonnegativeInteger(smoothingSteps.Text, smoothingLabel.Text);
            if (value > 100)
                throw new InvalidOperationException(localize("Invalid value for ", "Valore non valido per ") + smoothingLabel.Text);
            return value;
        }

        internal double ReadMaxSize()
        {
            return Length(maxSize.Text, false, maxLabel.Text);
        }

        internal void CheckValues()
        {
            double maximum = ReadMaxSize();
            double minimum = Length(minSize.Text, true, minLabel.Text);
            if (minimum >= maximum)
                throw new InvalidOperationException(localize("Min Mesh Size must be less than Max Mesh Size.",
                    "La dimensione minima deve essere inferiore alla massima."));
            SmoothingCount();
            if (AlgorithmCode == 1) NonnegativeInteger(refineSteps, localize("Refinement Steps", "Passaggi di raffinamento"));
            if (AlgorithmCode == 7)
            {
                Number(anisoMax, localize("Maximum Anisotropy", "Anisotropia massima"), 1, true);
                Number(smoothRatio, localize("Smooth Ratio", "Rapporto di regolarizzazione"), 0);
            }
            if (enableRecombination.Checked)
            {
                Number(quality.Text, qualityLabel.Text, 0, true, 1);
                NonnegativeInteger(topologySteps.Text, topologyLabel.Text);
            }
            if (enableOptimization.Checked)
            {
                PositiveInteger(iterations.Text, iterationsLabel.Text);
                if (optimizer.SelectedIndex == 2)
                {
                    Number(boldness, localize("Remeshing Boldness", "Intensità di remesh"), 0, true, 1);
                    Number(scaling, localize("Scaling on Triangulation", "Scala sulla triangolazione"), 0);
                }
            }
        }

        internal void ApplyOptions(Dictionary<string, double> options)
        {
            CheckValues();
            foreach (string name in ControlledOptions) options.Remove(name);
            options["Mesh.MeshSizeMin"] = Length(minSize.Text, true, minLabel.Text);
            options["Mesh.MeshSizeFactor"] = 1;
            options["Mesh.MeshSizeFromPoints"] = 0;
            options["Mesh.MeshSizeExtendFromBoundary"] = 0;
            options["Mesh.MeshSizeFromCurvature"] = 0;
            options["Mesh.MeshSizeFromParametricPoints"] = 0;
            options["Mesh.SubdivisionAlgorithm"] = 0;
            options["Mesh.AlgorithmSwitchOnFailure"] = switchOnFailure.Checked ? 1 : 0;
            if (AlgorithmCode == 1) options["Mesh.RefineSteps"] = NonnegativeInteger(refineSteps, "Refinement Steps");
            if (AlgorithmCode == 7)
            {
                options["Mesh.AnisoMax"] = Number(anisoMax, "Maximum Anisotropy", 1, true);
                options["Mesh.SmoothRatio"] = Number(smoothRatio, "Smooth Ratio", 0);
            }
            if (enableRecombination.Checked)
            {
                options["Mesh.RecombineMinimumQuality"] = Number(quality.Text, qualityLabel.Text, 0, true, 1);
                options["Mesh.RecombineOptimizeTopology"] = NonnegativeInteger(topologySteps.Text, topologyLabel.Text);
                options["Mesh.RecombineNodeRepositioning"] = reposition.Checked ? 1 : 0;
            }
            if (enableOptimization.Checked && optimizer.SelectedIndex == 2)
            {
                options["Mesh.QuadqsSizemapMethod"] = sizeMap;
                options["Mesh.QuadqsTopologyOptimizationMethods"] = topology;
                options["Mesh.QuadqsRemeshingBoldness"] = Number(boldness, "Remeshing Boldness", 0, true, 1);
                options["Mesh.QuadqsScalingOnTriangulation"] = Number(scaling, "Scaling on Triangulation", 0);
            }
        }

        internal int AlgorithmValue { get { return AlgorithmCode; } }
        internal bool RecombineValue { get { return enableRecombination.Checked; } }
        internal int RecombinationValue { get { return recombinationMethod.SelectedIndex; } }
        internal bool OptimizeValue { get { return enableOptimization.Checked; } }
        internal string OptimizerValue { get { return optimizer.SelectedItem as string; } }
        internal int IterationsValue { get { return enableOptimization.Checked ?
            PositiveInteger(iterations.Text, iterationsLabel.Text) : 1; } }

        internal Values Capture(double metersPerUnit)
        {
            double? maximum = parseLength(maxSize.Text), minimum = parseLength(minSize.Text);
            int parsedSmoothing;
            int? savedSmoothing = int.TryParse(smoothingSteps.Text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out parsedSmoothing) && parsedSmoothing >= 0 && parsedSmoothing <= 100
                ? (int?)parsedSmoothing : null;
            return new Values { MaxMeters = maximum.GetValueOrDefault() * metersPerUnit,
                MinMeters = minimum.GetValueOrDefault() * metersPerUnit,
                MaxText = maxSize.Text, MinText = minSize.Text,
                FavorRegularGrid = favorRegularGrid.Checked,
                Algorithm = algorithm.SelectedIndex, SmoothingSteps = savedSmoothing,
                RefineSteps = refineSteps, AnisoMax = anisoMax,
                SmoothRatio = smoothRatio, SwitchOnFailure = switchOnFailure.Checked,
                Recombine = enableRecombination.Checked, Recombination = recombinationMethod.SelectedIndex,
                MinimumQuality = quality.Text, TopologySteps = topologySteps.Text,
                NodeRepositioning = reposition.Checked, Optimize = enableOptimization.Checked,
                Optimizer = optimizer.SelectedIndex, SizeMap = sizeMap, Topology = topology,
                Boldness = boldness, Scaling = scaling, Iterations = iterations.Text };
        }

        private string RestoreLengthText(double meters, string entered)
        {
            if (meters <= 0) return entered ?? displayLength(0);
            string converted = displayLength(meters);
            double? oldValue = entered == null ? null : parseLength(entered);
            double? newValue = parseLength(converted);
            // Keep explicit units and spelling when they still represent the
            // same physical size. Convert bare values from another SAP unit.
            if (oldValue.HasValue && newValue.HasValue &&
                Math.Abs(oldValue.Value - newValue.Value) <= 1e-8 * Math.Max(1, Math.Abs(newValue.Value)))
                return entered;
            return converted;
        }

        internal void Restore(Values value, bool legacyAlgorithmIndices = false)
        {
            if (value == null) return;
            maxSize.Text = RestoreLengthText(value.MaxMeters, value.MaxText);
            minSize.Text = RestoreLengthText(value.MinMeters, value.MinText);
            favorRegularGrid.Checked = value.FavorRegularGrid;
            if (value.SmoothingSteps.HasValue && value.SmoothingSteps.Value >= 0 && value.SmoothingSteps.Value <= 100)
                smoothingSteps.Text = value.SmoothingSteps.Value.ToString(CultureInfo.InvariantCulture);
            if (value.RefineSteps != null) refineSteps = value.RefineSteps;
            if (value.AnisoMax != null) anisoMax = value.AnisoMax;
            if (value.SmoothRatio != null) smoothRatio = value.SmoothRatio;
            if (value.MinimumQuality != null) quality.Text = value.MinimumQuality;
            if (value.TopologySteps != null) topologySteps.Text = value.TopologySteps;
            if (value.Boldness != null) boldness = value.Boldness;
            if (value.Scaling != null) scaling = value.Scaling;
            if (value.Iterations != null) iterations.Text = value.Iterations;
            sizeMap = value.SizeMap; topology = value.Topology;
            int algorithmIndex = legacyAlgorithmIndices && value.Algorithm == 6 ? 5 :
                legacyAlgorithmIndices && value.Algorithm == 7 ? 6 : value.Algorithm;
            if (algorithmIndex >= 0 && algorithmIndex < algorithm.Items.Count) algorithm.SelectedIndex = algorithmIndex;
            switchOnFailure.Checked = value.SwitchOnFailure;
            if (value.Recombination >= 0 && value.Recombination < recombinationMethod.Items.Count)
                recombinationMethod.SelectedIndex = value.Recombination;
            if (value.Optimizer >= 0 && value.Optimizer < optimizer.Items.Count) optimizer.SelectedIndex = value.Optimizer;
            reposition.Checked = value.NodeRepositioning;
            enableOptimization.Checked = value.Optimize;
            if (AlgorithmCode != 11) enableRecombination.Checked = value.Recombine;
            else recombinationBeforeQuasiStructured = value.Recombine;
            UpdateEnabled();
        }

        private void ShowAlgorithmSettings()
        {
            bool meshAdapt = AlgorithmCode == 1;
            string[] names = meshAdapt ? new[] { localize("Refinement Steps", "Passaggi di raffinamento") } :
                new[] { localize("Maximum Anisotropy", "Anisotropia massima"),
                    localize("Smooth Ratio", "Rapporto di regolarizzazione") };
            string[] values = meshAdapt ? new[] { refineSteps } : new[] { anisoMax, smoothRatio };
            string[] changed = ShowSettings(localize("Mesh Algorithm Settings", "Impostazioni algoritmo di mesh"), names, values,
                fields => { if (meshAdapt) NonnegativeInteger(fields[0], names[0]);
                    else { Number(fields[0], names[0], 1, true); Number(fields[1], names[1], 0); } });
            if (changed == null) return;
            if (meshAdapt) refineSteps = changed[0];
            else { anisoMax = changed[0]; smoothRatio = changed[1]; }
            NotifySettingsChanged();
        }

        private void ShowOptimizationSettings()
        {
            string[] names = { localize("Size Map Method", "Metodo mappa delle dimensioni"),
                localize("Mesh Topology Optimization", "Ottimizzazione topologia della mesh"),
                localize("Remeshing Boldness", "Intensità di remesh"),
                localize("Scaling on Triangulation", "Scala sulla triangolazione") };
            string[][] choices = {
                new[] { localize("0 — Default", "0 — Predefinito"),
                    localize("1 — Cross Field", "1 — Campo incrociato"),
                    localize("2 — Cross Field with CAD Feature Adaptation", "2 — Campo incrociato con adattamento CAD"),
                    localize("3 — Background Mesh", "3 — Mesh di sfondo"),
                    localize("4 — Cross Field with CAD Feature Adaptation and Background Mesh", "4 — Campo incrociato, CAD e mesh di sfondo") },
                new[] { localize("0 — All", "0 — Tutti"),
                    localize("100 — Pattern-based CAD faces", "100 — Pattern su facce CAD"),
                    localize("10 — Disk quadrangulation remeshing", "10 — Quadrangolazione di dischi"),
                    localize("1 — Cavity remeshing", "1 — Remesh delle cavità"),
                    localize("110 — Pattern-Based + Disk quadrangulation", "110 — Pattern + dischi"),
                    localize("101 — Pattern-Based + Cavity remeshing", "101 — Pattern + cavità"),
                    localize("11 — Disk quadrangulation + Cavity remeshing", "11 — Dischi + cavità") }
            };
            using (var dialog = SettingsDialog(localize("Optimization Algorithm Settings", "Impostazioni algoritmo di ottimizzazione"), 4))
            {
                var body = (TableLayoutPanel)dialog.Tag;
                ComboBox map = AddChoice(body, names[0], choices[0], sizeMap, 0);
                int topologyIndex = Array.IndexOf(new[] { 0, 100, 10, 1, 110, 101, 11 }, topology);
                ComboBox methods = AddChoice(body, names[1], choices[1], Math.Max(0, topologyIndex), 1);
                TextBox bold = AddText(body, names[2], boldness, 2);
                TextBox scale = AddText(body, names[3], scaling, 3);
                dialog.FormClosing += delegate(object sender, FormClosingEventArgs e) {
                    if (dialog.DialogResult != DialogResult.OK) return;
                    try { Number(bold.Text, names[2], 0, true, 1); Number(scale.Text, names[3], 0); }
                    catch (InvalidOperationException error) {
                        MessageBox.Show(dialog, error.Message, dialog.Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        e.Cancel = true;
                    }
                };
                if (cPlugin.ShowHostModalFromPanel(dialog,
                    () => dialog.ShowDialog(scroller.FindForm())) != DialogResult.OK) return;
                sizeMap = map.SelectedIndex;
                topology = new[] { 0, 100, 10, 1, 110, 101, 11 }[methods.SelectedIndex];
                boldness = bold.Text; scaling = scale.Text;
                NotifySettingsChanged();
            }
        }

        private string[] ShowSettings(string title, string[] names, string[] values, Action<string[]> validate)
        {
            using (var dialog = SettingsDialog(title, names.Length))
            {
                var body = (TableLayoutPanel)dialog.Tag;
                var inputs = new TextBox[names.Length];
                for (int index = 0; index < names.Length; index++)
                    inputs[index] = AddText(body, names[index], values[index], index);
                dialog.FormClosing += delegate(object sender, FormClosingEventArgs e) {
                    if (dialog.DialogResult != DialogResult.OK) return;
                    try { validate(Array.ConvertAll(inputs, field => field.Text)); }
                    catch (InvalidOperationException error) {
                        MessageBox.Show(dialog, error.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        e.Cancel = true;
                    }
                };
                return cPlugin.ShowHostModalFromPanel(dialog,
                    () => dialog.ShowDialog(scroller.FindForm())) == DialogResult.OK
                    ? Array.ConvertAll(inputs, field => field.Text) : null;
            }
        }

        private Form SettingsDialog(string title, int rows)
        {
            var dialog = new Form { Text = title, Font = scroller.FindForm().Font, BackColor = Color.White,
                ForeColor = scroller.FindForm().ForeColor, FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false,
                ClientSize = new Size(550, 80 + 45 * rows), ShowInTaskbar = false };
            var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16),
                ColumnCount = 2, RowCount = rows + 1 };
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 53));
            body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47));
            for (int row = 0; row < rows; row++) body.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / rows));
            body.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            var actions = new FlowLayoutPanel { Dock = DockStyle.Right, AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
            var ok = new Button { Text = localize("OK", "OK"), AutoSize = true, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = localize("Cancel", "Annulla"), AutoSize = true, DialogResult = DialogResult.Cancel };
            actions.Controls.Add(cancel); actions.Controls.Add(ok);
            body.Controls.Add(actions, 0, rows);
            body.SetColumnSpan(actions, 2);
            dialog.AcceptButton = ok; dialog.CancelButton = cancel;
            dialog.Controls.Add(body);
            dialog.Tag = body;
            return dialog;
        }

        private static TextBox AddText(TableLayoutPanel body, string name, string value, int row)
        {
            body.Controls.Add(new Label { Text = name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            var field = new TextBox { Text = value, Dock = DockStyle.Fill };
            body.Controls.Add(field, 1, row);
            return field;
        }

        private static ComboBox AddChoice(TableLayoutPanel body, string name, string[] choices, int selected, int row)
        {
            body.Controls.Add(new Label { Text = name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            combo.Items.AddRange(choices);
            combo.SelectedIndex = selected;
            foreach (string choice in choices)
                combo.DropDownWidth = Math.Max(combo.DropDownWidth,
                    TextRenderer.MeasureText(choice, combo.Font).Width + 30);
            body.Controls.Add(combo, 1, row);
            return combo;
        }
    }
}
