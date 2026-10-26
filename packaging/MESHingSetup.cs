// Copyright (C) 2026 Leonardo Bandini
// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class MESHingSetup
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupWindow());
    }
}

internal sealed class SetupWindow : Form
{
    private const string MicrosoftGuide = "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist";
    private static readonly string[] ItalianSteps = {
        "Verifica delle applicazioni aperte", "Controllo del Visual C++ x64",
        "Download e installazione Microsoft (se necessari)", "Verifica dei componenti MESHing",
        "Preparazione dei file MESHing", "Sostituzione della versione precedente (se presente)",
        "Registrazione del plug-in", "Aggiornamento dei menu SAP2000 e CSiBridge",
        "Installazione completata"
    };
    private static readonly string[] EnglishSteps = {
        "Check for open applications", "Check Visual C++ x64",
        "Microsoft download and setup (if needed)", "Verify MESHing package",
        "Prepare MESHing files", "Replace a previous version (if present)",
        "Register the plug-in", "Update SAP2000 and CSiBridge menus",
        "Installation complete"
    };

    private readonly ComboBox languagePicker = new ComboBox();
    private readonly Panel masthead = new Panel();
    private readonly Panel registrationPanel = new Panel();
    private readonly Panel licensePanel = new Panel();
    private readonly Label heading = new Label();
    private readonly Label subtitle = new Label();
    private readonly Label introduction = new Label();
    private readonly Label registrationNotice = new Label();
    private readonly Label licenseNotice = new Label();
    private readonly Label languageLabel = new Label();
    private readonly Label status = new Label();
    private readonly Label[] stages = new Label[9];
    private readonly int[] stageState = new int[9];
    private readonly ProgressBar progress = new ProgressBar();
    private readonly TextBox details = new TextBox();
    private readonly Button install = new Button();
    private readonly Button close = new Button();
    private readonly Button showDetails = new Button();
    private readonly LinkLabel microsoftLink = new LinkLabel();
    private readonly LinkLabel licenseLink = new LinkLabel();
    private readonly LinkLabel gpl3Link = new LinkLabel();
    private readonly LinkLabel noticesLink = new LinkLabel();
    private readonly LinkLabel sourceLink = new LinkLabel();
    private bool running;
    private bool waitingForHosts;
    private bool completed;
    private bool failed;
    private bool downloaded;
    private string blockedHosts = "";
    private string currentPhase = "ready";
    private string failureDetails = "";

