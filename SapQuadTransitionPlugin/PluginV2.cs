// Copyright (C) 2026 Leonardo Bandini
// SPDX-License-Identifier: GPL-2.0-or-later
// Additional permission for the separately installed SAP2000 API:
// see SAP2000-LINKING-EXCEPTION.md.
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.Web.Script.Serialization;
using CSiAPIv1;

namespace SapQuadTransitionPlugin
{
    [ComVisible(true)]
    [Guid("D64E6254-E9B2-46C8-8C0E-C8A5C78D94AA")]
    [ProgId("SapQuadTransitionPlugin.cPlugin")]
    [ClassInterface(ClassInterfaceType.None)]
    public class cPlugin : cPluginContract
    {
        private const double Tolerance = 1e-7;
        private const int PointObjectType = 1;
        private const int FrameObjectType = 2;
        private const int CableObjectType = 3;
        private const int TendonObjectType = 4;
        private const int AreaObjectType = 5;
        private const int SolidObjectType = 6;
        private const int LinkObjectType = 7;
        private const string PluginVersion = "3.0.7";
        private const string WindowTitle = "MESHing " + PluginVersion;
        private const string UpdateManifestUrl = "https://api.github.com/repos/leonardobandini/MESHing/releases/latest";
        private const string LatestDownloadUrl = "https://github.com/leonardobandini/MESHing/releases/latest/download/MESHing-latest.zip";
        private static readonly Color UiNavy = Color.FromArgb(15, 23, 42);
        private static readonly Color UiBody = Color.FromArgb(30, 41, 59);
        private static readonly Color UiMuted = Color.FromArgb(71, 85, 105);
        private static readonly Color UiBlue = Color.FromArgb(37, 99, 235);
        private static readonly Color UiLight = Color.FromArgb(248, 250, 252);
        private static readonly Color UiInactive = Color.FromArgb(226, 232, 240);
        private static readonly Color UiBorder = Color.FromArgb(203, 213, 225);
        private static bool useItalian;
        private static int interactiveRunActive;
        private static int hostRegionalUnsafe;
        private static MeshOptionsDialog interactiveDialog;
        // Querying ProcessName/StartTime repeatedly is expensive in SAP2000's
        // Windows process environment. The host identity and start time cannot
        // change during this process; only the registry timestamp needs polling.
        private static readonly string CurrentHostProcessName = Process.GetCurrentProcess().ProcessName;
        private static readonly Lazy<DateTime> CurrentHostStartedUtc = new Lazy<DateTime>(() =>
        {
            using (Process host = Process.GetCurrentProcess()) return host.StartTime.ToUniversalTime();
        });

        private static string L(string english, string italian) { return useItalian ? italian : english; }
        private static bool IsSupportedHostProcess()
        {
            string name = CurrentHostProcessName;
            return name.Equals("SAP2000", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("CSiBridge", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ETABS", StringComparison.OrdinalIgnoreCase);
        }
        private static string HostProductName()
        {
            string name = CurrentHostProcessName;
            return name.Equals("CSiBridge", StringComparison.OrdinalIgnoreCase) ? "CSiBridge" :
                name.Equals("ETABS", StringComparison.OrdinalIgnoreCase) ? "ETABS" : "SAP2000";
        }
        private static bool IsSelectionOnlyTableFilterActive(cSapModel model)
        {
            int areaCount = 0, pointCount = 0;
            string[] areas = null, points = null;
            Check(model.AreaObj.GetNameList(ref areaCount, ref areas),
                "checking areas before Generate", "AreaObj.GetNameList");
            Check(model.PointObj.GetNameList(ref pointCount, ref points),
                "checking joints before Generate", "PointObj.GetNameList");
            return IsIncompleteEditingTableWithFullDisplay(model, "Connectivity - Area", "Area",
                    areas ?? new string[0]) ||
                IsIncompleteEditingTableWithFullDisplay(model, "Joint Coordinates", "Joint",
                    points ?? new string[0]);
        }

        private static bool IsIncompleteEditingTableWithFullDisplay(cSapModel model,
            string key, string objectField, string[] liveNames)
        {
            if (liveNames.Length == 0) return false;
            int version = 0, records = 0;
            string[] fields = null, values = null;
            int code = model.DatabaseTables.GetTableForEditingArray(key, "All", ref version,
                ref fields, ref records, ref values);
            if (code != 0 || records < 0 || fields == null || fields.Length == 0 ||
                (records > 0 && (values == null || values.Length != records * fields.Length)) ||
                (records == 0 && values != null && values.Length != 0)) return false;

            var live = new HashSet<string>(liveNames, StringComparer.OrdinalIgnoreCase);
            bool incomplete = records < live.Count;
            int column = Array.IndexOf(fields, objectField);
            if (!incomplete && column >= 0)
            {
                var editing = new HashSet<string>(Enumerable.Range(0, records).Select(row =>
                    values[row * fields.Length + column]), StringComparer.OrdinalIgnoreCase);
                incomplete = !editing.SetEquals(live);
            }
            if (!incomplete) return false;

            int displayVersion = 0, displayRecords = 0;
            string[] groups = null, displayFields = null, displayValues = null;
            int displayCode = model.DatabaseTables.GetTableForDisplayArray(key, ref groups, "All",
                ref displayVersion, ref displayFields, ref displayRecords, ref displayValues);
            if (displayCode != 0 || displayVersion != version || displayRecords < live.Count ||
                displayFields == null || displayFields.Length == 0 || displayValues == null ||
                displayValues.Length != displayRecords * displayFields.Length) return false;
            int displayColumn = Array.IndexOf(displayFields, objectField);
            return displayColumn >= 0 && new HashSet<string>(Enumerable.Range(0, displayRecords).Select(row =>
                displayValues[row * displayFields.Length + displayColumn]),
                StringComparer.OrdinalIgnoreCase).SetEquals(live);
        }
        private static string SavedModelPath(cSapModel model)
        {
            string path = model.GetModelFilename(true);
            if (!string.IsNullOrEmpty(path) &&
                string.Equals(Path.GetExtension(path), ".$et", StringComparison.OrdinalIgnoreCase))
            {
                // ETABS 23 reports its active temporary database as *.$et even
                // after saving an EDB. The numbered backup must copy the last
                // saved EDB, never the transient working database.
                string edb = Path.ChangeExtension(path, ".edb");
                if (!File.Exists(edb))
                    throw new FileNotFoundException("The saved ETABS EDB is unavailable for backup.", edb);
                return edb;
            }
            return path;
        }

        private static bool HasSavedModelFile(cSapModel model)
        {
            string path;
            try { path = SavedModelPath(model); }
            catch (FileNotFoundException) { return false; }
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
            string extension = Path.GetExtension(path);
            return string.Equals(extension, ".sdb", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".bdb", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, ".edb", StringComparison.OrdinalIgnoreCase);
        }
        private static void LogPluginException(Exception error)
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SAP2000QuadMesher");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "plugin-errors.log"),
                    DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + error + Environment.NewLine);
            }
            catch (Exception) { /* Diagnostics must never crash the host. */ }
        }

        private static string HelpPath(bool italian)
        {
            return Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                "docs", "help-gmsh-v2", italian ? "index-it.html" : "index-en.html");
        }

        private static void OpenHelp(Form owner)
        {
            string path = HelpPath(useItalian);
            if (!File.Exists(path))
            {
                ShowHostModal(owner.Handle, interactiveDialog == null ? Process.GetCurrentProcess().MainWindowHandle :
                    interactiveDialog.HostWindow,
                    () => MessageBox.Show(owner, L("The local help is missing from the installation.",
                        "La guida locale non è presente nell'installazione."), WindowTitle,
                        MessageBoxButtons.OK, MessageBoxIcon.Warning));
                return;
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        private static void ApplyPluginIcon(Form form)
        {
            string path = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                "img", "quad-mesher.ico");
            if (!File.Exists(path)) return;
            using (var image = new Icon(path)) form.Icon = (Icon)image.Clone();
        }
        internal static void TraceCancellationStage(string stage)
        {
            string path = Environment.GetEnvironmentVariable("SAP_QUAD_CANCEL_TRACE_PATH");
            if (string.IsNullOrEmpty(path)) return;
            try { File.AppendAllText(path, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + " " + stage + Environment.NewLine); }
            catch (IOException) { }
        }
        private static string LocalizeMessage(string message)
        {
            if (!useItalian || string.IsNullOrEmpty(message)) return message;
            var replacements = new[]
            {
                new[] { "The model is locked. Unlock it before generating the mesh.", "Il modello e bloccato. Sbloccarlo prima di generare la mesh." },
                new[] { "Select at least one area object.", "Selezionare almeno un oggetto area." },
                new[] { "Invalid maximum size.", "Dimensione massima non valida." },
                new[] { "No valid mesh could be generated while preserving the selected geometry, constraints and maximum size.",
                    "Non è stato possibile generare una mesh rispettando geometria, vincoli e dimensione massima selezionati." },
                new[] { "Exact symmetry requires a reflection axis compatible with the selected geometry and constraints.",
                    "La simmetria esatta richiede un asse compatibile con la geometria e i vincoli selezionati." },
                new[] { "Regular meshing and local refinement are mutually exclusive.", "La mesh regolare e il raffinamento locale si escludono a vicenda." },
                new[] { "A regular grid would subdivide a selected boundary edge. Enable strict maximum size between selected nodes to permit regular subdivisions.",
                     "La griglia regolare suddividerebbe un bordo selezionato. Attivare 'Inserisci nodi di mesh anche negli spigoli con nodi selezionati' per consentire le suddivisioni regolari." },
                new[] { "Regular aligned meshing requires a rectangular area", "La mesh regolare allineata richiede un'area rettangolare" },
                new[] { "Regular mesh guides are not coplanar with the area", "Le guide della mesh regolare non sono complanari all'area" },
                new[] { "Regular aligned meshing requires frame constraints parallel to the area edges", "La mesh regolare allineata richiede frame paralleli ai lati dell'area" },
                new[] { "Regular aligned mesh failed the geometric quality checks", "La mesh regolare allineata non ha superato le verifiche di qualità geometrica" },
                new[] { "Regular aligned mesh does not satisfy the primary symmetry axis", "La mesh regolare allineata non rispetta l'asse di simmetria principale" },
                new[] { "Regular aligned mesh does not satisfy the secondary symmetry axis", "La mesh regolare allineata non rispetta l'asse di simmetria secondario" },
                new[] { "Selected frames cannot produce a quality aligned mesh at this size. Use stress refinement or deselect the incompatible frames and joints.",
                     "I frame selezionati non consentono una mesh allineata di qualita con questo passo. Usare il raffinamento delle tensioni o deselezionare frame e nodi incompatibili." },
                new[] { "Selected joints are too close for a quality Cartesian grid at this size. Deselect the unnecessary joints or use stress refinement instead.",
                    "I nodi selezionati sono troppo ravvicinati per una griglia cartesiana di qualita con questo passo. Deselezionare i nodi non necessari oppure usare il raffinamento delle tensioni." },
                new[] { "Select an opening-boundary group.", "Selezionare un gruppo per il contorno delle aperture." },
                new[] { "Select a hole-center group.", "Selezionare un gruppo di centri dei fori." },
                new[] { "Invalid hole diameter.", "Diametro del foro non valido." },
                new[] { "Preparing geometry...", "Preparazione della geometria..." },
                new[] { "Generating and optimizing the mesh...", "Generazione e ottimizzazione della mesh..." },
                new[] { "Generating the mesh...", "Generazione della mesh..." },
                new[] { "Creating SAP2000 points...", "Creazione dei punti SAP2000..." },
                new[] { "Creating shell elements and transferring loads...", "Creazione degli elementi shell e trasferimento dei carichi..." },
                new[] { "FAST: bulk importing points and shell elements...", "FAST: importazione massiva di punti ed elementi shell..." },
                new[] { "FAST: bulk importing all points and shell elements...", "FAST: importazione massiva di tutti i punti ed elementi shell..." },
                new[] { "FAST: transferring assignments...", "FAST: trasferimento delle assegnazioni..." },
                new[] { "Mesh generator:", "Generatore mesh:" },
                new[] { "Traceback (most recent call last):", "" },
                new[] { "Undo failed.", "Annullamento non riuscito." },
                new[] { "Rollback failed:", "Ripristino non riuscito:" },
                new[] { "Selection restore failed:", "Ripristino della selezione non riuscito:" },
                new[] { "exact symmetry could not be generated; no non-symmetric fallback was applied.",
                    "impossibile generare la simmetria esatta; non e stata applicata una mesh non simmetrica." },
                new[] { "The selected areas do not form reflection pairs.",
                    "Le aree selezionate non formano coppie speculari." },
                new[] { "Selected nodes, boundaries or holes are not reflection-symmetric.",
                    "Nodi, contorni o fori selezionati non sono simmetrici per riflessione." },
                new[] { "The full selection is not exactly symmetric about the selected axis.",
                    "La mesh complessiva non e esattamente simmetrica rispetto all'asse selezionato." },
                new[] { "A mirrored mesh is missing a selected interior joint.",
                    "La mesh riflessa non contiene un nodo interno selezionato." },
                new[] { "no reflection axis is compatible with the selected geometry and constraints",
                    "nessun asse di riflessione e compatibile con geometria e vincoli selezionati" },
                new[] { "Undo failed. The generated mesh was not committed; inspect the model before continuing.",
                    "Annullamento non riuscito. La mesh generata non e stata confermata; controllare il modello prima di continuare." },
                new[] { "Unable to generate a full-quad mesh conforming to the selected frames",
                     "Impossibile generare una mesh interamente quadrangolare conforme ai frame selezionati" },
                new[] { "No valid mesh candidate", "Nessun candidato di mesh valido" },
                new[] { "maximum edge", "spigolo massimo" },
                new[] { "max edge", "spigolo massimo" },
                new[] { "allowed", "consentito" },
                new[] { "minimum oriented Jacobian", "Jacobiano orientato minimo" },
                new[] { "algorithm", "algoritmo" },
                new[] { "constrained interior points were not embedded", "punti interni vincolati non sono stati incorporati" },
                new[] { "This polygon is not a radial sector", "Questo poligono non e un settore radiale" },
                new[] { "No selected guide chain connects the radial sides", "Nessuna guida selezionata collega i lati radiali" },
                new[] { "Sector-block meshing is not applicable to holes, reflection or selected openings",
                    "La mesh a blocchi di settore non si applica a fori, simmetria o aperture selezionate" },
                new[] { "Prototype sector failed geometric validation", "Il settore non ha superato la verifica geometrica" },
                new[] { "Non-conforming shared edge between areas", "Bordo condiviso non congruente fra le aree" },
                new[] { "mesh import was cancelled before modifying SAP2000.",
                    "l'importazione e stata annullata prima di modificare SAP2000." },
                new[] { "Could not create a mesh that respects the requested maximum size", "Impossibile generare una mesh che rispetti la dimensione massima richiesta" },
                new[] { "No mesh candidate meets the requested quality limits", "Nessun candidato di mesh rispetta i limiti di qualita richiesti" },
                new[] { "Mesh script not found.", "Script di meshatura non trovato." },
                new[] { "The Python/Gmsh runtime bundled with the plugin was not found.",
                    "Runtime Python/Gmsh incluso nel plugin non trovato." },
                new[] { "does not belong to the selected areas.", "non appartiene alle aree selezionate." },
                new[] { "has zero length.", "ha lunghezza nulla." },
                new[] { "does not intersect the selected areas.", "non interseca le aree selezionate." },
                new[] { "The selected opening-boundary group contains no frame or shell-edge constraints in the selected areas.",
                     "Il gruppo contorno aperture non contiene frame o bordi shell vincolanti nelle aree selezionate." },
                new[] { "The selected circular-hole group contains no point objects.",
                    "Il gruppo dei fori circolari non contiene point object." },
                new[] { "None of the point objects in the selected circular-hole group belongs to the selected areas.",
                    "Nessun point object del gruppo fori circolari appartiene alle aree selezionate." },
                new[] { "lies inside the circular hole centered at", "si trova dentro il foro circolare con centro in" },
                new[] { "crosses the circular hole centered at", "attraversa il foro circolare con centro in" },
                new[] { "centered at point", "con centro nel punto" },
                new[] { "centered at", "con centro in" },
                new[] { "Symmetric circular holes require a detected symmetry axis.",
                    "I fori circolari simmetrici richiedono un asse di simmetria rilevato." },
                new[] { "Circular holes do not form reflected pairs across the selected symmetry axis.",
                    "I fori circolari non formano coppie speculari rispetto all'asse selezionato." },
                new[] { "Unsupported SAP2000 present units:", "Unita correnti SAP2000 non supportate:" },
                new[] { "Unsupported length unit:", "Unita di lunghezza non supportata:" },
                new[] { "Unsupported selected SAP2000 object type:", "Tipo di oggetto SAP2000 selezionato non supportato:" },
                new[] { "must have at least three edges.", "deve avere almeno tre lati." },
                new[] { "Failed to discretize a shared edge.", "Discretizzazione del bordo condiviso non riuscita." },
                new[] { "Select a symmetry axis compatible with the area and its constrained points.",
                    "Selezionare un asse di simmetria compatibile con l'area e i punti vincolati." },
                new[] { "Advanced local axes are not supported.", "Gli assi locali avanzati non sono supportati." },
                new[] { "Non-conforming intersection between areas", "Intersezione non congruente fra le aree" },
                new[] { "The generated transition mesh is not exactly symmetric about the secondary axis.",
                    "La mesh di transizione generata non e simmetrica rispetto all'asse secondario." },
                new[] { "In symmetric mode, each transition edge must have an even number of intervals.",
                    "In modalita simmetrica ciascun lato di transizione deve avere un numero pari di intervalli." },
                new[] { "The edge distribution is not symmetric about the midpoint.",
                    "La distribuzione dei nodi sul lato non e simmetrica rispetto al punto medio." },
                new[] { "The mesh backend did not stop; the model was left unchanged.",
                    "Il backend della mesh non si e arrestato; il modello non e stato modificato." },
                new[] { "Invalid mesh topology: an edge belongs to more than two shell elements.",
                    "Topologia della mesh non valida: un lato appartiene a piu di due elementi shell." },
                new[] { "The closed frames would remove the entire area.",
                     "I frame chiusi eliminerebbero l'intera area." },
                new[] { "Save the SAP2000 model before meshing, or disable the backup option.",
                    "Salvare il modello SAP2000 prima della mesh oppure disattivare il backup." },
                new[] { "Incomplete SAP2000 model backup.", "Backup del modello SAP2000 incompleto." },
                new[] { "No free numbered SAP2000 model backup name is available.",
                    "Non e disponibile un nome libero per il backup numerato del modello SAP2000." },
                new[] { "Symmetric mode requires a rectangular area.",
                    "La modalita simmetrica richiede un'area rettangolare." },
                new[] { "SAP2000 error while", "Errore SAP2000 durante" },
                new[] { ": code ", ": codice " },
                new[] { "FAST does not support the connectivity required by shell element", "FAST non supporta la connettivita richiesta dall'elemento shell" },
                new[] { "Invalid FAST import:", "Importazione FAST non valida:" },
                new[] { "Invalid FAST restore:", "Ripristino FAST non valido:" },
                new[] { "FAST: missing shell element", "FAST: elemento shell mancante" },
                new[] { "FAST: invalid connectivity for", "FAST: connettivita non valida per" },
                new[] { "FAST: invalid property for", "FAST: proprieta non valida per" },
                new[] { "FAST: inconsistent imported object count.", "FAST: numero di oggetti importati incoerente." },
                new[] { "FAST restore: inconsistent object count.", "Ripristino FAST: numero di oggetti incoerente." },
                new[] { "FAST is unavailable: field", "FAST non disponibile: il campo" },
                new[] { "is missing from", "manca in" },
                new[] { "FAST: GUID changed for", "FAST: GUID modificato per" },
                new[] { "Invalid FAST table:", "Tabella FAST non valida:" },
                new[] { "Missing FAST field:", "Campo FAST mancante:" },
                new[] { "Missing FAST identity field.", "Campo identificativo FAST mancante." },
                new[] { "Exact symmetry", "La simmetria esatta" },
                new[] { "Area ", "Area " },
                new[] { "Selected point ", "Il punto selezionato " },
                new[] { "Frame ", "Il frame " },
                new[] { "Circular hole", "Il foro circolare" },
                new[] { "The generated transition mesh is not exactly symmetric", "La mesh di transizione generata non e esattamente simmetrica" },
                new[] { "The generator returned an empty mesh.", "Il generatore ha restituito una mesh vuota." },
                new[] { "Unsupported connectivity.", "Connettivita non supportata." },
                new[] { "The area must be planar.", "L'area deve essere piana." },
                new[] { "Degenerate area.", "Area degenere." },
                new[] { "Outside shape references:", "Fuori riferimento geometrico:" },
                new[] { "of area", "della superficie" },
                new[] { "Invalid:", "Non validi:" },
                new[] { "Nodes:", "Nodi:" },
                new[] { "Elements:", "Elementi:" },
                new[] { "quads", "quadrilateri" },
                new[] { "triangles", "triangoli" },
                new[] { "Constraining frames:", "Frame vincolanti:" },
                new[] { "Shell elements removed for holes:", "Elementi shell rimossi per i fori:" },
                new[] { "Maximum size", "Dimensione massima" },
                new[] { "Area-weighted mesh quality", "Qualita della mesh pesata per superficie" },
                new[] { "Average edge length", "Lunghezza media degli spigoli" },
                new[] { "Average aspect ratio", "Rapporto d'aspetto medio" },
                new[] { "Average quadrilateral angular skew", "Distorsione angolare media dei quadrilateri" },
                new[] { "Average scaled Jacobian", "Jacobiano scalato medio" },
                new[] { "Average quadrilateral angles", "Angoli medi dei quadrilateri" },
                new[] { "Average triangle angles", "Angoli medi dei triangoli" },
                new[] { "Shape score: 0 - 1; 1 is ideal", "Punteggio geometrico: 0 - 1; 1 e ideale" },
                new[] { "Surface-weighted; requested nominal:", "Pesato per superficie; passo nominale richiesto:" },
                new[] { "Angular deviation: 0 - 0.5; 0 is ideal", "Deviazione angolare: 0 - 0,5; 0 e ideale" },
                new[] { "Average min/max; reference:", "Media dei minimi/massimi; riferimento:" },
                new[] { "over limit", "oltre il limite" },
                new[] { "CUBIT/Verdict reference:", "Riferimento CUBIT/Verdict:" },
                new[] { "Aspect ratio", "Rapporto d'aspetto" },
                new[] { "Quadrilateral skew", "Distorsione dei quadrilateri" },
                new[] { "Scaled Jacobian", "Jacobiano scalato" },
                new[] { "Quadrilateral angles", "Angoli dei quadrilateri" },
                new[] { "Triangle angles", "Angoli dei triangoli" },
                new[] { "Orientation", "Orientamento" },
                new[] { "Requested/constraints:", "Richiesta/vincoli:" },
                new[] { "degrees", "gradi" },
                new[] { "invalid elements", "elementi non validi" },
                new[] { "No inverted or degenerate elements", "Nessun elemento invertito o degenere" },
                new[] { "is ideal", "e ideale" },
                new[] { "EXCELLENT", "ECCELLENTE" },
                new[] { "ACCEPTABLE", "ACCETTABILE" },
                new[] { "POOR", "SCARSO" },
                new[] { "CRITICAL", "CRITICO" },
                new[] { "MINIMUM", "MINIMO" },
                new[] { "MAXIMUM", "MASSIMO" }
            };
            foreach (string[] replacement in replacements) message = message.Replace(replacement[0], replacement[1]);
            return message;
        }

        private static string LocalizeErrorMessage(string message)
        {
            if (message != null && message.Contains("1D mesh cannot be divided by 2"))
                return L("Gmsh full-quad cannot subdivide a prescribed boundary segment on this selection. Try Blossom (not full-quad) or change the boundary partition.",
                    "Gmsh full-quad non puo suddividere un segmento del bordo di questa selezione. Provare Blossom (non full-quad) oppure cambiare la partizione del bordo.");
            if (message != null && (message.Contains("Gmsh stopped unexpectedly") || message.Contains("access violation")))
                return L("Gmsh stopped while meshing this geometry. Try algorithm 8 + Blossom on one area; the SAP2000 model was not imported.",
                    "Gmsh si e interrotto durante la meshatura. Provare algoritmo 8 + Blossom su una sola area; il modello SAP2000 non e stato importato.");
            if (!useItalian || string.IsNullOrEmpty(message)) return message;
            // Python prints a traceback with English source lines and paths. Show only
            // the final error, and keep the full diagnostic in the process log.
            int generator = message.IndexOf("Mesh generator:", StringComparison.Ordinal);
            if (generator >= 0)
            {
                Console.Error.WriteLine(message);
                string[] lines = message.Substring(generator + "Mesh generator:".Length)
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                string last = lines.Length == 0 ? "" : lines[lines.Length - 1].Trim();
                int separator = last.IndexOf(": ", StringComparison.Ordinal);
                if (separator >= 0 && (last.StartsWith("RuntimeError:") || last.StartsWith("ValueError:")))
                    last = last.Substring(separator + 2);
                string translated = LocalizeMessage(last);
                if (translated == last && Regex.IsMatch(last, @"[A-Za-z]{4}"))
                    return "Il generatore non è riuscito a creare una mesh valida. Consultare il registro per i dettagli tecnici.";
                Match area = Regex.Match(message.Substring(0, generator), @"Area\s+([^:\s]+):");
                return (area.Success ? "Area " + area.Groups[1].Value + ": " : "") + "Generatore mesh: " + translated;
            }
            Match sapApi = Regex.Match(message,
                @"SAP2000 error while (?<operation>[^\r\n]+?)(?: \[API: (?<api>[^\]]+)\])?: code (?<code>\d+)");
            if (sapApi.Success)
            {
                Console.Error.WriteLine(message);
                Match area = Regex.Match(message.Substring(0, sapApi.Index), @"Area\s+([^:\s]+):");
                return (area.Success ? "Area " + area.Groups[1].Value + ": " : "") +
                    "Errore SAP2000 (codice " + sapApi.Groups["code"].Value + ")" +
                    (sapApi.Groups["api"].Success ? " — API " + sapApi.Groups["api"].Value : "") +
                    "; operazione: " + sapApi.Groups["operation"].Value.Trim() + ".";
            }
            string localized = LocalizeMessage(message);
            if (Regex.IsMatch(localized, @"\b(the|could|would|failed|invalid|selected|unable|requires?|does|cannot|must|missing|expected|generated|traceback|error while|is not|was not)\b",
                RegexOptions.IgnoreCase))
            {
                Console.Error.WriteLine(message);
                return "Impossibile generare una mesh valida con le opzioni correnti. Consultare il registro per i dettagli tecnici.";
            }
            return localized;
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr SetActiveWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool EnableWindow(IntPtr window, bool enable);

        private delegate bool WindowEnumerator(IntPtr window, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(WindowEnumerator enumerate, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, WindowEnumerator enumerate, IntPtr data);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder text, int capacity);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowRect
        {
            public int Left, Top, Right, Bottom;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out WindowRect rectangle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y,
            int width, int height, uint flags);

        private static T ShowHostModal<T>(IntPtr pluginWindow, IntPtr hostWindow, Func<T> show)
        {
            if (hostWindow == IntPtr.Zero || !IsWindow(hostWindow)) return show();
            uint hostPid;
            GetWindowThreadProcessId(hostWindow, out hostPid);
            if (hostPid != (uint)Process.GetCurrentProcess().Id) return show();
            var disabled = new List<IntPtr>();
            try
            {
                bool blocked = EnumWindows(delegate(IntPtr window, IntPtr unused)
                {
                    uint pid;
                    GetWindowThreadProcessId(window, out pid);
                    if (pid != hostPid || window == pluginWindow || !IsWindowVisible(window) ||
                        !IsWindowEnabled(window)) return true;
                    EnableWindow(window, false);
                    if (IsWindowEnabled(window)) return false;
                    disabled.Add(window);
                    return true;
                }, IntPtr.Zero);
                if (!blocked || IsWindowEnabled(hostWindow))
                    throw new InvalidOperationException("Cannot prevent SAP2000/CSiBridge interaction during a MESHing dialog.");
                return show();
            }
            finally
            {
                foreach (IntPtr window in disabled)
                {
                    uint pid;
                    if (IsWindow(window) && GetWindowThreadProcessId(window, out pid) != 0 &&
                        pid == hostPid && !IsWindowEnabled(window)) EnableWindow(window, true);
                }
            }
        }

        internal static T ShowHostModalFromPanel<T>(Form settings, Form parent, Func<T> show)
        {
            MeshOptionsDialog dialog = interactiveDialog;
            IntPtr hostWindow = dialog == null ? Process.GetCurrentProcess().MainWindowHandle : dialog.HostWindow;
            // Let ShowDialog disable its actual owner. Disabling the MESHing
            // window before assigning ownership can reorder the SAP window
            // behind unrelated applications on some machines.
            settings.Shown += delegate
            {
                if (hostWindow == IntPtr.Zero || !IsWindow(hostWindow)) return;
                uint hostPid;
                if (GetWindowThreadProcessId(hostWindow, out hostPid) == 0 ||
                    hostPid != (uint)Process.GetCurrentProcess().Id) return;
                // Keep the host in the normal window band, without activating it.
                if (!SetWindowPos(hostWindow, IntPtr.Zero, 0, 0, 0, 0,
                    0x0001 | 0x0002 | 0x0010 | 0x0200)) // NOSIZE, NOMOVE, NOACTIVATE, NOOWNERZORDER
                    LogPluginException(new InvalidOperationException("Cannot order the host below MESHing: " +
                        Marshal.GetLastWin32Error()));
            };
            // In standalone UI tests the plugin form itself can be the process
            // main window; it is not a separate SAP viewport to lock.
            return hostWindow == parent.Handle ? show() : ShowHostModal(parent.Handle, hostWindow, show);
        }

        private static string WindowText(IntPtr window)
        {
            var text = new StringBuilder(512);
            GetWindowText(window, text, text.Capacity);
            return text.ToString();
        }

        private static string WindowClass(IntPtr window)
        {
            var name = new StringBuilder(128);
            GetClassName(window, name, name.Capacity);
            return name.ToString();
        }

        private static bool TryDismissSapCompletion(int processId)
        {
            bool dismissed = false;
            EnumWindows(delegate(IntPtr window, IntPtr data)
            {
                try
                {
                    uint ownerProcess;
                    GetWindowThreadProcessId(window, out ownerProcess);
                    if (ownerProcess != processId || WindowClass(window) != "#32770" ||
                        WindowText(window) != HostProductName()) return true;
                    bool exactMessage = false;
                    IntPtr ok = IntPtr.Zero;
                    EnumChildWindows(window, delegate(IntPtr child, IntPtr unused)
                    {
                        try
                        {
                            string text = WindowText(child);
                            string kind = WindowClass(child);
                            if (kind == "Static" && text == "Plugin completed successfully.") exactMessage = true;
                            if (kind == "Button" && text == "OK") ok = child;
                        }
                        catch (Exception error) { LogPluginException(error); }
                        return true;
                    }, IntPtr.Zero);
                    if (exactMessage && ok != IntPtr.Zero)
                    {
                        // Only the exact completion notice may be hidden; never
                        // alter a SAP2000 warning, question or error dialog.
                        ShowWindow(window, 0);
                        IntPtr result;
                        bool clicked = SendMessageTimeout(ok, 0xF5, IntPtr.Zero, IntPtr.Zero,
                            0x2, 500, out result) != IntPtr.Zero; // BM_CLICK, SMTO_ABORTIFHUNG
                        // SAP2000 can destroy the modal window just after the
                        // button message returns. Do not restore the success
                        // notice (or report a false failure) in that race.
                        if (clicked)
                            for (int attempt = 0; attempt < 12 && IsWindow(window); attempt++)
                                Thread.Sleep(15);
                        dismissed = clicked && !IsWindow(window);
                        if (!dismissed && IsWindow(window)) ShowWindow(window, 5);
                        if (dismissed) Console.WriteLine("sap-plugin-completion-dismissed");
                    }
                }
                catch (Exception error) { LogPluginException(error); }
                return !dismissed;
            }, IntPtr.Zero);
            return dismissed;
        }

        private static void DismissSapCompletionWhenShown()
        {
            int processId = Process.GetCurrentProcess().Id;
            var watcher = new Thread(delegate()
            {
                try
                {
                    var elapsed = Stopwatch.StartNew();
                    while (elapsed.ElapsedMilliseconds <
                        90000
                        )
                    {
                        if (TryDismissSapCompletion(processId)) break;
                        Thread.Sleep(
                            25
                            );
                    }
                }
                catch (Exception error) { LogPluginException(error); }
            });
            watcher.IsBackground = true;
            try { watcher.Start(); }
            catch (Exception error) { LogPluginException(error); }
        }

        public int Info(ref string text)
        {
            TraceCancellationStage("plugin-info");
            text = "MESHing " + PluginVersion +
                L(": interactive Gmsh 2D meshing of selected areas in " + HostProductName() + ".",
                    ": meshatura Gmsh 2D delle aree selezionate in " + HostProductName() + ".");
            return 0;
        }

        public void Main(ref cSapModel model, ref cPluginCallback callback)
        {
            TraceCancellationStage("plugin-main");
            // Returning from the SAP2000 plugin callback releases the host's own
            // modal command loop. A modeless Form alone does not: even when its
            // window is enabled, SAP2000 cannot navigate while Main is running.
            // Run the interactive dialog on a dedicated STA after Finish. Atomic
            // automation remains synchronous so its completion contract is intact.
            if (IsSupportedHostProcess() &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SIZE")))
            {
                if (Interlocked.CompareExchange(ref interactiveRunActive, 1, 0) != 0)
                {
                    MeshOptionsDialog existing = interactiveDialog;
                    try
                    {
                        if (existing != null && !existing.IsDisposed && existing.IsHandleCreated)
                            existing.BeginInvoke((Action)delegate
                            {
                                if (!existing.IsDisposed) { existing.Show(); existing.BringToFront(); }
                            });
                    }
                    catch (InvalidOperationException error) { LogPluginException(error); }
                    DismissSapCompletionWhenShown();
                    try { callback.Finish(0); }
                    catch (Exception error) { LogPluginException(error); }
                    return;
                }
                cSapModel activeModel = model;
                // The plugin callback may return before the STA starts. A task
                // gate cannot be disposed while that worker is about to wait,
                // unlike a short-lived ManualResetEvent on the host thread.
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var thread = new Thread(delegate()
                {
                    try
                    {
                        if (!ready.Task.GetAwaiter().GetResult()) return;
                        System.Threading.ThreadExceptionEventHandler uiError = delegate(object sender,
                            System.Threading.ThreadExceptionEventArgs args)
                        {
                            LogPluginException(args.Exception);
                            try
                            {
                                MeshOptionsDialog current = interactiveDialog;
                                if (current != null && !current.IsDisposed)
                                    current.ReportUiFailure();
                            }
                            catch (Exception nested) { LogPluginException(nested); }
                        };
                        Application.ThreadException += uiError;
                        try { RunWithErrorHandling(activeModel, null); }
                        catch (Exception error) { LogPluginException(error); }
                        finally
                        {
                            Application.ThreadException -= uiError;
                            Interlocked.Exchange(ref interactiveRunActive, 0);
                        }
                    }
                    catch (Exception error)
                    {
                        LogPluginException(error);
                        Interlocked.Exchange(ref interactiveRunActive, 0);
                    }
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                bool accepted = false;
                try
                {
                    thread.Start();
                    DismissSapCompletionWhenShown();
                    callback.Finish(0);
                    accepted = true;
                }
                catch (Exception error) { LogPluginException(error); }
                finally
                {
                    ready.TrySetResult(accepted);
                    if (!accepted) Interlocked.Exchange(ref interactiveRunActive, 0);
                }
                return;
            }
            RunWithErrorHandling(model, callback);
        }

        private static void RunWithErrorHandling(cSapModel model, cPluginCallback callback)
        {
            TraceCancellationStage("plugin-run-start");
            int error = 0;
            try { Run(model); }
            catch (Exception exception)
            {
                error = 1;
                LogPluginException(exception);
                string automationError = Environment.GetEnvironmentVariable("SAP_QUAD_AUTOMATION_ERROR");
                if (!string.IsNullOrEmpty(automationError))
                    try { File.WriteAllText(automationError, exception.ToString()); } catch { }
                else
                    try { ShowHostModal(IntPtr.Zero, Process.GetCurrentProcess().MainWindowHandle,
                        () => MessageBox.Show(LocalizeErrorMessage(exception.Message), WindowTitle,
                            MessageBoxButtons.OK, exception is HostRegionalSettingsChangedException
                                ? MessageBoxIcon.Warning : MessageBoxIcon.Error)); }
                    catch (Exception displayError) { LogPluginException(displayError); }
            }
            finally { if (callback != null) callback.Finish(error); }
        }

        private static void Run(cSapModel model)
        {
            useItalian = MeshOptionsDialog.InitialItalian();
            if (HostRegionalSettingsChanged()) throw new HostRegionalSettingsChangedException();
            eUnits originalUnits = model.GetPresentUnits();
            eUnits databaseUnits = model.GetDatabaseUnits();
            string versionText = "";
            double versionNumber = 0;
            bool normalizeUnits = model.GetVersion(ref versionText, ref versionNumber) == 0 &&
                RequiresDatabaseUnitScope(versionText, versionNumber, originalUnits, databaseUnits);
            eUnits unitsToRestore = originalUnits;
            bool restoreScopedUnits = normalizeUnits;
            if (normalizeUnits)
            {
                Check(model.SetPresentUnits(databaseUnits), "setting deterministic API units", "cSapModel.SetPresentUnits");
                Console.WriteLine("mesh-units=database sap=" + versionText + " original=" + originalUnits +
                    " effective=" + databaseUnits);
            }
            Action scopeNewModelUnits = delegate
            {
                // An SDB opened while the modeless dialog is active has its own
                // present/database units. Never restore the first model's units
                // onto the second model when this dialog eventually closes.
                eUnits present = model.GetPresentUnits();
                eUnits database = model.GetDatabaseUnits();
                string sapVersion = "";
                double sapVersionNumber = 0;
                bool required = model.GetVersion(ref sapVersion, ref sapVersionNumber) == 0 &&
                    RequiresDatabaseUnitScope(sapVersion, sapVersionNumber, present, database);
                unitsToRestore = present;
                restoreScopedUnits = required;
                if (required)
                    Check(model.SetPresentUnits(database), "setting new SAP2000 model database units", "cSapModel.SetPresentUnits");
            };
            try { RunWithUnitContext(model, scopeNewModelUnits); }
            finally
            {
                if (restoreScopedUnits)
                {
                    Check(model.SetPresentUnits(unitsToRestore), "restoring SAP2000 present units", "cSapModel.SetPresentUnits");
                    Console.WriteLine("mesh-units-restored=" + unitsToRestore);
                }
            }
        }

        private static bool RequiresDatabaseUnitScope(string versionText, double versionNumber,
            eUnits presentUnits, eUnits databaseUnits)
        {
            Version version;
            bool contextDependent = Version.TryParse(versionText, out version)
                ? version.CompareTo(new Version(27, 2)) >= 0
                : versionNumber >= 27.2 - 1e-9;
            return contextDependent && presentUnits != databaseUnits;
        }

        private sealed class LiveSelection
        {
            // Refreshing thousands of SAP shells through per-object COM calls
            // would monopolize the host after Select All on a generated mesh.
            internal const int MaximumLiveAreas = 500;
            public readonly int[] Types;
            public readonly string[] Names, Areas, Points, Frames;
            public readonly int Links;
            private readonly string[] identity;

            public LiveSelection(cSapModel model)
            {
                int count = 0;
                int[] types = null;
                string[] names = null;
                Check(model.SelectObj.GetSelected(ref count, ref types, ref names), "reading the selection", "SelectObj.GetSelected");
                if (count < 0 || count > 0 && (types == null || names == null ||
                    types.Length < count || names.Length < count))
                    throw new InvalidDataException("SAP2000 returned an incomplete selection.");
                Types = (types ?? new int[0]).Take(count).ToArray();
                Names = (names ?? new string[0]).Take(count).ToArray();
                Areas = Enumerable.Range(0, count).Where(i => Types[i] == AreaObjectType)
                    .Select(i => Names[i]).Distinct().ToArray();
                Points = Enumerable.Range(0, count).Where(i => Types[i] == PointObjectType)
                    .Select(i => Names[i]).Distinct().ToArray();
                Frames = Enumerable.Range(0, count).Where(i => Types[i] == FrameObjectType)
                    .Select(i => Names[i]).Distinct().ToArray();
                Links = Types.Count(type => type == LinkObjectType);
                identity = Areas.Length > MaximumLiveAreas ? null :
                    Enumerable.Range(0, count).Select(i => Types[i] + ":" + Names[i])
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
            }

            public bool SameObjects(LiveSelection other)
            {
                return other != null && identity != null && other.identity != null &&
                    identity.SequenceEqual(other.identity, StringComparer.OrdinalIgnoreCase);
            }
        }

        private sealed class DialogModelLock : IDisposable
        {
            private readonly cSapModel model;
            private readonly bool active;
            private readonly bool alreadyLocked;
            private readonly string modelPath;
            private bool owned, temporaryUnlock, externalUnlock;

            internal DialogModelLock(cSapModel model, bool active)
            {
                this.model = model;
                this.active = active;
                if (!active) return;
                modelPath = model.GetModelFilename(true);
                alreadyLocked = model.GetModelIsLocked();
                if (!alreadyLocked)
                {
                    Check(model.SetModelIsLocked(true), "locking the model while MESHing is open",
                        "cSapModel.SetModelIsLocked");
                    if (!model.GetModelIsLocked())
                        throw new InvalidOperationException("SAP2000 did not lock the model for MESHing.");
                    owned = true;
                }
            }

            internal bool Active { get { return active; } }
            internal bool ExternalUnlockObserved { get { return externalUnlock; } }

            internal void AcknowledgeExternalRelock()
            {
                if (!active || !externalUnlock) return;
                if (!string.Equals(model.GetModelFilename(true), modelPath, StringComparison.OrdinalIgnoreCase) ||
                    !model.GetModelIsLocked())
                    throw new InvalidOperationException("The original model must be locked before continuing MESHing.");
                externalUnlock = false;
            }

            internal void CheckReady()
            {
                if (!active) return;
                if (!string.Equals(model.GetModelFilename(true), modelPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The model changed while MESHing was open. Close and reopen MESHing for the current model.");
                if (alreadyLocked)
                    throw new InvalidOperationException("The model was already locked when MESHing opened. Close MESHing and unlock it before meshing.");
                if (!temporaryUnlock && !model.GetModelIsLocked())
                {
                    // Re-lock promptly, but never treat an observed external
                    // unlock as proof that no edit occurred in the meantime.
                    Check(model.SetModelIsLocked(true), "relocking an externally unlocked model",
                        "cSapModel.SetModelIsLocked");
                    externalUnlock = true;
                }
                if (externalUnlock)
                    throw new InvalidOperationException("The model was unlocked while MESHing was open. It has been relocked; close and reopen MESHing before meshing.");
            }

            internal void UnlockForWork()
            {
                if (!active) return;
                CheckReady();
                Check(model.SetModelIsLocked(false), "unlocking the model for MESHing",
                    "cSapModel.SetModelIsLocked");
                if (model.GetModelIsLocked())
                    throw new InvalidOperationException("SAP2000 did not unlock the model for MESHing.");
                temporaryUnlock = true;
            }

            internal void RelockAfterSave()
            {
                if (!active) return;
                if (!string.Equals(model.GetModelFilename(true), modelPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The active model changed during the pre-mesh save.");
                // SAP2000 27.1 unlocks the model as a side effect of File.Save,
                // even when that method successfully saves a locked model.
                if (!model.GetModelIsLocked())
                    Check(model.SetModelIsLocked(true), "relocking the model after saving it",
                        "cSapModel.SetModelIsLocked");
                if (!model.GetModelIsLocked())
                    throw new InvalidOperationException("SAP2000 did not relock the model after saving it.");
            }

            internal void RelockAfterWork()
            {
                if (!active || !temporaryUnlock) return;
                if (!string.Equals(model.GetModelFilename(true), modelPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The model changed while MESHing was modifying it; the new model was not locked.");
                Check(model.SetModelIsLocked(true), "relocking the model after MESHing",
                    "cSapModel.SetModelIsLocked");
                if (!model.GetModelIsLocked())
                    throw new InvalidOperationException("SAP2000 did not relock the model after MESHing.");
                temporaryUnlock = false;
            }

            internal void RelockAfterRestore()
            {
                if (!active) return;
                if (!string.Equals(model.GetModelFilename(true), modelPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The active model changed while restoring the pre-mesh file.");
                if (externalUnlock)
                    throw new InvalidOperationException("An external model unlock was observed during restoration.");
                // OpenFile may leave the restored model unlocked even when the
                // pre-mesh transaction itself finished with the lock in place.
                // This is still MESHing's own file reload, not a user click.
                if (!model.GetModelIsLocked())
                    Check(model.SetModelIsLocked(true), "relocking the reopened pre-mesh model",
                        "cSapModel.SetModelIsLocked");
                if (!model.GetModelIsLocked())
                    throw new InvalidOperationException("SAP2000 did not lock the reopened pre-mesh model.");
            }

            public void Dispose()
            {
                if (!owned || !string.Equals(model.GetModelFilename(true), modelPath,
                        StringComparison.OrdinalIgnoreCase)) return;
                if (model.GetModelIsLocked())
                    Check(model.SetModelIsLocked(false), "restoring the unlocked state when MESHing closes",
                        "cSapModel.SetModelIsLocked");
            }
        }

        private static void RunWithUnitContext(cSapModel model, Action scopeNewModelUnits)
        {
            Console.WriteLine("mesh-stage=reading-model");
            eUnits presentUnits = model.GetPresentUnits();
            string lengthUnit = LengthUnit(presentUnits);
            LiveSelection selection = new LiveSelection(model);
            string selectionModelPath = model.GetModelFilename(true);
            Console.WriteLine("mesh-stage=selection-read objects=" + selection.Types.Length);
            string[] areas = selection.Areas, selectedPoints = selection.Points, selectedFrames = selection.Frames;
            int[] selectedTypes = selection.Types;
            string[] selectedNames = selection.Names;

            Console.WriteLine("mesh-stage=building-area-contexts areas=" + areas.Length);
            List<AreaContext> initialContexts;
            bool hasFrames;
            string[] pointGroups, openingGroups;
            SymmetryAxis[] symmetryAxes;
            var startupCancellation = new CancellationController(useItalian, null,
                Process.GetCurrentProcess().MainWindowHandle);
            Action startupCheckpoint = delegate
            {
                if (startupCancellation.Requested)
                    throw new OperationCanceledException("Mesh preparation cancelled by the user.");
            };
            try
            {
                initialContexts = areas.Length > 0
                    ? BuildAreaContexts(model, areas, selectedPoints, selectedFrames, startupCheckpoint)
                    : new List<AreaContext>();
                if (initialContexts.Count > 0)
                    ValidateExistingConstraintPositions(model, initialContexts, selectedPoints,
                        selectedFrames, Math.Max(Tolerance, 1e-6));
                Console.WriteLine("mesh-stage=area-contexts-ready");
                hasFrames = initialContexts.Count > 1 || initialContexts.Any(context => context.FrameConstraints.Count > 0);
                pointGroups = ReadPointGroups(model, startupCheckpoint);
                openingGroups = ReadOpeningGroups(model, startupCheckpoint);
                symmetryAxes = DetectSymmetryAxes(initialContexts, startupCheckpoint);
                startupCheckpoint();
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("mesh-stage=selection-preparation-cancelled");
                return;
            }
            finally { startupCancellation.Stop(); }
            Console.WriteLine("mesh-stage=options-ready");

            IntPtr sapWindow = Process.GetCurrentProcess().MainWindowHandle;
            bool modelHasSavedFile = HasSavedModelFile(model);
            var sessionLock = new DialogModelLock(model, callbackReleasedInSap() && modelHasSavedFile);
            MeshOptionsDialog dialog = null;
            try
            {
                using (dialog = new MeshOptionsDialog(lengthUnit, hasFrames, pointGroups, openingGroups,
                    symmetryAxes, areas.Length > 0))
                {
                if (callbackReleasedInSap()) interactiveDialog = dialog;
                dialog.SetSelectionCounts(areas.Length,
                    initialContexts.Count(context => context.CornerNames.Length == 4),
                    initialContexts.Count(context => context.CornerNames.Length == 3),
                    initialContexts.Count(context => context.CornerNames.Length > 4),
                    selectedFrames.Length, selectedTypes.Count(type => type == LinkObjectType), selectedPoints.Length);
                dialog.SetRequiresSavedModel(!modelHasSavedFile);
                bool leaveCompletedDialog = Environment.GetEnvironmentVariable("SAP_QUAD_LEAVE_COMPLETED_DIALOG") == "1";
                bool automatedExample = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SIZE"));
                // Native ownership keeps MESHing above its host without placing
                // either window above unrelated applications.
                dialog.ShowInTaskbar = false;
                if (sapWindow != IntPtr.Zero) dialog.Show(new WindowOwner(sapWindow));
                else dialog.Show();
                dialog.SetGenerationFilterCheck(delegate
                {
                    if (!IsSelectionOnlyTableFilterActive(model)) return true;
                    dialog.ShowSelectionOnlyFilterWarning(HostProductName());
                    return false;
                });
                if (callbackReleasedInSap()) dialog.AllowViewportInteraction(sapWindow);
                Console.WriteLine("mesh-stage=dialog-visible");
                if (!modelHasSavedFile && !automatedExample) dialog.ShowNeverSavedModelWarning();
                if (automatedExample) dialog.ApplyAutomatedOptions();
                bool oversizedSelection = false;
                Action<bool> refreshSelection = delegate(bool force)
                {
                    var refreshClock = Stopwatch.StartNew();
                    bool savedNow = HasSavedModelFile(model);
                    if (savedNow && !modelHasSavedFile)
                    {
                        // A new, unsaved model is deliberately left unlocked
                        // so the user can save it through the host. Acquire
                        // MESHing's lock only after the first real SDB/BDB exists.
                        var acquired = new DialogModelLock(model, callbackReleasedInSap());
                        sessionLock.Dispose();
                        sessionLock = acquired;
                        modelHasSavedFile = true;
                        dialog.SetRequiresSavedModel(false);
                        force = true;
                    }
                    else if (!savedNow) dialog.SetRequiresSavedModel(true);
                    if (sessionLock.Active) sessionLock.CheckReady();
                    LiveSelection next = new LiveSelection(model);
                    if (next.Areas.Length > LiveSelection.MaximumLiveAreas)
                    {
                        if (!oversizedSelection || next.Areas.Length != areas.Length || force)
                        {
                            selection = next;
                            areas = next.Areas;
                            oversizedSelection = true;
                            dialog.UpdateOversizedSelection(areas.Length, next.Frames.Length,
                                next.Links, next.Points.Length, LiveSelection.MaximumLiveAreas);
                            RecordPhase("csharp-selection-phases.csv", "oversized-selection-skipped-contexts", refreshClock);
                        }
                        return;
                    }
                    string nextModelPath = model.GetModelFilename(true);
                    if (!string.Equals(nextModelPath, selectionModelPath, StringComparison.OrdinalIgnoreCase))
                        scopeNewModelUnits();
                    string nextLengthUnit = LengthUnit(model.GetPresentUnits());
                    if (!force && next.SameObjects(selection) &&
                        string.Equals(nextModelPath, selectionModelPath, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(nextLengthUnit, dialog.LengthUnit, StringComparison.OrdinalIgnoreCase)) return;
                    oversizedSelection = false;
                    List<AreaContext> updated = next.Areas.Length > 0
                        ? BuildAreaContexts(model, next.Areas, next.Points, next.Frames)
                        : new List<AreaContext>();
                    if (updated.Count > 0)
                        ValidateExistingConstraintPositions(model, updated, next.Points,
                            next.Frames, Math.Max(Tolerance, 1e-6));
                    string[] updatedPointGroups = ReadPointGroups(model);
                    string[] updatedOpeningGroups = ReadOpeningGroups(model);
                    SymmetryAxis[] updatedAxes = DetectSymmetryAxes(updated);
                    dialog.ChangeLengthUnit(nextLengthUnit);
                    selection = next;
                    initialContexts = updated;
                    selectionModelPath = nextModelPath;
                    areas = next.Areas; selectedPoints = next.Points; selectedFrames = next.Frames;
                    selectedTypes = next.Types; selectedNames = next.Names;
                    symmetryAxes = updatedAxes;
                    dialog.UpdateLiveSelection(areas.Length,
                        updated.Count(context => context.CornerNames.Length == 4),
                        updated.Count(context => context.CornerNames.Length == 3),
                        updated.Count(context => context.CornerNames.Length > 4),
                        selectedFrames.Length, next.Links, selectedPoints.Length,
                        updated.Count > 1 || updated.Any(context => context.FrameConstraints.Count > 0),
                        updatedPointGroups, updatedOpeningGroups, updatedAxes);
                    Console.WriteLine("mesh-stage=selection-updated objects=" + next.Types.Length +
                        " areas=" + areas.Length);
                    RecordPhase("csharp-selection-phases.csv", "refresh-selection-and-contexts", refreshClock);
                };
                var selectionClock = Stopwatch.StartNew();
                var lockClock = Stopwatch.StartNew();
                while (dialog.Visible)
                {
                    while (dialog.Visible && !dialog.StartRequested)
                    {
                        Application.DoEvents();
                        if (dialog.DetectRegionalChange())
                        { dialog.AbandonForRegionalChange(); break; }
                        if (sessionLock.Active && lockClock.ElapsedMilliseconds >= 50)
                        {
                            try { sessionLock.CheckReady(); }
                            catch (Exception error)
                            {
                                if (sessionLock.ExternalUnlockObserved)
                                    dialog.ReportModelLockViolation(error.Message, sessionLock.AcknowledgeExternalRelock);
                                else dialog.SelectionRefreshFailed(error.Message);
                            }
                            finally { lockClock.Restart(); }
                        }
                        if (!automatedExample && dialog.CanRefreshSelection &&
                            selectionClock.ElapsedMilliseconds >= (oversizedSelection ? 2000 : 350))
                        {
                            try { refreshSelection(false); }
                            catch (Exception error)
                            {
                                 if (sessionLock.ExternalUnlockObserved)
                                     dialog.ReportModelLockViolation(error.Message, sessionLock.AcknowledgeExternalRelock);
                                else dialog.SelectionRefreshFailed(error.Message);
                            }
                            selectionClock.Restart();
                        }
                        Thread.Sleep(20);
                    }
                    if (!dialog.Visible) return;
                    if (!automatedExample)
                    {
                        try { refreshSelection(true); }
                        catch (Exception error)
                        {
                            dialog.SelectionRefreshFailed(error.Message);
                            continue;
                        }
                        if (!modelHasSavedFile) { dialog.SetRequiresSavedModel(true); continue; }
                        if (areas.Length == 0) { dialog.SelectionUnavailable(); continue; }
                    }

                    string previewModelPath = model.GetModelFilename(true);
                    dialog.SetRegionalModelPath(previewModelPath);
                    dialog.SetRegionalSafetyLock(() =>
                    {
                        if (sessionLock.Active) sessionLock.RelockAfterWork();
                        if (string.Equals(model.GetModelFilename(true), previewModelPath,
                                StringComparison.OrdinalIgnoreCase) && !model.GetModelIsLocked())
                            Check(model.SetModelIsLocked(true), "locking the unsafe preview until SAP exits",
                                "cSapModel.SetModelIsLocked");
                    });
                    LiveSelection appliedSelection = null;

                    string[] holePointNames = new string[0];
                    List<AreaContext> contexts = null;
                    var results = new List<MeshAttemptResult>();
                    FastTableSnapshot fastSnapshot = null;
                    PointSnapshot[] fastOriginalPoints = null;
                    ModelCsvSnapshot csvSnapshot = null;
                    DiskModelCheckpoint diskCheckpoint = null;
                    bool previewLockedByPlugin = false;
                    string symmetryNotice = null;
                    string automaticChoice = null;
                    bool requestedRegular = dialog.PreferRegularMesh, requestedRefined = dialog.StressRefinement;
                    bool requestedSymmetry = dialog.Symmetric, requestedBands = dialog.StructuredSectorBands;
                    bool requestedStrict = dialog.StrictMaximumSize;
                    bool useTabularUndo = dialog.FastMode, insertionStarted = false;
                    // The numeric FAST transaction is verified on SAP2000 and
                    // CSiBridge; ETABS keeps its separate table/commit path.
                    bool directCommit = dialog.FastMode &&
                        (HostProductName() == "SAP2000" || HostProductName() == "CSiBridge") &&
                        Environment.GetEnvironmentVariable("SAP_QUAD_TRIAL_DIRECT_COMMIT") != "0";
                    bool designTransferLoss = false;
                    try
                    {
                        dialog.BlockHostForOperation();
                        dialog.BeginWork("Preparing geometry...");
                        var generationClock = Stopwatch.StartNew();
                        dialog.ThrowIfCancellationRequested();
                        if (sessionLock.Active) sessionLock.CheckReady();
                        else if (model.GetModelIsLocked())
                            throw new InvalidOperationException(L("The model is locked. Unlock it before generating the mesh.",
                                "Il modello e bloccato. Sbloccarlo prima di generare la mesh."));
                        ValidateExistingConstraintPositions(model, initialContexts, selectedPoints,
                            selectedFrames, Math.Max(Tolerance, Math.Max(1, dialog.MaxSize) * 1e-6));
                        RecordGenerationPhase("validate-input-joints", generationClock);
                        string backupPath = dialog.CreateBackup
                            ? CreateNumberedBackup(SavedModelPath(model)) : null;
                        RecordGenerationPhase("create-numbered-backup", generationClock);
                        string savedPath = SavedModelPath(model);
                        if (string.IsNullOrEmpty(savedPath) || !File.Exists(savedPath))
                            throw new InvalidOperationException(L("Save the model to an SDB/BDB file before generating the mesh.",
                                "Salvare il modello in un file SDB/BDB prima di generare la mesh."));
                        dialog.SetIndeterminate(L("Saving the model before meshing...", "Salvataggio del modello prima della meshatura..."));
                        int saveResult;
                        dialog.MarkRegionalSaveStarted();
                        try { saveResult = model.File.Save(savedPath); }
                        finally { sessionLock.RelockAfterSave(); }
                        Check(saveResult, "saving the current model before generating the mesh",
                            "cSapModel.File.Save");
                        if (!string.Equals(model.GetModelFilename(true), savedPath, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("SAP2000 changed the active model during the pre-mesh save.");
                        RecordGenerationPhase("save-current-model", generationClock);
                        dialog.SetIndeterminate(directCommit ?
                            L("Protecting the saved model for Undo...",
                                "Protezione del modello salvato per Annulla...") :
                            L("Capturing the model snapshot for Undo...",
                                "Acquisizione dello snapshot del modello per Annulla..."));
                        TraceCancellationStage("snapshot-start");
                        diskCheckpoint = DiskModelCheckpoint.Capture(model, savedPath);
                        TraceCancellationStage("snapshot-complete");
                        RecordGenerationPhase("protect-saved-model", generationClock);
                        if (!directCommit)
                        {
                            csvSnapshot = ModelCsvSnapshot.Capture(model, dialog.PumpCancellation);
                            RecordGenerationPhase("capture-rollback-snapshot", generationClock);
                        }
                        holePointNames = dialog.GenerateCircularHoles
                            ? ReadGroupPoints(model, dialog.HoleGroup)
                            : new string[0];
                        MeshCandidate chosen = null;
                        Exception requestedError = null;
                        try
                        {
                            TraceCancellationStage("candidate-start");
                            chosen = PrepareCandidate(model, areas, selectedPoints, selectedFrames, holePointNames,
                                symmetryAxes, dialog);
                            TraceCancellationStage("candidate-complete");
                            RecordGenerationPhase("build-gmsh-candidates", generationClock);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception exception)
                        {
                            if (!IsRecoverableMeshFailure(exception)) throw;
                            requestedError = exception;
                        }
                        if (requestedError != null)
                        {
                            // A selected Gmsh algorithm must never silently change to a different one.
                            throw requestedError;
                        }
                        string[] designWarnings = (csvSnapshot == null
                            ? FindDesignOverwriteTables(model) : csvSnapshot.UnpreservedDesignTables).Concat(chosen.Meshes
                            .SelectMany(item => item.Loads.UnpreservedDesignTables))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        if (designWarnings.Length != 0)
                            dialog.ShowPreservationWarning(DesignOverwriteWarning(designWarnings, backupPath));
                        dialog.SetInsertEdgeNodes(chosen.InsertEdgeNodes);
                        dialog.SetMeshingStrategy(chosen.Regular, chosen.Refined, chosen.Symmetric, chosen.Bands);
                        contexts = chosen.Contexts;
                        symmetryNotice = chosen.SymmetryNotice;
                        PointCoordinateIndex pointIndex = ReadExactPointCoordinates(model, dialog.MaxSize, contexts);
                        // Both insertion paths must reuse an existing joint or
                        // add a joint only at a previously unoccupied position.
                        // In particular, never write a second QTP at the same
                        // coordinates to preserve a disconnected component.
                        pointIndex.ValidateNewJointPositions(chosen.Meshes);
                        RecordGenerationPhase("verify-reused-joints-and-polygon-schema", generationClock);
                        if (Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_UI_PREVIEW") == "1")
                            Console.WriteLine("mesh-topology-unique-new-joints=pass");
                        bool useFastInsertion = dialog.FastMode;
                        bool polygonCsvInsertion = useFastInsertion && pointIndex.IncompletePolygonAreas > 0;
                        if (useFastInsertion)
                        {
                            // A requested FAST operation must not silently become
                            // per-object Standard insertion or lose assignments.
                            SupportsFastTableInsertion(model);
                            if (polygonCsvInsertion && (!directCommit || !SupportsPolygonCsvInsertion(model)))
                                throw new InvalidOperationException("FAST polygon-continuation CSV import is not " +
                                    "verified on this host/version. The model was not modified.");
                        }
                        useTabularUndo = useFastInsertion;
                        if (useFastInsertion)
                        {
                            fastOriginalPoints = CapturePointSnapshots(model,
                                contexts.SelectMany(context => context.CornerNames).Concat(selectedPoints).Concat(holePointNames));
                        }
                        dialog.SetMeshingAreas(0, contexts.Count);
                        if (!useFastInsertion)
                        {
                            dialog.ThrowIfCancellationRequested();
                            sessionLock.UnlockForWork();
                            dialog.MarkRegionalModelModified();
                            insertionStarted = true;
                            foreach (PreparedMesh prepared in chosen.Meshes)
                            {
                                results.Add(new MeshAttemptResult(ApplyMesh(model, prepared.SourceArea, prepared.Property,
                                    prepared.Angle, prepared.Mass, prepared.EdgeConstraint, prepared.Loads, prepared.Mesh,
                                    dialog, prepared.OriginalPoints, pointIndex), prepared.Quality));
                                dialog.SetMeshingAreas(results.Count, contexts.Count);
                                dialog.ThrowIfCancellationRequested();
                            }
                        }
                        if (useFastInsertion)
                        {
                            dialog.ThrowIfCancellationRequested();
                            dialog.ShowBatchAreas(contexts.Count, true);
                            if (chosen.Meshes.Count > 1 || directCommit)
                            {
                                var snapshotClock = Stopwatch.StartNew();
                                fastSnapshot = CaptureFastSnapshot(model);
                                RecordFastPhase("batch-read-snapshot", snapshotClock);
                            }
                            dialog.ThrowIfCancellationRequested();
                            sessionLock.UnlockForWork();
                            dialog.MarkRegionalModelModified();
                            insertionStarted = true;
                            List<MeshOperation> operations = chosen.Meshes.Count == 1 && !directCommit
                                ? new List<MeshOperation> { ApplyMeshFast(model, chosen.Meshes[0].SourceArea,
                                    chosen.Meshes[0].Property, chosen.Meshes[0].Angle, chosen.Meshes[0].Mass,
                                    chosen.Meshes[0].EdgeConstraint, chosen.Meshes[0].Loads, chosen.Meshes[0].Mesh,
                                    dialog, chosen.Meshes[0].OriginalPoints, pointIndex) }
                                : ApplyMeshesFast(model, chosen.Meshes, dialog, pointIndex, fastSnapshot,
                                    directCommit, polygonCsvInsertion);
                            if (operations.Any(operation => !operation.FastMode))
                                throw new InvalidDataException("FAST returned a Standard insertion unexpectedly.");
                            Console.WriteLine("mesh-insertion=fast areas=" + operations.Count);
                            for (int i = 0; i < operations.Count; i++)
                                results.Add(new MeshAttemptResult(operations[i], chosen.Meshes[i].Quality));
                            dialog.SetMeshingAreas(results.Count, contexts.Count);
                            dialog.ThrowIfCancellationRequested();
                        }
                        RecordGenerationPhase("insert-generated-mesh", generationClock);
                        dialog.SetConfirmationOnlyApply(directCommit);
                        designTransferLoss = TransferAreaDesignProceduresOnce(model, chosen.Meshes, dialog);
                        RecordGenerationPhase("batch-design-procedure-import", generationClock);
                        if (directCommit)
                        {
                            dialog.ThrowIfCancellationRequested();
                            CommitNumericMeshes(model, results, dialog.ThrowIfRegionalSettingsChanged);
                            RecordGenerationPhase("commit-original-areas", generationClock);
                            Console.WriteLine("mesh-direct-commit=complete");
                        }
                        if (sessionLock.Active) sessionLock.RelockAfterWork();
                        else Check(model.SetModelIsLocked(true), "locking the generated preview against edits",
                            "cSapModel.SetModelIsLocked");
                        previewLockedByPlugin = model.GetModelIsLocked();
                        if (!previewLockedByPlugin)
                            throw new InvalidOperationException("SAP2000 did not lock the generated preview.");
                        Console.WriteLine("mesh-preview-locked=true");
                        MeshQualityReport combinedQuality = chosen.OverallQuality;
                        int generatedAreaCount = results.Sum(result => result.Operation.GeneratedAreas.Count);
                        MeshInventory afterMeshing = CaptureMeshInventory(model, directCommit ? null : results);
                        RecordGenerationPhase("count-preview-shell-vertices", generationClock);
                        dialog.ThrowIfCancellationRequested();
                        var applyClock = new Stopwatch();
                        dialog.SetApplyBackupInfo(() => backupPath);
                        dialog.SetOnApplied(() =>
                        {
                            dialog.ThrowIfRegionalSettingsChanged();
                            sessionLock.RelockAfterWork();
                            if (!automatedExample)
                                try { appliedSelection = new LiveSelection(model); }
                                catch (Exception error) { LogPluginException(error); }
                        });
                        dialog.SetReleaseCompletedUndo(() =>
                        {
                            if (csvSnapshot != null) csvSnapshot.Delete();
                            diskCheckpoint.Delete();
                        });
                        dialog.SetCompletedModelGuard(() => string.Equals(model.GetModelFilename(true),
                            previewModelPath, StringComparison.OrdinalIgnoreCase));
                        dialog.SetReleasePreviewModelLock(mode =>
                        {
                            dialog.ThrowIfRegionalSettingsChanged();
                            if (!previewLockedByPlugin) return;
                            if (!string.Equals(model.GetModelFilename(true), previewModelPath,
                                    StringComparison.OrdinalIgnoreCase) || !model.GetModelIsLocked())
                                throw new InvalidOperationException(L(
                                    "The model was unlocked or changed during preview. Apply/Undo was stopped to avoid altering an unexpected model.",
                                    "Il modello è stato sbloccato o cambiato durante l'anteprima. Applica/Annulla è stato fermato per non modificare un modello inatteso."));
                            if (sessionLock.Active) sessionLock.UnlockForWork();
                            else Check(model.SetModelIsLocked(false), "unlocking preview for " + mode,
                                "cSapModel.SetModelIsLocked");
                            if (model.GetModelIsLocked())
                                throw new InvalidOperationException("SAP2000 kept the preview locked during " + mode + ".");
                            previewLockedByPlugin = false;
                            Console.WriteLine("mesh-preview-unlocked-for=" + mode);
                        });
                        dialog.SetConfirmationOnlyApply(directCommit);
                        dialog.Complete(combinedQuality, generatedAreaCount, afterMeshing, symmetryNotice, automaticChoice,
                            delegate
                            {
                                try
                                {
                                dialog.ThrowIfRegionalSettingsChanged();
                                Stopwatch reloadClock = Stopwatch.StartNew();
                                // Generate has already completed the mesh on every
                                // host. Reopen its saved pre-mesh model for Undo,
                                // including unchanged and explicitly Standard areas.
                                diskCheckpoint.Restore(model);
                                RecordPhase("csharp-undo-phases.csv", "reload-pre-mesh-model", reloadClock);
                                dialog.ThrowIfRegionalSettingsChanged();
                                RestoreSelection(model, selectedTypes, selectedNames);
                                model.View.RefreshView(0, false);
                                RecordPhase("csharp-undo-phases.csv", "restore-selection-and-view", reloadClock);
                                if (csvSnapshot != null) csvSnapshot.Delete();
                                diskCheckpoint.Delete();
                                RecordPhase("csharp-undo-phases.csv", "delete-pre-mesh-copy", reloadClock);
                                }
                                finally
                                {
                                    sessionLock.RelockAfterWork();
                                    sessionLock.RelockAfterRestore();
                                }
                            },
                            delegate
                            {
                                applyClock.Restart();
                                dialog.ThrowIfRegionalSettingsChanged();
                                if (directCommit)
                                {
                                    RecordApplyPhase("confirm-existing-mesh", applyClock);
                                    return;
                                }
                                CommitAttempts(model, results, dialog.ThrowIfRegionalSettingsChanged);
                                RecordApplyPhase("commit-source-areas", applyClock);
                                dialog.ThrowIfRegionalSettingsChanged();
                                RenameGeneratedPointsAfterApply(model, results, dialog.ThrowIfRegionalSettingsChanged);
                                RecordApplyPhase("rename-generated-joints", applyClock);
                                dialog.ThrowIfRegionalSettingsChanged();
                                RenameGeneratedAreasAfterApply(model, results, chosen.Meshes,
                                    dialog.ThrowIfRegionalSettingsChanged);
                                RecordApplyPhase("rename-generated-areas", applyClock);
                                dialog.ThrowIfRegionalSettingsChanged();
                                MeshInventory committedInventory = VerifyAppliedObjectNames(model, results, afterMeshing);
                                csvSnapshot.MarkApplied();
                                RecordApplyPhase("verify-applied-names", applyClock);
                                try
                                {
                                    for (int i = 0; i < chosen.Meshes.Count; i++)
                                    {
                                        MeshOperation operation = results[i].Operation;
                                        if (operation.FastMode)
                                            chosen.Meshes[i].Loads.RemapChildName(operation.PrimaryGeneratedArea,
                                                operation.SourceArea);
                                    }
                                    RecordApplyPhase("finish-design-assignments", applyClock);
                                }
                                catch
                                {
                                    dialog.ThrowIfRegionalSettingsChanged();
                                    // Commit has already removed the source areas:
                                    // restore the full CSV snapshot rather than
                                    // leaving a partly assigned final mesh.
                                    csvSnapshot.Restore(model,
                                        results.SelectMany(item => item.Operation.GeneratedAreas.Concat(new[] {
                                            item.Operation.SourceArea, item.Operation.BackupArea, item.Operation.CommitBackupArea
                                        })).Where(name => !string.IsNullOrEmpty(name)),
                                        results.SelectMany(item => item.Operation.GeneratedPoints), useTabularUndo,
                                        dialog.ThrowIfRegionalSettingsChanged);
                                    throw;
                                }
                                // The preview already counted every child's corners. Apply
                                // renames those objects and removes only the source areas;
                                // the verified post-commit names/counts suffice for the UI.
                                dialog.SetGeneratedInventory(committedInventory);
                                RecordApplyPhase("reuse-verified-mesh-inventory", applyClock);
                                model.View.RefreshView(0, false);
                                RecordApplyPhase("refresh-sap-view", applyClock);
                            },
                            delegate
                            {
                                try
                                {
                                dialog.ThrowIfRegionalSettingsChanged();
                                if (directCommit) diskCheckpoint.Restore(model);
                                else csvSnapshot.Restore(model,
                                    results.SelectMany(item => item.Operation.Unchanged
                                        ? Enumerable.Empty<string>()
                                        : item.Operation.GeneratedAreas.Concat(new[] {
                                            item.Operation.SourceArea, item.Operation.BackupArea,
                                            item.Operation.CommitBackupArea
                                        })).Where(name => !string.IsNullOrEmpty(name)),
                                    results.SelectMany(item => item.Operation.GeneratedPoints), useTabularUndo,
                                    dialog.ThrowIfRegionalSettingsChanged);
                                dialog.ThrowIfRegionalSettingsChanged();
                                RestoreSelection(model, selectedTypes, selectedNames);
                                model.View.RefreshView(0, false);
                                }
                                finally { sessionLock.RelockAfterWork(); }
                            }, directCommit ? diskCheckpoint.Path : csvSnapshot.Path);
                        // Generate also commits the mesh. Keep the pre-mesh
                        // checkpoint until Undo, the next Generate or Close.
                        if (dialog.Visible && !dialog.IsDisposed)
                        {
                            dialog.CommitForGeneration();
                            if (dialog.UndoFailure != null) throw dialog.UndoFailure;
                            if (!automatedExample) refreshSelection(true);
                        }
                        model.View.RefreshView(0, false);
                        if (automaticChoice != null && !automatedExample)
                            dialog.ShowAutomaticChoice(automaticChoice, generatedAreaCount, chosen.TriangleCount,
                                selectedPoints.Length, selectedFrames.Length);
                        // Complete() pumps WinForms messages. An immediate
                        // Apply/Undo/Close can dispose this dialog before it
                        // returns; never recreate its handle just to focus it.
                        if (dialog.Visible && !dialog.IsDisposed)
                        {
                            dialog.AllowViewportInteraction(sapWindow);
                            if (!automatedExample || leaveCompletedDialog)
                            {
                                dialog.BringToFront();
                                dialog.Activate();
                                SetForegroundWindow(dialog.Handle);
                            }
                        }
                        if (automatedExample && Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_UNDO") == "1")
                        {
                            if (!dialog.Applied && !directCommit && Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_APPLY_BEFORE_UNDO") == "1")
                            {
                                int beforeApply = 0, afterApply = 0;
                                string[] inspectedAreas = null;
                                Check(model.AreaObj.GetNameList(ref beforeApply, ref inspectedAreas), "counting preview areas", "AreaObj.GetNameList");
                                int appliedOperations = results.Count(item => item.Operation.Active);
                                dialog.ApplyForAutomation();
                                if (!dialog.Applied || !dialog.Visible)
                                    throw new InvalidOperationException("The mesh was not applied with the options window still open.");
                                Check(model.AreaObj.GetNameList(ref afterApply, ref inspectedAreas), "counting applied areas", "AreaObj.GetNameList");
                                if (afterApply != beforeApply - appliedOperations)
                                    throw new InvalidDataException("Apply retained the original " + HostProductName() + " area objects: preview " +
                                        beforeApply + ", applied " + afterApply + ", active sources " + appliedOperations);
                            }
                            dialog.UndoForAutomation();
                            if (dialog.UndoFailure != null) throw dialog.UndoFailure;
                            if (!directCommit) csvSnapshot.Delete();
                            diskCheckpoint.Delete();
                            dialog.Close();
                            return;
                        }
                    }
                    catch (Exception exception)
                    {
                        bool cancelling = exception is OperationCanceledException || dialog.CancelRequested;
                        if (dialog.DetectRegionalChange())
                        {
                            // Neither CSV nor object rollback is safe with changed
                            // SAP table formatting. Keep the pre-mesh SDB/BDB intact.
                            dialog.AbandonForRegionalChange();
                            return;
                        }
                        if (!(exception is OperationCanceledException)) LogPluginException(exception);
                        if (previewLockedByPlugin && string.Equals(model.GetModelFilename(true),
                                previewModelPath, StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                if (sessionLock.Active) sessionLock.UnlockForWork();
                                else Check(model.SetModelIsLocked(false), "unlocking failed preview for restoration",
                                    "cSapModel.SetModelIsLocked");
                                previewLockedByPlugin = false;
                            }
                            catch (Exception unlock)
                            {
                                exception = new InvalidOperationException(exception.Message +
                                    " Preview unlock failed: " + unlock.Message, exception);
                            }
                        }
                        try
                        {
                            if (diskCheckpoint != null && !diskCheckpoint.Restored &&
                                (directCommit || cancelling))
                            {
                                diskCheckpoint.Restore(model);
                                if (csvSnapshot != null) csvSnapshot.Delete();
                                diskCheckpoint.Delete();
                            }
                            else if (!directCommit && (csvSnapshot == null ||
                                (!csvSnapshot.WasApplied && !csvSnapshot.RestoreAttempted)))
                            {
                                if (csvSnapshot != null && useTabularUndo && insertionStarted)
                                    csvSnapshot.Restore(model, new string[0], new string[0], true,
                                        dialog.ThrowIfRegionalSettingsChanged);
                                else
                                    UndoAttempts(model, results, fastSnapshot, fastOriginalPoints,
                                        dialog.ThrowIfRegionalSettingsChanged);
                                if (csvSnapshot != null) csvSnapshot.Delete();
                                if (diskCheckpoint != null) diskCheckpoint.Delete();
                            }
                        }
                        catch (Exception rollback)
                        {
                            exception = new InvalidOperationException(exception.Message + " Rollback failed: " + rollback.Message);
                        }
                        if (dialog.DetectRegionalChange())
                        { dialog.AbandonForRegionalChange(); return; }
                        try { RestoreSelection(model, selectedTypes, selectedNames); }
                        catch (Exception selectionRestore)
                        {
                            exception = new InvalidOperationException(exception.Message + " Selection restore failed: " + selectionRestore.Message);
                        }
                        dialog.SetInsertEdgeNodes(requestedStrict);
                        dialog.SetMeshingStrategy(requestedRegular, requestedRefined, requestedSymmetry, requestedBands);
                        model.View.RefreshView(0, false);
                        if (exception is OperationCanceledException)
                        {
                            dialog.Cancelled();
                            if (automatedExample) { dialog.Close(); return; }
                            continue;
                        }
                        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAP_QUAD_AUTOMATION_ERROR")))
                            throw exception;
                        dialog.Fail(exception.Message);
                        continue;
                    }
                    finally
                    {
                        try { sessionLock.RelockAfterWork(); }
                        finally { dialog.UnblockHostAfterOperation(); }
                    }
                    if (automatedExample && !leaveCompletedDialog)
                    {
                        if (Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_CLOSE_UNAPPLIED") != "1")
                            dialog.ApplyForAutomation();
                        dialog.Close();
                    }
                    while (dialog.Visible && !dialog.RetryRequested && dialog.UndoFailure == null)
                    {
                        if (dialog.DetectRegionalChange())
                        { dialog.AbandonForRegionalChange(); break; }
                        if (sessionLock.Active && lockClock.ElapsedMilliseconds >= 50)
                        {
                            try { sessionLock.CheckReady(); }
                            catch (Exception error)
                            {
                                if (sessionLock.ExternalUnlockObserved)
                                    dialog.ReportModelLockViolation(error.Message, sessionLock.AcknowledgeExternalRelock);
                                else dialog.SelectionRefreshFailed(error.Message);
                            }
                            finally { lockClock.Restart(); }
                        }
                        // The completed generation keeps its Undo available
                        // while the selection changes for the next operation.
                        if (!automatedExample && selectionClock.ElapsedMilliseconds >= (oversizedSelection ? 2000 : 350))
                        {
                            try
                            {
                                if (!string.Equals(model.GetModelFilename(true), previewModelPath,
                                    StringComparison.OrdinalIgnoreCase))
                                    dialog.DiscardCompletedForModelChange();
                                else if (dialog.Applied) refreshSelection(false);
                            }
                            catch (Exception error)
                            {
                                 if (sessionLock.ExternalUnlockObserved)
                                     dialog.ReportModelLockViolation(error.Message, sessionLock.AcknowledgeExternalRelock);
                                else dialog.SelectionRefreshFailed(error.Message);
                            }
                            selectionClock.Restart();
                        }
                        Application.DoEvents();
                        if (!automatedExample && dialog.Applied && appliedSelection == null &&
                            string.Equals(model.GetModelFilename(true), previewModelPath,
                                StringComparison.OrdinalIgnoreCase))
                            try { appliedSelection = new LiveSelection(model); }
                            catch (Exception error) { dialog.SelectionRefreshFailed(error.Message); }
                        Thread.Sleep(20);
                    }
                    Console.WriteLine("mesh-stage=preview-loop-ended visible=" + dialog.Visible +
                        " retry=" + dialog.RetryRequested + " undoFailure=" + (dialog.UndoFailure != null));
                    if (dialog.RegionalUnsafe) return;
                    if (dialog.UndoFailure != null)
                    {
                        Exception undoFailure = dialog.UndoFailure;
                        dialog.Close();
                        throw new InvalidOperationException("Undo failed. The generated mesh was not committed; inspect the model before continuing.", undoFailure);
                    }
                    if (dialog.PreviewRestoredOnClose)
                    {
                        Console.WriteLine("mesh-stage=preview-close-undo-complete");
                        return;
                    }
                    if (dialog.RetryRequested)
                    {
                        if (!directCommit) csvSnapshot.Delete();
                        diskCheckpoint.Delete();
                        Console.WriteLine("mesh-stage=undo-retry visible=" + dialog.Visible);
                        if (dialog.Visible) try
                        {
                            sessionLock.RelockAfterRestore();
                            // Finish releasing the previous model *and* read
                            // the new file's selection/units before Generate
                            // can become clickable again.
                            refreshSelection(true);
                            dialog.FinishRearm();
                        }
                        catch (Exception error)
                        {
                            dialog.FinishRearm();
                            dialog.SelectionRefreshFailed(error.Message);
                        }
                        continue;
                    }
                    if (!dialog.Applied)
                    {
                        if (previewLockedByPlugin)
                        {
                            if (sessionLock.Active) sessionLock.UnlockForWork();
                            else Check(model.SetModelIsLocked(false), "unlocking unapplied preview before restoration",
                                "cSapModel.SetModelIsLocked");
                            previewLockedByPlugin = false;
                        }
                        try
                        {
                            dialog.ThrowIfRegionalSettingsChanged();
                            if (directCommit) diskCheckpoint.Restore(model);
                            else if (useTabularUndo)
                                csvSnapshot.Restore(model, new string[0], new string[0], true,
                                    dialog.ThrowIfRegionalSettingsChanged);
                            else
                                UndoAttempts(model, results, fastSnapshot, fastOriginalPoints,
                                    dialog.ThrowIfRegionalSettingsChanged);
                            dialog.ThrowIfRegionalSettingsChanged();
                            RestoreSelection(model, selectedTypes, selectedNames);
                            dialog.ThrowIfRegionalSettingsChanged();
                            if (!directCommit) csvSnapshot.Delete();
                            diskCheckpoint.Delete();
                        }
                        catch (Exception) when (dialog.DetectRegionalChange())
                        { dialog.AbandonForRegionalChange(); return; }
                        sessionLock.RelockAfterWork();
                    }
                    else
                    {
                        if (!directCommit) csvSnapshot.Delete();
                        diskCheckpoint.Delete();
                    }
                    model.View.RefreshView(0, false);
                    return;
                }
                Console.WriteLine("mesh-stage=dialog-closed");
                }
            }
            finally
            {
                if (dialog != null && dialog.RegionalUnsafe && dialog.RegionalModelModified)
                {
                    try
                    {
                        if (sessionLock.Active) sessionLock.RelockAfterWork();
                        if (string.Equals(model.GetModelFilename(true), dialog.RegionalModelPath,
                                StringComparison.OrdinalIgnoreCase) && !model.GetModelIsLocked())
                            Check(model.SetModelIsLocked(true), "locking the unsafe preview until SAP exits",
                                "cSapModel.SetModelIsLocked");
                    }
                    catch (Exception error) { LogPluginException(error); }
                }
                interactiveDialog = null;
                try { if (dialog == null || !dialog.RegionalUnsafe || !dialog.RegionalModelModified) sessionLock.Dispose(); }
                finally
                {
                    if (sapWindow != IntPtr.Zero)
                    {
                        // The interactive plugin callback has already returned.
                        // Never leave SAP2000 disabled after our modeless dialog
                        // closes, even if a lock-cleanup OAPI call failed.
                        if (sessionLock.Active && !IsWindowEnabled(sapWindow))
                            EnableWindow(sapWindow, true);
                        SetActiveWindow(sapWindow);
                        SetForegroundWindow(sapWindow);
                        Application.DoEvents();
                    }
                }
            }
        }

        private static bool callbackReleasedInSap()
        {
            return IsSupportedHostProcess() &&
                Volatile.Read(ref interactiveRunActive) != 0;
        }

        private sealed class MeshCandidate
        {
            public readonly bool Regular, Refined, Symmetric, Bands, InsertEdgeNodes;
            public readonly List<AreaContext> Contexts;
            public readonly List<PreparedMesh> Meshes;
            public readonly string SymmetryNotice;
            public readonly MeshQualityReport OverallQuality;
            public MeshCandidate(MeshOptionsDialog dialog, List<AreaContext> contexts,
                List<PreparedMesh> meshes, string symmetryNotice)
            {
                Regular = dialog.PreferRegularMesh; Refined = dialog.StressRefinement;
                InsertEdgeNodes = dialog.StrictMaximumSize;
                Symmetric = dialog.Symmetric; Bands = dialog.StructuredSectorBands;
                Contexts = contexts; Meshes = meshes; SymmetryNotice = symmetryNotice;
                OverallQuality = CombineQuality(contexts, meshes, dialog);
            }
            public int ElementCount { get { return Meshes.Sum(mesh => mesh.Mesh.Elements.Count); } }
            public int TriangleCount { get { return Meshes.Sum(mesh => mesh.Mesh.Elements.Count(element => element.Length == 3)); } }
            public bool QualityPassed { get { return OverallQuality.Passed; } }
            public bool HardConstraintsPassed { get { return Meshes.All(mesh => mesh.Quality.Metrics.All(metric =>
                (metric.Name != "Maximum size" && metric.Name != "Orientation") || metric.Score >= 1)); } }
            public double QualityScore
            {
                get { return OverallQuality.OverallScore; }
            }
            public string Description { get { return Regular ? "Regular" : Refined ? "Refined" : "Standard"; } }
        }

        private sealed class MeshInventory
        {
            public readonly int Areas, Quads, Triangles, Polygons, Frames, Links, Joints;
            public MeshInventory(int areas, int quads, int triangles, int polygons, int frames, int links, int joints)
            {
                Areas = areas; Quads = quads; Triangles = triangles; Polygons = polygons;
                Frames = frames; Links = links; Joints = joints;
            }
        }

        private static MeshInventory CaptureMeshInventory(cSapModel model, List<MeshAttemptResult> preview)
        {
            int count = 0, quads = 0, triangles = 0, polygons = 0;
            string[] names = null;
            Check(model.AreaObj.GetNameList(ref count, ref names), "counting generated shell objects", "AreaObj.GetNameList");
            var originals = preview == null ? new HashSet<string>() : new HashSet<string>(preview
                .SelectMany(result => new[] { result.Operation.Unchanged || result.Operation.FastMode
                    ? result.Operation.SourceArea : result.Operation.BackupArea,
                    result.Operation.CommitBackupArea })
                .Where(name => !string.IsNullOrEmpty(name)));
            foreach (string name in names ?? new string[0])
            {
                if (originals.Contains(name)) continue;
                int corners = 0;
                string[] joints = null;
                Check(model.AreaObj.GetPoints(name, ref corners, ref joints), "counting shell corners for " + name, "AreaObj.GetPoints");
                if (corners == 4) quads++;
                else if (corners == 3) triangles++;
                else polygons++;
            }
            int frameCount = 0, linkCount = 0, jointCount = 0;
            string[] frames = null, links = null, points = null;
            Check(model.FrameObj.GetNameList(ref frameCount, ref frames), "counting frame objects", "FrameObj.GetNameList");
            Check(model.LinkObj.GetNameList(ref linkCount, ref links), "counting link objects", "LinkObj.GetNameList");
            Check(model.PointObj.GetNameList(ref jointCount, ref points), "counting joint objects", "PointObj.GetNameList");
            return new MeshInventory(quads + triangles + polygons, quads, triangles, polygons,
                frameCount, linkCount, jointCount);
        }

        private static bool IsRecoverableMeshFailure(Exception error)
        {
            for (Exception current = error; current != null; current = current.InnerException)
            {
                string text = current.Message;
                if (text.Contains("Mesh generator:") ||
                    text.IndexOf("exact symmetry", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    text.Contains("reflection-symmetric") || text.Contains("reflection pairs") ||
                    text.Contains("reflected pairs") || text.Contains("not exactly symmetric") ||
                    text.Contains("mirrored mesh") ||
                    text.Contains("Non-conforming shared edge") || text.Contains("Non-conforming intersection"))
                    return true;
            }
            return false;
        }

        private static MeshCandidate PrepareCandidate(cSapModel model, string[] areas, string[] selectedPoints,
            string[] selectedFrames, string[] holePointNames, SymmetryAxis[] symmetryAxes, MeshOptionsDialog dialog)
        {
            string denseBoundaries = null;
            string gmshBatchDirectory = null;
            var candidateClock = Stopwatch.StartNew();
            try
            {
                string[] constraintPoints = selectedPoints.Except(holePointNames).ToArray();
                TraceCancellationStage("contexts-start");
                List<AreaContext> contexts = BuildAreaContexts(model, areas, constraintPoints, selectedFrames,
                    dialog.PumpCancellation);
                RecordPhase("csharp-candidate-phases.csv", "read-area-contexts", candidateClock);
                TraceCancellationStage("contexts-complete");
                CaptureDesignProcedures(model, contexts);
                if (dialog.GenerateCircularHoles)
                    AssignCircularHoles(model, contexts, holePointNames, dialog.HoleDiameter);
                SymmetryAxis activeAxis = dialog.Symmetric ? dialog.SelectedSymmetryAxis : null;
                string symmetryNotice = null;
                if (dialog.Symmetric && activeAxis == null)
                    throw new InvalidOperationException("Exact symmetry requires a reflection axis compatible with the selected geometry and constraints.");
                if (activeAxis != null && dialog.GenerateCircularHoles)
                    ValidateSymmetricHoles(contexts, activeAxis);
                TraceCancellationStage("shared-boundaries-start");
                HarmonizeSharedBoundaries(contexts, dialog.MaxSize, dialog.PumpCancellation
                    , dialog.PreserveSelectedBoundaryEdges
                    );
                RecordPhase("csharp-candidate-phases.csv", "harmonize-shared-edges", candidateClock);
                TraceCancellationStage("shared-boundaries-complete");
                if (dialog.GenerateHoles)
                    HarmonizeAreaEdgeIntersections(contexts, dialog.MaxSize, dialog.PumpCancellation);
                TraceCancellationStage("intersections-complete");
                dialog.PumpCancellation();
                if (dialog.StrictMaximumSize) ApplyStrictMaximumSize(contexts, dialog.MaxSize);
                if (dialog.PreferRegularMesh && dialog.StrictMaximumSize && selectedPoints.Length > 0)
                    HarmonizeRegularGridSeams(contexts);
                if (dialog.GenerateHoles)
                    AssignOpeningBoundaries(model, contexts, dialog.OpeningGroup,
                        dialog.GenerateCircularHoles, selectedFrames);
                ValidateCircularHoleConstraints(contexts);
                if (activeAxis != null && contexts.Count > 1)
                    ValidateReflectedConstraints(contexts, activeAxis);
                RecordPhase("csharp-candidate-phases.csv", "prepare-holes-and-constraints", candidateClock);
                dialog.ThrowIfCancellationRequested();
                if (dialog.StructuredSectorBands && contexts.Count > 1 && !dialog.PreferRegularMesh &&
                    !dialog.GenerateCircularHoles && !dialog.Symmetric)
                {
                    denseBoundaries = Path.Combine(Path.GetTempPath(), "sap-dense-context-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(denseBoundaries);
                    foreach (AreaContext context in contexts)
                    {
                        dialog.PumpCancellation();
                        string section = "";
                        Check(model.AreaObj.GetProperty(context.Name, ref section), "reading the area property", "AreaObj.GetProperty");
                        WriteBoundary(Path.Combine(denseBoundaries, "area-" + context.Name + "-boundary.csv"),
                            context.Name, section, context.CornerNames, context.Corners,
                            context.EdgePoints, context.InteriorPoints, context.FrameConstraints, context.Holes,
                            null, context.ForceRefinement, dialog.StressRefinement, false,
                            new List<double[]>(), context.ExplicitlySelectedPointNames.ToArray(), contexts);
                        string denseTrace = Environment.GetEnvironmentVariable("SAP_QUAD_DENSE_TRACE_DIR");
                        if (!string.IsNullOrEmpty(denseTrace))
                            File.Copy(Path.Combine(denseBoundaries, "area-" + context.Name + "-boundary.csv"),
                                Path.Combine(denseTrace, "initial-area-" + context.Name + "-boundary.csv"), true);
                    }
                }
                var prepared = new List<PreparedMesh>();
                if (contexts.Count > 1 && (activeAxis == null || dialog.GenerateHoles))
                {
                    // Mesh shared SAP shell edges once in a single OCC/Gmsh
                    // model; independent per-area algorithms can introduce
                    // hanging nodes even with identical initial stations.
                    gmshBatchDirectory = Path.Combine(Path.GetTempPath(),
                        "sap-gmsh-batch-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(gmshBatchDirectory);
                    string outputs = Path.Combine(gmshBatchDirectory, "mesh");
                    Directory.CreateDirectory(outputs);
                    var manifest = new List<object>();
                    for (int i = 0; i < contexts.Count; i++)
                    {
                        AreaContext source = contexts[i];
                        dialog.PumpCancellation();
                        string section = "";
                        Check(model.AreaObj.GetProperty(source.Name, ref section), "reading area property for Gmsh batch", "AreaObj.GetProperty");
                        string file = "area-" + i.ToString(CultureInfo.InvariantCulture) + ".csv";
                        WriteBoundary(Path.Combine(gmshBatchDirectory, file), source.Name, section,
                            source.CornerNames, source.Corners, source.EdgePoints, source.InteriorPoints,
                            source.FrameConstraints, source.Holes, null, source.ForceRefinement,
                            false, false, new List<double[]>(), source.ExplicitlySelectedPointNames.ToArray(), contexts,
                            dialog.PreserveSelectedBoundaryEdges);
                        string boundaryTraceDirectory = Environment.GetEnvironmentVariable("SAP_QUAD_BOUNDARY_TRACE_DIR");
                        if (!string.IsNullOrEmpty(boundaryTraceDirectory))
                            File.Copy(Path.Combine(gmshBatchDirectory, file),
                                Path.Combine(boundaryTraceDirectory, "area-" + source.Name + "-boundary.csv"), true);
                        manifest.Add(new { boundary = file, mesh = "mesh-" + i.ToString(CultureInfo.InvariantCulture) + ".csv" });
                    }
                    string batchInput = Path.Combine(gmshBatchDirectory, "manifest.json");
                        File.WriteAllText(batchInput, new JavaScriptSerializer().Serialize(new { areas = manifest }), Encoding.UTF8);
                    RecordPhase("csharp-candidate-phases.csv", "write-gmsh-boundary-manifest", candidateClock);
                    dialog.SetIndeterminate(dialog.GmshOptimizeEnabled
                        ? "Generating and optimizing the mesh..." : "Generating the mesh...");
                    dialog.ShowBatchAreas(contexts.Count);
                    RunMesher("sap_gmsh_mesher.py", batchInput, outputs, dialog.MaxSize, dialog, null);
                    RecordPhase("csharp-candidate-phases.csv", "run-gmsh-backend", candidateClock);
                }
                dialog.SetMeshingAreas(0, contexts.Count);
                foreach (AreaContext context in contexts)
                {
                    dialog.PumpCancellation();
                    TraceCancellationStage("mesh-area-start " + context.Name);
                    SymmetryAxis contextAxis = null;
                    try
                    {
                        var preparationClock = Stopwatch.StartNew();
                        PreparedMesh mirror = null;
                        double[] reflection = null;
                        if (activeAxis != null && contexts.Count > 1)
                        {
                            for (int i = 0; i < prepared.Count && mirror == null; i++)
                            {
                                if (AreasReflect(contexts[i], context, activeAxis.Center, activeAxis.Direction))
                                { mirror = prepared[i]; reflection = activeAxis.Direction; }
                                else if (activeAxis.SecondaryDirection != null && AreasReflect(contexts[i], context,
                                    activeAxis.Center, activeAxis.SecondaryDirection))
                                { mirror = prepared[i]; reflection = activeAxis.SecondaryDirection; }
                            }
                            if (mirror == null)
                            {
                                bool primarySelf = AreasReflect(context, context, activeAxis.Center, activeAxis.Direction);
                                bool secondarySelf = activeAxis.SecondaryDirection != null && AreasReflect(context, context,
                                    activeAxis.Center, activeAxis.SecondaryDirection);
                                if (primarySelf && (activeAxis.SecondaryDirection == null || secondarySelf))
                                    contextAxis = activeAxis;
                                else if (primarySelf || secondarySelf)
                                    contextAxis = new SymmetryAxis(activeAxis.Center,
                                        primarySelf ? activeAxis.Direction : activeAxis.SecondaryDirection, 1);
                            }
                        }
                        else contextAxis = activeAxis;
                        prepared.Add(mirror != null
                            ? MirrorPreparedMesh(model, mirror, context, reflection, activeAxis.Center,
                                dialog, contexts)
                            : PrepareMeshAttempt(model, context.Name, context.CornerNames.Length,
                                context.CornerNames, context.Corners, context.EdgePoints, context.InteriorPoints,
                                 context.ConstraintPointNames.ToArray(), context.ExplicitlySelectedPointNames.ToArray(),
                                 context.FrameConstraints, context.Holes, context.ForceRefinement, context.Plane,
                                 contextAxis, dialog, contexts, denseBoundaries
                                 , gmshBatchDirectory == null ? null : Path.Combine(gmshBatchDirectory, "mesh",
                                     "mesh-" + prepared.Count.ToString(CultureInfo.InvariantCulture) + ".csv")
                                 ));
                        RecordFastPhase("prepare-area-" + context.Name, preparationClock);
                        dialog.SetMeshingAreas(prepared.Count, contexts.Count);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException("Area " + context.Name + ": " + exception.Message, exception);
                    }
                }
                RecordPhase("csharp-candidate-phases.csv", "read-and-prepare-area-meshes", candidateClock);
                ValidateAutomaticIntersectionEdges(contexts, prepared);
                ValidateSharedAreaEdges(contexts, prepared);
                if (activeAxis != null && contexts.Count > 1)
                    ValidateSelectionSymmetry(prepared, activeAxis);
                dialog.ThrowIfCancellationRequested();
                RecordPhase("csharp-candidate-phases.csv", "verify-shared-mesh-edges", candidateClock);
                var result = new MeshCandidate(dialog, contexts, prepared, symmetryNotice);
                RecordPhase("csharp-candidate-phases.csv", "aggregate-quality", candidateClock);
                return result;
            }
            finally
            {
                if (denseBoundaries != null)
                    try { Directory.Delete(denseBoundaries, true); } catch (IOException) { }
                if (gmshBatchDirectory != null)
                    try { Directory.Delete(gmshBatchDirectory, true); } catch (IOException) { }
            }
        }

        private static bool AreasReflect(AreaContext source, AreaContext target, double[] center, double[] axis)
        {
            return source.Corners.Length == target.Corners.Length && source.Corners.All(corner =>
                target.Corners.Any(other => Distance(ReflectPoint3D(corner, center, axis), other) <= Tolerance * 10));
        }

        private static void ValidateReflectedConstraints(List<AreaContext> contexts, SymmetryAxis axis)
        {
            foreach (double[] direction in new[] { axis.Direction, axis.SecondaryDirection }.Where(item => item != null))
            {
                foreach (AreaContext source in contexts)
                {
                    AreaContext target = contexts.FirstOrDefault(other => AreasReflect(source, other, axis.Center, direction));
                    if (target == null) throw new InvalidOperationException("The selected areas do not form reflection pairs.");
                    double[][] from = source.Corners.Concat(source.EdgePoints.SelectMany(edge => edge)
                        .Select(item => item.Coordinates)).Concat(source.InteriorPoints.Select(point => point.Coordinates)).ToArray();
                    double[][] to = target.Corners.Concat(target.EdgePoints.SelectMany(edge => edge)
                        .Select(item => item.Coordinates)).Concat(target.InteriorPoints.Select(point => point.Coordinates)).ToArray();
                    if (from.Any(point => !to.Any(other => Distance(ReflectPoint3D(point, axis.Center, direction), other) <= Tolerance * 10)) ||
                        source.Holes.Any(hole => !target.Holes.Any(other => Math.Abs(hole.Diameter - other.Diameter) <= Tolerance &&
                            Distance(ReflectPoint3D(hole.Center, axis.Center, direction), other.Center) <= Tolerance * 10)))
                        throw new InvalidOperationException("Selected nodes, boundaries or holes are not reflection-symmetric.");
                }
            }
        }

        private static PreparedMesh MirrorPreparedMesh(cSapModel model, PreparedMesh source, AreaContext target,
            double[] axis, double[] center, MeshOptionsDialog dialog, List<AreaContext> contexts)
        {
            var mesh = new MeshData { RemovedElements = source.Mesh.RemovedElements };
            foreach (KeyValuePair<int, double[]> node in source.Mesh.Nodes)
                mesh.Nodes.Add(node.Key, ReflectPoint3D(node.Value, center, axis));
            foreach (int[] element in source.Mesh.Elements)
            {
                int[] reflected = element.ToArray();
                double[] first = mesh.Nodes[reflected[0]];
                if (Dot(Cross(Subtract(mesh.Nodes[reflected[1]], first),
                    Subtract(mesh.Nodes[reflected[2]], first)), target.Plane.Normal) < 0)
                    Array.Reverse(reflected);
                mesh.Elements.Add(reflected);
            }
            mesh.ProtectedEdges.UnionWith(source.Mesh.ProtectedEdges);
            foreach (ConstraintPoint point in target.InteriorPoints)
                if (!mesh.Nodes.Values.Any(node => Distance(node, point.Coordinates) <= Tolerance * 10))
                    throw new InvalidDataException("A mirrored mesh is missing a selected interior joint.");
            string property = "";
            double angle = 0, mass = 0;
            bool advanced = false, edgeConstraint = false;
            Check(model.AreaObj.GetProperty(target.Name, ref property), "reading the area property", "AreaObj.GetProperty");
            Check(model.AreaObj.GetLocalAxes(target.Name, ref angle, ref advanced), "reading reflected area local axes", "AreaObj.GetLocalAxes");
            Check(model.AreaObj.GetMass(target.Name, ref mass), "reading reflected area mass", "AreaObj.GetMass");
            Check(model.AreaObj.GetEdgeConstraint(target.Name, ref edgeConstraint), "reading reflected area edge constraint", "AreaObj.GetEdgeConstraint");
            if (advanced) throw new InvalidOperationException("Advanced local axes are not supported.");
            PointSnapshot[] points = CapturePointSnapshots(model,
                target.CornerNames.Concat(target.ConstraintPointNames).Concat(target.Holes.Select(hole => hole.PointName)));
            MeshQualityReport quality = EvaluateMeshQuality(mesh, target.Plane,
                dialog.StrictMaximumSize ? dialog.MaxSize :
                Math.Max(AllowedMaxSize(dialog.MaxSize, target.EdgePoints), dialog.MaxSize *
                    (1 + dialog.EdgeTolerancePercent / 100.0)), target.FrameConstraints.Count);
            PropagateGeneratedIntersectionEdges(target.Name, target.Plane, mesh, contexts, dialog.PreferRegularMesh);
            return new PreparedMesh(target.Name, property, angle, mass, edgeConstraint,
                CaptureAreaLoads(model, target.Name, contexts), points, mesh, quality);
        }

        private static void ValidateSelectionSymmetry(List<PreparedMesh> prepared, SymmetryAxis axis)
        {
            var signatures = new HashSet<string>(prepared.SelectMany(item => item.Mesh.Elements
                .Select(element => MeshElementSignature(item.Mesh, element))));
            foreach (double[] direction in new[] { axis.Direction, axis.SecondaryDirection }.Where(item => item != null))
                foreach (PreparedMesh item in prepared)
                    foreach (int[] element in item.Mesh.Elements)
                        if (!signatures.Contains(MeshElementSignature(element.Select(tag =>
                            ReflectPoint3D(item.Mesh.Nodes[tag], axis.Center, direction)))))
                            throw new InvalidDataException("The full selection is not exactly symmetric about the selected axis.");
        }

        private static List<AreaContext> BuildAreaContexts(cSapModel model, string[] areas, string[] selectedPoints,
            string[] selectedFrames, Action checkpoint = null)
        {
            var contexts = new List<AreaContext>();
            foreach (string area in areas)
            {
                if (checkpoint != null) checkpoint();
                contexts.Add(BuildAreaContext(model, area));
            }
            PreserveExistingBoundaryNodes(model, contexts, areas, checkpoint);
            foreach (string pointName in selectedPoints)
            {
                if (checkpoint != null) checkpoint();
                double[] point = Coordinate(model, pointName);
                foreach (AreaContext context in contexts)
                    if (AddConstraintPoint(context, pointName, point))
                        context.ExplicitlySelectedPointNames.Add(pointName);
                // A group may include points from neighboring, deliberately
                // disconnected areas. They impose no constraint on this mesh.
            }
            foreach (string frameName in selectedFrames)
            {
                if (checkpoint != null) checkpoint();
                string firstName = "", secondName = "";
                Check(model.FrameObj.GetPoints(frameName, ref firstName, ref secondName), "reading frame endpoints for " + frameName, "FrameObj.GetPoints");
                double[] first = Coordinate(model, firstName), second = Coordinate(model, secondName);
                if (Distance(first, second) <= Tolerance)
                    throw new InvalidOperationException("Frame " + frameName + " has zero length.");
                var covered = new List<SegmentInterval>();
                var parameters = new List<double> { 0, 1 };
                foreach (AreaContext context in contexts)
                {
                    if (Math.Abs(context.Plane.SignedDistance(first)) > Tolerance ||
                        Math.Abs(context.Plane.SignedDistance(second)) > Tolerance) continue;
                    double[] projectedFirst = context.Plane.Project(first), projectedSecond = context.Plane.Project(second);
                    double[][] polygon = context.Corners.Select(context.Plane.Project).ToArray();
                    for (int edge = 0; edge < polygon.Length; edge++)
                        AddSegmentIntersectionParameters(projectedFirst, projectedSecond, polygon[edge],
                            polygon[(edge + 1) % polygon.Length], parameters);
                }
                List<double> breakpoints = parameters.Select(value => Math.Max(0, Math.Min(1, value)))
                    .OrderBy(value => value).Aggregate(new List<double>(), (values, value) =>
                    {
                        if (values.Count == 0 || Math.Abs(values[values.Count - 1] - value) > Tolerance) values.Add(value);
                        return values;
                    });
                for (int index = 1; index < breakpoints.Count; index++)
                {
                    double low = breakpoints[index - 1], high = breakpoints[index];
                    if ((high - low) * Distance(first, second) <= Tolerance) continue;
                    double[] midpoint = Interpolate(first, second, (low + high) * 0.5);
                    List<AreaContext> hosts = contexts.Where(context =>
                        Math.Abs(context.Plane.SignedDistance(midpoint)) <= Tolerance &&
                        PointInsideOrBoundary(context.Plane.Project(midpoint),
                            context.Corners.Select(context.Plane.Project).ToArray())).ToList();
                    if (hosts.Count == 0) continue;
                    covered.Add(new SegmentInterval(low, high));
                    double[] start = Interpolate(first, second, low), end = Interpolate(first, second, high);
                    string startName = low <= Tolerance ? firstName : "QT_FRAME_INTERSECTION";
                    string endName = high >= 1 - Tolerance ? secondName : "QT_FRAME_INTERSECTION";
                    foreach (AreaContext context in hosts)
                    {
                        AddConstraintPoint(context, startName, start, low <= Tolerance);
                        AddConstraintPoint(context, endName, end, high >= 1 - Tolerance);
                        if (!FrameOnBoundary(context, start, end))
                            context.FrameConstraints.Add(new FrameConstraint(frameName, startName, endName, start, end));
                    }
                }
                // Frames outside the selected domain are not meshing guides.
            }
            foreach (AreaContext context in contexts)
                for (int edge = 0; edge < context.EdgePoints.Length; edge++)
                    context.EdgePoints[edge] = context.EdgePoints[edge].OrderBy(point => point.T).ToList();

            return contexts;
        }

        private static void PreserveExistingBoundaryNodes(cSapModel model, List<AreaContext> contexts,
            string[] selectedAreas, Action checkpoint = null)
        {
            // An adjacent shell can already be meshed while only its unmeshed neighbor is
            // selected. Preserve the actual joint sequence on their common edge without
            // requiring the user to select every joint of the existing mesh manually.
            // A coincident shell contained *inside* a selected null area is different:
            // its coarse boundary is not a shared seam between neighboring regions.
            // Constraining the null area to that boundary would permit long, thin
            // elements despite the requested maximum size.
            int areaCount = 0;
            string[] names = null;
            Check(model.AreaObj.GetNameList(ref areaCount, ref names), "reading adjacent shell edges", "AreaObj.GetNameList");
            var selected = new HashSet<string>(selectedAreas, StringComparer.OrdinalIgnoreCase);
            var coordinates = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            var nullContexts = new HashSet<AreaContext>();
            foreach (AreaContext context in contexts)
            {
                string property = "";
                Check(model.AreaObj.GetProperty(context.Name, ref property), "reading selected area property", "AreaObj.GetProperty");
                if (string.Equals(property, "None", StringComparison.OrdinalIgnoreCase))
                    nullContexts.Add(context);
            }
            foreach (string name in names ?? new string[0])
            {
                if (checkpoint != null) checkpoint();
                if (selected.Contains(name)) continue;
                int count = 0;
                string[] points = null;
                Check(model.AreaObj.GetPoints(name, ref count, ref points), "reading adjacent shell " + name, "AreaObj.GetPoints");
                double[][] shell = points.Select(point =>
                {
                    double[] location;
                    if (!coordinates.TryGetValue(point, out location))
                        coordinates[point] = location = Coordinate(model, point);
                    return location;
                }).ToArray();
                var overlappingNullAreas = new HashSet<AreaContext>(nullContexts.Where(context =>
                    CoplanarShellContainedInArea(context, shell)));
                for (int i = 0; i < count; i++)
                {
                    string firstName = points[i], secondName = points[(i + 1) % count];
                    double[] first = shell[i], second = shell[(i + 1) % count];
                    foreach (AreaContext context in contexts)
                    {
                        if (overlappingNullAreas.Contains(context)) continue;
                        if (Math.Abs(context.Plane.SignedDistance(first)) > Tolerance ||
                            Math.Abs(context.Plane.SignedDistance(second)) > Tolerance) continue;
                        for (int edge = 0; edge < context.Corners.Length; edge++)
                        {
                            double firstT, secondT;
                            if (!OnSegment(first, context.Corners[edge], context.Corners[(edge + 1) % context.Corners.Length], out firstT) ||
                                !OnSegment(second, context.Corners[edge], context.Corners[(edge + 1) % context.Corners.Length], out secondT) ||
                                Math.Abs(firstT - secondT) <= Tolerance) continue;
                            AddConstraintPoint(context, firstName, first, false);
                            AddConstraintPoint(context, secondName, second, false);
                        }
                    }
                }
            }
            foreach (AreaContext context in contexts)
                for (int edge = 0; edge < context.EdgePoints.Length; edge++)
                    context.EdgePoints[edge] = context.EdgePoints[edge].OrderBy(point => point.T).ToList();
        }

        private static bool CoplanarShellContainedInArea(AreaContext context, double[][] shell)
        {
            if (shell.Length < 3 || shell.Any(point => Math.Abs(context.Plane.SignedDistance(point)) > Tolerance))
                return false;
            double[][] host = context.Corners.Select(context.Plane.Project).ToArray();
            double[][] inside = shell.Select(context.Plane.Project).ToArray();
            if (inside.Any(point => !PointInsideOrBoundary(point, host))) return false;
            // A common edge alone is not an overlap. Test points in the shell's
            // interior, including the case of identical coincident outlines.
            for (int i = 0; i < inside.Length; i++)
            {
                double[] first = inside[i], second = inside[(i + 1) % inside.Length];
                double[] third = inside[(i + 2) % inside.Length];
                double[] sample = { (first[0] + second[0] + third[0]) / 3,
                                    (first[1] + second[1] + third[1]) / 3 };
                if (PointInPolygon(sample, inside) && PointInPolygon(sample, host)) return true;
            }
            return false;
        }

        private static string[] ReadPointGroups(cSapModel model, Action checkpoint = null)
        {
            int count = 0;
            string[] names = null;
            Check(model.GroupDef.GetNameList(ref count, ref names), "reading SAP2000 groups", "GroupDef.GetNameList");
            var result = new List<string>();
            foreach (string name in names ?? new string[0])
            {
                if (checkpoint != null) checkpoint();
                if (ReadGroupPoints(model, name).Length > 0) result.Add(name);
            }
            return result.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        private static string[] ReadGroupPoints(cSapModel model, string groupName)
        {
            int count = 0;
            int[] types = null;
            string[] names = null;
            Check(model.GroupDef.GetAssignments(groupName, ref count, ref types, ref names), "reading group " + groupName, "GroupDef.GetAssignments");
            return Enumerable.Range(0, count).Where(index => types[index] == PointObjectType)
                .Select(index => names[index]).Distinct().ToArray();
        }

        private static string[] ReadOpeningGroups(cSapModel model, Action checkpoint = null)
        {
            int count = 0;
            string[] names = null;
            Check(model.GroupDef.GetNameList(ref count, ref names), "reading SAP2000 groups", "GroupDef.GetNameList");
            var result = new List<string>();
            foreach (string name in names ?? new string[0])
            {
                if (checkpoint != null) checkpoint();
                if (ReadGroupObjects(model, name).HasOpeningObjects) result.Add(name);
            }
            return result.OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }

        private static GroupObjects ReadGroupObjects(cSapModel model, string groupName)
        {
            int count = 0;
            int[] types = null;
            string[] names = null;
            Check(model.GroupDef.GetAssignments(groupName, ref count, ref types, ref names), "reading group " + groupName, "GroupDef.GetAssignments");
            var frames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var areas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < count; index++)
            {
                if (types[index] == FrameObjectType) frames.Add(names[index]);
                else if (types[index] == AreaObjectType) areas.Add(names[index]);
            }
            return new GroupObjects(frames, areas);
        }

        private static void AssignOpeningBoundaries(cSapModel model, List<AreaContext> contexts, string groupName,
            bool allowEmpty, string[] selectedFrames)
        {
            if (string.IsNullOrEmpty(groupName))
                throw new InvalidOperationException("Select an opening-boundary group.");
            GroupObjects members = ReadGroupObjects(model, groupName);
            var selected = new HashSet<string>(selectedFrames, StringComparer.OrdinalIgnoreCase);
            var openingSegments = new List<Tuple<double[], double[]>>();
            foreach (string name in members.Frames.Where(selected.Contains))
            {
                string first = "", second = "";
                Check(model.FrameObj.GetPoints(name, ref first, ref second),
                    "reading opening frame " + name, "FrameObj.GetPoints");
                openingSegments.Add(Tuple.Create(Coordinate(model, first), Coordinate(model, second)));
            }
            int eligible = 0;
            foreach (AreaContext context in contexts)
                foreach (FrameConstraint frame in context.FrameConstraints)
                {
                    // Splitting overlapping selected frames retains just one
                    // coincident subsegment. Its name can belong to a frame
                    // outside the opening group, even when another selected
                    // frame in the group covers exactly that same subsegment.
                    // Use the union of grouped frame geometry, not whichever
                    // frame name happened to survive deduplication.
                    frame.CanBoundOpening =
                        frame.Automatic && frame.SourceContext != null && members.Areas.Contains(frame.SourceContext.Name) ||
                        !frame.Automatic && members.Frames.Contains(frame.Name) ||
                        openingSegments.Any(segment => OpeningContainsFrame(segment, frame));
                    if (frame.CanBoundOpening) eligible++;
                }
            if (eligible == 0 && !allowEmpty)
                throw new InvalidOperationException("The selected opening-boundary group contains no frame or shell-edge constraints in the selected areas.");
            if (eligible == 0)
                Console.WriteLine("mesh-opening-boundaries=none circular-holes-only=true");
        }

        private static bool OpeningContainsFrame(Tuple<double[], double[]> opening, FrameConstraint frame)
        {
            double firstPosition, secondPosition;
            return OnSegment(frame.First, opening.Item1, opening.Item2, out firstPosition) &&
                OnSegment(frame.Second, opening.Item1, opening.Item2, out secondPosition);
        }

        private static void AssignCircularHoles(cSapModel model, List<AreaContext> contexts, string[] pointNames, double diameter)
        {
            if (pointNames.Length == 0) throw new InvalidOperationException("The selected circular-hole group contains no point objects.");
            double radius = diameter * 0.5;
            int totalAssigned = 0;
            foreach (string pointName in pointNames)
            {
                double[] center = Coordinate(model, pointName);
                var intersected = new List<AreaContext>();
                foreach (AreaContext context in contexts)
                {
                    if (Math.Abs(context.Plane.SignedDistance(center)) > Tolerance) continue;
                    double[] projectedCenter = context.Plane.Project(center);
                    double[][] polygon = context.Corners.Select(context.Plane.Project).ToArray();
                    double clearance = Enumerable.Range(0, polygon.Length)
                        .Min(edge => DistancePointToSegment2D(projectedCenter, polygon[edge], polygon[(edge + 1) % polygon.Length]));
                    if (!PointInsideOrBoundary(projectedCenter, polygon) && clearance >= radius - Tolerance) continue;
                    intersected.Add(context);
                }
                if (intersected.Count == 0) continue;
                for (int sample = 0; sample < 64; sample++)
                {
                    double angle = 2 * Math.PI * sample / 64;
                    double[] sampleVector = intersected[0].Plane.VectorFromPlane(radius * Math.Cos(angle), radius * Math.Sin(angle));
                    double[] samplePoint = { center[0] + sampleVector[0], center[1] + sampleVector[1], center[2] + sampleVector[2] };
                    bool covered = contexts.Any(context =>
                    {
                        if (Math.Abs(context.Plane.SignedDistance(samplePoint)) > Tolerance) return false;
                        return PointInsideOrBoundary(context.Plane.Project(samplePoint),
                            context.Corners.Select(context.Plane.Project).ToArray());
                    });
                    if (!covered)
                        throw new InvalidOperationException("Circular hole at point " + pointName +
                            " is not fully covered by the selected coplanar areas.");
                }
                foreach (AreaContext context in intersected)
                {
                    foreach (ConstraintPoint constraint in context.InteriorPoints)
                        if (Distance(center, constraint.Coordinates) <= radius + Tolerance)
                            throw new InvalidOperationException("Selected point " + constraint.Name + " lies inside the circular hole centered at " + pointName + ".");
                    foreach (FrameConstraint frame in context.FrameConstraints)
                    {
                        double distance = DistancePointToSegment2D(context.Plane.Project(center),
                            context.Plane.Project(frame.First), context.Plane.Project(frame.Second));
                        if (distance <= radius + Tolerance)
                            throw new InvalidOperationException("Frame " + frame.Name + " crosses the circular hole centered at " + pointName + ".");
                    }
                    context.Holes.Add(new HoleDefinition(context.Holes.Count + 1, pointName, center, diameter));
                    totalAssigned++;
                }
            }
            if (totalAssigned == 0)
                throw new InvalidOperationException("None of the point objects in the selected circular-hole group belongs to the selected areas.");

            foreach (AreaContext context in contexts)
                for (int first = 0; first < context.Holes.Count; first++)
                    for (int second = first + 1; second < context.Holes.Count; second++)
                        if (Distance(context.Holes[first].Center, context.Holes[second].Center) <=
                            context.Holes[first].Radius + context.Holes[second].Radius + Tolerance)
                            throw new InvalidOperationException("Circular holes centered at points " + context.Holes[first].PointName + " and " +
                                context.Holes[second].PointName + " overlap or touch in area " + context.Name + ".");
        }

        private static double DistancePointToSegment2D(double[] point, double[] first, double[] second)
        {
            double dx = second[0] - first[0], dy = second[1] - first[1];
            double lengthSquared = dx * dx + dy * dy;
            double t = lengthSquared == 0 ? 0 : ((point[0] - first[0]) * dx + (point[1] - first[1]) * dy) / lengthSquared;
            t = Math.Max(0, Math.Min(1, t));
            double x = first[0] + t * dx, y = first[1] + t * dy;
            double px = point[0] - x, py = point[1] - y;
            return Math.Sqrt(px * px + py * py);
        }

        private static void ValidateCircularHoleConstraints(List<AreaContext> contexts)
        {
            foreach (AreaContext context in contexts)
                foreach (HoleDefinition hole in context.Holes)
                    foreach (FrameConstraint frame in context.FrameConstraints)
                        if (DistancePointToSegment2D(context.Plane.Project(hole.Center),
                            context.Plane.Project(frame.First), context.Plane.Project(frame.Second)) <= hole.Radius + Tolerance)
                            throw new InvalidOperationException("Constraint " + frame.Name + " crosses the circular hole centered at " + hole.PointName + ".");
        }

        private static void ValidateSymmetricHoles(List<AreaContext> contexts, SymmetryAxis axis)
        {
            if (axis == null)
                throw new InvalidOperationException("Symmetric circular holes require a detected symmetry axis.");
            if (contexts.Count > 1)
            {
                HoleDefinition[] uniqueHoles = contexts.SelectMany(item => item.Holes)
                    .GroupBy(hole => hole.PointName).Select(group => group.First()).ToArray();
                double scale = contexts.SelectMany(item => item.Corners)
                    .SelectMany(first => contexts.SelectMany(item => item.Corners).Select(second => Distance(first, second))).Max();
                double tolerance = Math.Max(Tolerance * 10, scale * 1e-6);
                foreach (double[] direction in new[] { axis.Direction, axis.SecondaryDirection }.Where(item => item != null))
                    foreach (HoleDefinition hole in uniqueHoles)
                        if (!uniqueHoles.Any(other => Math.Abs(hole.Diameter - other.Diameter) <= Tolerance &&
                            Distance(ReflectPoint3D(hole.Center, axis.Center, direction), other.Center) <= tolerance))
                            throw new InvalidOperationException("Circular holes do not form reflected pairs across the selected symmetry axis.");
                return;
            }
            AreaContext context = contexts[0];
            double contextScale = context.Corners.SelectMany(first => context.Corners.Select(second => Distance(first, second))).Max();
            double maxRadius = context.Holes.Select(hole => hole.Radius).DefaultIfEmpty(0).Max();
            double symmetryTolerance = Math.Max(Tolerance * 10, Math.Min(contextScale * 0.01, maxRadius * 0.15));
            var unpaired = new HashSet<int>(Enumerable.Range(0, context.Holes.Count));
            while (unpaired.Count > 0)
            {
                int holeIndex = unpaired.First();
                HoleDefinition hole = context.Holes[holeIndex];
                unpaired.Remove(holeIndex);
                double[] offset = Subtract(hole.Center, axis.Center);
                double projection = Dot(offset, axis.Direction);
                double[] perpendicular = Subtract(offset, new[] { axis.Direction[0] * projection,
                    axis.Direction[1] * projection, axis.Direction[2] * projection });
                double axisDistance = Norm(perpendicular);
                if (axisDistance <= symmetryTolerance)
                {
                    double[] snapped =
                    {
                        axis.Center[0] + projection * axis.Direction[0],
                        axis.Center[1] + projection * axis.Direction[1],
                        axis.Center[2] + projection * axis.Direction[2]
                    };
                    context.Holes[holeIndex] = new HoleDefinition(hole.Id, hole.PointName, snapped, hole.Diameter);
                    // The last hole may lie on the primary axis. Do not skip
                    // checking the perpendicular axis in that case.
                    if (unpaired.Count == 0 && axis.SecondaryDirection != null)
                        ValidateSymmetricHoles(contexts, new SymmetryAxis(axis.Center, axis.SecondaryDirection, 2));
                    continue;
                }
                if (axisDistance <= hole.Radius + Tolerance)
                    throw new InvalidOperationException("A circular hole centered at point " + hole.PointName +
                        " crosses the symmetry axis without being centered on it.");
                double[] reflected =
                {
                    axis.Center[0] + 2 * projection * axis.Direction[0] - offset[0],
                    axis.Center[1] + 2 * projection * axis.Direction[1] - offset[1],
                    axis.Center[2] + 2 * projection * axis.Direction[2] - offset[2]
                };
                int counterpart = unpaired.Where(candidate =>
                    Math.Abs(context.Holes[candidate].Diameter - hole.Diameter) <= Tolerance &&
                    Distance(context.Holes[candidate].Center, reflected) <= symmetryTolerance).DefaultIfEmpty(-1).First();
                if (counterpart < 0)
                {
                    double nearest = unpaired.Select(candidate => Distance(context.Holes[candidate].Center, reflected))
                        .DefaultIfEmpty(double.PositiveInfinity).Min();
                    throw new InvalidOperationException("Circular hole centered at point " + hole.PointName +
                        " has no reflected counterpart across the selected symmetry axis. Nearest center distance: " +
                        nearest.ToString("G6", CultureInfo.InvariantCulture) + "; tolerance: " +
                        symmetryTolerance.ToString("G6", CultureInfo.InvariantCulture) + ".");
                }
                HoleDefinition other = context.Holes[counterpart];
                double[] otherOffset = Subtract(other.Center, axis.Center);
                double otherProjection = Dot(otherOffset, axis.Direction);
                double[] otherPerpendicular = Subtract(otherOffset, new[] { axis.Direction[0] * otherProjection,
                    axis.Direction[1] * otherProjection, axis.Direction[2] * otherProjection });
                double commonProjection = (projection + otherProjection) * 0.5;
                double[] commonPerpendicular =
                {
                    (perpendicular[0] - otherPerpendicular[0]) * 0.5,
                    (perpendicular[1] - otherPerpendicular[1]) * 0.5,
                    (perpendicular[2] - otherPerpendicular[2]) * 0.5
                };
                double[] firstCenter =
                {
                    axis.Center[0] + commonProjection * axis.Direction[0] + commonPerpendicular[0],
                    axis.Center[1] + commonProjection * axis.Direction[1] + commonPerpendicular[1],
                    axis.Center[2] + commonProjection * axis.Direction[2] + commonPerpendicular[2]
                };
                double[] secondCenter =
                {
                    axis.Center[0] + commonProjection * axis.Direction[0] - commonPerpendicular[0],
                    axis.Center[1] + commonProjection * axis.Direction[1] - commonPerpendicular[1],
                    axis.Center[2] + commonProjection * axis.Direction[2] - commonPerpendicular[2]
                };
                context.Holes[holeIndex] = new HoleDefinition(hole.Id, hole.PointName, firstCenter, hole.Diameter);
                context.Holes[counterpart] = new HoleDefinition(other.Id, other.PointName, secondCenter, other.Diameter);
                unpaired.Remove(counterpart);
            }
            if (axis.SecondaryDirection != null)
                ValidateSymmetricHoles(contexts, new SymmetryAxis(axis.Center, axis.SecondaryDirection, 2));
        }

        private static SymmetryAxis[] DetectSymmetryAxes(List<AreaContext> contexts, Action checkpoint = null)
        {
            if (contexts.Count > 1) return DetectMultiAreaSymmetryAxes(contexts, checkpoint);
            if (contexts.Count != 1) return new SymmetryAxis[0];
            AreaContext context = contexts[0];
            double[][] corners = context.Corners.Select(context.Plane.Project).ToArray();
            double[] center2D =
            {
                corners.Average(point => point[0]),
                corners.Average(point => point[1])
            };
            double extent = corners.Max(point => Math.Sqrt((point[0] - center2D[0]) * (point[0] - center2D[0]) +
                (point[1] - center2D[1]) * (point[1] - center2D[1])));
            double tolerance = Math.Max(Tolerance * 10, extent * 1e-6);
            var candidates = new List<double[]>();
            foreach (double[] point in corners.Concat(Enumerable.Range(0, corners.Length)
                .Select(index => new[] { (corners[index][0] + corners[(index + 1) % corners.Length][0]) * 0.5,
                    (corners[index][1] + corners[(index + 1) % corners.Length][1]) * 0.5 })))
            {
                double dx = point[0] - center2D[0], dy = point[1] - center2D[1];
                double length = Math.Sqrt(dx * dx + dy * dy);
                if (length <= tolerance) continue;
                double[] direction = { dx / length, dy / length };
                if (!candidates.Any(existing => Math.Abs(existing[0] * direction[0] + existing[1] * direction[1]) >= 1 - 1e-8))
                    candidates.Add(direction);
            }

            double[][] boundaryPoints = context.EdgePoints.SelectMany(edge => edge).Select(point => context.Plane.Project(point.Coordinates)).ToArray();
            double[][] interiorPoints = context.InteriorPoints.Select(point => context.Plane.Project(point.Coordinates)).ToArray();
            Tuple<double[], double[]>[] frameSegments = context.FrameConstraints.Select(frame =>
                Tuple.Create(context.Plane.Project(frame.First), context.Plane.Project(frame.Second))).ToArray();
            var axes = new List<SymmetryAxis>();
            foreach (double[] direction in candidates)
            {
                if (checkpoint != null) checkpoint();
                if (!ReflectionMatches(corners, center2D, direction, tolerance) ||
                    !ReflectionEdgesMatch(corners, center2D, direction, tolerance) ||
                    !ReflectionMatches(boundaryPoints, center2D, direction, tolerance) ||
                    !ReflectionMatches(interiorPoints, center2D, direction, tolerance) ||
                    !ReflectionSegmentsMatch(frameSegments, center2D, direction, tolerance)) continue;
                double[] globalDirection = context.Plane.VectorFromPlane(direction[0], direction[1]);
                double[] globalCenter =
                {
                    context.Corners.Average(point => point[0]), context.Corners.Average(point => point[1]),
                    context.Corners.Average(point => point[2])
                };
                axes.Add(new SymmetryAxis(globalCenter, globalDirection, axes.Count + 1));
            }
            return AddDoublePrincipalSymmetry(axes, context.Corners);
        }

        private static SymmetryAxis[] DetectMultiAreaSymmetryAxes(List<AreaContext> contexts, Action checkpoint = null)
        {
            Plane plane = contexts[0].Plane;
            if (contexts.SelectMany(context => context.Corners).Any(point => Math.Abs(plane.SignedDistance(point)) > Tolerance))
                return new SymmetryAxis[0];
            double[][][] polygons = contexts.Select(context => context.Corners.Select(plane.Project).ToArray()).ToArray();
            double minX = polygons.SelectMany(polygon => polygon).Min(point => point[0]);
            double maxX = polygons.SelectMany(polygon => polygon).Max(point => point[0]);
            double minY = polygons.SelectMany(polygon => polygon).Min(point => point[1]);
            double maxY = polygons.SelectMany(polygon => polygon).Max(point => point[1]);
            double[] center = { (minX + maxX) * 0.5, (minY + maxY) * 0.5 };
            double tolerance = Math.Max(Tolerance * 10, Math.Max(maxX - minX, maxY - minY) * 1e-6);
            var directions = new List<double[]> { new[] { 0.0, 1.0 }, new[] { 1.0, 0.0 } };
            if (Math.Abs((maxX - minX) - (maxY - minY)) <= tolerance)
            {
                double inverseRoot = 1 / Math.Sqrt(2);
                directions.Add(new[] { inverseRoot, inverseRoot });
                directions.Add(new[] { inverseRoot, -inverseRoot });
            }
            var axes = new List<SymmetryAxis>();
            Tuple<double[], double[]>[] frameSegments = contexts.SelectMany(context => context.FrameConstraints.Select(frame =>
                Tuple.Create(plane.Project(frame.First), plane.Project(frame.Second)))).ToArray();
            double[][] constrainedPoints = contexts.SelectMany(context => context.EdgePoints.SelectMany(edge => edge)
                .Select(point => plane.Project(point.Coordinates)).Concat(context.InteriorPoints.Select(point =>
                    plane.Project(point.Coordinates)))).ToArray();
            foreach (double[] direction in directions)
            {
                bool matches = true;
                foreach (double[][] polygon in polygons)
                {
                    if (checkpoint != null) checkpoint();
                    double[][] reflected = polygon.Select(point => ReflectPoint(point, center, direction)).ToArray();
                    if (polygons.Any(candidate => reflected.All(point =>
                        candidate.Any(candidatePoint => Distance2D(point, candidatePoint) <= tolerance)))) continue;
                    matches = false;
                    break;
                }
                if (!matches || !ReflectionMatches(constrainedPoints, center, direction, tolerance) ||
                    !ReflectionSegmentsMatch(frameSegments, center, direction, tolerance)) continue;
                axes.Add(new SymmetryAxis(plane.PointFromPlane(center[0], center[1]),
                    plane.VectorFromPlane(direction[0], direction[1]), axes.Count + 1));
            }
            return AddDoublePrincipalSymmetry(axes, contexts.SelectMany(context => context.Corners).ToArray());
        }

        private static SymmetryAxis[] AddDoublePrincipalSymmetry(List<SymmetryAxis> axes, double[][] corners)
        {
            var edgeDirections = new List<double[]>();
            for (int index = 0; index < corners.Length; index++)
            {
                double[] edge = Subtract(corners[(index + 1) % corners.Length], corners[index]);
                if (Norm(edge) > Tolerance) edgeDirections.Add(Normalize(edge));
            }
            SymmetryAxis[] principal = axes.Where(axis => edgeDirections.Any(edge =>
                Math.Abs(Dot(edge, axis.Direction)) >= 1 - 1e-8)).ToArray();
            for (int first = 0; first < principal.Length; first++)
                for (int second = first + 1; second < principal.Length; second++)
                    if (Math.Abs(Dot(principal[first].Direction, principal[second].Direction)) <= 1e-8)
                        return new[] { new SymmetryAxis(principal[first].Center, principal[first].Direction,
                            principal[second].Direction) }.Concat(axes).ToArray();
            return axes.ToArray();
        }

        private static bool ReflectionMatches(double[][] points, double[] center, double[] direction, double tolerance)
        {
            foreach (double[] point in points)
            {
                double dx = point[0] - center[0], dy = point[1] - center[1];
                double projection = dx * direction[0] + dy * direction[1];
                double[] reflected =
                {
                    center[0] + 2 * projection * direction[0] - dx,
                    center[1] + 2 * projection * direction[1] - dy
                };
                if (!points.Any(candidate => Math.Sqrt((candidate[0] - reflected[0]) * (candidate[0] - reflected[0]) +
                    (candidate[1] - reflected[1]) * (candidate[1] - reflected[1])) <= tolerance)) return false;
            }
            return true;
        }

        private static bool ReflectionEdgesMatch(double[][] polygon, double[] center, double[] direction, double tolerance)
        {
            for (int index = 0; index < polygon.Length; index++)
            {
                double[] first = ReflectPoint(polygon[index], center, direction);
                double[] second = ReflectPoint(polygon[(index + 1) % polygon.Length], center, direction);
                bool matched = Enumerable.Range(0, polygon.Length).Any(candidate =>
                    (Distance2D(first, polygon[candidate]) <= tolerance &&
                     Distance2D(second, polygon[(candidate + 1) % polygon.Length]) <= tolerance) ||
                    (Distance2D(second, polygon[candidate]) <= tolerance &&
                     Distance2D(first, polygon[(candidate + 1) % polygon.Length]) <= tolerance));
                if (!matched) return false;
            }
            return true;
        }

        private static bool ReflectionSegmentsMatch(Tuple<double[], double[]>[] segments, double[] center,
            double[] direction, double tolerance)
        {
            foreach (Tuple<double[], double[]> segment in segments)
            {
                double[] first = ReflectPoint(segment.Item1, center, direction);
                double[] second = ReflectPoint(segment.Item2, center, direction);
                if (!segments.Any(candidate =>
                    (Distance2D(first, candidate.Item1) <= tolerance && Distance2D(second, candidate.Item2) <= tolerance) ||
                    (Distance2D(first, candidate.Item2) <= tolerance && Distance2D(second, candidate.Item1) <= tolerance)))
                    return false;
            }
            return true;
        }

        private static double[] ReflectPoint(double[] point, double[] center, double[] direction)
        {
            double dx = point[0] - center[0], dy = point[1] - center[1];
            double projection = dx * direction[0] + dy * direction[1];
            return new[] { center[0] + 2 * projection * direction[0] - dx,
                center[1] + 2 * projection * direction[1] - dy };
        }

        private static double Distance2D(double[] first, double[] second)
        {
            double dx = first[0] - second[0], dy = first[1] - second[1];
            return Math.Sqrt(dx * dx + dy * dy);
        }

        private static string LengthUnit(eUnits units)
        {
            switch (units)
            {
                case eUnits.lb_in_F:
                case eUnits.kip_in_F: return "in";
                case eUnits.lb_ft_F:
                case eUnits.kip_ft_F: return "ft";
                case eUnits.kN_mm_C:
                case eUnits.kgf_mm_C:
                case eUnits.N_mm_C:
                case eUnits.Ton_mm_C: return "mm";
                case eUnits.kN_cm_C:
                case eUnits.kgf_cm_C:
                case eUnits.N_cm_C:
                case eUnits.Ton_cm_C: return "cm";
                case eUnits.kN_m_C:
                case eUnits.kgf_m_C:
                case eUnits.N_m_C:
                case eUnits.Ton_m_C: return "m";
                default: throw new InvalidOperationException("Unsupported SAP2000 present units: " + units + ".");
            }
        }

        private static double MetersPerUnit(string unit)
        {
            switch (unit)
            {
                case "m": return 1;
                case "cm": return 0.01;
                case "mm": return 0.001;
                case "ft": return 0.3048;
                case "in": return 0.0254;
                default: throw new ArgumentException("Unsupported length unit: " + unit);
            }
        }

        private static bool TryParseLength(string text, string presentUnit, out double value)
        {
            value = 0;
            Match match = Regex.Match(text ?? "", @"^\s*(?<number>(?:\d+(?:[.,]\d*)?|[.,]\d+)(?:[eE][+-]?\d+)?)\s*(?<unit>mm|cm|m|ft|in)?\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success) return false;
            double amount;
            if (!double.TryParse(match.Groups["number"].Value.Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out amount) || double.IsNaN(amount) || double.IsInfinity(amount) || amount <= 0)
                return false;
            string unit = match.Groups["unit"].Success ? match.Groups["unit"].Value.ToLowerInvariant() : presentUnit;
            value = amount * MetersPerUnit(unit) / MetersPerUnit(presentUnit);
            return !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
        }

        private static void RestoreSelection(cSapModel model, int[] types, string[] names)
        {
            int length = names == null ? 0 : names.Length;
            var expected = new HashSet<string>(Enumerable.Range(0, length)
                .Select(i => types[i] + ":" + names[i]), StringComparer.OrdinalIgnoreCase);
            HashSet<string> selected = ReadSelectedObjects(model);
            if (selected != null && selected.SetEquals(expected)) return;

            // If Undo already left the retained joints/frames selected, do not
            // toggle the SAP selection history a second time: only reselect
            // missing recreated areas.
            if (selected != null && selected.Count > 0 && selected.IsSubsetOf(expected))
            {
                for (int i = 0; i < length; i++)
                    if (!selected.Contains(types[i] + ":" + names[i]))
                        SelectForRestore(model, types[i], names[i]);
                selected = ReadSelectedObjects(model);
                if (selected != null && selected.SetEquals(expected)) return;
            }

            // SAP's single PreviousSelection call is fast for retained joints
            // and frames. Recreated source areas are not in the old selection:
            // add only those missing objects, never rely on history blindly.
            try
            {
                if (model.SelectObj.PreviousSelection() == 0)
                    selected = ReadSelectedObjects(model);
            }
            catch (COMException) { selected = null; }
            if (selected != null && selected.IsSubsetOf(expected))
            {
                for (int i = 0; i < length; i++)
                    if (!selected.Contains(types[i] + ":" + names[i]))
                        SelectForRestore(model, types[i], names[i]);
                HashSet<string> restored = ReadSelectedObjects(model);
                if (restored != null && restored.SetEquals(expected)) return;
            }

            // An unrelated prior selection or an unavailable history is not
            // sufficient evidence to drop an original user selection.
            Check(model.SelectObj.ClearSelection(), "clearing the selection", "SelectObj.ClearSelection");
            for (int i = 0; i < length; i++) SelectForRestore(model, types[i], names[i]);
            selected = ReadSelectedObjects(model);
            if (selected == null || !selected.SetEquals(expected))
                throw new InvalidDataException("The original SAP2000 selection could not be restored exactly.");
        }

        private static HashSet<string> ReadSelectedObjects(cSapModel model)
        {
            int count = 0;
            int[] types = null;
            string[] names = null;
            if (model.SelectObj.GetSelected(ref count, ref types, ref names) != 0 || count < 0 ||
                count > 0 && (types == null || names == null || types.Length < count || names.Length < count))
                return null;
            return new HashSet<string>(Enumerable.Range(0, count).Select(i => types[i] + ":" + names[i]),
                StringComparer.OrdinalIgnoreCase);
        }

        private static void SelectForRestore(cSapModel model, int type, string name)
        {
            switch (type)
            {
                case PointObjectType: Check(model.PointObj.SetSelected(name, true, eItemType.Objects), "restoring selected point " + name, "PointObj.SetSelected"); break;
                case FrameObjectType: Check(model.FrameObj.SetSelected(name, true, eItemType.Objects), "restoring selected frame " + name, "FrameObj.SetSelected"); break;
                case CableObjectType: Check(model.CableObj.SetSelected(name, true, eItemType.Objects), "restoring selected cable " + name, "CableObj.SetSelected"); break;
                case TendonObjectType: Check(model.TendonObj.SetSelected(name, true, eItemType.Objects), "restoring selected tendon " + name, "TendonObj.SetSelected"); break;
                case AreaObjectType: Check(model.AreaObj.SetSelected(name, true, eItemType.Objects), "restoring selected area " + name, "AreaObj.SetSelected"); break;
                case SolidObjectType: Check(model.SolidObj.SetSelected(name, true, eItemType.Objects), "restoring selected solid " + name, "SolidObj.SetSelected"); break;
                case LinkObjectType: Check(model.LinkObj.SetSelected(name, true, eItemType.Objects), "restoring selected link " + name, "LinkObj.SetSelected"); break;
                default: throw new InvalidOperationException("Unsupported selected SAP2000 object type: " + type + ".");
            }
        }

        private static AreaContext BuildAreaContext(cSapModel model, string areaName)
        {
            int cornerCount = 0;
            string[] cornerNames = null;
            Check(model.AreaObj.GetPoints(areaName, ref cornerCount, ref cornerNames), "reading area vertices for " + areaName, "AreaObj.GetPoints");
            if (cornerCount < 3) throw new InvalidOperationException("Area " + areaName + " must have at least three edges.");
            double[][] corners = cornerNames.Select(name => Coordinate(model, name)).ToArray();
            var context = new AreaContext(areaName, cornerNames, corners, Plane.FromPolygon(corners));
            for (int edge = 0; edge < cornerCount; edge++)
            {
                context.EdgePoints[edge].Add(new BoundaryPoint(cornerNames[edge], 0, corners[edge]));
                context.EdgePoints[edge].Add(new BoundaryPoint(cornerNames[(edge + 1) % cornerCount], 1, corners[(edge + 1) % cornerCount]));
            }
            return context;
        }

        private static bool AddConstraintPoint(AreaContext context, string pointName, double[] point, bool captureSnapshot = true)
        {
            if (Math.Abs(context.Plane.SignedDistance(point)) > Tolerance) return false;
            for (int edge = 0; edge < context.Corners.Length; edge++)
            {
                double t;
                if (!OnSegment(point, context.Corners[edge], context.Corners[(edge + 1) % context.Corners.Length], out t)) continue;
                BoundaryPoint existing = context.EdgePoints[edge].FirstOrDefault(item =>
                    Math.Abs(item.T - t) <= Tolerance);
                if (existing != null && !string.Equals(existing.Name, pointName, StringComparison.OrdinalIgnoreCase))
                    return false; // Keep the area's own topological node, not a coincident duplicate.
                if (existing == null) context.EdgePoints[edge].Add(new BoundaryPoint(pointName, t, point));
                if (captureSnapshot) context.ConstraintPointNames.Add(pointName);
                return true;
            }
            if (!PointInPolygon(context.Plane.Project(point), context.Corners.Select(context.Plane.Project).ToArray())) return false;
            ConstraintPoint matching = context.InteriorPoints.FirstOrDefault(item =>
                Distance(item.Coordinates, point) <= Tolerance);
            if (matching != null && !string.Equals(matching.Name, pointName, StringComparison.OrdinalIgnoreCase))
                return false;
            if (matching == null) context.InteriorPoints.Add(new ConstraintPoint(pointName, point));
            if (captureSnapshot) context.ConstraintPointNames.Add(pointName);
            return true;
        }

        private static bool FrameBelongsToArea(AreaContext context, double[] first, double[] second)
        {
            if (Math.Abs(context.Plane.SignedDistance(first)) > Tolerance || Math.Abs(context.Plane.SignedDistance(second)) > Tolerance)
                return false;
            double[][] polygon = context.Corners.Select(context.Plane.Project).ToArray();
            for (int sample = 0; sample <= 20; sample++)
            {
                double t = sample / 20.0;
                double[] point = Enumerable.Range(0, 3).Select(axis => first[axis] * (1 - t) + second[axis] * t).ToArray();
                if (!PointInsideOrBoundary(context.Plane.Project(point), polygon)) return false;
            }
            return true;
        }

        private static bool FrameOnBoundary(AreaContext context, double[] first, double[] second)
        {
            for (int edge = 0; edge < context.Corners.Length; edge++)
            {
                double firstT, secondT;
                if (OnSegment(first, context.Corners[edge], context.Corners[(edge + 1) % context.Corners.Length], out firstT) &&
                    OnSegment(second, context.Corners[edge], context.Corners[(edge + 1) % context.Corners.Length], out secondT)) return true;
            }
            return false;
        }

        private static bool HasSelectedInteriorEdgePoint(AreaContext context, int edge)
        {
            return context.EdgePoints[edge].Any(point => point.T > Tolerance && point.T < 1 - Tolerance &&
                context.ExplicitlySelectedPointNames.Contains(point.Name));
        }

        private static void HarmonizeSharedBoundaries(List<AreaContext> contexts, double maxSize,
            Action checkpoint = null, bool preserveSelectedEdges = false)
        {
            if (contexts.Count < 2) return;
            var sharedEdges = new HashSet<string>();
            var protectedSharedEdges = new HashSet<string>();
            for (int firstArea = 0; firstArea < contexts.Count; firstArea++)
            {
                if (checkpoint != null) checkpoint();
                for (int secondArea = firstArea + 1; secondArea < contexts.Count; secondArea++)
                    for (int firstEdge = 0; firstEdge < contexts[firstArea].Corners.Length; firstEdge++)
                        for (int secondEdge = 0; secondEdge < contexts[secondArea].Corners.Length; secondEdge++)
                        {
                            double[] overlapStart, overlapEnd;
                            if (!TryGetCollinearOverlap(
                                contexts[firstArea].Corners[firstEdge], contexts[firstArea].Corners[(firstEdge + 1) % contexts[firstArea].Corners.Length],
                                contexts[secondArea].Corners[secondEdge], contexts[secondArea].Corners[(secondEdge + 1) % contexts[secondArea].Corners.Length],
                                out overlapStart, out overlapEnd)) continue;
                            sharedEdges.Add(firstArea + ":" + firstEdge);
                            sharedEdges.Add(secondArea + ":" + secondEdge);
                            bool locked = preserveSelectedEdges &&
                                (HasSelectedInteriorEdgePoint(contexts[firstArea], firstEdge) ||
                                 HasSelectedInteriorEdgePoint(contexts[secondArea], secondEdge));
                            if (locked)
                            {
                                protectedSharedEdges.Add(firstArea + ":" + firstEdge);
                                protectedSharedEdges.Add(secondArea + ":" + secondEdge);
                            }
                            var breakpoints = new List<double[]> { overlapStart, overlapEnd };
                            foreach (BoundaryPoint point in contexts[firstArea].EdgePoints[firstEdge].Concat(contexts[secondArea].EdgePoints[secondEdge]))
                            {
                                double ignored;
                                if (OnSegment(point.Coordinates, overlapStart, overlapEnd, out ignored) &&
                                    !breakpoints.Any(existing => Distance(existing, point.Coordinates) <= Tolerance))
                                    breakpoints.Add(point.Coordinates);
                            }
                            double[] direction = Subtract(overlapEnd, overlapStart);
                            double lengthSquared = Dot(direction, direction);
                            breakpoints = breakpoints.OrderBy(point => Dot(Subtract(point, overlapStart), direction) / lengthSquared).ToList();
                            for (int i = 1; i < breakpoints.Count; i++)
                            {
                                // Shared edges are internal to the selected union. Both
                                // areas must use the same new nodes to obtain a useful
                                // graded mesh; only the exposed contour is immutable.
                                int divisions = locked ? 1 : Math.Max(1,
                                    (int)Math.Ceiling(Distance(breakpoints[i - 1], breakpoints[i]) / maxSize - 1e-9));
                                for (int division = 0; division <= divisions; division++)
                                {
                                    double t = (double)division / divisions;
                                    double[] point = Enumerable.Range(0, 3).Select(axis => breakpoints[i - 1][axis] * (1 - t) + breakpoints[i][axis] * t).ToArray();
                                    AddBoundaryCoordinate(contexts[firstArea], firstEdge, point);
                                    AddBoundaryCoordinate(contexts[secondArea], secondEdge, point);
                                }
                            }
                        }
            }

            foreach (string key in sharedEdges)
            {
                if (protectedSharedEdges.Contains(key)) continue;
                if (checkpoint != null) checkpoint();
                string[] fields = key.Split(':');
                AreaContext context = contexts[int.Parse(fields[0], CultureInfo.InvariantCulture)];
                int edge = int.Parse(fields[1], CultureInfo.InvariantCulture);
                List<BoundaryPoint> points = context.EdgePoints[edge].OrderBy(point => point.T).ToList();
                for (int i = 1; i < points.Count; i++)
                {
                    int divisions = Math.Max(1, (int)Math.Ceiling(Distance(points[i - 1].Coordinates, points[i].Coordinates) / maxSize - 1e-9));
                    for (int division = 1; division < divisions; division++)
                    {
                        double t = (double)division / divisions;
                        AddBoundaryCoordinate(context, edge, Enumerable.Range(0, 3)
                            .Select(axis => points[i - 1].Coordinates[axis] * (1 - t) + points[i].Coordinates[axis] * t).ToArray());
                    }
                }
                context.EdgePoints[edge] = context.EdgePoints[edge].OrderBy(point => point.T).ToList();
            }
        }

        private static void AddBoundaryCoordinate(AreaContext context, int edge, double[] point)
        {
            double t;
            if (!OnSegment(point, context.Corners[edge], context.Corners[(edge + 1) % context.Corners.Length], out t))
                throw new InvalidOperationException("Failed to discretize a shared edge.");
            if (!context.EdgePoints[edge].Any(existing => Math.Abs(existing.T - t) <= Tolerance))
                context.EdgePoints[edge].Add(new BoundaryPoint("QT_SHARED", t, point));
        }

        private static void HarmonizeAreaEdgeIntersections(List<AreaContext> contexts, double maxSize, Action checkpoint = null)
        {
            var userConstrainedEdges = new HashSet<string>();
            var automaticBoundaryEdges = new HashSet<string>();
            var intersections = new List<AreaEdgeIntersection>();
            foreach (AreaContext context in contexts)
            {
                context.FrameConstraints.RemoveAll(frame => frame.Automatic);
                context.ForceRefinement = false;
            }
            for (int contextIndex = 0; contextIndex < contexts.Count; contextIndex++)
                for (int edge = 0; edge < contexts[contextIndex].EdgePoints.Length; edge++)
                    if (contexts[contextIndex].EdgePoints[edge].Count > 2)
                        userConstrainedEdges.Add(contextIndex + ":" + edge);
            for (int sourceIndex = 0; sourceIndex < contexts.Count; sourceIndex++)
            {
                if (checkpoint != null) checkpoint();
                for (int hostIndex = 0; hostIndex < contexts.Count; hostIndex++)
                {
                    if (sourceIndex == hostIndex) continue;
                    AreaContext source = contexts[sourceIndex], host = contexts[hostIndex];
                    if (Math.Abs(Dot(source.Plane.Normal, host.Plane.Normal)) >= 1 - 1e-8) continue;
                    for (int edge = 0; edge < source.Corners.Length; edge++)
                    {
                        double[] first = source.Corners[edge], second = source.Corners[(edge + 1) % source.Corners.Length];
                        foreach (SegmentInterval interval in ClipSegmentToArea(host, first, second))
                            intersections.Add(new AreaEdgeIntersection(sourceIndex, edge, hostIndex, interval.Start, interval.End));
                    }
                }
            }

            foreach (AreaEdgeIntersection intersection in intersections)
            {
                if (checkpoint != null) checkpoint();
                AreaContext source = contexts[intersection.SourceIndex];
                double[] first = source.Corners[intersection.SourceEdge];
                double[] second = source.Corners[(intersection.SourceEdge + 1) % source.Corners.Length];
                AddBoundaryCoordinate(source, intersection.SourceEdge, Interpolate(first, second, intersection.Start));
                AddBoundaryCoordinate(source, intersection.SourceEdge, Interpolate(first, second, intersection.End));
            }

            foreach (AreaEdgeIntersection intersection in intersections)
            {
                AreaContext source = contexts[intersection.SourceIndex];
                List<BoundaryPoint> current = source.EdgePoints[intersection.SourceEdge]
                    .Where(point => point.T >= intersection.Start - Tolerance && point.T <= intersection.End + Tolerance)
                    .OrderBy(point => point.T).ToList();
                for (int i = 1; i < current.Count; i++)
                {
                    int divisions = Math.Max(1, (int)Math.Ceiling(Distance(current[i - 1].Coordinates, current[i].Coordinates) / maxSize - 1e-9));
                    for (int division = 1; division < divisions; division++)
                        AddBoundaryCoordinate(source, intersection.SourceEdge,
                            Interpolate(current[i - 1].Coordinates, current[i].Coordinates, (double)division / divisions));
                }
                source.EdgePoints[intersection.SourceEdge] = source.EdgePoints[intersection.SourceEdge].OrderBy(point => point.T).ToList();
            }

            foreach (AreaEdgeIntersection intersection in intersections)
            {
                AreaContext source = contexts[intersection.SourceIndex], host = contexts[intersection.HostIndex];
                source.ForceRefinement = true;
                List<BoundaryPoint> points = source.EdgePoints[intersection.SourceEdge]
                    .Where(point => point.T >= intersection.Start - Tolerance && point.T <= intersection.End + Tolerance)
                    .OrderBy(point => point.T).ToList();
                for (int i = 1; i < points.Count; i++)
                {
                    BoundaryPoint start = points[i - 1], end = points[i];
                    if (Distance(start.Coordinates, end.Coordinates) <= Tolerance) continue;
                    AddConstraintPoint(host, start.Name, start.Coordinates, false);
                    AddConstraintPoint(host, end.Name, end.Coordinates, false);
                    for (int hostEdge = 0; hostEdge < host.Corners.Length; hostEdge++)
                    {
                        double ignored;
                        if (OnSegment(start.Coordinates, host.Corners[hostEdge], host.Corners[(hostEdge + 1) % host.Corners.Length], out ignored) ||
                            OnSegment(end.Coordinates, host.Corners[hostEdge], host.Corners[(hostEdge + 1) % host.Corners.Length], out ignored))
                            automaticBoundaryEdges.Add(intersection.HostIndex + ":" + hostEdge);
                    }
                    if (!host.FrameConstraints.Any(frame => SameSegment(frame.First, frame.Second, start.Coordinates, end.Coordinates)))
                        host.FrameConstraints.Add(new FrameConstraint("QT_INTERSECTION_" + source.Name + "_" + (intersection.SourceEdge + 1),
                            start.Name, end.Name, start.Coordinates, end.Coordinates, true, source, intersection.SourceEdge));
                }
            }

            foreach (AreaContext context in contexts) SplitConstraintIntersections(context);
            foreach (AreaContext context in contexts) SplitAutomaticConstraintsAtSourcePoints(context);
            foreach (string key in automaticBoundaryEdges.Where(key => !userConstrainedEdges.Contains(key)))
            {
                string[] fields = key.Split(':');
                AreaContext context = contexts[int.Parse(fields[0], CultureInfo.InvariantCulture)];
                int edge = int.Parse(fields[1], CultureInfo.InvariantCulture);
                List<BoundaryPoint> points = context.EdgePoints[edge].OrderBy(point => point.T).ToList();
                for (int i = 1; i < points.Count; i++)
                {
                    int divisions = Math.Max(1, (int)Math.Ceiling(Distance(points[i - 1].Coordinates, points[i].Coordinates) / maxSize - 1e-9));
                    for (int division = 1; division < divisions; division++)
                    {
                        double t = (double)division / divisions;
                        AddBoundaryCoordinate(context, edge, Enumerable.Range(0, 3)
                            .Select(axis => points[i - 1].Coordinates[axis] * (1 - t) + points[i].Coordinates[axis] * t).ToArray());
                    }
                }
                context.EdgePoints[edge] = context.EdgePoints[edge].OrderBy(point => point.T).ToList();
            }
        }

        private static List<SegmentInterval> ClipSegmentToArea(AreaContext host, double[] first, double[] second)
        {
            var intervals = new List<SegmentInterval>();
            if (Math.Abs(host.Plane.SignedDistance(first)) > Tolerance || Math.Abs(host.Plane.SignedDistance(second)) > Tolerance)
                return intervals;
            double[] start = host.Plane.Project(first), end = host.Plane.Project(second);
            double[][] polygon = host.Corners.Select(host.Plane.Project).ToArray();
            var parameters = new List<double> { 0, 1 };
            for (int edge = 0; edge < polygon.Length; edge++)
                AddSegmentIntersectionParameters(start, end, polygon[edge], polygon[(edge + 1) % polygon.Length], parameters);
            List<double> sorted = parameters.Select(value => Math.Max(0, Math.Min(1, value))).OrderBy(value => value).ToList();
            var unique = new List<double>();
            foreach (double value in sorted)
                if (unique.Count == 0 || Math.Abs(unique[unique.Count - 1] - value) > Tolerance) unique.Add(value);
            for (int i = 1; i < unique.Count; i++)
            {
                double low = unique[i - 1], high = unique[i];
                if ((high - low) * Distance(first, second) <= Tolerance) continue;
                if (!PointInsideOrBoundary(Interpolate(start, end, (low + high) * 0.5), polygon)) continue;
                if (!FrameOnBoundary(host, Interpolate(first, second, low), Interpolate(first, second, high)))
                    intervals.Add(new SegmentInterval(low, high));
            }
            return intervals;
        }

        private static void AddSegmentIntersectionParameters(double[] first, double[] second, double[] edgeFirst, double[] edgeSecond,
            List<double> parameters)
        {
            double rx = second[0] - first[0], ry = second[1] - first[1];
            double sx = edgeSecond[0] - edgeFirst[0], sy = edgeSecond[1] - edgeFirst[1];
            double qx = edgeFirst[0] - first[0], qy = edgeFirst[1] - first[1];
            double denominator = Cross2(rx, ry, sx, sy);
            if (Math.Abs(denominator) > Tolerance)
            {
                double t = Cross2(qx, qy, sx, sy) / denominator;
                double u = Cross2(qx, qy, rx, ry) / denominator;
                if (t >= -Tolerance && t <= 1 + Tolerance && u >= -Tolerance && u <= 1 + Tolerance) parameters.Add(t);
                return;
            }
            if (Math.Abs(Cross2(qx, qy, rx, ry)) > Tolerance) return;
            double lengthSquared = rx * rx + ry * ry;
            if (lengthSquared <= Tolerance * Tolerance) return;
            parameters.Add((qx * rx + qy * ry) / lengthSquared);
            parameters.Add(((edgeSecond[0] - first[0]) * rx + (edgeSecond[1] - first[1]) * ry) / lengthSquared);
        }

        private static void SplitConstraintIntersections(AreaContext host)
        {
            List<FrameConstraint> frames = host.FrameConstraints.OrderByDescending(frame => frame.Automatic).ToList();
            var breakpoints = frames.Select(frame => new List<double[]> { frame.First, frame.Second }).ToArray();
            for (int first = 0; first < frames.Count; first++)
                for (int second = first + 1; second < frames.Count; second++)
                    AddConstraintIntersection(host.Plane, frames[first], frames[second], breakpoints[first], breakpoints[second]);
            host.FrameConstraints.Clear();
            for (int index = 0; index < frames.Count; index++)
            {
                FrameConstraint frame = frames[index];
                List<double[]> points = breakpoints[index].OrderBy(point => SegmentParameter(point, frame.First, frame.Second)).ToList();
                for (int i = 1; i < points.Count; i++)
                {
                    double[] first = points[i - 1], second = points[i];
                    if (Distance(first, second) <= Tolerance || host.FrameConstraints.Any(item => SameSegment(item.First, item.Second, first, second))) continue;
                    AddConstraintPoint(host, "QT_INTERSECTION", first, false);
                    AddConstraintPoint(host, "QT_INTERSECTION", second, false);
                    if (frame.Automatic)
                    {
                        AddBoundaryCoordinate(frame.SourceContext, frame.SourceEdge, first);
                        AddBoundaryCoordinate(frame.SourceContext, frame.SourceEdge, second);
                    }
                    host.FrameConstraints.Add(new FrameConstraint(frame.Name, frame.FirstName, frame.SecondName, first, second,
                        frame.Automatic, frame.SourceContext, frame.SourceEdge));
                }
            }
        }

        private static void AddConstraintIntersection(Plane plane, FrameConstraint first, FrameConstraint second,
            List<double[]> firstPoints, List<double[]> secondPoints)
        {
            double[] a = plane.Project(first.First), b = plane.Project(first.Second);
            double[] c = plane.Project(second.First), d = plane.Project(second.Second);
            double rx = b[0] - a[0], ry = b[1] - a[1], sx = d[0] - c[0], sy = d[1] - c[1];
            double qx = c[0] - a[0], qy = c[1] - a[1], denominator = Cross2(rx, ry, sx, sy);
            if (Math.Abs(denominator) > Tolerance)
            {
                double t = Cross2(qx, qy, sx, sy) / denominator;
                double u = Cross2(qx, qy, rx, ry) / denominator;
                if (t >= -Tolerance && t <= 1 + Tolerance && u >= -Tolerance && u <= 1 + Tolerance)
                {
                    double[] point = Interpolate(first.First, first.Second, Math.Max(0, Math.Min(1, t)));
                    AddUniquePoint(firstPoints, point); AddUniquePoint(secondPoints, point);
                }
                return;
            }
            if (Math.Abs(Cross2(qx, qy, rx, ry)) > Tolerance) return;
            foreach (double[] point in new[] { first.First, first.Second, second.First, second.Second })
            {
                double firstT, secondT;
                if (OnSegment(point, first.First, first.Second, out firstT) && OnSegment(point, second.First, second.Second, out secondT))
                { AddUniquePoint(firstPoints, point); AddUniquePoint(secondPoints, point); }
            }
        }

        private static void SplitAutomaticConstraintsAtSourcePoints(AreaContext host)
        {
            var split = new List<FrameConstraint>();
            foreach (FrameConstraint frame in host.FrameConstraints)
            {
                if (!frame.Automatic)
                {
                    split.Add(frame);
                    continue;
                }
                List<double[]> points = frame.SourceContext.EdgePoints[frame.SourceEdge].Select(point => point.Coordinates)
                    .Where(point =>
                    {
                        double ignored;
                        return OnSegment(point, frame.First, frame.Second, out ignored);
                    }).OrderBy(point => SegmentParameter(point, frame.First, frame.Second)).ToList();
                for (int i = 1; i < points.Count; i++)
                    if (Distance(points[i - 1], points[i]) > Tolerance &&
                        !split.Any(item => SameSegment(item.First, item.Second, points[i - 1], points[i])))
                        split.Add(new FrameConstraint(frame.Name, frame.FirstName, frame.SecondName, points[i - 1], points[i],
                            true, frame.SourceContext, frame.SourceEdge));
            }
            host.FrameConstraints.Clear();
            host.FrameConstraints.AddRange(split);
        }

        private static void AddUniquePoint(List<double[]> points, double[] point)
        {
            if (!points.Any(existing => Distance(existing, point) <= Tolerance)) points.Add(point);
        }

        private static bool SameSegment(double[] firstStart, double[] firstEnd, double[] secondStart, double[] secondEnd)
        {
            return (Distance(firstStart, secondStart) <= Tolerance && Distance(firstEnd, secondEnd) <= Tolerance) ||
                (Distance(firstStart, secondEnd) <= Tolerance && Distance(firstEnd, secondStart) <= Tolerance);
        }

        private static double SegmentParameter(double[] point, double[] first, double[] second)
        {
            double[] direction = Subtract(second, first);
            return Dot(Subtract(point, first), direction) / Dot(direction, direction);
        }

        private static double[] Interpolate(double[] first, double[] second, double t)
        {
            return Enumerable.Range(0, first.Length).Select(axis => first[axis] * (1 - t) + second[axis] * t).ToArray();
        }

        private static double Cross2(double ax, double ay, double bx, double by) { return ax * by - ay * bx; }

        private static bool TryGetCollinearOverlap(double[] firstStart, double[] firstEnd, double[] secondStart, double[] secondEnd,
            out double[] overlapStart, out double[] overlapEnd)
        {
            overlapStart = overlapEnd = null;
            double[] firstDirection = Subtract(firstEnd, firstStart), secondDirection = Subtract(secondEnd, secondStart);
            double firstLength = Norm(firstDirection), secondLength = Norm(secondDirection);
            if (Norm(Cross(firstDirection, secondDirection)) > Tolerance * firstLength * secondLength) return false;
            if (Norm(Cross(Subtract(secondStart, firstStart), firstDirection)) > Tolerance * firstLength ||
                Norm(Cross(Subtract(secondEnd, firstStart), firstDirection)) > Tolerance * firstLength) return false;
            double denominator = Dot(firstDirection, firstDirection);
            double secondT0 = Dot(Subtract(secondStart, firstStart), firstDirection) / denominator;
            double secondT1 = Dot(Subtract(secondEnd, firstStart), firstDirection) / denominator;
            double low = Math.Max(0, Math.Min(secondT0, secondT1));
            double high = Math.Min(1, Math.Max(secondT0, secondT1));
            if ((high - low) * firstLength <= Tolerance) return false;
            overlapStart = Enumerable.Range(0, 3).Select(axis => firstStart[axis] + low * firstDirection[axis]).ToArray();
            overlapEnd = Enumerable.Range(0, 3).Select(axis => firstStart[axis] + high * firstDirection[axis]).ToArray();
            return true;
        }

        private static MeshQualityReport CombineQuality(List<AreaContext> contexts, List<PreparedMesh> prepared,
            MeshOptionsDialog dialog)
        {
            return EvaluateGmshQuality(contexts, prepared, dialog);
        }

        private static bool AcceptGmshSize(double nominal, double maximumFree,
            double oversizedFraction, double weightedExcess)
        {
            // MeshSizeMax is a Gmsh target. Small, localized overruns are
            // reported, but only widespread/severe sizing fails this check.
            // The gross cap still prevents a tiny, very coarse cell from
            // disappearing into the surface-weighted average.
            return maximumFree <= nominal * 1.5 * 1.001 &&
                oversizedFraction <= .10 && weightedExcess <= .02;
        }

        private static MeshQualityReport EvaluateGmshQuality(List<AreaContext> contexts,
            List<PreparedMesh> prepared, MeshOptionsDialog dialog)
        {
            // Geometry/size are separate properties: the shape score is the
            // physical-area-weighted mean of ALL shells, including single-area
            // selections. A selected pre-existing edge longer than the target
            // is exempt only on that specific original segment.
            double areaSum = 0, shapeSum = 0, badArea = 0, oversizedArea = 0, weightedSizeExcess = 0;
            double weightedEdge = 0, weightedAspect = 0, weightedSkew = 0, weightedJacobian = 0;
            double quadArea = 0, triArea = 0, quadMin = 0, quadMax = 0, triMin = 0, triMax = 0;
            double maximumFree = 0, maximumProtected = 0;
            int invalid = 0, oversized = 0, lowShape = 0, quads = 0, triangles = 0;
            double nominal = dialog.MaxSize;
            double preferred = nominal * (1 + dialog.GmshMaximumSizeTolerancePercent / 100.0);
            for (int source = 0; source < prepared.Count; source++)
            {
                AreaContext context = contexts[source];
                MeshData mesh = prepared[source].Mesh;
                string[] explicitlySelected = context.ExplicitlySelectedPointNames.ToArray();
                var protectedEdges = new HashSet<string>();
                for (int edge = 0; edge < context.EdgePoints.Length; edge++)
                {
                    List<BoundaryPoint> stations = context.EdgePoints[edge].OrderBy(point => point.T).ToList();
                    bool protectedSide = dialog.PreserveSelectedBoundaryEdges &&
                        HasSelectedInteriorEdgePoint(context, edge);
                    for (int i = 1; i < stations.Count; i++)
                        if (protectedSide || IsSelectedBoundaryIntervalForNames(stations[i - 1], stations[i], explicitlySelected))
                            protectedEdges.Add(MeshElementSignature(new[] {
                                stations[i - 1].Coordinates, stations[i].Coordinates }));
                }
                foreach (int[] element in mesh.Elements)
                {
                    double[][] points = element.Select(tag => mesh.Nodes[tag]).ToArray();
                    double[] lengths = new double[points.Length];
                    bool outOfSize = false, inverted = false;
                    double elementMaximumFree = 0;
                    double minAngle = 180, maxAngle = 0, jacobian = 1;
                    for (int i = 0; i < points.Length; i++)
                    {
                        double[] first = points[i], second = points[(i + 1) % points.Length];
                        lengths[i] = Distance(first, second);
                        string signature = MeshElementSignature(new[] { first, second });
                        if (protectedEdges.Contains(signature)) maximumProtected = Math.Max(maximumProtected, lengths[i]);
                        else
                        {
                            maximumFree = Math.Max(maximumFree, lengths[i]);
                            elementMaximumFree = Math.Max(elementMaximumFree, lengths[i]);
                            if (lengths[i] > preferred * 1.001) outOfSize = true;
                        }
                        double[] previous = Subtract(points[(i - 1 + points.Length) % points.Length], first);
                        double[] following = Subtract(second, first);
                        double normProduct = Norm(previous) * Norm(following);
                        if (normProduct <= 1e-16) { inverted = true; continue; }
                        double angle = Math.Acos(Math.Max(-1, Math.Min(1, Dot(previous, following) / normProduct))) * 180 / Math.PI;
                        minAngle = Math.Min(minAngle, angle);
                        maxAngle = Math.Max(maxAngle, angle);
                        jacobian = Math.Min(jacobian, Norm(Cross(previous, following)) / normProduct);
                        if (Dot(Cross(following, previous), context.Plane.Normal) <= Tolerance) inverted = true;
                    }
                    double area = 0;
                    for (int i = 2; i < points.Length; i++)
                        area += Norm(Cross(Subtract(points[i - 1], points[0]),
                            Subtract(points[i], points[0]))) * .5;
                    if (area <= Tolerance * Tolerance || inverted) invalid++;
                    if (area <= Tolerance * Tolerance) continue;
                    bool quad = points.Length == 4;
                    double aspect = lengths.Max() / Math.Max(lengths.Min(), 1e-16);
                    double ideal = quad ? 90 : 60;
                    double skew = Math.Max(Math.Abs(minAngle - ideal), Math.Abs(maxAngle - ideal)) / ideal;
                    double score = Math.Pow(Clamp01(1 - (aspect - 1) / 6) *
                        Clamp01(jacobian) * Clamp01(1 - skew), 1.0 / 3);
                    bool outside = aspect > 4.0001 || (quad ?
                        jacobian < .4999 || minAngle < 44.9999 || maxAngle > 135.0001 :
                        minAngle < 29.9999 || maxAngle > 90.0001);
                    if (outside) { lowShape++; badArea += area; }
                    if (outOfSize)
                    {
                        oversized++;
                        oversizedArea += area;
                        weightedSizeExcess += area * (elementMaximumFree / preferred - 1);
                    }
                    areaSum += area;
                    shapeSum += area * score;
                    weightedEdge += area * lengths.Average();
                    weightedAspect += area * aspect;
                    if (quad)
                    {
                        quads++; quadArea += area;
                        weightedSkew += area * skew;
                        weightedJacobian += area * jacobian;
                        quadMin += area * minAngle; quadMax += area * maxAngle;
                    }
                    else
                    {
                        triangles++; triArea += area;
                        triMin += area * minAngle; triMax += area * maxAngle;
                    }
                }
            }
            if (areaSum <= 0) throw new InvalidDataException("The generated mesh has no positive-area elements.");
            double shapeAverage = shapeSum / areaSum;
            double oversizedFraction = oversizedArea / areaSum;
            double meanSizeExcess = weightedSizeExcess / areaSum;
            bool sizePassed = AcceptGmshSize(nominal, maximumFree, oversizedFraction, meanSizeExcess);
            bool passed = invalid == 0 && sizePassed && shapeAverage >= .5;
            string F(double value) { return value.ToString("0.###", CultureInfo.InvariantCulture); }
            var report = new MeshQualityReport(passed, "Elements: " + quads + " quads, " + triangles +
                " triangles. Outside shape references: " + lowShape + " (" + F(100 * badArea / areaSum) +
                "% of area). Size exceptions: " + oversized + " elements (" +
                F(100 * oversizedFraction) + "% of area). Invalid: " + invalid + ".")
                { OverallScore = shapeAverage };
            report.Metrics.Add(new QualityMetric("Area-weighted mesh quality", F(shapeAverage) + " / 1",
                "Surface weighted over all generated shells; >= 0.5 required", shapeAverage));
            report.Metrics.Add(new QualityMetric("Maximum size", F(maximumFree),
                "Free edge / nominal: " + F(maximumFree / nominal) +
                "; target with tolerance: <= " + F(preferred) +
                "; area exceptions: " + F(100 * oversizedFraction) + "% (<= 10%); protected original edge: " +
                F(maximumProtected) + "; gross free-edge limit: " + F(nominal * 1.5), sizePassed ? 1 : 0));
            report.Metrics.Add(new QualityMetric("Area-weighted size excess", F(100 * meanSizeExcess) + "%",
                "Surface-weighted excess above the tolerated target; <= 2%",
                Clamp01(1 - meanSizeExcess / .02)));
            report.Metrics.Add(new QualityMetric("Average edge length", F(weightedEdge / areaSum),
                "Surface-weighted; nominal " + F(nominal), Clamp01(1 -
                    Math.Max(0, weightedEdge / areaSum - nominal) / nominal)));
            report.Metrics.Add(new QualityMetric("Average aspect ratio", F(weightedAspect / areaSum),
                "Surface-weighted; reference 1 - 4", Clamp01(1 - (weightedAspect / areaSum - 1) / 6)));
            if (quads > 0)
            {
                report.Metrics.Add(new QualityMetric("Average quadrilateral angular skew", F(weightedSkew / quadArea),
                    "Surface-weighted; 0 is ideal", Clamp01(1 - weightedSkew / quadArea)));
                report.Metrics.Add(new QualityMetric("Average scaled Jacobian", F(weightedJacobian / quadArea),
                    "Surface-weighted; 1 is ideal", Clamp01(weightedJacobian / quadArea)));
                report.Metrics.Add(new QualityMetric("Average quadrilateral angles", F(quadMin / quadArea) + " - " +
                    F(quadMax / quadArea) + " degrees", "Area-weighted min/max", Clamp01(1 -
                    Math.Max(Math.Abs(quadMin / quadArea - 90), Math.Abs(quadMax / quadArea - 90)) / 90)));
            }
            if (triangles > 0)
                report.Metrics.Add(new QualityMetric("Average triangle angles", F(triMin / triArea) + " - " +
                    F(triMax / triArea) + " degrees", "Area-weighted min/max", Clamp01(1 -
                    Math.Max(Math.Abs(triMin / triArea - 60), Math.Abs(triMax / triArea - 60)) / 60)));
            report.Metrics.Add(new QualityMetric("Orientation", invalid + " invalid elements",
                "No inverted or degenerate elements", invalid == 0 ? 1 : 0));
            return report;
        }

        private static void ApplyStrictMaximumSize(List<AreaContext> contexts, double maxSize)
        {
            // Add the minimum number of stations between prescribed nodes before
            // writing the selection-wide sector boundaries. All sectors must see
            // the same opening and shared-edge stations when choosing a mesher.
            foreach (AreaContext context in contexts)
                foreach (List<BoundaryPoint> edge in context.EdgePoints)
                {
                    List<BoundaryPoint> stations = edge.OrderBy(point => point.T).ToList();
                    for (int i = 1; i < stations.Count; i++)
                    {
                        int divisions = Math.Max(1, (int)Math.Ceiling(
                            Distance(stations[i - 1].Coordinates, stations[i].Coordinates) / maxSize - 1e-9));
                        for (int j = 1; j < divisions; j++)
                        {
                            double fraction = (double)j / divisions;
                            edge.Add(new BoundaryPoint("QT_SIZE", stations[i - 1].T * (1 - fraction) + stations[i].T * fraction,
                                Interpolate(stations[i - 1].Coordinates, stations[i].Coordinates, fraction)));
                        }
                    }
                    edge.Sort((a, b) => a.T.CompareTo(b.T));
                }
        }

        private static void HarmonizeRegularGridSeams(List<AreaContext> contexts)
        {
            // A Cartesian guide inside one rectangle creates a row or column
            // all the way to its boundary. Seed both sides of each *selected*
            // coplanar seam with every selection-wide guide coordinate before
            // meshing either source shell, not only when its neighbor is next.
            for (int first = 0; first < contexts.Count; first++)
                for (int second = first + 1; second < contexts.Count; second++)
                {
                    AreaContext a = contexts[first], b = contexts[second];
                    if (b.Corners.Any(corner => Math.Abs(a.Plane.SignedDistance(corner)) > Tolerance)) continue;
                    List<double[]> guides = contexts.Where(context => context.Corners.All(corner =>
                            Math.Abs(a.Plane.SignedDistance(corner)) <= Tolerance))
                        .SelectMany(context => context.Corners
                            .Concat(context.InteriorPoints.Select(point => point.Coordinates))
                            .Concat(context.FrameConstraints.SelectMany(frame => new[] { frame.First, frame.Second }))
                            .Concat(context.EdgePoints.SelectMany(edge => edge.Select(point => point.Coordinates)))).ToList();
                    for (int ae = 0; ae < a.Corners.Length; ae++)
                        for (int be = 0; be < b.Corners.Length; be++)
                        {
                            double[] start, end;
                            if (!TryGetCollinearOverlap(a.Corners[ae], a.Corners[(ae + 1) % a.Corners.Length],
                                b.Corners[be], b.Corners[(be + 1) % b.Corners.Length], out start, out end)) continue;
                            double[] axis = Subtract(end, start);
                            double lengthSquared = Dot(axis, axis);
                            foreach (double[] guide in guides)
                            {
                                double t = Dot(Subtract(guide, start), axis) / lengthSquared;
                                if (t <= Tolerance || t >= 1 - Tolerance) continue;
                                double[] station = Interpolate(start, end, t);
                                AddBoundaryCoordinate(a, ae, station);
                                AddBoundaryCoordinate(b, be, station);
                            }
                            a.EdgePoints[ae] = a.EdgePoints[ae].OrderBy(point => point.T).ToList();
                            b.EdgePoints[be] = b.EdgePoints[be].OrderBy(point => point.T).ToList();
                        }
                }
        }

        private static PreparedMesh PrepareMeshAttempt(cSapModel model, string areaName, int cornerCount,
            string[] sourceCornerNames, double[][] sourceCorners, List<BoundaryPoint>[] sourceEdgePoints,
            List<ConstraintPoint> interiorPoints, string[] selectedPoints, string[] explicitlySelectedPoints,
            List<FrameConstraint> frameConstraints,
            List<HoleDefinition> holes, bool forceRefinement, Plane plane, SymmetryAxis symmetryAxis,
            MeshOptionsDialog dialog, List<AreaContext> allContexts, string denseBoundaries
            , string batchMeshPath
            )
        {
            double maxSize = dialog.MaxSize;
            bool symmetric = dialog.Symmetric && symmetryAxis != null;
            dialog.BeginWork("Preparing geometry...");
            string[] cornerNames = sourceCornerNames;
            double[][] corners = sourceCorners;
            List<BoundaryPoint>[] edgePoints = sourceEdgePoints;

            if (symmetric)
            {
                if (symmetryAxis == null)
                    throw new InvalidOperationException("Select a symmetry axis compatible with the area and its constrained points.");
            }

            var regularGuidePoints = new List<double[]>();
            if (dialog.PreferRegularMesh)
            {
                List<AreaContext> coplanar = allContexts.Where(context =>
                    context.Corners.All(point => Math.Abs(plane.SignedDistance(point)) <= Tolerance)).ToList();
                regularGuidePoints.AddRange(coplanar.SelectMany(context => context.Corners));
                regularGuidePoints.AddRange(coplanar.SelectMany(context => context.FrameConstraints)
                    .SelectMany(frame => new[] { frame.First, frame.Second }));
                if (dialog.StrictMaximumSize)
                    regularGuidePoints.AddRange(coplanar.SelectMany(context => context.InteriorPoints)
                        .Select(point => point.Coordinates));
                double[][] projected = regularGuidePoints.Select(plane.Project).ToArray();
                // A synthetic midpoint can become an interior grid row in one
                // shell but be filtered out as an unprescribed boundary point
                // in its perpendicular neighbor, leaving a hanging node.
                // Reserve the explicit center guide for exact symmetry.
                if (dialog.Symmetric && projected.Length > 0)
                    regularGuidePoints.Add(plane.PointFromPlane(
                        (projected.Min(point => point[0]) + projected.Max(point => point[0])) * 0.5,
                        (projected.Min(point => point[1]) + projected.Max(point => point[1])) * 0.5));
            }

            string property = "";
            double localAngle = 0, mass = 0;
            bool localAdvanced = false, edgeConstraint = false;
            Check(model.AreaObj.GetProperty(areaName, ref property), "reading the area property", "AreaObj.GetProperty");
            Check(model.AreaObj.GetLocalAxes(areaName, ref localAngle, ref localAdvanced), "reading area local axes", "AreaObj.GetLocalAxes");
            Check(model.AreaObj.GetMass(areaName, ref mass), "reading area mass", "AreaObj.GetMass");
            Check(model.AreaObj.GetEdgeConstraint(areaName, ref edgeConstraint), "reading area edge constraint", "AreaObj.GetEdgeConstraint");
            if (localAdvanced) throw new InvalidOperationException("Advanced local axes are not supported.");
            AreaLoads areaLoads = CaptureAreaLoads(model, areaName, allContexts);
            PointSnapshot[] originalPoints = CapturePointSnapshots(model,
                sourceCornerNames.Concat(selectedPoints).Concat(holes.Select(hole => hole.PointName)));

            string token = Guid.NewGuid().ToString("N");
            string boundaryFile = Path.Combine(Path.GetTempPath(), "sap_mesh_boundary_" + token + ".csv");
            string meshFile =
                batchMeshPath ??
                Path.Combine(Path.GetTempPath(), "sap_mesh_output_" + token + ".csv");
            try
            {
                WriteBoundary(boundaryFile, areaName, property, cornerNames, corners, edgePoints, interiorPoints,
                    frameConstraints, holes, symmetric ? symmetryAxis : null, forceRefinement, dialog.StressRefinement,
                    dialog.PreferRegularMesh, regularGuidePoints, explicitlySelectedPoints, allContexts
                    , dialog.PreserveSelectedBoundaryEdges
                    );
                string boundaryTrace = Environment.GetEnvironmentVariable("SAP_QUAD_BOUNDARY_TRACE");
                if (!string.IsNullOrEmpty(boundaryTrace)) File.Copy(boundaryFile, boundaryTrace, true);
                string boundaryTraceDirectory = Environment.GetEnvironmentVariable("SAP_QUAD_BOUNDARY_TRACE_DIR");
                if (!string.IsNullOrEmpty(boundaryTraceDirectory))
                    File.Copy(boundaryFile, Path.Combine(boundaryTraceDirectory, "area-" + areaName + "-boundary.csv"), true);
                string scriptName = "sap_gmsh_mesher.py";
                dialog.SetIndeterminate(dialog.GmshOptimizeEnabled
                    ? "Generating and optimizing the mesh..." : "Generating the mesh...");
                // Reproduce an explicitly chosen local trial against this exact
                // selection, through the normal SAP insertion and seam checks.
                string replayDirectory = Environment.GetEnvironmentVariable("SAP_QUAD_REPLAY_MESH_DIR");
                if (!string.IsNullOrEmpty(replayDirectory) &&
                    Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SIZE") != null && dialog.StrictMaximumSize)
                {
                    string replay = Path.Combine(replayDirectory, "sector-" + areaName + "-trial.csv");
                    File.Copy(replay, meshFile, true);
                }
                else
                    if (batchMeshPath == null)
                    RunMesher(scriptName, boundaryFile, meshFile, maxSize, dialog, denseBoundaries);
                string meshTraceDirectory = Environment.GetEnvironmentVariable("SAP_QUAD_MESH_TRACE_DIR");
                if (!string.IsNullOrEmpty(meshTraceDirectory))
                    File.Copy(meshFile, Path.Combine(meshTraceDirectory, "area-" + areaName + "-mesh.csv"), true);
                MeshData mesh = ReadMesh(meshFile, dialog);
                if (symmetric) ValidateMeshSymmetry(mesh, symmetryAxis);
                if (dialog.GenerateHoles || holes.Count > 0) RemoveEnclosedElements(mesh, areaName, allContexts);
                dialog.ThrowIfCancellationRequested();
                double lockedLimit = Enumerable.Range(0, edgePoints.Length).SelectMany(edge =>
                    edgePoints[edge].Zip(edgePoints[edge].Skip(1), (first, second) =>
                        IsSelectedBoundaryIntervalForNames(first, second, explicitlySelectedPoints)
                            ? Distance(first.Coordinates, second.Coordinates) : 0)).DefaultIfEmpty().Max();
                MeshQualityReport quality = EvaluateMeshQuality(mesh, plane,
                    dialog.StrictMaximumSize ? maxSize : Math.Max(Math.Max(AllowedMaxSize(maxSize, edgePoints), lockedLimit),
                        maxSize * (1 + dialog.EdgeTolerancePercent / 100.0)), frameConstraints.Count);
                dialog.ThrowIfCancellationRequested();
                if (batchMeshPath == null)
                    PropagateGeneratedIntersectionEdges(areaName, plane, mesh, allContexts, dialog.PreferRegularMesh);
                return new PreparedMesh(areaName, property, localAngle, mass, edgeConstraint, areaLoads, originalPoints, mesh, quality);
            }
            catch
            {
                string failedBoundary = Environment.GetEnvironmentVariable("SAP_QUAD_FAILED_BOUNDARY");
                if (!string.IsNullOrEmpty(failedBoundary))
                    try { File.Copy(boundaryFile, failedBoundary, true); } catch { }
                throw;
            }
            finally
            {
                TryDelete(boundaryFile);
                if (batchMeshPath == null)
                    TryDelete(meshFile);
            }
        }

        private static void PropagateGeneratedIntersectionEdges(string sourceArea, Plane sourcePlane,
            MeshData mesh, List<AreaContext> contexts, bool preferRegular)
        {
            int sourceIndex = contexts.FindIndex(context => context.Name == sourceArea);
            if (sourceIndex < 0 || sourceIndex == contexts.Count - 1) return;
            var meshEdges = mesh.Elements.SelectMany(element => Enumerable.Range(0, element.Length)
                .Select(index => new[] { mesh.Nodes[element[index]], mesh.Nodes[element[(index + 1) % element.Length]] })).ToList();
            foreach (AreaContext target in contexts.Skip(sourceIndex + 1))
                for (int edge = 0; edge < target.Corners.Length; edge++)
                {
                    double[] start = target.Corners[edge], end = target.Corners[(edge + 1) % target.Corners.Length];
                    if (Math.Abs(sourcePlane.SignedDistance(start)) > Tolerance ||
                        Math.Abs(sourcePlane.SignedDistance(end)) > Tolerance) continue;
                    // Gmsh's global quad refinement bisects every transfinite boundary segment.
                    // When the source already has exactly those midpoints, forwarding them as
                    // additional INPUT vertices would bisect them a second time on the next area.
                    bool regularTarget = preferRegular && target.Holes.Count == 0 &&
                        target.Corners.Length == 4 && IsRegularRectangularTarget(target);
                    if (target.FrameConstraints.Count > 0 &&
                        MatchesPrescribedBoundary(meshEdges, target.EdgePoints[edge], start, end))
                    {
                        // The source already kept these stations intact. The
                        // next framed polygon must not subdivide them again.
                        target.PreserveFrameBoundary = true;
                        continue;
                    }
                    if (!regularTarget && (target.ForceRefinement || target.FrameConstraints.Count > 0) &&
                        (target.Holes.Count == 0 || target.FrameConstraints.Count > 0) &&
                        MatchesRefinedBoundary(meshEdges, target.EdgePoints[edge], start, end)) continue;
                    int originalStations = target.EdgePoints[edge].Count;
                    foreach (double[][] segment in meshEdges)
                    {
                        double firstT, secondT;
                        if (!OnSegment(segment[0], start, end, out firstT) ||
                            !OnSegment(segment[1], start, end, out secondT) ||
                            Math.Abs(firstT - secondT) <= Tolerance) continue;
                        AddBoundaryCoordinate(target, edge, segment[0]);
                        AddBoundaryCoordinate(target, edge, segment[1]);
                    }
                    if (target.EdgePoints[edge].Count > originalStations)
                        target.PreserveFrameBoundary = true;
                    target.EdgePoints[edge] = target.EdgePoints[edge].OrderBy(point => point.T).ToList();
                }
        }

        private static bool MatchesPrescribedBoundary(List<double[][]> meshEdges, List<BoundaryPoint> boundary,
            double[] start, double[] end)
        {
            var generated = new HashSet<string>();
            foreach (double[][] segment in meshEdges)
            {
                double a, b;
                if (OnSegment(segment[0], start, end, out a) && OnSegment(segment[1], start, end, out b) &&
                    Math.Abs(a - b) > Tolerance)
                    generated.Add(MeshElementSignature(new[] { segment[0], segment[1] }));
            }
            if (generated.Count == 0) return false;
            var prescribed = new HashSet<string>();
            List<BoundaryPoint> stations = boundary.OrderBy(point => point.T).ToList();
            for (int i = 1; i < stations.Count; i++)
            {
                double a, b;
                if (OnSegment(stations[i - 1].Coordinates, start, end, out a) &&
                    OnSegment(stations[i].Coordinates, start, end, out b) && Math.Abs(a - b) > Tolerance)
                    prescribed.Add(MeshElementSignature(new[] { stations[i - 1].Coordinates, stations[i].Coordinates }));
            }
            return prescribed.Count > 0 && generated.SetEquals(prescribed);
        }

        private static bool MatchesRefinedBoundary(List<double[][]> meshEdges, List<BoundaryPoint> boundary,
            double[] start, double[] end)
        {
            var source = new List<double[]>();
            var covered = new List<double[][]>();
            foreach (double[][] segment in meshEdges)
            {
                double first, second;
                if (!OnSegment(segment[0], start, end, out first) ||
                    !OnSegment(segment[1], start, end, out second) ||
                    Math.Abs(first - second) <= Tolerance) continue;
                covered.Add(segment);
                foreach (double[] point in segment)
                    if (!source.Any(existing => Distance(existing, point) <= Tolerance)) source.Add(point);
            }
            if (source.Count < 2) return false;
            List<BoundaryPoint> points = boundary.OrderBy(point => point.T).ToList();
            var expected = points.Select(point => point.Coordinates).ToList();
            for (int i = 1; i < points.Count; i++)
                expected.Add(Interpolate(points[i - 1].Coordinates, points[i].Coordinates, 0.5));
            // Openings may remove only part of the source edge: match the surviving
            // intervals, rather than insisting that the entire target edge is meshed.
            return source.All(point => expected.Any(other => Distance(point, other) <= Tolerance)) &&
                expected.Where(point => covered.Any(segment =>
                {
                    double ignored;
                    return OnSegment(point, segment[0], segment[1], out ignored);
                })).All(point => source.Any(other => Distance(point, other) <= Tolerance));
        }

        private static bool IsRegularRectangularTarget(AreaContext target)
        {
            double[][] corners = target.Corners;
            try { EnsureRectangularSymmetry(corners); }
            catch (InvalidOperationException) { return false; }
            double[] u = Normalize(Subtract(corners[1], corners[0]));
            double[] v = Normalize(Subtract(corners[3], corners[0]));
            foreach (FrameConstraint frame in target.FrameConstraints)
            {
                double[] direction = Subtract(frame.Second, frame.First);
                double tolerance = Tolerance * Math.Max(Norm(direction), 1);
                if (Math.Min(Math.Abs(Dot(direction, u)), Math.Abs(Dot(direction, v))) > tolerance)
                    return false;
            }
            return true;
        }

        private static void ValidateAutomaticIntersectionEdges(List<AreaContext> contexts, List<PreparedMesh> prepared)
        {
            var byName = prepared.ToDictionary(item => item.SourceArea);
            foreach (AreaContext host in contexts)
                foreach (FrameConstraint frame in host.FrameConstraints.Where(item => item.Automatic && item.SourceContext != null))
                {
                    MeshData hostMesh = byName[host.Name].Mesh, sourceMesh = byName[frame.SourceContext.Name].Mesh;
                    HashSet<string> hostEdges = EdgesOnSegment(hostMesh, frame.First, frame.Second);
                    HashSet<string> sourceEdges = EdgesOnSegment(sourceMesh, frame.First, frame.Second);
                    // Opening boundaries can deliberately remove the mesh on one side.
                    if (hostEdges.Count > 0 && sourceEdges.Count > 0 && !hostEdges.SetEquals(sourceEdges))
                        throw new InvalidDataException("Non-conforming intersection between areas " +
                            host.Name + " and " + frame.SourceContext.Name + " along " + frame.Name +
                            ": mesh import was cancelled before modifying SAP2000.");
                }
        }

        private static void ValidateSharedAreaEdges(List<AreaContext> contexts, List<PreparedMesh> prepared)
        {
            var byName = prepared.ToDictionary(item => item.SourceArea);
            for (int first = 0; first < contexts.Count; first++)
                for (int second = first + 1; second < contexts.Count; second++)
                    for (int firstEdge = 0; firstEdge < contexts[first].Corners.Length; firstEdge++)
                        for (int secondEdge = 0; secondEdge < contexts[second].Corners.Length; secondEdge++)
                        {
                            AreaContext a = contexts[first], b = contexts[second];
                            double[] start, end;
                            if (!TryGetCollinearOverlap(a.Corners[firstEdge], a.Corners[(firstEdge + 1) % a.Corners.Length],
                                b.Corners[secondEdge], b.Corners[(secondEdge + 1) % b.Corners.Length], out start, out end)) continue;
                            HashSet<string> aEdges = EdgesOnSegment(byName[a.Name].Mesh, start, end);
                            HashSet<string> bEdges = EdgesOnSegment(byName[b.Name].Mesh, start, end);
                            if (aEdges.Count > 0 && bEdges.Count > 0 && !aEdges.SetEquals(bEdges))
                                throw new InvalidDataException("Non-conforming shared edge between areas " + a.Name +
                                    " and " + b.Name + ": mesh import was cancelled before modifying SAP2000.");
                        }
        }

        private static HashSet<string> EdgesOnSegment(MeshData mesh, double[] start, double[] end)
        {
            var matches = new HashSet<string>();
            foreach (int[] element in mesh.Elements)
                for (int i = 0; i < element.Length; i++)
                {
                    double[] first = mesh.Nodes[element[i]], second = mesh.Nodes[element[(i + 1) % element.Length]];
                    double firstT, secondT;
                    if (OnSegment(first, start, end, out firstT) && OnSegment(second, start, end, out secondT) &&
                        Math.Abs(firstT - secondT) > Tolerance)
                        matches.Add(MeshElementSignature(new[] { first, second }));
                }
            return matches;
        }

        private static bool TryGetTransitionPair(List<BoundaryPoint>[] edges, out int pairStart)
        {
            bool constrained02 = edges[0].Count > 2 && edges[2].Count > 2;
            bool constrained13 = edges[1].Count > 2 && edges[3].Count > 2;
            pairStart = constrained02 ? 0 : 1;
            // The specialized transition maps its two remaining sides using an
            // independently chosen row count. If either side already contains
            // prescribed shared stations, that mapping can discard them and
            // produce hanging joints on the neighboring selected shell.
            return constrained02 != constrained13 &&
                edges[(pairStart + 1) % 4].Count == 2 && edges[(pairStart + 3) % 4].Count == 2;
        }

        private static void ValidateMeshSymmetry(MeshData mesh, SymmetryAxis symmetryAxis)
        {
            var signatures = new HashSet<string>(mesh.Elements.Select(element => MeshElementSignature(mesh, element)));
            foreach (int[] element in mesh.Elements)
            {
                string reflected = MeshElementSignature(element.Select(tag =>
                    ReflectPoint3D(mesh.Nodes[tag], symmetryAxis.Center, symmetryAxis.Direction)));
                if (!signatures.Contains(reflected))
                    throw new InvalidDataException("The generated transition mesh is not exactly symmetric about the selected axis.");
                if (symmetryAxis.SecondaryDirection == null) continue;
                string secondary = MeshElementSignature(element.Select(tag =>
                    ReflectPoint3D(mesh.Nodes[tag], symmetryAxis.Center, symmetryAxis.SecondaryDirection)));
                if (!signatures.Contains(secondary))
                    throw new InvalidDataException("The generated transition mesh is not exactly symmetric about the secondary axis.");
            }
        }

        private static string MeshElementSignature(MeshData mesh, int[] element)
        {
            return MeshElementSignature(element.Select(tag => mesh.Nodes[tag]));
        }

        private static string MeshElementSignature(IEnumerable<double[]> points)
        {
            return string.Join("|", points.Select(point => string.Join(":", point.Select(value =>
                Math.Round(value, 8).ToString("R", CultureInfo.InvariantCulture)))).OrderBy(value => value));
        }

        private static double[] ReflectPoint3D(double[] point, double[] center, double[] direction)
        {
            double[] offset = Subtract(point, center);
            double projection = Dot(offset, direction);
            return Enumerable.Range(0, 3).Select(axis => center[axis] + 2 * projection * direction[axis] - offset[axis]).ToArray();
        }

        private static void ValidateSymmetricEdges(List<BoundaryPoint> first, List<BoundaryPoint> opposite)
        {
            foreach (List<BoundaryPoint> edge in new[] { first, opposite })
            {
                int segments = edge.Count - 1;
                if (segments < 2 || segments % 2 != 0)
                    throw new InvalidOperationException("In symmetric mode, each transition edge must have an even number of intervals.");
                for (int i = 0; i < edge.Count; i++)
                    if (Math.Abs(edge[i].T + edge[edge.Count - 1 - i].T - 1) > 1e-5)
                        throw new InvalidOperationException("The edge distribution is not symmetric about the midpoint.");
            }
        }

        private static void WriteBoundary(string path, string area, string property, string[] names, double[][] corners,
            List<BoundaryPoint>[] edges, List<ConstraintPoint> interior, List<FrameConstraint> frames,
            List<HoleDefinition> holes, SymmetryAxis symmetryAxis, bool forceRefinement, bool stressRefinement,
            bool preferRegular, List<double[]> regularGuidePoints, string[] selectedPoints,
            List<AreaContext> selectedContexts, bool preserveSelectedEdges = false)
        {
            using (var writer = new StreamWriter(path, false, Encoding.ASCII))
            {
                writer.WriteLine("AREA," + area + "," + property);
                if (forceRefinement) writer.WriteLine("REFINE,1");
                if (stressRefinement) writer.WriteLine("STRESS_REFINE,1");
                if (preferRegular) writer.WriteLine("PREFER_REGULAR,1");
                if (selectedContexts.Any(context => context.Name == area && context.PreserveFrameBoundary))
                    writer.WriteLine("PRESERVE_BOUNDARY,1");
                foreach (double[] point in regularGuidePoints)
                    WritePointRow(writer, "REGULAR_POINT", 0, "", 0, point);
                if (symmetryAxis != null)
                {
                    writer.WriteLine(string.Join(",", "SYMMETRY",
                        symmetryAxis.Center[0].ToString("R", CultureInfo.InvariantCulture),
                        symmetryAxis.Center[1].ToString("R", CultureInfo.InvariantCulture),
                        symmetryAxis.Center[2].ToString("R", CultureInfo.InvariantCulture),
                        symmetryAxis.Direction[0].ToString("R", CultureInfo.InvariantCulture),
                        symmetryAxis.Direction[1].ToString("R", CultureInfo.InvariantCulture),
                        symmetryAxis.Direction[2].ToString("R", CultureInfo.InvariantCulture)));
                    if (symmetryAxis.SecondaryDirection != null)
                        writer.WriteLine(string.Join(",", "SYMMETRY2",
                            symmetryAxis.Center[0].ToString("R", CultureInfo.InvariantCulture),
                            symmetryAxis.Center[1].ToString("R", CultureInfo.InvariantCulture),
                            symmetryAxis.Center[2].ToString("R", CultureInfo.InvariantCulture),
                            symmetryAxis.SecondaryDirection[0].ToString("R", CultureInfo.InvariantCulture),
                            symmetryAxis.SecondaryDirection[1].ToString("R", CultureInfo.InvariantCulture),
                            symmetryAxis.SecondaryDirection[2].ToString("R", CultureInfo.InvariantCulture)));
                }
                for (int i = 0; i < corners.Length; i++) WritePointRow(writer, "CORNER", i + 1, names[i], 0, corners[i]);
                for (int edge = 0; edge < edges.Length; edge++)
                {
                    bool shared = selectedContexts.Any(other => other.Name != area &&
                        Enumerable.Range(0, other.Corners.Length).Any(otherEdge =>
                        {
                            double[] start, end;
                            return TryGetCollinearOverlap(corners[edge], corners[(edge + 1) % corners.Length],
                                other.Corners[otherEdge], other.Corners[(otherEdge + 1) % other.Corners.Length],
                                out start, out end);
                        }));
                    if (!shared && edges[edge].Count == 2 && edges[edge].All(point => selectedPoints.Contains(point.Name)))
                        writer.WriteLine("LOCK_EDGE," + (edge + 1).ToString(CultureInfo.InvariantCulture));
                    foreach (BoundaryPoint point in edges[edge]) WritePointRow(writer, "EDGE", edge + 1, point.Name, point.T, point.Coordinates);
                    if (preserveSelectedEdges && edges[edge].Any(point => point.T > Tolerance &&
                        point.T < 1 - Tolerance && selectedPoints.Contains(point.Name)))
                        writer.WriteLine("PRESERVE_SELECTED_EDGE," + (edge + 1).ToString(CultureInfo.InvariantCulture));
                }
                foreach (ConstraintPoint point in interior) WritePointRow(writer, "INTERIOR", 0, point.Name, 0, point.Coordinates);
                foreach (string selected in selectedPoints.Distinct())
                {
                    int corner = Array.IndexOf(names, selected);
                    BoundaryPoint edge = corner < 0 ? edges.SelectMany(points => points)
                        .FirstOrDefault(point => point.Name == selected) : null;
                    ConstraintPoint inside = corner < 0 && edge == null ?
                        interior.FirstOrDefault(point => point.Name == selected) : null;
                    double[] coordinate = corner >= 0 ? corners[corner] : edge != null ? edge.Coordinates :
                        inside == null ? null : inside.Coordinates;
                    if (coordinate == null)
                        throw new InvalidDataException("Selected " + HostProductName() + " joint is missing from its area context: " + selected);
                    WritePointRow(writer, "EXPLICIT_POINT", 0, selected, 0, coordinate);
                }
                foreach (HoleDefinition hole in holes)
                    WritePointRow(writer, "HOLE", hole.Id, "", hole.Diameter, hole.Center);
                for (int i = 0; i < frames.Count; i++)
                {
                    FrameConstraint frame = frames[i];
                    writer.WriteLine(string.Join(",", "FRAME", (i + 1).ToString(CultureInfo.InvariantCulture), frame.Name, "0",
                        frame.First[0].ToString("R", CultureInfo.InvariantCulture), frame.First[1].ToString("R", CultureInfo.InvariantCulture), frame.First[2].ToString("R", CultureInfo.InvariantCulture),
                        frame.Second[0].ToString("R", CultureInfo.InvariantCulture), frame.Second[1].ToString("R", CultureInfo.InvariantCulture), frame.Second[2].ToString("R", CultureInfo.InvariantCulture),
                        frame.Automatic ? "1" : "0", frame.CanBoundOpening ? "1" : "0"));
                }
            }
        }

        private static bool IsSelectedBoundaryIntervalForNames(BoundaryPoint first, BoundaryPoint second, string[] names)
        {
            return names.Contains(first.Name) && names.Contains(second.Name);
        }

        private static void WritePointRow(StreamWriter writer, string kind, int edge, string name, double t, double[] p)
        {
            writer.WriteLine(string.Join(",", kind, edge.ToString(CultureInfo.InvariantCulture), name,
                t.ToString("R", CultureInfo.InvariantCulture), p[0].ToString("R", CultureInfo.InvariantCulture),
                p[1].ToString("R", CultureInfo.InvariantCulture), p[2].ToString("R", CultureInfo.InvariantCulture)));
        }

        private static string RunMesher(string scriptName, string boundary, string mesh, double maxSize, MeshOptionsDialog dialog,
            string denseBoundaries)
        {
            dialog.ThrowIfCancellationRequested();
            string pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            string script = Path.Combine(pluginDirectory, scriptName);
            string backend = Path.Combine(pluginDirectory, "mesher", "SapQuadMesherBackend.exe");
            if (!File.Exists(script)) throw new FileNotFoundException("Mesh script not found.", script);
            if (!File.Exists(backend)) throw new FileNotFoundException("The Python/Gmsh runtime bundled with the plugin was not found.", backend);
            string settingsPath = Path.Combine(Path.GetTempPath(), "sap-gmsh-options-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(settingsPath, dialog.GmshSettingsJson, Encoding.UTF8);
            string settingsTrace = Environment.GetEnvironmentVariable("SAP_GMSH_V2_SETTINGS_TRACE_DIR");
            if (!string.IsNullOrEmpty(settingsTrace) && Directory.Exists(settingsTrace))
            {
                File.Copy(settingsPath, Path.Combine(settingsTrace, Path.GetFileNameWithoutExtension(boundary) + "-settings.json"));
                File.WriteAllText(Path.Combine(settingsTrace, Path.GetFileNameWithoutExtension(boundary) + "-invocation.json"),
                    new JavaScriptSerializer().Serialize(new { script = scriptName, maxSize = maxSize,
                        settings = Path.GetFileName(settingsPath) }), Encoding.UTF8);
            }
            var start = new ProcessStartInfo(backend, Quote(script) + " " + Quote(boundary) + " " + Quote(mesh) + " " +
                maxSize.ToString("R", CultureInfo.InvariantCulture)
                + " " + Quote(settingsPath)
                )
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = pluginDirectory
            };
            start.EnvironmentVariables.Remove("PYTHONHOME");
            start.EnvironmentVariables.Remove("PYTHONPATH");
            start.EnvironmentVariables["PYTHONNOUSERSITE"] = "1";
            start.EnvironmentVariables["PYTHONDONTWRITEBYTECODE"] = "1";
            start.EnvironmentVariables["SAP_QUAD_PARALLEL_CANDIDATES"] = dialog.CandidateWorkers > 1 ? "1" : "0";
            start.EnvironmentVariables["SAP_QUAD_CANDIDATE_WORKERS"] = dialog.CandidateWorkers.ToString(CultureInfo.InvariantCulture);
            start.EnvironmentVariables["SAP_QUAD_EDGE_TOLERANCE_PERCENT"] =
                dialog.EdgeTolerancePercent.ToString(CultureInfo.InvariantCulture);
            start.EnvironmentVariables["SAP_QUAD_SECTOR_BLOCKS"] = dialog.StructuredSectorBands ? "1" : "0";
            if (denseBoundaries != null) start.EnvironmentVariables["SAP_QUAD_DENSE_BOUNDARIES"] = denseBoundaries;
            else start.EnvironmentVariables.Remove("SAP_QUAD_DENSE_BOUNDARIES");
            var backendClock = Stopwatch.StartNew();
            try
            {
            using (Process process = Process.Start(start))
            {
                string profileDirectory = Environment.GetEnvironmentVariable("SAP_QUAD_PROFILE_DIR");
                if (!string.IsNullOrEmpty(profileDirectory))
                    try { File.AppendAllText(Path.Combine(profileDirectory, "backend-start.csv"),
                        process.Id.ToString(CultureInfo.InvariantCulture) + Environment.NewLine); } catch { }
                try
                {
                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    while (!process.WaitForExit(100))
                    {
                        Application.DoEvents();
                        dialog.ThrowIfCancellationRequested();
                    }
                    dialog.ThrowIfCancellationRequested();
                    string output = outputTask.GetAwaiter().GetResult();
                    string error = errorTask.GetAwaiter().GetResult();
                    if (process.ExitCode != 0)
                    {
                        const string qualityMarker = "SAP_GMSH_NEAR_COLLINEAR:";
                        int marker = error.IndexOf(qualityMarker, StringComparison.Ordinal);
                        if (marker >= 0)
                        {
                            string detail = error.Substring(marker + qualityMarker.Length)
                                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                                .FirstOrDefault() ?? "";
                            string algorithm = dialog.SelectedGmshAlgorithm == 7 ? "BAMG (7)" :
                                dialog.SelectedGmshAlgorithm.ToString(CultureInfo.InvariantCulture);
                            throw new InvalidOperationException(L(
                                "The selected Gmsh algorithm " + algorithm +
                                " generated a nearly collinear triangle. No mesh was imported into SAP2000." +
                                " Choose another Mesh Algorithm, for example 8 — Frontal-Delaunay for Quads," +
                                " and click Generate again.\nDiagnostic: ",
                                "L'algoritmo Gmsh selezionato " + algorithm +
                                " ha generato un triangolo con nodi quasi allineati. Nessuna mesh e stata importata in SAP2000." +
                                " Selezionare un altro algoritmo, per esempio 8 — Frontal-Delaunay for Quads," +
                                " e premere di nuovo Genera.\nDettaglio: ") + detail);
                        }
                        throw new InvalidOperationException("Mesh generator: " +
                            (string.IsNullOrWhiteSpace(error) ? "Gmsh stopped unexpectedly (exit code " +
                                process.ExitCode.ToString(CultureInfo.InvariantCulture) + ")." : error.Trim()));
                    }
                    return output.Trim();
                }
                catch
                {
                    if (!process.HasExited)
                    {
                        // A parallel mesher owns candidate subprocesses. Kill the entire
                        // tree on cancellation so no Gmsh worker outlives the plugin.
                        try
                        {
                            using (Process stop = Process.Start(new ProcessStartInfo("taskkill.exe",
                                "/PID " + process.Id.ToString(CultureInfo.InvariantCulture) + " /T /F")
                            { UseShellExecute = false, CreateNoWindow = true }))
                                if (stop != null) stop.WaitForExit(5000);
                            if (!process.HasExited) process.Kill();
                        }
                        catch (InvalidOperationException) { if (!process.HasExited) throw; }
                        if (!process.WaitForExit(5000))
                            throw new InvalidOperationException("The mesh backend did not stop; the model was left unchanged.");
                    }
                    throw;
                }
                finally
                {
                    if (!string.IsNullOrEmpty(profileDirectory))
                        try
                        {
                            string timingPath = Path.Combine(profileDirectory, "csharp-backends.csv");
                            if (!File.Exists(timingPath)) File.WriteAllText(timingPath, "area,pid,seconds,exit_code" + Environment.NewLine);
                            File.AppendAllText(timingPath, string.Join(",", File.ReadLines(boundary).First().Split(',')[1],
                                process.Id.ToString(CultureInfo.InvariantCulture),
                                backendClock.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture),
                                process.HasExited ? process.ExitCode.ToString(CultureInfo.InvariantCulture) : "running") + Environment.NewLine);
                        }
                        catch { }
                }
            }
            }
            finally
            {
                TryDelete(settingsPath);
            }
        }

        private static MeshData ReadMesh(string path, MeshOptionsDialog dialog)
        {
            var data = new MeshData();
            var responsiveness = Stopwatch.StartNew();
            int rows = 0;
            foreach (string line in File.ReadLines(path))
            {
                if ((++rows & 4095) == 0 && responsiveness.ElapsedMilliseconds >= 150)
                {
                    Application.DoEvents();
                    dialog.ThrowIfCancellationRequested();
                    responsiveness.Restart();
                }
                string[] fields = line.Split(',');
                if (fields[0] == "N")
                    data.Nodes[int.Parse(fields[1], CultureInfo.InvariantCulture)] = new[]
                    {
                        double.Parse(fields[2], CultureInfo.InvariantCulture), double.Parse(fields[3], CultureInfo.InvariantCulture),
                        double.Parse(fields[4], CultureInfo.InvariantCulture)
                    };
                else if (fields[0] == "E")
                    data.Elements.Add(fields.Skip(1).Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray());
                else if (fields[0] == "Q")
                    data.Elements.Add(fields.Skip(1).Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray());
                else if (fields[0] == "F")
                    data.ProtectedEdges.Add(EdgeKey(int.Parse(fields[1], CultureInfo.InvariantCulture),
                        int.Parse(fields[2], CultureInfo.InvariantCulture)));
            }
            dialog.ThrowIfCancellationRequested();
            if (data.Nodes.Count == 0 || data.Elements.Count == 0) throw new InvalidDataException("The generator returned an empty mesh.");
            if (data.Elements.Any(element => element.Length < 3 || element.Length > 4)) throw new InvalidDataException("Unsupported connectivity.");
            return data;
        }

        private static string EdgeKey(int first, int second)
        {
            return first < second ? first + ":" + second : second + ":" + first;
        }

        private static void RemoveEnclosedElements(MeshData mesh, string areaName, List<AreaContext> contexts)
        {
            var owners = new Dictionary<string, List<int>>();
            for (int elementIndex = 0; elementIndex < mesh.Elements.Count; elementIndex++)
            {
                int[] element = mesh.Elements[elementIndex];
                for (int i = 0; i < element.Length; i++)
                {
                    string edge = EdgeKey(element[i], element[(i + 1) % element.Length]);
                    List<int> edgeOwners;
                    if (!owners.TryGetValue(edge, out edgeOwners)) owners[edge] = edgeOwners = new List<int>();
                    edgeOwners.Add(elementIndex);
                }
            }
            if (owners.Values.Any(items => items.Count > 2))
                throw new InvalidDataException("Invalid mesh topology: an edge belongs to more than two shell elements.");

            var exterior = new HashSet<int>();
            var pending = new Queue<int>();
            foreach (KeyValuePair<string, List<int>> item in owners.Where(item => item.Value.Count == 1))
            {
                if (mesh.ProtectedEdges.Contains(item.Key) || IsSharedUnionBoundary(mesh, item.Key, areaName, contexts)) continue;
                if (exterior.Add(item.Value[0])) pending.Enqueue(item.Value[0]);
            }
            var elementEdges = new List<string>[mesh.Elements.Count];
            for (int i = 0; i < elementEdges.Length; i++) elementEdges[i] = new List<string>();
            foreach (KeyValuePair<string, List<int>> item in owners)
                foreach (int elementIndex in item.Value) elementEdges[elementIndex].Add(item.Key);

            while (pending.Count > 0)
            {
                int current = pending.Dequeue();
                foreach (string edge in elementEdges[current])
                {
                    List<int> edgeOwners = owners[edge];
                    if (mesh.ProtectedEdges.Contains(edge) || edgeOwners.Count != 2) continue;
                    int neighbor = edgeOwners[0] == current ? edgeOwners[1] : edgeOwners[0];
                    if (exterior.Add(neighbor)) pending.Enqueue(neighbor);
                }
            }

            mesh.RemovedElements = mesh.Elements.Count - exterior.Count;
            if (mesh.RemovedElements == 0) return;
            List<int[]> retained = mesh.Elements.Where((element, index) => exterior.Contains(index)).ToList();
            if (retained.Count == 0) throw new InvalidDataException("The closed frames would remove the entire area.");
            mesh.Elements.Clear();
            mesh.Elements.AddRange(retained);
            var usedNodes = new HashSet<int>(mesh.Elements.SelectMany(element => element));
            foreach (int tag in mesh.Nodes.Keys.Where(tag => !usedNodes.Contains(tag)).ToArray()) mesh.Nodes.Remove(tag);
        }

        private static bool IsSharedUnionBoundary(MeshData mesh, string edge, string areaName, List<AreaContext> contexts)
        {
            string[] tags = edge.Split(':');
            double[] first = mesh.Nodes[int.Parse(tags[0], CultureInfo.InvariantCulture)];
            double[] second = mesh.Nodes[int.Parse(tags[1], CultureInfo.InvariantCulture)];
            double[] midpoint = Interpolate(first, second, 0.5);
            AreaContext current = contexts.First(context => context.Name == areaName);
            foreach (AreaContext context in contexts)
            {
                if (context == current || Math.Abs(Dot(current.Plane.Normal, context.Plane.Normal)) < 1 - 1e-8 ||
                    Math.Abs(context.Plane.SignedDistance(midpoint)) > Tolerance) continue;
                if (PointInsideOrBoundary(context.Plane.Project(midpoint), context.Corners.Select(context.Plane.Project).ToArray()))
                    return true;
            }
            return false;
        }

        private static double AllowedMaxSize(double maxSize, List<BoundaryPoint>[] edges)
        {
            double allowedMax = maxSize;
            foreach (List<BoundaryPoint> edge in edges.Where(item => item.Count > 2))
                for (int i = 1; i < edge.Count; i++)
                    allowedMax = Math.Max(allowedMax, Distance(edge[i - 1].Coordinates, edge[i].Coordinates));
            return allowedMax;
        }

        private static MeshQualityReport EvaluateMeshQuality(MeshData mesh, Plane plane, double allowedMax, int frameCount)
        {
            double minEdge = double.MaxValue, maxEdge = 0, maxAspect = 0;
            double minAngle = 180, maxAngle = 0, maxQuadSkew = 0, minQuadJacobian = 1;
            double minTriangleAngle = 180, maxTriangleAngle = 0;
            int triangles = 0, quads = 0, invalid = 0;
            foreach (int[] element in mesh.Elements)
            {
                double[][] points = element.Select(tag => mesh.Nodes[tag]).ToArray();
                double[] lengths = Enumerable.Range(0, points.Length)
                    .Select(i => Distance(points[i], points[(i + 1) % points.Length])).ToArray();
                minEdge = Math.Min(minEdge, lengths.Min());
                maxEdge = Math.Max(maxEdge, lengths.Max());
                maxAspect = Math.Max(maxAspect, lengths.Max() / lengths.Min());
                bool elementInvalid = false;
                for (int i = 0; i < points.Length; i++)
                {
                    double[] previous = Subtract(points[(i - 1 + points.Length) % points.Length], points[i]);
                    double[] following = Subtract(points[(i + 1) % points.Length], points[i]);
                    double scaledJacobian = Norm(Cross(previous, following)) / (Norm(previous) * Norm(following));
                    double cosine = Math.Max(-1, Math.Min(1, Dot(previous, following) / (Norm(previous) * Norm(following))));
                    double angle = Math.Acos(cosine) * 180 / Math.PI;
                    minAngle = Math.Min(minAngle, angle);
                    maxAngle = Math.Max(maxAngle, angle);
                    if (Dot(Cross(following, previous), plane.Normal) <= Tolerance) elementInvalid = true;
                    if (points.Length == 3)
                    {
                        minTriangleAngle = Math.Min(minTriangleAngle, angle);
                        maxTriangleAngle = Math.Max(maxTriangleAngle, angle);
                    }
                    if (points.Length == 4)
                    {
                        maxQuadSkew = Math.Max(maxQuadSkew, Math.Abs(angle - 90));
                        minQuadJacobian = Math.Min(minQuadJacobian, scaledJacobian);
                    }
                }
                if (elementInvalid) invalid++;
                if (points.Length == 3) triangles++; else quads++;
            }

            bool sizeOk = maxEdge <= allowedMax * 1.001;
            bool aspectOk = maxAspect <= 4.0001;
            bool quadShapeOk = quads == 0 || (maxQuadSkew <= 45.0001 && minQuadJacobian >= 0.4999 && minAngle >= 44.9999 && maxAngle <= 135.0001);
            bool triangleShapeOk = triangles == 0 || (minTriangleAngle >= 29.9999 && maxTriangleAngle <= 90.0001);
            bool orientationOk = invalid == 0;
            bool passed = sizeOk && aspectOk && quadShapeOk && triangleShapeOk && orientationOk;
            string F(double value) { return value.ToString("0.###", CultureInfo.InvariantCulture); }
            var report = new MeshQualityReport(passed,
                "Nodes: " + mesh.Nodes.Count + "    Elements: " + quads + " quads, " + triangles + " triangles" +
                (frameCount > 0 ? "    Constraining frames: " + frameCount : "") +
                (mesh.RemovedElements > 0 ? "    Shell elements removed for holes: " + mesh.RemovedElements : ""));
            report.Metrics.Add(new QualityMetric("Maximum size", F(maxEdge), "Requested/constraints: <= " + F(allowedMax),
                sizeOk ? 1 : Math.Max(0, 2 - maxEdge / allowedMax)));
            report.Metrics.Add(new QualityMetric("Aspect ratio", F(maxAspect), "CUBIT/Verdict: 1 - 4",
                Clamp01(1 - (maxAspect - 1) / 6)));
            if (quads > 0)
            {
                double normalizedSkew = maxQuadSkew / 90;
                report.Metrics.Add(new QualityMetric("Quadrilateral skew", F(normalizedSkew) + "  (" + F(maxQuadSkew) + " degrees)",
                    "CUBIT/Verdict: 0 - 0.5; 0 is ideal", Clamp01(1 - normalizedSkew)));
                report.Metrics.Add(new QualityMetric("Scaled Jacobian", F(minQuadJacobian),
                    "CUBIT/Verdict: 0.5 - 1; 1 is ideal", Clamp01(minQuadJacobian)));
                report.Metrics.Add(new QualityMetric("Quadrilateral angles", F(minAngle) + " - " + F(maxAngle) + " degrees",
                    "CUBIT/Verdict: 45 - 135 degrees", quadShapeOk ? 0.75 : 0.25));
            }
            if (triangles > 0)
                report.Metrics.Add(new QualityMetric("Triangle angles", F(minTriangleAngle) + " - " + F(maxTriangleAngle) + " degrees",
                    "CUBIT/Verdict: 30 - 90 degrees", triangleShapeOk ? 0.75 : 0.25));
            report.Metrics.Add(new QualityMetric("Orientation", invalid == 0 ? "0 invalid elements" : invalid + " invalid elements",
                "No inverted or degenerate elements", orientationOk ? 1 : 0));
            report.OverallScore = report.Metrics.Average(metric => metric.Score);
            return report;
        }

        private static double Clamp01(double value) { return Math.Max(0, Math.Min(1, value)); }

        private static MeshOperation ApplyMesh(cSapModel model, string sourceArea, string property, double angle, double mass,
            bool edgeConstraint, AreaLoads areaLoads, MeshData mesh, MeshOptionsDialog progress, PointSnapshot[] originalPoints,
            PointCoordinateIndex existingCoordinates)
        {
            bool profiling = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAP_QUAD_PROFILE_DIR"));
            long started = profiling ? Stopwatch.GetTimestamp() : 0;
            long setupTicks = 0, jointTicks = 0, shellTicks = 0, verifyTicks = 0;
            long propertyTicks = 0, assignmentTicks = 0, finalTicks = 0;
            string backupArea = "QTUNDO_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            Check(model.AreaObj.ChangeName(sourceArea, backupArea), "preparing original area for undo", "AreaObj.ChangeName");
            var operation = new MeshOperation(sourceArea, backupArea, originalPoints);
            var pointNames = new Dictionary<int, string>();
            int existingPointCount = 0;
            string[] existingPointNames = null;
            model.PointObj.GetNameList(ref existingPointCount, ref existingPointNames);
            var preExistingPoints = new HashSet<string>(existingPointNames ?? new string[0]);
            int completed = 0, total = mesh.Nodes.Count + mesh.Elements.Count;
            var progressClock = Stopwatch.StartNew();
            if (profiling) { setupTicks = Stopwatch.GetTimestamp() - started; started = Stopwatch.GetTimestamp(); }
            try
            {
                foreach (KeyValuePair<int, double[]> item in mesh.Nodes)
                {
                    string name;
                    double[] point = item.Value;
                    if (!existingCoordinates.TryFind(point, sourceArea, out name))
                    {
                        existingCoordinates.RequireUnoccupiedForNew(point, sourceArea);
                        name = "";
                        // Keep a coincident joint of an unrelated area distinct.
                        Check(model.PointObj.AddCartesian(point[0], point[1], point[2], ref name, "", "Global", true, 0), "creating point", "PointObj.AddCartesian");
                        existingCoordinates.Add(point, name, sourceArea);
                    }
                    pointNames[item.Key] = name;
                    if (!preExistingPoints.Contains(name) && !operation.GeneratedPoints.Contains(name))
                        operation.GeneratedPoints.Add(name);
                    completed++;
                    if (progressClock.ElapsedMilliseconds >= 100)
                    {
                        progress.SetProgress("Creating SAP2000 points...", completed, total);
                        progressClock.Restart();
                    }
                }
                if (profiling) jointTicks = Stopwatch.GetTimestamp() - started;
                if (mesh.Elements.Count == 1)
                {
                    string[] replacementPoints = mesh.Elements[0].Select(tag => pointNames[tag]).ToArray();
                    int sourcePointCount = 0;
                    string[] sourcePoints = null;
                    Check(model.AreaObj.GetPoints(backupArea, ref sourcePointCount, ref sourcePoints),
                        "checking unchanged Standard mesh", "AreaObj.GetPoints");
                    if (operation.GeneratedPoints.Count == 0 && sourcePoints != null &&
                        sourcePointCount == replacementPoints.Length &&
                        new HashSet<string>(sourcePoints, StringComparer.OrdinalIgnoreCase)
                            .SetEquals(replacementPoints))
                    {
                        Check(model.AreaObj.ChangeName(backupArea, sourceArea),
                            "restoring unchanged Standard source area", "AreaObj.ChangeName");
                        operation.Unchanged = true;
                        operation.Active = false;
                        return operation;
                    }
                }
                for (int i = 0; i < mesh.Elements.Count; i++)
                {
                    string[] points = mesh.Elements[i].Select(tag => pointNames[tag]).ToArray();
                    string name = "";
                    long phase = profiling ? Stopwatch.GetTimestamp() : 0;
                    int addArea = model.AreaObj.AddByPoint(points.Length, ref points, ref name, property,
                        i == 0 ? sourceArea : "");
                    if (profiling) { shellTicks += Stopwatch.GetTimestamp() - phase; phase = Stopwatch.GetTimestamp(); }
                    if (addArea != 0)
                        throw new InvalidOperationException("SAP2000 error while creating shell element " +
                            (i + 1) + " for source area " + sourceArea + " with joints " +
                            string.Join("|", points) + " [API: AreaObj.AddByPoint]: code " + addArea);
                    int createdCorners = 0;
                    string[] createdPoints = null;
                    int createdCode = model.AreaObj.GetPoints(name, ref createdCorners, ref createdPoints);
                    if (createdCode != 0 || createdCorners != points.Length ||
                        !new HashSet<string>(createdPoints ?? new string[0], StringComparer.OrdinalIgnoreCase)
                            .SetEquals(points))
                        throw new InvalidDataException("SAP2000 discarded or changed shell " + name +
                            " from source " + sourceArea + " at element " + (i + 1) +
                            " (GetPoints=" + createdCode + ", vertices=" + createdCorners +
                            ", requested=" + string.Join("|", points) + ", coordinates=" +
                            string.Join("|", mesh.Elements[i].Select(tag => string.Join(":",
                                mesh.Nodes[tag].Select(value => value.ToString("R", CultureInfo.InvariantCulture))))) + ").");
                    if (profiling) { verifyTicks += Stopwatch.GetTimestamp() - phase; phase = Stopwatch.GetTimestamp(); }
                    operation.GeneratedAreas.Add(name);
                    if (Math.Abs(angle) > Tolerance) progress.CheckOptionalTransfer(
                        model.AreaObj.SetLocalAxes(name, angle, eItemType.Objects), sourceArea, name,
                        "local axes", "AreaObj.SetLocalAxes");
                    if (Math.Abs(mass) > Tolerance) progress.CheckOptionalTransfer(
                        model.AreaObj.SetMass(name, mass, true, eItemType.Objects), sourceArea, name,
                        "area mass", "AreaObj.SetMass");
                    if (edgeConstraint) progress.CheckOptionalTransfer(
                        model.AreaObj.SetEdgeConstraint(name, true, eItemType.Objects), sourceArea, name,
                        "edge constraint", "AreaObj.SetEdgeConstraint");
                    if (profiling) { propertyTicks += Stopwatch.GetTimestamp() - phase; phase = Stopwatch.GetTimestamp(); }
                    areaLoads.Apply(model, name, points, progress);
                    if (profiling) assignmentTicks += Stopwatch.GetTimestamp() - phase;
                    completed++;
                    if (completed == total || progressClock.ElapsedMilliseconds >= 100)
                    {
                        progress.SetProgress("Creating shell elements and transferring loads...", completed, total);
                        progressClock.Restart();
                    }
                }
                if (profiling) started = Stopwatch.GetTimestamp();
                foreach (string createdArea in operation.GeneratedAreas)
                {
                    int vertices = 0;
                    string[] names = null;
                    if (model.AreaObj.GetPoints(createdArea, ref vertices, ref names) != 0)
                        throw new InvalidDataException("SAP2000 discarded shell " + createdArea +
                            " after creation from source " + sourceArea + ".");
                }
                areaLoads.FinishGeometry();
                if (profiling)
                {
                    finalTicks = Stopwatch.GetTimestamp() - started;
                    RecordStandardInsertion(sourceArea, operation.GeneratedAreas.Count, setupTicks,
                        jointTicks, shellTicks, verifyTicks, propertyTicks, assignmentTicks, finalTicks,
                        areaLoads.Count, areaLoads.HasInterpolatedValues,
                        areaLoads.InterpolationTicks, areaLoads.DirectAssignmentTicks);
                }
                return operation;
            }
            catch
            {
                if (progress.DetectRegionalChange()) throw;
                UndoMesh(model, operation);
                throw;
            }
        }

        private static List<MeshOperation> ApplyMeshesFast(cSapModel model, List<PreparedMesh> prepared,
            MeshOptionsDialog progress, PointCoordinateIndex existingCoordinates, FastTableSnapshot snapshot,
            bool finalNumericNames = false, bool polygonCsvInsertion = false)
        {
            // FAST now writes three-node shells with a valid Joint4 padding
            // while retaining NumJoints=3. Triangles alone are not a reason
            // to switch silently to the slower per-object insertion path.
            var clock = Stopwatch.StartNew();
            var responsiveness = Stopwatch.StartNew();
            int staged = 0;
            EditableTable joints = snapshot.Joints.Clone(), areas = WithPaddedExistingTriangles(snapshot.Areas), sections = snapshot.Sections.Clone();
            RequireFields(joints, "Joint", "CoordSys", "CoordType", "XorR", "Y", "Z", "SpecialJt", "GUID");
            RequireFields(areas, "Area", "Joint1", "Joint2", "Joint3", "GUID");
            RequireFields(sections, "Area", "Section");
            var usedPointNames = new HashSet<string>(joints.Values("Joint"));
            var usedAreaNames = new HashSet<string>(areas.Values("Area"));
            long nextPoint = MaximumNumericName(usedPointNames);
            long nextArea = MaximumNumericName(usedAreaNames);
            var expectedConnectivity = new Dictionary<string, string[]>();
            var expectedProperties = new Dictionary<string, string>();
            var operations = new List<MeshOperation>();
            string token = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
            int pointSequence = 1, areaSequence = 0;
            string sapDecimal = SapTableDecimalSeparator();

            foreach (PreparedMesh item in prepared)
            {
                var operation = new MeshOperation(item.SourceArea, null, item.OriginalPoints) { FastMode = true };
                operations.Add(operation);
                var meshPointNames = new Dictionary<int, string>();
                foreach (KeyValuePair<int, double[]> node in item.Mesh.Nodes)
                {
                    string pointName;
                    if (!existingCoordinates.TryFind(node.Value, item.SourceArea, out pointName))
                    {
                        existingCoordinates.RequireUnoccupiedForNew(node.Value, item.SourceArea);
                        do pointName = finalNumericNames
                            ? NextNumericName(ref nextPoint) :
                            "QTP" + token + (pointSequence++).ToString("D6", CultureInfo.InvariantCulture);
                        while (usedPointNames.Contains(pointName));
                        usedPointNames.Add(pointName);
                        existingCoordinates.Add(node.Value, pointName, item.SourceArea);
                        operation.GeneratedPoints.Add(pointName);
                        joints.Append(new Dictionary<string, string>
                        {
                            { "Joint", pointName }, { "CoordSys", "GLOBAL" }, { "CoordType", "Cartesian" },
                            { "XorR", FormatSapTableNumber(node.Value[0], sapDecimal) },
                            { "Y", FormatSapTableNumber(node.Value[1], sapDecimal) },
                            { "T", "" }, { "Z", FormatSapTableNumber(node.Value[2], sapDecimal) },
                            { "SpecialJt", "No" }, { "GUID", "" }
                        });
                    }
                    meshPointNames[node.Key] = pointName;
                    if ((++staged & 511) == 0) PollCancellation(progress, responsiveness);
                }
                foreach (int[] element in item.Mesh.Elements)
                {
                    string areaName;
                    do areaName = finalNumericNames
                        ? NextNumericName(ref nextArea) :
                        "QTA" + token + (areaSequence++).ToString("D6", CultureInfo.InvariantCulture);
                    while (usedAreaNames.Contains(areaName));
                    usedAreaNames.Add(areaName);
                    operation.GeneratedAreas.Add(areaName);
                    if (operation.PrimaryGeneratedArea == null) operation.PrimaryGeneratedArea = areaName;
                    string[] pointNames = element.Select(tag => meshPointNames[tag]).ToArray();
                    if (pointNames.Length > areas.MaxJointField)
                        throw new InvalidDataException("FAST does not support the connectivity required by shell element " + areaName + ".");
                    expectedConnectivity[areaName] = pointNames;
                    expectedProperties[areaName] = item.Property;
                    var connectivity = new Dictionary<string, string>
                    {
                        { "Area", areaName }, { "NumJoints", pointNames.Length.ToString(CultureInfo.InvariantCulture) }, { "GUID", "" }
                    };
                    for (int joint = 1; joint <= areas.MaxJointField; joint++)
                        connectivity["Joint" + joint] = joint <= pointNames.Length ? pointNames[joint - 1] :
                            pointNames.Length == 3 && joint == 4 ? pointNames[2] : "";
                    areas.Append(connectivity);
                    sections.Append(new Dictionary<string, string>
                    {
                        { "Area", areaName }, { "Section", item.Property }, { "MatProp", "Default" }
                    });
                    if ((++staged & 511) == 0) PollCancellation(progress, responsiveness);
                }
            }
            RecordFastPhase("batch-prepare-tables", clock);
            progress.SetIndeterminate("FAST: bulk importing all points and shell elements...");
            WriteEditableTable(model, joints);
            PollCancellation(progress, responsiveness);
            if (polygonCsvInsertion) WritePolygonAreaTableCsv(model, areas);
            else WriteEditableTable(model, areas);
            PollCancellation(progress, responsiveness);
            WriteEditableTable(model, sections);
            Application.DoEvents();
            progress.ThrowIfCancellationRequested();
            RecordFastPhase("batch-write-tables", clock);
            int fatal = 0, errors = 0, warnings = 0, information = 0;
            string importLog = "";
            Check(model.DatabaseTables.ApplyEditedTables(true, ref fatal, ref errors, ref warnings, ref information, ref importLog),
                "applying FAST tables", "DatabaseTables.ApplyEditedTables");
            progress.ThrowIfCancellationRequested();
            if (fatal != 0 || errors != 0 || warnings != 0)
                throw new InvalidOperationException("Invalid FAST import: " + ImportSummary(fatal, errors, warnings, importLog));
            RecordFastPhase("batch-apply-tables", clock);

            EditableTable resultingJoints = ReadEditableTable(model, "Joint Coordinates");
            EditableTable resultingAreas = ReadEditableTable(model, "Connectivity - Area");
            EditableTable resultingSections = ReadEditableTable(model, "Area Section Assignments");
            if (polygonCsvInsertion) VerifyOriginalPolygons(model, snapshot.Areas);
            PollCancellation(progress, responsiveness);
            Dictionary<string, string[]> areaRows = resultingAreas.RowsBy("Area");
            Dictionary<string, string[]> sectionRows = resultingSections.RowsBy("Area");
            foreach (KeyValuePair<string, string[]> expected in expectedConnectivity)
            {
                if ((++staged & 511) == 0) PollCancellation(progress, responsiveness);
                string[] row;
                if (!areaRows.TryGetValue(expected.Key, out row)) throw new InvalidDataException("FAST: missing shell element " + expected.Key);
                for (int i = 0; i < expected.Value.Length; i++)
                    if (resultingAreas.Value(row, "Joint" + (i + 1)) != expected.Value[i])
                        throw new InvalidDataException("FAST: invalid connectivity for " + expected.Key);
                if (!sectionRows.TryGetValue(expected.Key, out row) ||
                    resultingSections.Value(row, "Section") != expectedProperties[expected.Key])
                    throw new InvalidDataException("FAST: invalid property for " + expected.Key);
            }
            if (resultingJoints.RecordCount != joints.RecordCount || resultingAreas.RecordCount != areas.RecordCount)
                throw new InvalidDataException("FAST: inconsistent imported object count.");
            RecordFastPhase("batch-verify-tables", clock);

            int completed = 0, total = operations.Sum(operation => operation.GeneratedAreas.Count);
            for (int index = 0; index < prepared.Count; index++)
                foreach (string areaName in operations[index].GeneratedAreas)
                {
                    PreparedMesh item = prepared[index];
                    if (Math.Abs(item.Angle) > Tolerance) progress.CheckOptionalTransfer(
                        model.AreaObj.SetLocalAxes(areaName, item.Angle, eItemType.Objects), item.SourceArea, areaName,
                        "local axes", "AreaObj.SetLocalAxes");
                    if (Math.Abs(item.Mass) > Tolerance) progress.CheckOptionalTransfer(
                        model.AreaObj.SetMass(areaName, item.Mass, true, eItemType.Objects), item.SourceArea, areaName,
                        "area mass", "AreaObj.SetMass");
                    if (item.EdgeConstraint) progress.CheckOptionalTransfer(
                        model.AreaObj.SetEdgeConstraint(areaName, true, eItemType.Objects), item.SourceArea, areaName,
                        "edge constraint", "AreaObj.SetEdgeConstraint");
                    item.Loads.Apply(model, areaName, expectedConnectivity[areaName], progress);
                    completed++;
                    if (completed == total || completed % 500 == 0)
                        progress.SetProgress("FAST: transferring assignments...", completed, total);
                }
            RecordFastPhase("batch-assignments", clock);
            foreach (PreparedMesh item in prepared) item.Loads.FinishGeometry();
            return operations;
        }

        private static void RecordFastPhase(string phase, Stopwatch clock)
        {
            string directory = Environment.GetEnvironmentVariable("SAP_QUAD_PROFILE_DIR");
            if (!string.IsNullOrEmpty(directory))
                try
                {
                    File.AppendAllText(Path.Combine(directory, "csharp-fast-phases.csv"), phase + "," +
                        clock.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture) + Environment.NewLine);
                }
                catch { }
            clock.Restart();
        }

        private static void RecordApplyPhase(string phase, Stopwatch clock)
        {
            RecordPhase("csharp-apply-phases.csv", phase, clock);
        }

        private static void RecordGenerationPhase(string phase, Stopwatch clock)
        {
            RecordPhase("csharp-generate-phases.csv", phase, clock);
        }

        private static void RecordPhase(string file, string phase, Stopwatch clock)
        {
            string directory = Environment.GetEnvironmentVariable("SAP_QUAD_PROFILE_DIR");
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                try
                {
                    File.AppendAllText(Path.Combine(directory, file), phase + "," +
                        clock.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture) + Environment.NewLine);
                }
                catch (IOException) { }
            clock.Restart();
        }

        private static void RecordStandardInsertion(string area, int children,
            long setup, long joints, long shells, long verifyShells, long basicProperties,
            long assignments, long finalChecks, int directAssignmentCount, bool interpolated,
            long interpolationTicks, long directAssignmentTicks)
        {
            string directory = Environment.GetEnvironmentVariable("SAP_QUAD_PROFILE_DIR");
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
            long[] segments = { setup, joints, shells, verifyShells, basicProperties, assignments, finalChecks };
            try
            {
                File.AppendAllText(Path.Combine(directory, "csharp-standard-insertion.csv"),
                    area + "," + children.ToString(CultureInfo.InvariantCulture) + "," +
                    string.Join(",", segments.Select(ticks =>
                        (ticks / (double)Stopwatch.Frequency).ToString("R", CultureInfo.InvariantCulture))) +
                    "," + directAssignmentCount.ToString(CultureInfo.InvariantCulture) +
                    "," + (interpolated ? "1" : "0") + "," +
                    (interpolationTicks / (double)Stopwatch.Frequency).ToString("R", CultureInfo.InvariantCulture) +
                    "," + (directAssignmentTicks / (double)Stopwatch.Frequency).ToString("R", CultureInfo.InvariantCulture) +
                    Environment.NewLine);
            }
            catch (IOException) { }
        }

        private static void PollCancellation(MeshOptionsDialog progress, Stopwatch clock)
        {
            if (clock.ElapsedMilliseconds < 150) return;
            Application.DoEvents();
            progress.ThrowIfCancellationRequested();
            clock.Restart();
        }

        private static MeshOperation ApplyMeshFast(cSapModel model, string sourceArea, string property, double angle, double mass,
            bool edgeConstraint, AreaLoads areaLoads, MeshData mesh, MeshOptionsDialog progress, PointSnapshot[] originalPoints,
            PointCoordinateIndex existingCoordinates)
        {
            string token = Guid.NewGuid().ToString("N").Substring(0, 8);
            var operation = new MeshOperation(sourceArea, null, originalPoints) { FastMode = true };
            EditableTable joints = null, areas = null, sections = null;
            try
            {
                var responsiveness = Stopwatch.StartNew();
                joints = ReadEditableTable(model, "Joint Coordinates");
                areas = WithPaddedExistingTriangles(ReadEditableTable(model, "Connectivity - Area"));
                sections = ReadEditableTable(model, "Area Section Assignments");
                RequireFields(joints, "Joint", "CoordSys", "CoordType", "XorR", "Y", "Z", "SpecialJt", "GUID");
                RequireFields(areas, "Area", "Joint1", "Joint2", "Joint3", "GUID");
                RequireFields(sections, "Area", "Section");

                var usedPointNames = new HashSet<string>(joints.Values("Joint"));
                var usedAreaNames = new HashSet<string>(areas.Values("Area"));
                var meshPointNames = new Dictionary<int, string>();
                int pointSequence = 1;
                string sapDecimal = SapTableDecimalSeparator();
                foreach (KeyValuePair<int, double[]> item in mesh.Nodes)
                {
                    string pointName;
                    if (!existingCoordinates.TryFind(item.Value, sourceArea, out pointName))
                    {
                        existingCoordinates.RequireUnoccupiedForNew(item.Value, sourceArea);
                        do pointName = "QTP" + token.ToUpperInvariant() + (pointSequence++).ToString("D6", CultureInfo.InvariantCulture);
                        while (usedPointNames.Contains(pointName));
                        usedPointNames.Add(pointName);
                        existingCoordinates.Add(item.Value, pointName, sourceArea);
                        operation.GeneratedPoints.Add(pointName);
                        joints.Append(new Dictionary<string, string>
                        {
                            { "Joint", pointName }, { "CoordSys", "GLOBAL" }, { "CoordType", "Cartesian" },
                            { "XorR", FormatSapTableNumber(item.Value[0], sapDecimal) },
                            { "Y", FormatSapTableNumber(item.Value[1], sapDecimal) },
                            { "T", "" }, { "Z", FormatSapTableNumber(item.Value[2], sapDecimal) },
                            { "SpecialJt", "No" }, { "GUID", "" }
                        });
                    }
                    meshPointNames[item.Key] = pointName;
                    if ((meshPointNames.Count & 511) == 0) PollCancellation(progress, responsiveness);
                }

                var expectedConnectivity = new Dictionary<string, string[]>();
                for (int i = 0; i < mesh.Elements.Count; i++)
                {
                    string areaName = "QTA" + token.ToUpperInvariant() + i.ToString("D6", CultureInfo.InvariantCulture);
                    while (usedAreaNames.Contains(areaName)) areaName += "X";
                    usedAreaNames.Add(areaName);
                    operation.GeneratedAreas.Add(areaName);
                    if (i == 0) operation.PrimaryGeneratedArea = areaName;
                    string[] pointNames = mesh.Elements[i].Select(tag => meshPointNames[tag]).ToArray();
                    if (pointNames.Length > areas.MaxJointField)
                        throw new InvalidDataException("FAST does not support the connectivity required by shell element " + areaName + ".");
                    expectedConnectivity[areaName] = pointNames;
                    var connectivity = new Dictionary<string, string>
                    {
                        { "Area", areaName }, { "NumJoints", pointNames.Length.ToString(CultureInfo.InvariantCulture) }, { "GUID", "" }
                    };
                    for (int joint = 1; joint <= areas.MaxJointField; joint++)
                        connectivity["Joint" + joint] = joint <= pointNames.Length ? pointNames[joint - 1] :
                            pointNames.Length == 3 && joint == 4 ? pointNames[2] : "";
                    areas.Append(connectivity);
                    sections.Append(new Dictionary<string, string>
                    {
                        { "Area", areaName }, { "Section", property }, { "MatProp", "Default" }
                    });
                    if ((i & 511) == 0) PollCancellation(progress, responsiveness);
                }

                progress.SetIndeterminate("FAST: bulk importing points and shell elements...");
                WriteEditableTable(model, joints);
                WriteEditableTable(model, areas);
                WriteEditableTable(model, sections);
                Application.DoEvents();
                progress.ThrowIfCancellationRequested();
                int fatal = 0, errors = 0, warnings = 0, information = 0;
                string importLog = "";
                Check(model.DatabaseTables.ApplyEditedTables(true, ref fatal, ref errors, ref warnings, ref information, ref importLog),
                    "applying FAST tables", "DatabaseTables.ApplyEditedTables");
                progress.ThrowIfCancellationRequested();
                if (fatal != 0 || errors != 0 || warnings != 0)
                    throw new InvalidOperationException("Invalid FAST import: " + ImportSummary(fatal, errors, warnings, importLog));

                EditableTable resultingJoints = ReadEditableTable(model, "Joint Coordinates");
                EditableTable resultingAreas = ReadEditableTable(model, "Connectivity - Area");
                EditableTable resultingSections = ReadEditableTable(model, "Area Section Assignments");
                PollCancellation(progress, responsiveness);
                Dictionary<string, string[]> areaRows = resultingAreas.RowsBy("Area");
                Dictionary<string, string[]> sectionRows = resultingSections.RowsBy("Area");
                int completedForValidation = 0;
                foreach (KeyValuePair<string, string[]> expected in expectedConnectivity)
                {
                    if ((++completedForValidation & 511) == 0) PollCancellation(progress, responsiveness);
                    string[] row;
                    if (!areaRows.TryGetValue(expected.Key, out row)) throw new InvalidDataException("FAST: missing shell element " + expected.Key);
                    for (int i = 0; i < expected.Value.Length; i++)
                        if (resultingAreas.Value(row, "Joint" + (i + 1)) != expected.Value[i])
                            throw new InvalidDataException("FAST: invalid connectivity for " + expected.Key);
                    if (!sectionRows.TryGetValue(expected.Key, out row) || resultingSections.Value(row, "Section") != property)
                        throw new InvalidDataException("FAST: invalid property for " + expected.Key);
                }
                if (resultingJoints.RecordCount != joints.RecordCount || resultingAreas.RecordCount != areas.RecordCount)
                    throw new InvalidDataException("FAST: inconsistent imported object count.");

                int completed = 0;
                foreach (string areaName in operation.GeneratedAreas)
                {
                    if (Math.Abs(angle) > Tolerance) progress.CheckOptionalTransfer(
                        model.AreaObj.SetLocalAxes(areaName, angle, eItemType.Objects), sourceArea, areaName,
                        "local axes", "AreaObj.SetLocalAxes");
                    if (Math.Abs(mass) > Tolerance) progress.CheckOptionalTransfer(
                        model.AreaObj.SetMass(areaName, mass, true, eItemType.Objects), sourceArea, areaName,
                        "area mass", "AreaObj.SetMass");
                    if (edgeConstraint) progress.CheckOptionalTransfer(
                        model.AreaObj.SetEdgeConstraint(areaName, true, eItemType.Objects), sourceArea, areaName,
                        "edge constraint", "AreaObj.SetEdgeConstraint");
                    areaLoads.Apply(model, areaName, expectedConnectivity[areaName], progress);
                    completed++;
                    if (completed == operation.GeneratedAreas.Count || completed % 500 == 0)
                        progress.SetProgress("FAST: transferring assignments...", completed, operation.GeneratedAreas.Count);
                }
                areaLoads.FinishGeometry();
                return operation;
            }
            catch
            {
                if (!progress.DetectRegionalChange()) model.DatabaseTables.CancelTableEditing();
                throw;
            }
        }

        private static FastTableSnapshot CaptureFastSnapshot(cSapModel model)
        {
            var snapshot = new FastTableSnapshot(ReadEditableTable(model, "Joint Coordinates"),
                ReadEditableTable(model, "Connectivity - Area"), ReadEditableTable(model, "Area Section Assignments"));
            RequireFields(snapshot.Joints, "Joint", "GUID");
            RequireFields(snapshot.Areas, "Area", "GUID");
            return snapshot;
        }

        private static EditableTable WithPaddedExistingTriangles(EditableTable source)
        {
            EditableTable table = source.Clone();
            int count = Array.IndexOf(table.Fields, "NumJoints");
            int areaField = Array.IndexOf(table.Fields, "Area");
            int third = Array.IndexOf(table.Fields, "Joint3");
            int fourth = Array.IndexOf(table.Fields, "Joint4");
            if (areaField < 0 || third < 0 || fourth < 0) return table;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int row = 0; row < table.RecordCount; row++)
            {
                int offset = row * table.Fields.Length;
                string name = table.Data[offset + areaField];
                bool firstRow = !string.IsNullOrEmpty(name) && seen.Add(name);
                if (firstRow && (count < 0 || string.IsNullOrEmpty(table.Data[offset + count]) || table.Data[offset + count] == "3") &&
                    !string.IsNullOrEmpty(table.Data[offset + third]) &&
                    string.IsNullOrEmpty(table.Data[offset + fourth]))
                    table.Data[offset + fourth] = table.Data[offset + third];
            }
            return table;
        }

        private static bool SupportsPolygonCsvInsertion(cSapModel model)
        {
            string version = "";
            double number = 0;
            // The trimmed continuation round trip is verified on SAP2000
            // 27.1.0. Other hosts/versions fail the FAST preflight without
            // silently changing insertion mode.
            return (HostProductName() == "SAP2000" || HostProductName() == "CSiBridge") &&
                model.GetVersion(ref version, ref number) == 0 &&
                version.StartsWith("27.1.", StringComparison.Ordinal);
        }

        private static string CsvCell(string value)
        {
            value = value ?? "";
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0 ? value :
                "\"" + value.Replace("\"", "\"\"") + "\"";
        }

        private static void WritePolygonAreaTableCsv(cSapModel model, EditableTable table)
        {
            // SAP's editing-array import rejects a five-joint polygon's second
            // row (Area,Joint5,blank,blank,blank). The CSV API accepts the same
            // row without trailing empty cells; all full rows remain unchanged.
            int nameField = Array.IndexOf(table.Fields, "Area");
            int guidField = Array.IndexOf(table.Fields, "GUID");
            if (nameField < 0 || guidField < 0)
                throw new InvalidDataException("Polygon CSV import requires Area and GUID fields.");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var csv = new StringBuilder();
            csv.AppendLine(string.Join(",", table.Fields.Select(CsvCell)));
            int trimmed = 0;
            for (int row = 0; row < table.RecordCount; row++)
            {
                int offset = row * table.Fields.Length;
                string name = table.Data[offset + nameField];
                bool continuation = !seen.Add(name);
                int end = table.Fields.Length;
                if (continuation && string.IsNullOrEmpty(table.Data[offset + guidField]))
                {
                    while (end > nameField + 2 && string.IsNullOrEmpty(table.Data[offset + end - 1]))
                        end--;
                    if (end < table.Fields.Length) trimmed++;
                }
                csv.AppendLine(string.Join(",", Enumerable.Range(0, end)
                    .Select(index => CsvCell(table.Data[offset + index]))));
            }
            if (trimmed == 0) throw new InvalidDataException("No polygon continuation row required CSV trimming.");
            int version = table.Version;
            string contents = csv.ToString();
            Check(model.DatabaseTables.SetTableForEditingCSVString(table.Key, ref version, ref contents, ","),
                "preparing polygon-safe area connectivity CSV", "DatabaseTables.SetTableForEditingCSVString");
            Console.WriteLine("mesh-polygon-csv-continuations=" + trimmed);
        }

        private static void VerifyOriginalPolygons(cSapModel model, EditableTable source)
        {
            int nameField = Array.IndexOf(source.Fields, "Area");
            int guidField = Array.IndexOf(source.Fields, "GUID");
            int[] joints = Enumerable.Range(1, source.MaxJointField)
                .Select(index => Array.IndexOf(source.Fields, "Joint" + index)).Where(index => index >= 0).ToArray();
            var rows = Enumerable.Range(0, source.RecordCount).GroupBy(row => source.Data[row * source.Fields.Length + nameField],
                StringComparer.OrdinalIgnoreCase);
            foreach (var polygon in rows.Where(group => group.Count() > 1))
            {
                string[] expected = polygon.SelectMany(row => joints.Select(index =>
                    source.Data[row * source.Fields.Length + index])).Where(value => !string.IsNullOrEmpty(value)).ToArray();
                int count = 0;
                string[] actual = null;
                string guid = "";
                if (model.AreaObj.GetPoints(polygon.Key, ref count, ref actual) != 0 ||
                    count != expected.Length || !expected.SequenceEqual(actual ?? new string[0], StringComparer.OrdinalIgnoreCase) ||
                    model.AreaObj.GetGUID(polygon.Key, ref guid) != 0 ||
                    guid != source.Data[polygon.First() * source.Fields.Length + guidField])
                    throw new InvalidDataException("FAST CSV changed original polygon " + polygon.Key + ".");
            }
        }

        private static string[] FindDesignOverwriteTables(cSapModel model)
        {
            int count = 0;
            string[] keys = null, names = null;
            int[] importTypes = null;
            bool[] empty = null;
            Check(model.DatabaseTables.GetAllTables(ref count, ref keys, ref names,
                ref importTypes, ref empty), "checking design overwrite warnings",
                "DatabaseTables.GetAllTables");
            return Enumerable.Range(0, count).Where(index => !empty[index] &&
                keys[index].StartsWith("Overwrites - ", StringComparison.OrdinalIgnoreCase) &&
                keys[index].IndexOf("Design", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(index => keys[index]).ToArray();
        }

        private static string CreateNumberedBackup(string source)
        {
            if (string.IsNullOrEmpty(source) || !File.Exists(source))
                throw new InvalidOperationException("Save the SAP2000 model before meshing, or disable the backup option.");
            string directory = Path.GetDirectoryName(source);
            string stem = Path.GetFileNameWithoutExtension(source);
            for (int number = 1; number <= 999999; number++)
            {
                string suffix = "-backup-" + number.ToString("D4", CultureInfo.InvariantCulture) +
                    Path.GetExtension(source);
                // Continue the same sequence as backups produced by older installations.
                if (File.Exists(Path.Combine(directory, stem + ".quadmesher" + suffix))) continue;
                if (File.Exists(Path.Combine(directory, stem + ".FEMesher" + suffix))) continue;
                string backup = Path.Combine(directory, stem + ".MESHing" + suffix);
                if (File.Exists(backup)) continue;
                bool created = false;
                try
                {
                    using (var target = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    {
                        created = true;
                        input.CopyTo(target);
                        target.Flush(true);
                        if (target.Length != input.Length) throw new IOException("Incomplete SAP2000 model backup.");
                    }
                    Console.WriteLine("mesh-backup=" + backup);
                    return backup;
                }
                catch (IOException)
                {
                    if (created) { try { File.Delete(backup); } catch { } throw; }
                    if (File.Exists(backup)) continue;
                    throw;
                }
            }
            throw new IOException("No free numbered SAP2000 model backup name is available.");
        }

        private static string DesignOverwriteWarning(IEnumerable<string> tables, string backupPath)
        {
            string names = string.Join(", ", tables);
            string warning = L("These shell design overwrites/procedures are not transferred automatically " +
                    "(the table has records or is unreadable through the API): ",
                "Queste sovrascritture/procedure di progetto delle shell non sono trasferite automaticamente " +
                    "(la tabella contiene record oppure non e leggibile tramite API): ") +
                names + ". " +
                L("The mesh can be generated, but replicate these design settings manually on the child areas; " +
                    "Undo cannot guarantee restoring these particular design settings.",
                    "La mesh puo essere generata, ma occorre replicare manualmente tali impostazioni di progetto sulle aree figlie; " +
                    "Annulla non puo garantire il ripristino di queste specifiche impostazioni.");
            return warning + Environment.NewLine + Environment.NewLine +
                (backupPath == null
                    ? L("The numbered backup option is OFF. Cancel this generation, enable the backup option " +
                        "(selected by default) and generate again before applying the mesh.",
                        "La copia numerata e DISATTIVATA. Annullare questa generazione, attivare l'opzione di backup " +
                        "(selezionata per impostazione predefinita) e rigenerare prima di applicare la mesh.")
                    : L("A copy of the last saved model was created before meshing at: ",
                        "Una copia dell'ultimo modello salvato e stata creata prima della mesh in: ") +
                        backupPath + Environment.NewLine +
                        L("Use this file to recover the original design settings. Unsaved SAP2000 changes are not in this file.",
                            "Usare questo file per recuperare le impostazioni di progetto originali. " +
                            "Le modifiche SAP2000 non salvate non sono contenute nella copia."));
        }

        private static void UndoAttempts(cSapModel model, List<MeshAttemptResult> results, FastTableSnapshot snapshot,
            PointSnapshot[] originalPoints, Action checkpoint = null)
        {
            if (checkpoint != null) checkpoint();
            if (snapshot == null)
            {
                for (int i = results.Count - 1; i >= 0; i--)
                {
                    if (checkpoint != null) checkpoint();
                    UndoMesh(model, results[i].Operation, false);
                }
                return;
            }
            model.DatabaseTables.CancelTableEditing();
            if (checkpoint != null) checkpoint();
            WriteEditableTable(model, snapshot.Joints);
            EditableTable restoredAreas = WithPaddedExistingTriangles(snapshot.Areas);
            WriteEditableTable(model, restoredAreas);
            WriteEditableTable(model, snapshot.Sections);
            if (checkpoint != null) checkpoint();
            int fatal = 0, errors = 0, warnings = 0, information = 0; string log = "";
            Check(model.DatabaseTables.ApplyEditedTables(true, ref fatal, ref errors, ref warnings, ref information, ref log),
                "restoring FAST tables", "DatabaseTables.ApplyEditedTables");
            if (fatal != 0 || errors != 0 || warnings != 0)
                throw new InvalidOperationException("Invalid FAST restore: " + ImportSummary(fatal, errors, warnings, log));
            EditableTable joints = ReadEditableTable(model, "Joint Coordinates");
            EditableTable areas = ReadEditableTable(model, "Connectivity - Area");
            ValidateGuids(joints, "Joint", snapshot.JointGuids);
            ValidateGuids(areas, "Area", snapshot.AreaGuids);
            if (joints.RecordCount != snapshot.Joints.RecordCount || areas.RecordCount != snapshot.Areas.RecordCount)
                throw new InvalidDataException("FAST restore: inconsistent object count.");
            ModelCsvSnapshot.VerifyConnectivity(model, snapshot.Areas.Fields, snapshot.Areas.Data.ToArray(),
                snapshot.Areas.RecordCount, "FAST Undo");
            RestorePointSnapshots(model, originalPoints ?? new PointSnapshot[0]);
            foreach (MeshAttemptResult result in results)
            {
                result.Operation.Active = false;
                result.Operation.Undone = true;
            }
        }

        private static EditableTable ReadEditableTable(cSapModel model, string key)
        {
            int version = 0, records = 0;
            string[] fields = null, data = null;
            Check(model.DatabaseTables.GetTableForEditingArray(key, "", ref version, ref fields, ref records, ref data), "reading table " + key, "DatabaseTables.GetTableForEditingArray");
            if (fields != null && fields.Any(string.IsNullOrWhiteSpace))
                fields = ModelCsvSnapshot.RecoverFieldKeys(model, key, version, records, data, fields.Length);
            return new EditableTable(key, version, fields ?? new string[0], data ?? new string[0]);
        }

        private static EditableTable ReadDisplayTable(cSapModel model, string key)
        {
            int version = 0, records = 0;
            string[] groups = null, fields = null, data = null;
            Check(model.DatabaseTables.GetTableForDisplayArray(key, ref groups, "", ref version,
                ref fields, ref records, ref data), "reading displayed table " + key,
                "DatabaseTables.GetTableForDisplayArray");
            var table = new EditableTable(key, version, fields ?? new string[0], data ?? new string[0]);
            if (table.RecordCount != records)
                throw new InvalidDataException("SAP2000 returned incomplete displayed table " + key + ".");
            return table;
        }

        private static bool SupportsFastTableInsertion(cSapModel model)
        {
            // SAP 27.2 can return valid editing values with every FieldKey blank.
            // Use the same CSV/metadata-verified recovery as the actual FAST
            // reader; raw keys alone are not evidence that editable tables are
            // unavailable. Reject incomplete schemas before touching the model.
            EditableTable joints = ReadEditableTable(model, "Joint Coordinates");
            EditableTable areas = ReadEditableTable(model, "Connectivity - Area");
            EditableTable sections = ReadEditableTable(model, "Area Section Assignments");
            RequireFields(joints, "Joint", "CoordSys", "CoordType", "XorR", "Y", "Z", "SpecialJt", "GUID");
            RequireFields(areas, "Area", "Joint1", "Joint2", "Joint3", "GUID");
            RequireFields(sections, "Area", "Section");
            return true;
        }

        private static bool SharesTopologicalEdge(string[] firstNames, double[][] first,
            string[] secondNames, double[][] second)
        {
            var shared = new HashSet<string>(firstNames, StringComparer.OrdinalIgnoreCase);
            shared.IntersectWith(secondNames);
            for (int i = 0; i < first.Length; i++)
                for (int j = 0; j < second.Length; j++)
                {
                    double[] low, high;
                    if (!TryGetCollinearOverlap(first[i], first[(i + 1) % first.Length],
                        second[j], second[(j + 1) % second.Length], out low, out high)) continue;
                    if (shared.Count >= 2 && Enumerable.Range(0, first.Length).Count(k =>
                    {
                        double ignored;
                        return shared.Contains(firstNames[k]) && OnSegment(first[k], low, high, out ignored);
                    }) >= 2) return true;
                    // Adjacent coplanar shells can have a partial common edge
                    // without sharing any named corner (a T-junction). Join
                    // only when their interiors are on opposite sides AND no
                    // distinct original joint names occupy the same position.
                    // Coincident-but-unmerged input joints remain disconnected.
                    if (shared.Count == 0 &&
                        !Enumerable.Range(0, first.Length).Any(index => Enumerable.Range(0, second.Length).Any(other =>
                            !string.Equals(firstNames[index], secondNames[other], StringComparison.OrdinalIgnoreCase) &&
                            Distance(first[index], second[other]) <= Tolerance)) &&
                        OppositeInteriorsAtEdge(first, second, low, high)) return true;
                    // A short edge may end at a named station strictly inside a
                    // longer area's edge. The areas share only its other endpoint
                    // by name, but the intermediate station is a real shared seam,
                    // not a disconnected coincident joint. Do not connect edges
                    // that merely share a corner (or distinct names at the other end).
                    if (shared.Count == 1 &&
                        (shared.Contains(firstNames[i]) || shared.Contains(firstNames[(i + 1) % first.Length])) &&
                        (shared.Contains(secondNames[j]) || shared.Contains(secondNames[(j + 1) % second.Length])))
                    {
                        int firstNext = (i + 1) % first.Length, secondNext = (j + 1) % second.Length;
                        double t;
                        if ((!shared.Contains(firstNames[i]) && OnSegment(first[i], second[j], second[secondNext], out t) &&
                             t > Tolerance && t < 1 - Tolerance) ||
                            (!shared.Contains(firstNames[firstNext]) && OnSegment(first[firstNext], second[j], second[secondNext], out t) &&
                             t > Tolerance && t < 1 - Tolerance) ||
                            (!shared.Contains(secondNames[j]) && OnSegment(second[j], first[i], first[firstNext], out t) &&
                             t > Tolerance && t < 1 - Tolerance) ||
                            (!shared.Contains(secondNames[secondNext]) && OnSegment(second[secondNext], first[i], first[firstNext], out t) &&
                             t > Tolerance && t < 1 - Tolerance)) return true;
                    }
                }
            return false;
        }

        private static bool OppositeInteriorsAtEdge(double[][] first, double[][] second,
            double[] low, double[] high)
        {
            Plane plane = Plane.FromPolygon(first);
            if (second.Any(point => Math.Abs(plane.SignedDistance(point)) > Tolerance)) return false;
            double length = Distance(low, high);
            double probe = Math.Min(0.001, length * 0.001);
            if (probe <= Tolerance * 10) return false;
            double[] direction = Normalize(Cross(plane.Normal, Subtract(high, low)));
            double[] midpoint = Interpolate(low, high, 0.5);
            double[] plus = Enumerable.Range(0, 3).Select(axis => midpoint[axis] + direction[axis] * probe).ToArray();
            double[] minus = Enumerable.Range(0, 3).Select(axis => midpoint[axis] - direction[axis] * probe).ToArray();
            double[][] a = first.Select(plane.Project).ToArray(), b = second.Select(plane.Project).ToArray();
            bool firstPlus = PointInPolygon(plane.Project(plus), a);
            bool firstMinus = PointInPolygon(plane.Project(minus), a);
            bool secondPlus = PointInPolygon(plane.Project(plus), b);
            bool secondMinus = PointInPolygon(plane.Project(minus), b);
            return firstPlus != firstMinus && secondPlus != secondMinus && firstPlus != secondPlus;
        }

        private static HashSet<string> ConnectedBoundaryStations(AreaContext context,
            List<Tuple<string[], double[][]>> otherAreas)
        {
            var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int edge = 0; edge < context.Corners.Length; edge++)
            {
                double[] start = context.Corners[edge];
                double[] end = context.Corners[(edge + 1) % context.Corners.Length];
                var graph = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (Tuple<string[], double[][]> area in otherAreas)
                    for (int segment = 0; segment < area.Item1.Length; segment++)
                    {
                        int next = (segment + 1) % area.Item1.Length;
                        double firstT, lastT;
                        if (!OnSegment(area.Item2[segment], start, end, out firstT) ||
                            !OnSegment(area.Item2[next], start, end, out lastT) ||
                            Math.Abs(firstT - lastT) <= Tolerance) continue;
                        string firstName = area.Item1[segment], lastName = area.Item1[next];
                        HashSet<string> neighbors;
                        if (!graph.TryGetValue(firstName, out neighbors))
                            graph[firstName] = neighbors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        neighbors.Add(lastName);
                        if (!graph.TryGetValue(lastName, out neighbors))
                            graph[lastName] = neighbors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        neighbors.Add(firstName);
                    }
                string startName = context.CornerNames[edge];
                string endName = context.CornerNames[(edge + 1) % context.Corners.Length];
                if (!graph.ContainsKey(startName) || !graph.ContainsKey(endName)) continue;
                var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startName };
                var pending = new Queue<string>();
                pending.Enqueue(startName);
                while (pending.Count > 0)
                {
                    string current = pending.Dequeue();
                    foreach (string neighbor in graph[current])
                        if (reached.Add(neighbor)) pending.Enqueue(neighbor);
                }
                // Merely coincident endpoints are not a connected seam: both
                // original SAP joint identities must link through the chain.
                if (reached.Contains(endName)) connected.UnionWith(reached);
            }
            return connected;
        }

        private static void ValidateExistingConstraintPositions(cSapModel model, List<AreaContext> contexts,
            string[] selectedPoints, string[] selectedFrames, double tolerance)
        {
            var required = new HashSet<string>(selectedPoints, StringComparer.OrdinalIgnoreCase);
            foreach (AreaContext context in contexts)
            {
                required.UnionWith(context.CornerNames);
                required.UnionWith(context.ConstraintPointNames);
                foreach (List<BoundaryPoint> edge in context.EdgePoints)
                    required.UnionWith(edge.Select(point => point.Name));
            }
            foreach (string frame in selectedFrames)
            {
                string first = "", second = "";
                Check(model.FrameObj.GetPoints(frame, ref first, ref second),
                    "checking endpoints of selected frame " + frame, "FrameObj.GetPoints");
                required.Add(first); required.Add(second);
            }
            int count = 0;
            string[] names = null;
            Check(model.PointObj.GetNameList(ref count, ref names),
                "checking coincident original joints", "PointObj.GetNameList");
            if (names == null || names.Length != count)
                throw new InvalidDataException("SAP2000 returned incomplete original joint names.");
            new PointCoordinateIndex(tolerance).ValidateOriginalConstraints(model, names, required, contexts);
        }

        private static PointCoordinateIndex ReadExactPointCoordinates(cSapModel model, double maxSize,
            List<AreaContext> contexts)
        {
            var result = new PointCoordinateIndex(Math.Max(Tolerance, Math.Max(1, maxSize) * 1e-6));
            int[] groups = Enumerable.Range(0, contexts.Count).ToArray();
            for (int i = 0; i < contexts.Count; i++)
                for (int j = i + 1; j < contexts.Count; j++)
                    if (groups[i] != groups[j] && (SharesTopologicalEdge(contexts[i].CornerNames,
                        contexts[i].Corners, contexts[j].CornerNames, contexts[j].Corners) ||
                        HasIntersectingShellSegment(contexts[i], contexts[j])))
                    {
                        int oldGroup = groups[j], nextGroup = groups[i];
                        for (int k = 0; k < groups.Length; k++)
                            if (groups[k] == oldGroup) groups[k] = nextGroup;
                    }
            for (int i = 0; i < contexts.Count; i++)
                result.RegisterArea(contexts[i].Name, groups[i], contexts[i].CornerNames);

            int areaCount = 0;
            string[] areaNames = null;
            Check(model.AreaObj.GetNameList(ref areaCount, ref areaNames),
                "reading joint ownership before mesh insertion", "AreaObj.GetNameList");
            var incidence = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            var connectedOwners = new Dictionary<int, HashSet<string>>();
            var unselectedBoundaries = new List<Tuple<string[], double[][]>>();
            foreach (int group in groups.Distinct())
                connectedOwners[group] = new HashSet<string>(contexts.Where((context, index) => groups[index] == group)
                    .Select(context => context.Name), StringComparer.OrdinalIgnoreCase);
            var coordinates = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            foreach (string area in areaNames ?? new string[0])
            {
                int count = 0;
                string[] points = null;
                Check(model.AreaObj.GetPoints(area, ref count, ref points),
                    "reading joint ownership of area " + area, "AreaObj.GetPoints");
                if (count > 4 && count % 4 != 0) result.MarkIncompletePolygonArea();
                foreach (string point in points ?? new string[0])
                {
                    HashSet<string> owners;
                    if (!incidence.TryGetValue(point, out owners))
                        incidence[point] = owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    owners.Add(area);
                }
                if (contexts.Any(context => string.Equals(context.Name, area, StringComparison.OrdinalIgnoreCase)))
                    continue;
                double[][] corners = points.Select(name =>
                {
                    double[] position;
                    if (!coordinates.TryGetValue(name, out position))
                        coordinates[name] = position = Coordinate(model, name);
                    return position;
                }).ToArray();
                unselectedBoundaries.Add(Tuple.Create(points, corners));
                for (int i = 0; i < contexts.Count; i++)
                    if (SharesTopologicalEdge(contexts[i].CornerNames, contexts[i].Corners, points, corners))
                        connectedOwners[groups[i]].Add(area);
            }
            for (int i = 0; i < contexts.Count; i++)
            {
                AreaContext context = contexts[i];
                HashSet<string> connectedStations = ConnectedBoundaryStations(context, unselectedBoundaries);
                var originalCorners = new HashSet<string>(context.CornerNames, StringComparer.OrdinalIgnoreCase);
                IEnumerable<string> candidates = context.CornerNames.Concat(context.ConstraintPointNames)
                    .Concat(context.EdgePoints.SelectMany(edge => edge.Select(point => point.Name)));
                foreach (string name in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    HashSet<string> owners;
                    if (originalCorners.Contains(name) || connectedStations.Contains(name) ||
                        !incidence.TryGetValue(name, out owners) ||
                        owners.Any(owner => connectedOwners[groups[i]].Contains(owner)))
                        result.Permit(context.Name, name);
                }
            }
            int pointCount = 0;
            string[] pointNames = null;
            Check(model.PointObj.GetNameList(ref pointCount, ref pointNames),
                "reading eligible SAP2000 joint coordinates", "PointObj.GetNameList");
            foreach (string name in pointNames ?? new string[0])
            {
                double[] position;
                if (!coordinates.TryGetValue(name, out position)) position = Coordinate(model, name);
                result.AddOriginal(position, name);
            }
            return result;
        }

        private static bool HasIntersectingShellSegment(AreaContext first, AreaContext second)
        {
            if (Math.Abs(Dot(first.Plane.Normal, second.Plane.Normal)) >= 1 - 1e-8) return false;
            foreach (AreaContext source in new[] { first, second })
            {
                AreaContext host = source == first ? second : first;
                double[][] polygon = host.Corners.Select(host.Plane.Project).ToArray();
                var crossings = new List<double[]>();
                for (int edge = 0; edge < source.Corners.Length; edge++)
                {
                    double[] start = source.Corners[edge], end = source.Corners[(edge + 1) % source.Corners.Length];
                    double from = host.Plane.SignedDistance(start), to = host.Plane.SignedDistance(end);
                    if (Math.Abs(from) <= Tolerance && Math.Abs(to) <= Tolerance)
                    {
                        if (ClipSegmentToArea(host, start, end).Count > 0) return true;
                        continue;
                    }
                    if (from * to > 0 || Math.Abs(from - to) <= Tolerance) continue;
                    double t = from / (from - to);
                    if (t < -Tolerance || t > 1 + Tolerance) continue;
                    double[] crossing = Interpolate(start, end, t);
                    if (PointInsideOrBoundary(host.Plane.Project(crossing), polygon))
                        crossings.Add(crossing);
                }
                if (crossings.Any(point => crossings.Any(other => Distance(point, other) > Tolerance))) return true;
            }
            return false;
        }


        private static string SapTableDecimalSeparator()
        {
            var separator = new StringBuilder(8);
            // SAP uses the Windows per-user decimal override, which need not
            // match the .NET thread culture. Keep backend CSV/JSON invariant.
            if (GetLocaleInfoEx(null, 0x0000000E, separator, separator.Capacity) == 0 ||
                (separator.ToString() != "." && separator.ToString() != ","))
                throw new InvalidOperationException("Cannot read the Windows decimal separator for SAP tables.");
            return separator.ToString();
        }

        private sealed class HostRegionalSettingsChangedException : InvalidOperationException
        {
            internal HostRegionalSettingsChangedException() : base(HostRegionalWarningMessage(HostProductName())) { }
        }

        private static string HostRegionalWarningMessage(string host)
        {
            return L("Windows regional settings changed after this " + host + " session started. Do not use MESHing or " +
                    "save the model in this state. Close the model without saving, exit " + host + ", then restart it. " +
                    "Do not change regional settings while " + host + " is open.",
                "Le impostazioni internazionali di Windows sono cambiate dopo l'avvio di questa sessione di " + host + ". " +
                    "Non usare MESHing e non salvare il modello in questo stato. Chiudere il modello senza salvare, " +
                    "chiudere " + host + " e riavviarlo. Non cambiare le impostazioni internazionali mentre " + host + " è aperto.");
        }

        private static bool RegionalWriteAfterHostStart(DateTime hostStartedUtc, DateTime lastWriteUtc)
        {
            return lastWriteUtc >= hostStartedUtc;
        }

        private static DateTime RegistryLastWriteUtc(RegistryKey key)
        {
            long fileTime;
            int result = RegQueryInfoKey(key.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                IntPtr.Zero, IntPtr.Zero, out fileTime);
            if (result != 0) throw new InvalidOperationException("Cannot read the Windows regional change time: " + result);
            return DateTime.FromFileTimeUtc(fileTime);
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegQueryInfoKey(SafeRegistryHandle key, IntPtr keyClass,
            IntPtr classLength, IntPtr reserved, IntPtr subKeys, IntPtr maxSubKeyLength,
            IntPtr maxClassLength, IntPtr values, IntPtr maxValueNameLength,
            IntPtr maxValueLength, IntPtr securityDescriptor, out long lastWriteTime);

        private static bool HostRegionalSettingsChanged()
        {
            if (!IsSupportedHostProcess()) return false;
            if (Volatile.Read(ref hostRegionalUnsafe) != 0) return true;
            try
            {
                DateTime hostStartedUtc = CurrentHostStartedUtc.Value;
                string[] paths = { @"Control Panel\International", @"Control Panel\International\User Profile",
                    @"Control Panel\International\User Profile\Time",
                    @"Control Panel\International\User Profile\Calendars",
                    @"Control Panel\International\Geo" };
                for (int index = 0; index < paths.Length; index++)
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(paths[index]))
                    {
                        if (key == null)
                        {
                            if (index == 0) throw new InvalidOperationException("Windows regional settings registry key is missing.");
                            continue;
                        }
                        if (RegionalWriteAfterHostStart(hostStartedUtc, RegistryLastWriteUtc(key)))
                        {
                            Interlocked.Exchange(ref hostRegionalUnsafe, 1);
                            return true;
                        }
                    }
                }
                return false;
            }
            catch (Exception error)
            {
                // If the host's culture cannot be established, never import
                // model tables under an unverified Windows format.
                LogPluginException(error);
                Interlocked.Exchange(ref hostRegionalUnsafe, 1);
                return true;
            }
        }

        // Read Windows' live per-user overrides, not the .NET thread culture:
        // SAP tables can change formatting without updating CurrentCulture.
        private static string WindowsRegionalSignature()
        {
            uint[] fields = { 0x0000005C, 0x0000000E, 0x0000000F, 0x0000000C,
                0x00000010, 0x00000051, 0x00000013, 0x0000001F, 0x00000020,
                0x00001003, 0x00000014 };
            var values = new List<string>();
            foreach (uint field in fields)
            {
                var value = new StringBuilder(128);
                if (GetLocaleInfoEx(null, field, value, value.Capacity) == 0)
                    throw new InvalidOperationException("Cannot read the Windows regional settings for SAP tables.");
                values.Add(value.ToString());
            }
            return string.Join("\u001f", values);
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetLocaleInfoEx(string localeName, uint localeType, StringBuilder value, int capacity);

        private static string FormatSapTableNumber(double value, string separator)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || (separator != "." && separator != ","))
                throw new InvalidDataException("Invalid SAP table number or decimal separator.");
            return value.ToString("R", CultureInfo.InvariantCulture).Replace(".", separator);
        }

        private static void WriteEditableTable(cSapModel model, EditableTable table)
        {
            int version = table.Version;
            string[] fields = table.Fields.ToArray(), data = table.Data.ToArray();
            Check(model.DatabaseTables.SetTableForEditingArray(table.Key, ref version, ref fields, table.RecordCount, ref data),
                "preparing table " + table.Key, "DatabaseTables.SetTableForEditingArray");
        }

        private static void RequireFields(EditableTable table, params string[] fields)
        {
            string missing = fields.FirstOrDefault(field => !table.Fields.Contains(field));
            if (missing != null) throw new InvalidDataException("FAST is unavailable: field " + missing + " is missing from " + table.Key + ".");
        }

        private static void ValidateGuids(EditableTable table, string nameField, Dictionary<string, string> expected)
        {
            Dictionary<string, string[]> rows = table.RowsBy(nameField);
            foreach (KeyValuePair<string, string> item in expected)
            {
                string[] row;
                if (!rows.TryGetValue(item.Key, out row) || table.Value(row, "GUID") != item.Value)
                    throw new InvalidDataException("FAST: GUID changed for " + item.Key + ".");
            }
        }

        private static string ImportSummary(int fatal, int errors, int warnings, string log)
        {
            string[] lines = (log ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return "fatal=" + fatal + ", errors=" + errors + ", warnings=" + warnings + ". " +
                string.Join(" ", lines.Skip(Math.Max(0, lines.Length - 30)));
        }

        private static void UndoMesh(cSapModel model, MeshOperation operation, bool refresh = true)
        {
            if (!operation.Active) return;
            int areaCount = 0;
            string[] areaNames = null;
            Check(model.AreaObj.GetNameList(ref areaCount, ref areaNames), "reading areas before undo", "AreaObj.GetNameList");
            var remainingAreas = new HashSet<string>(areaNames ?? new string[0]);
            for (int i = operation.GeneratedAreas.Count - 1; i >= 0; i--)
                if (remainingAreas.Contains(operation.GeneratedAreas[i]))
                    Check(model.AreaObj.Delete(operation.GeneratedAreas[i], eItemType.Objects), "removing generated area " + operation.GeneratedAreas[i], "AreaObj.Delete");
            int pointCount = 0;
            string[] pointNames = null;
            Check(model.PointObj.GetNameList(ref pointCount, ref pointNames), "reading points before undo", "PointObj.GetNameList");
            var remainingPoints = new HashSet<string>(pointNames ?? new string[0]);
            foreach (string pointName in operation.GeneratedPoints)
            {
                if (!remainingPoints.Contains(pointName)) continue;
                Check(model.PointObj.SetSpecialPoint(pointName, true, eItemType.Objects), "preparing generated point removal " + pointName, "PointObj.SetSpecialPoint");
                Check(model.PointObj.DeleteSpecialPoint(pointName, eItemType.Objects), "removing generated point " + pointName, "PointObj.DeleteSpecialPoint");
            }
            if (operation.FastMode)
            {
                RestorePointSnapshots(model, operation.OriginalPoints);
                operation.Active = false;
                operation.Undone = true;
                if (refresh) model.View.RefreshView(0, false);
                return;
            }
            Check(model.AreaObj.ChangeName(operation.BackupArea, operation.SourceArea), "restoring the original area", "AreaObj.ChangeName");
            RestorePointSnapshots(model, operation.OriginalPoints);
            operation.Active = false;
            operation.Undone = true;
            if (refresh) model.View.RefreshView(0, false);
        }

        private static void CommitAttempts(cSapModel model, List<MeshAttemptResult> results, Action checkpoint = null)
        {
            if (checkpoint != null) checkpoint();
            if (!results.Any(result => result.Operation.FastMode))
            {
                var active = results.Select(result => result.Operation).Where(operation => operation.Active).ToList();
                var backups = new HashSet<string>(active.Select(operation => operation.BackupArea),
                    StringComparer.OrdinalIgnoreCase);
                if (backups.Count != active.Count || backups.Contains(null) || backups.Contains(""))
                    throw new InvalidDataException("Standard commit has missing or duplicate source-area backups.");
                int beforeCount = 0;
                string[] beforeNames = null;
                Check(model.AreaObj.GetNameList(ref beforeCount, ref beforeNames),
                    "checking Standard preview before commit", "AreaObj.GetNameList");
                if (!backups.IsSubsetOf(beforeNames ?? new string[0]))
                    throw new InvalidDataException("Standard preview lost a source area before Apply.");
                Check(model.SelectObj.ClearSelection(), "clearing selection before Standard commit", "SelectObj.ClearSelection");
                foreach (MeshOperation operation in active)
                    Check(model.AreaObj.SetSelected(operation.BackupArea, true, eItemType.Objects),
                        "selecting original area " + operation.BackupArea + " for Standard commit", "AreaObj.SetSelected");
                int standardDeleteCode = active.Count == 0 ? 0 : model.AreaObj.Delete("", eItemType.SelectedObjects);
                int afterCount = 0;
                string[] afterNames = null;
                Check(model.AreaObj.GetNameList(ref afterCount, ref afterNames),
                    "verifying Standard commit", "AreaObj.GetNameList");
                if (standardDeleteCode != 0 || afterCount != beforeCount - active.Count ||
                    backups.Overlaps(afterNames ?? new string[0]))
                    throw new InvalidOperationException("SAP2000 did not delete exactly the selected original areas " +
                        "in one Standard commit [API: AreaObj.Delete]: code " + standardDeleteCode + ".");
                foreach (MeshOperation operation in active) operation.Active = false;
                Check(model.SelectObj.ClearSelection(), "leaving the applied mesh unselected", "SelectObj.ClearSelection");
                return;
            }

            var prepared = new List<MeshOperation>();
            string token = Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();
            try
            {
                for (int i = 0; i < results.Count; i++)
                {
                    if (checkpoint != null) checkpoint();
                    MeshOperation operation = results[i].Operation;
                    operation.CommitBackupArea = "QTB" + token + i.ToString("D4", CultureInfo.InvariantCulture);
                    Check(model.AreaObj.ChangeName(operation.SourceArea, operation.CommitBackupArea), "preparing FAST commit", "AreaObj.ChangeName");
                    try { Check(model.AreaObj.ChangeName(operation.PrimaryGeneratedArea, operation.SourceArea), "renaming the FAST mesh area", "AreaObj.ChangeName"); }
                    catch (Exception renameFailure)
                    {
                        if (checkpoint != null) checkpoint();
                        try { Check(model.AreaObj.ChangeName(operation.CommitBackupArea, operation.SourceArea), "rolling back the current FAST source area", "AreaObj.ChangeName"); }
                        catch (Exception rollback)
                        {
                            throw new InvalidOperationException(renameFailure.Message + " FAST commit rollback failed: " + rollback.Message, renameFailure);
                        }
                        operation.CommitBackupArea = null;
                        throw;
                    }
                    prepared.Add(operation);
                }
            }
            catch (Exception exception)
            {
                if (checkpoint != null) checkpoint();
                Exception rollbackFailure = null;
                for (int i = prepared.Count - 1; i >= 0; i--)
                {
                    MeshOperation operation = prepared[i];
                    try
                    {
                        Check(model.AreaObj.ChangeName(operation.SourceArea, operation.PrimaryGeneratedArea), "rolling back FAST mesh name", "AreaObj.ChangeName");
                        Check(model.AreaObj.ChangeName(operation.CommitBackupArea, operation.SourceArea), "rolling back FAST source area", "AreaObj.ChangeName");
                        operation.CommitBackupArea = null;
                    }
                    catch (Exception rollback) { if (rollbackFailure == null) rollbackFailure = rollback; }
                }
                if (rollbackFailure != null)
                    throw new InvalidOperationException(exception.Message + " FAST commit rollback failed: " + rollbackFailure.Message, exception);
                throw;
            }

            try
            {
                Check(model.SelectObj.ClearSelection(), "clearing selection for FAST commit", "SelectObj.ClearSelection");
                foreach (MeshOperation operation in prepared)
                    Check(model.AreaObj.SetSelected(operation.CommitBackupArea, true, eItemType.Objects), "selecting FAST commit backup", "AreaObj.SetSelected");
            }
            catch (Exception exception)
            {
                if (checkpoint != null) checkpoint();
                Exception rollbackFailure = RollBackFastCommitNames(model, prepared);
                if (rollbackFailure != null)
                    throw new InvalidOperationException(exception.Message + " FAST commit rollback failed: " + rollbackFailure.Message, exception);
                throw;
            }
            if (checkpoint != null) checkpoint();
            int deleteResult = model.AreaObj.Delete("", eItemType.SelectedObjects);
            int remainingCount = 0;
            string[] remainingNames = null;
            Check(model.AreaObj.GetNameList(ref remainingCount, ref remainingNames), "verifying FAST commit cleanup", "AreaObj.GetNameList");
            var remaining = new HashSet<string>(remainingNames ?? new string[0]);
            List<MeshOperation> retainedBackups = prepared.Where(operation => remaining.Contains(operation.CommitBackupArea)).ToList();
            if (deleteResult != 0 || retainedBackups.Count != 0)
            {
                if (retainedBackups.Count == prepared.Count)
                {
                    if (checkpoint != null) checkpoint();
                    Exception rollbackFailure = RollBackFastCommitNames(model, prepared);
                    if (rollbackFailure != null)
                        throw new InvalidOperationException("SAP2000 could not remove the original FAST areas and rollback failed: " + rollbackFailure.Message);
                }
                throw new InvalidOperationException("SAP2000 error while removing original FAST areas " +
                    "[API: AreaObj.Delete]: code " + deleteResult +
                    ". The generated meshes retain their final names; inspect these backup areas: " +
                    string.Join(", ", retainedBackups.Select(operation => operation.CommitBackupArea)) + ".");
            }
            foreach (MeshOperation operation in prepared)
            {
                operation.CommitBackupArea = null;
                operation.Active = false;
            }
            Check(model.SelectObj.ClearSelection(), "leaving the applied FAST mesh unselected", "SelectObj.ClearSelection");
        }

        private static bool IsNumericObjectName(string name)
        {
            long number;
            return long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out number);
        }

        private static long MaximumNumericName(IEnumerable<string> names)
        {
            long maximum = 0;
            foreach (string name in names)
            {
                long value;
                if (long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out value))
                    maximum = Math.Max(maximum, value);
            }
            return maximum;
        }

        private static string NextNumericName(ref long maximum)
        {
            if (maximum == long.MaxValue)
                throw new InvalidOperationException("No numeric object names remain for the generated mesh.");
            return (++maximum).ToString(CultureInfo.InvariantCulture);
        }

        private static void CommitNumericMeshes(cSapModel model, List<MeshAttemptResult> results,
            Action checkpoint)
        {
            var sources = new HashSet<string>(results.Select(item => item.Operation.SourceArea),
                StringComparer.OrdinalIgnoreCase);
            var generated = new HashSet<string>(results.SelectMany(item => item.Operation.GeneratedAreas),
                StringComparer.OrdinalIgnoreCase);
            if (sources.Count != results.Count || sources.Overlaps(generated) ||
                results.Any(item => !item.Operation.FastMode || !item.Operation.Active ||
                    item.Operation.GeneratedAreas.Count == 0 ||
                    item.Operation.GeneratedAreas.Any(name => !IsNumericObjectName(name)) ||
                    item.Operation.GeneratedPoints.Any(name => !IsNumericObjectName(name))))
                throw new InvalidDataException("The generated numeric FAST mesh cannot be committed safely.");
            int beforeCount = 0;
            string[] before = null;
            Check(model.AreaObj.GetNameList(ref beforeCount, ref before),
                "listing pre-commit areas", "AreaObj.GetNameList");
            var present = new HashSet<string>(before ?? new string[0], StringComparer.OrdinalIgnoreCase);
            if (present.Count != beforeCount || !sources.IsSubsetOf(present) ||
                !generated.IsSubsetOf(present))
                throw new InvalidDataException("The numeric preview lost an original or generated area.");
            Check(model.SelectObj.ClearSelection(), "clearing numeric preview selection", "SelectObj.ClearSelection");
            foreach (string source in sources)
            {
                if (checkpoint != null) checkpoint();
                Check(model.AreaObj.SetSelected(source, true, eItemType.Objects),
                    "selecting original area " + source, "AreaObj.SetSelected");
            }
            if (checkpoint != null) checkpoint();
            Check(model.AreaObj.Delete("", eItemType.SelectedObjects),
                "deleting original areas after FAST insertion", "AreaObj.Delete");
            int afterCount = 0;
            string[] after = null;
            Check(model.AreaObj.GetNameList(ref afterCount, ref after),
                "verifying the committed numeric mesh", "AreaObj.GetNameList");
            var remaining = new HashSet<string>(after ?? new string[0], StringComparer.OrdinalIgnoreCase);
            if (remaining.Count != afterCount || afterCount != beforeCount - sources.Count ||
                sources.Overlaps(remaining) || !generated.IsSubsetOf(remaining))
                throw new InvalidDataException("The numeric commit left original areas or lost generated areas.");
            foreach (MeshAttemptResult item in results) item.Operation.Active = false;
            Check(model.SelectObj.ClearSelection(), "clearing selection after numeric commit", "SelectObj.ClearSelection");
        }

        private static void RenameGeneratedPointsAfterApply(cSapModel model, List<MeshAttemptResult> results,
            Action checkpoint = null)
        {
            if (checkpoint != null) checkpoint();
            string[] generated = results.SelectMany(result => result.Operation.GeneratedPoints
                .Where(name => result.Operation.FastMode || !IsNumericObjectName(name)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (generated.Length == 0) return;

            int count = 0;
            string[] names = null;
            Check(model.PointObj.GetNameList(ref count, ref names),
                "reading joint names before numeric numbering", "PointObj.GetNameList");
            var used = new HashSet<string>(names ?? new string[0], StringComparer.OrdinalIgnoreCase);
            if (count != used.Count || generated.Any(name => !used.Contains(name)))
                throw new InvalidDataException("The generated FAST joints changed before numeric numbering.");

            long maximum = 0;
            foreach (string name in used)
            {
                long number;
                if (long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out number))
                    maximum = Math.Max(maximum, number);
            }
            if (maximum > long.MaxValue - generated.Length)
                throw new InvalidOperationException("No numeric joint names are available after the existing names.");

            var renamed = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (string original in generated)
                {
                    if (checkpoint != null) checkpoint();
                    string numeric = (++maximum).ToString(CultureInfo.InvariantCulture);
                    if (used.Contains(numeric))
                        throw new InvalidDataException("Numeric joint name already in use: " + numeric);
                    Check(model.PointObj.ChangeName(original, numeric),
                        "numbering generated FAST joint " + original, "PointObj.ChangeName");
                    renamed.Add(new KeyValuePair<string, string>(original, numeric));
                    used.Remove(original);
                    used.Add(numeric);
                }

                int verifiedCount = 0;
                string[] verifiedNames = null;
                Check(model.PointObj.GetNameList(ref verifiedCount, ref verifiedNames),
                    "verifying numeric joint names", "PointObj.GetNameList");
                if (verifiedCount != count || !used.SetEquals(verifiedNames ?? new string[0]))
                    throw new InvalidDataException("SAP2000 did not retain all joints after numeric numbering.");
            }
            catch (Exception error)
            {
                if (checkpoint != null) checkpoint();
                Exception rollbackFailure = null;
                for (int i = renamed.Count - 1; i >= 0; i--)
                    try
                    {
                        Check(model.PointObj.ChangeName(renamed[i].Value, renamed[i].Key),
                            "restoring generated FAST joint name", "PointObj.ChangeName");
                    }
                    catch (Exception rollback) { if (rollbackFailure == null) rollbackFailure = rollback; }
                if (rollbackFailure != null)
                    throw new InvalidOperationException(error.Message +
                        " Numeric joint rollback failed: " + rollbackFailure.Message, error);
                throw;
            }

            var replacements = renamed.ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            foreach (MeshAttemptResult result in results)
            {
                List<string> points = result.Operation.GeneratedPoints;
                for (int i = 0; i < points.Count; i++)
                {
                    string numeric;
                    if (replacements.TryGetValue(points[i], out numeric)) points[i] = numeric;
                }
            }
            Console.WriteLine("mesh-applied-numeric-joints=" + renamed.Count);
        }

        private static void RenameGeneratedAreasAfterApply(cSapModel model,
            List<MeshAttemptResult> results, List<PreparedMesh> meshes, Action checkpoint = null)
        {
            if (checkpoint != null) checkpoint();
            if (results.Count != meshes.Count)
                throw new InvalidDataException("The generated mesh operations do not match their source areas.");
            string[] generated = results.SelectMany(result => result.Operation.GeneratedAreas
                .Where(name => result.Operation.FastMode
                    ? !string.Equals(name, result.Operation.PrimaryGeneratedArea,
                        StringComparison.OrdinalIgnoreCase)
                    : !IsNumericObjectName(name)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (generated.Length == 0) return;

            int count = 0;
            string[] names = null;
            Check(model.AreaObj.GetNameList(ref count, ref names),
                "reading area names before numeric numbering", "AreaObj.GetNameList");
            var used = new HashSet<string>(names ?? new string[0], StringComparer.OrdinalIgnoreCase);
            if (count != used.Count || generated.Any(name => !used.Contains(name)))
                throw new InvalidDataException("The generated FAST areas changed before numeric numbering.");

            long maximum = 0;
            foreach (string name in used)
            {
                long number;
                if (long.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out number))
                    maximum = Math.Max(maximum, number);
            }
            if (maximum > long.MaxValue - generated.Length)
                throw new InvalidOperationException("No numeric area names are available after the existing names.");

            var renamed = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (string original in generated)
                {
                    if (checkpoint != null) checkpoint();
                    string numeric = (++maximum).ToString(CultureInfo.InvariantCulture);
                    if (used.Contains(numeric))
                        throw new InvalidDataException("Numeric area name already in use: " + numeric);
                    Check(model.AreaObj.ChangeName(original, numeric),
                        "numbering generated FAST area " + original, "AreaObj.ChangeName");
                    renamed.Add(new KeyValuePair<string, string>(original, numeric));
                    used.Remove(original);
                    used.Add(numeric);
                }

                int verifiedCount = 0;
                string[] verifiedNames = null;
                Check(model.AreaObj.GetNameList(ref verifiedCount, ref verifiedNames),
                    "verifying numeric area names", "AreaObj.GetNameList");
                if (verifiedCount != count || !used.SetEquals(verifiedNames ?? new string[0]))
                    throw new InvalidDataException("SAP2000 did not retain all areas after numeric numbering.");
            }
            catch (Exception error)
            {
                if (checkpoint != null) checkpoint();
                Exception rollbackFailure = null;
                for (int i = renamed.Count - 1; i >= 0; i--)
                    try
                    {
                        Check(model.AreaObj.ChangeName(renamed[i].Value, renamed[i].Key),
                            "restoring generated FAST area name", "AreaObj.ChangeName");
                    }
                    catch (Exception rollback) { if (rollbackFailure == null) rollbackFailure = rollback; }
                if (rollbackFailure != null)
                    throw new InvalidOperationException(error.Message +
                        " Numeric area rollback failed: " + rollbackFailure.Message, error);
                throw;
            }

            var replacements = renamed.ToDictionary(pair => pair.Key, pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < results.Count; index++)
            {
                List<string> areas = results[index].Operation.GeneratedAreas;
                for (int i = 0; i < areas.Count; i++)
                {
                    string numeric;
                    if (!replacements.TryGetValue(areas[i], out numeric)) continue;
                    meshes[index].Loads.RemapChildName(areas[i], numeric);
                    areas[i] = numeric;
                }
            }
            Console.WriteLine("mesh-applied-numeric-areas=" + renamed.Count);
        }

        private static MeshInventory VerifyAppliedObjectNames(cSapModel model,
            List<MeshAttemptResult> results, MeshInventory preview)
        {
            int pointCount = 0, areaCount = 0, frameCount = 0, linkCount = 0;
            int unchangedQuads = 0, unchangedTriangles = 0, unchangedPolygons = 0;
            foreach (MeshAttemptResult result in results.Where(item => item.Operation.Unchanged))
            {
                int corners = 0;
                string[] cornersNames = null;
                Check(model.AreaObj.GetPoints(result.Operation.SourceArea, ref corners, ref cornersNames),
                    "checking unchanged source area after Apply", "AreaObj.GetPoints");
                if (corners == 4) unchangedQuads++;
                else if (corners == 3) unchangedTriangles++;
                else if (corners > 4) unchangedPolygons++;
                else throw new InvalidDataException("An unchanged source area lost its corners during Apply.");
            }
            int expectedAreas = preview.Areas + unchangedQuads + unchangedTriangles + unchangedPolygons;
            string[] pointNames = null, areaNames = null, frameNames = null, linkNames = null;
            Check(model.PointObj.GetNameList(ref pointCount, ref pointNames),
                "checking committed joint names", "PointObj.GetNameList");
            Check(model.AreaObj.GetNameList(ref areaCount, ref areaNames),
                "checking committed area names", "AreaObj.GetNameList");
            Check(model.FrameObj.GetNameList(ref frameCount, ref frameNames),
                "checking committed frames", "FrameObj.GetNameList");
            Check(model.LinkObj.GetNameList(ref linkCount, ref linkNames),
                "checking committed links", "LinkObj.GetNameList");
            var points = new HashSet<string>(pointNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var areas = new HashSet<string>(areaNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
            if (points.Count != pointCount || areas.Count != areaCount ||
                areaCount != expectedAreas ||
                preview.Quads + preview.Triangles + preview.Polygons +
                    unchangedQuads + unchangedTriangles + unchangedPolygons != areaCount ||
                frameCount != preview.Frames || linkCount != preview.Links ||
                results.SelectMany(result => result.Operation.GeneratedPoints)
                    .Any(name => !IsNumericObjectName(name) || !points.Contains(name)) ||
                results.SelectMany(result => result.Operation.GeneratedAreas.Where(name =>
                    !result.Operation.FastMode || !string.Equals(name,
                        result.Operation.PrimaryGeneratedArea, StringComparison.OrdinalIgnoreCase)))
                    .Any(name => !IsNumericObjectName(name) || !areas.Contains(name)) ||
                results.Any(result => !areas.Contains(result.Operation.SourceArea) && result.Operation.FastMode ||
                    !string.IsNullOrEmpty(result.Operation.BackupArea) &&
                        areas.Contains(result.Operation.BackupArea) ||
                    !string.IsNullOrEmpty(result.Operation.CommitBackupArea) &&
                        areas.Contains(result.Operation.CommitBackupArea)))
                throw new InvalidDataException("Applied mesh still has a temporary generated object name or a missing object.");
            return new MeshInventory(areaCount, preview.Quads + unchangedQuads,
                preview.Triangles + unchangedTriangles, preview.Polygons + unchangedPolygons,
                frameCount, linkCount, pointCount);
        }

        private static Exception RollBackFastCommitNames(cSapModel model, List<MeshOperation> operations)
        {
            Exception failure = null;
            for (int i = operations.Count - 1; i >= 0; i--)
            {
                MeshOperation operation = operations[i];
                try
                {
                    Check(model.AreaObj.ChangeName(operation.SourceArea, operation.PrimaryGeneratedArea), "rolling back FAST mesh name", "AreaObj.ChangeName");
                    Check(model.AreaObj.ChangeName(operation.CommitBackupArea, operation.SourceArea), "rolling back FAST source area", "AreaObj.ChangeName");
                    operation.CommitBackupArea = null;
                }
                catch (Exception exception) { if (failure == null) failure = exception; }
            }
            return failure;
        }

        private static PointSnapshot[] CapturePointSnapshots(cSapModel model, IEnumerable<string> pointNames)
        {
            var snapshots = new List<PointSnapshot>();
            foreach (string name in pointNames.Distinct())
            {
                bool special = false;
                int groupCount = 0;
                string[] groups = null;
                model.PointObj.GetSpecialPoint(name, ref special);
                model.PointObj.GetGroupAssign(name, ref groupCount, ref groups);
                snapshots.Add(new PointSnapshot(name, Coordinate(model, name), special, groups ?? new string[0]));
            }
            return snapshots.ToArray();
        }

        private static void RestorePointSnapshots(cSapModel model, PointSnapshot[] snapshots)
        {
            int pointCount = 0;
            string[] pointNames = null;
            model.PointObj.GetNameList(ref pointCount, ref pointNames);
            var existing = new HashSet<string>(pointNames ?? new string[0]);
            foreach (PointSnapshot snapshot in snapshots)
            {
                if (existing.Contains(snapshot.Name)) continue;
                string restoredName = "";
                Check(model.PointObj.AddCartesian(snapshot.Coordinates[0], snapshot.Coordinates[1], snapshot.Coordinates[2],
                    ref restoredName, snapshot.Name, "Global", snapshot.Special, 0), "restoring point " + snapshot.Name, "PointObj.AddCartesian");
                foreach (string group in snapshot.Groups)
                    model.PointObj.SetGroupAssign(restoredName, group, false, eItemType.Objects);
            }
        }

        private static AreaLoads CaptureAreaLoads(cSapModel model, string areaName)
        {
            return CaptureAreaLoads(model, areaName, null);
        }

        private static void CaptureDesignProcedures(cSapModel model, List<AreaContext> contexts)
        {
            try
            {
                EditableTable procedures = ReadEditableTable(model, "Area Design Procedures");
                RequireFields(procedures, "Area", "DesignProc");
                Dictionary<string, string[]> rows = procedures.RowsBy("Area");
                if (contexts.Any(context => !rows.ContainsKey(context.Name)))
                {
                    // After SAP's Interactive Database Editor has displayed Joint
                    // Coordinates, the editing getter can return zero records
                    // even though the saved model still has design procedures.
                    EditableTable displayed = ReadDisplayTable(model, "Area Design Procedures");
                    RequireFields(displayed, "Area", "DesignProc");
                    Dictionary<string, string[]> visible = displayed.RowsBy("Area");
                    if (contexts.Any(context => !visible.ContainsKey(context.Name)) ||
                        rows.Any(item => visible.ContainsKey(item.Key) &&
                            procedures.Value(item.Value, "DesignProc") !=
                                displayed.Value(visible[item.Key], "DesignProc")))
                        throw new InvalidDataException("SAP2000 returned conflicting or incomplete area design procedures.");
                    procedures = displayed;
                    rows = visible;
                    Console.WriteLine("mesh-design-procedures=display-api-fallback rows=" + displayed.RecordCount);
                }
                foreach (AreaContext context in contexts)
                {
                    string[] row;
                    if (rows.TryGetValue(context.Name, out row))
                        context.DesignProcedure = procedures.Value(row, "DesignProc");
                }
            }
            catch (Exception error) when ((error is InvalidOperationException &&
                (error.Message.Contains("code -96") || error.Message.Contains("code 1"))) ||
                error is InvalidDataException)
            {
                foreach (AreaContext context in contexts) context.DesignProcedureUnavailable = true;
            }
        }

        private static bool TransferAreaDesignProceduresOnce(cSapModel model,
            List<PreparedMesh> prepared, MeshOptionsDialog progress)
        {
            var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (PreparedMesh source in prepared)
            {
                if (string.IsNullOrEmpty(source.Loads.DesignProcedure)) continue;
                foreach (string child in source.Loads.ChildAreaNames)
                {
                    if (expected.ContainsKey(child))
                        throw new InvalidDataException("Two sources claim the same generated area " + child + ".");
                    expected.Add(child, source.Loads.DesignProcedure);
                }
            }
            if (expected.Count == 0) return false;
            try
            {
                EditableTable table = ReadEditableTable(model, "Area Design Procedures");
                RequireFields(table, "Area", "DesignProc");
                int areaIndex = Array.IndexOf(table.Fields, "Area");
                int procedureIndex = Array.IndexOf(table.Fields, "DesignProc");
                int areaCount = 0;
                string[] names = null;
                Check(model.AreaObj.GetNameList(ref areaCount, ref names),
                    "checking generated areas before design transfer", "AreaObj.GetNameList");
                var live = new HashSet<string>(names ?? new string[0], StringComparer.OrdinalIgnoreCase);
                if (live.Count != areaCount || expected.Keys.Any(child => !live.Contains(child)))
                    throw new InvalidDataException("A generated area is missing before design-procedure transfer.");
                if (table.RecordCount != live.Count)
                {
                    EditableTable displayed = ReadDisplayTable(model, "Area Design Procedures");
                    RequireFields(displayed, "Area", "DesignProc");
                    Dictionary<string, string[]> editableRows = table.RowsBy("Area");
                    Dictionary<string, string[]> visibleRows = displayed.RowsBy("Area");
                    if (!displayed.Fields.SequenceEqual(table.Fields) ||
                        displayed.RecordCount != live.Count || visibleRows.Count != live.Count ||
                        !new HashSet<string>(visibleRows.Keys, StringComparer.OrdinalIgnoreCase).SetEquals(live) ||
                        editableRows.Any(item => !visibleRows.ContainsKey(item.Key) ||
                            table.Value(item.Value, "DesignProc") !=
                                displayed.Value(visibleRows[item.Key], "DesignProc")))
                        throw new InvalidDataException("SAP2000 returned conflicting area design procedures before transfer.");
                    table = displayed;
                    areaIndex = Array.IndexOf(table.Fields, "Area");
                    procedureIndex = Array.IndexOf(table.Fields, "DesignProc");
                    Console.WriteLine("mesh-design-transfer=display-api-staging rows=" + displayed.RecordCount);
                }
                Dictionary<string, string[]> previous = table.RowsBy("Area");
                EditableTable sectionsBefore = ReadDisplayTable(model, "Area Section Assignments");
                RequireFields(sectionsBefore, "Area", "Section");
                if (sectionsBefore.RecordCount != live.Count ||
                    !new HashSet<string>(sectionsBefore.Values("Area"), StringComparer.OrdinalIgnoreCase).SetEquals(live))
                    throw new InvalidDataException("The displayed area sections omit generated areas before design transfer.");
                // SAP2000 27.1 keeps omitted design rows in a trivial one-row
                // round trip, but staging a changed child row reset an omitted
                // QTUNDO source from "No Design" to its default procedure.
                // Carry every live row in ONE import so Undo remains faithful.
                bool changed = false;
                for (int row = table.RecordCount - 1; row >= 0; row--)
                {
                    int offset = row * table.Fields.Length;
                    if (live.Contains(table.Data[offset + areaIndex])) continue;
                    table.Data.RemoveRange(offset, table.Fields.Length);
                    changed = true;
                }
                var pending = new HashSet<string>(expected.Keys, StringComparer.OrdinalIgnoreCase);
                for (int row = 0; row < table.RecordCount; row++)
                {
                    int offset = row * table.Fields.Length;
                    string area = table.Data[offset + areaIndex];
                    if (!pending.Remove(area)) continue;
                    if (table.Data[offset + procedureIndex] == expected[area]) continue;
                    table.Data[offset + procedureIndex] = expected[area];
                    changed = true;
                }
                foreach (string child in pending)
                {
                    table.Append(new Dictionary<string, string> {
                        { "Area", child }, { "DesignProc", expected[child] }
                    });
                    changed = true;
                }
                if (!changed) return false;
                WriteEditableTable(model, table);
                int fatal = 0, errors = 0, warnings = 0, information = 0;
                string log = "";
                if (!progress.CheckOptionalTransfer(model.DatabaseTables.ApplyEditedTables(true,
                    ref fatal, ref errors, ref warnings, ref information, ref log),
                    "selected areas", expected.Count.ToString(CultureInfo.InvariantCulture),
                    "area design procedures", "DatabaseTables.ApplyEditedTables"))
                {
                    model.DatabaseTables.CancelTableEditing();
                    return false;
                }
                if (fatal != 0 || errors != 0 || warnings != 0)
                    throw new InvalidOperationException("Area design procedure batch import failed: " +
                        ImportSummary(fatal, errors, warnings, log));
                EditableTable verified = ReadEditableTable(model, "Area Design Procedures");
                Dictionary<string, string[]> after = verified.RowsBy("Area");
                if (expected.Any(target => !after.ContainsKey(target.Key) ||
                    verified.Value(after[target.Key], "DesignProc") != target.Value))
                {
                    // After Show Tables, SAP2000 can accept every edited row
                    // without assigning Concrete Shell Design to the children.
                    // Trust only a complete, independent display-table read;
                    // no other design value or section is allowed to change.
                    EditableTable displayed = ReadDisplayTable(model, "Area Design Procedures");
                    RequireFields(displayed, "Area", "DesignProc");
                    Dictionary<string, string[]> displayedRows = displayed.RowsBy("Area");
                    EditableTable sectionsAfter = ReadDisplayTable(model, "Area Section Assignments");
                    Dictionary<string, string[]> beforeSections = sectionsBefore.RowsBy("Area");
                    Dictionary<string, string[]> afterSections = sectionsAfter.RowsBy("Area");
                    if (displayed.RecordCount == live.Count && displayedRows.Count == live.Count &&
                        displayedRows.Keys.All(live.Contains) &&
                        expected.Values.All(value => value == "Concrete Shell Design") &&
                        expected.Any(target => displayedRows.ContainsKey(target.Key) &&
                            displayed.Value(displayedRows[target.Key], "DesignProc") == "No Design") &&
                        expected.All(target => displayedRows.ContainsKey(target.Key) &&
                            (displayed.Value(displayedRows[target.Key], "DesignProc") == target.Value ||
                             displayed.Value(displayedRows[target.Key], "DesignProc") == "No Design")) &&
                        previous.All(retained => !live.Contains(retained.Key) ||
                            expected.ContainsKey(retained.Key) ||
                            retained.Key.StartsWith("QTUNDO", StringComparison.OrdinalIgnoreCase) ||
                            displayedRows.ContainsKey(retained.Key) &&
                            displayed.Value(displayedRows[retained.Key], "DesignProc") ==
                                table.Value(retained.Value, "DesignProc")) &&
                        sectionsAfter.Fields.SequenceEqual(sectionsBefore.Fields) &&
                        sectionsAfter.RecordCount == live.Count &&
                        afterSections.Count == live.Count &&
                        afterSections.All(item => beforeSections.ContainsKey(item.Key) &&
                            beforeSections[item.Key].SequenceEqual(item.Value)))
                    {
                        progress.ShowDesignTransferLossWarning();
                        return true;
                    }
                }
                foreach (KeyValuePair<string, string> target in expected)
                {
                    string[] row;
                    if (!after.TryGetValue(target.Key, out row) || verified.Value(row, "DesignProc") != target.Value)
                        throw new InvalidDataException("Area design procedure was not transferred to " + target.Key + ".");
                }
                foreach (KeyValuePair<string, string[]> retained in previous)
                {
                    if (expected.ContainsKey(retained.Key) || !live.Contains(retained.Key)) continue;
                    string[] row;
                    string desired = table.Value(retained.Value, "DesignProc");
                    if (!after.TryGetValue(retained.Key, out row))
                        throw new InvalidDataException("An unrelated area design procedure disappeared: " + retained.Key +
                            " (expected " + desired + ").");
                    string actual = verified.Value(row, "DesignProc");
                    if (actual != desired)
                        throw new InvalidDataException("An unrelated area design procedure changed: " + retained.Key +
                            " expected " + desired + " actual " + actual + ".");
                }
                return false;
            }
            catch (InvalidOperationException error) when (progress.WarnOptionalApiFailure(error,
                "selected areas", expected.Count.ToString(CultureInfo.InvariantCulture),
                "area design procedure batch transfer or verification"))
            {
                model.DatabaseTables.CancelTableEditing();
            }
            return false;
        }

        private static AreaLoads CaptureAreaLoads(cSapModel model, string areaName, List<AreaContext> contexts)
        {
            var loads = new AreaLoads(areaName);
            AreaContext capturedContext = contexts == null ? null : contexts.FirstOrDefault(context =>
                string.Equals(context.Name, areaName, StringComparison.OrdinalIgnoreCase));
            if (capturedContext != null)
            {
                loads.DesignProcedure = capturedContext.DesignProcedure;
                if (capturedContext.DesignProcedureUnavailable)
                    loads.UnpreservedDesignTables.Add("Area Design Procedures");
            }
            else try
            {
                EditableTable procedures = ReadEditableTable(model, "Area Design Procedures");
                RequireFields(procedures, "Area", "DesignProc");
                string[] procedureRow;
                if (procedures.RowsBy("Area").TryGetValue(areaName, out procedureRow))
                    loads.DesignProcedure = procedures.Value(procedureRow, "DesignProc");
            }
            catch (Exception error) when ((error is InvalidOperationException && error.Message.Contains("code -96")) ||
                error is InvalidDataException) { loads.UnpreservedDesignTables.Add("Area Design Procedures"); }
            int count = 0;
            string[] names = null, patterns = null, systems = null, patternNames = null;
            double[] x = null, y = null, z = null, values = null;
            int[] components = null, directions = null, types = null, distributionTypes = null;

            Check(model.AreaObj.GetLoadGravity(areaName, ref count, ref names, ref patterns, ref systems,
                ref x, ref y, ref z, eItemType.Objects), "reading gravity loads", "AreaObj.GetLoadGravity");
            for (int i = 0; i < count; i++)
            {
                string pattern = patterns[i], system = systems[i]; double gx = x[i], gy = y[i], gz = z[i];
                loads.Add(target => Check(model.AreaObj.SetLoadGravity(target, pattern, gx, gy, gz, false, system, eItemType.Objects), "transferring gravity load", "AreaObj.SetLoadGravity"));
            }

            count = 0; names = patterns = patternNames = null; values = null;
            Check(model.AreaObj.GetLoadPorePressure(areaName, ref count, ref names, ref patterns, ref values,
                ref patternNames, eItemType.Objects), "reading pore pressure load", "AreaObj.GetLoadPorePressure");
            for (int i = 0; i < count; i++)
            {
                string pattern = patterns[i], valuePattern = patternNames[i]; double value = values[i];
                if (!string.IsNullOrEmpty(valuePattern)) loads.RequirePattern(valuePattern);
                loads.Add(target => Check(model.AreaObj.SetLoadPorePressure(target, pattern, value, valuePattern, false, eItemType.Objects), "transferring pore pressure load", "AreaObj.SetLoadPorePressure"));
            }

            count = 0; names = patterns = null; values = null;
            Check(model.AreaObj.GetLoadRotate(areaName, ref count, ref names, ref patterns, ref values, eItemType.Objects), "reading rotational load", "AreaObj.GetLoadRotate");
            for (int i = 0; i < count; i++)
            {
                string pattern = patterns[i]; double value = values[i];
                loads.Add(target => Check(model.AreaObj.SetLoadRotate(target, pattern, value, false, eItemType.Objects), "transferring rotational load", "AreaObj.SetLoadRotate"));
            }

            count = 0; names = patterns = patternNames = null; components = null; values = null;
            Check(model.AreaObj.GetLoadStrain(areaName, ref count, ref names, ref patterns, ref components, ref values,
                ref patternNames, eItemType.Objects), "reading strain loads", "AreaObj.GetLoadStrain");
            for (int i = 0; i < count; i++)
            {
                string pattern = patterns[i], valuePattern = patternNames[i]; int component = components[i]; double value = values[i];
                if (!string.IsNullOrEmpty(valuePattern)) loads.RequirePattern(valuePattern);
                loads.Add(target => Check(model.AreaObj.SetLoadStrain(target, pattern, component, value, false, valuePattern, eItemType.Objects), "transferring strain load", "AreaObj.SetLoadStrain"));
            }

            count = 0; names = patterns = patternNames = null; components = null; values = null;
            Check(model.AreaObj.GetLoadSurfacePressure(areaName, ref count, ref names, ref patterns, ref components,
                ref values, ref patternNames, eItemType.Objects), "reading surface pressure loads", "AreaObj.GetLoadSurfacePressure");
            for (int i = 0; i < count; i++)
            {
                string pattern = patterns[i], valuePattern = patternNames[i]; int face = components[i]; double value = values[i];
                if (!string.IsNullOrEmpty(valuePattern)) loads.RequirePattern(valuePattern);
                if (face > 0)
                    CaptureEdgeAssignment(loads, model, areaName, face, "surface pressure",
                        (target, childFace) => Check(model.AreaObj.SetLoadSurfacePressure(target,
                            pattern, childFace, value, "", false, eItemType.Objects), "transferring edge pressure", "AreaObj.SetLoadSurfacePressure"));
                else loads.Add(target => Check(model.AreaObj.SetLoadSurfacePressure(target, pattern, face, value,
                    "", false, eItemType.Objects), "transferring surface pressure load", "AreaObj.SetLoadSurfacePressure"));
            }

            count = 0; names = patterns = patternNames = null; types = null; values = null;
            Check(model.AreaObj.GetLoadTemperature(areaName, ref count, ref names, ref patterns, ref types, ref values,
                ref patternNames, eItemType.Objects), "reading temperature loads", "AreaObj.GetLoadTemperature");
            for (int i = 0; i < count; i++)
            {
                string pattern = patterns[i], valuePattern = patternNames[i]; int loadType = types[i]; double value = values[i];
                if (!string.IsNullOrEmpty(valuePattern)) loads.RequirePattern(valuePattern);
                loads.Add(target => Check(model.AreaObj.SetLoadTemperature(target, pattern, loadType, value, valuePattern, false, eItemType.Objects), "transferring temperature load", "AreaObj.SetLoadTemperature"));
            }

            count = 0; names = patterns = systems = null; directions = null; values = null;
            Check(model.AreaObj.GetLoadUniform(areaName, ref count, ref names, ref patterns, ref systems, ref directions,
                ref values, eItemType.Objects), "reading uniform loads", "AreaObj.GetLoadUniform");
            for (int i = 0; i < count; i++)
            {
                string pattern = patterns[i], system = systems[i]; int direction = directions[i]; double value = values[i];
                loads.Add(target => Check(model.AreaObj.SetLoadUniform(target, pattern, value, direction, false, system, eItemType.Objects), "transferring uniform load", "AreaObj.SetLoadUniform"));
            }

            count = 0; names = patterns = systems = null; directions = distributionTypes = null; values = null;
            Check(model.AreaObj.GetLoadUniformToFrame(areaName, ref count, ref names, ref patterns, ref systems,
                ref directions, ref values, ref distributionTypes, eItemType.Objects), "reading uniform-to-frame loads", "AreaObj.GetLoadUniformToFrame");
            if (count > 0)
                throw new InvalidOperationException("Area " + areaName + " has a uniform-to-frame load. Its frame distribution " +
                    "cannot be preserved by assigning the full source load to every child area.");

            count = 0; names = patterns = null; types = distributionTypes = null; values = null;
            Check(model.AreaObj.GetLoadWindPressure_1(areaName, ref count, ref names, ref patterns, ref types,
                ref values, ref distributionTypes, eItemType.Objects), "reading wind pressure loads", "AreaObj.GetLoadWindPressure_1");
            for (int i = 0; i < count; i++)
            {
                string pattern = patterns[i]; int loadType = types[i], distribution = distributionTypes[i]; double coefficient = values[i];
                loads.Add(target => Check(model.AreaObj.SetLoadWindPressure_1(target, pattern, loadType, coefficient, distribution, eItemType.Objects), "transferring wind pressure load", "AreaObj.SetLoadWindPressure_1"));
            }

            int groupCount = 0;
            string[] groups = null;
            Check(model.AreaObj.GetGroupAssign(areaName, ref groupCount, ref groups), "reading area groups", "AreaObj.GetGroupAssign");
            if (groups == null && groupCount != 0) throw new InvalidDataException("Missing area group names for " + areaName);
            foreach (string group in groups ?? new string[0])
            {
                if (string.Equals(group, "ALL", StringComparison.OrdinalIgnoreCase)) continue;
                string captured = group;
                loads.Add(target => Check(model.AreaObj.SetGroupAssign(target, captured, false, eItemType.Objects),
                    "transferring area group " + captured, "AreaObj.SetGroupAssign"));
            }

            int springCount = 0;
            int[] springTypes = null, simpleTypes = null, faces = null, axisTypes = null, springDirections = null;
            double[] stiffness = null, vecX = null, vecY = null, vecZ = null, angles = null;
            string[] springLinks = null, springSystems = null;
            bool[] outward = null;
            Check(model.AreaObj.GetSpring(areaName, ref springCount, ref springTypes, ref stiffness,
                ref simpleTypes, ref springLinks, ref faces, ref axisTypes, ref springDirections,
                ref outward, ref vecX, ref vecY, ref vecZ, ref springSystems, ref angles),
                "reading area springs", "AreaObj.GetSpring");
            for (int i = 0; i < springCount; i++)
            {
                int springType = springTypes[i], simple = simpleTypes[i], face = faces[i];
                int axisType = axisTypes[i], direction = springDirections[i];
                double k = stiffness[i], angle = angles[i];
                string link = springLinks[i], system = springSystems[i];
                bool normal = outward[i], replace = i == 0;
                double[] vector = { vecX[i], vecY[i], vecZ[i] };
                Action<string, int> setSpring = (target, childFace) =>
                {
                    double[] copied = (double[])vector.Clone();
                    Check(model.AreaObj.SetSpring(target, springType, k, simple, link, childFace, axisType,
                        direction, normal, ref copied, angle, replace, system, eItemType.Objects),
                        "transferring area spring", "AreaObj.SetSpring");
                };
                if (face > 0) CaptureEdgeAssignment(loads, model, areaName, face, "area spring", setSpring);
                else loads.Add(target => setSpring(target, face));
            }

            double[] modifiers = null;
            Check(model.AreaObj.GetModifiers(areaName, ref modifiers), "reading area stiffness modifiers", "AreaObj.GetModifiers");
            if (modifiers == null || modifiers.Length == 0)
                throw new InvalidDataException("SAP2000 did not provide area modifiers for " + areaName);
            double[] capturedModifiers = (double[])modifiers.Clone();
            if (capturedModifiers.All(value => Math.Abs(value - 1) <= 1e-12))
                loads.ExpectDefaultModifiers(capturedModifiers);
            else loads.Add(target =>
            {
                double[] copied = (double[])capturedModifiers.Clone();
                int result = model.AreaObj.SetModifiers(target, ref copied, eItemType.Objects);
                if (result != 0)
                {
                    int targetCornerCount = 0;
                    string[] joints = null;
                    int pointsCode = model.AreaObj.GetPoints(target, ref targetCornerCount, ref joints);
                    throw new InvalidOperationException("SAP2000 error while transferring area stiffness modifiers " +
                        "to " + target + " from " + areaName + " [API: AreaObj.SetModifiers]: code " + result +
                        ", modifier count=" + copied.Length + ", GetPoints=" + pointsCode +
                        ", corners=" + targetCornerCount + ", units=" + model.GetPresentUnits());
                }
            });

            string material = null;
            Check(model.AreaObj.GetMaterialOverwrite(areaName, ref material), "reading area material overwrite", "AreaObj.GetMaterialOverwrite");
            if (!string.IsNullOrEmpty(material) && material != "None")
                loads.Add(target => Check(model.AreaObj.SetMaterialOverwrite(target, material, eItemType.Objects),
                    "transferring area material overwrite", "AreaObj.SetMaterialOverwrite"));

            string diaphragm = null;
            int diaphragmCode = model.AreaObj.GetDiaphragm(areaName, ref diaphragm);
            if (diaphragmCode != -100) Check(diaphragmCode, "reading area diaphragm", "AreaObj.GetDiaphragm");
            if (diaphragmCode == 0 && !string.IsNullOrEmpty(diaphragm) && diaphragm != "None")
                loads.Add(target => Check(model.AreaObj.SetDiaphragm(target, diaphragm), "transferring area diaphragm", "AreaObj.SetDiaphragm"));

            string springProperty = null;
            int springPropertyCode = model.AreaObj.GetSpringAssignment(areaName, ref springProperty);
            if (springPropertyCode != -100) Check(springPropertyCode, "reading named area spring", "AreaObj.GetSpringAssignment");
            if (springPropertyCode == 0 && !string.IsNullOrEmpty(springProperty) && springProperty != "None")
                loads.Add(target => Check(model.AreaObj.SetSpringAssignment(target, springProperty, eItemType.Objects),
                    "transferring named area spring", "AreaObj.SetSpringAssignment"));

            int thicknessType = 0, offsetType = 0;
            string thicknessPattern = null, offsetPattern = null;
            double thicknessScale = 0, offsetScale = 0;
            double[] thickness = null, offsets = null;
            Check(model.AreaObj.GetThickness(areaName, ref thicknessType, ref thicknessPattern,
                ref thicknessScale, ref thickness), "reading area thickness overrides", "AreaObj.GetThickness");
            Check(model.AreaObj.GetOffsets(areaName, ref offsetType, ref offsetPattern,
                ref offsetScale, ref offsets), "reading area joint offsets", "AreaObj.GetOffsets");
            if (thicknessType != 0 && !string.IsNullOrEmpty(thicknessPattern))
                loads.RequirePattern(thicknessPattern);
            if (offsetType != 0 && !string.IsNullOrEmpty(offsetPattern))
                loads.RequirePattern(offsetPattern);
            loads.CaptureInterpolatedValues(model, areaName, thicknessType, thicknessPattern,
                thicknessScale, thickness, offsetType, offsetPattern, offsetScale, offsets);

            double temperature = 0;
            string temperaturePattern = null;
            Check(model.AreaObj.GetMatTemp(areaName, ref temperature, ref temperaturePattern), "reading area material temperature", "AreaObj.GetMatTemp");
            if (Math.Abs(temperature) > Tolerance || !string.IsNullOrEmpty(temperaturePattern))
                loads.Add(target => Check(model.AreaObj.SetMatTemp(target, temperature, temperaturePattern, eItemType.Objects),
                    "transferring area material temperature", "AreaObj.SetMatTemp"));

            string pier = null, spandrel = null;
            int pierCode = model.AreaObj.GetPier(areaName, ref pier);
            int spandrelCode = model.AreaObj.GetSpandrel(areaName, ref spandrel);
            if (pierCode != -100) Check(pierCode, "reading area pier assignment", "AreaObj.GetPier");
            if (spandrelCode != -100) Check(spandrelCode, "reading area spandrel assignment", "AreaObj.GetSpandrel");
            if (pierCode == 0 && !string.IsNullOrEmpty(pier) && pier != "None")
                loads.Add(target => Check(model.AreaObj.SetPier(target, pier, eItemType.Objects), "transferring area pier", "AreaObj.SetPier"));
            if (spandrelCode == 0 && !string.IsNullOrEmpty(spandrel) && spandrel != "None")
                loads.Add(target => Check(model.AreaObj.SetSpandrel(target, spandrel, eItemType.Objects), "transferring area spandrel", "AreaObj.SetSpandrel"));

            return loads;
        }

        private static void CaptureEdgeAssignment(AreaLoads assignments, cSapModel model, string source,
            int face, string description, Action<string, int> apply)
        {
            int vertices = 0;
            string[] names = null;
            Check(model.AreaObj.GetPoints(source, ref vertices, ref names), "reading boundary for " + description, "AreaObj.GetPoints");
            if (face < 1 || face > vertices || names == null || names.Length != vertices)
                throw new InvalidDataException("Invalid " + description + " face " + face + " on area " + source);
            double[] start = Coordinate(model, names[face - 1]);
            double[] end = Coordinate(model, names[face % vertices]);
            double originalLength = Distance(start, end);
            if (originalLength <= Tolerance) throw new InvalidDataException("Degenerate source edge for " + description);
            double assignedLength = 0;
            assignments.Add(target =>
            {
                int count = 0;
                string[] points = null;
                Check(model.AreaObj.GetPoints(target, ref count, ref points), "reading child edges for " + description, "AreaObj.GetPoints");
                for (int i = 0; i < count; i++)
                {
                    double[] a = Coordinate(model, points[i]);
                    double[] b = Coordinate(model, points[(i + 1) % count]);
                    double alongA, alongB;
                    if (!OnSegment(a, start, end, out alongA) || !OnSegment(b, start, end, out alongB) ||
                        Distance(a, b) <= Tolerance) continue;
                    try { apply(target, i + 1); }
                    catch (InvalidOperationException error) when (IsOptionalTransferCodeOne(error))
                    {
                        assignments.MarkIncompleteEdgeAssignment();
                        throw;
                    }
                    assignedLength += Distance(a, b);
                }
            });
            assignments.VerifyAfterApply(() =>
            {
                if (Math.Abs(assignedLength - originalLength) > Math.Max(Tolerance, originalLength * 1e-5))
                    throw new InvalidDataException("The children do not cover the entire " + description +
                        " edge of area " + source + ": expected " + originalLength + ", assigned " + assignedLength);
            });
        }

        private static double[] Coordinate(cSapModel model, string name)
        {
            double x = 0, y = 0, z = 0;
            Check(model.PointObj.GetCoordCartesian(name, ref x, ref y, ref z, "Global"), "reading point " + name, "PointObj.GetCoordCartesian");
            return new[] { x, y, z };
        }

        private static bool PointInPolygon(double[] point, double[][] polygon3D)
        {
            bool inside = false;
            for (int i = 0, j = polygon3D.Length - 1; i < polygon3D.Length; j = i++)
            {
                double xi = polygon3D[i][0], yi = polygon3D[i][1], xj = polygon3D[j][0], yj = polygon3D[j][1];
                if ((yi > point[1]) != (yj > point[1]) && point[0] < (xj - xi) * (point[1] - yi) / (yj - yi) + xi) inside = !inside;
            }
            return inside;
        }

        private static bool PointInsideOrBoundary(double[] point, double[][] polygon)
        {
            if (PointInPolygon(point, polygon)) return true;
            for (int i = 0; i < polygon.Length; i++)
            {
                double[] first = polygon[i], second = polygon[(i + 1) % polygon.Length];
                double dx = second[0] - first[0], dy = second[1] - first[1];
                double lengthSquared = dx * dx + dy * dy;
                double t = lengthSquared == 0 ? 0 : ((point[0] - first[0]) * dx + (point[1] - first[1]) * dy) / lengthSquared;
                t = Math.Max(0, Math.Min(1, t));
                double qx = first[0] + t * dx, qy = first[1] + t * dy;
                if (Math.Sqrt((point[0] - qx) * (point[0] - qx) + (point[1] - qy) * (point[1] - qy)) <= Tolerance)
                    return true;
            }
            return false;
        }

        private static bool OnSegment(double[] p, double[] a, double[] b, out double t)
        {
            double[] ab = Subtract(b, a), ap = Subtract(p, a);
            t = Dot(ap, ab) / Dot(ab, ab);
            double[] projection = { a[0] + t * ab[0], a[1] + t * ab[1], a[2] + t * ab[2] };
            return t >= -Tolerance && t <= 1 + Tolerance && Distance(p, projection) <= Tolerance;
        }

        private static void EnsureRectangularSymmetry(double[][] p)
        {
            double[] bottom = Subtract(p[1], p[0]), top = Subtract(p[2], p[3]);
            double[] left = Subtract(p[3], p[0]), right = Subtract(p[2], p[1]);
            double scale = Math.Max(Math.Max(Distance(p[0], p[1]), Distance(p[1], p[2])), 1.0);
            if (Distance(bottom, top) > Tolerance * scale || Distance(left, right) > Tolerance * scale ||
                Math.Abs(Dot(bottom, left)) > Tolerance * Distance(p[0], p[1]) * Distance(p[0], p[3]))
                throw new InvalidOperationException("Symmetric mode requires a rectangular area.");
        }

        private static T[] Rotate<T>(T[] values, int offset) { return Enumerable.Range(0, values.Length).Select(i => values[(i + offset) % values.Length]).ToArray(); }
        private static string Quote(string value) { return "\"" + value.Replace("\"", "\\\"") + "\""; }
        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        private static double[] Subtract(double[] a, double[] b) { return new[] { a[0] - b[0], a[1] - b[1], a[2] - b[2] }; }
        private static double Dot(double[] a, double[] b) { return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]; }
        private static double[] Cross(double[] a, double[] b) { return new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] }; }
        private static double Norm(double[] value) { return Math.Sqrt(Dot(value, value)); }
        private static double[] Normalize(double[] value) { double n = Norm(value); return value.Select(x => x / n).ToArray(); }
        private static double Distance(double[] a, double[] b) { return Norm(Subtract(a, b)); }
        private static void Check(int result, string operation, string api = null)
        {
            if (result != 0)
                throw new InvalidOperationException("SAP2000 error while " + operation +
                    (api == null ? "" : " [API: " + api + "]") + ": code " + result);
        }

        private static bool IsOptionalTransferCodeOne(InvalidOperationException error)
        {
            return error.Message.StartsWith("SAP2000 error while transferring ", StringComparison.Ordinal) &&
                Regex.IsMatch(error.Message, @"\[API: [^\]]+\]: code 1(?:\b|$)");
        }

        private sealed class BoundaryPoint
        {
            public readonly string Name; public readonly double T; public readonly double[] Coordinates;
            public BoundaryPoint(string name, double t, double[] coordinates) { Name = name; T = t; Coordinates = coordinates; }
        }
        private sealed class ConstraintPoint
        {
            public readonly string Name; public readonly double[] Coordinates;
            public ConstraintPoint(string name, double[] coordinates) { Name = name; Coordinates = coordinates; }
        }
        private sealed class FrameConstraint
        {
            public readonly string Name, FirstName, SecondName;
            public readonly double[] First, Second;
            public readonly bool Automatic;
            public bool CanBoundOpening;
            public readonly AreaContext SourceContext;
            public readonly int SourceEdge;
            public FrameConstraint(string name, string firstName, string secondName, double[] first, double[] second, bool automatic = false,
                AreaContext sourceContext = null, int sourceEdge = -1)
            {
                Name = name; FirstName = firstName; SecondName = secondName; First = first; Second = second;
                Automatic = automatic; SourceContext = sourceContext; SourceEdge = sourceEdge;
            }
        }
        private sealed class GroupObjects
        {
            public readonly HashSet<string> Frames;
            public readonly HashSet<string> Areas;
            public bool HasOpeningObjects { get { return Frames.Count > 0 || Areas.Count > 0; } }
            public GroupObjects(HashSet<string> frames, HashSet<string> areas) { Frames = frames; Areas = areas; }
        }
        private sealed class HoleDefinition
        {
            public readonly int Id;
            public readonly string PointName;
            public readonly double[] Center;
            public readonly double Diameter;
            public double Radius { get { return Diameter * 0.5; } }
            public HoleDefinition(int id, string pointName, double[] center, double diameter)
            { Id = id; PointName = pointName; Center = center; Diameter = diameter; }
        }
        private sealed class SymmetryAxis
        {
            public readonly double[] Center;
            public readonly double[] Direction;
            public readonly double[] SecondaryDirection;
            private readonly string englishLabel, italianLabel;
            public SymmetryAxis(double[] center, double[] direction, int index)
            {
                Center = center; Direction = direction;
                englishLabel = string.Format(CultureInfo.InvariantCulture, "Axis {0}: direction ({1:0.###}, {2:0.###}, {3:0.###})",
                    index, direction[0], direction[1], direction[2]);
                italianLabel = string.Format(CultureInfo.InvariantCulture, "Asse {0}: direzione ({1:0.###}, {2:0.###}, {3:0.###})",
                    index, direction[0], direction[1], direction[2]);
            }
            public SymmetryAxis(double[] center, double[] direction, double[] secondaryDirection)
            {
                Center = center; Direction = direction; SecondaryDirection = secondaryDirection;
                englishLabel = string.Format(CultureInfo.InvariantCulture,
                    "Double symmetry: principal axes ({0:0.###}, {1:0.###}, {2:0.###}) / ({3:0.###}, {4:0.###}, {5:0.###})",
                    direction[0], direction[1], direction[2], secondaryDirection[0], secondaryDirection[1], secondaryDirection[2]);
                italianLabel = string.Format(CultureInfo.InvariantCulture,
                    "Doppia simmetria: assi principali ({0:0.###}, {1:0.###}, {2:0.###}) / ({3:0.###}, {4:0.###}, {5:0.###})",
                    direction[0], direction[1], direction[2], secondaryDirection[0], secondaryDirection[1], secondaryDirection[2]);
            }
            public override string ToString() { return L(englishLabel, italianLabel); }
        }
        private sealed class SegmentInterval
        {
            public readonly double Start, End;
            public SegmentInterval(double start, double end) { Start = start; End = end; }
        }
        private sealed class AreaEdgeIntersection
        {
            public readonly int SourceIndex, SourceEdge, HostIndex;
            public readonly double Start, End;
            public AreaEdgeIntersection(int sourceIndex, int sourceEdge, int hostIndex, double start, double end)
            {
                SourceIndex = sourceIndex; SourceEdge = sourceEdge; HostIndex = hostIndex; Start = start; End = end;
            }
        }
        private sealed class MeshData
        {
            public readonly Dictionary<int, double[]> Nodes = new Dictionary<int, double[]>();
            public readonly List<int[]> Elements = new List<int[]>();
            public readonly HashSet<string> ProtectedEdges = new HashSet<string>();
            public int RemovedElements;
        }
        private sealed class EditableTable
        {
            public readonly string Key;
            public readonly int Version;
            public readonly string[] Fields;
            public readonly List<string> Data;
            public EditableTable(string key, int version, string[] fields, IEnumerable<string> data)
            {
                Key = key; Version = version; Fields = fields; Data = new List<string>(data);
                if (Fields.Length == 0 || Data.Count % Fields.Length != 0) throw new InvalidDataException("Invalid FAST table: " + key);
            }
            public int RecordCount { get { return Data.Count / Fields.Length; } }
            public int MaxJointField
            {
                get
                {
                    return Fields.Where(field => field.StartsWith("Joint", StringComparison.Ordinal) && field.Length > 5)
                        .Select(field => { int value; return int.TryParse(field.Substring(5), out value) ? value : 0; }).DefaultIfEmpty().Max();
                }
            }
            public void Append(Dictionary<string, string> values)
            {
                foreach (string field in Fields)
                {
                    string value;
                    Data.Add(values.TryGetValue(field, out value) ? value : "");
                }
            }
            public IEnumerable<string> Values(string field)
            {
                int index = Array.IndexOf(Fields, field);
                if (index < 0) throw new InvalidDataException("Missing FAST field: " + field);
                for (int record = 0; record < RecordCount; record++) yield return Data[record * Fields.Length + index];
            }
            public Dictionary<string, string[]> RowsBy(string field)
            {
                int index = Array.IndexOf(Fields, field);
                if (index < 0) throw new InvalidDataException("Missing FAST field: " + field);
                var result = new Dictionary<string, string[]>();
                for (int record = 0; record < RecordCount; record++)
                {
                    var row = new string[Fields.Length];
                    Data.CopyTo(record * Fields.Length, row, 0, Fields.Length);
                    // SAP emits one continuation record per extra group of polygon
                    // vertices. Only the first record owns the area's GUID; replacing
                    // it with the continuation (empty GUID) falsely fails FAST Undo.
                    if (!result.ContainsKey(row[index])) result.Add(row[index], row);
                }
                return result;
            }
            public string Value(string[] row, string field)
            {
                int index = Array.IndexOf(Fields, field);
                if (index < 0) throw new InvalidDataException("Missing FAST field: " + field);
                return row[index];
            }
            public Dictionary<string, string> NonEmptyMap(string nameField, string valueField, IEnumerable<string> excluded)
            {
                var excludedNames = new HashSet<string>(excluded);
                var result = new Dictionary<string, string>();
                int nameIndex = Array.IndexOf(Fields, nameField), valueIndex = Array.IndexOf(Fields, valueField);
                if (nameIndex < 0 || valueIndex < 0) throw new InvalidDataException("Missing FAST identity field.");
                for (int record = 0; record < RecordCount; record++)
                {
                    string name = Data[record * Fields.Length + nameIndex];
                    string value = Data[record * Fields.Length + valueIndex];
                    if (!excludedNames.Contains(name) && !string.IsNullOrEmpty(value)) result[name] = value;
                }
                return result;
            }
            public EditableTable Clone() { return new EditableTable(Key, Version, Fields.ToArray(), Data.ToArray()); }
        }
        private sealed class PointCoordinateIndex
        {
            private sealed class Entry
            {
                public readonly double[] Point;
                public readonly string Name;
                public Entry(double[] point, string name) { Point = point; Name = name; }
            }
            private readonly Dictionary<string, List<Entry>> buckets = new Dictionary<string, List<Entry>>();
            private readonly Dictionary<string, int> groupByArea =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, HashSet<string>> cornersByArea =
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<int, HashSet<string>> permittedByGroup =
                new Dictionary<int, HashSet<string>>();
            private readonly double tolerance;
            public int IncompletePolygonAreas { get; private set; }
            public PointCoordinateIndex(double tolerance) { this.tolerance = tolerance; }
            public void MarkIncompletePolygonArea() { IncompletePolygonAreas++; }
            private long Cell(double value) { return (long)Math.Floor(value / tolerance); }
            private static string Key(long x, long y, long z) { return x + ":" + y + ":" + z; }
            public void RegisterArea(string area, int group, IEnumerable<string> corners)
            {
                groupByArea.Add(area, group);
                cornersByArea.Add(area, new HashSet<string>(corners, StringComparer.OrdinalIgnoreCase));
                if (!permittedByGroup.ContainsKey(group))
                    permittedByGroup.Add(group, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            }
            public void Permit(string area, string name)
            {
                permittedByGroup[groupByArea[area]].Add(name);
                Trace(area, null, name, "allowed");
            }
            private Entry FindCoincident(Dictionary<string, List<Entry>> index, double[] point)
            {
                long x = Cell(point[0]), y = Cell(point[1]), z = Cell(point[2]);
                for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        List<Entry> entries;
                        if (!index.TryGetValue(Key(x + dx, y + dy, z + dz), out entries)) continue;
                        Entry match = entries.FirstOrDefault(entry => Distance(entry.Point, point) <= tolerance);
                        if (match != null) return match;
                    }
                return null;
            }
            private Entry FindOtherCoincident(double[] point, string selectedName)
            {
                long x = Cell(point[0]), y = Cell(point[1]), z = Cell(point[2]);
                for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++)
                    for (long dz = -1; dz <= 1; dz++)
                    {
                        List<Entry> entries;
                        if (!buckets.TryGetValue(Key(x + dx, y + dy, z + dz), out entries)) continue;
                        Entry match = entries.FirstOrDefault(entry =>
                            !string.Equals(entry.Name, selectedName, StringComparison.OrdinalIgnoreCase) &&
                            Distance(entry.Point, point) <= tolerance);
                        if (match != null) return match;
                    }
                return null;
            }
            private static string Position(double[] point)
            {
                return string.Join(",", point.Select(value => value.ToString("R", CultureInfo.InvariantCulture)));
            }
            public void ValidateOriginalConstraints(cSapModel model, IEnumerable<string> names,
                HashSet<string> required, List<AreaContext> contexts)
            {
                // Visit required joints first, so a duplicate with any other
                // existing joint is caught regardless of SAP's name ordering.
                foreach (string name in names.OrderByDescending(required.Contains))
                {
                    double[] point = Coordinate(model, name);
                    Entry existing = FindCoincident(buckets, point);
                    if (existing != null && (required.Contains(name) || required.Contains(existing.Name) ||
                        contexts.Any(context => Enumerable.Range(0, context.Corners.Length).Any(edge =>
                        {
                            double t;
                            return OnSegment(point, context.Corners[edge],
                                context.Corners[(edge + 1) % context.Corners.Length], out t);
                        }))))
                        throw new InvalidDataException("Cannot mesh: original " + HostProductName() + " joints " + existing.Name + " and " +
                            name + " coincide at " + Position(point) +
                            " on the selected constraints or area boundaries. Separate or merge them before meshing; " +
                            "no SAP2000 objects were modified.");
                    AddOriginal(point, name);
                }
            }
            public void RequireUnoccupiedForNew(double[] point, string area)
            {
                Entry existing = FindCoincident(buckets, point);
                if (existing == null) return;
                Trace(area, point, existing.Name, "blocked-overlap-original");
                throw new InvalidDataException("Area " + area + " would create a new joint at " +
                    "existing " + HostProductName() + " joint " + existing.Name + " (" + Position(point) + "). " +
                    "Joint creation was refused; check whether the areas share a connected edge.");
            }
            public void ValidateNewJointPositions(IEnumerable<PreparedMesh> prepared)
            {
                // Simulate joint reuse across *all* candidate areas without
                // changing the insertion index or touching the SAP model.
                var staged = new Dictionary<string, List<Entry>>();
                foreach (PreparedMesh item in prepared)
                    foreach (double[] point in item.Mesh.Nodes.Values)
                    {
                        string reusable;
                        if (TryFind(point, item.SourceArea, out reusable))
                        {
                            Entry other = FindOtherCoincident(point, reusable);
                            if (other != null)
                                throw new InvalidDataException("Cannot choose between original joints " + reusable +
                                    " and " + other.Name + " at " + Position(point) + ". " +
                                    "Resolve coincident original joints before meshing; no SAP2000 objects were modified.");
                            continue;
                        }
                        RequireUnoccupiedForNew(point, item.SourceArea);
                        Entry previous = FindCoincident(staged, point);
                        if (previous != null)
                        {
                            if (groupByArea[previous.Name] != groupByArea[item.SourceArea])
                            {
                                Trace(item.SourceArea, point, previous.Name, "blocked-overlap-generated");
                                throw new InvalidDataException("Areas " + previous.Name + " and " + item.SourceArea +
                                    " would create separate joints at the same position (" + Position(point) + "). " +
                                    "Check their shared edge. No SAP2000 objects were modified.");
                            }
                            continue; // The same connected component will reuse one new joint.
                        }
                        string key = Key(Cell(point[0]), Cell(point[1]), Cell(point[2]));
                        List<Entry> entries;
                        if (!staged.TryGetValue(key, out entries)) staged[key] = entries = new List<Entry>();
                        entries.Add(new Entry(point.ToArray(), item.SourceArea));
                    }
            }
            private static void Trace(string area, double[] point, string name, string stage)
            {
                string directory = Environment.GetEnvironmentVariable("SAP_QUAD_MESH_TRACE_DIR");
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
                string position = point == null ? "" : string.Join(",", point.Select(value =>
                    value.ToString("R", CultureInfo.InvariantCulture)));
                try { File.AppendAllText(Path.Combine(directory, "joint-bindings.csv"),
                    stage + "," + area + "," + name + "," + position + Environment.NewLine); }
                catch (IOException) { }
            }
            public void Add(double[] point, string name, string area)
            {
                Permit(area, name);
                AddOriginal(point, name);
            }
            public void AddOriginal(double[] point, string name)
            {
                string key = Key(Cell(point[0]), Cell(point[1]), Cell(point[2]));
                List<Entry> entries;
                if (!buckets.TryGetValue(key, out entries)) buckets[key] = entries = new List<Entry>();
                if (!entries.Any(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase)))
                    entries.Add(new Entry(point.ToArray(), name));
            }
            public bool TryFind(double[] point, string area, out string name)
            {
                long x = Cell(point[0]), y = Cell(point[1]), z = Cell(point[2]);
                HashSet<string> allowed = permittedByGroup[groupByArea[area]];
                Entry best = null;
                double bestDistance = double.PositiveInfinity;
                for (long dx = -1; dx <= 1; dx++) for (long dy = -1; dy <= 1; dy++) for (long dz = -1; dz <= 1; dz++)
                {
                    List<Entry> entries;
                    if (!buckets.TryGetValue(Key(x + dx, y + dy, z + dz), out entries)) continue;
                    foreach (Entry entry in entries)
                    {
                        if (!allowed.Contains(entry.Name)) continue;
                        double distance = Distance(entry.Point, point);
                        if (distance > tolerance) continue;
                        if (distance <= Tolerance && cornersByArea[area].Contains(entry.Name))
                        { name = entry.Name; Trace(area, point, name, "corner"); return true; }
                        if (distance < bestDistance)
                        { best = entry; bestDistance = distance; }
                    }
                }
                bool connected = best != null;
                if (best == null) best = FindCoincident(buckets, point);
                name = best == null ? null : best.Name;
                Trace(area, point, name ?? "", best == null ? "new" : connected ? "reuse" : "reuse-global");
                return best != null;
            }
        }
        private sealed class FastTableSnapshot
        {
            public readonly EditableTable Joints, Areas, Sections;
            public readonly Dictionary<string, string> JointGuids, AreaGuids;
            public FastTableSnapshot(EditableTable joints, EditableTable areas, EditableTable sections)
            {
                Joints = joints; Areas = areas; Sections = sections;
                JointGuids = joints.NonEmptyMap("Joint", "GUID", new string[0]);
                AreaGuids = areas.NonEmptyMap("Area", "GUID", new string[0]);
            }
        }
        private sealed class AreaContext
        {
            public readonly string Name;
            public readonly string[] CornerNames;
            public readonly double[][] Corners;
            public readonly Plane Plane;
            public readonly List<BoundaryPoint>[] EdgePoints;
            public readonly List<ConstraintPoint> InteriorPoints = new List<ConstraintPoint>();
            public readonly List<FrameConstraint> FrameConstraints = new List<FrameConstraint>();
            public readonly List<HoleDefinition> Holes = new List<HoleDefinition>();
            public readonly HashSet<string> ConstraintPointNames = new HashSet<string>();
            public readonly HashSet<string> ExplicitlySelectedPointNames = new HashSet<string>();
            public bool ForceRefinement;
            public bool PreserveFrameBoundary;
            public string DesignProcedure;
            public bool DesignProcedureUnavailable;
            public AreaContext(string name, string[] cornerNames, double[][] corners, Plane plane)
            {
                Name = name;
                CornerNames = cornerNames;
                Corners = corners;
                Plane = plane;
                EdgePoints = Enumerable.Range(0, corners.Length).Select(index => new List<BoundaryPoint>()).ToArray();
            }
        }
        private sealed class MeshOperation
        {
            public readonly string SourceArea, BackupArea;
            public readonly PointSnapshot[] OriginalPoints;
            public readonly List<string> GeneratedAreas = new List<string>();
            public readonly List<string> GeneratedPoints = new List<string>();
            public bool FastMode;
            public string PrimaryGeneratedArea;
            public string CommitBackupArea;
            public bool Active = true;
            public bool Unchanged;
            public bool Undone;
            public MeshOperation(string sourceArea, string backupArea, PointSnapshot[] originalPoints)
            { SourceArea = sourceArea; BackupArea = backupArea; OriginalPoints = originalPoints; }
        }
        private sealed class PreparedMesh
        {
            public readonly string SourceArea, Property;
            public readonly double Angle, Mass;
            public readonly bool EdgeConstraint;
            public readonly AreaLoads Loads;
            public readonly PointSnapshot[] OriginalPoints;
            public readonly MeshData Mesh;
            public readonly MeshQualityReport Quality;
            public PreparedMesh(string sourceArea, string property, double angle, double mass, bool edgeConstraint,
                AreaLoads loads, PointSnapshot[] originalPoints, MeshData mesh, MeshQualityReport quality)
            {
                SourceArea = sourceArea; Property = property; Angle = angle; Mass = mass;
                EdgeConstraint = edgeConstraint; Loads = loads; OriginalPoints = originalPoints;
                Mesh = mesh; Quality = quality;
            }
        }
        private sealed class MeshAttemptResult
        {
            public readonly MeshOperation Operation;
            public readonly MeshQualityReport Quality;
            public MeshAttemptResult(MeshOperation operation, MeshQualityReport quality)
            { Operation = operation; Quality = quality; }
        }
        private sealed class PointSnapshot
        {
            public readonly string Name;
            public readonly double[] Coordinates;
            public readonly bool Special;
            public readonly string[] Groups;
            public PointSnapshot(string name, double[] coordinates, bool special, string[] groups)
            { Name = name; Coordinates = coordinates; Special = special; Groups = groups; }
        }
        private sealed class MeshQualityReport
        {
            public readonly bool Passed;
            public readonly string Summary;
            public double OverallScore;
            public readonly List<QualityMetric> Metrics = new List<QualityMetric>();
            public MeshQualityReport(bool passed, string summary) { Passed = passed; Summary = summary; }
        }
        private sealed class QualityMetric
        {
            public readonly string Name, Value, Reference;
            public readonly double Score;
            public QualityMetric(string name, string value, string reference, double score)
            { Name = name; Value = value; Reference = reference; Score = Clamp01(score); }
            public string Level
            {
                get
                {
                    if (Score >= 0.75) return "EXCELLENT";
                    if (Score >= 0.50) return "ACCEPTABLE";
                    if (Score >= 0.25) return "POOR";
                    return "CRITICAL";
                }
            }
        }
        private sealed class AreaLoads
        {
            private readonly List<Action<string>> assignments = new List<Action<string>>();
            private readonly List<Action> postApplyChecks = new List<Action>();
            private readonly List<string> children = new List<string>();
            private readonly HashSet<string> requiredPatterns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<string> UnpreservedDesignTables = new List<string>();
            private AreaValueTransfer interpolatedValues;
            private long interpolationTicks, directAssignmentTicks;
            private double[] defaultModifiers;
            private readonly string sourceArea;
            private bool incompleteEdgeAssignment;
            public string DesignProcedure;
            public IEnumerable<string> ChildAreaNames { get { return children; } }
            public AreaLoads(string sourceArea) { this.sourceArea = sourceArea; }
            public int Count { get { return assignments.Count; } }
            public bool HasInterpolatedValues { get { return interpolatedValues != null; } }
            public long InterpolationTicks { get { return interpolationTicks; } }
            public long DirectAssignmentTicks { get { return directAssignmentTicks; } }
            public void Add(Action<string> assignment) { assignments.Add(assignment); }
            public void ExpectDefaultModifiers(double[] values)
            {
                defaultModifiers = (double[])values.Clone();
            }
            public void MarkIncompleteEdgeAssignment() { incompleteEdgeAssignment = true; }
            public void RequirePattern(string pattern)
            {
                if (!string.IsNullOrEmpty(pattern)) requiredPatterns.Add(pattern);
            }
            public void CaptureInterpolatedValues(cSapModel model, string areaName, int thicknessType,
                string thicknessPattern, double thicknessScale, double[] thickness, int offsetType,
                string offsetPattern, double offsetScale, double[] offsets)
            {
                interpolatedValues = AreaValueTransfer.Capture(model, areaName, requiredPatterns,
                    thicknessType, thicknessPattern, thicknessScale, thickness,
                    offsetType, offsetPattern, offsetScale, offsets);
            }
            public void VerifyAfterApply(Action check) { postApplyChecks.Add(check); }
            public void Apply(cSapModel model, string areaName, string[] pointNames, MeshOptionsDialog progress)
            {
                bool profile = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAP_QUAD_PROFILE_DIR"));
                long start = profile ? Stopwatch.GetTimestamp() : 0;
                if (interpolatedValues != null) interpolatedValues.Apply(model, areaName, pointNames, progress);
                if (profile) { interpolationTicks += Stopwatch.GetTimestamp() - start; start = Stopwatch.GetTimestamp(); }
                if (defaultModifiers != null)
                {
                    double[] actual = null;
                    int read = model.AreaObj.GetModifiers(areaName, ref actual);
                    if (read != 0 || actual == null || actual.Length != defaultModifiers.Length ||
                        actual.Where((value, index) => Math.Abs(value - defaultModifiers[index]) > 1e-12).Any())
                    {
                        double[] copied = (double[])defaultModifiers.Clone();
                        progress.CheckOptionalTransfer(model.AreaObj.SetModifiers(areaName, ref copied, eItemType.Objects),
                            sourceArea, areaName, "default area stiffness modifiers", "AreaObj.SetModifiers");
                    }
                }
                foreach (Action<string> assignment in assignments)
                    try { assignment(areaName); }
                    catch (InvalidOperationException error) when (progress.WarnOptionalTransfer(error, sourceArea, areaName)) { }
                if (profile) directAssignmentTicks += Stopwatch.GetTimestamp() - start;
                children.Add(areaName);
            }
            public void RemapChildName(string oldName, string newName)
            {
                int index = children.FindIndex(name => name == oldName);
                if (index >= 0) children[index] = newName;
            }
            public void Finish(cSapModel model, MeshOptionsDialog progress)
            {
                FinishGeometry();
                if (children.Count == 0 || string.IsNullOrEmpty(DesignProcedure)) return;
                try { FinishDesignProcedure(model, progress); }
                catch (InvalidOperationException error) when (progress.WarnOptionalApiFailure(error,
                    sourceArea, string.Join(",", children), "area design procedure transfer or verification"))
                { model.DatabaseTables.CancelTableEditing(); }
            }
            public void FinishGeometry()
            {
                if (!incompleteEdgeAssignment)
                    foreach (Action check in postApplyChecks) check();
            }
            private void FinishDesignProcedure(cSapModel model, MeshOptionsDialog progress)
            {
                EditableTable table = ReadEditableTable(model, "Area Design Procedures");
                RequireFields(table, "Area", "DesignProc");
                int areaField = Array.IndexOf(table.Fields, "Area");
                int designField = Array.IndexOf(table.Fields, "DesignProc");
                // SAP can retain design-procedure records for shells removed by a
                // previous mesh. Its editing API returns those orphan records, but
                // ApplyEditedTables rejects the entire transfer when they are sent
                // back. Keep every assignment belonging to an actual shell.
                int areaCount = 0;
                string[] areaNames = null;
                Check(model.AreaObj.GetNameList(ref areaCount, ref areaNames),
                    "reading existing shells for design-procedure transfer", "AreaObj.GetNameList");
                var liveAreas = new HashSet<string>(areaNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
                if (liveAreas.Count != areaCount)
                    throw new InvalidDataException("Cannot validate design-procedure area identities.");
                bool changed = false;
                for (int row = table.RecordCount - 1; row >= 0; row--)
                {
                    int offset = row * table.Fields.Length;
                    if (liveAreas.Contains(table.Data[offset + areaField])) continue;
                    table.Data.RemoveRange(offset, table.Fields.Length);
                    changed = true;
                }
                var remaining = new HashSet<string>(children, StringComparer.OrdinalIgnoreCase);
                for (int row = 0; row < table.RecordCount; row++)
                {
                    int offset = row * table.Fields.Length;
                    if (!remaining.Remove(table.Data[offset + areaField])) continue;
                    if (table.Data[offset + designField] == DesignProcedure) continue;
                    table.Data[offset + designField] = DesignProcedure;
                    changed = true;
                }
                foreach (string area in remaining)
                {
                    int vertices = 0;
                    string[] vertexNames = null;
                    if (!liveAreas.Contains(area) || model.AreaObj.GetPoints(area, ref vertices, ref vertexNames) != 0)
                        throw new InvalidDataException("Cannot transfer design procedure to missing shell " + area + ".");
                    table.Append(new Dictionary<string, string> { { "Area", area }, { "DesignProc", DesignProcedure } });
                    changed = true;
                }
                if (!changed) return;
                WriteEditableTable(model, table);
                int fatal = 0, errors = 0, warnings = 0, information = 0;
                string log = "";
                if (!progress.CheckOptionalTransfer(model.DatabaseTables.ApplyEditedTables(true, ref fatal, ref errors,
                    ref warnings, ref information, ref log), sourceArea, string.Join(",", children),
                    "area design procedures", "DatabaseTables.ApplyEditedTables"))
                {
                    model.DatabaseTables.CancelTableEditing();
                    return;
                }
                if (fatal != 0 || errors != 0 || warnings != 0)
                    throw new InvalidOperationException("Area design procedure import failed: " +
                        ImportSummary(fatal, errors, warnings, log));
                var actual = ReadEditableTable(model, "Area Design Procedures").RowsBy("Area");
                foreach (string area in children)
                {
                    string[] row;
                    if (!actual.TryGetValue(area, out row) ||
                        row[Array.IndexOf(table.Fields, "DesignProc")] != DesignProcedure)
                        throw new InvalidDataException("Area design procedure was not transferred to " + area);
                }
            }
        }
        private sealed class AreaValueTransfer
        {
            private readonly string sourceArea;
            private readonly string[] sourcePoints;
            private readonly double[][] sourceCoordinates;
            private readonly double[][] sourcePlanePoints;
            private readonly Plane plane;
            private readonly Dictionary<string, double[]> patterns;
            private readonly HashSet<string> assignedPatternPoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, double[]> patternWeightCache = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
            private readonly int thicknessType;
            private readonly string thicknessPattern;
            private readonly double thicknessScale;
            private readonly double[] thickness;
            private readonly int offsetType;
            private readonly string offsetPattern;
            private readonly double offsetScale;
            private readonly double[] offsets;

            private AreaValueTransfer(string sourceArea, string[] sourcePoints, double[][] sourceCoordinates,
                Plane plane, Dictionary<string, double[]> patterns, int thicknessType, string thicknessPattern,
                double thicknessScale, double[] thickness, int offsetType, string offsetPattern,
                double offsetScale, double[] offsets)
            {
                this.sourceArea = sourceArea;
                this.sourcePoints = sourcePoints;
                this.sourceCoordinates = sourceCoordinates;
                this.plane = plane;
                sourcePlanePoints = sourceCoordinates.Select(plane.Project).ToArray();
                this.patterns = patterns;
                this.thicknessType = thicknessType;
                this.thicknessPattern = thicknessPattern ?? "";
                this.thicknessScale = thicknessScale;
                this.thickness = thickness;
                this.offsetType = offsetType;
                this.offsetPattern = offsetPattern ?? "";
                this.offsetScale = offsetScale;
                this.offsets = offsets;
            }

            public static AreaValueTransfer Capture(cSapModel model, string areaName,
                HashSet<string> requiredPatterns, int thicknessType, string thicknessPattern,
                double thicknessScale, double[] thickness, int offsetType, string offsetPattern,
                double offsetScale, double[] offsets)
            {
                int pointCount = 0;
                string[] pointNames = null;
                Check(model.AreaObj.GetPoints(areaName, ref pointCount, ref pointNames),
                    "reading source joints for interpolated assignments", "AreaObj.GetPoints");
                if (pointNames == null || pointCount < 3 || pointNames.Length != pointCount)
                    throw new InvalidDataException("Invalid source connectivity for interpolated assignments on area " + areaName + ".");
                double[][] coordinates = pointNames.Select(name => Coordinate(model, name)).ToArray();
                Plane sourcePlane = Plane.FromPolygon(coordinates);

                int patternCount = 0;
                string[] patternNames = null;
                Check(model.PatternDef.GetNameList(ref patternCount, ref patternNames), "reading joint pattern definitions", "PatternDef.GetNameList");
                var captured = new Dictionary<string, double[]>(StringComparer.OrdinalIgnoreCase);
                foreach (string pattern in patternNames ?? new string[0])
                {
                    var values = new double[pointCount];
                    for (int i = 0; i < pointCount; i++)
                    {
                        int code = model.PointObj.GetPatternValue(pointNames[i], pattern, ref values[i]);
                        if (code == 0) continue;
                        // SAP returns code 1 when a valid point has no explicit row for a
                        // defined pattern. Its effective pattern value is zero.
                        if (code == 1) values[i] = 0;
                        else Check(code, "reading joint pattern " + pattern + " at " + pointNames[i], "PointObj.GetPatternValue");
                    }
                    if (requiredPatterns.Contains(pattern) || values.Any(value => Math.Abs(value) > Tolerance))
                        captured[pattern] = values;
                }
                foreach (string required in requiredPatterns)
                    if (!captured.ContainsKey(required))
                        throw new InvalidDataException("Joint pattern " + required + " used by area " + areaName + " is not defined.");

                double[] capturedThickness = null;
                if (thicknessType != 0)
                {
                    if (thickness == null || thickness.Length != pointCount)
                        throw new InvalidDataException("SAP2000 returned " + (thickness == null ? 0 : thickness.Length) +
                            " thickness values for " + pointCount + " joints on area " + areaName + ".");
                    capturedThickness = (double[])thickness.Clone();
                }
                double[] capturedOffsets = null;
                if (offsetType < 0 || offsetType > 2)
                    throw new InvalidDataException("SAP2000 returned unsupported joint-offset type " + offsetType +
                        " on area " + areaName + ".");
                if (offsetType == 2)
                {
                    if (offsets == null || offsets.Length != pointCount)
                        throw new InvalidDataException("SAP2000 returned " + (offsets == null ? 0 : offsets.Length) +
                            " joint-offset values for " + pointCount + " joints on area " + areaName + ".");
                    capturedOffsets = (double[])offsets.Clone();
                }
                if (captured.Count == 0 && thicknessType == 0 && offsetType == 0) return null;
                return new AreaValueTransfer(areaName, pointNames.ToArray(), coordinates, sourcePlane,
                    captured, thicknessType, thicknessPattern, thicknessScale, capturedThickness,
                    offsetType, offsetPattern, offsetScale, capturedOffsets);
            }

            public void Apply(cSapModel model, string childArea, string[] childPoints, MeshOptionsDialog progress)
            {
                if (childPoints == null || childPoints.Length < 3)
                    throw new InvalidDataException("Invalid child connectivity while transferring interpolated assignments to " + childArea + ".");
                double[][] coordinates = childPoints.Select(name => Coordinate(model, name)).ToArray();
                double[][] geometricWeights = coordinates.Select(WeightsAt).ToArray();
                var patternWeights = new double[childPoints.Length][];
                for (int i = 0; i < childPoints.Length; i++)
                    if (!patternWeightCache.TryGetValue(childPoints[i], out patternWeights[i]))
                    {
                        patternWeights[i] = PatternWeightsAt(coordinates[i], geometricWeights[i]);
                        patternWeightCache[childPoints[i]] = patternWeights[i];
                    }

                foreach (KeyValuePair<string, double[]> pattern in patterns)
                    for (int i = 0; i < childPoints.Length; i++)
                    {
                        string key = pattern.Key + "\0" + childPoints[i];
                        if (!assignedPatternPoints.Add(key)) continue;
                        double value = Dot(patternWeights[i], pattern.Value);
                        value = Math.Max(pattern.Value.Min(), Math.Min(pattern.Value.Max(), value));
                        if (!progress.CheckOptionalTransfer(model.PointObj.SetPatternByXYZ(
                            childPoints[i], pattern.Key, 0, 0, 0, value, eItemType.Objects, 0, true),
                            sourceArea, childPoints[i], "joint pattern " + pattern.Key,
                            "PointObj.SetPatternByXYZ")) continue;
                        double actualPatternValue = 0;
                        if (!progress.CheckOptionalTransfer(model.PointObj.GetPatternValue(childPoints[i],
                            pattern.Key, ref actualPatternValue), sourceArea, childPoints[i],
                            "verification of joint pattern " + pattern.Key, "PointObj.GetPatternValue")) continue;
                        if (Math.Abs(actualPatternValue - value) > 1e-8 * Math.Max(1, Math.Abs(value)))
                            throw new InvalidDataException("Joint pattern " + pattern.Key + " was not transferred to " + childPoints[i] + ".");
                    }

                if (thicknessType != 0)
                {
                    double[] childThickness = geometricWeights.Select(weight => Dot(weight, thickness)).ToArray();
                    double[] input = PadTriangleValues(childThickness);
                    if (progress.CheckOptionalTransfer(model.AreaObj.SetThickness(childArea, thicknessType,
                        thicknessPattern, thicknessScale, ref input, eItemType.Objects), sourceArea, childArea,
                        "thickness overrides", "AreaObj.SetThickness"))
                    {
                        int actualType = 0;
                        string actualPattern = null;
                        double actualScale = 0;
                        double[] actual = null;
                        if (progress.CheckOptionalTransfer(model.AreaObj.GetThickness(childArea, ref actualType,
                            ref actualPattern, ref actualScale, ref actual), sourceArea, childArea,
                            "verification of thickness overrides", "AreaObj.GetThickness"))
                            VerifyVertexValues(childArea, "Thickness overrides", thicknessType, thicknessPattern,
                                thicknessScale, childThickness, actualType, actualPattern, actualScale, actual);
                    }
                }

                if (offsetType != 0)
                {
                    double[] childOffsets = offsetType == 2
                        ? geometricWeights.Select(weight => Dot(weight, offsets)).ToArray()
                        : new double[0];
                    double[] input = offsetType == 2 ? PadTriangleValues(childOffsets) : new double[0];
                    if (!progress.CheckOptionalTransfer(model.AreaObj.SetOffsets(childArea, offsetType,
                        offsetPattern, offsetScale, ref input, eItemType.Objects), sourceArea, childArea,
                        "joint offsets", "AreaObj.SetOffsets")) return;
                    int actualType = 0;
                    string actualPattern = null;
                    double actualScale = 0;
                    double[] actual = null;
                    if (!progress.CheckOptionalTransfer(model.AreaObj.GetOffsets(childArea, ref actualType,
                        ref actualPattern, ref actualScale, ref actual), sourceArea, childArea,
                        "verification of joint offsets", "AreaObj.GetOffsets")) return;
                    if (offsetType == 1)
                    {
                        if (actualType != offsetType || !string.Equals(actualPattern ?? "", offsetPattern,
                            StringComparison.OrdinalIgnoreCase) || Math.Abs(actualScale - offsetScale) > 1e-10)
                            throw new InvalidDataException("Joint offsets were not transferred to area " + childArea + ".");
                    }
                    else
                        VerifyVertexValues(childArea, "Joint offsets", offsetType, offsetPattern,
                            offsetScale, childOffsets, actualType, actualPattern, actualScale, actual);
                }
            }

            private static double[] PadTriangleValues(double[] values)
            {
                return values.Length == 3
                    ? new[] { values[0], values[1], values[2], values[2] }
                    : (double[])values.Clone();
            }

            private static void VerifyVertexValues(string childArea, string description, int expectedType,
                string expectedPattern, double expectedScale, double[] expected, int actualType,
                string actualPattern, double actualScale, double[] actual)
            {
                if (actualType != expectedType || !string.Equals(actualPattern ?? "", expectedPattern ?? "",
                    StringComparison.OrdinalIgnoreCase) || Math.Abs(actualScale - expectedScale) > 1e-10 ||
                    actual == null || actual.Length < expected.Length ||
                    actual.Take(expected.Length).Where((value, index) => Math.Abs(value - expected[index]) >
                        1e-8 * Math.Max(1, Math.Abs(expected[index]))).Any())
                    throw new InvalidDataException(description + " were not transferred to area " + childArea + ".");
            }

            private double[] WeightsAt(double[] coordinates)
            {
                if (Math.Abs(plane.SignedDistance(coordinates)) > Tolerance * 10)
                    throw new InvalidDataException("A generated joint is outside the source plane of area " + sourceArea + ".");
                double[] point = plane.Project(coordinates);
                for (int i = 0; i < sourceCoordinates.Length; i++)
                    if (Distance(coordinates, sourceCoordinates[i]) <= Tolerance)
                    {
                        var exact = new double[sourceCoordinates.Length];
                        exact[i] = 1;
                        return exact;
                    }
                for (int i = 0; i < sourcePlanePoints.Length; i++)
                {
                    int next = (i + 1) % sourcePlanePoints.Length;
                    double[] edge = Subtract2(sourcePlanePoints[next], sourcePlanePoints[i]);
                    double lengthSquared = Dot(edge, edge);
                    if (lengthSquared <= Tolerance * Tolerance) continue;
                    double t = Dot(Subtract2(point, sourcePlanePoints[i]), edge) / lengthSquared;
                    double[] closest = Add2(sourcePlanePoints[i], Scale2(edge, t));
                    if (t >= -Tolerance && t <= 1 + Tolerance && Distance2(point, closest) <= Tolerance * 10)
                    {
                        var boundary = new double[sourcePlanePoints.Length];
                        boundary[i] = 1 - Math.Max(0, Math.Min(1, t));
                        boundary[next] = Math.Max(0, Math.Min(1, t));
                        return boundary;
                    }
                }
                double[] result = sourcePlanePoints.Length == 4 ? QuadWeights(point) : MeanValueWeights(point);
                VerifyWeights(point, result);
                return result;
            }

            private double[] QuadWeights(double[] point)
            {
                double xi = 0, eta = 0;
                for (int iteration = 0; iteration < 30; iteration++)
                {
                    double[] n = { (1 - xi) * (1 - eta) / 4, (1 + xi) * (1 - eta) / 4,
                        (1 + xi) * (1 + eta) / 4, (1 - xi) * (1 + eta) / 4 };
                    double[] dXi = { -(1 - eta) / 4, (1 - eta) / 4, (1 + eta) / 4, -(1 + eta) / 4 };
                    double[] dEta = { -(1 - xi) / 4, -(1 + xi) / 4, (1 + xi) / 4, (1 - xi) / 4 };
                    double rx = Dot(n, sourcePlanePoints.Select(vertex => vertex[0]).ToArray()) - point[0];
                    double ry = Dot(n, sourcePlanePoints.Select(vertex => vertex[1]).ToArray()) - point[1];
                    double j00 = Dot(dXi, sourcePlanePoints.Select(vertex => vertex[0]).ToArray());
                    double j01 = Dot(dEta, sourcePlanePoints.Select(vertex => vertex[0]).ToArray());
                    double j10 = Dot(dXi, sourcePlanePoints.Select(vertex => vertex[1]).ToArray());
                    double j11 = Dot(dEta, sourcePlanePoints.Select(vertex => vertex[1]).ToArray());
                    double determinant = j00 * j11 - j01 * j10;
                    if (Math.Abs(determinant) <= 1e-14) throw new InvalidDataException("Degenerate quadrilateral interpolation on area " + sourceArea + ".");
                    double dXiValue = (j11 * rx - j01 * ry) / determinant;
                    double dEtaValue = (-j10 * rx + j00 * ry) / determinant;
                    xi -= dXiValue;
                    eta -= dEtaValue;
                    if (Math.Max(Math.Abs(dXiValue), Math.Abs(dEtaValue)) <= 1e-12) break;
                }
                return new[] { (1 - xi) * (1 - eta) / 4, (1 + xi) * (1 - eta) / 4,
                    (1 + xi) * (1 + eta) / 4, (1 - xi) * (1 + eta) / 4 };
            }

            private double[] MeanValueWeights(double[] point)
            {
                int count = sourcePlanePoints.Length;
                var vectors = sourcePlanePoints.Select(vertex => Subtract2(vertex, point)).ToArray();
                double[] radii = vectors.Select(Norm2).ToArray();
                var tanHalf = new double[count];
                for (int i = 0; i < count; i++)
                {
                    int next = (i + 1) % count;
                    double denominator = radii[i] * radii[next] + Dot(vectors[i], vectors[next]);
                    if (Math.Abs(denominator) <= 1e-14)
                        throw new InvalidDataException("Unstable polygon interpolation on area " + sourceArea + ".");
                    tanHalf[i] = Cross2(vectors[i], vectors[next]) / denominator;
                }
                var weights = new double[count];
                for (int i = 0; i < count; i++) weights[i] = (tanHalf[(i + count - 1) % count] + tanHalf[i]) / radii[i];
                double sum = weights.Sum();
                if (Math.Abs(sum) <= 1e-14) throw new InvalidDataException("Invalid polygon interpolation on area " + sourceArea + ".");
                for (int i = 0; i < count; i++) weights[i] /= sum;
                return weights;
            }

            private double[] PatternWeightsAt(double[] coordinates, double[] geometricWeights)
            {
                if (sourcePlanePoints.Length <= 4 || geometricWeights.Count(value => Math.Abs(value) > 1e-12) <= 2)
                    return geometricWeights;
                double[] point = plane.Project(coordinates);
                double[] weights = ThinPlateWeights(point);
                VerifyWeights(point, weights, true);
                return weights;
            }

            private double[] ThinPlateWeights(double[] point)
            {
                int count = sourcePlanePoints.Length;
                double scale = 0;
                for (int i = 0; i < count; i++)
                    scale += Distance2(sourcePlanePoints[i], sourcePlanePoints[(i + 1) % count]);
                scale /= count;
                if (scale <= Tolerance) throw new InvalidDataException("Degenerate polygon interpolation on area " + sourceArea + ".");

                int size = count + 3;
                var matrix = new double[size, size];
                var rhs = new double[size];
                for (int row = 0; row < count; row++)
                {
                    for (int column = 0; column < count; column++)
                        matrix[row, column] = ThinPlateKernel(Distance2(sourcePlanePoints[row], sourcePlanePoints[column]) / scale);
                    matrix[row, count] = matrix[count, row] = 1;
                    matrix[row, count + 1] = matrix[count + 1, row] = sourcePlanePoints[row][0] / scale;
                    matrix[row, count + 2] = matrix[count + 2, row] = sourcePlanePoints[row][1] / scale;
                    rhs[row] = ThinPlateKernel(Distance2(sourcePlanePoints[row], point) / scale);
                }
                rhs[count] = 1;
                rhs[count + 1] = point[0] / scale;
                rhs[count + 2] = point[1] / scale;
                return SolveLinearSystem(matrix, rhs).Take(count).ToArray();
            }

            private static double ThinPlateKernel(double radius)
            {
                return radius <= 1e-15 ? 0 : radius * radius * Math.Log(radius);
            }

            private static double[] SolveLinearSystem(double[,] matrix, double[] rhs)
            {
                int size = rhs.Length;
                var augmented = new double[size, size + 1];
                for (int row = 0; row < size; row++)
                {
                    for (int column = 0; column < size; column++) augmented[row, column] = matrix[row, column];
                    augmented[row, size] = rhs[row];
                }
                for (int pivot = 0; pivot < size; pivot++)
                {
                    int best = pivot;
                    for (int row = pivot + 1; row < size; row++)
                        if (Math.Abs(augmented[row, pivot]) > Math.Abs(augmented[best, pivot])) best = row;
                    if (Math.Abs(augmented[best, pivot]) <= 1e-13)
                        throw new InvalidDataException("Singular thin-plate interpolation system.");
                    if (best != pivot)
                        for (int column = pivot; column <= size; column++)
                        {
                            double temporary = augmented[pivot, column];
                            augmented[pivot, column] = augmented[best, column];
                            augmented[best, column] = temporary;
                        }
                    double divisor = augmented[pivot, pivot];
                    for (int column = pivot; column <= size; column++) augmented[pivot, column] /= divisor;
                    for (int row = 0; row < size; row++)
                    {
                        if (row == pivot) continue;
                        double factor = augmented[row, pivot];
                        if (Math.Abs(factor) <= 1e-18) continue;
                        for (int column = pivot; column <= size; column++)
                            augmented[row, column] -= factor * augmented[pivot, column];
                    }
                }
                var solution = new double[size];
                for (int row = 0; row < size; row++) solution[row] = augmented[row, size];
                return solution;
            }

            private void VerifyWeights(double[] point, double[] weights, bool allowNegative = false)
            {
                if ((!allowNegative && weights.Any(value => value < -1e-8)) || Math.Abs(weights.Sum() - 1) > 1e-8)
                    throw new InvalidDataException("Generated joint is outside the interpolation domain of area " + sourceArea + ".");
                double x = 0, y = 0;
                for (int i = 0; i < weights.Length; i++)
                {
                    x += weights[i] * sourcePlanePoints[i][0];
                    y += weights[i] * sourcePlanePoints[i][1];
                }
                if (Math.Sqrt((x - point[0]) * (x - point[0]) + (y - point[1]) * (y - point[1])) > Tolerance * 20)
                    throw new InvalidDataException("Interpolation failed to reproduce a generated joint on area " + sourceArea + ".");
            }

            private static double Dot(double[] first, double[] second)
            {
                double result = 0;
                for (int i = 0; i < first.Length; i++) result += first[i] * second[i];
                return result;
            }
            private static double[] Subtract2(double[] first, double[] second) { return new[] { first[0] - second[0], first[1] - second[1] }; }
            private static double[] Add2(double[] first, double[] second) { return new[] { first[0] + second[0], first[1] + second[1] }; }
            private static double[] Scale2(double[] value, double scale) { return new[] { value[0] * scale, value[1] * scale }; }
            private static double Distance2(double[] first, double[] second) { return Norm2(Subtract2(first, second)); }
            private static double Norm2(double[] value) { return Math.Sqrt(Dot(value, value)); }
            private static double Cross2(double[] first, double[] second) { return first[0] * second[1] - first[1] * second[0]; }
        }
        private sealed class Plane
        {
            private readonly double[] origin, u, v, normal;
            private Plane(double[] origin, double[] u, double[] v, double[] normal) { this.origin = origin; this.u = u; this.v = v; this.normal = normal; }
            public static Plane FromPolygon(double[][] points)
            {
                double[] first = Subtract(points[1], points[0]);
                double[] normal = null;
                for (int i = 2; i < points.Length && normal == null; i++)
                {
                    double[] candidate = Cross(first, Subtract(points[i], points[0]));
                    if (Norm(candidate) > Tolerance) normal = Normalize(candidate);
                }
                if (normal == null) throw new InvalidOperationException("Degenerate area.");
                var plane = new Plane(points[0], Normalize(first), Normalize(Cross(normal, Normalize(first))), normal);
                if (points.Any(point => Math.Abs(plane.SignedDistance(point)) > Tolerance))
                    throw new InvalidOperationException("The area must be planar.");
                return plane;
            }
            public double SignedDistance(double[] point) { return Dot(Subtract(point, origin), normal); }
            public double[] Project(double[] point) { double[] d = Subtract(point, origin); return new[] { Dot(d, u), Dot(d, v) }; }
            public double[] VectorFromPlane(double x, double y)
            { return new[] { x * u[0] + y * v[0], x * u[1] + y * v[1], x * u[2] + y * v[2] }; }
            public double[] PointFromPlane(double x, double y)
            { return new[] { origin[0] + x * u[0] + y * v[0], origin[1] + x * u[1] + y * v[1], origin[2] + x * u[2] + y * v[2] }; }
            public double[] Normal { get { return normal; } }
        }
        private static Panel CreateDialogHeader(string title, string subtitle, int width)
        {
            var header = new Panel { Left = 0, Top = 0, Width = width, Height = 76, BackColor = UiNavy };
            header.Controls.Add(new Label
            {
                Left = 22, Top = 13, Width = width - 44, Height = 31, Text = title,
                Font = new Font("Segoe UI", 15, FontStyle.Bold), ForeColor = Color.White, BackColor = UiNavy
            });
            header.Controls.Add(new Label
            {
                Left = 24, Top = 45, Width = width - 48, Height = 20, Text = subtitle,
                Font = new Font("Segoe UI", 8.5f), ForeColor = UiBorder, BackColor = UiNavy
            });
            return header;
        }

        private static void StyleButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 1;
            button.FlatAppearance.BorderColor = primary ? UiBlue : UiBorder;
            button.BackColor = primary ? UiBlue : Color.White;
            button.ForeColor = primary ? Color.White : UiBody;
            button.Font = new Font("Segoe UI", 9, primary ? FontStyle.Bold : FontStyle.Regular);
            button.Height = 32;
            button.Cursor = Cursors.Default;
        }

        private static void AddDialogFooter(Form form, int top, int width)
        {
            form.Controls.Add(new Panel { Left = 20, Top = top, Width = width - 40, Height = 1, BackColor = UiBorder });
            int textTop = top + (form.ClientSize.Height - top - 36) / 2;
            string logoPath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "img", "logo.png");
            if (File.Exists(logoPath))
            {
                var logo = new PictureBox
                {
                    Left = 20, Top = top + 4, Width = 46, Height = 42,
                    SizeMode = PictureBoxSizeMode.Zoom, Cursor = Cursors.Hand
                };
                using (Image fileImage = Image.FromFile(logoPath)) logo.Image = new Bitmap(fileImage);
                logo.Click += delegate
                {
                    Process.Start(new ProcessStartInfo("https://www.csi-italia.eu") { UseShellExecute = true });
                };
                var tooltip = new ToolTip();
                tooltip.SetToolTip(logo, "CSi Italia");
                form.Disposed += delegate { tooltip.Dispose(); logo.Image.Dispose(); };
                form.Controls.Add(logo);
            }
            else
            {
                var company = new LinkLabel
                {
                    Left = 20, Top = textTop, Width = 55, Height = 36, Text = "CSi Italia",
                    LinkColor = UiBlue, ActiveLinkColor = UiNavy, VisitedLinkColor = UiBlue,
                    Font = new Font("Segoe UI", 8, FontStyle.Bold), Cursor = Cursors.Hand,
                    TextAlign = ContentAlignment.MiddleLeft
                };
                company.LinkClicked += delegate
                {
                    Process.Start(new ProcessStartInfo("https://www.csi-italia.eu") { UseShellExecute = true });
                };
                form.Controls.Add(company);
            }
            var licenseLink = new LinkLabel
            {
                Left = 78, Top = textTop, Width = 96, Height = 36,
                Text = L("GPL / Source", "GPL / Fonti"), Tag = "license-link",
                LinkColor = UiBlue, ActiveLinkColor = UiNavy, VisitedLinkColor = UiBlue,
                TextAlign = ContentAlignment.MiddleLeft
            };
            licenseLink.LinkClicked += delegate
            {
                string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                ShowHostModal(form.Handle, interactiveDialog == null ? Process.GetCurrentProcess().MainWindowHandle :
                    interactiveDialog.HostWindow,
                    () => MessageBox.Show(form,
                        L("Copyright (C) 2026 Leonardo Bandini. GPL version 2 or later; no warranty. " +
                          "Read LICENSE, GPL-3.0.txt and SOURCE.md in " + root + " for license terms and corresponding source.",
                          "Copyright (C) 2026 Leonardo Bandini. GPL versione 2 o successiva; nessuna garanzia. " +
                          "Leggere LICENSE, GPL-3.0.txt e SOURCE.md in " + root + " per licenze e sorgenti corrispondenti."),
                        WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Information));
            };
            form.Controls.Add(licenseLink);
            form.Controls.Add(new Label
            {
                Left = width - 180, Top = textTop, Width = 160, Height = 36,
                Text = L("Version ", "Versione ") + PluginVersion, TextAlign = ContentAlignment.MiddleRight,
                ForeColor = UiMuted, Tag = "version"
            });
        }

        // This small window has its own message loop. Even when a large SAP API
        // call stalls the options thread, the user can always request cancellation.
        private sealed class CancellationController
        {
            private readonly bool italian;
            private readonly Rectangle? anchor;
            private readonly IntPtr ownerHandle;
            private readonly object progressLock = new object();
            private string progressMessage;
            private string progressArea = "";
            private int? progressPercent;
            private int requested, stopped;
            private Form window;
            public bool Requested { get { return Volatile.Read(ref requested) != 0; } }

            public CancellationController(bool italian, Rectangle? anchor = null, IntPtr ownerHandle = default(IntPtr))
            {
                this.italian = italian;
                this.anchor = anchor;
                this.ownerHandle = ownerHandle;
                progressMessage = italian ? "Preparazione della mesh..." : "Preparing the mesh...";
                var thread = new Thread(Run);
                thread.IsBackground = true;
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
            }

            public void Report(string message, int? percent = null)
            {
                lock (progressLock)
                {
                    progressMessage = message;
                    progressPercent = percent;
                }
            }

            public void ReportArea(string message)
            {
                lock (progressLock) progressArea = message;
            }

            public void Request()
            {
                Interlocked.Exchange(ref requested, 1);
                Form form = window;
                if (form == null || !form.IsHandleCreated || form.IsDisposed) return;
                try
                {
                    form.BeginInvoke((Action)delegate
                    {
                        try
                        {
                            if (form.IsDisposed) return;
                            foreach (Control control in form.Controls)
                                if (control is Button) control.Enabled = false;
                        }
                        catch (Exception error) { LogPluginException(error); }
                    });
                }
                catch (InvalidOperationException) { }
            }

            public void Stop()
            {
                Interlocked.Exchange(ref stopped, 1);
                Form form = window;
                if (form == null || !form.IsHandleCreated || form.IsDisposed) return;
                try { form.BeginInvoke((Action)delegate
                    {
                        try { if (!form.IsDisposed) form.Close(); }
                        catch (Exception error) { LogPluginException(error); }
                    }); }
                catch (InvalidOperationException) { }
            }

            private void Run()
            {
                System.Threading.ThreadExceptionEventHandler uiError = delegate(object sender,
                    System.Threading.ThreadExceptionEventArgs args)
                {
                    LogPluginException(args.Exception);
                    Interlocked.Exchange(ref requested, 1);
                };
                Application.ThreadException += uiError;
                try { RunWindow(); }
                catch (Exception error)
                {
                    // An unhandled exception on a background STA would terminate
                    // the entire SAP2000 process, not just the cancel window.
                    LogPluginException(error);
                    Interlocked.Exchange(ref requested, 1);
                }
                finally
                {
                    window = null;
                    Application.ThreadException -= uiError;
                }
            }

            private void RunWindow()
            {
                Thread.Sleep(750);
                if (Volatile.Read(ref stopped) != 0) return;
                using (var form = new Form())
                {
                    form.Text = italian ? "MESHing - Generazione" :
                        "MESHing - Generation";
                    form.ClientSize = new Size(450, 194);
                    form.Font = new Font("Segoe UI", 9);
                    form.FormBorderStyle = FormBorderStyle.FixedDialog;
                    form.MaximizeBox = form.MinimizeBox = false;
                    form.ShowInTaskbar = false;
                    form.StartPosition = FormStartPosition.Manual;
                    Rectangle screen = anchor.HasValue ? Screen.FromRectangle(anchor.Value).WorkingArea :
                        Screen.PrimaryScreen.WorkingArea;
                    int centerX = anchor.HasValue ? anchor.Value.Left + anchor.Value.Width / 2 : screen.Left + screen.Width / 2;
                    int centerY = anchor.HasValue ? anchor.Value.Top + anchor.Value.Height / 2 : screen.Top + screen.Height / 2;
                    form.Location = new Point(
                        Math.Max(screen.Left, Math.Min(screen.Right - form.Width, centerX - form.Width / 2)),
                        Math.Max(screen.Top, Math.Min(screen.Bottom - form.Height, centerY - form.Height / 2)));
                    var message = new Label { Left = 16, Top = 17, Width = 418, Height = 62,
                        Text = progressMessage };
                    var area = new Label { Left = 16, Top = 83, Width = 418, Height = 24,
                        Font = new Font("Segoe UI", 9, FontStyle.Bold) };
                    var progress = new ProgressBar { Left = 16, Top = 115, Width = 418, Height = 20,
                        Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 28 };
                    var button = new Button { Left = 244, Top = 151, Width = 190, Height = 30,
                        Text = italian ? "Annulla generazione" : "Cancel generation" };
                    var timer = new System.Windows.Forms.Timer { Interval = 100 };
                    timer.Tick += delegate
                    {
                        if (Requested)
                        {
                            message.Text = italian ? "Interruzione richiesta. Attendere il ripristino del modello." :
                                "Cancellation requested. Restoring the model...";
                            area.Text = "";
                            button.Enabled = false;
                            progress.Style = ProgressBarStyle.Marquee;
                            progress.MarqueeAnimationSpeed = 28;
                            return;
                        }
                        string text;
                        string areaText;
                        int? percent;
                        lock (progressLock) { text = progressMessage; areaText = progressArea; percent = progressPercent; }
                        if (message.Text != text) message.Text = text;
                        if (area.Text != areaText) area.Text = areaText;
                        if (percent.HasValue)
                        {
                            if (progress.Style != ProgressBarStyle.Continuous)
                            {
                                progress.MarqueeAnimationSpeed = 0;
                                progress.Style = ProgressBarStyle.Continuous;
                            }
                            progress.Value = percent.Value;
                        }
                        else if (progress.Style != ProgressBarStyle.Marquee)
                        {
                            progress.Style = ProgressBarStyle.Marquee;
                            progress.MarqueeAnimationSpeed = 28;
                        }
                    };
                    button.Click += delegate { Request(); };
                    form.FormClosing += delegate(object sender, FormClosingEventArgs e)
                    {
                        if (Volatile.Read(ref stopped) == 0) { Request(); e.Cancel = true; }
                    };
                    form.Shown += delegate { if (Volatile.Read(ref stopped) != 0) form.Close(); };
                    form.Controls.Add(message);
                    form.Controls.Add(area);
                    form.Controls.Add(progress);
                    form.Controls.Add(button);
                    form.AcceptButton = button;
                    window = form;
                    if (Volatile.Read(ref stopped) == 0)
                    {
                        timer.Start();
                        if (ownerHandle != IntPtr.Zero && IsWindow(ownerHandle))
                            form.Show(new WindowOwner(ownerHandle));
                        else form.Show();
                        Application.Run(new ApplicationContext(form));
                    }
                    timer.Dispose();
                    window = null;
                }
            }
        }

        private sealed class MeshOptionsDialog : Form
        {
            private sealed class SavedOptions
            {
                public SavedOptions() { }
                public double SizeMeters { get; set; }
                public double MinMeters { get; set; }
                public string Language { get; set; }
                public bool? FastMode { get; set; }
                public bool? CreateBackup { get; set; }
                // Read the two sizes from settings written by earlier versions.
                public GmshPreviewPanel.Values GmshPreview { get; set; }
            }
            private static string OptionsFile { get {
                string testPath = Environment.GetEnvironmentVariable("SAP_GMSH_V2_SETTINGS_PATH");
                if (!string.IsNullOrEmpty(testPath)) return testPath;
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SAP2000QuadMesher", "gmsh-v2-options.json");
            } }

            private static SavedOptions ReadSavedOptions()
            {
                try
                {
                    return File.Exists(OptionsFile) ? new JavaScriptSerializer().Deserialize<SavedOptions>(
                        File.ReadAllText(OptionsFile, Encoding.UTF8)) : null;
                }
                catch (IOException) { return null; }
                catch (UnauthorizedAccessException) { return null; }
                catch (ArgumentException) { return null; }
                catch (InvalidOperationException) { return null; }
            }

            internal static bool InitialItalian()
            {
                // Atomic runs must not inherit the interactive user's preferences.
                SavedOptions saved = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SIZE"))
                    ? ReadSavedOptions() : null;
                if (saved != null && saved.Language == "it") return true;
                if (saved != null && saved.Language == "en") return false;
                return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
            }

            private void RememberOptions()
            {
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SIZE"))) return;
                try
                {
                    if (gmshPreviewOptions == null) return;
                    double maximum, minimum;
                    bool validMaximum = TryParseLength(gmshPreviewOptions.MaxSize.Text, units, out maximum);
                    bool validMinimum = Regex.IsMatch(gmshPreviewOptions.MinSize.Text ?? "",
                        @"^\s*0+(?:[.,]0+)?\s*(?:mm|cm|m|ft|in)?\s*$",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    minimum = 0;
                    if (!validMinimum) validMinimum = TryParseLength(gmshPreviewOptions.MinSize.Text, units, out minimum);
                    SavedOptions options = ReadSavedOptions() ?? new SavedOptions();
                    if (validMaximum && validMinimum && minimum < maximum)
                    {
                        options.SizeMeters = maximum * MetersPerUnit(units);
                        options.MinMeters = minimum * MetersPerUnit(units);
                    }
                    else if (options.GmshPreview != null)
                    {
                        if (options.SizeMeters <= 0) options.SizeMeters = options.GmshPreview.MaxMeters;
                        options.MinMeters = options.GmshPreview.MinMeters;
                    }
                    options.Language = useItalian ? "it" : "en";
                    options.CreateBackup = createBackup.Checked;
                    // Persist the generation tab, but never its additional-tab
                    // regular-grid switch (which belongs to this session only).
                    if (validMaximum && validMinimum && minimum < maximum)
                    {
                        options.GmshPreview = gmshPreviewOptions.Capture(MetersPerUnit(units));
                        options.GmshPreview.FavorRegularGrid = false;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(OptionsFile));
                    string temporary = OptionsFile + ".tmp";
                    File.WriteAllText(temporary, new JavaScriptSerializer().Serialize(new {
                        options.SizeMeters, options.MinMeters, options.Language,
                        options.CreateBackup, options.GmshPreview }), Encoding.UTF8);
                    if (File.Exists(OptionsFile)) File.Replace(temporary, OptionsFile, null);
                    else File.Move(temporary, OptionsFile);
                }
                catch (IOException) { /* A read-only profile must not prevent meshing. */ }
                catch (UnauthorizedAccessException) { }
            }

            private void RestoreOptions()
            {
                SavedOptions saved = ReadSavedOptions();
                if (saved == null) return;
                if (saved.CreateBackup.HasValue) createBackup.Checked = saved.CreateBackup.Value;
                if (gmshPreviewOptions != null)
                {
                    if (saved.GmshPreview != null)
                        gmshPreviewOptions.Restore(saved.GmshPreview);
                    double maximum = saved.SizeMeters > 0 ? saved.SizeMeters :
                        saved.GmshPreview == null ? 0 : saved.GmshPreview.MaxMeters;
                    double minimum = saved.GmshPreview == null ? saved.MinMeters :
                        saved.GmshPreview.MinMeters;
                    if (maximum > 0 && !double.IsNaN(maximum) && !double.IsInfinity(maximum) &&
                        minimum >= 0 && minimum < maximum && !double.IsNaN(minimum) && !double.IsInfinity(minimum))
                    {
                        gmshPreviewOptions.MaxSize.Text = DefaultLength(maximum, units);
                        gmshPreviewOptions.MinSize.Text = DefaultLength(minimum, units);
                    }
                }
            }
            private readonly TextBox size = new TextBox();
            private readonly NumericUpDown edgeTolerance = new NumericUpDown();
            private int rememberedTolerance = 20;
            private readonly CheckBox strictMaximumSize = new CheckBox();
            private readonly CheckBox symmetric = new CheckBox();
            private readonly ComboBox symmetryAxis = new ComboBox();
            private readonly TabControl profiles = new TabControl();
            private readonly CheckBox generateHoles = new CheckBox();
            private readonly Label openingGroupLabel = new Label();
            private readonly ComboBox openingGroup = new ComboBox();
            private readonly CheckBox generateCircularHoles = new CheckBox();
            private readonly Label holeGroupLabel = new Label();
            private readonly ComboBox holeGroup = new ComboBox();
            private readonly Label holeDiameterLabel = new Label();
            private readonly TextBox holeDiameter = new TextBox();
            private readonly ToolTip measurementToolTip = new ToolTip();
            private readonly CheckBox preferRegular = new CheckBox();
            private readonly CheckBox stressRefinement = new CheckBox();
            private readonly CheckBox structuredSectorBands = new CheckBox();
            private readonly CheckBox fastMode = new CheckBox();
            private readonly CheckBox createBackup = new CheckBox();
            private readonly Label createBackupDescription = new Label();
            private readonly CheckBox useMaximumCores = new CheckBox();
            private readonly NumericUpDown workerLimit = new NumericUpDown();
            private readonly Label status = new Label();
            private string preservationWarning;
            private bool designTransferLossWarning;
            private readonly Dictionary<string, HashSet<string>> transferWarnings =
                new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            private int reportedTransferWarnings;
            private readonly System.Windows.Forms.Timer elapsedTimer = new System.Windows.Forms.Timer();
            private readonly Stopwatch meshClock = new Stopwatch();
            private readonly Stopwatch operationClock = new Stopwatch();
            private int meshAreasCompleted;
            private int meshAreasTotal;
            private bool meshingActive;
            private readonly Button generate = new Button();
            private readonly Button details = new Button();
            private readonly Button undo = new Button();
            private readonly ComboBox language = new ComboBox();
            private readonly LinkLabel verifiedExamples = new LinkLabel();
            private readonly LinkLabel updateNotice = new LinkLabel();
            private readonly System.Windows.Forms.Timer updateBlinkTimer = new System.Windows.Forms.Timer();
            private Font updateNoticeBoldFont;
            private readonly Label headerTitle;
            private readonly Label headerSubtitle;
            private readonly Panel selectionSummary = new Panel();
            private readonly Panel selectionAccent = new Panel();
            private readonly Label selectionHeader = new Label();
            private readonly Label areaSelection = new Label();
            private readonly Label objectSelection = new Label();
            private readonly Label createdElements = new Label();
            private int selectedAreas, selectedQuads, selectedTriangles, selectedPolygons, selectedFrameCount, selectedLinks, selectedJoints;
            private int generatedElementCount;
            private MeshInventory generatedInventory;
            private bool hasFrames;
            private bool hasSelectedAreas;
            private bool hasSymmetryAxes;
            private SymmetryAxis doubleSymmetryAxis;
            private SymmetryAxis[] symmetryAxes;
            private string units;
            private readonly CheckBox gmshOGrid = new CheckBox();
            private readonly CheckBox gmshPreserveSelectedEdges = new CheckBox();
            private readonly TextBox gmshExtra = new TextBox(), gmshEffectivePreview = new TextBox();
            private readonly Button gmshResetDefault = new Button();
            private readonly TabControl gmshPreviewTabs = new TabControl();
            private readonly TabPage gmshPreviewEmptyTab = new TabPage("Gmsh");
            private readonly TabPage gmshHolesTab = new TabPage(), gmshAdvancedTab = new TabPage();
            private readonly TabPage gmshSapTab = new TabPage();
            private GmshPreviewPanel gmshPreviewOptions;
            public int SelectedGmshAlgorithm { get { return gmshPreviewOptions.AlgorithmValue; } }
            private readonly Label gmshSymmetryAxisLabel = new Label();
            private readonly Label gmshAdvancedLabel = new Label(), gmshAdvancedPreviewLabel = new Label();
            private readonly Label gmshAdvancedHint = new Label();
            private readonly Label gmshSymmetryHint = new Label();

            public string GmshSettingsJson
            {
                get
                {
                    var options = new Dictionary<string, double>();
                    // Do not apply debug overrides while their tab
                    // is hidden and the user cannot inspect or change them.
                    foreach (string raw in gmshPreviewTabs.TabPages.Contains(gmshAdvancedTab)
                        ? gmshExtra.Lines : new string[0])
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#")) continue;
                        string[] pair = line.Split(new[] { '=' }, 2);
                        double number;
                        if (pair.Length != 2 || !Regex.IsMatch(pair[0].Trim(), @"^Mesh\.[A-Za-z][A-Za-z0-9]*$") ||
                            !double.TryParse(pair[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out number) ||
                            double.IsNaN(number) || double.IsInfinity(number))
                            throw new InvalidOperationException("Invalid Gmsh option: " + line +
                                ". Use Mesh.Option = number (dot decimal separator).");
                        if (!GmshPreviewPanel.ControlsOption(pair[0].Trim()))
                        {
                            if (options.ContainsKey(pair[0].Trim()))
                                throw new InvalidOperationException("Duplicate Gmsh option: " + pair[0].Trim());
                            options.Add(pair[0].Trim(), number);
                        }
                    }
                    gmshPreviewOptions.ApplyOptions(options);
                    if (gmshOGrid.Checked && (!GenerateCircularHoles || HoleGroup == null))
                        throw new InvalidOperationException("Choose a circular-hole point group before enabling the O-grid.");
                    var settings = new {
                        algorithm = gmshPreviewOptions.AlgorithmValue,
                        recombine = gmshPreviewOptions.RecombineValue,
                        recombination = gmshPreviewOptions.RecombinationValue,
                        optimize = gmshPreviewOptions.OptimizeValue,
                        optimizeMethod = gmshPreviewOptions.OptimizerValue,
                        optimizeIterations = gmshPreviewOptions.IterationsValue,
                        smoothing = gmshPreviewOptions.SmoothingValue,
                        qualityTolerancePercent = 20,
                        preserveSelectedEdges = gmshPreserveSelectedEdges.Checked,
                        oGrid = gmshOGrid.Checked,
                        exactSymmetry = symmetric.Checked,
                        structured = new { mode = gmshPreviewOptions.RegularGridValue ? "automatic" : "unstructured",
                            nodes = 5, distribution = "Progression",
                            coefficient = 1, arrangement = "Left", cornerAngle = 2.3561944901923448 },
                        localField = (object)null,
                        fieldGraph = (object)null,
                        options,
                        previewMode = true
                    };
                    return new JavaScriptSerializer().Serialize(settings);
                }
            }
            private bool working;
            private string regionalSignature;
            private string regionalModelPath;
            private Action regionalSafetyLock;
            private bool regionalUnsafe;
            private bool regionalModelModified;
            private bool regionalSaveStarted;
            private bool regionalWarningShown;
            public bool RegionalUnsafe { get { return regionalUnsafe; } }
            public bool RegionalModelModified { get { return regionalModelModified; } }
            public string RegionalModelPath { get { return regionalModelPath; } }
            public void SetRegionalModelPath(string path) { regionalModelPath = path; }
            public void SetRegionalSafetyLock(Action callback) { regionalSafetyLock = callback; }
            public void BeginRegionalTransaction()
            {
                if (HostRegionalSettingsChanged())
                {
                    regionalUnsafe = true;
                    return;
                }
                regionalSignature = WindowsRegionalSignature();
                regionalModelModified = false;
                regionalSaveStarted = false;
            }
            public void MarkRegionalSaveStarted() { regionalSaveStarted = true; }
            public void MarkRegionalModelModified() { regionalModelModified = true; }
            public bool DetectRegionalChange()
            {
                if (regionalUnsafe) return true;
                if (HostRegionalSettingsChanged()) return regionalUnsafe = true;
                if (regionalSignature == null) return false;
                try { regionalUnsafe = regionalSignature != WindowsRegionalSignature(); }
                catch (InvalidOperationException) { regionalUnsafe = true; }
                return regionalUnsafe;
            }
            protected override void WndProc(ref Message message)
            {
                base.WndProc(ref message);
                // Windows broadcasts WM_SETTINGCHANGE when the user edits the
                // regional format. Latch it even between generation checkpoints.
                if (message.Msg == 0x001A)
                    DetectRegionalChange();
            }
            public void AbandonForRegionalChange()
            {
                if (!regionalUnsafe || regionalWarningShown) return;
                regionalWarningShown = true;
                if (regionalModelModified && regionalSafetyLock != null)
                    try { regionalSafetyLock(); }
                    catch (Exception error) { LogPluginException(error); }
                StopCancellationController();
                working = false;
                ControlBox = true;
                generate.Enabled = undo.Enabled = gmshResetDefault.Enabled = false;
                string host = HostProductName();
                string message = regionalModelModified
                    ? L("Windows regional settings changed during this " + host + " session. The open model may contain both original and generated elements. Do not save it: saving could permanently damage the model. Close the model WITHOUT SAVING, exit " + host + " and restart it before reopening the model saved before Generate. Never change regional settings while " + host + " is open. MESHing will not Apply or Undo with incompatible table settings.",
                        "Le impostazioni internazionali di Windows sono cambiate durante questa sessione di " + host + ". Il modello aperto può contenere elementi originali e generati. NON salvarlo: il salvataggio potrebbe compromettere definitivamente il modello. Chiudere il modello SENZA SALVARE, chiudere " + host + " e riavviarlo prima di riaprire il modello salvato prima di Genera. Non cambiare le impostazioni internazionali mentre " + host + " è aperto. MESHing non eseguirà Applica o Annulla con tabelle in un formato diverso.")
                    : regionalSaveStarted
                    ? L("Windows regional settings changed while " + host + " was open, possibly during the pre-mesh save. No mesh elements were inserted. Close the model WITHOUT SAVING, exit and restart " + host + ". Check the saved model before reusing it; prefer the numbered backup if its save was affected. Never change regional settings while " + host + " is open.",
                        "Le impostazioni internazionali di Windows sono cambiate con " + host + " aperto, eventualmente durante il salvataggio pre-mesh. Nessun elemento di mesh è stato inserito. Chiudere il modello SENZA SALVARE, chiudere e riavviare " + host + ". Controllare il modello su disco prima di riutilizzarlo; preferire il backup numerato se il salvataggio è stato compromesso. Non cambiare le impostazioni internazionali mentre " + host + " è aperto.")
                    : HostRegionalWarningMessage(host);
                if (IsDisposed) MessageBox.Show(message, WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else
                {
                    ShowHostMessage(message, WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    Close();
                }
            }
            private bool skipGenerateBackupWarning;
            private bool skipNextGenerationWarning;
            private bool committingGeneration;
            private bool confirmationOnlyApply;
            public void SetConfirmationOnlyApply(bool enabled) { confirmationOnlyApply = enabled; }
            private Func<string> applyBackupInfo;
            private string insertionNotice;
            private bool closingPreview;
            private int cancellationRequested;
            private long lastCancellationPump;
            private CancellationController cancellationController;
            private IntPtr viewportWindow;
            private bool viewportWasDisabled;
            private bool viewportEnabledByPlugin;
            private bool commitWindowLocked;
            public bool Applied { get; private set; }
            public bool PreviewRestoredOnClose { get; private set; }
            public bool CancelRequested
            {
                get { return Volatile.Read(ref cancellationRequested) != 0 ||
                    (cancellationController != null && cancellationController.Requested); }
                private set { Volatile.Write(ref cancellationRequested, value ? 1 : 0); }
            }
            public void ReportUiFailure()
            {
                if (working)
                {
                    CancelRequested = true;
                    if (cancellationController != null) cancellationController.Request();
                    if (cancellationController != null)
                        cancellationController.Report(L("Interface error: cancelling and restoring the model...",
                            "Errore dell'interfaccia: annullamento e ripristino del modello in corso..."));
                }
                else
                    status.Text = L("An interface error was recorded. Review the model before continuing.",
                        "È stato registrato un errore dell'interfaccia. Controllare il modello prima di continuare.");
            }
            public void PumpCancellation()
            {
                if (CancelRequested) throw new OperationCanceledException("Mesh generation cancelled by the user.");
                long now = Stopwatch.GetTimestamp();
                if (now - lastCancellationPump < Stopwatch.Frequency / 20) return;
                lastCancellationPump = now;
                ThrowIfCancellationRequested();
                Application.DoEvents();
                ThrowIfCancellationRequested();
            }
            private void StopCancellationController()
            {
                CancellationController active = cancellationController;
                // The progress window may receive Cancel immediately before
                // Complete stops its thread. Keep that request latched until
                // the final generation checkpoint has run.
                if (active != null && active.Requested) CancelRequested = true;
                cancellationController = null;
                if (active != null) active.Stop();
            }
            private bool checkingUpdates;
            private bool modelLockWarningShown;
            private bool requiresSavedModel;
            private bool updateCheckSucceeded;
            private bool updateBlinkOn;
            private string availableVersion;
            private string availableReleaseUrl;
            private string availableDownloadUrl;
            private MeshQualityReport qualityReport;
            private string generationCompletionStatus;
            private Action undoAction;
            private Action applyAction;
            private Action appliedUndoAction;
            private string snapshotPath;
            private Func<bool> completedModelGuard;
            private Func<bool> generationFilterCheck;
            private Action onApplied;
            private Action releaseCompletedUndo;
            private Action<string> releasePreviewModelLock;
            public double MaxSize { get; private set; }
            public int EdgeTolerancePercent { get { return strictMaximumSize.Checked ? 0 : (int)edgeTolerance.Value; } }
            public bool StrictMaximumSize { get { return strictMaximumSize.Checked; } }
            public bool Symmetric { get { return symmetric.Checked; } }
            public SymmetryAxis SelectedSymmetryAxis { get { return DoubleSymmetrySelected
                ? doubleSymmetryAxis : symmetryAxis.SelectedItem as SymmetryAxis; } }
            public bool DoubleSymmetrySelected { get { return (symmetryAxis.SelectedItem as SymmetryAxis) != null &&
                (symmetryAxis.SelectedItem as SymmetryAxis).SecondaryDirection != null; } }
            public bool GenerateHoles { get { return generateHoles.Checked; } }
            public string OpeningGroup { get { return openingGroup.SelectedItem as string; } }
            public bool GenerateCircularHoles { get { return
                generateCircularHoles.Checked;
            } }
            public string HoleGroup { get { return holeGroup.SelectedItem as string; } }
            public double HoleDiameter { get; private set; }
            public bool PreferRegularMesh { get { return preferRegular.Checked; } }
            public bool StressRefinement { get { return stressRefinement.Checked; } }
            public bool StructuredSectorBands { get { return structuredSectorBands.Checked; } }
            public bool FastMode { get { return fastMode.Checked; } }
            public bool CreateBackup { get { return createBackup.Checked; } }
            public bool PreserveSelectedBoundaryEdges { get { return gmshPreserveSelectedEdges.Checked; } }
            public int GmshMaximumSizeTolerancePercent { get { return 20; } }
            public int CandidateWorkers { get { return useMaximumCores.Checked ? Environment.ProcessorCount : (int)workerLimit.Value; } }
            public bool StartRequested { get; private set; }
            public bool RetryRequested { get; private set; }
            public Exception UndoFailure { get; private set; }
            public bool CanRefreshSelection { get { return !working &&
                (Applied || qualityReport == null && undoAction == null); } }
            private string SaveModelFirstText()
            {
                return L("Save this new model in " + HostProductName() + " before generating a mesh with MESHing.",
                    "Salvare questo nuovo modello in " + HostProductName() + " prima di generare la mesh con MESHing.");
            }
            public void SetRequiresSavedModel(bool required)
            {
                if (requiresSavedModel == required) return;
                requiresSavedModel = required;
                UpdateOptionState();
                if (required)
                {
                    generate.Enabled = details.Enabled = undo.Enabled = false;
                    status.Text = SaveModelFirstText();
                }
                else if (!working && qualityReport == null)
                    status.Text = hasSelectedAreas ? L("Ready. Selection updated.", "Pronto. Selezione aggiornata.") :
                        L("Select at least one area in " + HostProductName() + "; this window updates automatically.",
                          "Selezionare almeno un'area in " + HostProductName() + ": questa finestra si aggiorna automaticamente.");
            }
            public void ShowNeverSavedModelWarning()
            {
                ShowHostMessage(L("This model has never been saved. Save it in " + HostProductName() +
                    " before meshing with MESHing. The options and help remain available in the meantime.",
                    "Questo modello non è mai stato salvato. Salvarlo in " + HostProductName() +
                    " prima di procedere con la meshatura tramite MESHing. Opzioni e guida restano consultabili."),
                    WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            public string LengthUnit { get { return units; } }
            public void ChangeLengthUnit(string nextUnit)
            {
                if (string.Equals(units, nextUnit, StringComparison.OrdinalIgnoreCase)) return;
                string oldUnit = units;
                double diameter;
                bool validDiameter = TryParseLength(holeDiameter.Text, oldUnit, out diameter);
                string diameterText = holeDiameter.Text;
                GmshPreviewPanel.Values savedSizes = gmshPreviewOptions.Capture(MetersPerUnit(oldUnit));
                units = nextUnit;
                gmshPreviewOptions.ChangeLengthUnit(nextUnit, savedSizes);
                if (validDiameter && !Regex.IsMatch(diameterText ?? "", @"(?:mm|cm|m|ft|in)\s*$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    holeDiameter.Text = DefaultLength(diameter * MetersPerUnit(oldUnit), nextUnit);
                UpdateGmshLanguage();
            }
            public void SetCompletedModelGuard(Func<bool> currentModel)
            {
                completedModelGuard = currentModel;
            }
            public void SetOnApplied(Action callback) { onApplied = callback; }
            public void SetReleaseCompletedUndo(Action callback) { releaseCompletedUndo = callback; }
            public void ReleaseCompletedUndo()
            {
                Action release = releaseCompletedUndo;
                if (release == null) return;
                release();
                releaseCompletedUndo = null;
            }
            public void CommitForGeneration()
            {
                if (qualityReport == null || Applied) return;
                committingGeneration = true;
                try { generate.PerformClick(); }
                finally { committingGeneration = false; }
                if (!Applied && UndoFailure == null)
                    throw new InvalidOperationException("Generate did not commit the mesh.");
                if (Applied && generationCompletionStatus != null)
                {
                    status.Text = generationCompletionStatus;
                    measurementToolTip.SetToolTip(status, "");
                }
            }
            public void SetReleasePreviewModelLock(Action<string> callback)
            { releasePreviewModelLock = callback; }
            private bool ReleasePreviewModelLock(string operation)
            {
                try
                {
                    if (releasePreviewModelLock != null) releasePreviewModelLock(operation);
                    releasePreviewModelLock = null;
                    return true;
                }
                catch (Exception error)
                {
                    status.Text = LocalizeErrorMessage(error.Message);
                    ShowHostMessage(status.Text, WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false; // Do not run a destructive action on the wrong/locked model.
                }
            }
            public void SetGenerationFilterCheck(Func<bool> callback)
            {
                generationFilterCheck = callback;
            }
            public void ShowSelectionOnlyFilterWarning(string product)
            {
                string message = L(
                    product + " is returning model tables filtered to selected objects. Open \"Display->Show Tables\", " +
                    "turn off \"Selection Only\" in the upper-right corner, close the table, then click Generate again.",
                    product + " restituisce tabelle del modello filtrate agli oggetti selezionati. Apri \"Display->Show Tables\", " +
                    "disattiva \"Selection Only\" in alto a destra, chiudi la tabella e premi di nuovo Genera.");
                ShowHostMessage(message, WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            public void SetInsertionNotice(string notice) { insertionNotice = notice; }
            public void SetApplyBackupInfo(Func<string> callback) { applyBackupInfo = callback; }
            private bool ConfirmGenerateWithoutBackup()
            {
                if (createBackup.Checked || skipGenerateBackupWarning ||
                    !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SIZE"))) return true;
                using (var choice = new Form())
                {
                    choice.Text = L("Back up the model before Generate?",
                        "Creare un backup del modello prima di Genera?");
                    choice.ClientSize = new Size(510, 226);
                    choice.Font = Font;
                    choice.BackColor = Color.White;
                    choice.FormBorderStyle = FormBorderStyle.FixedDialog;
                    choice.StartPosition = FormStartPosition.CenterParent;
                    choice.MaximizeBox = false;
                    choice.MinimizeBox = false;
                    choice.ShowInTaskbar = false;
                    ApplyPluginIcon(choice);
                    choice.Controls.Add(new Label {
                        Left = 18, Top = 15, Width = 474, Height = 85,
                        Text = L("The numbered backup is OFF. Generate will save the current model over its SDB/BDB " +
                            "before meshing. Enable the backup to keep a copy of the previously saved file; " +
                            "it will not contain unsaved edits.",
                            "Il backup numerato è DISATTIVATO. Genera salverà il modello corrente sul suo SDB/BDB " +
                            "prima della mesh. Attivare il backup per conservare il file salvato in precedenza; " +
                            "non conterrà le modifiche non ancora salvate.")
                    });
                    var enableBackup = new CheckBox {
                        Left = 18, Top = 107, Width = 474, Height = 27,
                        Text = L("Enable backup in Backup options for this generation (recommended)",
                            "Attiva il backup in Opzioni di backup per questa generazione (consigliato)")
                    };
                    choice.Controls.Add(enableBackup);
                    var suppress = new CheckBox {
                        Left = 18, Top = 145, Width = 474, Height = 25,
                        Text = L("Do not show this backup warning again",
                            "Non mostrare più questo avviso sul backup")
                    };
                    choice.Controls.Add(suppress);
                    var cancel = new Button { Left = 223, Top = 182, Width = 125, Height = 29,
                        Text = L("Cancel", "Annulla"), DialogResult = DialogResult.Cancel };
                    var proceed = new Button { Left = 360, Top = 182, Width = 132, Height = 29,
                        Text = L("Continue Generate", "Continua Genera"), DialogResult = DialogResult.Yes };
                    StyleButton(cancel, false);
                    StyleButton(proceed, true);
                    choice.Controls.Add(cancel); choice.Controls.Add(proceed);
                    choice.AcceptButton = cancel;
                    choice.CancelButton = cancel;
                    bool confirmed = ShowHostDialog(choice) == DialogResult.Yes;
                    if (enableBackup.Checked)
                    {
                        createBackup.Checked = true;
                        RememberOptions();
                    }
                    if (!confirmed) return false;
                    if (suppress.Checked) skipGenerateBackupWarning = true;
                }
                RememberOptions();
                return true;
            }
            private bool ConfirmNextGeneration()
            {
                if (qualityReport == null || skipNextGenerationWarning ||
                    !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SIZE"))) return true;
                using (var choice = new Form())
                {
                    choice.Text = L("Generate another mesh?", "Generare un'altra mesh?");
                    choice.ClientSize = new Size(510, 198);
                    choice.Font = Font; choice.BackColor = Color.White;
                    choice.FormBorderStyle = FormBorderStyle.FixedDialog;
                    choice.StartPosition = FormStartPosition.CenterParent;
                    choice.MaximizeBox = choice.MinimizeBox = choice.ShowInTaskbar = false;
                    ApplyPluginIcon(choice);
                    choice.Controls.Add(new Label { Left = 18, Top = 15, Width = 474, Height = 95,
                        Text = L("Generate will save the current model before creating the next mesh. " +
                            "Undo will then restore only the model saved before this next generation; " +
                            "the previous generation can no longer be undone.",
                            "Genera salverà il modello corrente prima di creare la prossima mesh. " +
                            "Annulla ripristinerà soltanto il modello salvato prima di questa nuova generazione; " +
                            "la generazione precedente non potrà più essere annullata.") });
                    var suppress = new CheckBox { Left = 18, Top = 116, Width = 474, Height = 25,
                        Text = L("Do not show this warning again in this session",
                            "Non mostrare più questo avviso in questa sessione") };
                    choice.Controls.Add(suppress);
                    var cancel = new Button { Left = 230, Top = 153, Width = 120, Height = 30,
                        Text = L("Cancel", "Annulla"), DialogResult = DialogResult.Cancel };
                    var proceed = new Button { Left = 362, Top = 153, Width = 130, Height = 30,
                        Text = "OK", DialogResult = DialogResult.OK };
                    StyleButton(cancel, false); StyleButton(proceed, true);
                    choice.Controls.Add(cancel); choice.Controls.Add(proceed);
                    choice.AcceptButton = cancel; choice.CancelButton = cancel;
                    if (ShowHostDialog(choice) != DialogResult.OK) return false;
                    if (suppress.Checked) skipNextGenerationWarning = true;
                    return true;
                }
            }
            private bool ConfirmApply()
            {
                if (designTransferLossWarning)
                {
                    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SIZE")))
                        return Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_ALLOW_DESIGN_LOSS") == "1";
                    string backup = applyBackupInfo == null ? null : applyBackupInfo();
                    string warning = confirmationOnlyApply ? L(
                        "The generated mesh has no Concrete Shell Design on its areas. Confirming it discards Undo; " +
                        "reassign design procedures and overwrites on the new shells. Undo reloads the pre-mesh saved model.",
                        "La mesh generata non ha Concrete Shell Design sulle nuove aree. Confermandola si rinuncia ad " +
                        "Annulla; riassegnare le procedure e le sovrascritture di progetto alle nuove shell. " +
                        "Annulla ricarica il modello salvato prima della mesh.") : L(
                        "SAP2000 did not transfer Concrete Shell Design to the generated areas: they have No Design. " +
                        "Applying this mesh will lose the original shell design procedure; design overwrites must be " +
                        "assigned again on the new shells. Undo mesh will no longer be available after Apply. " +
                        "Continue only if you accept this loss and have checked the original model/backup.",
                        "SAP2000 non ha trasferito Concrete Shell Design alle nuove aree: risultano No Design. " +
                        "Applicando questa mesh perderai la procedura di progetto originale; eventuali sovrascritture " +
                        "Concrete Shell Design andranno riassegnate alle nuove shell. Dopo Applica non sarà più disponibile " +
                        "Annulla mesh. Continua solo se accetti questa perdita e hai controllato il modello originale/backup.");
                    if (!string.IsNullOrEmpty(backup) && File.Exists(backup))
                        warning += Environment.NewLine + Environment.NewLine +
                            L("Numbered backup: ", "Backup numerato: ") + backup;
                    return ShowHostMessage(warning,
                        L("Shell design assignments will be lost", "Perdita delle assegnazioni di progetto shell"),
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                }
                // Atomic fixtures choose Apply/Undo explicitly in the runner.
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SIZE"))) return true;
                using (var choice = new Form())
                {
                    choice.Text = confirmationOnlyApply ? L("Confirm generated mesh?", "Confermare la mesh generata?") :
                        L("Apply generated mesh?", "Applicare la mesh generata?");
                    choice.ClientSize = new Size(510, 207);
                    choice.Font = Font;
                    choice.BackColor = Color.White;
                    choice.FormBorderStyle = FormBorderStyle.FixedDialog;
                    choice.StartPosition = FormStartPosition.CenterParent;
                    choice.MaximizeBox = false;
                    choice.MinimizeBox = false;
                    choice.ShowInTaskbar = false;
                    ApplyPluginIcon(choice);
                    choice.Controls.Add(new Label {
                        Left = 18, Top = 15, Width = 474, Height = 80,
                        Text = confirmationOnlyApply ? L("The generated geometry is already present. Confirming it " +
                            "discards Undo. The resulting model is not saved automatically.",
                            "La geometria generata è già presente. Confermando si rinuncia ad Annulla. " +
                            "Il modello risultante non viene salvato automaticamente.") :
                            L("Are you sure you want to apply the generated geometry? After Yes, " +
                            "Undo mesh will not be available. The applied SAP2000 model is not saved automatically.",
                            "Confermi l'applicazione della geometria generata? Dopo Sì, " +
                            "Annulla mesh non sarà più disponibile. Il modello SAP2000 applicato non viene salvato automaticamente.")
                    });
                    string file = applyBackupInfo == null ? null : applyBackupInfo();
                    bool available = !string.IsNullOrEmpty(file) && File.Exists(file);
                    var backup = new Label {
                        Left = 18, Top = 99, Width = 474, Height = 34,
                        Text = available
                            ? L("Backup of the last saved model: ", "Backup dell'ultimo modello salvato: ") +
                                Path.GetFileName(file)
                            : L("No numbered backup was created for this mesh.",
                                "Nessun backup numerato è stato creato per questa mesh.")
                    };
                    choice.Controls.Add(backup);
                    if (available)
                    {
                        var pathTip = new ToolTip();
                        pathTip.SetToolTip(backup, file);
                        choice.Disposed += delegate { pathTip.Dispose(); };
                    }
                    var suppress = new CheckBox {
                        Left = 18, Top = 140, Width = 474, Height = 25,
                        Text = L("Do not show this Apply confirmation again",
                            "Non mostrare più questa conferma di Applica")
                    };
                    choice.Controls.Add(suppress);
                    var no = new Button { Left = 238, Top = 169, Width = 115, Height = 29,
                        Text = L("No", "No"), DialogResult = DialogResult.No };
                    var yes = new Button { Left = 365, Top = 169, Width = 127, Height = 29,
                        Text = L("Yes, apply", "Sì, applica"), DialogResult = DialogResult.Yes };
                    StyleButton(no, false);
                    StyleButton(yes, true);
                    choice.Controls.Add(no); choice.Controls.Add(yes);
                    choice.AcceptButton = no;
                    choice.CancelButton = no;
                    if (ShowHostDialog(choice) != DialogResult.Yes) return false;
                }
                RememberOptions();
                return true;
            }
            private bool CurrentModelMatchesPreview()
            {
                try { return completedModelGuard == null || completedModelGuard(); }
                catch (COMException) { return false; }
            }
            public void DiscardCompletedForModelChange()
            {
                if (working || qualityReport == null) return;
                ReleaseCompletedUndo();
                ClearCompletedPreview();
                status.Text = L("SAP2000 model changed. The previous preview/Undo is no longer available; select an area in the current model.",
                    "Modello SAP2000 cambiato. Anteprima e Annulla precedenti non sono più disponibili: selezionare un'area nel modello corrente.");
            }
            public void RearmAppliedForNewSelection()
            {
                if (working || !Applied) return;
                ClearCompletedPreview();
                status.Text = L("Applied mesh retained. New selection detected; ready to generate another mesh.",
                    "Mesh applicata mantenuta. Nuova selezione rilevata: pronto per generare un'altra mesh.");
            }
            public void FinishRearm()
            {
                if (!RetryRequested || working) return;
                RetryRequested = false;
                UpdateOptionState();
                generate.Enabled = hasSelectedAreas && !requiresSavedModel;
            }
            private void ClearCompletedPreview()
            {
                qualityReport = null;
                undoAction = null;
                applyAction = appliedUndoAction = null;
                completedModelGuard = null;
                onApplied = null;
                UndoFailure = null;
                preservationWarning = null;
                designTransferLossWarning = false;
                measurementToolTip.SetToolTip(undo, "");
                measurementToolTip.SetToolTip(status, "");
                ResetReady();
            }
            protected override bool ProcessCmdKey(ref Message message, Keys keyData)
            {
                if (keyData == Keys.F1) { OpenHelp(this); return true; }
                return base.ProcessCmdKey(ref message, keyData);
            }
            public void SetMeshingStrategy(bool regular, bool refined, bool symmetry, bool bands)
            {
                preferRegular.Checked = false;
                stressRefinement.Checked = false;
                if (regular) preferRegular.Checked = true;
                else if (refined) stressRefinement.Checked = true;
                symmetric.Checked = symmetry;
                structuredSectorBands.Checked = bands;
                UpdateOptionState();
            }
            public void SetInsertEdgeNodes(bool enabled) { strictMaximumSize.Checked = enabled; }
            public void SetSelectionCounts(int areas, int quads, int triangles, int polygons, int frames, int links, int joints)
            {
                selectedAreas = areas; selectedQuads = quads; selectedTriangles = triangles;
                selectedPolygons = polygons; selectedFrameCount = frames; selectedLinks = links; selectedJoints = joints;
                ApplySelectionSummary();
                UpdateGmshPreview();
            }
            public bool GmshOptimizeEnabled { get { return gmshPreviewOptions.OptimizeValue; } }
            public void UpdateOversizedSelection(int areas, int frames, int links, int joints, int limit)
            {
                if (!CanRefreshSelection) return;
                hasSelectedAreas = false;
                SetSelectionCounts(areas, 0, 0, 0, frames, links, joints);
                UpdateOptionState();
                generate.Enabled = false;
                status.Text = L(
                    "Select All contains " + areas + " areas. Select up to " + limit +
                    " source areas to mesh; geometry inspection is paused for this selection.",
                    "Seleziona tutto contiene " + areas + " aree. Selezionare fino a " + limit +
                    " aree sorgente da meshare; l'analisi geometrica e sospesa per questa selezione.");
            }
            public void UpdateLiveSelection(int areas, int quads, int triangles, int polygons,
                int frames, int links, int joints, bool containsFrameConstraints,
                string[] pointGroups, string[] openingGroups, SymmetryAxis[] axes)
            {
                if (!CanRefreshSelection) return;
                hasSelectedAreas = areas > 0;
                hasFrames = containsFrameConstraints;
                string oldHoleGroup = holeGroup.SelectedItem as string;
                string oldOpeningGroup = openingGroup.SelectedItem as string;
                bool holeLost = oldHoleGroup != null && !pointGroups.Contains(oldHoleGroup);
                bool openingLost = oldOpeningGroup != null && !openingGroups.Contains(oldOpeningGroup);
                UpdateGroups(holeGroup, pointGroups);
                UpdateGroups(openingGroup, openingGroups);
                if (holeLost) { generateCircularHoles.Checked = false; gmshOGrid.Checked = false; }
                if (openingLost) generateHoles.Checked = false;

                SymmetryAxis previous = symmetryAxis.SelectedItem as SymmetryAxis;
                symmetryAxes = axes;
                hasSymmetryAxes = axes.Length > 0;
                doubleSymmetryAxis = axes.FirstOrDefault(axis => axis.SecondaryDirection != null);
                SymmetryAxis matching = previous == null ? null : axes.FirstOrDefault(axis =>
                    Distance(axis.Center, previous.Center) <= 1e-6 &&
                    Distance(axis.Direction, previous.Direction) <= 1e-6 &&
                    (axis.SecondaryDirection == null && previous.SecondaryDirection == null ||
                     axis.SecondaryDirection != null && previous.SecondaryDirection != null &&
                     Distance(axis.SecondaryDirection, previous.SecondaryDirection) <= 1e-6));
                symmetryAxis.Items.Clear();
                symmetryAxis.Items.AddRange(axes);
                if (matching != null) symmetryAxis.SelectedItem = matching;
                else
                {
                    symmetric.Checked = false;
                    if (axes.Length > 0) symmetryAxis.SelectedIndex = 0;
                }
                SetSelectionCounts(areas, quads, triangles, polygons, frames, links, joints);
                UpdateOptionState();
                if (RetryRequested) generate.Enabled = false;
                if (Applied && !hasSelectedAreas && !requiresSavedModel && generationCompletionStatus != null)
                    status.Text = generationCompletionStatus;
                else
                {
                    if (hasSelectedAreas) generationCompletionStatus = null;
                    status.Text = requiresSavedModel ? SaveModelFirstText() :
                        hasSelectedAreas ? L("Ready. Selection updated.", "Pronto. Selezione aggiornata.") :
                        L("Select at least one area in " + HostProductName() + "; this window updates automatically.",
                          "Selezionare almeno un'area in " + HostProductName() + ": questa finestra si aggiorna automaticamente.");
                }
            }
            private static void UpdateGroups(ComboBox box, string[] names)
            {
                string previous = box.SelectedItem as string;
                if (box.Items.Cast<string>().SequenceEqual(names, StringComparer.OrdinalIgnoreCase)) return;
                box.Items.Clear();
                box.Items.AddRange(names);
                if (previous != null && box.Items.Contains(previous)) box.SelectedItem = previous;
                else if (names.Length > 0) box.SelectedIndex = 0;
            }
            public void SelectionUnavailable()
            {
                StartRequested = false;
                status.Text = L("Select at least one area in " + HostProductName() + "; this window updates automatically.",
                    "Selezionare almeno un'area in " + HostProductName() + ": questa finestra si aggiorna automaticamente.");
            }
            public void SelectionRefreshFailed(string diagnostic)
            {
                StartRequested = false;
                generate.Enabled = false;
                status.Text = L("Unable to read the current selection; retrying...",
                    "Impossibile leggere la selezione corrente; nuovo tentativo...");
                measurementToolTip.SetToolTip(status, diagnostic);
            }
            private DialogResult ShowHostMessage(string text, string title, MessageBoxButtons buttons,
                MessageBoxIcon icon, MessageBoxDefaultButton defaultButton = MessageBoxDefaultButton.Button1)
            {
                return ShowHostModal(Handle, viewportWindow,
                    () => MessageBox.Show(this, text, title, buttons, icon, defaultButton));
            }
            private DialogResult ShowHostDialog(Form form)
            {
                return ShowHostModal(Handle, viewportWindow, () => form.ShowDialog(this));
            }
            public void ReportModelLockViolation(string diagnostic)
            { ReportModelLockViolation(diagnostic, null); }
            public void ReportModelLockViolation(string diagnostic, Action acknowledged)
            {
                if (modelLockWarningShown) return;
                bool wasGenerateEnabled = generate.Enabled, wasUndoEnabled = undo.Enabled;
                bool wasDetailsEnabled = details.Enabled;
                string previousStatus = status.Text;
                string previousTooltip = measurementToolTip.GetToolTip(status);
                modelLockWarningShown = true;
                try
                {
                    generate.Enabled = undo.Enabled = details.Enabled = false;
                    string message = L("The " + HostProductName() + " model cannot be unlocked while MESHing is open. " +
                        "The model has been relocked; the mesh preview is unchanged.",
                        "Non è possibile sbloccare il modello " + HostProductName() + " mentre MESHing è aperto. " +
                        "Il modello è stato nuovamente bloccato; l'anteprima della mesh non è cambiata.");
                    measurementToolTip.SetToolTip(status, diagnostic);
                    status.Text = message;
                    ShowHostMessage(message, WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    if (acknowledged != null) acknowledged();
                }
                finally
                {
                    modelLockWarningShown = false;
                    if (!IsDisposed)
                    {
                        generate.Enabled = wasGenerateEnabled;
                        undo.Enabled = wasUndoEnabled;
                        details.Enabled = wasDetailsEnabled;
                        status.Text = previousStatus;
                        measurementToolTip.SetToolTip(status, previousTooltip);
                    }
                }
            }
            public void SetGeneratedInventory(MeshInventory inventory)
            {
                generatedInventory = inventory;
                ApplySelectionSummary();
            }
            public void UndoForAutomation() { undo.PerformClick(); }
            public void ApplyForAutomation() { if (qualityReport != null && !Applied) generate.PerformClick(); }
            internal IntPtr HostWindow { get { return viewportWindow; } }
            public void AllowViewportInteraction(IntPtr window)
            {
                // SAP2000 handles navigation on its own thread after the plugin
                // callback has returned. Keep the dialog in sight while the user
                // zooms or pans in the uncovered portion of the viewport.
                if (window == IntPtr.Zero) return;
                viewportWindow = window;
                if (!IsWindowEnabled(window))
                {
                    viewportWasDisabled = true;
                    EnableWindow(window, true);
                    viewportEnabledByPlugin = IsWindowEnabled(window);
                }
            }
            private void PauseViewportInteraction()
            {
                if (viewportEnabledByPlugin && viewportWindow != IntPtr.Zero)
                {
                    EnableWindow(viewportWindow, false);
                    viewportEnabledByPlugin = false;
                }
            }
            private void LockViewportForCommit()
            {
                if (viewportWindow == IntPtr.Zero || !IsWindowEnabled(viewportWindow)) return;
                EnableWindow(viewportWindow, false);
                commitWindowLocked = !IsWindowEnabled(viewportWindow);
                if (commitWindowLocked) viewportEnabledByPlugin = false;
            }
            private void UnlockViewportAfterCommit()
            {
                if (!commitWindowLocked) return;
                if (viewportWindow != IntPtr.Zero && !IsWindowEnabled(viewportWindow))
                    EnableWindow(viewportWindow, true);
                commitWindowLocked = false;
                if (viewportWasDisabled && viewportWindow != IntPtr.Zero)
                    viewportEnabledByPlugin = IsWindowEnabled(viewportWindow);
            }
            private DialogResult ChoosePreviewClose()
            {
                // The automated SAP fixtures select one of the two real actions;
                // interactive users must always make the choice in the dialog.
                if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SIZE")))
                {
                    if (Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_CLOSE_UNAPPLIED") == "1")
                        return DialogResult.No;
                    if (Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_CLOSE_APPLY") == "1")
                        return DialogResult.Yes;
                }
                using (var choice = new Form())
                {
                    choice.Text = L("Close mesh preview", "Chiudere l'anteprima mesh");
                    choice.ClientSize = new Size(440, 135);
                    choice.Font = Font;
                    choice.BackColor = Color.White;
                    choice.FormBorderStyle = FormBorderStyle.FixedDialog;
                    choice.StartPosition = FormStartPosition.CenterParent;
                    choice.ControlBox = false;
                    choice.ShowInTaskbar = false;
                    ApplyPluginIcon(choice);
                        choice.Controls.Add(new Label { Left = 18, Top = 17, Width = 404, Height = 53,
                        Text = L("Close the plugin: undo the preview or continue to Apply confirmation?",
                            "Chiudere il plugin: annullare l'anteprima o continuare alla conferma di Applica?") });
                    var restore = new Button { Left = 48, Top = 85, Width = 160, Height = 32,
                        Text = L("Undo mesh", "Annulla mesh"), DialogResult = DialogResult.No };
                    var apply = new Button { Left = 232, Top = 85, Width = 160, Height = 32,
                        Text = L("Continue to Apply", "Continua ad Applica"), DialogResult = DialogResult.Yes };
                    StyleButton(restore, false);
                    StyleButton(apply, true);
                    choice.Controls.Add(restore);
                    choice.Controls.Add(apply);
                    choice.FormClosing += delegate(object sender, FormClosingEventArgs e)
                    {
                        if (choice.DialogResult != DialogResult.No && choice.DialogResult != DialogResult.Yes)
                            e.Cancel = true;
                    };
                    return ShowHostDialog(choice);
                }
            }
            internal Form ShowCloseProgress(bool apply)
            {
                var progressWindow = new Form {
                    Text = closingPreview ? L("Closing mesh preview", "Chiusura anteprima mesh") :
                        apply ? confirmationOnlyApply ? L("Confirming mesh", "Conferma mesh") :
                            L("Applying mesh", "Applicazione mesh") : L("Undoing mesh", "Annullamento mesh"),
                    ClientSize = new Size(440, 160), Font = Font, BackColor = Color.White,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterParent,
                    ControlBox = false, ShowInTaskbar = false
                };
                ApplyPluginIcon(progressWindow);
                string action = apply ? confirmationOnlyApply ?
                        L("Confirming the generated mesh.", "Conferma della mesh generata.") :
                        L("Applying the mesh.", "Applicazione della mesh in corso.") :
                        L("Restoring the original model.", "Ripristino del modello originale in corso.");
                progressWindow.Controls.Add(new Label { Left = 18, Top = 17, Width = 404, Height = 100,
                    Text = action + "\n" + L(
                        HostProductName() + " may appear frozen. Do not close or stop the program; please wait until the operation finishes.",
                        HostProductName() + " potrebbe sembrare bloccato. Non chiudere né arrestare il programma; attendere il completamento dell'operazione.") });
                progressWindow.Controls.Add(new ProgressBar { Left = 18, Top = 126, Width = 404, Height = 18,
                    Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 26 });
                progressWindow.FormClosing += delegate(object sender, FormClosingEventArgs e)
                {
                    if (!Equals(progressWindow.Tag, true)) e.Cancel = true;
                };
                WindowRect sapBounds;
                if (viewportWindow != IntPtr.Zero && IsWindow(viewportWindow) &&
                    GetWindowRect(viewportWindow, out sapBounds) &&
                    sapBounds.Right > sapBounds.Left && sapBounds.Bottom > sapBounds.Top)
                {
                    progressWindow.StartPosition = FormStartPosition.Manual;
                    progressWindow.Location = new Point(
                        sapBounds.Left + (sapBounds.Right - sapBounds.Left - progressWindow.Width) / 2,
                        sapBounds.Top + (sapBounds.Bottom - sapBounds.Top - progressWindow.Height) / 2);
                }
                bool ownerWasEnabled = Enabled;
                Enabled = false;
                progressWindow.Disposed += delegate
                {
                    if (ownerWasEnabled && !IsDisposed) Enabled = true;
                };
                try
                {
                    progressWindow.Show(this);
                    Application.DoEvents();
                    return progressWindow;
                }
                catch
                {
                    progressWindow.Dispose();
                    throw;
                }
            }
            public MeshOptionsDialog(string units, bool hasFrames, string[] pointGroups, string[] openingGroups,
                SymmetryAxis[] symmetryAxes, bool hasSelectedAreas = true)
            {
                useItalian = InitialItalian();
                this.units = units;
                this.hasFrames = hasFrames;
                this.hasSelectedAreas = hasSelectedAreas;
                this.symmetryAxes = symmetryAxes;
                hasSymmetryAxes = symmetryAxes.Length > 0;
                doubleSymmetryAxis = symmetryAxes.FirstOrDefault(axis => axis.SecondaryDirection != null);
                Text = WindowTitle; FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(570, 762); MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
                HelpButton = true;
                HelpButtonClicked += delegate(object sender, System.ComponentModel.CancelEventArgs e)
                { e.Cancel = true; OpenHelp(this); };
                ApplyPluginIcon(this);
                Font = new Font("Segoe UI", 9); BackColor = Color.White; ForeColor = UiBody; AutoScaleMode = AutoScaleMode.Dpi;
                updateNoticeBoldFont = new Font(Font, FontStyle.Bold);
                Panel header = CreateDialogHeader("MESHing", "Advanced 2D mesh generation", ClientSize.Width);
                headerTitle = (Label)header.Controls[0]; headerSubtitle = (Label)header.Controls[1]; Controls.Add(header);
                headerTitle.Width = 390; headerSubtitle.Width = 410;
                verifiedExamples.Left = 414; verifiedExamples.Top = 10; verifiedExamples.Width = 136; verifiedExamples.Height = 27;
                verifiedExamples.Tag = "atomic-report"; verifiedExamples.TextAlign = ContentAlignment.MiddleRight;
                verifiedExamples.BackColor = UiNavy; verifiedExamples.LinkColor = UiBorder;
                verifiedExamples.ActiveLinkColor = Color.White; verifiedExamples.VisitedLinkColor = UiBorder;
                verifiedExamples.Cursor = Cursors.Hand;
                verifiedExamples.LinkClicked += delegate
                {
                    string report = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                        "docs", "SAP2000-Quad-Mesher-Atomic-Examples.pdf");
                    if (!File.Exists(report))
                    {
                        ShowHostMessage(L("The verified examples PDF is missing from the installation.",
                            "Il PDF degli esempi verificati non e presente nell'installazione."),
                            WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    Process.Start(new ProcessStartInfo(report) { UseShellExecute = true });
                };
                header.Controls.Add(verifiedExamples);
                language.Left = 442; language.Top = 43; language.Width = 108; language.Height = 24;
                language.DropDownStyle = ComboBoxStyle.DropDownList; language.FlatStyle = FlatStyle.Flat;
                language.Items.AddRange(new object[] { "English", "Italiano" }); language.SelectedIndex = useItalian ? 1 : 0;
                Controls.Add(language); language.BringToFront();
                profiles.TabPages.Add("Reinforced Concrete"); profiles.TabPages.Add("Steel Structures"); profiles.TabPages.Add("General Purpose");
                profiles.SelectedIndex = 2;
                edgeTolerance.Minimum = 0; edgeTolerance.Maximum = 50; edgeTolerance.Value = 20;
                size.Text = DefaultLength(0.1, units);
                strictMaximumSize.CheckedChanged += delegate
                {
                    if (strictMaximumSize.Checked)
                    {
                        rememberedTolerance = (int)edgeTolerance.Value;
                        edgeTolerance.Value = 0;
                    }
                    else edgeTolerance.Value = rememberedTolerance;
                };
                symmetric.Left = 20; symmetric.Top = 204; symmetric.Width = 210; symmetric.Text = "Enforce exact symmetry";
                symmetric.FlatStyle = FlatStyle.Standard;
                symmetric.Enabled = true; Controls.Add(symmetric);
                symmetryAxis.Left = 235; symmetryAxis.Top = 200; symmetryAxis.Width = 315; symmetryAxis.DropDownStyle = ComboBoxStyle.DropDownList; symmetryAxis.FlatStyle = FlatStyle.Flat;
                symmetryAxis.Items.AddRange(symmetryAxes); if (hasSymmetryAxes) symmetryAxis.SelectedIndex = 0;
                symmetryAxis.Enabled = false; Controls.Add(symmetryAxis);
                symmetric.CheckedChanged += delegate { symmetryAxis.Enabled = symmetric.Checked && hasSymmetryAxes && !working; };
                generateHoles.Left = 20; generateHoles.Top = 236; generateHoles.Width = 530; generateHoles.FlatStyle = FlatStyle.Standard;
                generateHoles.Text = "Generate holes enclosed by selected frames or shell edges";
                generateHoles.Enabled = true; Controls.Add(generateHoles);
                openingGroupLabel.Left = 40; openingGroupLabel.Top = 270; openingGroupLabel.Width = 190; openingGroupLabel.Text = "Opening-boundary group:"; Controls.Add(openingGroupLabel);
                openingGroup.Left = 235; openingGroup.Top = 266; openingGroup.Width = 315; openingGroup.DropDownStyle = ComboBoxStyle.DropDownList; openingGroup.FlatStyle = FlatStyle.Flat;
                openingGroup.Items.AddRange(openingGroups); if (openingGroups.Length > 0) openingGroup.SelectedIndex = 0; Controls.Add(openingGroup);
                generateHoles.CheckedChanged += delegate { UpdateOptionState(); };
                generateCircularHoles.Left = 20; generateCircularHoles.Top = 300; generateCircularHoles.Width = 530; generateCircularHoles.FlatStyle = FlatStyle.Standard;
                generateCircularHoles.Text = "Generate circular O-grid holes";
                generateCircularHoles.Enabled = true; Controls.Add(generateCircularHoles);
                holeGroupLabel.Left = 40; holeGroupLabel.Top = 334; holeGroupLabel.Width = 180; holeGroupLabel.Text = "Hole-center group:"; Controls.Add(holeGroupLabel);
                holeGroup.Left = 235; holeGroup.Top = 330; holeGroup.Width = 315; holeGroup.DropDownStyle = ComboBoxStyle.DropDownList; holeGroup.FlatStyle = FlatStyle.Flat;
                holeGroup.Items.AddRange(pointGroups); if (pointGroups.Length > 0) holeGroup.SelectedIndex = 0; Controls.Add(holeGroup);
                holeDiameterLabel.Left = 40; holeDiameterLabel.Top = 366; holeDiameterLabel.Width = 250; holeDiameterLabel.Text = "Hole diameter (" + units + "):"; Controls.Add(holeDiameterLabel);
                holeDiameter.Left = 440; holeDiameter.Top = 362; holeDiameter.Width = 110; holeDiameter.Text = DefaultLength(0.05, units); holeDiameter.BorderStyle = BorderStyle.FixedSingle; Controls.Add(holeDiameter);
                holeGroup.Enabled = holeDiameter.Enabled = false;
                generateCircularHoles.CheckedChanged += delegate {
                    if (!generateCircularHoles.Checked) gmshOGrid.Checked = false;
                    UpdateOptionState();
                    UpdateGmshPreview();
                };
                structuredSectorBands.Checked = true;
                preferRegular.CheckedChanged += delegate
                {
                    if (preferRegular.Checked) stressRefinement.Checked = false;
                    UpdateOptionState();
                };
                stressRefinement.CheckedChanged += delegate
                {
                    if (stressRefinement.Checked) preferRegular.Checked = false;
                    UpdateOptionState();
                };
                profiles.SelectedIndexChanged += delegate { ApplyProfileDefaults(profiles.SelectedIndex); };
                // FAST is the interactive default; atomic fixtures may still
                // exercise the Standard fallback without exposing a UI switch.
                fastMode.Checked = true;
                useMaximumCores.FlatStyle = FlatStyle.Flat; useMaximumCores.Checked = true;
                useMaximumCores.CheckedChanged += delegate { workerLimit.Enabled = !working && this.hasSelectedAreas && !useMaximumCores.Checked; };
                workerLimit.Minimum = 1; workerLimit.Maximum = Math.Max(1, Environment.ProcessorCount);
                workerLimit.Value = Math.Min(2, workerLimit.Maximum); workerLimit.Enabled = false;
                createBackup.Left = 20; createBackup.Top = 547; createBackup.Width = 530;
                createBackup.Checked = true; createBackup.FlatStyle = FlatStyle.Standard; Controls.Add(createBackup);

                status.Left = 20; status.Top = 606; status.Width = 530; status.Height = 54; status.Text = "Ready."; status.ForeColor = UiBody; Controls.Add(status);
                elapsedTimer.Interval = 1000;
                elapsedTimer.Tick += delegate { if (meshingActive) UpdateMeshingStatus(); };
                generate.Text = "Generate"; generate.Left = 20; generate.Top = 664; generate.Width = 160; StyleButton(generate, true);
                details.Text = "Mesh quality"; details.Left = 195; details.Top = 664; details.Width = 160; details.Enabled = false; StyleButton(details, false);
                undo.Text = "Undo mesh"; undo.Left = 370; undo.Top = 664; undo.Width = 180; undo.Enabled = false; StyleButton(undo, false);
                generate.Click += delegate
                {
                    // Do not begin a second transaction while the previous
                    // snapshot/preview frame is still being released.
                    if (RetryRequested || requiresSavedModel) return;
                    if (qualityReport != null && !Applied)
                    {
                        if (!CurrentModelMatchesPreview())
                        {
                            DiscardCompletedForModelChange();
                            return;
                        }
                        if (Applied) return;
                        LockViewportForCommit();
                        bool confirmed = false;
                        try { confirmed = committingGeneration || ConfirmApply(); }
                        finally { if (!confirmed) UnlockViewportAfterCommit(); }
                        if (!confirmed) return;
                        if (DetectRegionalChange()) { AbandonForRegionalChange(); return; }
                        Form operation = null;
                        bool showWarnings = false;
                        try
                        {
                        operation = ShowCloseProgress(true);
                        bool readyToCommit = false;
                        try { readyToCommit = ReleasePreviewModelLock("Apply"); }
                        finally { if (!readyToCommit) UnlockViewportAfterCommit(); }
                        if (!readyToCommit) return;
                        if (DetectRegionalChange()) { AbandonForRegionalChange(); return; }
                        working = true;
                        ControlBox = false;
                        generate.Enabled = details.Enabled = undo.Enabled = false;
                        gmshResetDefault.Enabled = false;
                        status.Text = confirmationOnlyApply ?
                            L("Confirming the generated mesh...", "Conferma della mesh generata...") :
                            L("Removing temporary original areas and finalizing the mesh...",
                                "Rimozione delle aree originali temporanee e conclusione della mesh...");
                        Application.DoEvents();
                        bool commitSucceeded = false;
                        try
                        {
                            ThrowIfRegionalSettingsChanged();
                            applyAction();
                            ThrowIfRegionalSettingsChanged();
                            commitSucceeded = true;
                        }
                        catch (Exception exception)
                        {
                            if (DetectRegionalChange())
                            {
                                if (operation != null)
                                { operation.Tag = true; operation.Close(); operation.Dispose(); operation = null; }
                                AbandonForRegionalChange();
                                return;
                            }
                            try { appliedUndoAction(); }
                            catch (Exception rollback)
                            {
                                exception = new InvalidOperationException(exception.Message +
                                    " CSV snapshot restore failed: " + rollback.Message, exception);
                            }
                            if (DetectRegionalChange()) { AbandonForRegionalChange(); return; }
                            UndoFailure = exception;
                            status.Text = LocalizeErrorMessage(exception.Message);
                            if (operation != null)
                            { operation.Tag = true; operation.Close(); operation.Dispose(); operation = null; }
                            ShowHostMessage(status.Text, WindowTitle,
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }
                        finally
                        {
                            if (!commitSucceeded) UnlockViewportAfterCommit();
                            working = false;
                            ControlBox = true;
                            gmshResetDefault.Enabled = true;
                        }
                        try
                        {
                            Applied = true;
                            if (!committingGeneration) undoAction = null;
                            applyAction = appliedUndoAction = null;
                            try { if (onApplied != null) onApplied(); }
                            catch (Exception) when (DetectRegionalChange())
                            { AbandonForRegionalChange(); return; }
                            regionalSignature = null;
                            onApplied = null;
                            applyBackupInfo = null;
                            snapshotPath = null;
                            generate.Enabled = false;
                            generate.Text = L("Generate", "Genera");
                            undo.Enabled = committingGeneration;
                            details.Enabled = true;
                            status.Text = L("Mesh generated. Select an area for another generation or use Undo for this mesh.",
                                "Mesh generata. Selezionare un'area per una nuova generazione oppure usare Annulla per questa mesh.");
                            if (preservationWarning != null)
                                status.Text += L(" Design overrides require manual review.",
                                    " Le sovrascritture di progetto richiedono verifica manuale.");
                            measurementToolTip.SetToolTip(undo, "");
                            measurementToolTip.SetToolTip(status, preservationWarning ?? "");
                            showWarnings = true;
                            return;
                        }
                        finally
                        {
                            UnlockViewportAfterCommit();
                        }
                        }
                        finally
                        {
                            if (operation != null)
                            { operation.Tag = true; operation.Close(); operation.Dispose(); }
                            UnlockViewportAfterCommit();
                            if (showWarnings) ShowTransferWarnings();
                        }
                    }
                    if (!this.hasSelectedAreas) return;
                    if (generationFilterCheck != null)
                    {
                        try { if (!generationFilterCheck()) return; }
                        catch (Exception error)
                        {
                            LogPluginException(error);
                            SelectionRefreshFailed(error.Message);
                            return;
                        }
                    }
                    double value;
                    try { gmshPreviewOptions.CheckValues(); value = gmshPreviewOptions.ReadMaxSize(); }
                    catch (InvalidOperationException error)
                    {
                        ShowHostMessage(error.Message, WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    MaxSize = value;
                    if (PreferRegularMesh && StressRefinement)
                    { ShowHostMessage(L("Regular meshing and local refinement are mutually exclusive.", "La mesh regolare e il raffinamento locale si escludono a vicenda."), WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    if (GenerateHoles && openingGroup.SelectedItem == null)
                    { ShowHostMessage(L("Select an opening-boundary group.", "Selezionare un gruppo per il contorno delle aperture."), WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                    if (GenerateCircularHoles)
                    {
                        double diameter;
                        if (holeGroup.SelectedItem == null)
                        { ShowHostMessage(L("Select a hole-center group.", "Selezionare un gruppo di centri dei fori."), WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                        if (!TryParseLength(holeDiameter.Text, units, out diameter))
                        { ShowHostMessage(L("Invalid hole diameter.", "Diametro del foro non valido."), WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                        HoleDiameter = diameter;
                        holeDiameter.Text = diameter.ToString("G9", CultureInfo.InvariantCulture);
                    }
                    if (!ConfirmNextGeneration() || !ConfirmGenerateWithoutBackup()) return;
                    try { BeginRegionalTransaction(); }
                    catch (InvalidOperationException error)
                    {
                        ShowHostMessage(error.Message, WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (DetectRegionalChange()) { AbandonForRegionalChange(); return; }
                    bool replacingCompleted = qualityReport != null;
                    if (replacingCompleted)
                    {
                        ReleaseCompletedUndo();
                        ClearCompletedPreview();
                    }
                    RememberOptions();
                    // Keep the rearm request until the completed generation's
                    // preview loop releases its resources. Otherwise StartRequested
                    // cannot be consumed by the outer generation loop.
                    if (!replacingCompleted) RetryRequested = false;
                    operationClock.Restart();
                    StartRequested = true;
                };
                details.Click += delegate
                {
                    if (qualityReport != null)
                        using (var qualityDialog = new QualityReportDialog(qualityReport)) ShowHostDialog(qualityDialog);
                };
                undo.Click += delegate
                {
                    if (working)
                    {
                        CancelRequested = true;
                        if (cancellationController != null) cancellationController.Request();
                        undo.Enabled = false;
                        return;
                    }
                    if (undoAction == null) return;
                    if (DetectRegionalChange()) { AbandonForRegionalChange(); return; }
                    if (!CurrentModelMatchesPreview())
                    {
                        DiscardCompletedForModelChange();
                        return;
                    }
                    LockViewportForCommit();
                    Form operation = null;
                    try
                    {
                    operation = ShowCloseProgress(false);
                    bool readyToUndo = false;
                    try { readyToUndo = ReleasePreviewModelLock("Undo"); }
                    finally { if (!readyToUndo) UnlockViewportAfterCommit(); }
                    if (!readyToUndo) return;
                    if (DetectRegionalChange()) { AbandonForRegionalChange(); return; }
                    working = true;
                    ControlBox = false;
                    undo.Enabled = false;
                    details.Enabled = false;
                    SetIndeterminate(L("Restoring the original objects and selection...", "Ripristino degli oggetti originali e della selezione..."));
                    try
                    {
                        ThrowIfRegionalSettingsChanged();
                        undoAction();
                        ThrowIfRegionalSettingsChanged();
                        undoAction = null;
                        regionalSignature = null;
                        qualityReport = null;
                        ResetReady();
                        if (viewportWasDisabled) AllowViewportInteraction(viewportWindow);
                    }
                    catch (Exception exception)
                    {
                        if (DetectRegionalChange()) { AbandonForRegionalChange(); return; }
                        UndoFailure = exception;
                        status.Text = L("Undo failed.", "Annullamento non riuscito.");
                    }
                    finally
                    {
                        UnlockViewportAfterCommit();
                        working = false;
                        ControlBox = true;
                    }
                    }
                    finally
                    {
                        if (operation != null)
                        {
                            operation.Tag = true;
                            operation.Close();
                            operation.Dispose();
                        }
                        UnlockViewportAfterCommit();
                    }
                };
                FormClosing += delegate(object sender, FormClosingEventArgs e)
                {
                    if (DetectRegionalChange())
                    {
                        if (!regionalWarningShown)
                        { e.Cancel = true; AbandonForRegionalChange(); }
                        return;
                    }
                    if (working || closingPreview) { e.Cancel = true; return; }
                    if (qualityReport == null || undoAction == null || UndoFailure != null) return;
                    if (Applied)
                    {
                        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SIZE"))) return;
                        e.Cancel = ShowHostMessage(L(
                            "Closing MESHing makes Undo unavailable. The meshed model has not been saved: " +
                            "the SDB/BDB on disk contains the state before the latest Generate. " +
                            "If you close the model without saving, that earlier state will be restored. Exit MESHing?",
                            "Chiudendo MESHing non sarà più possibile usare Annulla. Il modello con la mesh " +
                            "non è stato salvato: l'SDB/BDB su disco contiene lo stato precedente all'ultimo Genera. " +
                            "Se si chiude il modello senza salvare, verrà ripristinato quello stato. Uscire da MESHing?"),
                            WindowTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
                            MessageBoxDefaultButton.Button2) != DialogResult.OK;
                        return;
                    }
                    e.Cancel = true;
                    closingPreview = true;
                    try
                    {
                        bool apply = ChoosePreviewClose() == DialogResult.Yes;
                        if (apply) generate.PerformClick();
                        else undo.PerformClick();
                        if (UndoFailure != null) return;
                        if (apply && Applied) e.Cancel = false;
                        if (!apply && qualityReport == null && RetryRequested)
                        { PreviewRestoredOnClose = true; e.Cancel = false; }
                    }
                    finally { closingPreview = false; }
                };
                FormClosed += delegate { ReleaseCompletedUndo(); RememberOptions(); };
                Controls.Add(generate); Controls.Add(details); Controls.Add(undo);
                AddDialogFooter(this, 704, ClientSize.Width);
                // Keep the update link centered between the GPL link and version label.
                updateNotice.Left = 180; updateNotice.Width = 205;
                updateNotice.Top = 715; updateNotice.Height = 36;
                updateNotice.TextAlign = ContentAlignment.MiddleCenter; updateNotice.Tag = "update-notice";
                updateNotice.LinkColor = UiBlue; updateNotice.ActiveLinkColor = UiNavy;
                updateNotice.VisitedLinkColor = UiBlue; updateNotice.Cursor = Cursors.Default;
                updateBlinkTimer.Interval = 650;
                updateBlinkTimer.Tick += delegate
                {
                    if (string.IsNullOrEmpty(availableDownloadUrl)) { updateBlinkTimer.Stop(); return; }
                    updateBlinkOn = !updateBlinkOn;
                    updateNotice.BackColor = updateBlinkOn ? Color.FromArgb(254, 243, 199) : Color.White;
                    updateNotice.LinkColor = updateBlinkOn ? Color.FromArgb(185, 28, 28) : UiBlue;
                    updateNotice.Invalidate();
                };
                updateNotice.LinkClicked += async delegate
                {
                    if (!string.IsNullOrEmpty(availableDownloadUrl))
                    {
                        ShowHostMessage(L("The ZIP download will open in your browser. Extract it, close SAP2000 and CSiBridge, then run install_MESHing.cmd from the extracted folder. Reopen the host application to use the new version.",
                            "Il download dello ZIP si aprira nel browser. Estrailo, chiudi SAP2000 e CSiBridge, quindi esegui install_MESHing.cmd dalla cartella estratta. Riapri l'applicazione per usare la nuova versione."),
                            WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        Process.Start(new ProcessStartInfo(availableDownloadUrl) { UseShellExecute = true });
                    }
                    else await CheckForUpdatesAsync();
                };
                Controls.Add(updateNotice);
                Shown += async delegate { await CheckForUpdatesAsync(); };
                language.SelectedIndexChanged += delegate
                {
                    useItalian = language.SelectedIndex == 1;
                    ApplyLanguage();
                    RememberOptions();
                };
                ConfigureSelectionSummary();
                ApplyLanguage();
                UpdateOptionState();
                ConfigureGmshLayout();
            }

            private void ConfigureGmshLayout()
            {
                verifiedExamples.Visible = false;
                profiles.Visible = false;
                Control header = headerTitle.Parent;
                header.Controls.Add(language);
                language.Anchor = AnchorStyles.Top | AnchorStyles.Right;
                language.SetBounds(header.ClientSize.Width - 128, 43, 108, 24);
                language.BringToFront();
                gmshPreviewTabs.SetBounds(20, 153, 530, 420);
                gmshPreviewTabs.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
                gmshPreviewTabs.TabPages.AddRange(new[] { gmshPreviewEmptyTab, gmshHolesTab, gmshSapTab });
                Controls.Add(gmshPreviewTabs);
                gmshPreviewOptions = new GmshPreviewPanel(units, (en, it) => L(en, it),
                    text => { double value; return TryParseLength(text, units, out value) ? (double?)value : null; },
                    meters => DefaultLength(meters, units));
                gmshPreviewOptions.SettingsChanged += delegate { UpdateGmshPreview(); };
                gmshPreviewEmptyTab.Controls.Add(gmshPreviewOptions.Content);

                gmshHolesTab.AutoScroll = true;
                // Keep every additional control inside the normal tab viewport.
                // The scrollbar remains available if display scaling reduces it.
                gmshHolesTab.Controls.Add(gmshPreviewOptions.RegularGrid);
                gmshPreviewOptions.RegularGrid.SetBounds(12, 6, 494, 23);
                gmshHolesTab.Controls.Add(gmshPreserveSelectedEdges);
                gmshPreserveSelectedEdges.FlatStyle = FlatStyle.Standard;
                gmshPreserveSelectedEdges.SetBounds(12, 31, 494, 23);
                gmshHolesTab.Controls.Add(symmetric); symmetric.SetBounds(12, 60, 494, 23);
                gmshHolesTab.Controls.Add(gmshSymmetryAxisLabel); gmshSymmetryAxisLabel.SetBounds(40, 87, 178, 23);
                gmshHolesTab.Controls.Add(symmetryAxis); symmetryAxis.SetBounds(222, 87, 284, 24);
                gmshHolesTab.Controls.Add(gmshSymmetryHint); gmshSymmetryHint.SetBounds(40, 114, 466, 25);
                gmshSymmetryHint.Font = new Font(Font, FontStyle.Italic);
                gmshHolesTab.Controls.Add(generateCircularHoles);
                generateCircularHoles.SetBounds(12, 149, 494, 23);
                gmshHolesTab.Controls.Add(holeGroupLabel); holeGroupLabel.SetBounds(40, 179, 178, 23);
                gmshHolesTab.Controls.Add(holeGroup); holeGroup.SetBounds(222, 179, 284, 23);
                gmshHolesTab.Controls.Add(holeDiameterLabel); holeDiameterLabel.SetBounds(40, 206, 245, 23);
                gmshHolesTab.Controls.Add(holeDiameter); holeDiameter.SetBounds(396, 206, 110, 23);
                gmshHolesTab.Controls.Add(gmshOGrid);
                gmshOGrid.FlatStyle = FlatStyle.Standard;
                gmshOGrid.SetBounds(40, 235, 466, 23);
                gmshHolesTab.Controls.Add(generateHoles); generateHoles.SetBounds(12, 272, 494, 40);
                gmshHolesTab.Controls.Add(openingGroupLabel); openingGroupLabel.SetBounds(40, 320, 178, 23);
                gmshHolesTab.Controls.Add(openingGroup); openingGroup.SetBounds(222, 320, 284, 23);
                foreach (Control control in gmshHolesTab.Controls)
                    if (control.Right > 482) control.Width = 482 - control.Left;

                gmshAdvancedTab.Controls.Add(gmshAdvancedLabel); gmshAdvancedLabel.SetBounds(12, 14, 494, 44);
                gmshExtra.Multiline = true; gmshExtra.ScrollBars = ScrollBars.Vertical;
                gmshExtra.Font = new Font("Consolas", 9); gmshExtra.SetBounds(12, 64, 494, 154);
                gmshAdvancedTab.Controls.Add(gmshExtra);
                gmshAdvancedTab.Controls.Add(gmshAdvancedPreviewLabel);
                gmshAdvancedPreviewLabel.SetBounds(12, 226, 490, 23);
                gmshEffectivePreview.Multiline = true; gmshEffectivePreview.ReadOnly = true;
                gmshEffectivePreview.ScrollBars = ScrollBars.Vertical;
                gmshEffectivePreview.Font = new Font("Consolas", 8);
                gmshEffectivePreview.SetBounds(12, 249, 494, 71);
                gmshAdvancedTab.Controls.Add(gmshEffectivePreview);
                gmshAdvancedTab.Controls.Add(gmshAdvancedHint);
                gmshAdvancedHint.SetBounds(12, 325, 494, 48);
                gmshSapTab.Controls.Add(createBackup); createBackup.SetBounds(12, 24, 16, 28);
                createBackup.CheckAlign = ContentAlignment.MiddleLeft;
                createBackup.Text = "";
                gmshSapTab.Controls.Add(createBackupDescription);
                createBackupDescription.SetBounds(28, 29, 478, 110);
                createBackupDescription.TextAlign = ContentAlignment.TopLeft;
                createBackupDescription.Cursor = Cursors.Hand;
                createBackupDescription.Click += delegate { if (createBackup.Enabled) createBackup.Checked = !createBackup.Checked; };

                gmshResetDefault.Click += delegate { ResetGmshOptions(); };
                StyleButton(gmshResetDefault, false);
                Button[] commands = { generate, details, undo, gmshResetDefault };
                for (int index = 0; index < commands.Length; index++)
                    commands[index].SetBounds(20 + index * 135, 664, 125, 32);
                Controls.Add(gmshResetDefault);

                gmshOGrid.CheckedChanged += delegate { UpdateGmshPreview(); };
                gmshPreserveSelectedEdges.CheckedChanged += delegate { UpdateGmshPreview(); };
                gmshExtra.TextChanged += delegate { UpdateGmshPreview(); };
                symmetric.CheckedChanged += delegate { UpdateGmshPreview(); };
                symmetryAxis.SelectedIndexChanged += delegate { UpdateGmshPreview(); };
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SIZE")))
                    RestoreOptions();
                // Additional options are session-only, regardless of settings
                // previously written by older releases.
                gmshPreviewOptions.RegularGrid.Checked = false;
                gmshPreserveSelectedEdges.Checked = false;
                symmetric.Checked = false;
                generateCircularHoles.Checked = false;
                gmshOGrid.Checked = false;
                generateHoles.Checked = false;
                gmshPreviewTabs.SelectedTab = gmshPreviewEmptyTab;
                gmshPreviewTabs.BringToFront();
                UpdateGmshLanguage();
                UpdateGmshPreview();
            }

            private void UpdateGmshPreview()
            {
                if (gmshPreviewOptions == null) return;
                try { gmshEffectivePreview.Text = "Mesh.MeshSizeMax = " + gmshPreviewOptions.MaxSize.Text +
                    Environment.NewLine + GmshSettingsJson; }
                catch (InvalidOperationException error) { gmshEffectivePreview.Text = error.Message; }
            }

            private void ResetGmshOptions()
            {
                if (working) return;
                gmshPreviewOptions.ResetExceptSizes();
                gmshExtra.Clear();
                gmshOGrid.Checked = false;
                gmshPreserveSelectedEdges.Checked = false;
                generateCircularHoles.Checked = false;
                generateHoles.Checked = false;
                if (holeGroup.Items.Count > 0) holeGroup.SelectedIndex = 0;
                if (openingGroup.Items.Count > 0) openingGroup.SelectedIndex = 0;
                holeDiameter.Text = DefaultLength(0.05, units);
                if (hasSymmetryAxes) symmetryAxis.SelectedIndex = 0;
                profiles.SelectedIndex = 2;
                preferRegular.Checked = false;
                stressRefinement.Checked = false;
                strictMaximumSize.Checked = false;
                edgeTolerance.Value = 20;
                structuredSectorBands.Checked = true;
                fastMode.Checked = true;
                createBackup.Checked = true;
                useMaximumCores.Checked = true;
                workerLimit.Value = Math.Min(2, workerLimit.Maximum);
                UpdateOptionState();
                UpdateGmshPreview();
            }

            private void UpdateGmshLanguage()
            {
                if (gmshPreviewOptions != null) gmshPreviewOptions.Localize();
                if (gmshPreviewOptions != null)
                    measurementToolTip.SetToolTip(gmshPreviewOptions.SmoothingSteps, L(
                        "Smoothing passes during generation and, if enabled, recombination node repositioning. 0 disables these passes; Optimize Mesh runs separately.",
                        "Passaggi di smoothing durante la generazione e, se attivo, il riposizionamento dei nodi nella ricomposizione. 0 li disattiva; Ottimizza mesh è separato."));
                if (gmshPreviewOptions != null)
                    measurementToolTip.SetToolTip(gmshPreviewOptions.RegularGrid, L(
                        "Use Gmsh automatic transfinite meshing on compatible patches. Keep joints, frames and shared boundaries conforming; other patches remain unstructured.",
                        "Usa la mesh transfinite automatica nelle zone compatibili. Nodi, frame e bordi condivisi rimangono vincolanti; le altre zone restano non strutturate."));
                gmshPreserveSelectedEdges.Text = L("Do not add nodes to edges with selected interior joints",
                    "Non aggiungere nodi sui lati con nodi interni selezionati");
                measurementToolTip.SetToolTip(gmshPreserveSelectedEdges, L(
                    "Preserve the intervals between explicitly selected SAP2000 joints on area edges. Other edges may still be subdivided; a protected interval can exceed the nominal mesh size.",
                    "Mantiene gli intervalli fra nodi SAP2000 esplicitamente selezionati sui bordi delle aree. Gli altri bordi possono essere suddivisi; un tratto protetto può superare la dimensione nominale."));
                gmshHolesTab.Text = L("Additional options", "Opzioni aggiuntive");
                gmshAdvancedTab.Text = L("Gmsh JSON", "JSON Gmsh");
                gmshSapTab.Text = L("Backup options", "Opzioni di backup");
                generateCircularHoles.Text = L("Create circular holes around this group of joints",
                    "Crea fori circolari ai nodi SAP2000 del gruppo");
                holeGroupLabel.Text = L("Group", "Gruppo");
                holeDiameterLabel.Text = L("Diameter (" + units + ")", "Diametro (" + units + ")");
                gmshOGrid.Text = L("Generate a structured O-Grid mesh",
                    "O-grid strutturata attorno ai fori circolari (quad radiali)");
                generateHoles.Text = L("Create openings around the following group of frames and/or shells edges",
                    "Ritaglia aperture delimitate da frame o spigoli del gruppo");
                openingGroupLabel.Text = L("Group", "Gruppo");
                symmetric.Text = L("Enforce mesh symmetry about the following axes",
                    "Imponi simmetria esatta rispetto all'asse o agli assi scelti");
                gmshSymmetryAxisLabel.Text = L("Axes of simmetry", "Assi di simmetria");
                UpdateSymmetryHint();
                gmshResetDefault.Text = L("Reset defaults", "Ripristina default");
                measurementToolTip.SetToolTip(gmshResetDefault, L(
                    "Reset plugin options to defaults while keeping both mesh sizes.",
                    "Ripristina le opzioni predefinite mantenendo entrambe le dimensioni della mesh."));
                gmshAdvancedLabel.Text = L("Other numeric options, one per line: Mesh.Option = value (dot decimals).",
                    "Altre opzioni numeriche, una per riga: Mesh.Opzione = valore (decimali col punto).");
                gmshAdvancedPreviewLabel.Text = L("Effective settings sent to Gmsh:", "Opzioni effettivamente inviate a Gmsh:");
                gmshAdvancedHint.Text = L("Only first-order 2D shells can be imported.",
                    "Sono importabili solo shell 2D del primo ordine.");
            }

            private void UpdateSymmetryHint()
            {
                gmshSymmetryHint.Text = !hasSelectedAreas
                    ? L("Select an area in SAP2000 to discover available symmetry axes.",
                        "Selezionare un'area in SAP2000 per trovare gli assi di simmetria disponibili.")
                    : !hasSymmetryAxes
                        ? L("* No axis of simmetry exists for the current selection",
                            "* Nessun asse di simmetria esiste per la selezione corrente")
                        : L("Select one or two perpendicular symmetry axes.",
                            "Scegliere uno o due assi di simmetria ortogonali.");
                gmshSymmetryHint.ForeColor = hasSymmetryAxes || !hasSelectedAreas ? UiMuted : Color.FromArgb(185, 28, 28);
            }

            private void ConfigureSelectionSummary()
            {
                selectionSummary.SetBounds(20, 85, 530, 64);
                selectionSummary.BackColor = UiLight;
                selectionSummary.BorderStyle = BorderStyle.FixedSingle;
                selectionAccent.SetBounds(0, 0, 4, 64);
                selectionAccent.BackColor = UiBlue;
                selectionSummary.Controls.Add(selectionAccent);
                selectionHeader.SetBounds(12, 4, 496, 18);
                selectionHeader.Font = new Font(Font, FontStyle.Bold);
                selectionHeader.ForeColor = UiNavy;
                areaSelection.SetBounds(12, 23, 496, 19);
                objectSelection.SetBounds(12, 43, 496, 19);
                selectionSummary.Controls.Add(selectionHeader);
                selectionSummary.Controls.Add(areaSelection);
                selectionSummary.Controls.Add(objectSelection);
                Controls.Add(selectionSummary);
                status.Height = 30;
                createdElements.SetBounds(20, 637, 530, 18);
                createdElements.ForeColor = UiMuted;
                Controls.Add(createdElements);
                ApplySelectionSummary();
            }

            private void ApplySelectionSummary()
            {
                bool hasGenerated = generatedInventory != null;
                selectionHeader.Text = hasGenerated ? L("Objects after meshing", "Report sugli elementi creati") :
                    L("Selection at opening", "Selezione all'apertura");
                int areas = hasGenerated ? generatedInventory.Areas : selectedAreas;
                int quads = hasGenerated ? generatedInventory.Quads : selectedQuads;
                int triangles = hasGenerated ? generatedInventory.Triangles : selectedTriangles;
                int polygons = hasGenerated ? generatedInventory.Polygons : selectedPolygons;
                int frames = hasGenerated ? generatedInventory.Frames : selectedFrameCount;
                int links = hasGenerated ? generatedInventory.Links : selectedLinks;
                int joints = hasGenerated ? generatedInventory.Joints : selectedJoints;
                areaSelection.Text = L("Areas: ", "Aree: ") + areas + "   |   " +
                    L("Quads: ", "Quadrangolari: ") + quads + "   |   " +
                    L("Triangles: ", "Triangolari: ") + triangles + "   |   " +
                    L("Polygons: ", "Poligonali: ") + polygons;
                objectSelection.Text = L("Frames: ", "Frame: ") + frames + "   |   " +
                    "Link: " + links + "   |   " + L("Joints: ", "Nodi: ") + joints;
                measurementToolTip.SetToolTip(objectSelection, hasGenerated ?
                    L("Links in the model are counted but are not meshing constraints.",
                        "I link presenti nel modello sono conteggiati ma non costituiscono vincoli di mesh.") :
                    L("Selected links are counted here but are not mesh constraints.",
                        "I link selezionati sono conteggiati ma non costituiscono vincoli di mesh."));
            }

            private void SelectProfile(int index)
            {
                profiles.SelectedIndex = index;
                ApplyProfileDefaults(index);
            }

            private void ApplyProfileDefaults(int index)
            {
                if (index == 0) { generateCircularHoles.Checked = false; preferRegular.Checked = true; stressRefinement.Checked = false; }
                else if (index == 1) { preferRegular.Checked = false; stressRefinement.Checked = true; }
                UpdateOptionState();
            }

            private void UpdateCreatedElements()
            {
                createdElements.Text = L("Created elements: ", "Elementi creati: ") + generatedElementCount;
            }
            public void BlockHostForOperation()
            {
                LockViewportForCommit();
                if (viewportWindow != IntPtr.Zero && IsWindowEnabled(viewportWindow))
                    throw new InvalidOperationException("Cannot prevent edits in SAP2000 during the mesh operation.");
            }
            public void UnblockHostAfterOperation() { UnlockViewportAfterCommit(); }
            private static bool TryGetNewRelease(string json, out string version, out string releaseUrl, out string downloadUrl)
            {
                version = releaseUrl = downloadUrl = null;
                var release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                object tag, url, assets, flag;
                if (release == null || !release.TryGetValue("tag_name", out tag) ||
                    !release.TryGetValue("html_url", out url) ||
                    !release.TryGetValue("assets", out assets) ||
                    (release.TryGetValue("draft", out flag) && Convert.ToBoolean(flag)) ||
                    (release.TryGetValue("prerelease", out flag) && Convert.ToBoolean(flag))) return false;
                string tagName = Convert.ToString(tag, CultureInfo.InvariantCulture);
                if (string.IsNullOrEmpty(tagName) || !Regex.IsMatch(tagName, @"^v\d+\.\d+\.\d+$")) return false;
                Version candidate, current;
                if (!Version.TryParse(tagName.Substring(1), out candidate) ||
                    !Version.TryParse(PluginVersion, out current) || candidate.CompareTo(current) <= 0) return false;
                Uri uri;
                if (!Uri.TryCreate(Convert.ToString(url, CultureInfo.InvariantCulture), UriKind.Absolute, out uri) ||
                    uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com" ||
                    uri.AbsolutePath != "/leonardobandini/MESHing/releases/tag/" + tagName ||
                    assets is string || !(assets is System.Collections.IEnumerable)) return false;
                bool hasInstaller = false;
                foreach (object item in (System.Collections.IEnumerable)assets)
                {
                    var asset = item as Dictionary<string, object>;
                    object name, download;
                    if (asset == null || !asset.TryGetValue("name", out name) ||
                        !asset.TryGetValue("browser_download_url", out download) ||
                        Convert.ToString(name, CultureInfo.InvariantCulture) != "MESHing-latest.zip") continue;
                    Uri downloadUri;
                    if (Uri.TryCreate(Convert.ToString(download, CultureInfo.InvariantCulture), UriKind.Absolute, out downloadUri) &&
                        downloadUri.Scheme == Uri.UriSchemeHttps && downloadUri.Host == "github.com" &&
                        downloadUri.AbsolutePath == "/leonardobandini/MESHing/releases/download/" + tagName + "/MESHing-latest.zip")
                        hasInstaller = true;
                }
                if (!hasInstaller) return false;
                version = candidate.ToString();
                releaseUrl = uri.AbsoluteUri;
                downloadUrl = LatestDownloadUrl;
                return true;
            }
            private async Task CheckForUpdatesAsync()
            {
                if (checkingUpdates || IsDisposed) return;
                checkingUpdates = true;
                updateNotice.Text = L("Checking for updates...", "Ricerca aggiornamenti...");
                try
                {
                    using (var handler = new HttpClientHandler { SslProtocols = SslProtocols.Tls12 })
                    using (var client = new HttpClient(handler))
                    {
                        client.Timeout = TimeSpan.FromSeconds(3);
                        client.DefaultRequestHeaders.UserAgent.ParseAdd("SapQuadMesher/" + PluginVersion);
                        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                        string json = Environment.GetEnvironmentVariable("SAP_QUAD_TEST_UPDATE_MANIFEST_JSON");
                        if (string.IsNullOrEmpty(json)) json = await client.GetStringAsync(UpdateManifestUrl);
                        string version, releaseUrl, downloadUrl;
                        updateCheckSucceeded = true;
                        if (TryGetNewRelease(json, out version, out releaseUrl, out downloadUrl))
                        {
                            availableVersion = version;
                            availableReleaseUrl = releaseUrl;
                            availableDownloadUrl = downloadUrl;
                        }
                    }
                }
                catch (Exception)
                {
                    // An unavailable network must not prevent the options window from opening.
                    updateCheckSucceeded = false;
                }
                finally
                {
                    checkingUpdates = false;
                    if (!IsDisposed) UpdateNoticeText();
                }
            }
            private void UpdateNoticeText()
            {
                bool hasUpdate = !string.IsNullOrEmpty(availableDownloadUrl);
                if (hasUpdate && !updateBlinkTimer.Enabled) updateBlinkTimer.Start();
                if (!hasUpdate && updateBlinkTimer.Enabled) updateBlinkTimer.Stop();
                if (!hasUpdate) { updateNotice.BackColor = Color.White; updateNotice.LinkColor = UiBlue; }
                updateNotice.Text = checkingUpdates ? L("Checking for updates...", "Ricerca aggiornamenti...") :
                    hasUpdate ? L("Update available\nDownload ", "Aggiornamento disponibile\nScarica ") + availableVersion :
                    updateCheckSucceeded ? L("Version " + PluginVersion + " is up to date", "La versione " + PluginVersion + " è aggiornata") :
                    L("Check for updates", "Cerca aggiornamenti");
                updateNotice.Font = hasUpdate ? updateNoticeBoldFont : Font;
            }
            private static string DefaultLength(double meters, string lengthUnit)
            {
                return (meters / MetersPerUnit(lengthUnit)).ToString("0.########", CultureInfo.InvariantCulture);
            }
            public void SetMeshingAreas(int completed, int total)
            {
                if (completed == 0)
                {
                    meshClock.Restart();
                    elapsedTimer.Start();
                }
                meshAreasCompleted = completed;
                meshAreasTotal = total;
                meshingActive = completed < total;
                if (cancellationController != null && total > 0)
                    cancellationController.ReportArea(meshingActive
                        ? L("Area ", "Area ") + (completed + 1) + "/" + total
                        : L("Areas processed: ", "Aree elaborate: ") + total + "/" + total);
                if (meshingActive) UpdateMeshingStatus();
                else elapsedTimer.Stop();
            }
            public void ShowBatchAreas(int total, bool importing = false)
            {
                if (cancellationController == null || total < 2) return;
                cancellationController.ReportArea((importing
                    ? L("SAP2000 import: areas 1–", "Importazione SAP2000: aree 1–")
                    : L("Gmsh: areas 1–", "Gmsh: aree 1–")) + total + "/" + total +
                    L(" together", " insieme"));
            }
            public void ApplyAutomatedOptions()
            {
                string profile = Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_PROFILE");
                int index = profile == "Reinforced Concrete" ? 0 : profile == "Steel Structures" ? 1 : 2;
                SelectProfile(index);
                size.Text = Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SIZE");
                strictMaximumSize.Checked = Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_STRICT_SIZE") == "1";
                structuredSectorBands.Checked = Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SECTOR_BLOCKS") != "0";
                string toleranceText = Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_EDGE_TOLERANCE");
                decimal tolerance;
                if (!string.IsNullOrEmpty(toleranceText))
                {
                    if (!decimal.TryParse(toleranceText, NumberStyles.Integer, CultureInfo.InvariantCulture, out tolerance)
                        || tolerance < edgeTolerance.Minimum || tolerance > edgeTolerance.Maximum || strictMaximumSize.Checked)
                        throw new InvalidOperationException("Invalid automated edge tolerance percentage.");
                    edgeTolerance.Value = tolerance;
                }
                else if (!strictMaximumSize.Checked)
                    edgeTolerance.Value = 0; // Keep atomic fixtures' explicit historical 0% limit.
                string group = Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_HOLE_GROUP");
                if (!string.IsNullOrEmpty(group))
                {
                    if (!holeGroup.Items.Contains(group)) throw new InvalidOperationException("Hole-center group not found: " + group);
                    generateCircularHoles.Checked = true;
                    holeGroup.SelectedItem = group;
                    holeDiameter.Text = Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_DIAMETER");
                }
                string opening = Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_OPENING_GROUP");
                if (!string.IsNullOrEmpty(opening))
                {
                    if (!openingGroup.Items.Contains(opening)) throw new InvalidOperationException("Opening-boundary group not found: " + opening);
                    generateHoles.Checked = true;
                    openingGroup.SelectedItem = opening;
                }
                if (Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_SYMMETRY") == "1") symmetric.Checked = true;
                string axisIndexText = Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_SYMMETRY_INDEX");
                int axisIndex;
                if (!string.IsNullOrEmpty(axisIndexText))
                {
                    if (!int.TryParse(axisIndexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out axisIndex) ||
                        axisIndex < 0 || axisIndex >= symmetryAxis.Items.Count)
                        throw new InvalidOperationException("Invalid automated V2 symmetry-axis index.");
                    symmetryAxis.SelectedIndex = axisIndex;
                }
                fastMode.Checked = Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_FAST") == "1";
                createBackup.Checked = Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_BACKUP") == "1";
                if (Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_REGULAR") == "1") preferRegular.Checked = true;
                if (Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_REFINED") == "1") stressRefinement.Checked = true;
                string workers = Environment.GetEnvironmentVariable("SAP_QUAD_ATOMIC_WORKERS");
                int requested;
                if (int.TryParse(workers, out requested) && requested >= 1 && requested <= workerLimit.Maximum)
                {
                    useMaximumCores.Checked = false;
                    workerLimit.Value = requested;
                }
                if (Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_OGRID") == "1")
                    gmshOGrid.Checked = true;
                if (Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_QUAD_QS") == "1")
                    gmshPreviewOptions.Optimizer.SelectedIndex = 2;
                if (Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_REGULAR_GRID") == "1")
                    gmshPreviewOptions.RegularGrid.Checked = true;
                if (Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_PRESERVE_EDGES") == "1")
                    gmshPreserveSelectedEdges.Checked = true;
                string recombinationText = Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_RECOMBINATION");
                int recombination;
                if (!string.IsNullOrEmpty(recombinationText))
                {
                    if (!int.TryParse(recombinationText, NumberStyles.Integer, CultureInfo.InvariantCulture,
                        out recombination) || recombination < 0 || recombination > 3)
                        throw new InvalidOperationException("Invalid automated V2 recombination method.");
                    gmshPreviewOptions.Recombine.Checked = true;
                    gmshPreviewOptions.RecombinationMethod.SelectedIndex = recombination;
                }
                gmshPreviewOptions.MaxSize.Text = size.Text;
                string previewValues = Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_PREVIEW_VALUES_JSON");
                if (!string.IsNullOrEmpty(previewValues))
                    gmshPreviewOptions.Restore(new JavaScriptSerializer().Deserialize<GmshPreviewPanel.Values>(previewValues));
                gmshPreviewTabs.SelectedTab = gmshPreviewEmptyTab;
                if (Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_UI_PREVIEW") == "1")
                {
                    Console.WriteLine("gmsh-v2-preview-settings=" + GmshSettingsJson);
                    string previewScreenshot = Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_UI_SCREENSHOT");
                    if (!string.IsNullOrEmpty(previewScreenshot))
                    {
                        if (!Directory.Exists(Path.GetDirectoryName(previewScreenshot)))
                            throw new DirectoryNotFoundException("Preview screenshot destination does not exist.");
                        string directory = Path.GetDirectoryName(previewScreenshot);
                        foreach (var capture in new[] {
                            new { Page = gmshPreviewEmptyTab, File = previewScreenshot, ScrollSymmetry = false },
                            new { Page = gmshHolesTab, File = Path.Combine(directory, "new-ui-additional.png"), ScrollSymmetry = false },
                            new { Page = gmshHolesTab, File = Path.Combine(directory, "new-ui-symmetry.png"), ScrollSymmetry = true },
                            new { Page = gmshSapTab, File = Path.Combine(directory, "new-ui-transfer.png"), ScrollSymmetry = false }
                        })
                        {
                            gmshPreviewTabs.SelectedTab = capture.Page;
                            if (capture.Page == gmshHolesTab)
                            {
                                if (capture.ScrollSymmetry) gmshHolesTab.ScrollControlIntoView(gmshSymmetryHint);
                                else gmshHolesTab.AutoScrollPosition = Point.Empty;
                            }
                            Application.DoEvents();
                            using (var screenshot = new Bitmap(Width, Height))
                            {
                                DrawToBitmap(screenshot, new Rectangle(Point.Empty, screenshot.Size));
                                screenshot.Save(capture.File, System.Drawing.Imaging.ImageFormat.Png);
                            }
                        }
                        gmshPreviewTabs.SelectedTab = gmshPreviewEmptyTab;
                    }
                    int pauseMilliseconds;
                    if (int.TryParse(Environment.GetEnvironmentVariable("SAP_GMSH_V2_ATOMIC_UI_PAUSE_MS"), out pauseMilliseconds) &&
                        pauseMilliseconds > 0 && pauseMilliseconds <= 10000)
                    {
                        Stopwatch pause = Stopwatch.StartNew();
                        while (pause.ElapsedMilliseconds < pauseMilliseconds)
                        { Application.DoEvents(); Thread.Sleep(40); }
                    }
                }
                generate.PerformClick();
                if (!StartRequested) throw new InvalidOperationException("The automated mesh options could not be applied.");
                Console.WriteLine("mesh-stage=automation-generate");
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    StopCancellationController();
                    // In interactive mode callback.Finish has already released
                    // SAP's modal command loop: disabling the host here would
                    // leave SAP2000 unresponsive after MESHing closes.
                    if (viewportWasDisabled && !callbackReleasedInSap()) PauseViewportInteraction();
                    elapsedTimer.Dispose(); updateBlinkTimer.Dispose(); updateNoticeBoldFont.Dispose(); measurementToolTip.Dispose();
                }
                base.Dispose(disposing);
            }
            private void UpdateMeshingStatus()
            {
                if (CancelRequested) return;
                string elapsed = L("Elapsed: ", "Trascorso: ") + meshClock.Elapsed.ToString(@"mm\:ss");
                string estimate = L("ETA available after the first area.", "Stima disponibile dopo la prima area.");
                if (meshAreasCompleted > 0)
                {
                    TimeSpan estimatedTotal = TimeSpan.FromTicks(
                        meshClock.Elapsed.Ticks * meshAreasTotal / meshAreasCompleted);
                    TimeSpan remaining = estimatedTotal - meshClock.Elapsed;
                    estimate = L("Estimated remaining: ", "Tempo residuo stimato: ") + remaining.ToString(@"mm\:ss");
                }
                string message = GmshOptimizeEnabled
                    ? L("Generating and optimizing Gmsh mesh", "Generazione e ottimizzazione mesh Gmsh")
                    : L("Generating Gmsh mesh", "Generazione mesh Gmsh");
                message += "\n" + elapsed + "  |  " + estimate;
                if (cancellationController != null)
                    cancellationController.Report(message, Math.Max(0, Math.Min(100,
                        (int)Math.Round(100.0 * meshAreasCompleted / meshAreasTotal))));
                else status.Text = message;
            }
            private void ApplyLanguage()
            {
                headerTitle.Text = "MESHing";
                // Reserve the right side of the header for the language selector.
                headerSubtitle.SetBounds(24, 42, ClientSize.Width - 170, 33);
                headerSubtitle.Text = "Advanced 2D mesh generation";
                verifiedExamples.Text = L("Verified examples", "Esempi verificati");
                profiles.TabPages[0].Text = L("Reinforced Concrete", "Calcestruzzo armato");
                profiles.TabPages[1].Text = L("Steel Structures", "Strutture in acciaio");
                profiles.TabPages[2].Text = L("General Purpose", "Uso generale");
                symmetric.Text = L("Enforce symmetry", "Imponi simmetria");
                measurementToolTip.SetToolTip(symmetric, L(
                    "Enforces mesh symmetry about one or two selected axes. This is a strong meshing constraint and may not always be achievable, even when the original geometry is symmetric.",
                    "Impone la simmetria della meshatura secondo uno o due assi selezionati. È un vincolo molto forte nella creazione della mesh e non sempre può essere applicato, anche quando la geometria iniziale è simmetrica."));
                generateHoles.Text = L("Generate openings enclosed by selected frames or shell edges", "Genera aperture racchiuse da frame o bordi shell selezionati");
                openingGroupLabel.Text = L("Opening-boundary group:", "Gruppo contorno aperture:");
                generateCircularHoles.Text = L("Generate circular O-grid holes", "Genera fori circolari O-grid");
                holeGroupLabel.Text = L("Hole-center group:", "Gruppo centri dei fori:");
                holeDiameterLabel.Text = L("Diameter (", "Diametro (") + units + ")";
                string tip = L("Enter a number in the current SAP2000 unit (" + units + ") or add m, cm, mm, ft, in; for example 25mm or 2.5ft. Explicit units are converted automatically.",
                    "Inserire un numero nell'unita SAP2000 corrente (" + units + ") oppure aggiungere m, cm, mm, ft, in; per esempio 25mm o 2,5ft. Le unita esplicite sono convertite automaticamente.");
                measurementToolTip.SetToolTip(holeDiameter, tip);
                string host = HostProductName();
                string extension = host == "CSiBridge" ? "BDB" : host == "ETABS" ? "EDB" : "SDB";
                createBackupDescription.Text = L("Before every Generate, selecting this option requests a copy of the " +
                    extension + " model file on disk. This file contains the model state at the user's last save. " +
                    "Therefore this backup does not include any changes made to the model since that save.",
                    "Prima di ogni Genera, selezionando questa opzione si richiede la copia del file " + extension +
                    " del modello presente sul disco, questo file contiene lo stato del modello con l'ultimo salvataggio che l'Utente ha fatto. " +
                    "Questo backup quindi non include le modifiche eventuali al modello fatte dall'ultimo salvataggio.");
                createBackup.AccessibleName = createBackupDescription.Text;
                measurementToolTip.SetToolTip(createBackup, createBackupDescription.Text);
                measurementToolTip.SetToolTip(createBackupDescription, measurementToolTip.GetToolTip(createBackup));
                ApplySelectionSummary();
                UpdateCreatedElements();
                generate.Text = L("Generate", "Genera");
                undo.Text = L("Undo mesh", "Annulla mesh");
                details.Text = L("Mesh quality", "Qualita della mesh");
                if (!working && qualityReport == null && !RetryRequested)
                    status.Text = requiresSavedModel ? SaveModelFirstText() : L("Ready.", "Pronto.");
                if (!hasSelectedAreas && !requiresSavedModel && qualityReport == null) status.Text = L("Select at least one area in " + HostProductName() + "; this window updates automatically.",
                    "Selezionare almeno un'area in " + HostProductName() + ": questa finestra si aggiorna automaticamente.");
                UpdateNoticeText();
                foreach (Control control in Controls)
                {
                    if (control.Tag as string == "version") control.Text = L("Version ", "Versione ") + PluginVersion;
                    if (control.Tag as string == "license-link") control.Text = L("GPL / Source", "GPL / Fonti");
                }
                object selected = symmetryAxis.SelectedItem;
                symmetryAxis.Items.Clear(); symmetryAxis.Items.AddRange(symmetryAxes);
                if (selected != null) symmetryAxis.SelectedItem = selected;
                else if (hasSymmetryAxes) symmetryAxis.SelectedIndex = 0;
                UpdateGmshLanguage();
            }
            private void UpdateOptionState()
            {
                generateCircularHoles.Visible = holeGroupLabel.Visible = holeGroup.Visible = holeDiameterLabel.Visible = holeDiameter.Visible = true;
                preferRegular.Visible = stressRefinement.Visible = false;
                UpdateSymmetryHint();
                if (working) return;
                if (!hasSelectedAreas)
                {
                    profiles.Enabled = size.Enabled = edgeTolerance.Enabled = strictMaximumSize.Enabled = symmetric.Enabled = symmetryAxis.Enabled = false;
                    generateHoles.Enabled = generateCircularHoles.Enabled = true;
                    openingGroup.Enabled = generateHoles.Checked && openingGroup.Items.Count > 0;
                    gmshOGrid.Enabled = generateCircularHoles.Checked;
                    holeGroup.Enabled = generateCircularHoles.Checked && holeGroup.Items.Count > 0;
                    holeDiameter.Enabled = generateCircularHoles.Checked;
                    preferRegular.Enabled = stressRefinement.Enabled = structuredSectorBands.Enabled = false;
                    useMaximumCores.Enabled = workerLimit.Enabled = generate.Enabled = false;
                    createBackup.Enabled = !requiresSavedModel;
                    createBackupDescription.Enabled = !requiresSavedModel;
                    return;
                }
                generate.Enabled = !requiresSavedModel && (qualityReport == null || Applied);
                symmetric.Enabled = hasSymmetryAxes;
                strictMaximumSize.Enabled = edgeTolerance.Enabled = false;
                symmetryAxis.Enabled = symmetric.Checked && hasSymmetryAxes;
                // Incident shell edges can close a grouped opening even when
                // no independent frame object was explicitly selected.
                // The user may choose an option before defining its group. Only
                // the dependent selector needs a group; Generate already gives
                // an explicit missing-group message instead of changing intent.
                generateHoles.Enabled = true;
                gmshOGrid.Enabled = generateCircularHoles.Checked;
                openingGroup.Enabled = generateHoles.Checked && openingGroup.Items.Count > 0;
                generateCircularHoles.Enabled = true;
                holeGroup.Enabled = generateCircularHoles.Checked && holeGroup.Items.Count > 0;
                holeDiameter.Enabled = generateCircularHoles.Checked;
                structuredSectorBands.Enabled = hasFrames;
                useMaximumCores.Enabled = true;
                workerLimit.Enabled = !useMaximumCores.Checked;
                createBackup.Enabled = true;
                createBackupDescription.Enabled = true;
            }
            public void BeginWork(string text)
            {
                generationCompletionStatus = null;
                insertionNotice = null;
                preservationWarning = null;
                designTransferLossWarning = false;
                transferWarnings.Clear();
                reportedTransferWarnings = 0;
                if (cancellationController == null) cancellationController = new CancellationController(useItalian, Bounds, Handle);
                PauseViewportInteraction();
                working = true;
                meshAreasCompleted = meshAreasTotal = 0;
                ControlBox = false;
                size.Enabled = edgeTolerance.Enabled = strictMaximumSize.Enabled = false;
                profiles.Enabled = false;
                symmetric.Enabled = false;
                symmetryAxis.Enabled = false;
                generateHoles.Enabled = false;
                openingGroup.Enabled = false;
                generateCircularHoles.Enabled = false;
                holeGroup.Enabled = false;
                holeDiameter.Enabled = false;
                preferRegular.Enabled = false;
                stressRefinement.Enabled = false;
                structuredSectorBands.Enabled = false;
                fastMode.Enabled = false;
                useMaximumCores.Enabled = workerLimit.Enabled = false;
                createBackup.Enabled = false;
                createBackupDescription.Enabled = false;
                gmshPreviewTabs.Enabled = false;
                gmshResetDefault.Enabled = false;
                language.Enabled = false;
                generate.Enabled = false;
                undo.Enabled = false;
                undo.Text = L("Undo mesh", "Annulla mesh");
                details.Enabled = false;
                status.Text = "";
                SetIndeterminate(text);
            }
            public void SetIndeterminate(string text)
            {
                meshingActive = (text == "Generating and optimizing the mesh..." ||
                    text == "Generating the mesh...") && meshAreasTotal > meshAreasCompleted;
                string message = LocalizeMessage(text);
                if (meshingActive) UpdateMeshingStatus();
                else
                {
                    status.Text = message;
                    if (working && cancellationController != null) cancellationController.Report(message);
                }
                Application.DoEvents();
                ThrowIfCancellationRequested();
            }
            public void ShowPreservationWarning(string warning)
            {
                preservationWarning = warning;
                status.Text = L("Design assignments require manual review. See warning for details.",
                    "Le assegnazioni di progetto richiedono una verifica manuale. Vedere l'avviso.");
                measurementToolTip.SetToolTip(status, warning);
                string trace = Environment.GetEnvironmentVariable("SAP_QUAD_ASSIGNMENT_WARNING_TRACE");
                if (!string.IsNullOrEmpty(trace))
                    try { File.WriteAllText(trace, warning); } catch (IOException) { }
            }
            public void ShowDesignTransferLossWarning()
            {
                designTransferLossWarning = true;
                string warning = confirmationOnlyApply ? L(
                    "SAP2000 did not transfer Concrete Shell Design to the generated areas (No Design). " +
                    "Confirming the mesh discards Undo; Undo reloads the saved pre-mesh model.",
                    "SAP2000 non ha trasferito Concrete Shell Design alle nuove aree (No Design). " +
                    "Confermando la mesh si rinuncia ad Annulla; Annulla ricarica il modello salvato prima della mesh.") :
                    L("SAP2000 did not transfer Concrete Shell Design to the generated areas (No Design). " +
                        "If you Apply, the original shell design procedure is lost and any design overwrites must be " +
                        "assigned again. Undo mesh restores the original model from the CSV snapshot.",
                    "SAP2000 non ha trasferito Concrete Shell Design alle nuove aree (No Design). " +
                        "Se scegli Applica perderai la procedura di progetto originale; eventuali sovrascritture " +
                        "andranno riassegnate. Annulla mesh ripristina il modello originale dallo snapshot CSV.");
                preservationWarning = string.IsNullOrEmpty(preservationWarning)
                    ? warning : preservationWarning + Environment.NewLine + warning;
                status.Text = warning;
                measurementToolTip.SetToolTip(status, preservationWarning);
                Console.WriteLine("mesh-design-transfer-loss=Concrete Shell Design; apply requires explicit consent");
            }
            public bool CheckOptionalTransfer(int result, string sourceArea, string target,
                string property, string api)
            {
                if (result == 0) return true;
                if (result == 1)
                {
                    RecordOptionalTransfer(sourceArea, target, property, api);
                    return false;
                }
                Check(result, "transferring " + property + " from " + sourceArea + " to " + target, api);
                return false;
            }
            private void RecordOptionalTransfer(string sourceArea, string target, string property, string api)
            {
                string key = "Area " + sourceArea + ": " + property + " [API: " + api + ", code 1]";
                HashSet<string> targets;
                if (!transferWarnings.TryGetValue(key, out targets))
                    transferWarnings[key] = targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                targets.Add(target);
            }
            public bool WarnOptionalTransfer(InvalidOperationException error, string sourceArea, string target)
            {
                Match match = Regex.Match(error.Message,
                    @"^SAP2000 error while transferring (?<property>.+?) \[API: (?<api>[^\]]+)\]: code 1(?:\b|$)");
                if (!match.Success) return false;
                string property = match.Groups["property"].Value;
                if (property.StartsWith("area stiffness modifiers to ", StringComparison.Ordinal))
                    property = "area stiffness modifiers";
                RecordOptionalTransfer(sourceArea, target, property, match.Groups["api"].Value);
                return true;
            }
            public bool WarnOptionalApiFailure(InvalidOperationException error, string sourceArea,
                string target, string property)
            {
                Match match = Regex.Match(error.Message,
                    @"^SAP2000 error while [^\r\n]+ \[API: (?<api>[^\]]+)\]: code 1(?:\b|$)");
                if (!match.Success) return false;
                RecordOptionalTransfer(sourceArea, target, property, match.Groups["api"].Value);
                return true;
            }
            private void ShowTransferWarnings()
            {
                if (transferWarnings.Count == 0) return;
                string details = string.Join(Environment.NewLine, transferWarnings.Select(item =>
                    item.Key + " — " + item.Value.Count + L(" element(s), e.g. ", " elementi, per es. ") +
                    item.Value.First()));
                status.Text = L("Mesh created; some property transfers need review. See the warning.",
                    "Mesh creata; alcuni trasferimenti di proprietà richiedono verifica. Vedere l'avviso.");
                measurementToolTip.SetToolTip(status, (preservationWarning ?? "") + Environment.NewLine + details);
                if (transferWarnings.Count == reportedTransferWarnings) return;
                reportedTransferWarnings = transferWarnings.Count;
                ShowHostMessage(L("Mesh created, but SAP2000 could not transfer or verify these properties " +
                    "(code 1). Assign and check them manually on the affected elements:",
                    "Mesh creata, ma SAP2000 non ha trasferito o verificato queste proprietà " +
                    "(codice 1). Assegnarle e verificarle manualmente sugli elementi interessati:") + Environment.NewLine + Environment.NewLine +
                    details, WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            public void SetProgress(string text, int completed, int total)
            {
                meshingActive = false;
                int percent = Math.Max(0, Math.Min(100, total == 0 ? 100 : (int)Math.Round(100.0 * completed / total)));
                string message = LocalizeMessage(text) + "\n" +
                    L("Phase progress: ", "Avanzamento fase: ") + completed + "/" + total +
                    " (" + percent + "%)";
                if (working && cancellationController != null) cancellationController.Report(message, percent);
                else status.Text = message;
                Application.DoEvents();
                ThrowIfCancellationRequested();
            }
            public void ThrowIfCancellationRequested()
            {
                ThrowIfRegionalSettingsChanged();
                if (CancelRequested) throw new OperationCanceledException("Mesh generation cancelled by the user.");
            }
            public void ThrowIfRegionalSettingsChanged()
            {
                if (DetectRegionalChange())
                    throw new InvalidOperationException("Windows regional settings changed during meshing.");
            }
            public void Cancelled()
            {
                StopCancellationController();
                measurementToolTip.SetToolTip(status, "");
                if (viewportWasDisabled) AllowViewportInteraction(viewportWindow);
                meshingActive = false;
                elapsedTimer.Stop();
                StartRequested = false;
                CancelRequested = false;
                working = false;
                Applied = false;
                ControlBox = true;
                qualityReport = null;
                undoAction = null;
                applyAction = appliedUndoAction = null;
                completedModelGuard = null;
                onApplied = null;
                applyBackupInfo = null;
                snapshotPath = null;
                generatedElementCount = 0;
                UpdateCreatedElements();
                SetGeneratedInventory(null);
                gmshPreviewTabs.Enabled = true;
                gmshResetDefault.Enabled = true;
                size.Enabled = strictMaximumSize.Enabled = profiles.Enabled = language.Enabled = fastMode.Enabled = generate.Enabled = useMaximumCores.Enabled = true;
                UpdateOptionState();
                undo.Enabled = details.Enabled = false;
                undo.Text = L("Undo mesh", "Annulla mesh");
                generate.Text = L("Generate", "Genera");
                status.Text = L("Generation cancelled. Original model and selection restored; adjust the options and try again.",
                    "Generazione annullata. Modello e selezione originali ripristinati; modificare le opzioni e riprovare.");
            }
            public void Complete(MeshQualityReport report, int generatedAreaCount, MeshInventory afterMeshing, string symmetryNotice,
                string automaticChoice,
                Action undoMesh, Action applyMesh, Action undoAppliedMesh, string originalSnapshotPath)
            {
                ThrowIfCancellationRequested();
                StopCancellationController();
                meshingActive = false;
                elapsedTimer.Stop();
                operationClock.Stop();
                qualityReport = report;
                undoAction = undoMesh;
                applyAction = applyMesh;
                appliedUndoAction = undoAppliedMesh;
                snapshotPath = originalSnapshotPath;
                UndoFailure = null;
                working = false;
                Applied = false;
                ControlBox = true;
                generatedElementCount = generatedAreaCount;
                UpdateCreatedElements();
                SetGeneratedInventory(afterMeshing);
                gmshPreviewTabs.Enabled = true;
                gmshResetDefault.Enabled = true;
                UpdateOptionState();
                status.Text = report.Passed
                    ? L("Mesh completed. Area elements created: ", "Mesh completata. Elementi area creati: ") + generatedAreaCount +
                        L(". Quality checks passed.", ". Controlli di qualita superati.")
                    : L("Mesh completed. Area elements created: ", "Mesh completata. Elementi area creati: ") + generatedAreaCount +
                        L(". Review the quality warnings.", ". Verificare gli avvisi sulla qualita.");
                status.Text += L(" Elapsed: ", " Tempo impiegato: ") +
                    operationClock.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s.";
                if (insertionNotice != null)
                {
                    status.Text += Environment.NewLine + insertionNotice;
                    measurementToolTip.SetToolTip(status,
                        (preservationWarning == null ? "" : preservationWarning + Environment.NewLine) + insertionNotice);
                }
                if (preservationWarning != null)
                {
                    status.Text += L(" Design assignments: manual replication required.",
                        " Assegnazioni di progetto: replicazione manuale necessaria.");
                    if (insertionNotice == null) measurementToolTip.SetToolTip(status, preservationWarning);
                }
                if (symmetric.Checked)
                    status.Text += symmetryNotice == null ? L(" Exact symmetry applied.", " Simmetria esatta applicata.") :
                        L(" Exact symmetry was not applied: ", " La simmetria esatta non e stata applicata: ") +
                        LocalizeMessage(symmetryNotice) + L(". A non-symmetric mesh was generated instead.", ". E stata invece generata una mesh non simmetrica.");
                if (automaticChoice != null)
                {
                    status.Text += L(" Automatic choice: ", " Scelta automatica: ") +
                        StrategyName(automaticChoice) + ".";
                    RememberOptions();
                }
                string completionTrace = Environment.GetEnvironmentVariable("SAP_QUAD_COMPLETION_TRACE");
                if (!string.IsNullOrEmpty(completionTrace))
                    try { File.WriteAllText(completionTrace, "elements=" + generatedAreaCount.ToString(CultureInfo.InvariantCulture) +
                        Environment.NewLine + "seconds=" + operationClock.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture) +
                        Environment.NewLine + "message=" + status.Text +
                        Environment.NewLine + "quality_passed=" + report.Passed +
                        Environment.NewLine + "quality_score=" + report.OverallScore.ToString("R", CultureInfo.InvariantCulture) +
                        Environment.NewLine + "quality_summary=" + report.Summary +
                        Environment.NewLine + "after_mesh_areas=" + afterMeshing.Areas +
                        " quads=" + afterMeshing.Quads + " triangles=" + afterMeshing.Triangles +
                        " polygons=" + afterMeshing.Polygons + " frames=" + afterMeshing.Frames +
                        " links=" + afterMeshing.Links + " joints=" + afterMeshing.Joints +
                        Environment.NewLine + "quality_metrics=" + string.Join("; ", report.Metrics.Select(metric =>
                            metric.Name + "=" + metric.Value)) + Environment.NewLine); } catch { }
                generationCompletionStatus = status.Text;
                details.Enabled = true;
                undo.Enabled = true;
                undo.Text = L("Undo mesh", "Annulla mesh");
                generate.Enabled = true;
                generate.Text = L("Generate", "Genera");
                language.Enabled = true;
                AcceptButton = details;
                ShowTransferWarnings();
                // The completed-mesh status must not retain the lengthy
                // preservation/transfer warning tooltip from generation.
                measurementToolTip.SetToolTip(status, "");
                Application.DoEvents();
                ThrowIfCancellationRequested();
            }
            private string StrategyName(string strategy)
            {
                if (strategy == "Regular") return L("Regular", "Regolare");
                if (strategy == "Refined") return L("Refinement", "Raffinamento");
                return L("Standard", "Standard");
            }
            public void ShowAutomaticChoice(string strategy, int elements, int triangles, int points, int frames)
            {
                string text = L("For this shape and its geometric constraints, the requested settings did not yield " +
                        "an acceptable mesh. The plugin selected:",
                        "Per la forma e i vincoli geometrici presenti, le opzioni richieste non hanno prodotto " +
                        "una mesh accettabile. Il plugin ha scelto:") + Environment.NewLine + Environment.NewLine +
                    L("Mesh: ", "Mesh: ") + StrategyName(strategy) +
                    L("  |  Maximum size: ", "  |  Dimensione massima: ") +
                    MaxSize.ToString("0.###", CultureInfo.CurrentCulture) + " " + units +
                    (StrictMaximumSize ? L(" (strict)", " (rigida)") :
                        L(" (tolerance ", " (tolleranza ") + EdgeTolerancePercent + "%)") + Environment.NewLine +
                    L("Insert mesh nodes on edges with selected joints: ", "Inserisci nodi di mesh negli spigoli con nodi selezionati: ") +
                    (StrictMaximumSize ? L("yes", "sì") : L("no", "no")) + Environment.NewLine +
                    L("Constraints retained: ", " Vincoli mantenuti: ") + points + L(" joints, ", " nodi, ") +
                    frames + L(" frames", " frame") +
                    (GenerateHoles ? L(", openings", ", aperture") : "") +
                    (GenerateCircularHoles ? L(", circular holes", ", fori circolari") : "") +
                    L("  |  Symmetry: ", "  |  Simmetria: ") +
                    (Symmetric ? L("yes", "si") : L("no", "no")) + Environment.NewLine +
                    L("Result: ", "Risultato: ") + elements + L(" elements, ", " elementi, ") + triangles +
                    L(" triangles. Quality: ", " triangoli. Qualita: ") +
                    (qualityReport.Passed ? L("acceptable", "accettabile") : L("review warnings", "verificare gli avvisi"));
                ShowHostMessage(text, WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            public void Fail(string message)
            {
                StopCancellationController();
                if (viewportWasDisabled) AllowViewportInteraction(viewportWindow);
                meshingActive = false;
                elapsedTimer.Stop();
                StartRequested = false;
                CancelRequested = false;
                working = false;
                Applied = false;
                ControlBox = true;
                qualityReport = null;
                undoAction = null;
                applyAction = appliedUndoAction = null;
                completedModelGuard = null;
                onApplied = null;
                snapshotPath = null;
                generatedElementCount = 0;
                UpdateCreatedElements();
                SetGeneratedInventory(null);
                gmshPreviewTabs.Enabled = true;
                gmshResetDefault.Enabled = true;
                status.Text = L("Mesh generation did not complete. Change the options and try again.",
                    "Generazione della mesh non completata. Modificare le opzioni e riprovare.");
                size.Enabled = true;
                profiles.Enabled = true;
                UpdateOptionState();
                fastMode.Enabled = true;
                language.Enabled = true;
                generate.Enabled = true;
                generate.Text = L("Generate", "Genera");
                undo.Enabled = false;
                undo.Text = L("Undo mesh", "Annulla mesh");
                details.Enabled = false;
                ShowHostMessage(LocalizeErrorMessage(message), WindowTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                Activate();
            }
            private void ResetReady()
            {
                StopCancellationController();
                StartRequested = false;
                CancelRequested = false;
                RetryRequested = true;
                working = false;
                Applied = false;
                applyAction = appliedUndoAction = null;
                completedModelGuard = null;
                onApplied = null;
                applyBackupInfo = null;
                snapshotPath = null;
                ControlBox = true;
                gmshPreviewTabs.Enabled = true;
                gmshResetDefault.Enabled = true;
                generatedElementCount = 0;
                UpdateCreatedElements();
                SetGeneratedInventory(null);
                status.Text = preservationWarning == null
                    ? L("Original objects and selection restored. Ready to generate again.",
                        "Oggetti originali e selezione ripristinati. Pronto per generare di nuovo.")
                    : L("Geometry restored. Design overrides require manual review; see the saved backup.",
                        "Geometria ripristinata. Verificare le sovrascritture di progetto; vedere la copia salvata.");
                if (preservationWarning != null) measurementToolTip.SetToolTip(status, preservationWarning);
                size.Enabled = true;
                profiles.Enabled = true;
                UpdateOptionState();
                fastMode.Enabled = true;
                language.Enabled = true;
                generate.Enabled = false; // FinishRearm enables it after snapshot cleanup.
                undo.Enabled = false;
                details.Enabled = false;
                undo.Text = L("Undo mesh", "Annulla mesh");
                generate.Text = L("Generate", "Genera");
                AcceptButton = generate;
                Application.DoEvents();
            }
        }
        private sealed class WindowOwner : IWin32Window
        {
            public IntPtr Handle { get; private set; }
            public WindowOwner(IntPtr handle) { Handle = handle; }
        }
        private sealed class QualityReportDialog : Form
        {
            protected override bool ProcessCmdKey(ref Message message, Keys keyData)
            {
                if (keyData == Keys.F1) { OpenHelp(this); return true; }
                return base.ProcessCmdKey(ref message, keyData);
            }
            public QualityReportDialog(MeshQualityReport report)
            {
                Text = WindowTitle + L(" - Mesh Quality", " - Qualita della mesh");
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterScreen;
                ClientSize = new Size(780, 650);
                MaximizeBox = false;
                MinimizeBox = false;
                HelpButton = true;
                HelpButtonClicked += delegate(object sender, System.ComponentModel.CancelEventArgs e)
                { e.Cancel = true; OpenHelp(this); };
                ApplyPluginIcon(this);
                ShowInTaskbar = false;
                Font = new Font("Segoe UI", 9);
                BackColor = Color.White;
                ForeColor = UiBody;
                AutoScaleMode = AutoScaleMode.Dpi;
                Controls.Add(CreateDialogHeader(L("Mesh quality", "Qualita della mesh"),
                    L("Element metrics", "Metriche degli elementi"), ClientSize.Width));

                var metrics = new FlowLayoutPanel
                {
                    Left = 16, Top = 88, Width = 748,
                    Height = 445, AutoScroll = true,
                    FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = UiLight,
                    Padding = new Padding(4)
                };
                foreach (QualityMetric metric in report.Metrics.Where(item => item.Name != "Maximum size"))
                    metrics.Controls.Add(new QualityMetricControl(metric) { Width = 718, Height = 84, Margin = new Padding(2, 2, 2, 5) });
                Controls.Add(metrics);

                var close = new Button { Left = 664, Top = 542, Width = 96,
                    Text = L("Close", "Chiudi"), DialogResult = DialogResult.OK };
                StyleButton(close, true);
                Controls.Add(close);
                AddDialogFooter(this, 594, ClientSize.Width);
                AcceptButton = close;
                CancelButton = close;
            }
        }
        private sealed class QualityMetricControl : Control
        {
            private readonly QualityMetric metric;
            public QualityMetricControl(QualityMetric metric)
            {
                this.metric = metric;
                DoubleBuffered = true;
                BackColor = Color.White;
                ForeColor = UiBody;
                Font = new Font("Segoe UI", 8.5f);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                using (var bold = new Font(Font, FontStyle.Bold))
                {
                    using (var bodyBrush = new SolidBrush(UiBody))
                    {
                        e.Graphics.DrawString(LocalizeMessage(metric.Name), bold, bodyBrush, 10, 6);
                        e.Graphics.DrawString(LocalizeMessage(metric.Value), bold, bodyBrush, 365, 6);
                    }
                    string level = LocalizeMessage(metric.Level);
                    SizeF levelSize = e.Graphics.MeasureString(level, bold);
                    e.Graphics.DrawString(level, bold, LevelBrush(metric.Score), Width - levelSize.Width - 12, 6);
                }
                using (var mutedBrush = new SolidBrush(UiMuted)) e.Graphics.DrawString(LocalizeMessage(metric.Reference), Font, mutedBrush, 10, 27);

                int left = 10, top = 48, barWidth = Math.Max(100, Width - 20), barHeight = 13;
                Color[] colors = { Color.FromArgb(196, 55, 55), Color.FromArgb(230, 133, 45), Color.FromArgb(224, 190, 58), Color.FromArgb(55, 155, 92) };
                for (int i = 0; i < colors.Length; i++)
                {
                    int x1 = left + i * barWidth / colors.Length;
                    int x2 = left + (i + 1) * barWidth / colors.Length;
                    using (var brush = new SolidBrush(colors[i])) e.Graphics.FillRectangle(brush, x1, top, x2 - x1, barHeight);
                }
                using (var border = new Pen(UiBorder))
                {
                    e.Graphics.DrawRectangle(border, left, top, barWidth, barHeight);
                    e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
                }
                int marker = left + (int)Math.Round(metric.Score * barWidth);
                using (var pen = new Pen(UiNavy, 2)) e.Graphics.DrawLine(pen, marker, top - 5, marker, top + barHeight + 5);
                Point[] arrow = { new Point(marker - 5, top - 6), new Point(marker + 5, top - 6), new Point(marker, top) };
                using (var navyBrush = new SolidBrush(UiNavy)) e.Graphics.FillPolygon(navyBrush, arrow);
                using (var mutedBrush = new SolidBrush(UiMuted)) e.Graphics.DrawString(L("MINIMUM", "MINIMO"), Font, mutedBrush, left, top + 18);
                string maximum = L("MAXIMUM", "MASSIMO");
                SizeF maximumSize = e.Graphics.MeasureString(maximum, Font);
                using (var mutedBrush = new SolidBrush(UiMuted)) e.Graphics.DrawString(maximum, Font, mutedBrush, left + barWidth - maximumSize.Width, top + 18);
            }
            private static Brush LevelBrush(double score)
            {
                if (score >= 0.75) return Brushes.SeaGreen;
                if (score >= 0.50) return Brushes.DarkGoldenrod;
                if (score >= 0.25) return Brushes.DarkOrange;
                return Brushes.Firebrick;
            }
        }
    }
}
