using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;
using WpfInspector.Core.Models;

namespace WpfInspector.Core.Services;

public sealed class PortablePdbAnalysisService : IPdbAnalysisService
{
    public PdbAnalysisResult Analyze(string pdbPath, string? assemblyPath = null)
    {
        if (string.IsNullOrWhiteSpace(pdbPath))
        {
            throw new ArgumentException("PDB パスが未指定です。", nameof(pdbPath));
        }

        if (!File.Exists(pdbPath))
        {
            throw new FileNotFoundException("PDB ファイルが見つかりません。", pdbPath);
        }

        using var pdbStream = File.OpenRead(pdbPath);
        if (!IsPortablePdb(pdbStream))
        {
            if (IsWindowsPdb(pdbStream))
            {
                throw new NotSupportedException("Classic Windows PDB は未対応です。Portable PDB を指定してください。");
            }

            throw new InvalidDataException("認識できない PDB 形式です。");
        }

        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream, MetadataStreamOptions.LeaveOpen);
        var pdbReader = provider.GetMetadataReader();
        var warnings = new List<string>();
        var methodDisplayMap = string.IsNullOrWhiteSpace(assemblyPath)
            ? CreateFallbackMethodMap(warnings)
            : TryBuildMethodDisplayMap(assemblyPath!, warnings);

        var documentLookup = BuildDocumentLookup(pdbReader);
        var documents = new List<PdbDocumentInfo>(documentLookup.Count);
        foreach (var documentHandle in pdbReader.Documents)
        {
            var document = pdbReader.GetDocument(documentHandle);
            documents.Add(new PdbDocumentInfo(
                Name: GetDocumentName(pdbReader, document.Name),
                HashAlgorithm: MapHashAlgorithm(pdbReader.GetGuid(document.HashAlgorithm)),
                HashLength: pdbReader.GetBlobBytes(document.Hash).Length,
                Language: MapLanguage(pdbReader.GetGuid(document.Language))));
        }

        var methods = new List<PdbMethodInfo>();
        var totalSequencePoints = 0;
        var totalVisibleSequencePoints = 0;

        foreach (var methodDebugHandle in pdbReader.MethodDebugInformation)
        {
            var rowNumber = MetadataTokens.GetRowNumber(methodDebugHandle);
            if (rowNumber == 0)
            {
                continue;
            }

            var debugInformation = pdbReader.GetMethodDebugInformation(methodDebugHandle);
            var methodHandle = MetadataTokens.MethodDefinitionHandle(rowNumber);
            var token = MetadataTokens.GetToken(methodHandle);

            var currentDocument = debugInformation.Document;
            var primaryDocument = currentDocument.IsNil ? null : ResolveDocumentName(documentLookup, currentDocument);
            var sequencePointCount = 0;
            var visibleSequencePointCount = 0;

            foreach (var sequencePoint in debugInformation.GetSequencePoints())
            {
                if (!sequencePoint.Document.IsNil)
                {
                    currentDocument = sequencePoint.Document;
                }

                if (primaryDocument is null && !currentDocument.IsNil)
                {
                    primaryDocument = ResolveDocumentName(documentLookup, currentDocument);
                }

                sequencePointCount++;
                if (!sequencePoint.IsHidden)
                {
                    visibleSequencePointCount++;
                }
            }

            if (sequencePointCount == 0)
            {
                continue;
            }

            totalSequencePoints += sequencePointCount;
            totalVisibleSequencePoints += visibleSequencePointCount;

            methods.Add(new PdbMethodInfo(
                Token: token,
                DisplayName: methodDisplayMap.TryGetValue(token, out var displayName)
                    ? displayName
                    : $"0x{token:X8}",
                Document: primaryDocument,
                SequencePointCount: sequencePointCount,
                VisibleSequencePointCount: visibleSequencePointCount));
        }