    internal SetupWindow()
    {
        Text = "MESHing";
        using (var installerIcon = Icon.ExtractAssociatedIcon(typeof(SetupWindow).Assembly.Location))
            Icon = (Icon)installerIcon.Clone();
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(720, 710);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        Font = new Font("Segoe UI", 9.5f);
        BackColor = Color.FromArgb(248, 250, 252);

        masthead.SetBounds(0, 0, 720, 139);
        masthead.BackColor = Color.FromArgb(15, 23, 42);
        heading.SetBounds(28, 14, 400, 47);
        heading.Font = new Font("Segoe UI", 25f, FontStyle.Bold);
        heading.ForeColor = Color.White;
        subtitle.SetBounds(29, 78, 600, 25);
        subtitle.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
        subtitle.ForeColor = Color.FromArgb(94, 234, 212);
        introduction.SetBounds(29, 91, 665, 44);
        introduction.ForeColor = Color.FromArgb(226, 232, 240);
        introduction.AutoSize = false;
        introduction.Visible = false;
        languageLabel.SetBounds(468, 20, 116, 25);
        languageLabel.ForeColor = Color.White;
        languagePicker.SetBounds(585, 17, 105, 28);
        languagePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        languagePicker.Items.AddRange(new object[] { "Italiano", "English" });
        languagePicker.SelectedIndexChanged += (sender, args) => Render();

        masthead.Controls.AddRange(new Control[] { heading, subtitle, introduction, languageLabel, languagePicker });
        registrationPanel.SetBounds(24, 152, 672, 67);
        registrationPanel.BackColor = Color.FromArgb(254, 243, 199);
        registrationNotice.SetBounds(14, 11, 644, 51);
        registrationNotice.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        registrationPanel.Controls.Add(registrationNotice);
        licensePanel.SetBounds(24, 228, 672, 65);
        licensePanel.BackColor = Color.FromArgb(224, 242, 254);
        licenseNotice.SetBounds(14, 8, 644, 53);
        licensePanel.Controls.Add(licenseNotice);

        for (int index = 0; index < stages.Length; index++)
        {
            stages[index] = new Label();
            stages[index].SetBounds(29, 306 + index * 27, 660, 25);
            stages[index].AutoEllipsis = true;
            Controls.Add(stages[index]);
        }
        progress.SetBounds(26, 551, 668, 17);
        progress.Maximum = 9;
        status.SetBounds(26, 577, 668, 40);
        status.AutoEllipsis = true;
        microsoftLink.SetBounds(26, 624, 218, 28);
        microsoftLink.LinkClicked += (sender, args) =>
            Process.Start(new ProcessStartInfo(MicrosoftGuide) { UseShellExecute = true });
        licenseLink.SetBounds(250, 624, 90, 28);
        licenseLink.LinkClicked += (sender, args) => OpenIncludedText("LICENSE-APPLICATION.md");
        gpl3Link.SetBounds(344, 624, 76, 28);
        gpl3Link.LinkClicked += (sender, args) => OpenIncludedText("GPL-3.0.txt");
        noticesLink.SetBounds(422, 624, 95, 28);
        noticesLink.LinkClicked += (sender, args) => OpenIncludedText("THIRD_PARTY_NOTICES.md");
        sourceLink.SetBounds(520, 624, 95, 28);
        sourceLink.LinkClicked += (sender, args) => OpenIncludedText("SOURCE.md");
        showDetails.SetBounds(389, 663, 88, 33);
        showDetails.Click += (sender, args) => ToggleDetails();
        install.SetBounds(488, 663, 102, 33);
        install.Click += async (sender, args) => await StartInstallation();
        close.SetBounds(600, 663, 94, 33);
        close.Click += (sender, args) =>
        {
            if (waitingForHosts) CancelHostWait();
            else Close();
        };
        details.SetBounds(26, 707, 668, 170);
        details.ReadOnly = true;
        details.Multiline = true;
        details.ScrollBars = ScrollBars.Vertical;
        details.Visible = false;
        details.Font = new Font("Consolas", 8.5f);
        install.FlatStyle = FlatStyle.Flat;
        install.FlatAppearance.BorderSize = 0;
        install.BackColor = Color.FromArgb(30, 64, 175);
        install.ForeColor = Color.White;
        foreach (Button secondary in new[] { showDetails, close })
        {
            secondary.FlatStyle = FlatStyle.Flat;
            secondary.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
            secondary.BackColor = Color.White;
        }
        foreach (LinkLabel link in new[] { microsoftLink, licenseLink, gpl3Link, noticesLink, sourceLink })
        {
            link.LinkColor = Color.FromArgb(30, 64, 175);
            link.ActiveLinkColor = Color.FromArgb(15, 118, 110);
        }

        Controls.AddRange(new Control[] { masthead, registrationPanel, licensePanel, progress, status,
            microsoftLink, licenseLink, gpl3Link, noticesLink, sourceLink,
            showDetails, install, close, details });
        languagePicker.SelectedIndex = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName == "it" ? 0 : 1;
        Render();
    }

    private bool Italian { get { return languagePicker.SelectedIndex == 0; } }
    private string L(string italian, string english) { return Italian ? italian : english; }

