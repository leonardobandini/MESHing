// Copyright (C) 2026 Leonardo Bandini
// SPDX-License-Identifier: GPL-2.0-or-later
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using Microsoft.Win32;

internal static class SQMPluginInstaller
{
    private const string Guid = "{D64E6254-E9B2-46C8-8C0E-C8A5C78D94AA}";
    private const string ProgId = "SapQuadTransitionPlugin.cPlugin";
    private const string ClassKey = @"Software\Classes\CLSID\" + Guid;
    private const string ProgIdKey = @"Software\Classes\" + ProgId;
    private const string DllName = "SapQuadTransitionPlugin.dll";
    private const string MenuName = "SapQuadTransitionPlugin";
    private const string MenuText = "MESHing";
    private const string VisualCppDownload = "https://aka.ms/vc14/vc_redist.x64.exe";
    private const string VisualCppRegistryKey = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64";
    private static readonly Version MinimumVisualCppVersion = new Version(14, 42, 34226, 3);

    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 1 || args.Length > 3 ||
                  (args[0] != "install" && args[0] != "uninstall" && args[0] != "verify" &&
                    args[0] != "test-install" && args[0] != "test-menu" &&
                    args[0] != "test-menu-bridge" && args[0] != "test-hosts") ||
                 ((args[0] == "test-install" || args[0] == "test-menu" ||
                   args[0] == "test-menu-bridge") && args.Length != 2) ||
                 (args[0] == "test-hosts" && args.Length != 3) ||
                 (args[0] != "test-hosts" && args.Length > 2))
                 throw new ArgumentException("Usage: SQMPluginInstaller.exe install|uninstall|verify [package directory], test-install TEST_ROOT, test-menu[-bridge] TEMP_PROFILE or test-hosts TEMP_INSTALL_ROOT TEMP_PROFILE_ROOT");

            if (args[0] == "test-hosts")
            {
                string installs = Path.GetFullPath(args[1]), profiles = Path.GetFullPath(args[2]);
                string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
                if (!installs.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                    !profiles.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(installs, profiles, StringComparison.OrdinalIgnoreCase) ||
                    !Directory.Exists(installs) || !Directory.Exists(profiles))
                    throw new ArgumentException("Host discovery tests require distinct existing temporary directories.");
                UpdateInstalledHostMenus(installs, profiles);
                return 0;
            }

            if (args[0] == "test-menu" || args[0] == "test-menu-bridge")
            {
                string profile = Path.GetFullPath(args[1]);
                if (!profile.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(profile.TrimEnd('\\'), Path.GetTempPath().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Menu tests require a child folder of the temporary directory.");
                if (args[0] == "test-menu-bridge") UpdateBridgeMenu(profile, 27);
                else UpdateSapMenu(profile, 27);
                return 0;
            }

            string source = Path.GetFullPath(args[0] == "test-install" ? AppDomain.CurrentDomain.BaseDirectory :
                args.Length == 2 ? args[1] : AppDomain.CurrentDomain.BaseDirectory);
            string root = args[0] == "test-install" ? Path.GetFullPath(args[1]) :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SAP2000QuadMesher");
            if (args[0] == "test-install" && (!root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(root.TrimEnd('\\'), Path.GetTempPath().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The test installation must use a child directory of the temporary folder.");
            if (args[0] == "verify") { VerifyPackage(source); return 0; }
            if (args[0] == "test-install") { Install(source, root, false); return 0; }
            Phase("hosts");
            if (Process.GetProcessesByName("SAP2000").Length != 0 ||
                Process.GetProcessesByName("CSiBridge").Length != 0 ||
                Process.GetProcessesByName("ETABS").Length != 0)
                throw new InvalidOperationException("Close SAP2000, CSiBridge and ETABS before installing or uninstalling MESHing. Save any models first if needed.");
            if (args[0] == "install")
            {
                Phase("prerequisite");
                EnsureVisualCppRuntime();
                Phase("prerequisite-ready");
                Install(source, root, true);
                Phase("done");
            }
            else Uninstall(root);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("MESHing: " + error);
            return 1;
        }
    }

    private static void Phase(string name)
    {
        Console.WriteLine("FEMESH_PHASE=" + name);
        Console.Out.Flush();
    }

    private static bool HasRequiredVisualCppVersion(Version version)
    {
        return version != null && version.CompareTo(MinimumVisualCppVersion) >= 0;
    }

    private static Version ReadVisualCppVersion()
    {
        foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using (RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
            using (RegistryKey runtime = machine.OpenSubKey(VisualCppRegistryKey))
            {
                if (runtime == null || Convert.ToInt32(runtime.GetValue("Installed", 0)) != 1) continue;
                Version parsed = ParseVisualCppVersion(Convert.ToString(runtime.GetValue("Version", "")));
                if (parsed != null) return parsed;
                try
                {
                    return new Version(Convert.ToInt32(runtime.GetValue("Major")),
                        Convert.ToInt32(runtime.GetValue("Minor")),
                        Convert.ToInt32(runtime.GetValue("Bld")),
                        Convert.ToInt32(runtime.GetValue("Rbld")));
                }
                catch (Exception error) when (error is ArgumentException || error is FormatException ||
                    error is InvalidCastException || error is OverflowException)
                {
                    continue;
                }
            }
        }
        return null;
    }

    private static bool IsMicrosoftSigned(string file)
    {
        // Windows PowerShell verifies the Authenticode signature AND the publisher's trust chain.
        // X509Certificate.CreateFromSignedFile alone does not verify the file signature.
        string script = "$s=Get-AuthenticodeSignature -LiteralPath '" + file.Replace("'", "''") +
            "'; if ($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch " +
            "'(^|,\\s*)O=Microsoft Corporation(,|$)') { exit 1 }; exit 0";
        string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            @"System32\WindowsPowerShell\v1.0\powershell.exe");
        using (Process check = Process.Start(new ProcessStartInfo(powershell,
            "-NoProfile -NonInteractive -EncodedCommand " + encoded)
        {
            UseShellExecute = false, CreateNoWindow = true
        }))
        {
            if (!check.WaitForExit(30000)) { check.Kill(); return false; }
            return check.ExitCode == 0;
        }
    }

    private static void EnsureVisualCppRuntime()
    {
        if (HasRequiredVisualCppVersion(ReadVisualCppVersion())) return;
        Console.WriteLine("Microsoft Visual C++ Redistributable x64 14.42.34226.3 or later is required.");
        Console.WriteLine("Downloading the separate prerequisite directly from Microsoft: " + VisualCppDownload);
        string directory = Path.Combine(Path.GetTempPath(), "MESHing-vc-redist-" + System.Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory, "vc_redist.x64.exe");
            Phase("download");
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (WebClient download = new WebClient()) download.DownloadFile(VisualCppDownload, file);
            Phase("publisher-check");
            if (!IsMicrosoftSigned(file) || !HasRequiredVisualCppVersion(
                ParseVisualCppVersion(FileVersionInfo.GetVersionInfo(file).FileVersion)))
                throw new InvalidDataException("The downloaded prerequisite is not a trusted Microsoft x64 Redistributable of the required version.");
            Phase("microsoft-setup");
            Console.WriteLine("Starting Microsoft's interactive installer. Complete its license and UAC prompts.");
            using (Process installer = Process.Start(new ProcessStartInfo(file, "/install")
            {
                UseShellExecute = true, Verb = "runas"
            }))
            {
                installer.WaitForExit();
                if (installer.ExitCode == 3010)
                    throw new InvalidOperationException("Microsoft Visual C++ installation requires a restart. Restart Windows, then run the MESHing installer again.");
                if (installer.ExitCode != 0)
                    throw new InvalidOperationException("Microsoft Visual C++ installation was cancelled or failed (code " + installer.ExitCode + ").");
            }
            if (!HasRequiredVisualCppVersion(ReadVisualCppVersion()))
                throw new InvalidOperationException("Microsoft Visual C++ installation finished, but the required x64 version was not detected.");
        }
        catch (Exception error)
        {
            throw new InvalidOperationException("MESHing installation has not started. Install the prerequisite from " +
                VisualCppDownload + " and retry. " + error.Message, error);
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static Version ParseVisualCppVersion(string text)
    {
        Version version;
        return Version.TryParse((text ?? "").TrimStart('v', 'V'), out version) ? version : null;
    }

    private static void VerifyPackage(string source)
    {
        foreach (string file in new[] { DllName,
#if GMSH_V2
            "install_MESHing.cmd", "uninstall_MESHing.cmd",
#else
            "install_plugin.cmd", "uninstall_plugin.cmd",
#endif
            "README.md" })
            if (!File.Exists(Path.Combine(source, file))) throw new FileNotFoundException("Package file missing: " + file);
        foreach (string file in new[] {
#if GMSH_V2
            "MESHingSetup.exe", "LICENSE", "GPL-3.0.txt", "LICENSE-APPLICATION.md", "NATIVE-SOURCES.json",
            "THIRD_PARTY_NOTICES.md", "SOURCE.md", "sap_gmsh_mesher.py",
            "licenses\\GMSH-LICENSE.txt", "licenses\\PYTHON-LICENSE.txt",
            "licenses\\OCCT-LGPL-2.1.txt", "licenses\\OCCT-LGPL-EXCEPTION.txt",
            "licenses\\FLTK-LICENSE.txt", "licenses\\MED-LGPL-3.0.txt", "licenses\\MED-GPL-3.0.txt",
            "licenses\\PETSC-LICENSE.txt", "licenses\\HDF5-LICENSE.txt", "licenses\\HDF5-LBNL-LICENSE.txt",
            "licenses\\CGNS-LICENSE.txt", "licenses\\MMG-LICENSE.txt", "licenses\\MMG-LGPL-3.0.txt",
            "docs\\help-gmsh-v2\\index-it.html", "docs\\help-gmsh-v2\\index-en.html",
            "docs\\help-gmsh-v2\\style.css", "docs\\help-gmsh-v2\\assets\\examples.json",
            "docs\\help-gmsh-v2\\assets\\LogoMESHing.png",
#else
            "docs\\help\\index-it.html", "docs\\help\\index-en.html", "docs\\help\\help.css",
#endif
            "img\\quad-mesher.ico", "img\\quad-mesher-icon.svg" })
            if (!File.Exists(Path.Combine(source, file))) throw new FileNotFoundException("Help or icon missing: " + file);
        string backend = Path.Combine(source, "mesher", "SapQuadMesherBackend.exe");
        if (!File.Exists(backend)) throw new FileNotFoundException("Backend missing: " + backend);
        string version = FileVersionInfo.GetVersionInfo(Path.Combine(source, DllName)).FileVersion;
        if (string.IsNullOrEmpty(version)) throw new InvalidDataException("The plugin has no file version.");
        using (var process = Process.Start(new ProcessStartInfo(backend, "--self-test")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true
        }))
        {
            if (!process.WaitForExit(60000)) { process.Kill(); throw new TimeoutException("Backend self-test timed out."); }
            if (process.ExitCode != 0) throw new InvalidOperationException("Backend self-test failed: " + process.StandardError.ReadToEnd());
        }
        Console.WriteLine("Verified MESHing " + version + " in " + source);
    }

    private static void CopyDirectory(string source, string destination, bool overwrite = false)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            string target = Path.Combine(destination, Path.GetFileName(file));
            try { File.Copy(file, target, overwrite); }
            catch (IOException) when (overwrite && SameFile(file, target))
            {
                // An open documentation viewer may lock a PDF that has not
                // changed. No replacement is necessary in that case.
            }
        }
        foreach (string directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)), overwrite);
    }

    private static bool SameFile(string first, string second)
    {
        if (!File.Exists(second) || new FileInfo(first).Length != new FileInfo(second).Length) return false;
        using (var sha = SHA256.Create())
        using (var input = File.OpenRead(first))
        using (var existing = File.OpenRead(second))
        {
            byte[] left = sha.ComputeHash(input), right = sha.ComputeHash(existing);
            for (int i = 0; i < left.Length; i++) if (left[i] != right[i]) return false;
            return true;
        }
    }

    private static void MoveDirectory(string source, string destination)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { Directory.Move(source, destination); return; }
            catch (Exception error) when ((error is IOException || error is UnauthorizedAccessException) && attempt < 11)
            { Thread.Sleep(250); }
        }
    }

    private static void Install(string source, string root, bool register)
    {
        if (string.Equals(source.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            Phase("verification");
            VerifyPackage(root);
            Phase("already-installed");
            if (register)
            {
                Phase("registration");
                Register(Path.Combine(root, DllName));
                Phase("menus");
                ConfigureHostMenus();
            }
            return;
        }
        Phase("verification");
        VerifyPackage(source);
        string parent = Path.GetDirectoryName(root);
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, "SAP2000QuadMesher.new-" + System.Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(parent, "SAP2000QuadMesher.previous-" + System.Guid.NewGuid().ToString("N"));
        string oldCodeBase = register ? RegisteredCodeBase() : null;
        bool movedOld = false, movedNew = false, copiedOverOld = false;
        try
        {
            Phase("copy");
            CopyDirectory(source, staging);
            VerifyPackage(staging);
            Phase(Directory.Exists(root) ? "previous-version" : "previous-none");
            if (Directory.Exists(root))
            {
                try { MoveDirectory(root, backup); movedOld = true; }
                catch (IOException)
                {
                    // A process can hold the old directory handle even after
                    // SAP2000 exits. Retain a verified rollback copy, then
                    // replace individual files without renaming that handle.
                    CopyDirectory(root, backup);
                    copiedOverOld = true;
                }
            }
            if (copiedOverOld) { CopyDirectory(staging, root, true); VerifyPackage(root); }
            else { MoveDirectory(staging, root); movedNew = true; }
            if (register)
            {
                Phase("registration");
                Register(Path.Combine(root, DllName));
            }
        }
        catch
        {
            if (movedNew) Directory.Delete(root, true);
            if (movedOld) MoveDirectory(backup, root);
            if (copiedOverOld) CopyDirectory(backup, root, true);
            if (register)
            {
                Unregister();
                if (!string.IsNullOrEmpty(oldCodeBase))
                {
                    Uri oldUri;
                    if (Uri.TryCreate(oldCodeBase, UriKind.Absolute, out oldUri) && oldUri.IsFile && File.Exists(oldUri.LocalPath))
                        Register(oldUri.LocalPath);
                }
            }
            throw;
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, true); }

        if (movedOld || copiedOverOld)
        {
            try { Directory.Delete(backup, true); }
            catch (IOException) { Console.WriteLine("Previous installation backup remains in " + backup); }
        }
        if (register)
        {
            Phase("menus");
            ConfigureHostMenus();
            string legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "SQM_Plugin");
            if (Directory.Exists(legacy) && string.Equals(oldCodeBase,
                new Uri(Path.Combine(legacy, DllName)).AbsoluteUri, StringComparison.OrdinalIgnoreCase))
            {
                try { Directory.Delete(legacy, true); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
                { Console.WriteLine("Previous installation remains in " + legacy); }
            }
        }
        Console.WriteLine("MESHing installed in " + root);
        Console.WriteLine("Restart SAP2000 and/or CSiBridge to use the new version.");
    }

    private static void ConfigureHostMenus()
    {
        bool sapMenuAvailable = UpdateInstalledHostMenus(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Computers and Structures"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Computers and Structures"));
        // ETABS is deliberately excluded from the distributed installer.
