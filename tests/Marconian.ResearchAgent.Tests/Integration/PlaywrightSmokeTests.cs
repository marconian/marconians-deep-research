using System.Threading.Tasks;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace Marconian.ResearchAgent.Tests.Integration;

[Explicit("Requires Playwright browser installation and network access.")]
[Parallelizable(ParallelScope.Self)]
public sealed class PlaywrightSmokeTests : PageTest
{
    [Test]
    public async Task GoogleHomePage_ShouldLoadTitle()
    {
        await Page.GotoAsync("https://www.google.com", new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded,
            Timeout = 10000
        });

        string title = await Page.TitleAsync();
        Assert.That(title, Does.Contain("Google"));
    }
}
