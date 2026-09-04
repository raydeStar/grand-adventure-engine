using System.Text;
using GAE.Narrator;

namespace GAE.Narrator.Tests;

public class CodexCliLocatorTests
{
    /// <summary>Codex narration contains typographic punctuation, so every redirected stream must stay UTF-8 on Windows.</summary>
    [Fact]
    public void CreateStartInfo_UsesUtf8ForAllRedirectedStreams()
    {
        var startInfo = CodexCliLocator.CreateStartInfo("definitely-not-the-npm-codex-shim");

        Assert.Equal(Encoding.UTF8.WebName, startInfo.StandardInputEncoding!.WebName);
        Assert.Equal(Encoding.UTF8.WebName, startInfo.StandardOutputEncoding!.WebName);
        Assert.Equal(Encoding.UTF8.WebName, startInfo.StandardErrorEncoding!.WebName);
    }
}
