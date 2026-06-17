using System.Text.Json;
using Microsoft.Data.Sqlite;
using VideoAnalysis.Core.Abstractions;
using VideoAnalysis.Core.Enums;
using VideoAnalysis.Core.Models;

namespace VideoAnalysis.Infrastructure.Persistence;

public sealed class SqliteProjectRepository : IProjectRepository
{
    private const int SchemaVersion = 6;
    private const string ProjectDatabaseFileName = "project.db";
    private const string ProjectManifestFileName = "project.json";

    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly string _projectsRootPath;
    private readonly string? _legacyDatabasePath;
    private readonly SemaphoreSlim _initializeLock = new(1, 1);
    private bool _isInitialized;

    public SqliteProjectRepository(string projectsRootPath, string? legacyDatabasePath = null)
    {
        _projectsRootPath = Path.GetFullPath(projectsRootPath);
        _legacyDatabasePath = string.IsNullOrWhiteSpace(legacyDatabasePath)
            ? null
            : Path.GetFullPath(legacyDatabasePath);

        Directory.CreateDirectory(_projectsRootPath);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_isInitialized)
        {
            return;
        }

        await _initializeLock.WaitAsync(cancellationToken);
        try
        {
            if (_isInitialized)
            {
                return;
            }

            Directory.CreateDirectory(_projectsRootPath);
            await MigrateLegacyStorageAsync(cancellationToken);
            _isInitialized = true;
        }
        finally
        {
            _initializeLock.Release();
        }
    }

    public async Task CreateProjectAsync(Project project, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);

        var normalizedProject = project with
        {
            ProjectFolderPath = string.IsNullOrWhiteSpace(project.ProjectFolderPath)
                ? GetDefaultProjectFolderPath(project.Id, project.Name)
                : Path.GetFullPath(project.ProjectFolderPath)
        };

        await using var connection = await OpenProjectConnectionByFolderAsync(normalizedProject.ProjectFolderPath, cancellationToken);
        await UpsertProjectInternalAsync(connection, normalizedProject, cancellationToken);
        await SaveProjectManifestAsync(normalizedProject, cancellationToken);
    }

    public async Task<Project?> GetProjectAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var projectFolderPath = await ResolveProjectFolderPathAsync(projectId, cancellationToken);
        if (projectFolderPath is null)
        {
            return null;
        }

        return await LoadProjectFromDatabaseAsync(projectFolderPath, cancellationToken);
    }

    public async Task<IReadOnlyList<Project>> ListProjectsAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);

        var projects = new List<Project>();
        foreach (var projectFolderPath in EnumerateProjectFolders())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var project = await LoadProjectFromManifestOrDatabaseAsync(projectFolderPath, cancellationToken);
            if (project is not null)
            {
                projects.Add(project);
            }
        }

        return projects
            .OrderByDescending(static project => project.UpdatedAtUtc)
            .ThenBy(static project => project.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task UpsertProjectVideoAsync(ProjectVideo projectVideo, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await ExecuteAsync(projectVideo.ProjectId, cancellationToken, async connection =>
        {
            await UpsertProjectVideoInternalAsync(connection, projectVideo, cancellationToken);
        }, touchProject: true);
    }

    public async Task<ProjectVideo?> GetProjectVideoAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var projectFolderPath = await ResolveProjectFolderPathAsync(projectId, cancellationToken);
        if (projectFolderPath is null)
        {
            return null;
        }

        await using var connection = await OpenProjectConnectionByFolderAsync(projectFolderPath, cancellationToken);
        const string sql = """
                           SELECT id, project_id, title, original_file_name, stored_file_path, imported_at, proxy_file_path
                           FROM ProjectVideo
                           WHERE project_id = $project_id
                           LIMIT 1;
                           """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$project_id", projectId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapProjectVideo(reader) : null;
    }

    public Task UpsertMediaAssetAsync(MediaAsset mediaAsset, CancellationToken cancellationToken)
    {
        var projectVideo = new ProjectVideo(
            mediaAsset.Id,
            mediaAsset.ProjectId,
            Path.GetFileNameWithoutExtension(mediaAsset.FilePath),
            Path.GetFileName(mediaAsset.FilePath),
            mediaAsset.FilePath,
            mediaAsset.ImportedAtUtc);

        return UpsertProjectVideoAsync(projectVideo, cancellationToken);
    }

    public async Task<MediaAsset?> GetMediaAssetAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var projectVideo = await GetProjectVideoAsync(projectId, cancellationToken);
        if (projectVideo is null)
        {
            return null;
        }

        return new MediaAsset(
            projectVideo.Id,
            projectVideo.ProjectId,
            projectVideo.StoredFilePath,
            0,
            0,
            0,
            0,
            projectVideo.ImportedAtUtc);
    }

    public async Task<IReadOnlyList<TagPreset>> GetTagPresetsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        return await QueryAsync(projectId, cancellationToken, connection =>
        {
            const string sql = """
                               SELECT id, project_id, name, color_hex, category, is_system, hotkey, icon_key, show_in_statistics, pre_roll_frames, post_roll_frames
                               FROM TagPreset
                               WHERE project_id = $project_id
                               ORDER BY is_system DESC, name ASC;
                               """;

            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$project_id", projectId.ToString());
            return command;
        }, MapTagPreset);
    }

    public async Task UpsertTagPresetAsync(TagPreset preset, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await ExecuteAsync(preset.ProjectId, cancellationToken, async connection =>
        {
            await UpsertTagPresetInternalAsync(connection, preset, cancellationToken);
        }, touchProject: true);
    }

    public async Task DeleteTagPresetAsync(Guid projectId, Guid tagPresetId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await ExecuteAsync(projectId, cancellationToken, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM TagPreset WHERE project_id = $project_id AND id = $id;";
            command.Parameters.AddWithValue("$project_id", projectId.ToString());
            command.Parameters.AddWithValue("$id", tagPresetId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, touchProject: true);
    }

    public async Task<IReadOnlyList<TagEvent>> GetTagEventsAsync(Guid projectId, TagQuery query, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        return await QueryAsync(projectId, cancellationToken, connection =>
        {
            var sql = """
                      SELECT id, project_id, tag_preset_id, start_frame, end_frame, player, period, notes, created_at, team_side, is_open
                      FROM TagEvent
                      WHERE project_id = $project_id
                      """;

            var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$project_id", projectId.ToString());

            if (query.TagPresetId.HasValue)
            {
                sql += " AND tag_preset_id = $tag_preset_id";
                command.Parameters.AddWithValue("$tag_preset_id", query.TagPresetId.Value.ToString());
            }

            if (!string.IsNullOrWhiteSpace(query.Player))
            {
                sql += " AND lower(player) = lower($player)";
                command.Parameters.AddWithValue("$player", query.Player.Trim());
            }

            if (!string.IsNullOrWhiteSpace(query.Period))
            {
                sql += " AND lower(period) = lower($period)";
                command.Parameters.AddWithValue("$period", query.Period.Trim());
            }

            if (!string.IsNullOrWhiteSpace(query.Text))
            {
                sql += " AND lower(coalesce(notes, '')) LIKE lower($notes)";
                command.Parameters.AddWithValue("$notes", $"%{query.Text.Trim()}%");
            }

            if (query.TeamSide.HasValue)
            {
                sql += " AND team_side = $team_side";
                command.Parameters.AddWithValue("$team_side", (int)query.TeamSide.Value);
            }

            if (query.IsOpen.HasValue)
            {
                sql += " AND is_open = $is_open";
                command.Parameters.AddWithValue("$is_open", query.IsOpen.Value ? 1 : 0);
            }

            sql += " ORDER BY start_frame, end_frame, created_at;";
            command.CommandText = sql;
            return command;
        }, MapTagEvent);
    }

    public async Task UpsertTagEventAsync(TagEvent tagEvent, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await ExecuteAsync(tagEvent.ProjectId, cancellationToken, async connection =>
        {
            await UpsertTagEventInternalAsync(connection, tagEvent, cancellationToken);
        }, touchProject: true);
    }

    public async Task DeleteTagEventAsync(Guid projectId, Guid tagEventId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await ExecuteAsync(projectId, cancellationToken, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM TagEvent WHERE project_id = $project_id AND id = $id;";
            command.Parameters.AddWithValue("$project_id", projectId.ToString());
            command.Parameters.AddWithValue("$id", tagEventId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, touchProject: true);
    }

    public async Task<IReadOnlyList<Playlist>> GetPlaylistsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        return await QueryAsync(projectId, cancellationToken, connection =>
        {
            const string sql = """
                               SELECT id, project_id, name, description, created_at, updated_at
                               FROM Playlist
                               WHERE project_id = $project_id
                               ORDER BY updated_at DESC, created_at DESC;
                               """;

            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$project_id", projectId.ToString());
            return command;
        }, MapPlaylist);
    }

    public async Task<Playlist?> GetPlaylistAsync(Guid projectId, Guid playlistId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        return await QuerySingleAsync(projectId, cancellationToken, connection =>
        {
            const string sql = """
                               SELECT id, project_id, name, description, created_at, updated_at
                               FROM Playlist
                               WHERE project_id = $project_id AND id = $id
                               LIMIT 1;
                               """;

            var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$project_id", projectId.ToString());
            command.Parameters.AddWithValue("$id", playlistId.ToString());
            return command;
        }, MapPlaylist);
    }

    public async Task UpsertPlaylistAsync(Playlist playlist, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await ExecuteAsync(playlist.ProjectId, cancellationToken, async connection =>
        {
            await UpsertPlaylistInternalAsync(connection, playlist, cancellationToken);
        }, touchProject: true);
    }

    public async Task DeletePlaylistAsync(Guid projectId, Guid playlistId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        await ExecuteAsync(projectId, cancellationToken, async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM Playlist WHERE project_id = $project_id AND id = $id;";
            command.Parameters.AddWithValue("$project_id", projectId.ToString());
            command.Parameters.AddWithValue("$id", playlistId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }, touchProject: true);
    }

    public async Task<IReadOnlyList<PlaylistItem>> GetPlaylistItemsAsync(Guid playlistId, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var projectFolderPath = await ResolveProjectFolderPathByPlaylistIdAsync(playlistId, cancellationToken);
        if (projectFolderPath is null)
        {
            return [];
        }

        await using var connection = await OpenProjectConnectionByFolderAsync(projectFolderPath, cancellationToken);
        const string sql = """
                           SELECT id, playlist_id, tag_event_id, tag_preset_id, sort_order, event_start_frame, event_end_frame,
                                  clip_start_frame, clip_end_frame, pre_roll_frames, post_roll_frames, label, player, team_side
                           FROM PlaylistItem
                           WHERE playlist_id = $playlist_id
                           ORDER BY sort_order ASC, clip_start_frame ASC;
                           """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$playlist_id", playlistId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var items = new List<PlaylistItem>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(MapPlaylistItem(reader));
        }

        return items;
    }

    public async Task ReplacePlaylistItemsAsync(Guid playlistId, IReadOnlyList<PlaylistItem> items, CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        var projectFolderPath = await ResolveProjectFolderPathByPlaylistIdAsync(playlistId, cancellationToken);
        if (projectFolderPath is null)
        {
            throw new InvalidOperationException($"Playlist {playlistId} was not found.");
        }

        await using var connection = await OpenProjectConnectionByFolderAsync(projectFolderPath, cancellationToken);
        await ReplacePlaylistItemsInternalAsync(connection, playlistId, items, cancellationToken);

        var projectId = await GetProjectIdByPlaylistIdAsync(connection, playlistId, cancellationToken);
        if (projectId.HasValue)
        {
            await TouchProjectAsync(connection, projectId.Value, cancellationToken);
        }
    }

    public Task<IReadOnlyList<Annotation>> GetAnnotationsAsync(Guid projectId, FrameRange range, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<Annotation>>([]);
    }

    public Task UpsertAnnotationAsync(Annotation annotation, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteAnnotationAsync(Guid projectId, Guid annotationId, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<ClipRecipe>> GetClipRecipesAsync(Guid projectId, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<ClipRecipe>>([]);
    }

    public Task UpsertClipRecipeAsync(ClipRecipe recipe, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<ExportJob>> GetExportJobsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        return Task.FromResult<IReadOnlyList<ExportJob>>([]);
    }

    public Task UpsertExportJobAsync(ExportJob exportJob, CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task ExecuteAsync(Guid projectId, CancellationToken cancellationToken, Func<SqliteConnection, Task> action, bool touchProject)
    {
        var projectFolderPath = await ResolveProjectFolderPathAsync(projectId, cancellationToken)
            ?? throw new InvalidOperationException($"Project {projectId} was not found.");

        await using var connection = await OpenProjectConnectionByFolderAsync(projectFolderPath, cancellationToken);
        await action(connection);
        if (touchProject)
        {
            await TouchProjectAsync(connection, projectId, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<T>> QueryAsync<T>(
        Guid projectId,
        CancellationToken cancellationToken,
        Func<SqliteConnection, SqliteCommand> createCommand,
        Func<SqliteDataReader, T> map)
    {
        var projectFolderPath = await ResolveProjectFolderPathAsync(projectId, cancellationToken);
        if (projectFolderPath is null)
        {
            return [];
        }

        await using var connection = await OpenProjectConnectionByFolderAsync(projectFolderPath, cancellationToken);
        await using var command = createCommand(connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var items = new List<T>();
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(map(reader));
        }

        return items;
    }

    private async Task<T?> QuerySingleAsync<T>(
        Guid projectId,
        CancellationToken cancellationToken,
        Func<SqliteConnection, SqliteCommand> createCommand,
        Func<SqliteDataReader, T> map) where T : class
    {
        var projectFolderPath = await ResolveProjectFolderPathAsync(projectId, cancellationToken);
        if (projectFolderPath is null)
        {
            return null;
        }

        await using var connection = await OpenProjectConnectionByFolderAsync(projectFolderPath, cancellationToken);
        await using var command = createCommand(connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? map(reader) : null;
    }

    private async Task<string?> ResolveProjectFolderPathAsync(Guid projectId, CancellationToken cancellationToken)
    {
        foreach (var projectFolderPath in EnumerateProjectFolders())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Path.GetFileName(projectFolderPath).EndsWith($"-{projectId:N}", StringComparison.OrdinalIgnoreCase))
            {
                return projectFolderPath;
            }

            var manifest = await TryReadProjectManifestAsync(projectFolderPath, cancellationToken);
            if (manifest?.Id == projectId)
            {
                return projectFolderPath;
            }

            var project = await LoadProjectFromDatabaseAsync(projectFolderPath, cancellationToken);
            if (project?.Id == projectId)
            {
                await SaveProjectManifestAsync(project, cancellationToken);
                return projectFolderPath;
            }
        }

        return null;
    }

    private async Task<string?> ResolveProjectFolderPathByPlaylistIdAsync(Guid playlistId, CancellationToken cancellationToken)
    {
        foreach (var projectFolderPath in EnumerateProjectFolders())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(GetProjectDatabasePath(projectFolderPath)))
            {
                continue;
            }

            await using var connection = await OpenProjectConnectionByFolderAsync(projectFolderPath, cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1 FROM Playlist WHERE id = $id LIMIT 1;";
            command.Parameters.AddWithValue("$id", playlistId.ToString());
            var exists = await command.ExecuteScalarAsync(cancellationToken);
            if (exists is not null)
            {
                return projectFolderPath;
            }
        }

        return null;
    }

    private async Task<Project?> LoadProjectFromManifestOrDatabaseAsync(string projectFolderPath, CancellationToken cancellationToken)
    {
        var manifest = await TryReadProjectManifestAsync(projectFolderPath, cancellationToken);
        if (manifest is not null)
        {
            return MapProjectManifest(manifest);
        }

        var project = await LoadProjectFromDatabaseAsync(projectFolderPath, cancellationToken);
        if (project is not null)
        {
            await SaveProjectManifestAsync(project, cancellationToken);
        }

        return project;
    }

    private async Task<Project?> LoadProjectFromDatabaseAsync(string projectFolderPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(GetProjectDatabasePath(projectFolderPath)))
        {
            return null;
        }

        await using var connection = await OpenProjectConnectionByFolderAsync(projectFolderPath, cancellationToken);
        const string sql = """
                           SELECT id, name, description, home_team_name, away_team_name, project_folder_path, created_at, updated_at, is_broadcast_mode
                           FROM Project
                           LIMIT 1;
                           """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapProject(reader) : null;
    }

    private async Task<SqliteConnection> OpenProjectConnectionByFolderAsync(string projectFolderPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(projectFolderPath);

        var connection = new SqliteConnection(CreateConnectionString(GetProjectDatabasePath(projectFolderPath)));
        await connection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        await EnsureCurrentSchemaAsync(connection, cancellationToken);
        return connection;
    }

    private async Task SaveProjectManifestAsync(Project project, CancellationToken cancellationToken)
    {
        var manifest = new ProjectManifest(
            project.Id,
            project.Name,
            project.Description,
            project.HomeTeamName,
            project.AwayTeamName,
            project.ProjectFolderPath,
            project.CreatedAtUtc,
            project.UpdatedAtUtc,
            project.IsBroadcastMode);

        var manifestPath = GetProjectManifestPath(project.ProjectFolderPath);
        Directory.CreateDirectory(project.ProjectFolderPath);
        var json = JsonSerializer.Serialize(manifest, JsonSerializerOptions);
        await File.WriteAllTextAsync(manifestPath, json, cancellationToken);
    }

    private static async Task<ProjectManifest?> TryReadProjectManifestAsync(string projectFolderPath, CancellationToken cancellationToken)
    {
        var manifestPath = GetProjectManifestPath(projectFolderPath);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            return JsonSerializer.Deserialize<ProjectManifest>(json, JsonSerializerOptions);
        }
        catch
        {
            return null;
        }
    }

    private async Task TouchProjectAsync(SqliteConnection connection, Guid projectId, CancellationToken cancellationToken)
    {
        var updatedAt = DateTimeOffset.UtcNow;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE Project SET updated_at = $updated_at WHERE id = $id;";
            command.Parameters.AddWithValue("$updated_at", updatedAt.ToString("O"));
            command.Parameters.AddWithValue("$id", projectId.ToString());
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var project = await GetProjectInternalAsync(connection, projectId, cancellationToken);
        if (project is not null)
        {
            await SaveProjectManifestAsync(project, cancellationToken);
        }
    }

    private static async Task<Project?> GetProjectInternalAsync(SqliteConnection connection, Guid projectId, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT id, name, description, home_team_name, away_team_name, project_folder_path, created_at, updated_at, is_broadcast_mode
                           FROM Project
                           WHERE id = $id
                           LIMIT 1;
                           """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", projectId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? MapProject(reader) : null;
    }

    private static async Task<Guid?> GetProjectIdByPlaylistIdAsync(SqliteConnection connection, Guid playlistId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT project_id FROM Playlist WHERE id = $id LIMIT 1;";
        command.Parameters.AddWithValue("$id", playlistId.ToString());
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text ? Guid.Parse(text) : null;
    }

    private async Task MigrateLegacyStorageAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_legacyDatabasePath) || !File.Exists(_legacyDatabasePath))
        {
            return;
        }

        await using var legacyConnection = new SqliteConnection(CreateConnectionString(_legacyDatabasePath));
        await legacyConnection.OpenAsync(cancellationToken);
        await ExecuteNonQueryAsync(legacyConnection, "PRAGMA foreign_keys = ON;", cancellationToken);

        if (!await TableExistsAsync(legacyConnection, "Project", cancellationToken))
        {
            return;
        }

        var legacyProjects = await QueryLegacyProjectsAsync(legacyConnection, cancellationToken);
        foreach (var project in legacyProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetFolderPath = GetDefaultProjectFolderPath(project.Id, project.Name);
            await MoveLegacyProjectFolderAsync(project.ProjectFolderPath, targetFolderPath, cancellationToken);
            Directory.CreateDirectory(targetFolderPath);

            var migratedProject = project with { ProjectFolderPath = targetFolderPath };
            await using var targetConnection = await OpenProjectConnectionByFolderAsync(targetFolderPath, cancellationToken);
            await UpsertProjectInternalAsync(targetConnection, migratedProject, cancellationToken);

            var legacyProjectVideo = await GetLegacyProjectVideoAsync(legacyConnection, project.Id, cancellationToken);
            if (legacyProjectVideo is not null)
            {
                var migratedVideoPath = RewriteLegacyPath(legacyProjectVideo.StoredFilePath, project.ProjectFolderPath, targetFolderPath);
                var migratedProjectVideo = legacyProjectVideo with { StoredFilePath = migratedVideoPath };
                await UpsertProjectVideoInternalAsync(targetConnection, migratedProjectVideo, cancellationToken);
            }

            foreach (var preset in await GetLegacyTagPresetsAsync(legacyConnection, project.Id, cancellationToken))
            {
                await UpsertTagPresetInternalAsync(targetConnection, preset, cancellationToken);
            }

            foreach (var tagEvent in await GetLegacyTagEventsAsync(legacyConnection, project.Id, cancellationToken))
            {
                await UpsertTagEventInternalAsync(targetConnection, tagEvent, cancellationToken);
            }

            var playlists = await GetLegacyPlaylistsAsync(legacyConnection, project.Id, cancellationToken);
            foreach (var playlist in playlists)
            {
                await UpsertPlaylistInternalAsync(targetConnection, playlist, cancellationToken);
                var items = await GetLegacyPlaylistItemsAsync(legacyConnection, playlist.Id, cancellationToken);
                await ReplacePlaylistItemsInternalAsync(targetConnection, playlist.Id, items, cancellationToken);
            }

            await SaveProjectManifestAsync(migratedProject, cancellationToken);
        }
    }

    private static async Task<IReadOnlyList<Project>> QueryLegacyProjectsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT id, name, description, home_team_name, away_team_name, project_folder_path, created_at, updated_at, 0 AS is_broadcast_mode
                           FROM Project
                           ORDER BY updated_at DESC;
                           """;

        return await QueryLegacyAsync(connection, sql, cancellationToken, _ => { }, MapProject);
    }

    private static async Task<ProjectVideo?> GetLegacyProjectVideoAsync(SqliteConnection connection, Guid projectId, CancellationToken cancellationToken)
    {
        const string sql = """
                           SELECT id, project_id, title, original_file_name, stored_file_path, imported_at, NULL AS proxy_file_path
                           FROM ProjectVideo
                           WHERE project_id = $project_id
                           LIMIT 1;
                           """;

        return await QueryLegacySingleAsync(connection, sql, cancellationToken, command =>
        {
            command.Parameters.AddWithValue("$project_id", projectId.ToString());
        }, MapProjectVideo);
    }

    private static async Task<IReadOnlyList<TagPreset>> GetLegacyTagPresetsAsync(SqliteConnection connection, Guid projectId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "TagPreset", cancellationToken))
        {
            return [];
        }

        const string sql = """
                           SELECT id, project_id, name, color_hex, category, is_system, hotkey, icon_key, show_in_statistics, pre_roll_frames, post_roll_frames
                           FROM TagPreset
                           WHERE project_id = $project_id
                           ORDER BY is_system DESC, name ASC;
                           """;

        return await QueryLegacyAsync(connection, sql, cancellationToken, command =>
        {
            command.Parameters.AddWithValue("$project_id", projectId.ToString());
        }, MapTagPreset);
    }

    private static async Task<IReadOnlyList<TagEvent>> GetLegacyTagEventsAsync(SqliteConnection connection, Guid projectId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "TagEvent", cancellationToken))
        {
            return [];
        }

        const string sql = """
                           SELECT id, project_id, tag_preset_id, start_frame, end_frame, player, period, notes, created_at, team_side, is_open
                           FROM TagEvent
                           WHERE project_id = $project_id
                           ORDER BY start_frame, end_frame, created_at;
                           """;

        return await QueryLegacyAsync(connection, sql, cancellationToken, command =>
        {
            command.Parameters.AddWithValue("$project_id", projectId.ToString());
        }, MapTagEvent);
    }

    private static async Task<IReadOnlyList<Playlist>> GetLegacyPlaylistsAsync(SqliteConnection connection, Guid projectId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "Playlist", cancellationToken))
        {
            return [];
        }

        const string sql = """
                           SELECT id, project_id, name, description, created_at, updated_at
                           FROM Playlist
                           WHERE project_id = $project_id
                           ORDER BY updated_at DESC, created_at DESC;
                           """;

        return await QueryLegacyAsync(connection, sql, cancellationToken, command =>
        {
            command.Parameters.AddWithValue("$project_id", projectId.ToString());
        }, MapPlaylist);
    }

    private static async Task<IReadOnlyList<PlaylistItem>> GetLegacyPlaylistItemsAsync(SqliteConnection connection, Guid playlistId, CancellationToken cancellationToken)
    {
        if (!await TableExistsAsync(connection, "PlaylistItem", cancellationToken))
        {
            return [];
        }

        const string sql = """
                           SELECT id, playlist_id, tag_event_id, tag_preset_id, sort_order, event_start_frame, event_end_frame,
                                  clip_start_frame, clip_end_frame, pre_roll_frames, post_roll_frames, label, player, team_side
                           FROM PlaylistItem
                           WHERE playlist_id = $playlist_id
                           ORDER BY sort_order ASC, clip_start_frame ASC;
                           """;

        return await QueryLegacyAsync(connection, sql, cancellationToken, command =>
        {
            command.Parameters.AddWithValue("$playlist_id", playlistId.ToString());
        }, MapPlaylistItem);
    }

    private static async Task MoveLegacyProjectFolderAsync(string sourceFolderPath, string targetFolderPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceFolderPath))
        {
            return;
        }

        sourceFolderPath = Path.GetFullPath(sourceFolderPath);
        targetFolderPath = Path.GetFullPath(targetFolderPath);

        if (!Directory.Exists(sourceFolderPath) || PathsEqual(sourceFolderPath, targetFolderPath))
        {
            return;
        }

        if (Directory.Exists(targetFolderPath))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetFolderPath) ?? targetFolderPath);

        try
        {
            Directory.Move(sourceFolderPath, targetFolderPath);
        }
        catch
        {
            await CopyDirectoryAsync(sourceFolderPath, targetFolderPath, cancellationToken);
        }
    }

    private static async Task CopyDirectoryAsync(string sourceFolderPath, string targetFolderPath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(targetFolderPath);

        foreach (var directory in Directory.GetDirectories(sourceFolderPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceFolderPath, directory);
            Directory.CreateDirectory(Path.Combine(targetFolderPath, relativePath));
        }

        foreach (var file in Directory.GetFiles(sourceFolderPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceFolderPath, file);
            var targetFilePath = Path.Combine(targetFolderPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFilePath)!);

            await using var sourceStream = File.OpenRead(file);
            await using var targetStream = File.Create(targetFilePath);
            await sourceStream.CopyToAsync(targetStream, cancellationToken);
        }
    }

    private string GetDefaultProjectFolderPath(Guid projectId, string projectName)
    {
        return Path.Combine(_projectsRootPath, $"{SanitizeForPath(projectName)}-{projectId:N}");
    }

    private IEnumerable<string> EnumerateProjectFolders()
    {
        return Directory.Exists(_projectsRootPath)
            ? Directory.EnumerateDirectories(_projectsRootPath)
            : Enumerable.Empty<string>();
    }

    private static string GetProjectDatabasePath(string projectFolderPath) => Path.Combine(projectFolderPath, ProjectDatabaseFileName);

    private static string GetProjectManifestPath(string projectFolderPath) => Path.Combine(projectFolderPath, ProjectManifestFileName);

    private static string CreateConnectionString(string databasePath) => new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Pooling = false
    }.ToString();

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long count && count > 0;
    }

    private static string NormalizeHotkey(string hotkey) => string.IsNullOrWhiteSpace(hotkey) ? string.Empty : hotkey.Trim();

    private static object DbValue(string? value) => value is null ? DBNull.Value : value;

    private static TagPreset MapTagPreset(SqliteDataReader reader)
    {
        return new TagPreset(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5) == 1,
            reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
            reader.IsDBNull(7) ? "event" : reader.GetString(7),
            reader.IsDBNull(8) || reader.GetInt64(8) == 1,
            reader.IsDBNull(9) ? 0 : reader.GetInt32(9),
            reader.IsDBNull(10) ? 0 : reader.GetInt32(10));
    }

    private static TagEvent MapTagEvent(SqliteDataReader reader)
    {
        return new TagEvent(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            DateTimeOffset.Parse(reader.GetString(8)),
            reader.IsDBNull(9) ? TeamSide.Unknown : (TeamSide)reader.GetInt32(9),
            !reader.IsDBNull(10) && reader.GetInt32(10) == 1);
    }

    private static Project MapProject(SqliteDataReader reader)
    {
        return new Project(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            DateTimeOffset.Parse(reader.GetString(6)),
            DateTimeOffset.Parse(reader.GetString(7)),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            !reader.IsDBNull(8) && reader.GetInt64(8) == 1);
    }

    private static Project MapProjectManifest(ProjectManifest manifest)
    {
        return new Project(
            manifest.Id,
            manifest.Name,
            manifest.CreatedAtUtc,
            manifest.UpdatedAtUtc,
            manifest.Description,
            manifest.HomeTeamName,
            manifest.AwayTeamName,
            manifest.ProjectFolderPath,
            manifest.IsBroadcastMode);
    }

    private static ProjectVideo MapProjectVideo(SqliteDataReader reader)
    {
        return new ProjectVideo(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    private static Playlist MapPlaylist(SqliteDataReader reader)
    {
        return new Playlist(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4)),
            DateTimeOffset.Parse(reader.GetString(5)));
    }

    private static PlaylistItem MapPlaylistItem(SqliteDataReader reader)
    {
        return new PlaylistItem(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)),
            Guid.Parse(reader.GetString(3)),
            reader.GetInt32(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? TeamSide.Unknown : (TeamSide)reader.GetInt32(13));
    }

    private static async Task ExecuteNonQueryAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<T>> QueryLegacyAsync<T>(SqliteConnection connection, string sql, CancellationToken cancellationToken, Action<SqliteCommand> bind, Func<SqliteDataReader, T> map)
    {
        var items = new List<T>();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(map(reader));
        }

        return items;
    }

    private static async Task<T?> QueryLegacySingleAsync<T>(SqliteConnection connection, string sql, CancellationToken cancellationToken, Action<SqliteCommand> bind, Func<SqliteDataReader, T> map) where T : class
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? map(reader) : null;
    }

    private static async Task UpsertProjectInternalAsync(SqliteConnection connection, Project project, CancellationToken cancellationToken)
    {
        const string sql = """
                           INSERT INTO Project (
                               id, name, description, home_team_name, away_team_name, project_folder_path, created_at, updated_at, is_broadcast_mode)
                           VALUES (
                               $id, $name, $description, $home_team_name, $away_team_name, $project_folder_path, $created_at, $updated_at, $is_broadcast_mode)
                           ON CONFLICT(id) DO UPDATE SET
                               name = excluded.name,
                               description = excluded.description,
                               home_team_name = excluded.home_team_name,
                               away_team_name = excluded.away_team_name,
                               project_folder_path = excluded.project_folder_path,
                               is_broadcast_mode = excluded.is_broadcast_mode,
                               updated_at = excluded.updated_at;
                           """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", project.Id.ToString());
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$description", DbValue(project.Description));
        command.Parameters.AddWithValue("$home_team_name", DbValue(project.HomeTeamName));
        command.Parameters.AddWithValue("$away_team_name", DbValue(project.AwayTeamName));
        command.Parameters.AddWithValue("$project_folder_path", project.ProjectFolderPath);
        command.Parameters.AddWithValue("$created_at", project.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", project.UpdatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$is_broadcast_mode", project.IsBroadcastMode ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertProjectVideoInternalAsync(SqliteConnection connection, ProjectVideo projectVideo, CancellationToken cancellationToken)
    {
        const string sql = """
                           INSERT INTO ProjectVideo (
                               id, project_id, title, original_file_name, stored_file_path, imported_at, proxy_file_path)
                           VALUES (
                               $id, $project_id, $title, $original_file_name, $stored_file_path, $imported_at, $proxy_file_path)
                           ON CONFLICT(project_id) DO UPDATE SET
                               id = excluded.id,
                               title = excluded.title,
                               original_file_name = excluded.original_file_name,
                               stored_file_path = excluded.stored_file_path,
                               imported_at = excluded.imported_at,
                               proxy_file_path = excluded.proxy_file_path;
                           """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", projectVideo.Id.ToString());
        command.Parameters.AddWithValue("$project_id", projectVideo.ProjectId.ToString());
        command.Parameters.AddWithValue("$title", projectVideo.Title);
        command.Parameters.AddWithValue("$original_file_name", projectVideo.OriginalFileName);
        command.Parameters.AddWithValue("$stored_file_path", projectVideo.StoredFilePath);
        command.Parameters.AddWithValue("$imported_at", projectVideo.ImportedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$proxy_file_path", DbValue(projectVideo.ProxyFilePath));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertTagPresetInternalAsync(SqliteConnection connection, TagPreset preset, CancellationToken cancellationToken)
    {
        const string sql = """
                           INSERT INTO TagPreset (
                               id, project_id, name, color_hex, category, is_system, hotkey, icon_key, show_in_statistics, pre_roll_frames, post_roll_frames, created_at, updated_at)
                           VALUES (
                               $id, $project_id, $name, $color_hex, $category, $is_system, $hotkey, $icon_key, $show_in_statistics, $pre_roll_frames, $post_roll_frames, $created_at, $updated_at)
                           ON CONFLICT(id) DO UPDATE SET
                               name = excluded.name,
                               color_hex = excluded.color_hex,
                               category = excluded.category,
                               is_system = excluded.is_system,
                               hotkey = excluded.hotkey,
                               icon_key = excluded.icon_key,
                               show_in_statistics = excluded.show_in_statistics,
                               pre_roll_frames = excluded.pre_roll_frames,
                               post_roll_frames = excluded.post_roll_frames,
                               updated_at = excluded.updated_at;
                           """;

        var now = DateTimeOffset.UtcNow.ToString("O");
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", preset.Id.ToString());
        command.Parameters.AddWithValue("$project_id", preset.ProjectId.ToString());
        command.Parameters.AddWithValue("$name", preset.Name.Trim());
        command.Parameters.AddWithValue("$color_hex", preset.ColorHex.Trim());
        command.Parameters.AddWithValue("$category", string.IsNullOrWhiteSpace(preset.Category) ? "Custom" : preset.Category.Trim());
        command.Parameters.AddWithValue("$is_system", preset.IsSystem ? 1 : 0);
        command.Parameters.AddWithValue("$hotkey", NormalizeHotkey(preset.Hotkey));
        command.Parameters.AddWithValue("$icon_key", string.IsNullOrWhiteSpace(preset.IconKey) ? "event" : preset.IconKey.Trim());
        command.Parameters.AddWithValue("$show_in_statistics", preset.ShowInStatistics ? 1 : 0);
        command.Parameters.AddWithValue("$pre_roll_frames", Math.Max(0, preset.PreRollFrames));
        command.Parameters.AddWithValue("$post_roll_frames", Math.Max(0, preset.PostRollFrames));
        command.Parameters.AddWithValue("$created_at", now);
        command.Parameters.AddWithValue("$updated_at", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertTagEventInternalAsync(SqliteConnection connection, TagEvent tagEvent, CancellationToken cancellationToken)
    {
        const string sql = """
                           INSERT INTO TagEvent (
                               id, project_id, tag_preset_id, start_frame, end_frame, player, period, notes, created_at, team_side, is_open)
                           VALUES (
                               $id, $project_id, $tag_preset_id, $start_frame, $end_frame, $player, $period, $notes, $created_at, $team_side, $is_open)
                           ON CONFLICT(id) DO UPDATE SET
                               tag_preset_id = excluded.tag_preset_id,
                               start_frame = excluded.start_frame,
                               end_frame = excluded.end_frame,
                               player = excluded.player,
                               period = excluded.period,
                               notes = excluded.notes,
                               team_side = excluded.team_side,
                               is_open = excluded.is_open;
                           """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", tagEvent.Id.ToString());
        command.Parameters.AddWithValue("$project_id", tagEvent.ProjectId.ToString());
        command.Parameters.AddWithValue("$tag_preset_id", tagEvent.TagPresetId.ToString());
        command.Parameters.AddWithValue("$start_frame", tagEvent.StartFrame);
        command.Parameters.AddWithValue("$end_frame", tagEvent.EndFrame);
        command.Parameters.AddWithValue("$player", DbValue(tagEvent.Player));
        command.Parameters.AddWithValue("$period", DbValue(tagEvent.Period));
        command.Parameters.AddWithValue("$notes", DbValue(tagEvent.Notes));
        command.Parameters.AddWithValue("$created_at", tagEvent.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$team_side", (int)tagEvent.TeamSide);
        command.Parameters.AddWithValue("$is_open", tagEvent.IsOpen ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertPlaylistInternalAsync(SqliteConnection connection, Playlist playlist, CancellationToken cancellationToken)
    {
        const string sql = """
                           INSERT INTO Playlist (
                               id, project_id, name, description, created_at, updated_at)
                           VALUES (
                               $id, $project_id, $name, $description, $created_at, $updated_at)
                           ON CONFLICT(id) DO UPDATE SET
                               name = excluded.name,
                               description = excluded.description,
                               updated_at = excluded.updated_at;
                           """;

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", playlist.Id.ToString());
        command.Parameters.AddWithValue("$project_id", playlist.ProjectId.ToString());
        command.Parameters.AddWithValue("$name", playlist.Name.Trim());
        command.Parameters.AddWithValue("$description", DbValue(string.IsNullOrWhiteSpace(playlist.Description) ? null : playlist.Description.Trim()));
        command.Parameters.AddWithValue("$created_at", playlist.CreatedAtUtc.ToString("O"));
        command.Parameters.AddWithValue("$updated_at", playlist.UpdatedAtUtc.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplacePlaylistItemsInternalAsync(SqliteConnection connection, Guid playlistId, IReadOnlyList<PlaylistItem> items, CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var deleteCommand = connection.CreateCommand())
            {
                deleteCommand.Transaction = transaction;
                deleteCommand.CommandText = "DELETE FROM PlaylistItem WHERE playlist_id = $playlist_id;";
                deleteCommand.Parameters.AddWithValue("$playlist_id", playlistId.ToString());
                await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            const string insertSql = """
                                     INSERT INTO PlaylistItem (
                                         id, playlist_id, tag_event_id, tag_preset_id, sort_order, event_start_frame, event_end_frame,
                                         clip_start_frame, clip_end_frame, pre_roll_frames, post_roll_frames, label, player, team_side)
                                     VALUES (
                                         $id, $playlist_id, $tag_event_id, $tag_preset_id, $sort_order, $event_start_frame, $event_end_frame,
                                         $clip_start_frame, $clip_end_frame, $pre_roll_frames, $post_roll_frames, $label, $player, $team_side);
                                     """;

            foreach (var item in items)
            {
                await using var insertCommand = connection.CreateCommand();
                insertCommand.Transaction = transaction;
                insertCommand.CommandText = insertSql;
                insertCommand.Parameters.AddWithValue("$id", item.Id.ToString());
                insertCommand.Parameters.AddWithValue("$playlist_id", item.PlaylistId.ToString());
                insertCommand.Parameters.AddWithValue("$tag_event_id", item.TagEventId.ToString());
                insertCommand.Parameters.AddWithValue("$tag_preset_id", item.TagPresetId.ToString());
                insertCommand.Parameters.AddWithValue("$sort_order", item.SortOrder);
                insertCommand.Parameters.AddWithValue("$event_start_frame", item.EventStartFrame);
                insertCommand.Parameters.AddWithValue("$event_end_frame", item.EventEndFrame);
                insertCommand.Parameters.AddWithValue("$clip_start_frame", item.ClipStartFrame);
                insertCommand.Parameters.AddWithValue("$clip_end_frame", item.ClipEndFrame);
                insertCommand.Parameters.AddWithValue("$pre_roll_frames", item.PreRollFrames);
                insertCommand.Parameters.AddWithValue("$post_roll_frames", item.PostRollFrames);
                insertCommand.Parameters.AddWithValue("$label", item.Label);
                insertCommand.Parameters.AddWithValue("$player", DbValue(item.Player));
                insertCommand.Parameters.AddWithValue("$team_side", (int)item.TeamSide);
                await insertCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var touchCommand = connection.CreateCommand();
            touchCommand.Transaction = transaction;
            touchCommand.CommandText = "UPDATE Playlist SET updated_at = $updated_at WHERE id = $playlist_id;";
            touchCommand.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
            touchCommand.Parameters.AddWithValue("$playlist_id", playlistId.ToString());
            await touchCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static async Task EnsureCurrentSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
                           CREATE TABLE IF NOT EXISTS Project (
                               id TEXT PRIMARY KEY,
                               name TEXT NOT NULL,
                               description TEXT NULL,
                               home_team_name TEXT NULL,
                               away_team_name TEXT NULL,
                               project_folder_path TEXT NOT NULL,
                               created_at TEXT NOT NULL,
                               updated_at TEXT NOT NULL,
                               is_broadcast_mode INTEGER NOT NULL DEFAULT 0
                           );

                           CREATE TABLE IF NOT EXISTS ProjectVideo (
                               id TEXT PRIMARY KEY,
                               project_id TEXT NOT NULL UNIQUE REFERENCES Project(id) ON DELETE CASCADE,
                               title TEXT NOT NULL,
                               original_file_name TEXT NOT NULL,
                               stored_file_path TEXT NOT NULL,
                               proxy_file_path TEXT NULL,
                               imported_at TEXT NOT NULL
                           );

                           CREATE TABLE IF NOT EXISTS TagPreset (
                               id TEXT PRIMARY KEY,
                               project_id TEXT NOT NULL REFERENCES Project(id) ON DELETE CASCADE,
                               name TEXT NOT NULL,
                               color_hex TEXT NOT NULL,
                               category TEXT NOT NULL,
                               is_system INTEGER NOT NULL,
                               hotkey TEXT NOT NULL DEFAULT '',
                               icon_key TEXT NOT NULL DEFAULT 'event',
                               show_in_statistics INTEGER NOT NULL DEFAULT 1,
                               pre_roll_frames INTEGER NOT NULL DEFAULT 0,
                               post_roll_frames INTEGER NOT NULL DEFAULT 0,
                               created_at TEXT NOT NULL,
                               updated_at TEXT NOT NULL
                           );

                           CREATE UNIQUE INDEX IF NOT EXISTS ux_tag_preset_hotkey_per_project
                           ON TagPreset(project_id, lower(hotkey))
                           WHERE length(trim(hotkey)) > 0;

                           CREATE TABLE IF NOT EXISTS TagEvent (
                               id TEXT PRIMARY KEY,
                               project_id TEXT NOT NULL REFERENCES Project(id) ON DELETE CASCADE,
                               tag_preset_id TEXT NOT NULL REFERENCES TagPreset(id) ON DELETE CASCADE,
                               start_frame INTEGER NOT NULL,
                               end_frame INTEGER NOT NULL,
                               player TEXT NULL,
                               period TEXT NULL,
                               notes TEXT NULL,
                               created_at TEXT NOT NULL,
                               team_side INTEGER NOT NULL,
                               is_open INTEGER NOT NULL
                           );

                           CREATE INDEX IF NOT EXISTS ix_tag_event_project_preset_open
                           ON TagEvent(project_id, tag_preset_id, is_open, start_frame);

                           CREATE TABLE IF NOT EXISTS Playlist (
                               id TEXT PRIMARY KEY,
                               project_id TEXT NOT NULL REFERENCES Project(id) ON DELETE CASCADE,
                               name TEXT NOT NULL,
                               description TEXT NULL,
                               created_at TEXT NOT NULL,
                               updated_at TEXT NOT NULL
                           );

                           CREATE INDEX IF NOT EXISTS ix_playlist_project_updated
                           ON Playlist(project_id, updated_at DESC, created_at DESC);

                           CREATE TABLE IF NOT EXISTS PlaylistItem (
                               id TEXT PRIMARY KEY,
                               playlist_id TEXT NOT NULL REFERENCES Playlist(id) ON DELETE CASCADE,
                               tag_event_id TEXT NOT NULL,
                               tag_preset_id TEXT NOT NULL,
                               sort_order INTEGER NOT NULL,
                               event_start_frame INTEGER NOT NULL,
                               event_end_frame INTEGER NOT NULL,
                               clip_start_frame INTEGER NOT NULL,
                               clip_end_frame INTEGER NOT NULL,
                               pre_roll_frames INTEGER NOT NULL,
                               post_roll_frames INTEGER NOT NULL,
                               label TEXT NOT NULL,
                               player TEXT NULL,
                               team_side INTEGER NOT NULL
                           );

                           CREATE INDEX IF NOT EXISTS ix_playlist_item_playlist_order
                           ON PlaylistItem(playlist_id, sort_order, clip_start_frame);
                           """;

        await ExecuteNonQueryAsync(connection, sql, cancellationToken);
        await EnsureProjectColumnsAsync(connection, cancellationToken);
        await EnsureProjectVideoColumnsAsync(connection, cancellationToken);
        await EnsureTagPresetColumnsAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {SchemaVersion};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureProjectColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(Project);";

        var hasIsBroadcastMode = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var columnName = reader.GetString(1);
                if (string.Equals(columnName, "is_broadcast_mode", StringComparison.OrdinalIgnoreCase))
                {
                    hasIsBroadcastMode = true;
                    break;
                }
            }
        }

        if (!hasIsBroadcastMode)
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE Project ADD COLUMN is_broadcast_mode INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }
    }

    private static async Task EnsureProjectVideoColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(ProjectVideo);";

        var hasProxyFilePath = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var columnName = reader.GetString(1);
                if (string.Equals(columnName, "proxy_file_path", StringComparison.OrdinalIgnoreCase))
                {
                    hasProxyFilePath = true;
                    break;
                }
            }
        }

        if (!hasProxyFilePath)
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE ProjectVideo ADD COLUMN proxy_file_path TEXT NULL;", cancellationToken);
        }
    }

    private static async Task EnsureTagPresetColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(TagPreset);";

        var hasShowInStatistics = false;
        var hasPreRollFrames = false;
        var hasPostRollFrames = false;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var columnName = reader.GetString(1);
                if (string.Equals(columnName, "show_in_statistics", StringComparison.OrdinalIgnoreCase))
                {
                    hasShowInStatistics = true;
                }
                else if (string.Equals(columnName, "pre_roll_frames", StringComparison.OrdinalIgnoreCase))
                {
                    hasPreRollFrames = true;
                }
                else if (string.Equals(columnName, "post_roll_frames", StringComparison.OrdinalIgnoreCase))
                {
                    hasPostRollFrames = true;
                }
            }
        }

        if (!hasShowInStatistics)
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE TagPreset ADD COLUMN show_in_statistics INTEGER NOT NULL DEFAULT 1;", cancellationToken);
        }

        if (!hasPreRollFrames)
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE TagPreset ADD COLUMN pre_roll_frames INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }

        if (!hasPostRollFrames)
        {
            await ExecuteNonQueryAsync(connection, "ALTER TABLE TagPreset ADD COLUMN post_roll_frames INTEGER NOT NULL DEFAULT 0;", cancellationToken);
        }
    }

    private static string RewriteLegacyPath(string originalPath, string originalProjectFolderPath, string targetProjectFolderPath)
    {
        if (string.IsNullOrWhiteSpace(originalPath))
        {
            return originalPath;
        }

        var fullOriginalPath = Path.GetFullPath(originalPath);
        var fullOriginalProjectFolderPath = Path.GetFullPath(originalProjectFolderPath);
        var fullTargetProjectFolderPath = Path.GetFullPath(targetProjectFolderPath);

        if (!fullOriginalPath.StartsWith(fullOriginalProjectFolderPath, StringComparison.OrdinalIgnoreCase))
        {
            return fullOriginalPath;
        }

        var relativePath = Path.GetRelativePath(fullOriginalProjectFolderPath, fullOriginalPath);
        return Path.Combine(fullTargetProjectFolderPath, relativePath);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeForPath(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Trim().Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "project" : sanitized;
    }
}
