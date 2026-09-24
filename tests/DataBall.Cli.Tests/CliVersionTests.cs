// SPDX-License-Identifier: Apache-2.0
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace squalor.DataBall.Cli.Tests;

public class CliVersionTests
{
    [Fact]
    public void Cli_Version_MatchesPackageVersion()
    {
        var sln = FindUp("DataBall.sln");
        var csproj = Path.Combine(sln, "src", "DataBall", "DataBall.csproj");
        var version = XDocument.Load(csproj).Descendants("Version").First().Value;
        Assert.Equal("1.3.0", version);

        var suiteAgents = Path.Combine(Directory.GetParent(sln)!.FullName, "AGENTS.md");
        if (!File.Exists(suiteAgents))
            return;
        var agents = File.ReadAllText(suiteAgents);
        Assert.Matches(new Regex(@"squalor\.DataBall`?\s+\*?\*?1\.3\.0"), agents);
    }

    private static string FindUp(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, name)))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
