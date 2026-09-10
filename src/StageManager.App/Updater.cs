using System.IO;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace StageManager.App;

/// <summary>Outcome of an update check, so the UI can tell "you are up to date" from "the check did not work".</summary>
internal enum UpdateCheckResult
{
    NotInstalled,
    UpToDate,
    UpdateReady,
    Failed,
}

/// <summary>
/// Checks GitHub Releases for a newer version and downloads it in the background. Applying an update restarts
/// the app; the caller is expected to turn Stage Manager off first so the user's windows are put back.
/// Does nothing in builds that were not installed by Velopack (for example when run from bin/ or dotnet run).
/// </summary>
internal sealed class Updater
{
    /// <summary>
    /// A private repository cannot be read anonymously. A token in this environment variable, or in
    /// %LocalAppData%\StageManager\github-token.txt, is sent with the release requests.
    /// </summary>
    private const string TokenEnvironmentVariable = "STAGEMANAGER_GITHUB_TOKEN";
    private const string TokenFileName = "github-token.txt";

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

    /// <summary>Why the last check failed, for the UI.</summary>
    public string? LastError { get; private set; }

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
            var token = ReadToken();
            Log.Write($"updater: {repository}{(token != null ? " (with token)" : "")}");
            _manager = new UpdateManager(new GithubSource(repository, token, false));
        }
        catch (Exception ex)
        {
            Log.Write("updater unavailable: " + ex.Message);
        }
    }

    private static string? ReadToken()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment)) return fromEnvironment.Trim();
        try
        {
            var file = Path.Combine(Log.Directory, TokenFileName);
            if (File.Exists(file))
            {
                var token = File.ReadAllText(file).Trim();
                if (token.Length > 0) return token;
            }
        }
        catch (Exception ex)
        {
            Log.Write("token file unreadable: " + ex.Message);
        }
        return null;
    }

    /// <summary>Looks for a newer release and downloads it.</summary>
    public async Task<UpdateCheckResult> CheckAsync()
    {
        LastError = null;
        if (_manager == null)
        {
            LastError = "업데이트 저장소가 설정되지 않았습니다.";
            return UpdateCheckResult.Failed;
        }
        if (!_manager.IsInstalled)
        {
            Log.Write("update check skipped: this build was not installed by the installer");
            return UpdateCheckResult.NotInstalled;
        }
        if (_ready != null) return UpdateCheckResult.UpdateReady;

        try
        {
            var info = await _manager.CheckForUpdatesAsync();
            if (info == null)
            {
                Log.Write($"no update available; current version {CurrentVersion}");
                return UpdateCheckResult.UpToDate;
            }

            Log.Write($"update {info.TargetFullRelease.Version} found; downloading");
            await _manager.DownloadUpdatesAsync(info);
            _ready = info;
            Log.Write($"update {info.TargetFullRelease.Version} downloaded");
            UpdateReady?.Invoke(info.TargetFullRelease.Version.ToString());
            return UpdateCheckResult.UpdateReady;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Write("update check failed: " + ex);
            return UpdateCheckResult.Failed;
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