        return new PdbAnalysisResult(
            PdbPath: pdbPath,
            AssemblyPath: assemblyPath,
            Format: "Portable PDB",
            DocumentCount: documents.Count,
            MethodCount: methods.Count,
            SequencePointCount: totalSequencePoints,
            VisibleSequencePointCount: totalVisibleSequencePoints,
            Documents: documents
                .OrderBy(document => document.Name, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Methods: methods
                .OrderByDescending(method => method.VisibleSequencePointCount)
                .ThenBy(method => method.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Warnings: warnings);
    }

    private static Dictionary<int, string> CreateFallbackMethodMap(List<string> warnings)
    {
        warnings.Add("アセンブリ未指定のため、メソッド名はトークン中心で表示します。");
        return new Dictionary<int, string>();
    }

    private static Dictionary<int, string> TryBuildMethodDisplayMap(string assemblyPath, List<string> warnings)
    {
        if (!File.Exists(assemblyPath))
        {
            warnings.Add("アセンブリが見つからないため、メソッド名を解決できませんでした。");
            return new Dictionary<int, string>();
        }

        try
        {
            using var assemblyStream = File.OpenRead(assemblyPath);
            using var peReader = new PEReader(assemblyStream, PEStreamOptions.Default);
            if (!peReader.HasMetadata)
            {
                warnings.Add("対象ファイルに .NET メタデータがないため、メソッド名を解決できませんでした。");
                return new Dictionary<int, string>();
            }

            return BuildMethodDisplayMap(peReader.GetMetadataReader());
        }
        catch
        {
            warnings.Add("アセンブリのメタデータ読み込みに失敗したため、メソッド名を解決できませんでした。");
            return new Dictionary<int, string>();
        }
    }

    private static Dictionary<DocumentHandle, string> BuildDocumentLookup(MetadataReader pdbReader)
    {
        var lookup = new Dictionary<DocumentHandle, string>();
        foreach (var documentHandle in pdbReader.Documents)
        {
            lookup[documentHandle] = GetDocumentName(pdbReader, pdbReader.GetDocument(documentHandle).Name);
        }

        return lookup;
    }

    private static string? ResolveDocumentName(IReadOnlyDictionary<DocumentHandle, string> documentLookup, DocumentHandle handle)
    {
        return documentLookup.TryGetValue(handle, out var documentName) ? documentName : null;
    }

    private static Dictionary<int, string> BuildMethodDisplayMap(MetadataReader assemblyReader)
    {
        var methodDisplayMap = new Dictionary<int, string>();
        foreach (var typeHandle in assemblyReader.TypeDefinitions)
        {
            var typeDefinition = assemblyReader.GetTypeDefinition(typeHandle);
            var typeName = GetTypeName(assemblyReader, typeDefinition);
            foreach (var methodHandle in typeDefinition.GetMethods())
            {
                var methodDefinition = assemblyReader.GetMethodDefinition(methodHandle);
                var methodName = assemblyReader.GetString(methodDefinition.Name);
                methodDisplayMap[MetadataTokens.GetToken(methodHandle)] = $"{typeName}.{methodName}";
            }
        }

        return methodDisplayMap;
    }

    private static string GetTypeName(MetadataReader assemblyReader, TypeDefinition typeDefinition)
    {
        var @namespace = assemblyReader.GetString(typeDefinition.Namespace);
        var name = assemblyReader.GetString(typeDefinition.Name);
        return string.IsNullOrWhiteSpace(@namespace) ? name : $"{@namespace}.{name}";
    }

    private static string GetDocumentName(MetadataReader reader, DocumentNameBlobHandle handle)
    {
        var blobReader = reader.GetBlobReader(handle);
        if (blobReader.RemainingBytes == 0)
        {
            return string.Empty;
        }

        var separator = (char)blobReader.ReadByte();
        var parts = new List<string>();
        while (blobReader.RemainingBytes > 0)
        {
            var partHandle = MetadataTokens.BlobHandle(blobReader.ReadCompressedInteger());
            parts.Add(Encoding.UTF8.GetString(reader.GetBlobBytes(partHandle)));
        }

        return string.Join(separator, parts);
    }

    private static string MapHashAlgorithm(Guid handle)
    {
        if (handle == new Guid("ff1816ec-aa5e-4d10-87f7-6f4963833460"))
        {
            return "SHA-1";
        }

        if (handle == new Guid("8829d00f-11b8-4213-878b-770e8597ac16"))
        {
            return "SHA-256";
        }

        return handle == Guid.Empty ? "なし" : handle.ToString("D");
    }

    private static string MapLanguage(Guid handle)
    {
        if (handle == new Guid("3f5162f8-07c6-11d3-9053-00c04fa302a1"))
        {
            return "C#";
        }

        if (handle == new Guid("3a12d0b8-c26c-11d0-b442-00a0244a1dd2"))
        {
            return "Visual Basic";
        }

        if (handle == new Guid("ab4f38c9-b6e6-43ba-be3b-58080b2ccce3"))
        {
            return "F#";
        }

        return handle == Guid.Empty ? "不明" : handle.ToString("D");
    }

    private static bool IsPortablePdb(Stream stream)
    {
        Span<byte> signature = stackalloc byte[4];
        if (stream.Read(signature) != signature.Length)
        {
            stream.Position = 0;
            return false;
        }

        stream.Position = 0;
        return signature.SequenceEqual("BSJB"u8);
    }

    private static bool IsWindowsPdb(Stream stream)
    {
        Span<byte> signature = stackalloc byte[32];
        var read = stream.Read(signature);
        stream.Position = 0;
        return Encoding.ASCII.GetString(signature[..read]).StartsWith("Microsoft C/C++ MSF 7.00", StringComparison.Ordinal);
    }
}
