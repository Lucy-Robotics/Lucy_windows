using System.Collections.ObjectModel;
using System.Windows.Input;
using Lucy.Core;
using Lucy.Mvvm;

namespace Lucy.Views;

/// <summary>One editable override file (a tab of the editor).</summary>
public sealed class ConfigDocument : ObservableObject
{
    private readonly ConfigOverride _file;

    public ConfigDocument(ConfigOverride file, string title, string help)
    {
        _file = file;
        Title = title;
        Help = help;
        _text = file.ReadCurrent();
        Validate();

        DefaultCommand = new RelayCommand(() => Text = _file.ReadBase());
        ExampleCommand = new RelayCommand(() => Text = _file.ReadExample(), () => _file.HasExample);
        FormatCommand = new RelayCommand(() => Text = ConfigOverride.Format(Text), () => IsValid);
    }

    public string Title { get; }
    public string Help { get; }
    public string FileName => _file.FileName;
    public string LocalPath => _file.LocalPath;
    public ConfigOverride File => _file;

    public string Origin => _file.HasLocal
        ? $"Editing your existing override ({_file.FileName})."
        : $"Starting from the tracked {Path.GetFileName(_file.BasePath)}. Leave it unchanged to keep the defaults.";

    public ICommand DefaultCommand { get; }
    public ICommand ExampleCommand { get; }
    public ICommand FormatCommand { get; }

    private string _text;
    public string Text
    {
        get => _text;
        set { if (Set(ref _text, value ?? "")) Validate(); }
    }

    private string? _error;
    public string? Error { get => _error; private set => Set(ref _error, value); }

    public bool IsValid => Error == null;

    /// <summary>What saving would do, shown under the editor.</summary>
    public string State => Error != null ? "✕ " + Error
        : ConfigOverride.SameJson(Text, _file.ReadBase())
            ? (_file.HasLocal ? "✓ Same as the defaults: the override will be removed." : "✓ Defaults: no override will be written.")
            : $"✓ Valid. Will be saved as config\\{_file.FileName}.";

    private void Validate()
    {
        Error = _file.Validate(Text);
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(State));
        CommandManager.InvalidateRequerySuggested();
    }
}

/// <summary>
/// Pause between "clone lucy_ws" and "install.py": lets the user write config/repos.json.local
/// (forks, branches, extra repos) and config/launcher_config.json.local before anything is fetched.
/// </summary>
public sealed class ConfigEditorViewModel : ObservableObject
{
    private readonly TaskCompletionSource<bool> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConfigEditorViewModel(Workspace ws)
    {
        Documents.Add(new ConfigDocument(new ConfigOverride(ws, ConfigKind.Repos), "Repositories",
            "Repositories install.py clones into src/: change a branch, point to a fork, add a repo. " +
            "\"url_https\" is used normally, \"url_ssh\" in Developer mode."));
        Documents.Add(new ConfigDocument(new ConfigOverride(ws, ConfigKind.Launcher), "Launcher",
            "Packages shown in the launcher: commands, dependencies, conflicts, readiness checks. " +
            "Windows-only keys \"command_win\" and \"readiness_check_win\" are honoured."));
        _selected = Documents[0];

        SaveCommand = new RelayCommand(Save, () => Documents.All(d => d.IsValid));
        SkipCommand = new RelayCommand(Skip);
    }

    public ObservableCollection<ConfigDocument> Documents { get; } = new();

    private ConfigDocument _selected;
    public ConfigDocument Selected { get => _selected; set => Set(ref _selected, value ?? Documents[0]); }

    public ICommand SaveCommand { get; }
    public ICommand SkipCommand { get; }

    /// <summary>Completes when the user continues. Result: lines describing what was written.</summary>
    public Task<bool> Completion => _done.Task;

    public List<string> Summary { get; } = new();

    private void Save()
    {
        try
        {
            foreach (var d in Documents)
            {
                var r = d.File.Save(d.Text);
                Summary.Add(r switch
                {
                    ConfigOverride.SaveResult.Written => $"wrote config\\{d.FileName}",
                    ConfigOverride.SaveResult.Removed => $"removed config\\{d.FileName} (back to defaults)",
                    _ => $"config\\{d.FileName}: unchanged",
                });
            }
            _done.TrySetResult(true);
        }
        catch (Exception ex)
        {
            Core.Log.Error("saving config overrides failed", ex);
            Dialogs.Error("Configuration", "Could not save the configuration.", ex.Message);
        }
    }

    private void Skip()
    {
        var edited = Documents.Where(d => d.Text != d.File.ReadCurrent()).ToList();
        if (edited.Count > 0 && !Dialogs.Confirm("Configuration",
                "Discard your edits and continue with the current configuration?",
                "Discard & continue", "Keep editing",
                detail: "Edited: " + string.Join(", ", edited.Select(d => d.FileName))))
            return;
        Summary.Add("configuration left as is");
        _done.TrySetResult(false);
    }

    public void Cancel() => _done.TrySetCanceled();
}
