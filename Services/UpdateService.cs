using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using HdrBridge.Models;
using Velopack;
using Velopack.Sources;

namespace HdrBridge.Services;

public sealed class UpdateService : IDisposable {
    private readonly SettingsService _settingsService;
    private readonly NotificationService _notifications;
    private readonly DispatcherTimer _silentCheckTimer;
    private readonly HttpClient _httpClient;
    private readonly UpdateManager _updateManager;

    private bool _isChecking;
    private bool _isDownloading;
    private UpdateInfo? _downloadedUpdate;

    public event EventHandler<UpdateStateChangedEventArgs>? StateChanged;

    public bool IsUpdateReadyToInstall => _downloadedUpdate is not null;

    public UpdateService(SettingsService settingsService, NotificationService notifications) {
        _settingsService = settingsService;
        _notifications = notifications;

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HdrBridge", "1.2.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        _updateManager = new UpdateManager(new GithubSource("https://github.com/GetTheNya/HdrBridge", null, false));

        _silentCheckTimer = new DispatcherTimer {
            Interval = TimeSpan.FromHours(2)
        };
        _silentCheckTimer.Tick += async (_, _) => await CheckForUpdatesAsync(silent: true, downloadIfAvailable: true);

    }

    public void Start() {
        _silentCheckTimer.Start();
    }

    public async Task<UpdateResult> CheckForUpdatesAsync(bool silent, bool downloadIfAvailable) {
        if (_isChecking || _isDownloading) {
            return UpdateResult.NoUpdate("Update check already running.");
        }

        _isChecking = true;
        RaiseState(new UpdateStateChangedEventArgs {
            IsChecking = true,
            StatusText = silent ? "Checking in background..." : "Checking for updates..."
        });

        try {
            var update = await _updateManager.CheckForUpdatesAsync();
            if (update is null) {
                var noUpdateMessage = "You are already on the latest version.";
                RaiseState(new UpdateStateChangedEventArgs {
                    IsChecking = false,
                    StatusText = noUpdateMessage
                });
                return UpdateResult.NoUpdate(noUpdateMessage);
            }

            var releaseTag = "v" + update.TargetFullRelease.Version.ToString();
            var releaseNotes = await FetchReleaseNotesMarkdownAsync(releaseTag);

            RaiseState(new UpdateStateChangedEventArgs {
                IsChecking = false,
                IsUpdateAvailable = true,
                LatestVersion = releaseTag,
                ReleaseNotesMarkdown = releaseNotes,
                StatusText = $"Update available: {releaseTag}"
            });

            if (!downloadIfAvailable) {
                return UpdateResult.Available(update, releaseTag, releaseNotes);
            }

            return await DownloadUpdateAsync(update, silent);
        } catch (Exception ex) {
            var msg = $"Update check failed: {ex.Message}";
            RaiseState(new UpdateStateChangedEventArgs {
                IsChecking = false,
                StatusText = msg
            });
            return UpdateResult.Failed(msg);
        } finally {
            _isChecking = false;
        }
    }

    public async Task<UpdateResult> DownloadUpdateAsync(UpdateInfo update, bool silent) {
        if (_isDownloading) {
            return UpdateResult.NoUpdate("Update download already running.");
        }

        _isDownloading = true;
        RaiseState(new UpdateStateChangedEventArgs {
            IsDownloading = true,
            DownloadProgressPercent = 0,
            StatusText = "Downloading update..."
        });

        try {
            await _updateManager.DownloadUpdatesAsync(update, progress => {
                RaiseState(new UpdateStateChangedEventArgs {
                    IsDownloading = true,
                    DownloadProgressPercent = progress,
                    StatusText = $"Downloading update... {progress}%"
                });
            });

            _downloadedUpdate = update;
            RaiseState(new UpdateStateChangedEventArgs {
                IsDownloading = false,
                IsUpdateReady = true,
                DownloadProgressPercent = 100,
                LatestVersion = "v" + update.TargetFullRelease.Version.ToString(),
                StatusText = "Update ready. Restart to install."
            });

            if (silent) {
                _notifications.Show("HdrBridge update", "A new version is ready. Restart the app to install.");
            }

            return UpdateResult.Ready(update);
        } catch (Exception ex) {
            var msg = $"Update download failed: {ex.Message}";
            RaiseState(new UpdateStateChangedEventArgs {
                IsDownloading = false,
                StatusText = msg
            });
            return UpdateResult.Failed(msg);
        } finally {
            _isDownloading = false;
        }
    }

