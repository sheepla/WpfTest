using System.Reflection.PortableExecutable;
using WpfInspector.Core.Models;

namespace WpfInspector.Core.Services;

public sealed class PeMetadataService : IPeMetadataService
{
    public PeSymbolInfo? TryReadDebugInfo(string modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath) || !File.Exists(modulePath))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(modulePath);
            using var peReader = new PEReader(stream, PEStreamOptions.Default);

            var architecture = peReader.PEHeaders.PEHeader?.Magic switch
            {
                PEMagic.PE32Plus => "x64",
                PEMagic.PE32 => "x86",
                _ => null
            };

            var entries = peReader.ReadDebugDirectory();
            if (entries.IsEmpty)
            {
                return new PeSymbolInfo(
                    OriginalPdbPath: null,
                    SuggestedPdbPath: null,
                    SuggestedPdbExists: false,
                    Signature: null,
                    Age: null,
                    HasEmbeddedPortablePdb: false,
                    SymbolState: "Debug Directory なし",
                    Architecture: architecture);
            }

            string? originalPdbPath = null;
            string? suggestedPdbPath = null;
            Guid? signature = null;
            int? age = null;
            var hasEmbeddedPortablePdb = false;

            foreach (var entry in entries)
            {
                if (entry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
                {
                    hasEmbeddedPortablePdb = true;
                    continue;
                }

                if (entry.Type != DebugDirectoryEntryType.CodeView)
                {
                    continue;
                }

                try
                {
                    var codeViewData = peReader.ReadCodeViewDebugDirectoryData(entry);
                    originalPdbPath = codeViewData.Path;
                    signature = codeViewData.Guid;
                    age = codeViewData.Age;

                    var pdbFileName = Path.GetFileName(codeViewData.Path);
                    if (!string.IsNullOrWhiteSpace(pdbFileName))
                    {
                        suggestedPdbPath = Path.Combine(Path.GetDirectoryName(modulePath) ?? string.Empty, pdbFileName);
                    }
                }
                catch
                {
                    // 壊れた CodeView エントリは無視して観測だけ継続する。
                }
            }

            var suggestedExists = !string.IsNullOrWhiteSpace(suggestedPdbPath) && File.Exists(suggestedPdbPath);
            var symbolState = ResolveSymbolState(hasEmbeddedPortablePdb, originalPdbPath, suggestedExists);

            return new PeSymbolInfo(
                OriginalPdbPath: originalPdbPath,
                SuggestedPdbPath: suggestedPdbPath,
                SuggestedPdbExists: suggestedExists,
                Signature: signature,
                Age: age,
                HasEmbeddedPortablePdb: hasEmbeddedPortablePdb,
                SymbolState: symbolState,
                Architecture: architecture);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveSymbolState(bool hasEmbeddedPortablePdb, string? originalPdbPath, bool suggestedExists)
    {
        if (hasEmbeddedPortablePdb && suggestedExists)
        {
            return "Embedded + ローカル PDB";
        }

        if (hasEmbeddedPortablePdb)
        {
            return "Embedded Portable PDB";
        }

        if (suggestedExists)
        {
            return "ローカル PDB 検出";
        }

        if (!string.IsNullOrWhiteSpace(originalPdbPath))
        {
            return "PDB 参照あり";
        }

        return "シンボル参照なし";
    }
}

