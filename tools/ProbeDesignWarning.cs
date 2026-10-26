// SPDX-License-Identifier: GPL-2.0-or-later
// Read-only probe for the known unreadable design-overwrite table and warnings.
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using CSiAPIv1;

internal static class ProbeDesignWarning
{
    private static int Main(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[0], out int pid))
            throw new ArgumentException("Usage: PID TEMP_COPY.sdb V2_DLL");
        string path = Path.GetFullPath(args[1]);
        string temp = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "opencode")) + Path.DirectorySeparatorChar;
        Process process = Process.GetProcessById(pid);
        if (process.ProcessName != "SAP2000" ||
            process.MainWindowTitle.IndexOf("Circular_Floor_Beta_Copy",StringComparison.OrdinalIgnoreCase)<0 ||
            !path.StartsWith(temp,StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
            throw new InvalidOperationException("Read-only probe requires its isolated SAP copy.");
        cHelper helper = new Helper();
        cSapModel model = helper.GetObjectProcess("CSI.SAP2000.API.SapObject",pid).SapModel;
        if (!string.Equals(Path.GetFullPath(model.GetModelFilename(true)),path,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Attached model mismatch.");
        Type snapshot = Assembly.LoadFrom(Path.GetFullPath(args[2]))
            .GetType("SapQuadTransitionPlugin.ModelCsvSnapshot",true);
        const string key="Overwrites - Concrete Shell Design - Eurocode 2-2004";
        var read = snapshot.GetMethod("Read",BindingFlags.NonPublic|BindingFlags.Static);
        object table=read.Invoke(null,new object[]{model,key,true});
        Console.WriteLine("UNREADABLE_DESIGN_TABLE_SKIPPED="+(table==null));
        if(table!=null)throw new InvalidOperationException("Known unreadable table unexpectedly readable.");
        Type plugin = snapshot.Assembly.GetType("SapQuadTransitionPlugin.cPlugin",true);
        var warning = plugin.GetMethod("DesignOverwriteWarning",BindingFlags.NonPublic|BindingFlags.Static);
        string[] keys={key};
        string without=(string)warning.Invoke(null,new object[]{keys,null});
        string backup=Path.Combine(Path.GetDirectoryName(path),"Circular_Floor_Beta_Copy.quadmesher-backup-0001.sdb");
        string with=(string)warning.Invoke(null,new object[]{keys,backup});
        Console.WriteLine("WARNING_NO_BACKUP="+without);
        Console.WriteLine("WARNING_WITH_BACKUP="+with);
        if(!without.Contains(key)||!with.Contains(key)||!with.Contains(backup)||
            !(without.Contains("OFF")||without.Contains("DISATTIVATA")))
            throw new InvalidOperationException("The warning does not distinguish backup states.");
        Console.WriteLine("DESIGN_WARNING_VARIANTS=PASS");
        return 0;
    }
}
