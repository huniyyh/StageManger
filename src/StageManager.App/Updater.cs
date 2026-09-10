using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace StageManager.App;

/// <summary>
/// Checks GitHub Releases for a newer version and downloads it in the background. Applying an update restarts
/// the app; the caller is expected to turn Stage Manager off first so the user's windows are put back.
/// Does nothing in builds that were not installed by Velopack (for example when run from bin/ or dotnet run).
/// </summary>
internal sealed class Updater
{
    private readonly UpdateManager? _manager;
    private UpdateInfo? _ready;

    /// <summary>A new version has been downloaded and can be applied; the argument is its version.</summary>
    public event Action<string>? UpdateReady;

    public bool IsInstalled => _manager?.IsInstalled == true;

    public string CurrentVersion
        => _manager?.CurrentVersion?.ToString()
           ?? typeof(Updater).Assembly.GetName().Version?.ToString(3)
           ?? "dev";

    public string? ReadyVersion => _ready?.TargetFullRelease.Version.ToString();

    public Updater()
    {
        // The repository is stamped into the assembly from the project file (AssemblyMetadata UpdateRepository).
        var repository = typeof(Updater).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value;
        if (string.IsNullOrWhiteSpace(repository))
        {
            Log.Write("updater disabled: no UpdateRepository in the assembly metadata");
            return;
        }

        try
        {
            _manager = new UpdateManager(new GithubSource(repository, null, false));
        }
        catch (Exception ex)
        {
            Log.Write("updater unavailable: " + ex.Message);
        }
    }

    /// <summary>Looks for a newer release and downloads it. Returns true when one is ready to apply.</summary>
    public async Task<bool> CheckAsync()
    {
        if (_manager == null) return false;
        if (!_manager.IsInstalled)
        {
            Log.Write("update check skipped: this build was not installed by the installer");
            return false;
        }
        if (_ready != null) return true;

        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info == null)
            {
                Log.Write($"no update available; current version {CurrentVersion}");
                return false;
            }

            Log.Write($"update {info.TargetFullRelease.Version} found; downloading");
            await _manager.DownloadUpdatesAsync(info);
            _ready = info;
            Log.Write($"update {info.TargetFullRelease.Version} downloaded");
            UpdateReady?.Invoke(info.TargetFullRelease.Version.ToString());
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("update check failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>Exits the process, installs the downloaded version and starts it again.</summary>
    public void ApplyAndRestart()
    {
        if (_manager == null || _ready == null) return;
        Log.Write($"applying update {_ready.TargetFullRelease.Version} and restarting");
        _manager.ApplyUpdatesAndRestart(_ready);
    }
}
