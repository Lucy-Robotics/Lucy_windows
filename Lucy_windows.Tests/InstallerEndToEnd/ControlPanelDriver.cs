using Microsoft.Playwright;

namespace Lucy.Tests;

internal static class ControlPanelDriver
{
    private const double MovedRad = 0.05;

    public static async Task MoveTheRobotAsync(E2EContext ctx, string url)
    {
        Assert.False(string.IsNullOrEmpty(url), "the launcher reports no control panel URL");
        using var joints = await JointStates.ConnectAsync(new Uri("ws://127.0.0.1:9090"));
        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1600, Height = 1000 } });
        page.Console += (_, m) => { if (m.Type == "error") ctx.Note("browser: " + m.Text); };
        await page.AddInitScriptAsync("localStorage.setItem('gettingStartedCompleted', 'true')");
        try
        {
            await page.GotoAsync(url);
            await page.Locator(".ant-slider").First.WaitForAsync(new() { Timeout = 180_000 });
            ctx.Note("control panel shows the joint sliders");

            await page.GetByRole(AriaRole.Button, new() { Name = "Control Robot: ON", Exact = true }).First.ClickAsync();
            var sliders = page.Locator(".ant-slider:not(.ant-slider-disabled)");
            await sliders.First.WaitForAsync(new() { Timeout = 30_000 });
            ctx.Note("Control Robot is ON");

            var start = await joints.WaitAsync("/joint_states", s => s.Count > 0, TimeSpan.FromSeconds(30));
            var rail = sliders.First;
            await rail.ScrollIntoViewIfNeededAsync();
            var box = (await rail.BoundingBoxAsync())!;
            var moved = false;
            foreach (var fraction in new[] { 0.8f, 0.2f })
            {
                await page.Mouse.ClickAsync(box.X + box.Width * fraction, box.Y + box.Height / 2);
                try
                {
                    await joints.WaitAsync("the slider's move", s => Diff(start, s) > MovedRad, TimeSpan.FromSeconds(20));
                    moved = true;
                    break;
                }
                catch (TimeoutException) { ctx.Note($"no move after clicking the slider at {fraction:P0}"); }
            }
            Assert.True(moved, "dragging a slider with Control Robot ON never moved /joint_states");
            ctx.Note("a slider moved the robot");
        }
        finally
        {
            await page.Mouse.MoveAsync(0, 0);
            await page.ScreenshotAsync(new() { Path = ctx.ShotPath("6-control-panel.png"), FullPage = true });
        }
    }

    private static double Diff(IReadOnlyDictionary<string, double> a, IReadOnlyDictionary<string, double> b) =>
        a.Keys.Intersect(b.Keys).Select(k => Math.Abs(a[k] - b[k])).DefaultIfEmpty(0).Max();
}