    public void InstallUpdateAndRestart() {
        if (_downloadedUpdate is null) {
            return;
        }

        _updateManager.ApplyUpdatesAndRestart(_downloadedUpdate);
    }

    public string? GetCurrentVersionTag() {
        var version = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(version)) {
            return null;
        }

        if (version.Contains('+', StringComparison.Ordinal)) {
            version = version.Split('+')[0];
        }

        return version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : $"v{version}";
    }

    public bool ShouldShowWhatsNew(out string currentVersionTag) {
        currentVersionTag = GetCurrentVersionTag() ?? "v0.0.0";
        var lastSeen = _settingsService.CurrentSettings.LastSeenWhatsNewVersion;
        return !string.Equals(lastSeen, currentVersionTag, StringComparison.OrdinalIgnoreCase);
    }

    public async Task<string> GetReleaseNotesForCurrentVersionAsync() {
        var currentTag = GetCurrentVersionTag();
        if (string.IsNullOrWhiteSpace(currentTag)) {
            return "No release notes available for this build.";
        }

        return await FetchReleaseNotesMarkdownAsync(currentTag);
    }

    public void MarkWhatsNewAsSeen(string versionTag) {
        _settingsService.CurrentSettings.LastSeenWhatsNewVersion = versionTag;
        _settingsService.SaveSettings();
    }

    private async Task<string> FetchReleaseNotesMarkdownAsync(string tagName) {
        var url = $"https://api.github.com/repos/GetTheNya/HdrBridge/releases/tags/{tagName}";
        using var response = await _httpClient.GetAsync(url);
        if (!response.IsSuccessStatusCode) {
            return $"Release notes are not available for {tagName} yet.";
        }

        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("body", out var bodyProp)) {
            return bodyProp.GetString() ?? "Release notes are empty.";
        }

        return "Release notes are empty.";
    }

    private void RaiseState(UpdateStateChangedEventArgs args) {
        StateChanged?.Invoke(this, args);
    }

    public void Dispose() {
        _silentCheckTimer.Stop();
        _httpClient.Dispose();
    }
}

public sealed class UpdateStateChangedEventArgs : EventArgs {
    public bool IsChecking { get; init; }
    public bool IsDownloading { get; init; }
    public bool IsUpdateAvailable { get; init; }
    public bool IsUpdateReady { get; init; }
    public int DownloadProgressPercent { get; init; }
    public string? LatestVersion { get; init; }
    public string? ReleaseNotesMarkdown { get; init; }
    public string StatusText { get; init; } = string.Empty;
}

public sealed class UpdateResult {
    public bool HasUpdate { get; private init; }
    public bool IsReadyToInstall { get; private init; }
    public bool Success { get; private init; }
    public string Message { get; private init; } = string.Empty;
    public UpdateInfo? UpdateInfo { get; private init; }
    public string? VersionTag { get; private init; }
    public string? ReleaseNotesMarkdown { get; private init; }

    public static UpdateResult NoUpdate(string message) => new() {
        Success = true,
        HasUpdate = false,
        Message = message
    };

    public static UpdateResult Available(UpdateInfo updateInfo, string versionTag, string releaseNotesMarkdown) => new() {
        Success = true,
        HasUpdate = true,
        IsReadyToInstall = false,
        UpdateInfo = updateInfo,
        VersionTag = versionTag,
        ReleaseNotesMarkdown = releaseNotesMarkdown,
        Message = $"Update {versionTag} is available."
    };

    public static UpdateResult Ready(UpdateInfo updateInfo) => new() {
        Success = true,
        HasUpdate = true,
        IsReadyToInstall = true,
        UpdateInfo = updateInfo,
        Message = "Update is ready to install."
    };

    public static UpdateResult Failed(string message) => new() {
        Success = false,
        HasUpdate = false,
        Message = message
    };
}
