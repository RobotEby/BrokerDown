using System.Xml.Linq;
using Shouldly;
using Xunit;

namespace Chaos.UnitTests;

[Trait("Category", "Unit")]
[Trait("Category", "Architecture")]
public sealed class ArchitectureTests
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ChaosLab.NET.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Solution root not found");
    }

    [Fact]
    public void DomainAndGatewayContractsDoNotReferenceTransportOrPersistence()
    {
        var source = Path.Combine(Root(), "src");
        var domain = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(p => p.Contains(Path.DirectorySeparatorChar + "Domain" + Path.DirectorySeparatorChar))
            .Append(Path.Combine(source, "Payments.Api", "Application", "IPaymentGateway.cs")).ToArray();
        domain.ShouldNotBeEmpty();
        foreach (var file in domain)
            foreach (var forbidden in new[] { "Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "MassTransit", "Shared.Infrastructure", "DbContext", "SqlClient" })
                File.ReadAllText(file).Contains(forbidden, StringComparison.Ordinal).ShouldBeFalse(file);
    }

    [Fact]
    public void SharedContractsAreIndependent_AndProjectReferencesHaveNoCycles()
    {
        var root = Root();
        var contracts = XDocument.Load(Path.Combine(root, "src", "Shared.Contracts", "Shared.Contracts.csproj"));
        contracts.Descendants("ProjectReference").ShouldBeEmpty();
        contracts.Descendants("PackageReference").ShouldBeEmpty();
        var visiting = new HashSet<string>();
        var visited = new HashSet<string>();
        void Visit(string project)
        {
            if (visited.Contains(project)) return;
            visiting.Add(project).ShouldBeTrue("Cyclic project reference: " + project);
            foreach (var reference in XDocument.Load(project).Descendants("ProjectReference"))
                Visit(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(project)!, reference.Attribute("Include")!.Value)));
            visiting.Remove(project);
            visited.Add(project);
        }
        foreach (var project in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)) Visit(project);
    }
}