#if GMSH_V2
        // Retire the known development preview only after the SAP Tools entry is usable.
        if (sapMenuAvailable)
        {
            try { RetirePreviewRegistration(); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            { Console.WriteLine("Previous V2 preview cleanup requires attention: " + error.Message); }
        }
#endif
    }

    private static bool UpdateInstalledHostMenus(string installationRoot, string profileRoot)
    {
        if (!Directory.Exists(installationRoot)) return false;
        bool sapMenuAvailable = false;
        foreach (string directory in Directory.GetDirectories(installationRoot).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            Match host = Regex.Match(Path.GetFileName(directory), @"^(SAP2000|CSiBridge) (?<version>\d+)$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            int version;
            if (!host.Success || !int.TryParse(host.Groups["version"].Value, out version) || version < 27) continue;
            string product = host.Groups[1].Value.Equals("SAP2000", StringComparison.OrdinalIgnoreCase)
                ? "SAP2000" : "CSiBridge";
            if (!File.Exists(Path.Combine(directory, product + ".exe")) ||
                !File.Exists(Path.Combine(directory, "CSiAPIv1.dll"))) continue;
            string profile = Path.Combine(profileRoot, product + " " + version);
            try
            {
                bool available = product == "SAP2000" ? UpdateSapMenu(profile, version) :
                    UpdateBridgeMenu(profile, version);
                if (product == "SAP2000" && available) sapMenuAvailable = true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException ||
                                          error is InvalidDataException || error is XmlException)
            {
                Console.WriteLine(product + " " + version + " MESHing menu could not be updated automatically: " + error.Message);
                Console.WriteLine("In " + product + " use Tools > Add/Show Plugins to add " + MenuName + " manually.");
            }
        }
        return sapMenuAvailable;
    }


    private static string AddIniMenuEntry(string contents, out bool changed)
    {
        Match section = Regex.Match(contents, @"(?ms)^\[PlugIn\]\r?\n.*?(?=^\[|\z)");
        if (!section.Success) throw new InvalidDataException("The SAP2000 INI has no [PlugIn] section.");
        Match number = Regex.Match(section.Value, @"(?m)^(?<prefix>[ \t]*NumberPlugIns=)(?<count>\d+)[ \t]*\r?$");
        if (!number.Success) throw new InvalidDataException("SAP2000 plugin count missing from the INI.");
        MatchCollection names = Regex.Matches(section.Value, @"(?m)^[ \t]*PlugInName=(?<name>[^\r\n]*)");
        MatchCollection labels = Regex.Matches(section.Value, @"(?m)^[ \t]*PlugInMenuText=[^\r\n]*");
        int count = int.Parse(number.Groups["count"].Value);
        if (count != names.Count || count != labels.Count)
            throw new InvalidDataException("The SAP2000 plugin INI has inconsistent name, label or count records.");
#if GMSH_V2
        for (int i = names.Count - 1; i >= 0; i--)
        {
            if (!string.Equals(names[i].Groups["name"].Value.Trim(), "SapGmshPlugin", StringComparison.OrdinalIgnoreCase)) continue;
            string eol = section.Value.Contains("\r\n") ? "\r\n" : "\n";
            string local = section.Value;
            foreach (Match match in new[] { names[i], labels[i] }.OrderByDescending(match => match.Index))
            {
                int end = match.Index + match.Length;
                if (end < local.Length && local.Substring(end).StartsWith(eol, StringComparison.Ordinal)) end += eol.Length;
                local = local.Remove(match.Index, end - match.Index);
            }
            Match numberLine = Regex.Match(local, @"(?m)^(?<prefix>[ \t]*NumberPlugIns=)(?<count>\d+)[ \t]*\r?$");
            local = local.Substring(0, numberLine.Groups["count"].Index) + (count - 1) +
                local.Substring(numberLine.Groups["count"].Index + numberLine.Groups["count"].Length);
            contents = contents.Substring(0, section.Index) + local + contents.Substring(section.Index + section.Length);
            string rest = AddIniMenuEntry(contents, out changed);
            changed = true;
            return rest;
        }
#endif
        for (int index = 0; index < names.Count; index++)
        {
            if (!string.Equals(names[index].Groups["name"].Value.Trim(), MenuName,
                StringComparison.OrdinalIgnoreCase)) continue;
            Match label = labels[index];
            int separator = label.Value.IndexOf('=');
            string replacement = label.Value.Substring(0, separator + 1) + MenuText;
            changed = !string.Equals(label.Value, replacement, StringComparison.Ordinal);
            return changed ? contents.Substring(0, section.Index + label.Index) + replacement +
                contents.Substring(section.Index + label.Index + label.Length) : contents;
        }

        string lineEnd = section.Value.Contains("\r\n") ? "\r\n" : "\n";
        string updated = section.Value.Substring(0, number.Groups["count"].Index) + (count + 1) +
            section.Value.Substring(number.Groups["count"].Index + number.Groups["count"].Length);
        updated = updated.TrimEnd('\r', '\n') + lineEnd + " PlugInName=" + MenuName +
            lineEnd + " PlugInMenuText=" + MenuText + lineEnd + lineEnd;
        changed = true;
        return contents.Substring(0, section.Index) + updated + contents.Substring(section.Index + section.Length);
    }

    private static byte[] UpdatedSettings(byte[] original, out bool changed)
    {
        var settings = new XmlDocument { PreserveWhitespace = true };
        using (var stream = new MemoryStream(original)) settings.Load(stream);
        XmlNode menu = settings.SelectSingleNode("/Settings/PlugIn");
        if (menu == null) throw new InvalidDataException("SAP2000 settings have no PlugIn field.");
        var entries = new XmlDocument { PreserveWhitespace = true };
        entries.LoadXml(menu.InnerText);
        XmlNode root = entries.SelectSingleNode("/ArrayOfString");
        if (root == null) throw new InvalidDataException("SAP2000 plugin settings are not an ArrayOfString.");
        changed = false;
#if GMSH_V2
        foreach (XmlNode item in root.SelectNodes("string"))
        {
            var preview = new XmlDocument();
            preview.LoadXml(item.InnerText.TrimStart('\ufeff', ' ', '\r', '\n'));
            XmlNode name = preview.SelectSingleNode("/PlugIn/m_name");
            if (name != null && string.Equals(name.InnerText, "SapGmshPlugin", StringComparison.OrdinalIgnoreCase))
            {
                root.RemoveChild(item);
                changed = true;
            }
        }
#endif
        foreach (XmlNode item in root.SelectNodes("string"))
        {
            var record = new XmlDocument();
            string contents = item.InnerText;
            record.LoadXml(contents.TrimStart('\ufeff', ' ', '\r', '\n'));
            XmlNode name = record.SelectSingleNode("/PlugIn/m_name");
            if (name == null) throw new InvalidDataException("SAP2000 plugin entry has no m_name.");
            if (!string.Equals(name.InnerText, MenuName, StringComparison.OrdinalIgnoreCase)) continue;
            XmlNode hidden = record.SelectSingleNode("/PlugIn/m_Hide");
            if (hidden == null)
            {
                hidden = record.CreateElement("m_Hide");
                record.DocumentElement.AppendChild(hidden);
            }
            XmlNode caption = record.SelectSingleNode("/PlugIn/m_Text");
            if (caption == null)
            {
                caption = record.CreateElement("m_Text");
                record.DocumentElement.AppendChild(caption);
            }
            if (caption.InnerText != MenuText)
            {
                caption.InnerText = MenuText;
                changed = true;
            }
            if (hidden.InnerText != "false")
            {
                hidden.InnerText = "false";
                changed = true;
            }
            if (changed) item.InnerText = (contents.Length > 0 && contents[0] == '\ufeff' ? "\ufeff" : "") + record.OuterXml;
            return SerializeSettings(settings, menu, entries, original, changed);
        }

        XmlElement newItem = entries.CreateElement("string");
        newItem.InnerText = "\ufeff<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<PlugIn><ClassID>S_PlugIn</ClassID><ClassVersion>2</ClassVersion>" +
            "<m_name>" + MenuName + "</m_name><m_Path /><m_Text>" + MenuText +
            "</m_Text><m_found>false</m_found><m_Info /><m_Hide>false</m_Hide></PlugIn>";
        root.AppendChild(newItem);
        changed = true;
        return SerializeSettings(settings, menu, entries, original, changed);
    }

    private static byte[] SerializeSettings(XmlDocument settings, XmlNode menu, XmlDocument entries,
        byte[] original, bool changed)
    {
        if (!changed) return original;
        menu.InnerText = entries.OuterXml;
        using (var stream = new MemoryStream())
        {
            settings.Save(stream);
            return stream.ToArray();
        }
    }

    private static bool UpdateSapMenu(string profile, int version)
    {
        return UpdateHostMenu(profile, "SAP2000v" + version + ".ini",
            "SAP2000v" + version + ".settings", "SAP2000 " + version);
    }

    private static bool UpdateBridgeMenu(string profile, int version)
    {
        return UpdateHostMenu(profile, "CSiBridgev" + version + ".ini",
            "CSiBridgev" + version + ".settings", "CSiBridge " + version);
    }

    private static bool UpdateHostMenu(string profile, string iniFile, string settingsFile, string host)
    {
        string ini = Path.Combine(profile, iniFile);
        string settings = Path.Combine(profile, settingsFile);
        if (!File.Exists(ini) || !File.Exists(settings))
        {
            Console.WriteLine(host + " menu preferences are not initialized. Start and close " + host +
                " once, then rerun the installer to add the menu entry.");
            return false;
        }
        byte[] iniOriginal = File.ReadAllBytes(ini);
        byte[] settingsOriginal = File.ReadAllBytes(settings);
        bool utf8Bom = iniOriginal.Length >= 3 && iniOriginal[0] == 0xef && iniOriginal[1] == 0xbb && iniOriginal[2] == 0xbf;
        Encoding iniEncoding = utf8Bom ? new UTF8Encoding(true) : Encoding.Default;
        string iniText = iniEncoding.GetString(iniOriginal);
        bool iniChanged, settingsChanged;
        string iniUpdated = AddIniMenuEntry(iniText, out iniChanged);
        byte[] settingsUpdated = UpdatedSettings(settingsOriginal, out settingsChanged);
        if (!iniChanged && !settingsChanged)
        {
            Console.WriteLine(host + " Tools plugin menu already contains " + MenuName + " (" + MenuText + ").");
            return true;
        }

        string token = System.Guid.NewGuid().ToString("N");
        string iniNew = ini + ".sqm-new-" + token, settingsNew = settings + ".sqm-new-" + token;
        string iniBackup = ini + ".sqm-backup-" + token, settingsBackup = settings + ".sqm-backup-" + token;
        bool iniReplaced = false, settingsReplaced = false, restored = true;
        try
        {
            if (iniChanged)
            {
                File.WriteAllBytes(iniNew, iniEncoding.GetBytes(iniUpdated));
                File.Replace(iniNew, ini, iniBackup);
                iniReplaced = true;
            }
            if (settingsChanged)
            {
                File.WriteAllBytes(settingsNew, settingsUpdated);
                File.Replace(settingsNew, settings, settingsBackup);
                settingsReplaced = true;
            }
            Console.WriteLine(host + " Tools plugin menu updated: " + MenuName + " (" + MenuText + ").");
        }
        catch (Exception failure)
        {
            try
            {
                if (settingsReplaced) File.Copy(settingsBackup, settings, true);
                if (iniReplaced) File.Copy(iniBackup, ini, true);
            }
            catch (Exception rollback)
            {
                restored = false;
                throw new IOException("SAP2000 menu rollback failed; original copies remain in " + profile +
                    ": " + rollback.Message, failure);
            }
            throw;
        }
        finally
        {
            foreach (string path in new[] { iniNew, settingsNew })
                if (File.Exists(path)) try { File.Delete(path); } catch { }
            if (restored)
                foreach (string path in new[] { iniBackup, settingsBackup })
                    if (File.Exists(path)) try { File.Delete(path); } catch { }
        }
        return true;
    }

    private static string RegisteredCodeBase()
    {
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ClassKey + @"\InprocServer32"))
            return key == null ? null : key.GetValue("CodeBase") as string;
    }

    private static void Register(string dll)
    {
        AssemblyName assembly = AssemblyName.GetAssemblyName(dll);
        string codeBase = new Uri(dll).AbsoluteUri;
        using (RegistryKey progId = Registry.CurrentUser.CreateSubKey(ProgIdKey))
            progId.SetValue(null, ProgId);
        using (RegistryKey progIdClass = Registry.CurrentUser.CreateSubKey(ProgIdKey + @"\CLSID"))
            progIdClass.SetValue(null, Guid);
        using (RegistryKey clsid = Registry.CurrentUser.CreateSubKey(ClassKey))
            clsid.SetValue(null, ProgId);
        using (RegistryKey inproc = Registry.CurrentUser.CreateSubKey(ClassKey + @"\InprocServer32"))
        {
            inproc.SetValue(null, "mscoree.dll");
            inproc.SetValue("ThreadingModel", "Both");
            SetAssemblyValues(inproc, assembly.FullName, codeBase);
        }
        using (RegistryKey version = Registry.CurrentUser.CreateSubKey(ClassKey + @"\InprocServer32\" + assembly.Version))
            SetAssemblyValues(version, assembly.FullName, codeBase);
        using (RegistryKey progId = Registry.CurrentUser.CreateSubKey(ClassKey + @"\ProgId"))
            progId.SetValue(null, ProgId);
        using (Registry.CurrentUser.CreateSubKey(ClassKey + @"\Implemented Categories\{62C8FE65-4EBB-45e7-b440-6e39b2cdbf29}")) { }
    }

    private static void SetAssemblyValues(RegistryKey key, string assembly, string codeBase)
    {
        key.SetValue("Class", ProgId);
        key.SetValue("Assembly", assembly);
        key.SetValue("RuntimeVersion", "v4.0.30319");
        key.SetValue("CodeBase", codeBase);
    }

    private static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(ClassKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(ProgIdKey, false);
    }

#if GMSH_V2
    private static void RetirePreviewRegistration()
    {
        const string previewGuid = "{724078D7-35A4-4D91-A181-2B55F42FD39B}";
        string previewRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "SAP2000GmshMesh");
        string expected = new Uri(Path.Combine(previewRoot, "SapGmshPlugin.dll")).AbsoluteUri;
        string keyPath = @"Software\Classes\CLSID\" + previewGuid;
        using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath + @"\InprocServer32"))
        {
            if (key == null || !string.Equals(key.GetValue("CodeBase") as string, expected,
                StringComparison.OrdinalIgnoreCase)) return;
        }
        Registry.CurrentUser.DeleteSubKeyTree(keyPath, false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\SapGmshPlugin.cPlugin", false);
        try { if (Directory.Exists(previewRoot)) Directory.Delete(previewRoot, true); }
        catch (IOException) { Console.WriteLine("Old V2 preview directory remains in " + previewRoot); }
    }
#endif

    private static void Uninstall(string root)
    {
        string installedCodeBase = new Uri(Path.Combine(root, DllName)).AbsoluteUri;
        string activeCodeBase = RegisteredCodeBase();
        if (!string.IsNullOrEmpty(activeCodeBase) &&
            !string.Equals(activeCodeBase, installedCodeBase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A different plugin installation is registered; nothing was removed.");
        if (string.Equals(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\'),
            Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run the uninstaller from the extracted ZIP, outside the installation folder.");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        if (!string.IsNullOrEmpty(activeCodeBase)) Unregister();
        Console.WriteLine("SQM Plugin removed for the current user.");
    }
}
