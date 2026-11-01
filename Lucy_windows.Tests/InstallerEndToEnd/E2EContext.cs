using System.Windows;
using Xunit.Abstractions;

namespace Lucy.Tests;

internal sealed class E2EContext
{
    private readonly ITestOutputHelper _out;

    public E2EContext(ITestOutputHelper output, string shots)
    {
        _out = output;
        Shots = shots;
    }

    public string Shots { get; }

    public string ShotPath(string name) => Path.Combine(Shots, name);

    public void Shot(Window win, string name) => RenderTests.Save(win, ShotPath(name));

    public void Note(string line)
    {
        _out.WriteLine(line);
        Console.WriteLine("installer-e2e: " + line);
    }

    public void WaitUntil(Func<bool> done, TimeSpan timeout, string what, Action? onTimeout = null, bool throwOnTimeout = true)
    {
        var deadline = DateTime.Now + timeout;
        while (!done())
        {
            if (DateTime.Now > deadline)
            {
                onTimeout?.Invoke();
                if (throwOnTimeout) throw new TimeoutException($"timed out after {timeout.TotalMinutes:0.#} min waiting for {what}");
                return;
            }
            RenderTests.Pump(TimeSpan.FromMilliseconds(500));
        }
    }
}
