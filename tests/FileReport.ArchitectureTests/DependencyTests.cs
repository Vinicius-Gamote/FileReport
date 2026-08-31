using System.Xml.Linq;

namespace FileReport.ArchitectureTests;

public sealed class DependencyTests
{
    private static readonly IReadOnlyDictionary<string, string[]> AllowedReferences = new Dictionary<string, string[]>
    {
        ["FileReport.Domain"] = [],
        ["FileReport.Contracts"] = [],
        ["FileReport.Application"] = ["FileReport.Domain"],
        ["FileReport.Infrastructure"] = ["FileReport.Application"],
        ["FileReport.Api"] = ["FileReport.Application", "FileReport.Infrastructure", "FileReport.Contracts"],
        ["FileReport.Worker"] = ["FileReport.Application", "FileReport.Infrastructure"]
    };

    [Fact]
    public void ProductionProjectsRespectDependencyDirection()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "FileReport.slnx")))
            root = root.Parent;

        Assert.NotNull(root);
        foreach (var (name, allowed) in AllowedReferences)
        {
            var project = XDocument.Load(Path.Combine(root.FullName, "src", name, name + ".csproj"));
            Assert.Empty(Violations(name, project, allowed));
        }
    }

    [Fact]
    public void DomainCheckRejectsAnInfrastructureDependencyEvenWhenUnused()
    {
        var project = XDocument.Parse("<Project><ItemGroup><PackageReference Include='Microsoft.EntityFrameworkCore'/><ProjectReference Include='../FileReport.Infrastructure/FileReport.Infrastructure.csproj'/></ItemGroup></Project>");
        Assert.Equal(2, Violations("FileReport.Domain", project, []).Count);
    }

    private static List<string> Violations(string name, XDocument project, string[] allowed)
    {
        var violations = new List<string>();
        foreach (var reference in project.Descendants("ProjectReference"))
        {
            var dependency = Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value);
            if (!allowed.Contains(dependency)) violations.Add(dependency);
        }

        if (name is "FileReport.Domain" or "FileReport.Application" or "FileReport.Contracts")
            violations.AddRange(project.Descendants("PackageReference").Select(reference => reference.Attribute("Include")!.Value));

        return violations;
    }
}
