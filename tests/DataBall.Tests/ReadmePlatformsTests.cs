// SPDX-License-Identifier: Apache-2.0
using System;
using System.IO;
using Xunit;

namespace squalor.DataBall.Tests
{
    public class ReadmePlatformsTests
    {
        [Fact]
        public void Readme_Platforms_NamesCiMatrixAndNotCiRids()
        {
            var readme = File.ReadAllText(Path.Combine(RepoRoot(), "README.md"));
            var platforms = Section(readme, "## Platforms");
            Assert.Contains("windows-latest", platforms, StringComparison.Ordinal);
            Assert.Contains("macos-latest", platforms, StringComparison.Ordinal);
            Assert.Contains("ubuntu-latest", platforms, StringComparison.Ordinal);
            foreach (var rid in new[] { "win-arm64", "linux-arm64" })
            {
                var i = platforms.IndexOf(rid, StringComparison.Ordinal);
                Assert.True(i >= 0, "Platforms must name " + rid);
                var period = platforms.IndexOf('.', i);
                var span = period < 0 ? platforms[i..] : platforms[i..period];
                Assert.Contains("not CI-tested", span, StringComparison.Ordinal);
            }
        }

        private static string RepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DataBall.sln")))
                dir = dir.Parent;
            Assert.NotNull(dir);
            return dir!.FullName;
        }

        private static string Section(string markdown, string heading)
        {
            var start = markdown.IndexOf(heading, StringComparison.Ordinal);
            Assert.True(start >= 0, heading);
            start += heading.Length;
            var end = markdown.IndexOf("\n## ", start, StringComparison.Ordinal);
            return end < 0 ? markdown[start..] : markdown[start..end];
        }
    }
}
