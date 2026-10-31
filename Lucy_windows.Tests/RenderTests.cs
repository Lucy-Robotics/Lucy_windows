using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Lucy.Core;
using Lucy.Launcher;
using Lucy.Views;

namespace Lucy.Tests;

/// <summary>
/// Renders the real windows off-screen to PNG for visual review. Opt-in:
///   set LUCY_RENDER=C:\some\output\dir   then   dotnet test --filter Render
/// Uses a fake workspace with DEV=true, so nothing is started.
/// </summary>
public class RenderTests
{
    [Fact]
    public void Render_Screens()
    {
        var outDir = Environment.GetEnvironmentVariable("LUCY_RENDER");
        if (string.IsNullOrEmpty(outDir)) return;
        Directory.CreateDirectory(outDir);

        var wsDir = Directory.CreateTempSubdirectory("lucyrender").FullName;
        Directory.CreateDirectory(Path.Combine(wsDir, "config"));
        File.WriteAllText(Path.Combine(wsDir, "config", "launcher_config.json"), TestData.LauncherConfig);
        Directory.CreateDirectory(Path.Combine(wsDir, "install", "share", "inmoov_urdf"));
        File.WriteAllText(Path.Combine(wsDir, "install", "setup.bat"), "");
        File.WriteAllText(Path.Combine(wsDir, ".env"), "DEV=true\n");
        const string realCfg = @"C:\Dev\lucy_ws\config";
        foreach (var f in new[] { "repos.json", "repos.json.local.example" })
            if (File.Exists(Path.Combine(realCfg, f))) File.Copy(Path.Combine(realCfg, f), Path.Combine(wsDir, "config", f));

        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                // A plain Application with Lucy's theme: constructing Lucy.App would run its startup.
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                foreach (var dict in new[] { "Theme/Tokens.xaml", "Theme/Controls.xaml" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/Lucy;component/{dict}") });

                var shell = new ShellViewModel();
                var win = new MainWindow(shell) { Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false };

                shell.ShowInstaller(InstallMode.Install, null);
                ((InstallViewModel)shell.Page!).ReviewConfig = true;
                win.Show();
                Save(win, Path.Combine(outDir, "1-installer-setup.png"));

                var inst = (InstallViewModel)shell.Page!;
                inst.Phase = InstallPhase.Running;
                inst.Steps.Add(new InstallStep("a", "Check prerequisites", 1) { State = StepState.Done });
                inst.Steps.Add(new InstallStep("b", "Git", 1) { State = StepState.Skipped, Detail = "2.47.1" });
                inst.Steps.Add(new InstallStep("c", "pixi (package manager)", 1) { State = StepState.Done, Detail = "0.80.0" });
                inst.Steps.Add(new InstallStep("d", "Create the pixi environment (ROS 2 Jazzy)", 1) { State = StepState.Running });
                inst.Steps.Add(new InstallStep("e", "Build the ROS workspace (colcon)", 1));
                inst.Progress = 46;
                inst.Status = "Creating the pixi environment — this downloads several GB...";
                for (var i = 0; i < 30; i++) inst.Log.Append($"Pixi install (RoboStack Jazzy) ... package {i} downloaded");
                inst.Log.Flush();
                Save(win, Path.Combine(outDir, "2-installer-progress.png"));

                var editor = new ConfigEditorViewModel(new Workspace(wsDir));
                editor.Documents[0].Text = editor.Documents[0].Text.Replace("\"branch\": \"master\"", "\"branch\": \"dev\"");
                editor.Documents[1].Text = editor.Documents[1].Text.Replace("\"type\": \"tool\"", "\"type\": \"tool\",,");
                inst.ConfigEditor = editor;
                Save(win, Path.Combine(outDir, "6-config-editor.png"));
                editor.Selected = editor.Documents[1];
                Save(win, Path.Combine(outDir, "7-config-editor-error.png"));
                inst.ConfigEditor = null;

                shell.ShowLauncher(new Workspace(wsDir));
                Pump(TimeSpan.FromSeconds(3)); // InitializeAsync: guard + Load
                var vm = (LauncherViewModel)shell.Page!;
                var st = vm.Engine.State;
                st.Get("core")!.Selected = true;
                st.Get("core")!.Status = PackageStatus.Loading;
                st.Get("core")!.StageText = "6/8 Building robot model...";
                st.Get("rviz")!.Selected = true;
                st.Get("control_panel")!.Selected = true;
                st.Get("control_panel")!.Status = PackageStatus.Running;
                st.Get("lucy_cli")!.Status = PackageStatus.Crashed;
                st.RefreshCanEnable();
                var session = new Session("control_panel", "Control Panel", ConsoleMode.Captured);
                session.Log.Append("Pixi task (panel-dev): yarn dev");
                session.Log.Append("  VITE v5.4.14  ready in 1447 ms");
                session.Log.Append("  ➜  Local:   http://localhost:4004/");
                session.Log.Flush();
                session.IsAlive = true;
                vm.Engine.Sessions.Add(session);
                vm.SelectedSession = session;
                Save(win, Path.Combine(outDir, "3-launcher.png"));

                shell.Overlay = "Stopping all processes...";
                Save(win, Path.Combine(outDir, "4-overlay.png"));
                shell.Overlay = null;

                var dlg = DialogWindow.Create("Uninstall Lucy", "Remove Lucy from this computer?",
                    "This removes the app, its Start Menu shortcut and settings.", false, 1, new[] { "Remove app only", "Remove all", "Cancel" });
                dlg.Owner = null;
                dlg.WindowStartupLocation = WindowStartupLocation.Manual;
                dlg.Left = -20000; dlg.Top = -20000; dlg.ShowActivated = false;
                dlg.Show();
                Save(dlg, Path.Combine(outDir, "5-dialog.png"));
                dlg.Close();

                vm.Engine.FinishTeardown();
                win.ForceClose();
                Pump(TimeSpan.FromMilliseconds(300));
                app.Shutdown();
            }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static void Pump(TimeSpan duration)
    {
        var end = DateTime.Now + duration;
        while (DateTime.Now < end)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(30);
        }
    }

    private static void Save(Window win, string path)
    {
        Pump(TimeSpan.FromMilliseconds(700));
        var root = (FrameworkElement)win.Content;
        var w = (int)Math.Ceiling(root.ActualWidth);
        var h = (int)Math.Ceiling(root.ActualHeight);
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