    private void Render()
    {
        heading.Text = "MESHing";
        subtitle.Text = L("Generazione avanzata di mesh 2D", "Advanced 2D mesh generation");
        introduction.Text = string.Empty;
        registrationNotice.Text = L("L'installazione registra MESHing nei menu Strumenti di SAP2000 e CSiBridge. " +
            "Chiudere tutte le sessioni aperte: la procedura resterà bloccata finché i programmi non saranno chiusi.",
            "Setup registers MESHing in the Tools menus of SAP2000 and CSiBridge. " +
            "Close all open sessions: installation cannot continue until the applications are closed.");
        licenseNotice.Text = L("Autore: Leonardo Bandini - CSi Italia · MESHing: GPL-2.0-or-later · backend: GPLv3." +
            Environment.NewLine + "Gmsh 4.15.2: GPL-2.0-or-later con eccezione. Il runtime Microsoft è separato.",
            "Author: Leonardo Bandini - CSi Italia · MESHing: GPL-2.0-or-later · backend: GPLv3." +
            Environment.NewLine + "Gmsh 4.15.2: GPL-2.0-or-later with exception. Microsoft's runtime is separate.");
        languageLabel.Text = "Lingua / Language";
        string[] names = Italian ? ItalianSteps : EnglishSteps;
        string[] states = Italian
            ? new[] { "In attesa", "In corso", "Completato", "Non necessario", "Interrotto" }
            : new[] { "Pending", "In progress", "Complete", "Not needed", "Stopped" };
        for (int index = 0; index < stages.Length; index++)
        {
            stages[index].Text = (index + 1) + ". " + names[index] + " — " + states[stageState[index]];
            stages[index].ForeColor = stageState[index] == 4 ? Color.Firebrick :
                stageState[index] == 1 ? Color.FromArgb(30, 64, 175) :
                stageState[index] == 2 ? Color.FromArgb(22, 101, 52) : Color.FromArgb(71, 85, 105);
        }
        microsoftLink.Text = L("Prerequisito Microsoft", "Microsoft prerequisite");
        licenseLink.Text = L("Licenze", "Licenses");
        gpl3Link.Text = "GPLv3";
        noticesLink.Text = L("Terze parti", "Third parties");
        sourceLink.Text = L("Sorgenti", "Sources");
        showDetails.Text = L("Dettagli", "Details");
        install.Text = failed ? L("Riprova", "Retry") : L("Installa", "Install");
        close.Text = waitingForHosts ? L("Annulla", "Cancel") : L("Chiudi", "Close");
        status.Text = waitingForHosts ? L("CHIUDERE LE SESSIONI: " + blockedHosts + ". In attesa; non verrà chiuso alcun modello.",
                "CLOSE THESE SESSIONS: " + blockedHosts + ". Waiting; no model will be closed automatically.") :
            failed ? L("Installazione interrotta. Consultare i dettagli e riprovare.",
                "Installation stopped. Review the details and retry.") :
            completed ? L("MESHing installato. Riaprire SAP2000 o CSiBridge per utilizzarlo.",
                "MESHing installed. Restart SAP2000 or CSiBridge to use it.") :
            currentPhase == "microsoft-setup" ? L("Completare il setup Microsoft e l'eventuale richiesta UAC.",
                "Complete Microsoft's setup and any UAC prompt.") :
            currentPhase == "download" ? L("Download del prerequisito dal sito Microsoft in corso...",
                "Downloading the prerequisite directly from Microsoft...") :
            currentPhase == "publisher-check" ? L("Verifica della firma digitale Microsoft...",
                "Verifying the Microsoft digital signature...") :
            running ? L("Operazione in corso: " + names[ActiveStage()] + ".",
                "In progress: " + names[ActiveStage()] + ".") :
            L("Pronto. È possibile cambiare lingua prima o durante l'installazione.",
                "Ready. You can change languages before or during installation.");
        install.Enabled = !running && !completed;
        close.Enabled = !running || waitingForHosts;
        showDetails.Enabled = !string.IsNullOrEmpty(details.Text);
    }

    private int ActiveStage()
    {
        for (int index = 0; index < stageState.Length; index++)
            if (stageState[index] == 1) return index;
        return 0;
    }

    private void ToggleDetails()
    {
        details.Visible = !details.Visible;
        ClientSize = new Size(ClientSize.Width, details.Visible ? 890 : 710);
    }

