using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using SmarterMailMcp.Core.Models;

namespace SmarterMail.Tests;

/// <summary>
/// Core, the shared tool libraries and the MCP host library are loaded into public-facing hosts
/// (the Agent serves strangers), so none of them may be able to start a process. Checked on the compiled IL metadata: any use
/// of <c>System.Diagnostics.Process</c> or <c>ProcessStartInfo</c> (new, static call, field, local,
/// parameter type) needs a TypeReference row, and a <c>Type.GetType("System.Diagnostics.Process…")</c>
/// dodge leaves the name in the user-string heap.
/// </summary>
public sealed class ProcessSpawnGuardTests
{
    private static readonly string[] ForbiddenTypes = ["Process", "ProcessStartInfo"];
    private const string ForbiddenNamespace = "System.Diagnostics";

    public static TheoryData<string> SharedAssemblies => new()
    {
        typeof(UserContext).Assembly.Location,             // src/Core
        ToolLibraryTests.Mailbox.Location,                 // src/Tools.Mailbox
        ToolLibraryTests.DomainAdmin.Location,             // src/Tools.DomainAdmin
        ToolLibraryTests.SysAdmin.Location,                // src/Tools.SysAdmin
        typeof(SmarterMailMcp.Hosting.McpHost).Assembly.Location,   // src/Mcp.Hosting
    };

    [Theory]
    [MemberData(nameof(SharedAssemblies))]
    public void Shared_assembly_does_not_reference_Process(string path)
    {
        var hits = ProcessReferences(path);
        Assert.True(hits.Count == 0,
            $"{Path.GetFileName(path)} references process APIs ({string.Join(", ", hits)}). " +
            "Tools must not spawn processes: keep any such feature out of the shared libraries and hosts.");
    }

    /// <summary>Positive control: proves the detector actually finds a real reference (<see cref="ProcessReferenceFixture"/>).</summary>
    [Fact]
    public void Detector_finds_Process_in_this_test_assembly()
    {
        var hits = ProcessReferences(typeof(ProcessReferenceFixture).Assembly.Location);
        Assert.Contains("System.Diagnostics.Process", hits);
        Assert.Contains("System.Diagnostics.ProcessStartInfo", hits);
    }

    private static List<string> ProcessReferences(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var hits = new List<string>();

        foreach (var handle in md.TypeReferences)
        {
            var type = md.GetTypeReference(handle);
            if (md.StringComparer.Equals(type.Namespace, ForbiddenNamespace) &&
                ForbiddenTypes.Any(name => md.StringComparer.Equals(type.Name, name)))
            {
                hits.Add($"{ForbiddenNamespace}.{md.GetString(type.Name)}");
            }
        }

        // Late-bound lookups by name, e.g. Type.GetType("System.Diagnostics.Process, ...").
        var us = md.GetHeapSize(HeapIndex.UserString) > 1 ? MetadataTokens.UserStringHandle(1) : default;
        while (!us.IsNil)
        {
            var literal = md.GetUserString(us);
            if (literal.Contains("System.Diagnostics.Process", StringComparison.Ordinal))
                hits.Add($"string literal \"{literal}\"");
            us = md.GetNextHandle(us);
        }

        return hits.Distinct().ToList();
    }
}

/// <summary>Deliberately references both forbidden types, so the positive control has something to find.</summary>
internal static class ProcessReferenceFixture
{
    internal static readonly Type Process = typeof(System.Diagnostics.Process);
    internal static readonly Type StartInfo = typeof(System.Diagnostics.ProcessStartInfo);
}
