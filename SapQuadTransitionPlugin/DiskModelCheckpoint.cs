// Copyright (C) 2026 Leonardo Bandini
// SPDX-License-Identifier: GPL-2.0-or-later
// Additional permission for the separately installed SAP2000 API:
// see SAP2000-LINKING-EXCEPTION.md.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using CSiAPIv1;

namespace SapQuadTransitionPlugin
{
    // Experimental FAST rollback: SAP reloads the very SDB/BDB it saved before
    // generation. A private copy protects that state if the original file changes
    // while the preview is open; it is never substituted silently for another model.
    internal sealed class DiskModelCheckpoint
    {
        private readonly string modelPath, privatePath;
        private readonly byte[] digest;
        private readonly HashSet<string> areaNames;
        private bool restored;

        private DiskModelCheckpoint(string modelPath, string privatePath, byte[] digest,
            HashSet<string> areaNames)
        {
            this.modelPath = modelPath;
            this.privatePath = privatePath;
            this.digest = digest;
            this.areaNames = areaNames;
        }

        internal string Path { get { return privatePath; } }
        internal bool Restored { get { return restored; } }

        private static byte[] Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var input = File.OpenRead(path)) return sha.ComputeHash(input);
        }

        private static void Trace(string phase, Stopwatch clock)
        {
            string text = "disk-undo-phase=" + phase + " seconds=" +
                clock.Elapsed.TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
            Console.WriteLine(text);
            string path = Environment.GetEnvironmentVariable("SAP_QUAD_UNDO_TRACE");
            if (!string.IsNullOrEmpty(path)) File.AppendAllText(path, text + Environment.NewLine);
        }

        internal static DiskModelCheckpoint Capture(cSapModel model, string modelPath)
        {
            int count = 0;
            string[] names = null;
            if (model.AreaObj.GetNameList(ref count, ref names) != 0 || names == null || count != names.Length)
                throw new InvalidDataException("The pre-mesh area inventory cannot be verified.");
            var expected = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
            if (expected.Count != count) throw new InvalidDataException("Duplicate pre-mesh area names.");
            byte[] hash = Hash(modelPath);
            string directory = System.IO.Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "SAP2000QuadMesher", "snapshots");
            Directory.CreateDirectory(directory);
            string copy = System.IO.Path.Combine(directory, "premesh-" + Guid.NewGuid().ToString("N") +
                System.IO.Path.GetExtension(modelPath));
            try
            {
                File.Copy(modelPath, copy, false);
                if (!hash.SequenceEqual(Hash(copy)))
                    throw new InvalidDataException("The private pre-mesh model copy differs from the saved SDB/BDB.");
                return new DiskModelCheckpoint(modelPath, copy, hash, expected);
            }
            catch
            {
                try { File.Delete(copy); } catch (IOException) { }
                throw;
            }
        }

        internal void Restore(cSapModel model)
        {
            var clock = Stopwatch.StartNew();
            if (restored) throw new InvalidOperationException("The pre-mesh model has already been restored.");
            if (!File.Exists(modelPath) || !digest.SequenceEqual(Hash(modelPath)))
                throw new InvalidDataException("The saved pre-mesh model changed on disk. " +
                    "Undo will not overwrite it; the protected original remains at " + privatePath);
            Trace("verify-saved-file", clock);
            clock.Restart();
            int code = model.File.OpenFile(modelPath);
            Trace("open-saved-model", clock);
            clock.Restart();
            if (code != 0 || !string.Equals(model.GetModelFilename(true), modelPath,
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SAP2000 could not reopen the saved pre-mesh model: " + code +
                    ". Protected copy: " + privatePath);
            int count = 0;
            string[] names = null;
            if (model.AreaObj.GetNameList(ref count, ref names) != 0 || names == null ||
                count != areaNames.Count || !areaNames.SetEquals(names))
                throw new InvalidDataException("Reopening the pre-mesh SDB/BDB did not restore every original area. " +
                    "Protected copy: " + privatePath);
            Trace("verify-restored-areas", clock);
            restored = true;
        }

        internal void Delete()
        {
            if (File.Exists(privatePath)) File.Delete(privatePath);
        }
    }
}