    private void OpenIncludedText(string file)
    {
        string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, file);
        if (!File.Exists(path))
        {
            MessageBox.Show(this, L("Documento non disponibile nel pacchetto.", "Document missing from the package."),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.Windows), "notepad.exe"), "\"" + path + "\"") { UseShellExecute = true });
    }

    private static string[] OpenHosts()
    {
        var found = new System.Collections.Generic.List<string>();
        foreach (string name in new[] { "SAP2000", "CSiBridge", "ETABS" })
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process) found.Add(name + " (PID " + process.Id + ")");
            }
        return found.ToArray();
    }

    private void BlockOnHosts(string[] names)
    {
        blockedHosts = string.Join(", ", names);
        waitingForHosts = true;
        running = true;
        currentPhase = "waiting-hosts";
        stageState[0] = 1;
        Render();
    }

    private void CancelHostWait()
    {
        waitingForHosts = false;
        running = false;
        blockedHosts = "";
        stageState[0] = 0;
        currentPhase = "ready";
        Render();
    }

    private async Task StartInstallation()
    {
        if (running || completed) return;
        for (int index = 0; index < stageState.Length; index++) stageState[index] = 0;
        currentPhase = "ready";
        failureDetails = "";
        details.Clear();
        downloaded = false;
        failed = false;
        running = true;
        progress.Value = 0;
        Render();
        string[] open = OpenHosts();
        if (open.Length > 0)
        {
            BlockOnHosts(open);
            MessageBox.Show(this,
                L("Chiudere SAP2000 e CSiBridge (e ETABS, se aperto) prima di continuare. " +
                    "La procedura attenderà che tutte le sessioni rilevate siano chiuse; nessun modello sarà chiuso automaticamente.",
                    "Close SAP2000 and CSiBridge (and ETABS if open) before continuing. " +
                    "Setup will wait for all detected sessions to close; it will not close any model automatically."),
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            while (waitingForHosts && (open = OpenHosts()).Length > 0)
            {
                blockedHosts = string.Join(", ", open);
                Render();
                await Task.Delay(800);
                if (IsDisposed) return;
            }
            if (!waitingForHosts) return;
            waitingForHosts = false;
        }
        int code = await Task.Run(() => RunInstaller());
        running = false;
        if (code != 0)
        {
            failed = true;
            stageState[ActiveStage()] = 4;
            if (!string.IsNullOrEmpty(failureDetails)) details.AppendText(failureDetails + Environment.NewLine);
        }
        else
        {
            completed = true;
            progress.Value = 9;
        }
        Render();
    }

    private int RunInstaller()
    {
        string directory = AppDomain.CurrentDomain.BaseDirectory;
        string program = Path.Combine(directory, "SQMPluginInstaller.exe");
        try
        {
            if (!File.Exists(program)) throw new FileNotFoundException("Missing installer engine", program);
            using (Process process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(program, "install \"" + directory.TrimEnd('\\') + "\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                process.OutputDataReceived += (sender, args) => ReceiveLine(args.Data, false);
                process.ErrorDataReceived += (sender, args) => ReceiveLine(args.Data, true);
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                return process.ExitCode;
            }
        }
        catch (Exception error)
        {
            failureDetails = error.ToString();
            return 1;
        }
    }

    private void ReceiveLine(string line, bool error)
    {
        if (line == null || IsDisposed) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed) return;
                if (line.StartsWith("FEMESH_PHASE=", StringComparison.Ordinal))
                    SetPhase(line.Substring("FEMESH_PHASE=".Length));
                else
                {
                    details.AppendText(line + Environment.NewLine);
                    if (error) failureDetails = line;
                    Render();
                }
            }));
        }
        catch (InvalidOperationException) { }
    }

    private void SetPhase(string phase)
    {
        currentPhase = phase;
        int next;
        switch (phase)
        {
            case "hosts": next = 0; break;
            case "prerequisite": next = 1; break;
            case "download":
                downloaded = true;
                next = 2;
                break;
            case "publisher-check": case "microsoft-setup": next = 2; break;
            case "prerequisite-ready":
                stageState[1] = 2;
                stageState[2] = downloaded ? 2 : 3;
                progress.Value = 3;
                Render();
                return;
            case "verification": next = 3; break;
            case "copy": next = 4; break;
            case "previous-version": next = 5; break;
            case "previous-none":
                stageState[4] = 2;
                stageState[5] = 3;
                progress.Value = 6;
                Render();
                return;
            case "already-installed":
                stageState[3] = 2;
                stageState[4] = 3;
                stageState[5] = 3;
                progress.Value = 6;
                Render();
                return;
            case "registration": next = 6; break;
            case "menus": next = 7; break;
            case "done": next = 8; break;
            default: return;
        }
        for (int index = 0; index < next; index++)
            if (stageState[index] == 0 || stageState[index] == 1) stageState[index] = 2;
        stageState[next] = phase == "done" ? 2 : 1;
        progress.Value = phase == "done" ? 9 : next;
        Render();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (waitingForHosts)
        {
            waitingForHosts = false;
            running = false;
        }
        else if (running)
        {
            e.Cancel = true;
            MessageBox.Show(this, L("Attendere il completamento o chiudere il setup Microsoft per annullare.",
                "Wait for completion or close Microsoft's setup to cancel."), Text, MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        base.OnFormClosing(e);
    }
}
